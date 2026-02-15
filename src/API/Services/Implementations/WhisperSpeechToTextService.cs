using API.Services.Interfaces;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace API.Services.Implementations;

/// <summary>
/// Speech-to-text service using Whisper.net with NAudio for Windows audio capture
/// </summary>
public class WhisperSpeechToTextService : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WhisperSpeechToTextService> _logger;
    private readonly IAudioDeviceService _audioDeviceService;
    private readonly IConfiguration _configuration;
    
    private WaveInEvent? _waveIn;
    private WhisperProcessor? _processor;
    private CancellationTokenSource? _cts;
    private bool _isPaused;
    private readonly List<float> _audioBuffer = new();
    private readonly object _bufferLock = new();
    private Task? _transcriptionTask;

    public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

    public WhisperSpeechToTextService(
        ILogger<WhisperSpeechToTextService> logger,
        IAudioDeviceService audioDeviceService,
        IConfiguration configuration)
    {
        _logger = logger;
        _audioDeviceService = audioDeviceService;
        _configuration = configuration;
    }

    public async Task StartTranscriptionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Whisper transcription");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Download or locate Whisper model
            var modelPath = await EnsureWhisperModelAsync();

            // Initialize Whisper processor
            var factory = WhisperFactory.FromPath(modelPath);
            _processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt("This is a technology panel discussion.")
                .Build();

            // Start audio capture
            StartAudioCapture();

            // Start transcription loop
            _transcriptionTask = Task.Run(() => TranscriptionLoopAsync(_cts.Token), _cts.Token);
            
            _logger.LogInformation("Whisper transcription started successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Whisper transcription");
            throw;
        }
    }

    public async Task StopTranscriptionAsync()
    {
        _logger.LogInformation("Stopping Whisper transcription");
        
        _cts?.Cancel();
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
        
        if (_transcriptionTask != null)
        {
            try
            {
                await _transcriptionTask;
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }
        
        _logger.LogInformation("Whisper transcription stopped");
    }

    public Task PauseTranscriptionAsync()
    {
        _logger.LogInformation("Pausing Whisper transcription");
        _isPaused = true;
        return Task.CompletedTask;
    }

    public Task ResumeTranscriptionAsync()
    {
        _logger.LogInformation("Resuming Whisper transcription");
        _isPaused = false;
        return Task.CompletedTask;
    }

    private void StartAudioCapture()
    {
        var selectedDevice = _audioDeviceService.GetSelectedInputDevice();
        var deviceNumber = selectedDevice != null ? int.Parse(selectedDevice.Id) : 0;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = deviceNumber,
            WaveFormat = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit, mono (Whisper standard)
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.RecordingStopped += (s, e) =>
        {
            if (e.Exception != null)
            {
                _logger.LogError(e.Exception, "Audio recording stopped with error");
            }
            else
            {
                _logger.LogInformation("Audio recording stopped normally");
            }
        };
        
        _waveIn.StartRecording();
        _logger.LogInformation("Audio capture started on device {DeviceNumber}", deviceNumber);
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (_isPaused) return;

        // Convert byte array to float array (16-bit PCM to float)
        var floatBuffer = new float[e.BytesRecorded / 2];
        for (int i = 0; i < floatBuffer.Length; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            floatBuffer[i] = sample / 32768f; // Normalize to [-1, 1]
        }

        lock (_bufferLock)
        {
            _audioBuffer.AddRange(floatBuffer);
        }
    }

    private async Task TranscriptionLoopAsync(CancellationToken cancellationToken)
    {
        const int samplesPerSegment = 16000 * 10; // 10 seconds of audio at 16kHz

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, cancellationToken); // Process every 5 seconds

                if (_isPaused) continue;

                float[] audioSegment;
                lock (_bufferLock)
                {
                    if (_audioBuffer.Count < samplesPerSegment / 2) continue; // Wait for more data

                    audioSegment = _audioBuffer.Take(samplesPerSegment).ToArray();
                    _audioBuffer.RemoveRange(0, Math.Min(samplesPerSegment, _audioBuffer.Count));
                }

                // Process with Whisper
                await foreach (var segment in _processor!.ProcessAsync(audioSegment, cancellationToken))
                {
                    var text = segment.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogDebug("Whisper transcription: {Text}", text);
                        
                        TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                        {
                            Text = text,
                            Timestamp = DateTime.UtcNow,
                            IsFinal = true
                        });
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Whisper transcription");
                // Continue processing despite errors
            }
        }
    }

    private async Task<string> EnsureWhisperModelAsync()
    {
        // Check configuration for model path
        var configuredPath = _configuration["Whisper:ModelPath"];
        if (!string.IsNullOrEmpty(configuredPath) && File.Exists(configuredPath))
        {
            _logger.LogInformation("Using Whisper model from configuration: {Path}", configuredPath);
            return configuredPath;
        }

        // Default location in application directory
        var modelDir = Path.Combine(AppContext.BaseDirectory, "Models");
        Directory.CreateDirectory(modelDir);

        var modelPath = Path.Combine(modelDir, "ggml-base.en.bin");
        
        if (!File.Exists(modelPath))
        {
            _logger.LogInformation("Downloading Whisper base.en model to {Path}...", modelPath);
            _logger.LogInformation("This may take a few minutes on first run...");
            
            try
            {
                using var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(GgmlType.BaseEn);
                using var fileStream = File.Create(modelPath);
                await modelStream.CopyToAsync(fileStream);
                
                _logger.LogInformation("Whisper model downloaded successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download Whisper model");
                throw;
            }
        }
        else
        {
            _logger.LogInformation("Using existing Whisper model at {Path}", modelPath);
        }

        return modelPath;
    }

    public void Dispose()
    {
        _cts?.Dispose();
        _waveIn?.Dispose();
        _processor?.Dispose();
    }
}

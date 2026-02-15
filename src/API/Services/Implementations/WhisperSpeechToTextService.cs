using API.Services.Interfaces;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace API.Services.Implementations;

/// <summary>
/// Speech-to-text service using Whisper.net with NAudio for Windows audio capture.
/// Supports capturing from multiple audio devices simultaneously.
/// </summary>
public class WhisperSpeechToTextService(
    ILogger<WhisperSpeechToTextService> logger,
    IAudioDeviceService audioDeviceService,
    IConfiguration configuration) : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WhisperSpeechToTextService> _logger = logger;
    private readonly List<DeviceCapture> _deviceCaptures = [];
    private WhisperFactory? _whisperFactory;
    private CancellationTokenSource? _cts;
    private bool _isPaused;

    public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

    public async Task StartTranscriptionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting Whisper transcription");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            // Download or locate Whisper model
            var modelPath = await EnsureWhisperModelAsync();

            // Initialize Whisper factory (shared across all device processors)
            _whisperFactory = WhisperFactory.FromPath(modelPath);

            // Get selected devices
            var selectedDevices = audioDeviceService.GetSelectedInputDevices();
            
            if (selectedDevices.Count == 0)
            {
                _logger.LogWarning("No audio devices selected, using default device");
                var defaultDevice = audioDeviceService.GetSelectedInputDevice();
                if (defaultDevice != null)
                {
                    selectedDevices = [defaultDevice];
                }
            }

            _logger.LogInformation("Starting audio capture from {Count} device(s)", selectedDevices.Count);

            // Start capture and transcription for each device
            foreach (var device in selectedDevices)
            {
                var capture = new DeviceCapture(device, _whisperFactory, _logger);
                capture.TranscriptionReceived += OnDeviceTranscriptionReceived;
                _deviceCaptures.Add(capture);
                
                capture.Start(() => _isPaused, _cts.Token);
            }
            
            _logger.LogInformation("Whisper transcription started successfully on {Count} device(s)", 
                _deviceCaptures.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Whisper transcription");
            throw;
        }
    }

    private void OnDeviceTranscriptionReceived(object? sender, TranscriptionReceivedEventArgs e)
    {
        // Forward transcription events from individual devices
        TranscriptionReceived?.Invoke(this, e);
    }

    public async Task StopTranscriptionAsync()
    {
        _logger.LogInformation("Stopping Whisper transcription");
        
        _cts?.Cancel();

        // Stop all device captures
        foreach (var capture in _deviceCaptures)
        {
            await capture.StopAsync();
            capture.TranscriptionReceived -= OnDeviceTranscriptionReceived;
            capture.Dispose();
        }
        _deviceCaptures.Clear();
        
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

    private async Task<string> EnsureWhisperModelAsync()
    {
        // Check configuration for model path
        var configuredPath = configuration["Whisper:ModelPath"];
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
        foreach (var capture in _deviceCaptures)
        {
            capture.Dispose();
        }
        _deviceCaptures.Clear();
        
        _cts?.Dispose();
        _whisperFactory?.Dispose();
    }

    /// <summary>
    /// Encapsulates audio capture and transcription for a single device
    /// </summary>
    private class DeviceCapture(AudioDeviceInfo device, WhisperFactory whisperFactory, ILogger logger) : IDisposable
    {
        private readonly ILogger _logger = logger;
        
        private WaveInEvent? _waveIn;
        private WhisperProcessor? _processor;
        private Task? _transcriptionTask;
        private readonly List<float> _audioBuffer = [];
        private readonly object _bufferLock = new();
        private Func<bool>? _isPausedCheck;

        public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

        public void Start(Func<bool> isPausedCheck, CancellationToken cancellationToken)
        {
            _isPausedCheck = isPausedCheck;
            
            // Create a dedicated processor for this device
            _processor = whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt("This is a technology panel discussion.")
                .Build();

            var deviceNumber = int.Parse(device.Id);

            _waveIn = new WaveInEvent
            {
                DeviceNumber        = deviceNumber,
                WaveFormat          = new WaveFormat(16000, 16, 1), // 16kHz, 16-bit, mono (Whisper standard)
                BufferMilliseconds  = 100
            };

            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.RecordingStopped += (s, e) =>
            {
                if (e.Exception != null)
                {
                    _logger.LogError(e.Exception, "Audio recording stopped with error on device {DeviceName}", 
                        device.EffectiveName);
                }
                else
                {
                    _logger.LogInformation("Audio recording stopped normally on device {DeviceName}", 
                        device.EffectiveName);
                }
            };
            
            _waveIn.StartRecording();
            _logger.LogInformation("Audio capture started on device {DeviceName} (ID: {DeviceId})", 
                device.EffectiveName, device.Id);

            // Start transcription loop for this device
            _transcriptionTask = Task.Run(() => TranscriptionLoopAsync(cancellationToken), cancellationToken);
        }

        public async Task StopAsync()
        {
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
        }

        private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_isPausedCheck?.Invoke() == true) return;

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

                    if (_isPausedCheck?.Invoke() == true) continue;

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
                            _logger.LogDebug("Whisper transcription from {DeviceName}: {Text}", 
                                device.EffectiveName, text);
                            
                            TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                            {
                                Text = text,
                                Timestamp = DateTime.UtcNow,
                                IsFinal = true,
                                SpeakerName = device.EffectiveName
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
                    _logger.LogError(ex, "Error during Whisper transcription on device {DeviceName}", 
                        device.EffectiveName);
                    // Continue processing despite errors
                }
            }
        }

        public void Dispose()
        {
            _waveIn?.Dispose();
            _processor?.Dispose();
        }
    }
}

using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;
using Shared;
using Whisper.net;
using Whisper.net.Ggml;

namespace API.Services.Implementations;

/// <summary>
/// Speech-to-text service using Whisper.net. Audio capture is delegated to an
/// <see cref="IAudioCaptureFactory"/>, so this class is platform-agnostic — PipeWire on
/// Linux, NAudio on Windows. Supports capturing from multiple nodes/devices simultaneously.
///
/// Capture runs continuously and is never stopped mid-event. When Bubbles is speaking,
/// the affected audio is dropped on its way to Whisper rather than at the capture node —
/// see <see cref="SelfSuppressionGate"/>.
/// </summary>
public class WhisperSpeechToTextService(
    ILogger<WhisperSpeechToTextService> logger,
    IAudioDeviceService audioDeviceService,
    IAudioCaptureFactory captureFactory,
    SelfSuppressionGate suppressionGate,
    IOptions<SelfSuppressionOptions> suppressionOptions,
    IOptions<TranscriptionOptions> transcriptionOptions,
    IConfiguration configuration) : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WhisperSpeechToTextService> _logger = logger;
    private readonly List<DeviceCapture> _deviceCaptures = [];
    private WhisperFactory? _whisperFactory;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Held while transcription is "paused" via the ISpeechToTextService API. Pausing is
    /// the same mechanism as TTS self-suppression, just without an automatic end.
    /// </summary>
    private IDisposable? _manualSuppression;

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

            if (selectedDevices.Count == 0)
            {
                _logger.LogError("No audio capture devices available - transcription will not run");
                return;
            }

            _logger.LogInformation("Starting audio capture from {Count} device(s)", selectedDevices.Count);

            // Start capture and transcription for each device
            foreach (var device in selectedDevices)
            {
                var capture = new DeviceCapture(
                    device, captureFactory, _whisperFactory, suppressionGate,
                    suppressionOptions.Value, transcriptionOptions.Value, _logger);
                capture.TranscriptionReceived += OnDeviceTranscriptionReceived;
                _deviceCaptures.Add(capture);

                await capture.StartAsync(_cts.Token);
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

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

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
        _logger.LogInformation("Pausing Whisper transcription (capture keeps running)");
        _manualSuppression ??= suppressionGate.Suppress("manual pause");
        return Task.CompletedTask;
    }

    public Task ResumeTranscriptionAsync()
    {
        _logger.LogInformation("Resuming Whisper transcription");
        _manualSuppression?.Dispose();
        _manualSuppression = null;
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
        _manualSuppression?.Dispose();

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
    private class DeviceCapture(
        AudioDeviceInfo device,
        IAudioCaptureFactory captureFactory,
        WhisperFactory whisperFactory,
        SelfSuppressionGate suppressionGate,
        SelfSuppressionOptions suppressionOptions,
        TranscriptionOptions transcriptionOptions,
        ILogger logger) : IDisposable
    {
        private const int SegmentSamples = IAudioCaptureFactory.SampleRate * 10; // 10 seconds

        private readonly ILogger _logger = logger;

        private IAudioCaptureSource? _capture;
        private WhisperProcessor? _processor;
        private Task? _transcriptionTask;
        private readonly Queue<AudioBlock> _blocks = new();
        private readonly Lock _bufferLock = new();
        private int _bufferedSamples;

        public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Create a dedicated processor for this device
            _processor = whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt("This is a technology panel discussion.")
                .Build();

            _capture = captureFactory.Create(device);
            _capture.SamplesAvailable += OnSamplesAvailable;
            await _capture.StartAsync(cancellationToken);

            // Start transcription loop for this device
            _transcriptionTask = Task.Run(() => TranscriptionLoopAsync(cancellationToken), cancellationToken);
        }

        public async Task StopAsync()
        {
            if (_capture is not null)
            {
                _capture.SamplesAvailable -= OnSamplesAvailable;
                await _capture.StopAsync();
                _capture.Dispose();
                _capture = null;
            }

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

        private void OnSamplesAvailable(object? sender, AudioSamplesEventArgs e)
        {
            // Never dropped here, whatever the panelist is doing. Gating happens on the way
            // into Whisper so the capture node is never interrupted.
            var endUtc = DateTime.UtcNow;
            var duration = TimeSpan.FromSeconds(e.Samples.Length / (double)IAudioCaptureFactory.SampleRate);

            lock (_bufferLock)
            {
                _blocks.Enqueue(new AudioBlock(endUtc - duration, endUtc, e.Samples));
                _bufferedSamples += e.Samples.Length;
            }
        }

        private async Task TranscriptionLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(5000, cancellationToken); // Process every 5 seconds

                    var drained = DrainSegment();
                    if (drained.Count == 0) continue;

                    var runs = AudioSegmentation.SplitOnSuppression(
                        drained, suppressionGate.IsSuppressed, out var suppressedSamples);

                    if (suppressedSamples > 0)
                    {
                        _logger.LogInformation(
                            "Dropped {Seconds:F1}s of self-audio from {DeviceName} before transcription",
                            suppressedSamples / (double)IAudioCaptureFactory.SampleRate, device.EffectiveName);
                    }

                    foreach (var run in runs)
                    {
                        await TranscribeAsync(run, cancellationToken);
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

        /// <summary>
        /// Take up to one segment's worth of buffered blocks. Blocks are taken whole —
        /// they are only 100ms each, which is far finer than the suppression tail.
        /// </summary>
        private List<AudioBlock> DrainSegment()
        {
            var drained = new List<AudioBlock>();

            lock (_bufferLock)
            {
                if (_bufferedSamples < SegmentSamples / 2) return drained; // Wait for more data

                var taken = 0;
                while (_blocks.Count > 0 && taken < SegmentSamples)
                {
                    var block = _blocks.Dequeue();
                    _bufferedSamples -= block.Samples.Length;
                    taken += block.Samples.Length;
                    drained.Add(block);
                }
            }

            return drained;
        }

        private async Task TranscribeAsync(List<AudioBlock> run, CancellationToken cancellationToken)
        {
            var seconds = AudioSegmentation.DurationSeconds(run);

            if (seconds < suppressionOptions.MinimumSegmentSeconds)
            {
                _logger.LogDebug("Skipping {Seconds:F2}s fragment from {DeviceName} - too short to transcribe",
                    seconds, device.EffectiveName);
                return;
            }

            // Whisper answers confidently even when handed silence, so don't ask.
            var rms = AudioSegmentation.Rms(run);
            if (rms < transcriptionOptions.SilenceRmsThreshold)
            {
                _logger.LogDebug("Skipping {Seconds:F1}s of silence from {DeviceName} (RMS {Rms:F5})",
                    seconds, device.EffectiveName, rms);
                return;
            }

            var audio = AudioSegmentation.Concatenate(run);
            var runStart = run[0].StartUtc;

            await foreach (var segment in _processor!.ProcessAsync(audio, cancellationToken))
            {
                var text = segment.Text.Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;

                if (transcriptionOptions.DropNonSpeechArtefacts && TranscriptFilter.IsNonSpeech(text))
                {
                    _logger.LogDebug("Dropping non-speech artefact from {DeviceName}: {Text}",
                        device.EffectiveName, text);
                    continue;
                }

                _logger.LogDebug("Whisper transcription from {DeviceName}: {Text}",
                    device.EffectiveName, text);

                TranscriptionReceived?.Invoke(this, new TranscriptionReceivedEventArgs
                {
                    // The time the audio happened, not the time Whisper finished with it -
                    // otherwise everything looks several seconds newer than it is and the
                    // "recent transcript" window is skewed.
                    Text        = text,
                    Timestamp   = runStart + segment.Start,
                    IsFinal     = true,
                    SpeakerName = device.EffectiveName
                });
            }
        }

        public void Dispose()
        {
            _capture?.Dispose();
            _processor?.Dispose();
        }
    }
}

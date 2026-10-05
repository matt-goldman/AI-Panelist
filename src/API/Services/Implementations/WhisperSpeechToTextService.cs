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
    IOptions<TriggerPhraseOptions> triggerOptions,
    TriggerPhraseMatcher triggerMatcher,
    CaptureLevelMonitor levelMonitor,
    IConfiguration configuration) : ISpeechToTextService, IDisposable
{
    private readonly ILogger<WhisperSpeechToTextService> _logger = logger;
    private readonly List<DeviceCapture> _deviceCaptures = [];
    private WhisperFactory? _whisperFactory;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Serialises starting, stopping and re-selecting, so a device change part way through
    /// startup can't leave two sets of captures running on the same nodes.
    /// </summary>
    private readonly SemaphoreSlim _captureLock = new(1, 1);

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

        // Picking a microphone on the setup page has to take effect there and then.
        audioDeviceService.SelectionChanged -= OnSelectionChanged;
        audioDeviceService.SelectionChanged += OnSelectionChanged;

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

            await StartCapturesAsync(selectedDevices, _cts.Token);

            _logger.LogInformation("Whisper transcription started successfully on {Count} device(s)",
                _deviceCaptures.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Whisper transcription");
            throw;
        }
    }

    /// <summary>
    /// Start one capture per selected device. The caller holds <see cref="_captureLock"/>
    /// or is still in startup.
    /// </summary>
    private async Task StartCapturesAsync(List<AudioDeviceInfo> devices, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting audio capture from {Count} device(s): {Devices}",
            devices.Count, string.Join(", ", devices.Select(d => d.EffectiveName)));

        foreach (var device in devices)
        {
            var capture = new DeviceCapture(
                device, captureFactory, _whisperFactory!, suppressionGate,
                suppressionOptions.Value, transcriptionOptions.Value, _logger,
                CreateTriggerListener(device), levelMonitor);
            capture.TranscriptionReceived += OnDeviceTranscriptionReceived;
            _deviceCaptures.Add(capture);

            try
            {
                await capture.StartAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                // One bad node must not cost us the others. Losing the moderator's
                // microphone because a monitor source vanished is not a trade worth making.
                _logger.LogError(ex, "Couldn't start capture on {DeviceName}; continuing without it",
                    device.EffectiveName);
            }
        }
    }

    /// <summary>
    /// Tear down every running capture.
    /// </summary>
    private async Task StopCapturesAsync()
    {
        foreach (var capture in _deviceCaptures)
        {
            await capture.StopAsync();
            capture.TranscriptionReceived -= OnDeviceTranscriptionReceived;
            capture.Dispose();
        }

        _deviceCaptures.Clear();

        // A meter still showing the last reading from a device no longer being captured is
        // worse than an empty one.
        levelMonitor.Clear();
    }

    private async void OnSelectionChanged(object? sender, EventArgs e)
    {
        try
        {
            await RestartCapturesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't apply the new device selection");
        }
    }

    /// <summary>
    /// Swap to the currently selected devices without interrupting transcription as a
    /// whole. Capture is cheap to restart; what it must never do is silently keep reading
    /// the old nodes.
    /// </summary>
    private async Task RestartCapturesAsync()
    {
        if (_whisperFactory is null || _cts is null) return;

        await _captureLock.WaitAsync();
        try
        {
            if (_whisperFactory is null || _cts is null || _cts.IsCancellationRequested) return;

            var devices = audioDeviceService.GetSelectedInputDevices();
            var before = _deviceCaptures.Select(c => c.DeviceId).ToList();
            var after = devices.Select(d => d.Id).ToList();

            if (before.Count == after.Count && before.All(after.Contains))
            {
                _logger.LogDebug("Device selection changed but the capture set is the same; leaving capture alone");
                return;
            }

            _logger.LogInformation("Device selection changed: capturing {After} instead of {Before}",
                after.Count == 0 ? "nothing" : string.Join(", ", after),
                before.Count == 0 ? "nothing" : string.Join(", ", before));

            await StopCapturesAsync();
            await StartCapturesAsync(devices, _cts.Token);
        }
        finally
        {
            _captureLock.Release();
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

        audioDeviceService.SelectionChanged -= OnSelectionChanged;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
        }

        await _captureLock.WaitAsync();
        try
        {
            await StopCapturesAsync();
        }
        finally
        {
            _captureLock.Release();
        }

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

    /// <summary>
    /// A trigger phrase listener for this device, if spoken triggers are on and the
    /// device is one we listen to.
    /// </summary>
    private TriggerPhraseListener? CreateTriggerListener(AudioDeviceInfo device)
    {
        var options = triggerOptions.Value;
        if (!options.Enabled) return null;

        if (options.Devices.Count > 0
            && !options.Devices.Any(d => string.Equals(d, device.EffectiveName, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(d, device.Name, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(d, device.Id, StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation("Not listening for trigger phrases on {DeviceName} - not in TriggerPhrases:Devices",
                device.EffectiveName);
            return null;
        }

        return new TriggerPhraseListener(
            device, _whisperFactory!, suppressionGate, triggerMatcher,
            options, transcriptionOptions.Value, _logger);
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
        audioDeviceService.SelectionChanged -= OnSelectionChanged;
        _manualSuppression?.Dispose();

        foreach (var capture in _deviceCaptures)
        {
            capture.Dispose();
        }
        _deviceCaptures.Clear();

        _cts?.Dispose();
        _captureLock.Dispose();
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
        ILogger logger,
        TriggerPhraseListener? triggerListener,
        CaptureLevelMonitor levelMonitor) : IDisposable
    {
        private const int SegmentSamples = IAudioCaptureFactory.SampleRate * 10; // 10 seconds

        private readonly ILogger _logger = logger;

        private IAudioCaptureSource? _capture;
        private WhisperProcessor? _processor;
        private Task? _transcriptionTask;

        /// <summary>
        /// This capture's own cancellation, linked to the service's. Without it, stopping
        /// one device would wait on a loop that only ends when the whole service does — so
        /// swapping devices hung instead of swapping.
        /// </summary>
        private CancellationTokenSource? _cts;
        private readonly Queue<AudioBlock> _blocks = new();
        private readonly Lock _bufferLock = new();
        private int _bufferedSamples;

        public event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;

        /// <summary>Which node this capture is reading, for comparing selections.</summary>
        public string DeviceId => device.Id;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            // Create a dedicated processor for this device
            _processor = whisperFactory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt("This is a technology panel discussion.")
                .Build();

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;

            _capture = captureFactory.Create(device);
            _capture.SamplesAvailable += OnSamplesAvailable;
            await _capture.StartAsync(token);

            triggerListener?.Start(token);

            // Start transcription loop for this device
            _transcriptionTask = Task.Run(() => TranscriptionLoopAsync(token), token);
        }

        public async Task StopAsync()
        {
            // Ends this device's loops without waiting for the whole service to shut down.
            if (_cts is not null)
            {
                await _cts.CancelAsync();
            }

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

                _transcriptionTask = null;
            }

            if (triggerListener is not null)
            {
                await triggerListener.StopAsync();
            }
        }

        private void OnSamplesAvailable(object? sender, AudioSamplesEventArgs e)
        {
            // Never dropped here, whatever the panelist is doing. Gating happens on the way
            // into Whisper so the capture node is never interrupted.
            var endUtc = DateTime.UtcNow;
            var duration = TimeSpan.FromSeconds(e.Samples.Length / (double)IAudioCaptureFactory.SampleRate);

            var block = new AudioBlock(endUtc - duration, endUtc, e.Samples);

            // Every block, so "is this node carrying anything?" has a live answer rather
            // than one that only updates when a transcript segment is drained.
            levelMonitor.Report(device, e.Samples);

            lock (_bufferLock)
            {
                _blocks.Enqueue(block);
                _bufferedSamples += e.Samples.Length;
            }

            triggerListener?.Add(block);
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
        /// Take up to one segment's worth of buffered blocks, cutting at a pause rather
        /// than at a fixed boundary.
        ///
        /// A hard cut lands mid-word, and Whisper given a fragment that starts mid-word
        /// does not report a fragment — it invents a plausible beginning. "I just want to
        /// know if you're happy to be on this panel" came back as "I hope you found this
        /// panel", which then went to the model as though it were what was said. Cutting
        /// where the speaker was briefly quiet costs nothing and removes the whole class
        /// of error.
        /// </summary>
        private List<AudioBlock> DrainSegment()
        {
            var drained = new List<AudioBlock>();

            lock (_bufferLock)
            {
                if (_bufferedSamples < SegmentSamples / 2) return drained; // Wait for more data

                var candidates = new List<AudioBlock>();
                var taken = 0;

                foreach (var block in _blocks)
                {
                    if (taken >= SegmentSamples) break;
                    candidates.Add(block);
                    taken += block.Samples.Length;
                }

                var count = BlocksEndingAtAPause(candidates);

                for (var i = 0; i < count; i++)
                {
                    var block = _blocks.Dequeue();
                    _bufferedSamples -= block.Samples.Length;
                    drained.Add(block);
                }
            }

            return drained;
        }

        /// <summary>
        /// How many of <paramref name="candidates"/> to take so the cut lands in a quiet
        /// block. Falls back to all of them when the speaker never pauses, because a
        /// late transcript is worse than an imperfect one.
        /// </summary>
        private int BlocksEndingAtAPause(List<AudioBlock> candidates)
        {
            if (candidates.Count == 0) return 0;

            // Never give back so much that we keep re-examining the same audio: at least
            // this much of the segment is always consumed.
            var minimumSamples = (int)(IAudioCaptureFactory.SampleRate * suppressionOptions.MinimumSegmentSeconds);
            var floor = 0;
            var running = 0;

            for (var i = 0; i < candidates.Count; i++)
            {
                running += candidates[i].Samples.Length;
                if (running >= minimumSamples) { floor = i + 1; break; }
            }

            if (floor == 0) return candidates.Count;

            for (var i = candidates.Count - 1; i >= floor; i--)
            {
                if (IsQuiet(candidates[i])) return i + 1;
            }

            // Continuous speech right across the window: cut anyway.
            return candidates.Count;
        }

        private bool IsQuiet(AudioBlock block)
        {
            double sumOfSquares = 0;
            foreach (var sample in block.Samples) sumOfSquares += sample * (double)sample;

            var rms = Math.Sqrt(sumOfSquares / Math.Max(1, block.Samples.Length));
            return rms < transcriptionOptions.SilenceRmsThreshold;
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
            _cts?.Dispose();
            _capture?.Dispose();
            _processor?.Dispose();
            triggerListener?.Dispose();
        }
    }
}

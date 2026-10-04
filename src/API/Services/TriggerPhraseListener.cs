using API.Configuration;
using API.Services.Interfaces;
using Shared;
using Whisper.net;

namespace API.Services;

/// <summary>
/// The low-latency half of a device's speech recognition: transcribes the last few
/// seconds of audio every hop and hands the text to the <see cref="TriggerPhraseMatcher"/>.
/// Its output never reaches the transcript.
///
/// It reads the same gated audio as the transcript — suppression windows are applied
/// before Whisper sees anything — so Bubbles saying "over to you, Bubbles" in an answer
/// can't trigger itself.
///
/// Once a phrase matches, the audio up to the end of that window is marked consumed, so
/// the same utterance can't fire again as the window slides past it.
/// </summary>
public sealed class TriggerPhraseListener(
    AudioDeviceInfo device,
    WhisperFactory whisperFactory,
    SelfSuppressionGate suppressionGate,
    TriggerPhraseMatcher matcher,
    TriggerPhraseOptions options,
    TranscriptionOptions transcriptionOptions,
    ILogger logger) : IDisposable
{
    private readonly int _windowSamples = (int)(IAudioCaptureFactory.SampleRate * options.WindowMs / 1000.0);
    private readonly Queue<AudioBlock> _blocks = new();
    private readonly Lock _bufferLock = new();
    private int _bufferedSamples;

    private WhisperProcessor? _processor;
    private Task? _loopTask;
    private DateTime _consumedUntilUtc = DateTime.MinValue;
    private DateTime _lastProcessedEndUtc = DateTime.MinValue;
    private bool _warnedSlow;

    public void Start(CancellationToken cancellationToken)
    {
        // Its own processor: a WhisperProcessor isn't safe to share with the transcript loop.
        //
        // Tuned for a short window, not accuracy over long audio. Whisper's encoder pads
        // everything to 30 seconds, so by default a 4 second window costs as much as a 30
        // second one (~1s on CPU). Shrinking the audio context to just cover the window
        // (one unit per 20ms, plus headroom) brings that to ~200ms. Temperature fallback is
        // off because it re-decodes mostly-silent windows several times over, which is
        // where multi-second passes came from in testing.
        var audioContext = Math.Min(1500, Math.Max(384, options.WindowMs / 20 + 128));
        _processor = whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .WithPrompt("This is a technology panel discussion.")
            .WithAudioContextSize(audioContext)
            .WithTemperatureInc(0)
            .WithNoContext()
            .WithSingleSegment()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 8))
            .Build();

        _loopTask = Task.Run(() => ListenLoopAsync(cancellationToken), cancellationToken);
        logger.LogInformation("Listening for trigger phrases on {DeviceName} ({Window}ms window, {Hop}ms hop)",
            device.EffectiveName, options.WindowMs, options.HopMs);
    }

    /// <summary>
    /// Feed a captured block. Called on the capture thread, so it only queues.
    /// </summary>
    public void Add(AudioBlock block)
    {
        lock (_bufferLock)
        {
            _blocks.Enqueue(block);
            _bufferedSamples += block.Samples.Length;

            while (_bufferedSamples > _windowSamples && _blocks.Count > 1)
            {
                _bufferedSamples -= _blocks.Dequeue().Samples.Length;
            }
        }
    }

    public async Task StopAsync()
    {
        if (_loopTask is null) return;

        try
        {
            await _loopTask;
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.HopMs));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await ListenOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Trigger phrase detection failed on {DeviceName}", device.EffectiveName);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping
        }
    }

    private async Task ListenOnceAsync(CancellationToken cancellationToken)
    {
        List<AudioBlock> window;
        lock (_bufferLock)
        {
            window = _blocks.Where(b => b.StartUtc >= _consumedUntilUtc).ToList();
        }

        if (window.Count == 0) return;

        // The most recent stretch of audio Bubbles wasn't talking over. A trigger said just
        // now is at the tail, so that's the only run worth the Whisper time.
        var runs = AudioSegmentation.SplitOnSuppression(window, suppressionGate.IsSuppressed, out _);
        if (runs.Count == 0) return;

        var run = runs[^1];
        var runEndUtc = run[^1].EndUtc;

        // Nothing new since last pass (e.g. we're mid-answer and everything since is
        // suppressed): the result would be identical.
        if (runEndUtc <= _lastProcessedEndUtc) return;

        if (AudioSegmentation.DurationSeconds(run) < options.MinimumSeconds) return;
        if (AudioSegmentation.Rms(run) < transcriptionOptions.SilenceRmsThreshold) return;

        _lastProcessedEndUtc = runEndUtc;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var parts = new List<string>();
        await foreach (var segment in _processor!.ProcessAsync(AudioSegmentation.Concatenate(run), cancellationToken))
        {
            var part = segment.Text.Trim();
            if (part.Length > 0) parts.Add(part);
        }
        stopwatch.Stop();

        // If one pass takes longer than the hop, triggers are arriving later than the
        // config suggests. Say so once rather than every second.
        if (!_warnedSlow && stopwatch.ElapsedMilliseconds > options.HopMs)
        {
            _warnedSlow = true;
            logger.LogWarning(
                "Trigger phrase transcription on {DeviceName} took {Ms}ms, longer than the {Hop}ms hop - trigger latency will be higher than expected",
                device.EffectiveName, stopwatch.ElapsedMilliseconds, options.HopMs);
        }

        var text = string.Join(' ', parts);
        if (text.Length == 0) return;

        if (matcher.TryMatch(text, device.EffectiveName))
        {
            _consumedUntilUtc = runEndUtc;
        }
        else if (TriggerPhraseMatcher.Normalise(text).Contains("bubble"))
        {
            // The near misses are what decide whether fuzzy matching is ever worth adding.
            logger.LogDebug("Heard Bubbles' name on {DeviceName} but no trigger phrase ({Ms}ms): {Text}",
                device.EffectiveName, stopwatch.ElapsedMilliseconds, text);
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
    }
}

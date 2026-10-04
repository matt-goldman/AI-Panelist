using System.Collections.Concurrent;
using API.Configuration;
using API.Services.Implementations.PipeWire;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Says something when a response has produced nothing to say.
///
/// Prefers audio that doesn't depend on anything still working: recorded WAVs from
/// wwwroot/audio/fallback-phrases if there are any, otherwise the configured lines,
/// synthesised once at startup and kept in memory. Only if neither exists does it ask the
/// TTS on the spot — which may well be the thing that just failed.
/// </summary>
public sealed class FallbackSpeechService(
    ILogger<FallbackSpeechService> logger,
    ITextToSpeechService tts,
    IAudioPlaybackService playback,
    IOptions<SilenceGuardOptions> options,
    IWebHostEnvironment environment)
{
    private readonly SilenceGuardOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, byte[]> _synthesised = new();
    private readonly Random _random = new();
    private int _lastIndex = -1;

    private IReadOnlyList<string> Lines =>
        _options.Lines.Count > 0 ? _options.Lines : SilenceGuardOptions.DefaultLines;

    private string[] RecordedFiles
    {
        get
        {
            var directory = Path.Combine(
                environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"),
                "audio", "fallback-phrases");

            return Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.wav").OrderBy(f => f).ToArray()
                : [];
        }
    }

    /// <summary>
    /// Synthesise and cache the fallback lines, so they're ready before they're needed.
    /// Failures are logged and retried on demand; nothing here may stop startup.
    /// </summary>
    public async Task WarmAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled) return;

        var recorded = RecordedFiles;
        if (recorded.Length > 0)
        {
            logger.LogInformation("Fallback lines: {Count} recorded WAV(s)", recorded.Length);
            return;
        }

        foreach (var line in Lines)
        {
            await TrySynthesiseAsync(line, cancellationToken);
        }

        logger.LogInformation("Fallback lines: {Ready} of {Count} synthesised and cached", _synthesised.Count, Lines.Count);
    }

    /// <summary>
    /// Speak a fallback line. Returns false if there was nothing that could be played.
    /// </summary>
    /// <param name="onFirstAudio">Invoked just before the line is heard, as for a normal response.</param>
    public async Task<bool> SpeakAsync(Func<Task>? onFirstAudio, CancellationToken cancellationToken)
    {
        var (description, audio) = await PickAsync(cancellationToken);
        if (audio is null)
        {
            logger.LogError("No fallback line available to speak - Bubbles is staying silent");
            return false;
        }

        logger.LogWarning("Speaking fallback line: {Line}", description);

        var wav = WavAudio.Parse(audio);
        await using var stream = await playback.OpenStreamAsync(wav.Format, cancellationToken);

        if (onFirstAudio is not null)
        {
            await onFirstAudio();
        }

        await stream.WriteAsync(WavAudio.ToPcm16(wav), cancellationToken);
        await stream.CompleteAsync(cancellationToken);
        return true;
    }

    private async Task<(string Description, byte[]? Audio)> PickAsync(CancellationToken cancellationToken)
    {
        var recorded = RecordedFiles;
        if (recorded.Length > 0)
        {
            var file = recorded[NextIndex(recorded.Length)];
            return (Path.GetFileName(file), await File.ReadAllBytesAsync(file, cancellationToken));
        }

        var lines = Lines;
        var start = NextIndex(lines.Count);

        // Prefer whichever cached line comes next, so a TTS outage doesn't matter.
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[(start + i) % lines.Count];
            if (_synthesised.TryGetValue(line, out var cached)) return (line, cached);
        }

        return (lines[start], await TrySynthesiseAsync(lines[start], cancellationToken));
    }

    /// <summary>
    /// A random index that isn't the last one used, so the same quip doesn't land twice running.
    /// </summary>
    private int NextIndex(int count)
    {
        if (count <= 1) return 0;

        int index;
        do index = _random.Next(count);
        while (index == _lastIndex);

        _lastIndex = index;
        return index;
    }

    private async Task<byte[]?> TrySynthesiseAsync(string line, CancellationToken cancellationToken)
    {
        if (_synthesised.TryGetValue(line, out var cached)) return cached;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.SynthesisTimeoutSeconds));

            var audio = await tts.SynthesizeAsync(line, timeout.Token);
            _synthesised[line] = audio;
            return audio;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Couldn't synthesise fallback line \"{Line}\": {Error}", line, ex.Message);
            return null;
        }
    }
}

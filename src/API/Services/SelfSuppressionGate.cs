using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// Tracks the windows of wall-clock time during which Bubbles was speaking, so transcripts
/// covering that audio can be discarded.
///
/// The AI's TTS goes into StreamYard as a guest and comes back to us inside the captured
/// tab mix. Without this, Bubbles transcribes itself, feeds its own words back into the
/// context, and can self-trigger.
///
/// Two design points that matter:
///
/// 1. This gates the <em>transcript</em>, not the microphone. Audio capture runs
///    continuously — stopping and restarting a PipeWire capture node mid-event causes
///    buffer glitches, and we would lose whatever a host said while the node was down.
///
/// 2. Suppression is evaluated against the window of time the <em>audio</em> covers, not
///    the moment the transcript appears. Whisper hands us text several seconds after the
///    audio happened, so "am I speaking right now?" is the wrong question to ask.
/// </summary>
public sealed class SelfSuppressionGate(
    ILogger<SelfSuppressionGate> logger,
    IOptions<SelfSuppressionOptions> options)
{
    private readonly SelfSuppressionOptions _options = options.Value;
    private readonly List<Window> _closedWindows = [];
    private readonly Lock _sync = new();

    private DateTime? _openSince;
    private int _openCount;

    /// <summary>
    /// Whether Bubbles is speaking at this instant. Only for state reporting — never use
    /// this to decide whether to drop a transcript.
    /// </summary>
    public bool IsSpeaking
    {
        get
        {
            lock (_sync)
            {
                return _openCount > 0;
            }
        }
    }

    /// <summary>
    /// Open a suppression window. Dispose the returned scope when playback stops; the
    /// window then stays open for a further <see cref="SelfSuppressionOptions.TailMs"/>.
    /// Nested/overlapping scopes are reference counted, so a streamed response made of
    /// many chunks produces one continuous window.
    /// </summary>
    public IDisposable Suppress(string reason)
    {
        lock (_sync)
        {
            if (_openCount++ == 0)
            {
                _openSince = DateTime.UtcNow;
                logger.LogDebug("Self-suppression opened ({Reason})", reason);
            }
        }

        return new Scope(this, reason);
    }

    /// <summary>
    /// Whether a stretch of captured audio overlaps any window in which Bubbles was
    /// speaking, and so must not be transcribed.
    /// </summary>
    public bool IsSuppressed(DateTime windowStartUtc, DateTime windowEndUtc)
    {
        if (!_options.Enabled) return false;

        lock (_sync)
        {
            // Currently speaking: everything from the moment we started is contaminated.
            // No upper bound, because we do not yet know when it ends.
            if (_openCount > 0 && _openSince is { } openSince && windowEndUtc >= openSince)
            {
                return true;
            }

            foreach (var window in _closedWindows)
            {
                if (windowStartUtc < window.EndUtc && windowEndUtc > window.StartUtc)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void Close(string reason)
    {
        lock (_sync)
        {
            if (_openCount == 0) return;
            if (--_openCount > 0) return;

            if (_openSince is not { } openSince) return;

            var endUtc = DateTime.UtcNow.AddMilliseconds(_options.TailMs);
            _closedWindows.Add(new Window(openSince, endUtc));
            _openSince = null;

            logger.LogInformation(
                "Self-suppression closed ({Reason}): {Duration:F1}s of audio suppressed, plus a {Tail}ms tail",
                reason, (endUtc - openSince).TotalSeconds - _options.TailMs / 1000.0, _options.TailMs);

            var cutoff = DateTime.UtcNow.AddSeconds(-_options.HistorySeconds);
            _closedWindows.RemoveAll(w => w.EndUtc < cutoff);
        }
    }

    private readonly record struct Window(DateTime StartUtc, DateTime EndUtc);

    private sealed class Scope(SelfSuppressionGate gate, string reason) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            gate.Close(reason);
        }
    }
}

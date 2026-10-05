namespace Shared;

/// <summary>
/// The loudness of one piece of Bubbles' speech, so a display can move a mouth that
/// actually corresponds to the audio.
///
/// TTS audio never passes through the display app — the API synthesises it and plays it
/// straight out — so the display has no way to measure it. This is the measurement,
/// published as the audio is queued.
///
/// Sent per chunk rather than per frame, with a schedule, because the pipeline
/// deliberately synthesises ahead of playback: streaming individual frames in real time
/// would fight that. The display replays the envelope on its own clock instead, which
/// stays smooth through network jitter and costs almost nothing to send.
/// </summary>
/// <param name="Sequence">
/// Chunk index within a response. 0 means a new response is starting, and is what a
/// display anchors its clock on.
/// </param>
/// <param name="StartOffsetMs">
/// When this chunk's audio is heard, in milliseconds from the start of the response's
/// audio. Derived from the byte position in the playback stream, so it stays exact across
/// a long answer rather than accumulating error.
/// </param>
/// <param name="FrameMs">Milliseconds of audio per frame.</param>
/// <param name="Bands">
/// Values per frame. 1 is plain loudness. The field exists so frequency bands — enough for
/// a crude "oo" versus "ah" mouth shape — can be added later without changing anything
/// that carries or consumes this message.
/// </param>
/// <param name="Levels">
/// <paramref name="Bands"/> values per frame, frame-major, each 0 (silent) to 1 (loud).
/// </param>
/// <param name="LatencyMs">
/// How far behind the queued audio the audible audio is, so the display can delay by the
/// same amount. Owned by the API because only it knows the output buffer depth.
/// </param>
public sealed record SpeechEnvelope(
    int Sequence,
    double StartOffsetMs,
    int FrameMs,
    int Bands,
    float[] Levels,
    int LatencyMs)
{
    /// <summary>Frames in this envelope.</summary>
    public int FrameCount => Bands > 0 ? Levels.Length / Bands : 0;

    /// <summary>Milliseconds of audio this envelope covers.</summary>
    public double DurationMs => FrameCount * (double)FrameMs;

    /// <summary>Offset just past the end of this envelope.</summary>
    public double EndOffsetMs => StartOffsetMs + DurationMs;
}

/// <summary>
/// What the mouth should be doing right now.
/// </summary>
/// <param name="Level">0 (closed) to 1 (wide), already smoothed.</param>
/// <param name="Speaking">Whether audio is playing, or about to.</param>
/// <param name="Bridging">
/// Audio is still coming but this instant has none — the seam between two chunks. The
/// display should show "still going" rather than dropping to idle, which is what makes a
/// 300ms gap read as a natural pause instead of a fault.
/// </param>
public readonly record struct MouthState(float Level, bool Speaking, bool Bridging);

/// <summary>
/// Replays <see cref="SpeechEnvelope"/>s on the display's own clock.
///
/// Pure logic with no UI or platform dependency, so the MAUI display and the GTK display
/// behave identically rather than each inventing its own smoothing.
///
/// Safe to add to from a SignalR callback while sampling from a render loop.
/// </summary>
public sealed class SpeechEnvelopeTimeline
{
    /// <summary>
    /// What a seam between chunks shows. Not zero: bars that collapse to nothing mid-answer
    /// look broken, which is the whole problem this is here to avoid.
    /// </summary>
    private const float BridgeLevel = 0.22f;

    /// <summary>
    /// Rise and fall time constants. Fast attack so consonants land on the beat, slower
    /// release so the mouth doesn't flutter between syllables.
    /// </summary>
    private static readonly TimeSpan Attack = TimeSpan.FromMilliseconds(35);
    private static readonly TimeSpan Release = TimeSpan.FromMilliseconds(120);

    private readonly object _sync = new();
    private readonly List<SpeechEnvelope> _envelopes = [];

    private DateTime? _anchorUtc;
    private bool _complete;
    private float _level;
    private DateTime? _lastSampleUtc;

    /// <summary>
    /// Take one envelope. <see cref="SpeechEnvelope.Sequence"/> 0 starts a new response and
    /// re-anchors the clock to now plus the reported output latency.
    /// </summary>
    /// <param name="nowUtc">Arrival time; defaults to now. Present so this is testable.</param>
    public void Add(SpeechEnvelope envelope, DateTime? nowUtc = null)
    {
        if (envelope.FrameCount == 0) return;

        lock (_sync)
        {
            if (envelope.Sequence == 0 || _anchorUtc is null)
            {
                _envelopes.Clear();
                _complete = false;

                // Offset 0 is heard one output-buffer later than this message arrives.
                // Subtracting StartOffsetMs matters only when joining a response already in
                // progress, where the first envelope seen is not the first one sent.
                _anchorUtc = (nowUtc ?? DateTime.UtcNow)
                    .AddMilliseconds(envelope.LatencyMs - envelope.StartOffsetMs);
            }

            _envelopes.Add(envelope);
        }
    }

    /// <summary>
    /// The response has finished speaking. Until this arrives, a hole in the timeline is
    /// treated as a seam to bridge rather than the end — so the mouth only returns to idle
    /// on genuine completion.
    /// </summary>
    public void Complete()
    {
        lock (_sync)
        {
            _complete = true;
        }
    }

    /// <summary>
    /// Forget everything, for a cancel or a dropped connection. A frozen mouth left
    /// mid-word is worse than no mouth.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _envelopes.Clear();
            _anchorUtc = null;
            _complete = false;
            _level = 0f;
        }
    }

    /// <summary>
    /// Sample the mouth at <paramref name="nowUtc"/>. Call once per rendered frame.
    /// </summary>
    public MouthState Sample(DateTime nowUtc)
    {
        lock (_sync)
        {
            var delta = _lastSampleUtc is { } last ? nowUtc - last : TimeSpan.Zero;
            _lastSampleUtc = nowUtc;

            if (_anchorUtc is not { } anchor)
            {
                _level = 0f;
                return new MouthState(0f, false, false);
            }

            var elapsedMs = (nowUtc - anchor).TotalMilliseconds;

            float target;
            bool speaking;
            var bridging = false;

            if (elapsedMs < 0)
            {
                // Queued but not audible yet: hold the mouth shut rather than opening early.
                target = 0f;
                speaking = true;
            }
            else if (FindFrame(elapsedMs) is { } frameLevel)
            {
                target = frameLevel;
                speaking = true;
            }
            else if (_complete && (_envelopes.Count == 0 || elapsedMs >= _envelopes[^1].EndOffsetMs))
            {
                target = 0f;
                speaking = false;
                if (_level <= 0.01f) _anchorUtc = null;
            }
            else
            {
                // A hole in the timeline with more audio still to come.
                target = BridgeLevel;
                speaking = true;
                bridging = true;
            }

            _level = Smooth(_level, target, delta);
            return new MouthState(_level, speaking, bridging);
        }
    }

    /// <summary>
    /// Level at an offset, or null if no envelope covers it. Scans from the end because
    /// playback moves forward and the newest envelopes are the relevant ones.
    /// </summary>
    private float? FindFrame(double offsetMs)
    {
        for (var i = _envelopes.Count - 1; i >= 0; i--)
        {
            var envelope = _envelopes[i];
            if (offsetMs < envelope.StartOffsetMs || offsetMs >= envelope.EndOffsetMs) continue;

            var frame = (int)((offsetMs - envelope.StartOffsetMs) / envelope.FrameMs);
            frame = Math.Clamp(frame, 0, envelope.FrameCount - 1);

            return envelope.Levels[frame * envelope.Bands];
        }

        return null;
    }

    private static float Smooth(float current, float target, TimeSpan delta)
    {
        if (delta <= TimeSpan.Zero) return target;

        var tau = target > current ? Attack : Release;
        var alpha = 1 - Math.Exp(-delta.TotalMilliseconds / tau.TotalMilliseconds);

        return current + (float)((target - current) * alpha);
    }
}

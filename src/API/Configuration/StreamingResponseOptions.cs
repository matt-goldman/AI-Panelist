namespace API.Configuration;

/// <summary>
/// Controls how a streamed response is cut into speakable chunks.
///
/// The metric this exists to improve is time-to-first-audio, not total generation time.
/// Chunk boundaries are chosen by punctuation, never by token count: a fixed-size split
/// lands mid-phrase and the TTS renders bad prosody at the seam.
/// </summary>
public class StreamingResponseOptions
{
    public const string SectionName = "StreamingResponse";

    /// <summary>
    /// Master switch. Off falls back to the proven behaviour — generate the whole response,
    /// then synthesise it, then play it — including the canned filler phrases.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shortest the opening chunk may be. Too short and it comes out prosodically flat
    /// ("Well," on its own sounds like a glitch); a short opening clause is the sweet spot.
    /// </summary>
    public int FirstChunkMinChars { get; set; } = 20;

    /// <summary>
    /// Longest the opening chunk may be before we cut at the nearest word boundary.
    /// Kept small so audio starts quickly.
    /// </summary>
    public int FirstChunkMaxChars { get; set; } = 110;

    /// <summary>
    /// Shortest a subsequent chunk may be. Later chunks can be longer — playback of the
    /// previous chunk is buying time — and longer chunks give the TTS more context for
    /// natural intonation.
    /// </summary>
    public int MinChunkChars { get; set; } = 80;

    /// <summary>
    /// Longest a subsequent chunk may be before we cut at the nearest word boundary.
    /// </summary>
    public int MaxChunkChars { get; set; } = 320;

    /// <summary>
    /// How many synthesised chunks may be queued ahead of playback. Generation is well
    /// under realtime, so a small lookahead is enough to stay gapless; a larger one just
    /// delays the effect of cancelling.
    /// </summary>
    public int SynthesisLookahead { get; set; } = 2;
}

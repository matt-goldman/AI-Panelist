namespace API.Configuration;

/// <summary>
/// Settings for the self-suppression gate, which stops Bubbles from transcribing its own
/// voice after it comes back around through StreamYard's mix.
/// </summary>
public class SelfSuppressionOptions
{
    public const string SectionName = "SelfSuppression";

    /// <summary>
    /// Master switch. Turning this off restores the naive behaviour (everything captured
    /// gets transcribed), which is only useful for demonstrating the echo problem.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long to keep suppressing after playback ends. Covers audio still in flight
    /// through StreamYard and back into the captured tab mix, so Bubbles' trailing words
    /// don't leak into the next window. 200-500ms is the useful range.
    /// </summary>
    public int TailMs { get; set; } = 350;

    /// <summary>
    /// Audio shorter than this is not worth sending to Whisper — short fragments left over
    /// after suppressed audio is removed produce hallucinated text rather than useful
    /// transcript.
    /// </summary>
    public double MinimumSegmentSeconds { get; set; } = 1.0;

    /// <summary>
    /// How long to remember closed suppression windows. Only needs to exceed the longest
    /// gap between audio being captured and being handed to Whisper.
    /// </summary>
    public int HistorySeconds { get; set; } = 120;
}

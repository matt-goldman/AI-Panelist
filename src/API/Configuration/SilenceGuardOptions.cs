namespace API.Configuration;

/// <summary>
/// The never-go-silent guarantee. If a response produces nothing speakable — the model
/// spent its whole budget reasoning, the LLM errored, or no answer text arrived for too
/// long — Bubbles says a canned line instead of ignoring whoever asked.
///
/// Only acts when something has already gone wrong, so it costs nothing on a normal answer.
/// </summary>
public class SilenceGuardOptions
{
    public const string SectionName = "SilenceGuard";

    /// <summary>
    /// Master switch. Off restores the old behaviour: an empty answer is logged and
    /// Bubbles says nothing.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Longest the model may go without producing answer text — from the trigger to the
    /// first word, or between words — before we give up on it and say a fallback line.
    /// At ~35 tokens/s this is roughly the reasoning budget in practice. 0 disables the
    /// timer but keeps the empty-answer and error fallbacks.
    /// </summary>
    public int MaxSilenceSeconds { get; set; } = 25;

    /// <summary>
    /// Fallback lines, synthesised once in the background at startup and cached so they
    /// still work if the TTS falls over later. Recorded WAVs in
    /// wwwroot/audio/fallback-phrases take precedence over these. Empty uses
    /// <see cref="DefaultLines"/>.
    /// </summary>
    public List<string> Lines { get; set; } = [];

    /// <summary>
    /// How long to wait for the TTS when a fallback line isn't cached yet.
    /// </summary>
    public int SynthesisTimeoutSeconds { get; set; } = 10;

    public static readonly string[] DefaultLines =
    [
        "I'm not a performing seal, you know! Next question.",
        "Look, my brain's gone full spinning beach ball on that one. Come back to me.",
        "Yeah, nah, that one's tied my circuits in a knot. Ask me again in a bit.",
        "I had a brilliant answer for that, and then I lost it. Typical."
    ];
}

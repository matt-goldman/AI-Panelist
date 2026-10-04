namespace API.Configuration;

/// <summary>
/// Answer off the cuff by default, and only think when the question needs it.
///
/// Every response starts as a pass with reasoning turned off, which is fast — first token
/// in well under a second. The model either just answers, or replies with
/// <see cref="Marker"/> and a short holding line ("Now you're asking, let me think...").
/// The holding line is spoken straight away, and a second pass with reasoning on produces
/// the real answer while it plays.
///
/// Reasoning on/off is currently sent as Ollama's "think" option; other providers ignore it.
/// </summary>
public class ResponseTriageOptions
{
    public const string SectionName = "ResponseTriage";

    /// <summary>
    /// Master switch. Off, responses go out in one pass with the model's default reasoning,
    /// exactly as before.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// What the model starts its reply with to ask for thinking time.
    /// </summary>
    public string Marker { get; set; } = "[THINK]";

    /// <summary>
    /// Output budget for the thinking pass — reasoning and answer together. In practice
    /// <see cref="ThinkingTimeoutSeconds"/> bites first; this only stops a runaway.
    /// </summary>
    public int ThinkingMaxOutputTokens { get; set; } = 3000;

    /// <summary>
    /// How long the thinking pass may reason before it must start answering. Past this,
    /// it's abandoned for a quick best-effort answer with reasoning off. Counted from the
    /// end of the triage pass, so the holding line covers the first few seconds of it.
    /// Keep it under <see cref="SilenceGuardOptions.MaxSilenceSeconds"/>, or the silence
    /// guard fires first and a quip replaces the answer. 0 disables the limit.
    /// </summary>
    public int ThinkingTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Appended to the end of the first-pass prompt. {marker} is replaced with
    /// <see cref="Marker"/>.
    /// </summary>
    public string Instruction { get; set; } =
        """
        Almost always, just answer straight away - off the cuff is what a panelist does.
        The only exception: if answering properly needs working something out step by step -
        a calculation, a logic puzzle, or a precise multi-step task - reply with {marker}
        followed by one short, natural holding line (under 15 words) that reacts to what was
        actually asked, and nothing else.
        """;

    /// <summary>
    /// The follow-up on the thinking pass, after the holding line has been said.
    /// </summary>
    public string ContinuePrompt { get; set; } =
        "Now give your full answer, carrying on naturally from what you just said. Don't repeat it.";

    /// <summary>
    /// Sent when the thinking pass has run out of time, asking for an answer without reasoning.
    /// </summary>
    public string GiveUpPrompt { get; set; } =
        "No time to work it all out - give your best off-the-cuff answer right now. Approximate is fine; say so if you're estimating.";

    /// <summary>
    /// Spoken if the model asks for thinking time without giving a usable holding line.
    /// </summary>
    public string DefaultHoldingLine { get; set; } = "Ooh, good one. Give me a second on that.";

    /// <summary>
    /// Longest holding line we'll speak; anything beyond is the model ignoring instructions.
    /// </summary>
    public int MaxHoldingLineChars { get; set; } = 160;
}

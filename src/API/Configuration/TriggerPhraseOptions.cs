namespace API.Configuration;

/// <summary>
/// Spoken trigger phrases: the moderator says a known phrase ("over to you, Bubbles")
/// instead of pressing the button. The button stays as the silent fallback.
///
/// Detection deliberately does not reuse the transcript loop. The transcript wants long
/// windows for accuracy and runs every 5 seconds over 10 second segments, which would put
/// a trigger 5-10 seconds behind the voice. A separate worker transcribes a short rolling
/// window of the same gated audio on a ~1 second hop and does nothing but match phrases.
/// </summary>
public class TriggerPhraseOptions
{
    public const string SectionName = "TriggerPhrases";

    /// <summary>
    /// Master switch. Off, nothing changes: no extra Whisper worker runs and only the
    /// moderator button triggers a response.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Trigger cores. Matched as whole words against normalised text (lowercase, no
    /// punctuation), so "Over to you, Bubbles!" matches "over to you bubbles". Every core
    /// should contain "Bubbles" plus a direction word, so third-person mentions and
    /// host-to-host "what do you think" don't fire. Empty uses <see cref="DefaultPhrases"/>.
    /// </summary>
    public List<string> Phrases { get; set; } = [];

    /// <summary>
    /// Only listen on capture devices whose name or ID is in this list. Empty listens on
    /// every selected device. In person, set this to the moderator's mic so panelists
    /// can't trigger Bubbles by quoting a phrase.
    /// </summary>
    public List<string> Devices { get; set; } = [];

    /// <summary>
    /// How much recent audio each pass transcribes. Must comfortably exceed the longest
    /// phrase, since a phrase is only matched once it sits wholly inside one window.
    /// </summary>
    public int WindowMs { get; set; } = 5000;

    /// <summary>
    /// How often the window is transcribed. Sets the worst-case trigger latency (plus
    /// Whisper's processing time for one window).
    /// </summary>
    public int HopMs { get; set; } = 1000;

    /// <summary>
    /// Clean audio shorter than this isn't transcribed - Whisper invents text from scraps.
    /// </summary>
    public double MinimumSeconds { get; set; } = 1.0;

    /// <summary>
    /// After a trigger fires, ignore further matches for this long. Covers the same
    /// phrase arriving via a second device in person.
    /// </summary>
    public int CooldownMs { get; set; } = 4000;

    public static readonly string[] DefaultPhrases =
    [
        "what do you think bubbles",
        "over to you bubbles",
        "your take bubbles",
        "how about you bubbles",
        "care to weigh in bubbles",
        "give us your view bubbles",
        "bubbles jump in here",
        "take it away bubbles"
    ];
}

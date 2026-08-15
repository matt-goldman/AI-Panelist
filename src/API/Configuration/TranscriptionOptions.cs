namespace API.Configuration;

/// <summary>
/// Guards against Whisper inventing text when there is nothing to transcribe.
///
/// Given near-silent audio, Whisper reliably produces confident nonsense — "Thank you.",
/// "[BLANK_AUDIO]", or fragments of its own initial prompt. During a panel there are long
/// stretches where nobody is talking into the captured mix, so without these guards the
/// rolling transcript and the summaries built from it fill up with hallucinations.
/// </summary>
public class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    /// <summary>
    /// Audio quieter than this RMS is never sent to Whisper. Digital silence is 0; room
    /// tone through a conference mix sits around 0.001-0.01. Raise it if silence still
    /// produces text, lower it if quiet speech is being missed.
    /// </summary>
    public float SilenceRmsThreshold { get; set; } = 0.005f;

    /// <summary>
    /// Discard Whisper's non-speech annotations — "[BLANK_AUDIO]", "[ Silence ]",
    /// "(upbeat music)" — and results that are nothing but punctuation.
    /// </summary>
    public bool DropNonSpeechArtefacts { get; set; } = true;
}

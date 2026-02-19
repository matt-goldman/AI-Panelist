namespace API.Configuration;

public class AIPanelistOptions
{
    public const string SectionName = "AIPanelist";

    /// <summary>
    /// Audio input device ID (null for default device)
    /// </summary>
    public string? AudioInputDeviceId { get; set; }

    /// <summary>
    /// Transcript buffer duration in seconds (default: 180 = 3 minutes)
    /// </summary>
    public int TranscriptBufferSeconds { get; set; } = 180;

    /// <summary>
    /// Summary generation interval in seconds (default: 45 seconds)
    /// </summary>
    public int SummaryIntervalSeconds { get; set; } = 45;

    /// <summary>
    /// Maximum response length in words (default: 150)
    /// </summary>
    public int MaxResponseWords { get; set; } = 150;

    /// <summary>
    /// Enable filler phrases (default: true)
    /// </summary>
    public bool EnableFillerPhrases { get; set; } = true;

    /// <summary>
    /// Filler phrase audio file paths
    /// </summary>
    public List<string> FillerPhraseFiles { get; set; } = [];

    /// <summary>
    /// Introductory phrase for the AI panelist
    /// </summary>
    public string IntroPhrase { get; set; } = string.Empty;

    /// <summary>
    /// STT service implementation type (Mock, Whisper)
    /// </summary>
    public string SttServiceType { get; set; } = "Mock";

    /// <summary>
    /// LLM service implementation type (Mock, Ollama)
    /// </summary>
    public string LlmServiceType { get; set; } = "Mock";

    /// <summary>
    /// TTS service implementation type (Mock, Azure)
    /// </summary>
    public string TtsServiceType { get; set; } = "Mock";

    /// <summary>
    /// Audio device service implementation type (Mock, Windows)
    /// </summary>
    public string? AudioDeviceServiceType { get; set; } = "Mock";

    /// <summary>
    /// Audio playback service implementation type (Mock, Windows)
    /// </summary>
    public string? AudioPlaybackServiceType { get; set; } = "Mock";
}

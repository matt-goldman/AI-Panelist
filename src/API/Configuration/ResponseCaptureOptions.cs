namespace API.Configuration;

/// <summary>
/// Configuration options for response capture (LLM responses and TTS audio)
/// </summary>
public class ResponseCaptureOptions
{
    public const string SectionName = "ResponseCapture";

    /// <summary>
    /// Enable response capture (default: false)
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Capture LLM text responses to file (default: true when enabled)
    /// </summary>
    public bool CaptureTextResponses { get; set; } = true;

    /// <summary>
    /// Capture TTS audio responses to file (default: true when enabled)
    /// </summary>
    public bool CaptureAudioResponses { get; set; } = true;

    /// <summary>
    /// Output directory for text response files (default: ./captured-responses/text)
    /// </summary>
    public string TextOutputDirectory { get; set; } = "./captured-responses/text";

    /// <summary>
    /// Output directory for audio response files (default: ./captured-responses/audio)
    /// </summary>
    public string AudioOutputDirectory { get; set; } = "./captured-responses/audio";
}

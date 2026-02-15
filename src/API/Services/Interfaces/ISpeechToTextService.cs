namespace API.Services.Interfaces;

/// <summary>
/// Interface for speech-to-text transcription services
/// </summary>
public interface ISpeechToTextService
{
    /// <summary>
    /// Start continuous transcription from the specified audio device
    /// </summary>
    Task StartTranscriptionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop continuous transcription
    /// </summary>
    Task StopTranscriptionAsync();

    /// <summary>
    /// Pause transcription (e.g., during TTS playback)
    /// </summary>
    Task PauseTranscriptionAsync();

    /// <summary>
    /// Resume transcription after pause
    /// </summary>
    Task ResumeTranscriptionAsync();

    /// <summary>
    /// Event raised when new transcription is available
    /// </summary>
    event EventHandler<TranscriptionReceivedEventArgs>? TranscriptionReceived;
}

public class TranscriptionReceivedEventArgs : EventArgs
{
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsFinal { get; set; }
    
    /// <summary>
    /// The speaker/device name associated with this transcription (for rough attribution)
    /// </summary>
    public string? SpeakerName { get; set; }
}

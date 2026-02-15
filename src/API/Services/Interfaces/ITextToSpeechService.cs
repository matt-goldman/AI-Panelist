namespace API.Services.Interfaces;

/// <summary>
/// Interface for text-to-speech synthesis services
/// </summary>
public interface ITextToSpeechService
{
    /// <summary>
    /// Synthesize text to speech and play it asynchronously
    /// </summary>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop any currently playing speech
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Check if speech is currently playing
    /// </summary>
    bool IsSpeaking { get; }
}

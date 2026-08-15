namespace API.Services.Interfaces;

/// <summary>
/// Interface for text-to-speech synthesis services
/// </summary>
public interface ITextToSpeechService
{
    /// <summary>
    /// Synthesize text to speech and play it asynchronously
    /// </summary>
    /// <param name="text">The text to speak</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <param name="onPlaybackStarting">Optional callback invoked just before audio playback begins (after synthesis completes)</param>
    Task SpeakAsync(string text, CancellationToken cancellationToken = default, Func<Task>? onPlaybackStarting = null);

    /// <summary>
    /// Synthesize text to a WAV byte array without playing it. This is what the streamed
    /// response path uses: it synthesizes chunk N+1 while chunk N is still playing, so
    /// synthesis and playback have to be separable.
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop any currently playing speech
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Check if speech is currently playing
    /// </summary>
    bool IsSpeaking { get; }
}

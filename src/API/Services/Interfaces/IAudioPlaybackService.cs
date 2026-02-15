namespace API.Services.Interfaces;

/// <summary>
/// Interface for audio playback services
/// </summary>
public interface IAudioPlaybackService
{
    /// <summary>
    /// Play audio from a file path asynchronously
    /// </summary>
    Task PlayAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Play audio from byte array asynchronously
    /// </summary>
    Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop currently playing audio
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Check if audio is currently playing
    /// </summary>
    bool IsPlaying { get; }
}

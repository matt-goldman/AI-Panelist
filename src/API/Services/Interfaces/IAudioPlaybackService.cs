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

    /// <summary>
    /// Open a continuous playback stream for gapless chunked audio (streamed TTS).
    /// The caller writes raw PCM in <paramref name="format"/> and the stream plays it
    /// as it arrives, so chunk N+1 can be written while chunk N is still playing.
    /// </summary>
    Task<IAudioPlaybackStream> OpenStreamAsync(AudioStreamFormat format, CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw PCM format of a playback stream.
/// </summary>
/// <param name="SampleRate">Samples per second, e.g. 24000.</param>
/// <param name="Channels">Channel count, 1 for mono.</param>
/// <param name="BitsPerSample">Bits per sample; only 16-bit signed little-endian PCM is supported.</param>
public sealed record AudioStreamFormat(int SampleRate, int Channels, int BitsPerSample = 16);

/// <summary>
/// A gapless PCM playback stream. Dispose stops playback immediately;
/// <see cref="CompleteAsync"/> lets queued audio drain first.
/// </summary>
public interface IAudioPlaybackStream : IAsyncDisposable
{
    /// <summary>
    /// Queue raw PCM for playback. Returns once the data has been handed off,
    /// not once it has been heard.
    /// </summary>
    Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default);

    /// <summary>
    /// Signal end-of-audio and wait for everything already queued to finish playing.
    /// </summary>
    Task CompleteAsync(CancellationToken cancellationToken = default);
}

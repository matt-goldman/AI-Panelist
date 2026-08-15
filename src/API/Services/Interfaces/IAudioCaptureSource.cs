using Shared;

namespace API.Services.Interfaces;

/// <summary>
/// A single running audio capture stream. Implementations deliver mono PCM samples
/// normalised to [-1, 1] at <see cref="IAudioCaptureFactory.SampleRate"/>, which is
/// what Whisper expects.
/// </summary>
public interface IAudioCaptureSource : IDisposable
{
    /// <summary>
    /// Raised whenever a new block of samples is available.
    /// </summary>
    event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    /// <summary>
    /// Begin capturing. Returns once capture has started; samples arrive via
    /// <see cref="SamplesAvailable"/>.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop capturing and release the underlying device/node.
    /// </summary>
    Task StopAsync();
}

public class AudioSamplesEventArgs(float[] samples) : EventArgs
{
    /// <summary>
    /// Mono samples normalised to [-1, 1].
    /// </summary>
    public float[] Samples { get; } = samples;
}

/// <summary>
/// Creates platform-specific capture streams. This is the seam that keeps
/// <c>WhisperSpeechToTextService</c> free of any platform audio API.
/// </summary>
public interface IAudioCaptureFactory
{
    /// <summary>
    /// Sample rate every capture source delivers. Whisper requires 16 kHz mono.
    /// </summary>
    public const int SampleRate = 16000;

    IAudioCaptureSource Create(AudioDeviceInfo device);
}

using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;
using Shared;

namespace API.Services.Setup;

/// <param name="Peak">Loudest sample heard, 0 to 1.</param>
/// <param name="Rms">Average level over the whole measurement.</param>
public sealed record OutputLevel(string Node, float Peak, float Rms, double Seconds)
{
    /// <summary>
    /// Whether this is speech rather than a silent node. Well below conversational level,
    /// because the point is to tell "something arrived" from "nothing arrived".
    /// </summary>
    public bool HeardSomething => Peak > 0.02f;
}

/// <summary>
/// Listens to the virtual microphone while Bubbles speaks, so the setup app can say
/// whether its voice actually reached the thing the broadcast reads from.
///
/// This exists because of a genuinely confusing property of the design: Bubbles' voice
/// goes into a null sink whose only consumer is the virtual mic, so <em>hearing nothing
/// locally is the correct behaviour</em>. Silence is therefore the expected symptom of a
/// working system and of a completely broken one, which is a terrible thing to be
/// debugging twenty minutes before a show. Measuring the virtual mic tells the two apart
/// without needing ears, speakers, or the stream to be up.
/// </summary>
public sealed class SpeechOutputProbe(
    ILogger<SpeechOutputProbe> logger,
    IAudioCaptureFactory captureFactory,
    IOptions<AIPanelistOptions> panelistOptions,
    IOptions<PipeWireOptions> pipeWireOptions)
{
    private readonly PipeWireOptions _pipeWire = pipeWireOptions.Value;

    /// <summary>
    /// Only meaningful on the PipeWire path, where the virtual mic exists.
    /// </summary>
    public bool Available =>
        string.Equals(panelistOptions.Value.AudioDeviceServiceType, "pipewire", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(_pipeWire.VirtualMicSource);

    /// <summary>
    /// Run <paramref name="action"/> while listening to the virtual mic, and report what
    /// was heard. Returns null when there is nothing to listen to, in which case the action
    /// still runs — a measurement failing must never stop the thing being measured.
    /// </summary>
    public async Task<OutputLevel?> MeasureDuringAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        if (!Available)
        {
            await action(cancellationToken);
            return null;
        }

        var node = _pipeWire.VirtualMicSource!;
        IAudioCaptureSource? capture = null;

        double sumOfSquares = 0;
        var samples = 0;
        var peak = 0f;
        var sync = new Lock();

        try
        {
            capture = captureFactory.Create(new AudioDeviceInfo { Id = node, Name = node });

            capture.SamplesAvailable += OnSamples;
            await capture.StartAsync(cancellationToken);

            // The capture node takes a moment to start carrying audio; without this the
            // opening word is missed and a short line can measure as silence.
            await Task.Delay(250, cancellationToken);

            await action(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't measure the output path on {Node}", node);

            // The action may not have run if setting up the capture threw.
            if (samples == 0) await action(cancellationToken);
            return null;
        }
        finally
        {
            if (capture is not null)
            {
                capture.SamplesAvailable -= OnSamples;
                try
                {
                    await capture.StopAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Stopping the output probe failed");
                }

                capture.Dispose();
            }
        }

        lock (sync)
        {
            if (samples == 0) return null;

            var level = new OutputLevel(
                node, peak, (float)Math.Sqrt(sumOfSquares / samples),
                samples / (double)IAudioCaptureFactory.SampleRate);

            logger.LogInformation(
                "Output path on {Node}: peak {Peak:F3}, RMS {Rms:F4} over {Seconds:F1}s",
                node, level.Peak, level.Rms, level.Seconds);

            return level;
        }

        void OnSamples(object? sender, AudioSamplesEventArgs e)
        {
            lock (sync)
            {
                foreach (var sample in e.Samples)
                {
                    sumOfSquares += sample * (double)sample;
                    var magnitude = Math.Abs(sample);
                    if (magnitude > peak) peak = magnitude;
                }

                samples += e.Samples.Length;
            }
        }
    }
}

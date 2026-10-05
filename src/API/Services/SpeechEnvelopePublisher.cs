using API.Configuration;
using API.Hubs;
using API.Services.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Shared;

namespace API.Services;

/// <summary>
/// Measures Bubbles' outgoing audio and broadcasts the result, so a display can drive a
/// mouth from it. See <see cref="SpeechEnvelope"/> for why it goes out per chunk with a
/// schedule rather than frame by frame.
///
/// Everything here is off the critical path by construction: measuring a chunk is a pass
/// over bytes already in memory, and the broadcast is fire-and-forget. A display that has
/// gone away, or a hub that throws, must never be able to interrupt audio that is playing.
/// </summary>
public sealed class SpeechEnvelopePublisher(
    ILogger<SpeechEnvelopePublisher> logger,
    IHubContext<BubblesHub> hubContext,
    IOptions<SpeechEnvelopeOptions> options)
{
    private readonly SpeechEnvelopeOptions _options = options.Value;
    private readonly Lock _sync = new();

    private int _sequence;
    private double _writtenMs;

    public bool Enabled => _options.Enabled;

    /// <summary>
    /// Start a new response. The next envelope is sequence 0, which is what tells a
    /// display to re-anchor its clock.
    /// </summary>
    public void Begin()
    {
        lock (_sync)
        {
            _sequence = 0;
            _writtenMs = 0;
        }
    }

    /// <summary>
    /// Measure a piece of 16-bit PCM and publish its envelope. Call in the same order the
    /// audio is written, since the schedule is derived from the running byte position.
    /// </summary>
    public void Publish(ReadOnlyMemory<byte> pcm16, AudioStreamFormat format)
    {
        if (!_options.Enabled) return;

        try
        {
            var envelope = Measure(pcm16.Span, format);
            if (envelope is null) return;

            // Not awaited: the audio path does not wait for the display.
            _ = BroadcastAsync(Messages.SpeechEnvelope, envelope);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't publish a speech envelope");
        }
    }

    /// <summary>
    /// Bubbles has finished speaking, for real rather than between chunks.
    /// </summary>
    public void Complete()
    {
        if (!_options.Enabled) return;

        _ = BroadcastAsync(Messages.SpeechComplete, null);
    }

    private async Task BroadcastAsync(string message, SpeechEnvelope? envelope)
    {
        try
        {
            if (envelope is null)
            {
                await hubContext.Clients.All.SendAsync(message);
            }
            else
            {
                await hubContext.Clients.All.SendAsync(message, envelope);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Speech envelope broadcast failed");
        }
    }

    /// <summary>
    /// RMS per frame, mapped to 0..1 on a decibel scale. Loudness is logarithmic, so a
    /// linear mapping of RMS leaves the mouth barely moving for most of a sentence.
    /// </summary>
    private SpeechEnvelope? Measure(ReadOnlySpan<byte> pcm16, AudioStreamFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bytesPerSampleFrame = channels * 2;
        var samplesPerFrame = Math.Max(1, format.SampleRate * _options.FrameMs / 1000);
        var bytesPerFrame = samplesPerFrame * bytesPerSampleFrame;

        var frameCount = pcm16.Length / bytesPerFrame;
        if (frameCount == 0) return null;

        var levels = new float[frameCount];

        for (var frame = 0; frame < frameCount; frame++)
        {
            var slice = pcm16.Slice(frame * bytesPerFrame, bytesPerFrame);
            double sumOfSquares = 0;

            // Channels are summed into one value: a mouth has no use for stereo.
            for (var offset = 0; offset + 1 < slice.Length; offset += 2)
            {
                var sample = (short)(slice[offset] | (slice[offset + 1] << 8)) / 32768.0;
                sumOfSquares += sample * sample;
            }

            var rms = Math.Sqrt(sumOfSquares / (slice.Length / 2.0));
            levels[frame] = ToLevel(rms);
        }

        lock (_sync)
        {
            var envelope = new SpeechEnvelope(
                Sequence:      _sequence++,
                StartOffsetMs: _writtenMs,
                FrameMs:       _options.FrameMs,
                Bands:         1,
                Levels:        levels,
                LatencyMs:     _options.OutputLatencyMs);

            // Advance by the audio actually written, not by the measured frames, so a
            // trailing part-frame doesn't drift the schedule over a long answer.
            _writtenMs += pcm16.Length / (double)(format.SampleRate * bytesPerSampleFrame) * 1000;

            return envelope;
        }
    }

    private float ToLevel(double rms)
    {
        if (rms <= 1e-7) return 0f;

        var db = 20 * Math.Log10(rms);
        var level = (db - _options.FloorDb) / (_options.CeilingDb - _options.FloorDb);

        return (float)Math.Clamp(level, 0, 1);
    }
}

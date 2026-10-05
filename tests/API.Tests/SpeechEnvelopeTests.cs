using API.Configuration;
using API.Services;
using API.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shared;
using Xunit;

namespace API.Tests;

/// <summary>
/// The mouth. The API measures its own audio and publishes an envelope with a schedule;
/// the display replays it on its own clock. These cover both halves.
/// </summary>
public class SpeechEnvelopeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static SpeechEnvelope Envelope(int sequence, double startMs, float[] levels, int latencyMs = 0, int frameMs = 100)
        => new(sequence, startMs, frameMs, Bands: 1, levels, latencyMs);

    /// <summary>
    /// Drives the timeline like a real render loop, because the smoothing is time-based:
    /// a fast attack but a slower release, so the mouth does not flutter between syllables.
    /// </summary>
    private static MouthState Render(SpeechEnvelopeTimeline timeline, DateTime from, DateTime to)
    {
        var state = default(MouthState);
        for (var at = from; at <= to; at = at.AddMilliseconds(16)) state = timeline.Sample(at);
        return state;
    }

    [Fact]
    public void Nothing_received_means_not_speaking()
    {
        var timeline = new SpeechEnvelopeTimeline();

        Assert.False(timeline.Sample(T0).Speaking);
    }

    [Fact]
    public void The_mouth_follows_the_measured_level()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [0.05f, 0.95f, 0.95f, 0.05f, 0.05f, 0.05f]), T0);

        Assert.True(Render(timeline, T0, T0.AddMilliseconds(290)).Level > 0.8f);
        Assert.True(Render(timeline, T0.AddMilliseconds(290), T0.AddMilliseconds(590)).Level < 0.2f);
    }

    [Fact]
    public void A_gap_before_completion_is_a_seam_to_bridge_not_the_end()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [0.5f, 0.5f]), T0);

        var seam = Render(timeline, T0.AddMilliseconds(200), T0.AddMilliseconds(400));

        Assert.True(seam.Speaking);
        Assert.True(seam.Bridging);
        Assert.True(seam.Level > 0.1f); // bars that collapse mid-answer look broken
    }

    [Fact]
    public void A_later_chunk_resumes_on_the_original_anchor()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [0.5f, 0.5f]), T0);
        timeline.Add(Envelope(1, 400, [0.9f, 0.9f]), T0.AddMilliseconds(300));

        var resumed = Render(timeline, T0.AddMilliseconds(380), T0.AddMilliseconds(560));

        Assert.False(resumed.Bridging);
        Assert.True(resumed.Level > 0.7f);
    }

    [Fact]
    public void Only_genuine_completion_closes_the_mouth()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [0.9f]), T0);
        timeline.Complete();

        var ended = Render(timeline, T0.AddMilliseconds(100), T0.AddMilliseconds(1500));

        Assert.False(ended.Speaking);
        Assert.False(ended.Bridging);
    }

    [Fact]
    public void The_mouth_stays_shut_until_the_audio_is_audible()
    {
        // Audio is queued before it is heard, so the display delays by the output latency.
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [1f, 1f, 1f], latencyMs: 300), T0);

        Assert.True(Render(timeline, T0, T0.AddMilliseconds(250)).Level < 0.05f);
        Assert.True(Render(timeline, T0.AddMilliseconds(250), T0.AddMilliseconds(450)).Level > 0.8f);
    }

    [Fact]
    public void A_display_joining_mid_answer_still_lines_up()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(7, 4000, [0.95f, 0.95f]), T0);

        Assert.True(Render(timeline, T0, T0.AddMilliseconds(150)).Level > 0.8f);
    }

    [Fact]
    public void Resetting_clears_everything()
    {
        var timeline = new SpeechEnvelopeTimeline();
        timeline.Add(Envelope(0, 0, [0.9f]), T0);
        timeline.Reset();

        Assert.False(timeline.Sample(T0.AddMilliseconds(20)).Speaking);
    }

    // ---------------------------------------------------------------- publisher

    private static byte[] Pcm(int samples, Func<int, double> amplitude)
    {
        var bytes = new byte[samples * 2];

        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(i * 0.05) * amplitude(i) * short.MaxValue);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return bytes;
    }

    [Fact]
    public void The_published_schedule_follows_the_audio_that_was_queued()
    {
        var captured = new List<SpeechEnvelope>();
        var hub = new CapturingHubContext((method, args) =>
        {
            if (method == Messages.SpeechEnvelope) captured.Add((SpeechEnvelope)args[0]!);
        });

        var publisher = new SpeechEnvelopePublisher(
            NullLogger<SpeechEnvelopePublisher>.Instance, hub, Options.Create(new SpeechEnvelopeOptions()));

        var format = new AudioStreamFormat(16000, 1);

        publisher.Begin();
        publisher.Publish(Pcm(16000, i => i < 8000 ? 0.5 : 0.0), format); // 1s: loud then silent
        publisher.Publish(Pcm(8000, _ => 0.25), format);                  // 0.5s quieter

        Assert.Equal([0, 1], captured.Select(e => e.Sequence));
        Assert.Equal(0, captured[0].StartOffsetMs, 1);
        Assert.Equal(1000, captured[1].StartOffsetMs, 1);
        Assert.Equal(20, captured[0].FrameCount); // 1s of 50ms frames
    }

    [Fact]
    public void Loudness_is_measured_not_guessed()
    {
        var captured = new List<SpeechEnvelope>();
        var hub = new CapturingHubContext((method, args) =>
        {
            if (method == Messages.SpeechEnvelope) captured.Add((SpeechEnvelope)args[0]!);
        });

        var publisher = new SpeechEnvelopePublisher(
            NullLogger<SpeechEnvelopePublisher>.Instance, hub, Options.Create(new SpeechEnvelopeOptions()));

        publisher.Begin();
        publisher.Publish(Pcm(16000, i => i < 8000 ? 0.5 : 0.0), new AudioStreamFormat(16000, 1));

        var levels = captured[0].Levels;

        Assert.All(levels, level => Assert.InRange(level, 0f, 1f));
        Assert.All(levels.Take(10), level => Assert.True(level > 0.5f, "loud audio should read high"));
        Assert.All(levels.Skip(10), level => Assert.Equal(0f, level));
    }
}

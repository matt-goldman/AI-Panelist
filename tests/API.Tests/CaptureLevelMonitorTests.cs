using API.Configuration;
using API.Services;
using Microsoft.Extensions.Options;
using Shared;
using Xunit;

namespace API.Tests;

/// <summary>
/// The capture meter answers two questions with one measurement: "is this node carrying
/// anything at all?", which is the check that would have caught the room-microphone bug in
/// seconds, and "which panelist is on which microphone?", which is how channels get named.
/// </summary>
public class CaptureLevelMonitorTests
{
    private static CaptureLevelMonitor Monitor(float threshold = 0.005f) =>
        new(Options.Create(new TranscriptionOptions { SilenceRmsThreshold = threshold }));

    private static float[] Tone(int count, double amplitude) =>
        [.. Enumerable.Range(0, count).Select(i => (float)(Math.Sin(i * 0.05) * amplitude))];

    [Fact]
    public void Nothing_reported_means_no_meters()
    {
        Assert.Empty(Monitor().Snapshot());
    }

    [Fact]
    public void Speech_reads_as_signal_and_room_tone_does_not()
    {
        var monitor = Monitor();
        var loud = new AudioDeviceInfo { Id = "mic-1", Name = "panelist-1", DisplayName = "Renee" };
        var quiet = new AudioDeviceInfo { Id = "mic-2", Name = "panelist-2" };

        monitor.Report(loud, Tone(1600, 0.3));
        monitor.Report(quiet, Tone(1600, 0.0005));

        var levels = monitor.Snapshot();
        var speech = levels.Single(l => l.DeviceId == "mic-1");
        var silence = levels.Single(l => l.DeviceId == "mic-2");

        Assert.Equal("Renee", speech.Name);               // the name the transcript will use
        Assert.Equal("panelist-2", silence.Name);         // falls back to the node name
        Assert.True(speech.Rms > 0.1f);
        Assert.NotNull(speech.SecondsSinceSignal);
        Assert.Null(silence.SecondsSinceSignal);          // never carried anything

        // Still a reading, so a quiet node is distinguishable from a dead one.
        Assert.True(silence.Rms > 0);
    }

    [Fact]
    public void The_peak_holds_after_the_sound_stops()
    {
        var monitor = Monitor();
        var device = new AudioDeviceInfo { Id = "mic-1", Name = "panelist-1" };

        monitor.Report(device, Tone(1600, 0.3));
        monitor.Report(device, Tone(1600, 0.0));

        var level = monitor.Snapshot().Single();

        Assert.Equal(0f, level.Rms);
        Assert.True(level.Peak > 0.05f, "a peak you only glanced at should stay visible");
    }

    [Fact]
    public void Clearing_removes_stale_meters_when_capture_stops()
    {
        var monitor = Monitor();
        monitor.Report(new AudioDeviceInfo { Id = "mic-1", Name = "panelist-1" }, Tone(1600, 0.3));

        monitor.Clear();

        Assert.Empty(monitor.Snapshot());
    }
}

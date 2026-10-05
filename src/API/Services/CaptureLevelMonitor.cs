using System.Collections.Concurrent;
using API.Configuration;
using Microsoft.Extensions.Options;
using Shared;

namespace API.Services;

/// <summary>
/// How much signal each capture device is carrying, right now.
///
/// Measured on the capture thread as blocks arrive, which costs one pass over samples
/// already in memory and gives a continuous reading rather than one sampled every few
/// seconds when a transcript segment happens to be drained.
///
/// Two jobs, one measurement. It is the answer to "is this node actually carrying
/// anything?", which is the check that would have caught the room-microphone bug in
/// seconds instead of hours. It is also a per-channel level meter, which is how you tell
/// which panelist is on which mic when naming them.
/// </summary>
public sealed class CaptureLevelMonitor(IOptions<TranscriptionOptions> transcriptionOptions)
{
    /// <summary>
    /// How fast the peak reading falls away, per second. Slow enough to see a peak you
    /// only glanced at, fast enough to follow someone stopping talking.
    /// </summary>
    private const double PeakDecayPerSecond = 0.6;

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly float _silenceThreshold = transcriptionOptions.Value.SilenceRmsThreshold;

    /// <summary>
    /// Report a block of captured mono samples. Called on the capture thread.
    /// </summary>
    public void Report(AudioDeviceInfo device, float[] samples)
    {
        if (samples.Length == 0) return;

        double sumOfSquares = 0;
        foreach (var sample in samples) sumOfSquares += sample * (double)sample;
        var rms = (float)Math.Sqrt(sumOfSquares / samples.Length);

        var now = DateTime.UtcNow;
        var entry = _entries.GetOrAdd(device.Id, _ => new Entry());

        lock (entry)
        {
            var decay = Math.Pow(PeakDecayPerSecond, (now - entry.UpdatedUtc).TotalSeconds);
            entry.Name = device.EffectiveName;
            entry.Rms = rms;
            entry.Peak = Math.Max(rms, (float)(entry.Peak * (entry.UpdatedUtc == default ? 0 : decay)));
            entry.UpdatedUtc = now;

            if (rms >= _silenceThreshold) entry.LastSignalUtc = now;
        }
    }

    /// <summary>
    /// Forget a device, so a stale meter doesn't linger after capture stops.
    /// </summary>
    public void Remove(string deviceId) => _entries.TryRemove(deviceId, out _);

    public void Clear() => _entries.Clear();

    /// <summary>
    /// The current reading for every device that has reported.
    /// </summary>
    public IReadOnlyList<CaptureLevel> Snapshot()
    {
        var now = DateTime.UtcNow;
        var levels = new List<CaptureLevel>();

        foreach (var (deviceId, entry) in _entries)
        {
            lock (entry)
            {
                levels.Add(new CaptureLevel(
                    DeviceId:            deviceId,
                    Name:                entry.Name,
                    Rms:                 entry.Rms,
                    Peak:                entry.Peak,
                    SecondsSinceBlock:   (now - entry.UpdatedUtc).TotalSeconds,
                    SecondsSinceSignal:  entry.LastSignalUtc is { } last ? (now - last).TotalSeconds : null));
            }
        }

        return levels.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The RMS above which audio counts as signal rather than room tone. The same
    /// threshold Whisper is gated on, so "carrying signal" means the same thing here as it
    /// does to the transcript.
    /// </summary>
    public float SilenceThreshold => _silenceThreshold;

    private sealed class Entry
    {
        public string Name = string.Empty;
        public float Rms;
        public float Peak;
        public DateTime UpdatedUtc;
        public DateTime? LastSignalUtc;
    }
}

/// <summary>
/// One device's level reading.
/// </summary>
/// <param name="Rms">Level of the most recent block, 0 to 1.</param>
/// <param name="Peak">Recent peak, decaying, so a brief peak stays visible.</param>
/// <param name="SecondsSinceBlock">
/// How long since any audio arrived at all. Climbing means capture itself has stopped,
/// which is a different fault from a node carrying silence.
/// </param>
/// <param name="SecondsSinceSignal">
/// How long since this device carried anything above the silence threshold, or null if it
/// never has.
/// </param>
public sealed record CaptureLevel(
    string DeviceId,
    string Name,
    float Rms,
    float Peak,
    double SecondsSinceBlock,
    double? SecondsSinceSignal);

namespace API.Configuration;

/// <summary>
/// Settings for the Linux/PipeWire audio backend. Node names here are PipeWire
/// <c>node.name</c> values (what <c>pactl list short sources|sinks</c> prints in
/// column 2), not indexes — indexes change between reboots.
/// </summary>
public class PipeWireOptions
{
    public const string SectionName = "PipeWire";

    /// <summary>
    /// Sink that TTS output is played into — this is the AI guest's "mic" feed.
    /// Should be the null sink created by scripts/pipewire-setup.sh.
    /// Null/empty plays to the system default sink (i.e. out loud), which is
    /// almost certainly not what you want during the event.
    /// </summary>
    public string? OutputSink { get; set; }

    /// <summary>
    /// Node to capture from when the moderator app has not selected a device.
    /// Should be the monitor of the sink the StreamYard tab plays into.
    /// </summary>
    public string? DefaultCaptureNode { get; set; }

    /// <summary>
    /// Latency requested from pw-cat for capture, e.g. "50ms" or "1024".
    /// </summary>
    public string CaptureLatency { get; set; } = "50ms";

    /// <summary>
    /// Latency requested from pw-cat for playback.
    /// </summary>
    public string PlaybackLatency { get; set; } = "50ms";

    /// <summary>
    /// Include monitor sources when enumerating input devices. Required, since the
    /// tab-audio capture node is a sink monitor.
    /// </summary>
    public bool IncludeMonitorSources { get; set; } = true;

    /// <summary>
    /// Restart a capture stream automatically if pw-cat exits unexpectedly
    /// (e.g. the node disappears when a browser tab is closed).
    /// </summary>
    public bool AutoRestartCapture { get; set; } = true;

    /// <summary>
    /// Delay before retrying a failed capture stream.
    /// </summary>
    public int CaptureRestartDelayMs { get; set; } = 1000;
}

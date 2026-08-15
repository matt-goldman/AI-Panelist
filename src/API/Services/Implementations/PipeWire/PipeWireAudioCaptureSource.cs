using System.Diagnostics;
using API.Configuration;
using API.Services.Interfaces;
using Shared;

namespace API.Services.Implementations.PipeWire;

/// <summary>
/// Captures audio from a PipeWire node by streaming raw PCM out of <c>pw-cat --record</c>.
///
/// pw-cat is asked for 16 kHz mono s16le directly, so PipeWire does the resampling and
/// downmixing for us and we only have to normalise to float. Targeting by node name means
/// this works identically for a hardware mic, a null-sink monitor (the StreamYard tab mix),
/// or anything else in the graph.
/// </summary>
public sealed class PipeWireAudioCaptureSource : IAudioCaptureSource
{
    private const int BytesPerSample = 2; // s16le
    private const string MonitorSuffix = ".monitor";

    private readonly AudioDeviceInfo _device;
    private readonly PipeWireOptions _options;
    private readonly ILogger _logger;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;

    public event EventHandler<AudioSamplesEventArgs>? SamplesAvailable;

    public PipeWireAudioCaptureSource(AudioDeviceInfo device, PipeWireOptions options, ILogger logger)
    {
        _device = device;
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await VerifyTargetExistsAsync(cancellationToken);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _pumpTask = Task.Run(() => PumpWithRestartAsync(_cts.Token), CancellationToken.None);

        _logger.LogInformation("PipeWire capture started on node {Node} ({DeviceName})",
            TargetNode ?? "<default>", _device.EffectiveName);
    }

    /// <summary>
    /// pw-cat does not fail on an unknown --target; it quietly captures the default source
    /// instead. That failure is close to undetectable at runtime — you get a plausible
    /// stream of the wrong audio — so check the node exists up front and say so loudly.
    /// </summary>
    private async Task VerifyTargetExistsAsync(CancellationToken cancellationToken)
    {
        var target = TargetNode;
        if (string.IsNullOrWhiteSpace(target)) return;

        var isMonitor = target.EndsWith(MonitorSuffix, StringComparison.Ordinal);
        var nodeName = isMonitor ? target[..^MonitorSuffix.Length] : target;
        var kind = isMonitor ? "sinks" : "sources";

        try
        {
            var (exitCode, stdOut, _) = await PipeWireCli.RunAsync(
                "pactl", ["list", "short", kind], cancellationToken);

            if (exitCode != 0) return; // Can't check; let pw-cat try anyway.

            var found = stdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('\t'))
                .Any(fields => fields.Length > 1 && fields[1] == nodeName);

            if (!found)
            {
                _logger.LogError(
                    "PipeWire node '{Node}' not found among {Kind}. pw-cat will silently capture the "
                    + "default source instead of failing, so transcription would run on the wrong audio. "
                    + "Check PipeWire:DefaultCaptureNode and that scripts/bubbles-audio.sh has been run.",
                    nodeName, kind);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not verify PipeWire capture target '{Node}'", target);
        }
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;

        await _cts.CancelAsync();

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }

        _logger.LogInformation("PipeWire capture stopped on node {Node}", TargetNode ?? "<default>");
    }

    private string? TargetNode =>
        string.IsNullOrWhiteSpace(_device.Id) ? _options.DefaultCaptureNode : _device.Id;

    private async Task PumpWithRestartAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PipeWire capture failed on node {Node}", TargetNode ?? "<default>");
            }

            if (!_options.AutoRestartCapture || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _logger.LogWarning("Restarting PipeWire capture on node {Node} in {Delay}ms",
                TargetNode ?? "<default>", _options.CaptureRestartDelayMs);

            try
            {
                await Task.Delay(_options.CaptureRestartDelayMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = "pw-cat",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        startInfo.ArgumentList.Add("--record");
        startInfo.ArgumentList.Add("--rate");
        startInfo.ArgumentList.Add(IAudioCaptureFactory.SampleRate.ToString());
        startInfo.ArgumentList.Add("--channels");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("s16");
        startInfo.ArgumentList.Add("--latency");
        startInfo.ArgumentList.Add(_options.CaptureLatency);
        startInfo.ArgumentList.Add("--raw");

        var target = TargetNode;
        if (!string.IsNullOrWhiteSpace(target))
        {
            // A sink's monitor is not a PipeWire node in its own right - "<sink>.monitor" is
            // a PulseAudio compatibility name. pw-cat does not resolve it and, worse, does
            // not fail: it silently falls back to the default source, so you capture the
            // room mic and never find out. Capture the sink node itself and ask PipeWire
            // for its monitor ports instead.
            if (target.EndsWith(MonitorSuffix, StringComparison.Ordinal))
            {
                startInfo.ArgumentList.Add("--target");
                startInfo.ArgumentList.Add(target[..^MonitorSuffix.Length]);
                startInfo.ArgumentList.Add("-P");
                startInfo.ArgumentList.Add("stream.capture.sink=true");
            }
            else
            {
                startInfo.ArgumentList.Add("--target");
                startInfo.ArgumentList.Add(target);
            }
        }

        // Trailing "-" means write the raw stream to stdout.
        startInfo.ArgumentList.Add("-");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pw-cat for audio capture.");

        var stderrTask = DrainStderrAsync(process, cancellationToken);

        try
        {
            // 100ms of audio per read, matching the old NAudio BufferMilliseconds.
            var buffer = new byte[IAudioCaptureFactory.SampleRate / 10 * BytesPerSample];
            var stream = process.StandardOutput.BaseStream;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    // pw-cat exited.
                    break;
                }

                // A read can land mid-sample; carry the odd byte into the next block.
                var usable = read - (read % BytesPerSample);
                if (usable <= 0) continue;

                var samples = new float[usable / BytesPerSample];
                for (var i = 0; i < samples.Length; i++)
                {
                    var sample = BitConverter.ToInt16(buffer, i * BytesPerSample);
                    samples[i] = sample / 32768f;
                }

                SamplesAvailable?.Invoke(this, new AudioSamplesEventArgs(samples));
            }
        }
        finally
        {
            await TerminateAsync(process);
            await stderrTask;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"pw-cat exited unexpectedly (code {process.ExitCode}) while capturing from '{target ?? "<default>"}'.");
        }
    }

    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogDebug("pw-cat (capture {Node}): {Message}", TargetNode ?? "<default>", line);
                }
            }
        }
        catch (Exception)
        {
            // Draining stderr is best-effort; never let it take down capture.
        }
    }

    private static async Task TerminateAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
        }
        catch (Exception)
        {
            // Process already gone.
        }
    }

    public void Dispose()
    {
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Best effort on teardown.
        }

        _cts?.Dispose();
    }
}

/// <summary>
/// Creates PipeWire capture streams.
/// </summary>
public sealed class PipeWireAudioCaptureFactory(
    Microsoft.Extensions.Options.IOptions<PipeWireOptions> options,
    ILogger<PipeWireAudioCaptureFactory> logger) : IAudioCaptureFactory
{
    public IAudioCaptureSource Create(AudioDeviceInfo device)
        => new PipeWireAudioCaptureSource(device, options.Value, logger);
}

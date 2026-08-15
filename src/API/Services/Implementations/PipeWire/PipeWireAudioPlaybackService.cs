using System.Diagnostics;
using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace API.Services.Implementations.PipeWire;

/// <summary>
/// Plays audio into a specific PipeWire sink via <c>pw-cat --playback</c>.
///
/// During the event the target sink is the null sink whose monitor is remapped into the
/// AI guest's virtual microphone, so everything this service plays goes to StreamYard
/// rather than out of the laptop speakers.
/// </summary>
public class PipeWireAudioPlaybackService(
    ILogger<PipeWireAudioPlaybackService> logger,
    IOptions<PipeWireOptions> options) : IAudioPlaybackService, IDisposable
{
    private readonly PipeWireOptions _options = options.Value;
    private readonly Lock _sync = new();

    private Process? _current;
    private CancellationTokenSource? _cts;

    public bool IsPlaying { get; private set; }

    public async Task PlayAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            logger.LogError("Audio file not found: {FilePath}", filePath);
            throw new FileNotFoundException("Audio file not found", filePath);
        }

        logger.LogInformation("PipeWire: playing {FilePath} to {Sink}", filePath, TargetSinkDescription);

        // Let pw-cat handle the container so we support whatever the filler/intro clips are.
        await RunPlaybackAsync(BuildArguments(format: null, filePath), stdin: null, cancellationToken);
    }

    public async Task PlayAsync(byte[] audioData, CancellationToken cancellationToken = default)
    {
        var wav = WavAudio.Parse(audioData);

        logger.LogInformation("PipeWire: playing {Bytes} bytes ({Rate}Hz {Channels}ch {Bits}bit) to {Sink}",
            audioData.Length, wav.Format.SampleRate, wav.Format.Channels, wav.Format.BitsPerSample,
            TargetSinkDescription);

        await RunPlaybackAsync(
            BuildArguments(wav.Format, "-", wav.PwCatFormat),
            stdin: wav.Pcm,
            cancellationToken);
    }

    public Task<IAudioPlaybackStream> OpenStreamAsync(
        AudioStreamFormat format,
        CancellationToken cancellationToken = default)
    {
        if (format.BitsPerSample != 16)
        {
            throw new ArgumentException("Streaming playback only supports 16-bit PCM.", nameof(format));
        }

        var startInfo = BuildStartInfo(BuildArguments(format, "-"));
        startInfo.RedirectStandardInput = true;

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pw-cat for streaming playback.");

        lock (_sync)
        {
            _current = process;
            IsPlaying = true;
        }

        logger.LogInformation("PipeWire: opened streaming playback ({Rate}Hz {Channels}ch) to {Sink}",
            format.SampleRate, format.Channels, TargetSinkDescription);

        return Task.FromResult<IAudioPlaybackStream>(
            new PipeWirePlaybackStream(process, format, logger, OnStreamFinished));
    }

    public Task StopAsync()
    {
        logger.LogInformation("PipeWire: stopping playback");

        Process? process;
        lock (_sync)
        {
            _cts?.Cancel();
            process = _current;
            _current = null;
            IsPlaying = false;
        }

        KillQuietly(process);
        return Task.CompletedTask;
    }

    private void OnStreamFinished(Process process)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_current, process))
            {
                _current = null;
                IsPlaying = false;
            }
        }
    }

    private string TargetSinkDescription =>
        string.IsNullOrWhiteSpace(_options.OutputSink) ? "<default sink>" : _options.OutputSink;

    private List<string> BuildArguments(AudioStreamFormat? format, string source, string? pwCatFormat = null)
    {
        var arguments = new List<string> { "--playback" };

        if (!string.IsNullOrWhiteSpace(_options.OutputSink))
        {
            arguments.Add("--target");
            arguments.Add(_options.OutputSink);
        }

        arguments.Add("--latency");
        arguments.Add(_options.PlaybackLatency);

        if (format is not null)
        {
            // Raw mode: pw-cat can't infer the format, so state it explicitly.
            arguments.Add("--raw");
            arguments.Add("--rate");
            arguments.Add(format.SampleRate.ToString());
            arguments.Add("--channels");
            arguments.Add(format.Channels.ToString());
            arguments.Add("--format");
            arguments.Add(pwCatFormat ?? "s16");
        }

        arguments.Add(source);
        return arguments;
    }

    private static ProcessStartInfo BuildStartInfo(IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName              = "pw-cat",
            RedirectStandardError = true,
            UseShellExecute       = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task RunPlaybackAsync(
        List<string> arguments,
        ReadOnlyMemory<byte>? stdin,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildStartInfo(arguments);
        startInfo.RedirectStandardInput = stdin.HasValue;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start pw-cat for playback.");

        lock (_sync)
        {
            _cts?.Dispose();
            _cts = cts;
            _current = process;
            IsPlaying = true;
        }

        try
        {
            if (stdin.HasValue)
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin.Value, cts.Token);
                await process.StandardInput.BaseStream.FlushAsync(cts.Token);
                process.StandardInput.Close();
            }

            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            await process.WaitForExitAsync(cts.Token);

            var stderr = await stderrTask;
            if (process.ExitCode != 0)
            {
                logger.LogError("pw-cat playback exited with {ExitCode}: {Error}", process.ExitCode, stderr.Trim());
            }
            else
            {
                logger.LogInformation("PipeWire: playback completed");
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("PipeWire: playback cancelled");
            KillQuietly(process);
        }
        finally
        {
            OnStreamFinished(process);
        }
    }

    private static void KillQuietly(Process? process)
    {
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
    }

    /// <summary>
    /// A pw-cat process being fed raw PCM on stdin. Writes return as soon as the data is
    /// handed to the pipe, so the caller can synthesise the next chunk while this one plays.
    /// </summary>
    private sealed class PipeWirePlaybackStream(
        Process process,
        AudioStreamFormat format,
        ILogger logger,
        Action<Process> onFinished) : IAudioPlaybackStream
    {
        private bool _completed;

        public async Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
        {
            if (_completed)
            {
                throw new InvalidOperationException("Cannot write to a completed playback stream.");
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"pw-cat exited (code {process.ExitCode}) before playback finished.");
            }

            await process.StandardInput.BaseStream.WriteAsync(pcm, cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (_completed) return;
            _completed = true;

            try
            {
                // Closing stdin makes pw-cat drain what it has buffered, then exit.
                process.StandardInput.Close();
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                    logger.LogError("pw-cat streaming playback exited with {ExitCode}: {Error}",
                        process.ExitCode, stderr.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("PipeWire: streaming playback cancelled while draining");
                KillQuietly(process);
            }
            finally
            {
                onFinished(process);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_completed)
            {
                logger.LogDebug("PipeWire: disposing streaming playback ({Rate}Hz) without draining", format.SampleRate);
                KillQuietly(process);
                onFinished(process);
            }

            process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

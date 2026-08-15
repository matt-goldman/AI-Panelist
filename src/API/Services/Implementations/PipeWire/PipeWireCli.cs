using System.Diagnostics;

namespace API.Services.Implementations.PipeWire;

/// <summary>
/// Thin wrapper over the PipeWire/PulseAudio command line tools.
/// </summary>
internal static class PipeWireCli
{
    /// <summary>
    /// Run a command and return stdout. stderr is returned separately rather than
    /// merged, because pactl emits harmless "Invalid ASCII character" noise there
    /// for devices with non-ASCII names.
    /// </summary>
    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName               = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return (process.ExitCode, await stdOutTask, await stdErrTask);
    }

    /// <summary>
    /// Whether the PipeWire tooling this backend depends on is actually present.
    /// </summary>
    public static async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (exitCode, _, _) = await RunAsync("pactl", ["info"], cancellationToken);
            return exitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

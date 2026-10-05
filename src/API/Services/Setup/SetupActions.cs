using API.Configuration;
using API.Services.Implementations.PipeWire;
using Microsoft.Extensions.Options;

namespace API.Services.Setup;

public sealed record ActionResult(bool Succeeded, string Detail, string Output);

/// <summary>
/// The handful of fixes the setup app is allowed to apply itself.
///
/// Deliberately a fixed list, not a command runner. Each entry maps to one
/// <c>bubbles-audio.sh</c> subcommand with no caller-supplied arguments, because this is
/// reachable over HTTP from an API that has no authentication — the same posture as the
/// existing trigger and disable endpoints, which is fine on a laptop on a private network
/// and would not be fine anywhere else.
///
/// The point of running them here rather than printing instructions is that the setup app
/// runs on the machine being set up. Walking to a terminal is the thing being automated away.
/// </summary>
public sealed class SetupActions(
    ILogger<SetupActions> logger,
    IOptions<SetupOptions> options,
    IWebHostEnvironment environment)
{
    public const string AudioGraphUp = "audio-graph-up";
    public const string AudioGraphDown = "audio-graph-down";
    public const string AudioGraphRoute = "audio-graph-route";
    public const string EchoOn = "echo-on";
    public const string EchoOff = "echo-off";

    private static readonly Dictionary<string, (string[] Arguments, string Describes)> Allowed = new()
    {
        [AudioGraphUp]    = (["up"],            "Load the audio graph"),
        [AudioGraphDown]  = (["down"],          "Unload the audio graph"),
        [AudioGraphRoute] = (["route"],         "Route the browser tab into the capture sink"),
        [EchoOn]          = (["echo", "on"],    "Turn the rehearsal echo path on"),
        [EchoOff]         = (["echo", "off"],   "Turn the rehearsal echo path off")
    };

    private readonly SetupOptions _options = options.Value;

    public static IEnumerable<(string Id, string Describes)> Available =>
        Allowed.Select(entry => (entry.Key, entry.Value.Describes));

    public async Task<ActionResult> RunAsync(string id, CancellationToken cancellationToken)
    {
        if (!_options.AllowAudioGraphActions)
        {
            return new ActionResult(false,
                "Running audio graph commands is switched off (Setup:AllowAudioGraphActions).", string.Empty);
        }

        if (!Allowed.TryGetValue(id, out var action))
        {
            return new ActionResult(false, $"'{id}' is not something this can run.", string.Empty);
        }

        var script = ResolveScript();
        if (script is null)
        {
            return new ActionResult(false,
                "Couldn't find bubbles-audio.sh. Set Setup:AudioScriptPath to its full path.", string.Empty);
        }

        logger.LogInformation("Setup action {Id}: {Script} {Arguments}", id, script, string.Join(' ', action.Arguments));

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));

            var (exitCode, stdOut, stdErr) = await PipeWireCli.RunAsync(script, action.Arguments, timeout.Token);
            var output = string.Join('\n', new[] { stdOut, stdErr }.Where(s => !string.IsNullOrWhiteSpace(s)));

            if (exitCode != 0)
            {
                logger.LogWarning("Setup action {Id} exited {ExitCode}", id, exitCode);
                return new ActionResult(false, $"{action.Describes} failed (exit {exitCode}).", output);
            }

            return new ActionResult(true, $"{action.Describes}: done.", output);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup action {Id} failed", id);
            return new ActionResult(false, $"{action.Describes} failed: {ex.Message}", string.Empty);
        }
    }

    /// <summary>
    /// Finds the script relative to the content root, since the API normally runs from
    /// somewhere under the repository. An explicit setting always wins.
    /// </summary>
    private string? ResolveScript()
    {
        if (_options.AudioScriptPath is { Length: > 0 } configured)
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var directory = new DirectoryInfo(environment.ContentRootPath);

        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "scripts", "bubbles-audio.sh");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}

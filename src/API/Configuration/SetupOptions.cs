namespace API.Configuration;

/// <summary>
/// The setup app: readiness checks, live input meters, and the few fixes it can apply
/// itself. Served from the API at /setup.
/// </summary>
public class SetupOptions
{
    public const string SectionName = "Setup";

    /// <summary>
    /// Whether the setup app may run <c>bubbles-audio.sh</c> itself — loading the audio
    /// graph, turning the rehearsal echo path off. Off, it reports the command to run
    /// instead. Only ever enable this where the API is on a network you control, since it
    /// has no authentication.
    /// </summary>
    public bool AllowAudioGraphActions { get; set; } = true;

    /// <summary>
    /// Full path to <c>scripts/bubbles-audio.sh</c>. Left unset, it's looked for by walking
    /// up from the content root, which works whenever the API runs from inside the repository.
    /// </summary>
    public string? AudioScriptPath { get; set; }
}

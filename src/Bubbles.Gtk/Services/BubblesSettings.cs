using System.Text.Json;

namespace Bubbles.Gtk.Services;

/// <summary>
/// Where the GTK build keeps its API address.
///
/// The mobile app uses <c>Preferences</c> plus a CommunityToolkit popup to ask for the
/// address. Neither works here: the GTK Essentials package does not implement Preferences,
/// and CommunityToolkit.Maui has no GTK support. Since this build runs on the same machine
/// as the API, a config file with an environment variable override is a better fit than a
/// prompt anyway - it means the display can start unattended.
/// </summary>
public static class BubblesSettings
{
    public const string EnvironmentVariable = "BUBBLES_API";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "bubbles",
        "settings.json");

    /// <summary>
    /// API address, in precedence order: BUBBLES_API, then the config file, then the
    /// local API's default. Accepts a bare host, host:port, or a full URL.
    /// </summary>
    public static string GetApiAddress()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();

        try
        {
            if (File.Exists(SettingsPath))
            {
                var stored = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath))?.ApiAddress;
                if (!string.IsNullOrWhiteSpace(stored)) return stored.Trim();
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never stop the display coming up.
        }

        return "http://localhost:5141";
    }

    public static void SetApiAddress(string address)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(address)));
        }
        catch (Exception)
        {
            // Non-fatal: we still have the address for this run.
        }
    }

    /// <summary>
    /// Turn whatever was configured into a hub URL. A bare host gets http, not https —
    /// the mobile app hard-codes https because it talks to the API across the network,
    /// but this build normally runs on the same box as the API.
    /// </summary>
    public static Uri BuildHubUri(string address)
    {
        var trimmed = address.Trim().TrimEnd('/');

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        return new Uri($"{trimmed}/bubbles", UriKind.Absolute);
    }

    private sealed record Settings(string ApiAddress);
}

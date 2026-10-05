using System.Text.Json;
using API.Configuration;
using Microsoft.Extensions.Options;

namespace API.Services;

/// <summary>
/// What was chosen on the Inputs page: which nodes to capture, what each speaker is
/// called, and which are switched off.
/// </summary>
public sealed record DeviceSelection(
    List<string> SelectedIds,
    Dictionary<string, string> DisplayNames,
    Dictionary<string, bool> EnabledStates)
{
    public static DeviceSelection Empty() => new([], [], []);
}

/// <summary>
/// Keeps the device setup across a restart.
///
/// It used to live only in memory, which meant every restart silently reset the capture
/// node to the configured default and threw away the speaker names. That is almost
/// certainly why speaker attribution "didn't work on the night": it worked, and then
/// something was restarted. It also makes an in-person run look broken, because the
/// fallback is the browser tab's sink and in a room nothing is feeding it.
///
/// Deliberately a plain JSON file. It has to be readable and deletable by hand when
/// something is wrong at five to showtime.
/// </summary>
public sealed class DeviceSelectionStore
{
    private readonly ILogger<DeviceSelectionStore> _logger;
    private readonly string _path;
    private readonly Lock _sync = new();

    public DeviceSelectionStore(ILogger<DeviceSelectionStore> logger, IOptions<AIPanelistOptions> options)
    {
        _logger = logger;

        _path = options.Value.DeviceSelectionPath is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "bubbles", "device-selection.json");
    }

    /// <summary>Where the file lives, so the setup page can say.</summary>
    public string FilePath => _path;

    public DeviceSelection Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return DeviceSelection.Empty();

                var selection = JsonSerializer.Deserialize<DeviceSelection>(File.ReadAllText(_path));
                if (selection is null) return DeviceSelection.Empty();

                _logger.LogInformation(
                    "Restored device setup from {Path}: {Count} device(s), {Named} named",
                    _path, selection.SelectedIds.Count, selection.DisplayNames.Count);

                return selection;
            }
            catch (Exception ex)
            {
                // A corrupt file must not stop the app starting. Starting with no selection
                // is recoverable in seconds from the setup page; not starting is not.
                _logger.LogWarning(ex, "Couldn't read the device setup from {Path}; starting with none", _path);
                return DeviceSelection.Empty();
            }
        }
    }

    public void Save(DeviceSelection selection)
    {
        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(selection, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Couldn't save the device setup to {Path}", _path);
            }
        }
    }
}

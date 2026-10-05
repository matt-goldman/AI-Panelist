using System.Text.Json;
using API.Configuration;
using API.Services.Interfaces;
using Microsoft.Extensions.Options;
using Shared;

namespace API.Services.Implementations.PipeWire;

/// <summary>
/// Enumerates PipeWire capture nodes via <c>pactl -f json list sources</c>.
///
/// Unlike the Windows service this re-enumerates on every call rather than caching,
/// because the nodes we care about most (the null sinks created by
/// scripts/pipewire-setup.sh, and browser stream nodes) appear and disappear while the
/// app is running. Device IDs are PipeWire <c>node.name</c> values, which are stable
/// across restarts — indexes are not.
/// </summary>
public class PipeWireAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<PipeWireAudioDeviceService> logger;
    private readonly PipeWireOptions _options;
    private readonly DeviceSelectionStore _store;
    private readonly Dictionary<string, string> _displayNames;
    private readonly Dictionary<string, bool> _enabledStates;
    private readonly Lock _sync = new();

    private List<string> _selectedIds;
    private List<AudioDeviceInfo> _lastEnumerated = [];

    public event EventHandler? SelectionChanged;

    public PipeWireAudioDeviceService(
        ILogger<PipeWireAudioDeviceService> logger,
        IOptions<PipeWireOptions> options,
        DeviceSelectionStore store)
    {
        this.logger = logger;
        _options = options.Value;
        _store = store;

        // Restored rather than reset: a restart used to silently drop back to the
        // configured capture node and lose every speaker name.
        var saved = store.Load();
        _selectedIds = saved.SelectedIds;
        _displayNames = saved.DisplayNames;
        _enabledStates = saved.EnabledStates;
    }

    /// <summary>
    /// Write the current setup out. Called after anything the Inputs page can change.
    /// </summary>
    private void Persist()
    {
        DeviceSelection snapshot;
        lock (_sync)
        {
            snapshot = new DeviceSelection(
                [.. _selectedIds],
                new Dictionary<string, string>(_displayNames),
                new Dictionary<string, bool>(_enabledStates));
        }

        _store.Save(snapshot);
    }

    public async Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        var devices = await EnumerateSourcesAsync();

        lock (_sync)
        {
            _lastEnumerated = devices;
        }

        Decorate(devices);
        return devices;
    }

    public AudioDeviceInfo? GetSelectedInputDevice() => GetSelectedInputDevices(includeDisabled: true).FirstOrDefault();

    public List<AudioDeviceInfo> GetSelectedInputDevices() => GetSelectedInputDevices(includeDisabled: false);

    private List<AudioDeviceInfo> GetSelectedInputDevices(bool includeDisabled)
    {
        List<string> ids;
        List<AudioDeviceInfo> known;

        lock (_sync)
        {
            ids = [.. _selectedIds];
            known = _lastEnumerated;
        }

        if (known.Count == 0)
        {
            // First access before anything enumerated (e.g. STT starting at boot).
            known = EnumerateSourcesAsync().GetAwaiter().GetResult();
            lock (_sync)
            {
                _lastEnumerated = known;
            }
        }

        if (ids.Count == 0)
        {
            // Nothing chosen yet: fall back to the configured capture node, then the
            // system default source.
            var fallback = known.FirstOrDefault(d => d.Id == _options.DefaultCaptureNode)
                           ?? known.FirstOrDefault(d => d.IsDefault)
                           ?? known.FirstOrDefault();

            if (fallback is null)
            {
                logger.LogWarning("No PipeWire capture nodes found");
                return [];
            }

            // Worth saying loudly: in a room, the configured capture node is the browser
            // tab's sink, which nothing is feeding, and the symptom is Bubbles hearing
            // nothing at all.
            logger.LogWarning(
                "No capture device has been chosen, so falling back to {Node}. "
                + "If this is an in-person event, pick the microphones on the setup page.",
                fallback.Id);
            ids = [fallback.Id];
        }

        var selected = ids
            .Select(id => known.FirstOrDefault(d => d.Id == id)
                          ?? new AudioDeviceInfo { Id = id, Name = id })
            .ToList();

        Decorate(selected);

        return includeDisabled ? selected : selected.Where(d => d.IsEnabled).ToList();
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        var selected = await SelectInputDevicesAsync([deviceId]);
        return selected.Count == 1;
    }

    public async Task<List<string>> SelectInputDevicesAsync(IEnumerable<string> deviceIds)
    {
        var requested = deviceIds.ToList();
        var devices = await GetInputDevicesAsync();

        var matched = requested.Where(id => devices.Any(d => d.Id == id)).ToList();

        foreach (var missing in requested.Except(matched))
        {
            logger.LogWarning("PipeWire node not found: {DeviceId}", missing);
        }

        lock (_sync)
        {
            _selectedIds = matched;
        }

        Persist();

        logger.LogInformation("Selected {Count} of {Requested} PipeWire capture nodes: {Nodes}",
            matched.Count, requested.Count, string.Join(", ", matched));

        SelectionChanged?.Invoke(this, EventArgs.Empty);

        return matched;
    }

    public Task<bool> SetDeviceDisplayNameAsync(string deviceId, string displayName)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                _displayNames.Remove(deviceId);
            }
            else
            {
                _displayNames[deviceId] = displayName;
            }
        }

        Persist();

        logger.LogInformation("Display name for {DeviceId} set to '{DisplayName}'", deviceId, displayName);
        return Task.FromResult(true);
    }

    public Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled)
    {
        lock (_sync)
        {
            _enabledStates[deviceId] = isEnabled;
        }

        Persist();

        logger.LogInformation("Device {DeviceId} enabled state set to {IsEnabled}", deviceId, isEnabled);

        // A disabled device must stop being captured, not merely stop being listed.
        SelectionChanged?.Invoke(this, EventArgs.Empty);

        return Task.FromResult(true);
    }

    private void Decorate(List<AudioDeviceInfo> devices)
    {
        lock (_sync)
        {
            foreach (var device in devices)
            {
                device.DisplayName = _displayNames.GetValueOrDefault(device.Id);
                device.IsEnabled = _enabledStates.GetValueOrDefault(device.Id, true);
            }
        }
    }

    private async Task<List<AudioDeviceInfo>> EnumerateSourcesAsync()
    {
        var devices = new List<AudioDeviceInfo>();

        string json;
        try
        {
            var (exitCode, stdOut, stdErr) = await PipeWireCli.RunAsync("pactl", ["-f", "json", "list", "sources"]);
            if (exitCode != 0)
            {
                logger.LogError("pactl exited with {ExitCode}: {Error}", exitCode, stdErr);
                return devices;
            }

            json = stdOut;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enumerate PipeWire sources. Is pactl installed and PipeWire running?");
            return devices;
        }

        var defaultSource = await GetDefaultSourceAsync();

        // pactl's JSON writer cannot emit non-ASCII: a RØDE comes back with its
        // description as the literal "(null)". The plain text listing renders it fine, so
        // it is read as a fallback rather than showing the operator a node id where a
        // device name should be - which is exactly the moment you are trying to work out
        // which microphone is which.
        var descriptions = await DescriptionsFromTextAsync();

        try
        {
            using var document = JsonDocument.Parse(json);

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("name", out var nameElement)) continue;

                var nodeName = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(nodeName)) continue;

                var isMonitor = element.TryGetProperty("monitor_source", out var monitorOf)
                                && !string.IsNullOrWhiteSpace(monitorOf.GetString());

                if (isMonitor && !_options.IncludeMonitorSources) continue;

                devices.Add(new AudioDeviceInfo
                {
                    Id        = nodeName,
                    Name      = DescribeSource(element, nodeName, descriptions),
                    IsDefault = nodeName == defaultSource
                });
            }
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse pactl JSON output");
            return devices;
        }

        logger.LogInformation("Found {Count} PipeWire capture nodes", devices.Count);
        return devices;
    }

    /// <summary>
    /// The device's name as a human would recognise it: the JSON description, else the one
    /// from the text listing, else the node id.
    /// </summary>
    private static string DescribeSource(JsonElement element, string nodeName, IReadOnlyDictionary<string, string> fallbacks)
    {
        if (element.TryGetProperty("description", out var descriptionElement))
        {
            var description = descriptionElement.GetString();
            if (IsUsable(description)) return description!;
        }

        return fallbacks.TryGetValue(nodeName, out var fromText) && IsUsable(fromText) ? fromText : nodeName;

        static bool IsUsable(string? value) => !string.IsNullOrWhiteSpace(value) && value != "(null)";
    }

    /// <summary>
    /// Node name to description, read from `pactl list sources`, which unlike the JSON
    /// output copes with non-ASCII device names.
    /// </summary>
    private async Task<Dictionary<string, string>> DescriptionsFromTextAsync()
    {
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var (exitCode, stdOut, _) = await PipeWireCli.RunAsync("pactl", ["list", "sources"]);
            if (exitCode != 0) return descriptions;

            string? name = null;

            foreach (var raw in stdOut.Split('\n'))
            {
                var line = raw.Trim();

                if (line.StartsWith("Name:", StringComparison.Ordinal))
                {
                    name = line["Name:".Length..].Trim();
                }
                else if (name is not null && line.StartsWith("Description:", StringComparison.Ordinal))
                {
                    descriptions[name] = line["Description:".Length..].Trim();
                    name = null;
                }
            }
        }
        catch (Exception ex)
        {
            // Only costs us nicer names, so it must never be fatal.
            logger.LogDebug(ex, "Couldn't read source descriptions from pactl's text output");
        }

        return descriptions;
    }

    private async Task<string?> GetDefaultSourceAsync()
    {
        try
        {
            var (exitCode, stdOut, _) = await PipeWireCli.RunAsync("pactl", ["get-default-source"]);
            return exitCode == 0 ? stdOut.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

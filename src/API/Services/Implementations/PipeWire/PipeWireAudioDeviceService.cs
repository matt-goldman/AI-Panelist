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
public class PipeWireAudioDeviceService(
    ILogger<PipeWireAudioDeviceService> logger,
    IOptions<PipeWireOptions> options) : IAudioDeviceService
{
    private readonly PipeWireOptions _options = options.Value;
    private readonly Dictionary<string, string> _displayNames = [];
    private readonly Dictionary<string, bool> _enabledStates = [];
    private readonly Lock _sync = new();

    private List<string> _selectedIds = [];
    private List<AudioDeviceInfo> _lastEnumerated = [];

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

            logger.LogInformation("No device selected, defaulting to {Node}", fallback.Id);
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

        logger.LogInformation("Selected {Count} of {Requested} PipeWire capture nodes: {Nodes}",
            matched.Count, requested.Count, string.Join(", ", matched));

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

        logger.LogInformation("Display name for {DeviceId} set to '{DisplayName}'", deviceId, displayName);
        return Task.FromResult(true);
    }

    public Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled)
    {
        lock (_sync)
        {
            _enabledStates[deviceId] = isEnabled;
        }

        logger.LogInformation("Device {DeviceId} enabled state set to {IsEnabled}", deviceId, isEnabled);
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
                    Name      = DescribeSource(element, nodeName),
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
    /// pactl reports "(null)" descriptions for some devices, so fall back to the
    /// node name rather than showing the moderator a list of "(null)" entries.
    /// </summary>
    private static string DescribeSource(JsonElement element, string nodeName)
    {
        if (element.TryGetProperty("description", out var descriptionElement))
        {
            var description = descriptionElement.GetString();
            if (!string.IsNullOrWhiteSpace(description) && description != "(null)")
            {
                return description;
            }
        }

        return nodeName;
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

using API.Services.Interfaces;
using NAudio.Wave;
using Shared;

namespace API.Services.Implementations;

/// <summary>
/// Windows implementation of audio device service using NAudio
/// </summary>
public class WindowsAudioDeviceService(ILogger<WindowsAudioDeviceService> logger) : IAudioDeviceService
{
    private readonly ILogger<WindowsAudioDeviceService> _logger = logger;
    private List<AudioDeviceInfo> _selectedDevices = new();
    private List<AudioDeviceInfo>? _cachedDevices;
    private readonly Dictionary<string, string> _displayNames = new();
    private readonly Dictionary<string, bool> _enabledStates = new();

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (_cachedDevices != null)
        {
            // Return cached devices with current display names applied
            ApplyDisplayNames(_cachedDevices);
            return Task.FromResult(_cachedDevices);
        }

        _logger.LogInformation("Enumerating Windows audio input devices");

        _cachedDevices = new List<AudioDeviceInfo>();
        var deviceCount = WaveInEvent.DeviceCount;

        for (int i = 0; i < deviceCount; i++)
        {
            try
            {
                var capabilities = WaveInEvent.GetCapabilities(i);
                var deviceId = i.ToString();
                _cachedDevices.Add(new AudioDeviceInfo
                {
                    Id = deviceId,
                    Name = capabilities.ProductName,
                    DisplayName = _displayNames.GetValueOrDefault(deviceId),
                    IsDefault = i == 0 // First device is typically default
                });
                
                _logger.LogDebug("Found audio device {Index}: {Name}", i, capabilities.ProductName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get capabilities for device {Index}", i);
            }
        }

        _logger.LogInformation("Found {Count} audio input devices", _cachedDevices.Count);
        return Task.FromResult(_cachedDevices);
    }

    public AudioDeviceInfo? GetSelectedInputDevice()
    {
        if (_selectedDevices.Count == 0)
        {
            // Initialize cache on first access
            if (_cachedDevices == null)
            {
                _cachedDevices = GetInputDevicesAsync().GetAwaiter().GetResult();
            }
            var defaultDevice = _cachedDevices.FirstOrDefault(d => d.IsDefault);
            if (defaultDevice != null)
            {
                _selectedDevices.Add(defaultDevice);
            }
        }

        return _selectedDevices.FirstOrDefault();
    }

    public List<AudioDeviceInfo> GetSelectedInputDevices()
    {
        if (_selectedDevices.Count == 0)
        {
            // Initialize with default device on first access
            GetSelectedInputDevice();
        }

        // Apply current display names and enabled states
        ApplyDisplayNames(_selectedDevices);

        // Return only enabled devices for transcription
        return _selectedDevices.Where(d => d.IsEnabled).ToList();
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Selecting audio input device: {DeviceId}", deviceId);

        var devices = await GetInputDevicesAsync();
        var device = devices.FirstOrDefault(d => d.Id == deviceId);

        if (device != null)
        {
            _selectedDevices = [device];
            _logger.LogInformation("Selected device: {Name}", device.Name);
            return true;
        }

        _logger.LogWarning("Device not found: {DeviceId}", deviceId);
        return false;
    }

    public async Task<List<string>> SelectInputDevicesAsync(IEnumerable<string> deviceIds)
    {
        _logger.LogInformation("Selecting multiple audio input devices: {DeviceIds}", string.Join(", ", deviceIds));

        var devices = await GetInputDevicesAsync();
        var selectedIds = new List<string>();
        _selectedDevices = new List<AudioDeviceInfo>();

        foreach (var deviceId in deviceIds)
        {
            var device = devices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null)
            {
                _selectedDevices.Add(device);
                selectedIds.Add(deviceId);
                _logger.LogInformation("Selected device: {Name} (ID: {Id})", device.Name, device.Id);
            }
            else
            {
                _logger.LogWarning("Device not found: {DeviceId}", deviceId);
            }
        }

        _logger.LogInformation("Successfully selected {Count} of {Requested} devices", 
            selectedIds.Count, deviceIds.Count());

        return selectedIds;
    }

    public Task<bool> SetDeviceDisplayNameAsync(string deviceId, string displayName)
    {
        _logger.LogInformation("Setting display name for device {DeviceId} to '{DisplayName}'", 
            deviceId, displayName);

        // Store the display name
        if (string.IsNullOrWhiteSpace(displayName))
        {
            _displayNames.Remove(deviceId);
        }
        else
        {
            _displayNames[deviceId] = displayName;
        }

        // Update cached devices if present
        if (_cachedDevices != null)
        {
            var device = _cachedDevices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null)
            {
                device.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
                _logger.LogInformation("Updated display name for device '{Name}' to '{DisplayName}'", 
                    device.Name, device.EffectiveName);
                return Task.FromResult(true);
            }
        }

        // Device not found in cache, but store the name for when it's enumerated
        _logger.LogWarning("Device {DeviceId} not found in cache, display name stored for later", deviceId);
        return Task.FromResult(false);
    }

    public Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled)
    {
        _logger.LogInformation("Setting device {DeviceId} enabled state to {IsEnabled}", deviceId, isEnabled);

        // Store the enabled state
        _enabledStates[deviceId] = isEnabled;

        // Update cached devices if present
        if (_cachedDevices != null)
        {
            var device = _cachedDevices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null)
            {
                device.IsEnabled = isEnabled;
                _logger.LogInformation("Updated enabled state for device '{Name}' to {IsEnabled}", 
                    device.Name, isEnabled);

                // Also update in selected devices
                var selectedDevice = _selectedDevices.FirstOrDefault(d => d.Id == deviceId);
                if (selectedDevice != null)
                {
                    selectedDevice.IsEnabled = isEnabled;
                }

                return Task.FromResult(true);
            }
        }

        _logger.LogWarning("Device {DeviceId} not found in cache, enabled state stored for later", deviceId);
        return Task.FromResult(false);
    }

    private void ApplyDisplayNames(List<AudioDeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            device.DisplayName = _displayNames.GetValueOrDefault(device.Id);
            device.IsEnabled = _enabledStates.GetValueOrDefault(device.Id, true);
        }
    }
}

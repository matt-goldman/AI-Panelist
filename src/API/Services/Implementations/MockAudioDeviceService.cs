using API.Services.Interfaces;
using Shared;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of audio device service for testing
/// </summary>
public class MockAudioDeviceService : IAudioDeviceService
{
    /// <inheritdoc />
    public event EventHandler? SelectionChanged;

    private readonly ILogger<MockAudioDeviceService> _logger;
    private List<AudioDeviceInfo> _selectedDevices = [];
    private List<AudioDeviceInfo>? _cachedDevices;
    private readonly Dictionary<string, string> _displayNames = [];

    public MockAudioDeviceService(ILogger<MockAudioDeviceService> logger)
    {
        _logger = logger;
    }

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (_cachedDevices != null)
        {
            ApplyDisplayNames(_cachedDevices);
            return Task.FromResult(_cachedDevices);
        }

        _logger.LogInformation("Mock: Enumerating audio input devices");

        _cachedDevices =
        [
            new AudioDeviceInfo
            {
                Id = "mock-device-1",
                Name = "Mock Microphone (Default)",
                IsDefault = true
            },
            new AudioDeviceInfo
            {
                Id = "mock-device-2",
                Name = "Mock USB Microphone",
                IsDefault = false
            },
            new AudioDeviceInfo
            {
                Id = "mock-device-3",
                Name = "Mock Wireless Headset",
                IsDefault = false
            }
        ];

        ApplyDisplayNames(_cachedDevices);
        return Task.FromResult(_cachedDevices);
    }

    public AudioDeviceInfo? GetSelectedInputDevice()
    {
        if (_selectedDevices.Count == 0)
        {
            // Return default device from cached list
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
            GetSelectedInputDevice();
        }

        ApplyDisplayNames(_selectedDevices);
        return _selectedDevices.ToList();
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Mock: Selecting audio input device {DeviceId}", deviceId);

        var devices = await GetInputDevicesAsync();
        var device = devices.FirstOrDefault(d => d.Id == deviceId);

        if (device != null)
        {
            _selectedDevices = [device];
            return true;
        }

        return false;
    }

    public async Task<List<string>> SelectInputDevicesAsync(IEnumerable<string> deviceIds)
    {
        _logger.LogInformation("Mock: Selecting multiple audio input devices: {DeviceIds}", 
            string.Join(", ", deviceIds));

        var devices = await GetInputDevicesAsync();
        var selectedIds = new List<string>();
        _selectedDevices = [];

        foreach (var deviceId in deviceIds)
        {
            var device = devices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null)
            {
                _selectedDevices.Add(device);
                selectedIds.Add(deviceId);
            }
        }

        return selectedIds;
    }

    public Task<bool> SetDeviceDisplayNameAsync(string deviceId, string displayName)
    {
        _logger.LogInformation("Mock: Setting display name for device {DeviceId} to '{DisplayName}'", 
            deviceId, displayName);

        if (string.IsNullOrWhiteSpace(displayName))
        {
            _displayNames.Remove(deviceId);
        }
        else
        {
            _displayNames[deviceId] = displayName;
        }

        if (_cachedDevices != null)
        {
            var device = _cachedDevices.FirstOrDefault(d => d.Id == deviceId);
            if (device != null)
            {
                device.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName;
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    private void ApplyDisplayNames(List<AudioDeviceInfo> devices)
    {
        foreach (var device in devices)
        {
            device.DisplayName = _displayNames.GetValueOrDefault(device.Id);
        }
    }

    public Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled)
    {
        throw new NotImplementedException();
    }
}

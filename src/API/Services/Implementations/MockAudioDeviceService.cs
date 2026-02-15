using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of audio device service for testing
/// </summary>
public class MockAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<MockAudioDeviceService> _logger;
    private List<AudioDeviceInfo> _selectedDevices = new();
    private List<AudioDeviceInfo>? _cachedDevices;

    public MockAudioDeviceService(ILogger<MockAudioDeviceService> logger)
    {
        _logger = logger;
    }

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (_cachedDevices != null)
        {
            return Task.FromResult(_cachedDevices);
        }

        _logger.LogInformation("Mock: Enumerating audio input devices");

        _cachedDevices = new List<AudioDeviceInfo>
        {
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
        };

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
        _selectedDevices = new List<AudioDeviceInfo>();

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
}

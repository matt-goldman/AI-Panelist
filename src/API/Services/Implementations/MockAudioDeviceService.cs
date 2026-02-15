using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of audio device service for testing
/// </summary>
public class MockAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<MockAudioDeviceService> _logger;
    private AudioDeviceInfo? _selectedDevice;
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
        if (_selectedDevice == null)
        {
            // Return default device from cached list
            if (_cachedDevices == null)
            {
                // Initialize cache synchronously on first access
                _cachedDevices = GetInputDevicesAsync().GetAwaiter().GetResult();
            }
            _selectedDevice = _cachedDevices.FirstOrDefault(d => d.IsDefault);
        }

        return _selectedDevice;
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Mock: Selecting audio input device {DeviceId}", deviceId);

        var devices = await GetInputDevicesAsync();
        _selectedDevice = devices.FirstOrDefault(d => d.Id == deviceId);

        return _selectedDevice != null;
    }
}

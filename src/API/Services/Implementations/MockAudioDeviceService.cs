using API.Services.Interfaces;

namespace API.Services.Implementations;

/// <summary>
/// Mock implementation of audio device service for testing
/// </summary>
public class MockAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<MockAudioDeviceService> _logger;
    private AudioDeviceInfo? _selectedDevice;

    public MockAudioDeviceService(ILogger<MockAudioDeviceService> logger)
    {
        _logger = logger;
    }

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        _logger.LogInformation("Mock: Enumerating audio input devices");

        var devices = new List<AudioDeviceInfo>
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

        return Task.FromResult(devices);
    }

    public AudioDeviceInfo? GetSelectedInputDevice()
    {
        if (_selectedDevice == null)
        {
            // Return default device
            var devices = GetInputDevicesAsync().Result;
            _selectedDevice = devices.FirstOrDefault(d => d.IsDefault);
        }

        return _selectedDevice;
    }

    public Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Mock: Selecting audio input device {DeviceId}", deviceId);

        var devices = GetInputDevicesAsync().Result;
        _selectedDevice = devices.FirstOrDefault(d => d.Id == deviceId);

        return Task.FromResult(_selectedDevice != null);
    }
}

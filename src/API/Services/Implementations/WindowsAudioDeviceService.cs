using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

/// <summary>
/// Windows implementation of audio device service using NAudio
/// </summary>
public class WindowsAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<WindowsAudioDeviceService> _logger;
    private AudioDeviceInfo? _selectedDevice;
    private List<AudioDeviceInfo>? _cachedDevices;

    public WindowsAudioDeviceService(ILogger<WindowsAudioDeviceService> logger)
    {
        _logger = logger;
    }

    public Task<List<AudioDeviceInfo>> GetInputDevicesAsync()
    {
        if (_cachedDevices != null)
        {
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
                _cachedDevices.Add(new AudioDeviceInfo
                {
                    Id = i.ToString(),
                    Name = capabilities.ProductName,
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
        if (_selectedDevice == null)
        {
            // Initialize cache on first access
            if (_cachedDevices == null)
            {
                _cachedDevices = GetInputDevicesAsync().GetAwaiter().GetResult();
            }
            _selectedDevice = _cachedDevices.FirstOrDefault(d => d.IsDefault);
        }

        return _selectedDevice;
    }

    public async Task<bool> SelectInputDeviceAsync(string deviceId)
    {
        _logger.LogInformation("Selecting audio input device: {DeviceId}", deviceId);

        var devices = await GetInputDevicesAsync();
        _selectedDevice = devices.FirstOrDefault(d => d.Id == deviceId);

        if (_selectedDevice != null)
        {
            _logger.LogInformation("Selected device: {Name}", _selectedDevice.Name);
        }
        else
        {
            _logger.LogWarning("Device not found: {DeviceId}", deviceId);
        }

        return _selectedDevice != null;
    }
}

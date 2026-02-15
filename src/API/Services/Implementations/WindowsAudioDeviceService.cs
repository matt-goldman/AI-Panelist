using API.Services.Interfaces;
using NAudio.Wave;

namespace API.Services.Implementations;

/// <summary>
/// Windows implementation of audio device service using NAudio
/// </summary>
public class WindowsAudioDeviceService : IAudioDeviceService
{
    private readonly ILogger<WindowsAudioDeviceService> _logger;
    private List<AudioDeviceInfo> _selectedDevices = new();
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

        return _selectedDevices.ToList();
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
}

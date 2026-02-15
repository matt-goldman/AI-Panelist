namespace API.Services.Interfaces;

/// <summary>
/// Interface for audio device enumeration and selection
/// </summary>
public interface IAudioDeviceService
{
    /// <summary>
    /// Get list of available input audio devices
    /// </summary>
    Task<List<AudioDeviceInfo>> GetInputDevicesAsync();

    /// <summary>
    /// Get the currently selected input device
    /// </summary>
    AudioDeviceInfo? GetSelectedInputDevice();

    /// <summary>
    /// Select an input device by ID
    /// </summary>
    Task<bool> SelectInputDeviceAsync(string deviceId);
}

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

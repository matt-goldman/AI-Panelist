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
    /// Get the currently selected input device (legacy single-device support)
    /// </summary>
    AudioDeviceInfo? GetSelectedInputDevice();

    /// <summary>
    /// Get all currently selected input devices
    /// </summary>
    List<AudioDeviceInfo> GetSelectedInputDevices();

    /// <summary>
    /// Select an input device by ID (legacy single-device support)
    /// </summary>
    Task<bool> SelectInputDeviceAsync(string deviceId);

    /// <summary>
    /// Select multiple input devices by their IDs
    /// </summary>
    /// <returns>List of device IDs that were successfully selected</returns>
    Task<List<string>> SelectInputDevicesAsync(IEnumerable<string> deviceIds);
}

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
}

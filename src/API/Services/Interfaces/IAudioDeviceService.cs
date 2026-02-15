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

    /// <summary>
    /// Set a user-friendly display name for a device (e.g., panelist name)
    /// </summary>
    /// <returns>True if the device was found and renamed</returns>
    Task<bool> SetDeviceDisplayNameAsync(string deviceId, string displayName);
}

public class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// User-assigned display name (e.g., panelist name). Returns Name if not set.
    /// </summary>
    public string? DisplayName { get; set; }
    
    /// <summary>
    /// Gets the effective name to use (DisplayName if set, otherwise Name)
    /// </summary>
    public string EffectiveName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    
    public bool IsDefault { get; set; }
}

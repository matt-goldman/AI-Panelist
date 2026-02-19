using Shared;

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

    /// <summary>
    /// Enable or disable a device for transcription. Disabled devices are ignored.
    /// </summary>
    /// <returns>True if the device was found and updated</returns>
    Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled);
}

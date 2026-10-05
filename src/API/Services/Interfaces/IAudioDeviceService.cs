using Shared;

namespace API.Services.Interfaces;

/// <summary>
/// Interface for audio device enumeration and selection
/// </summary>
public interface IAudioDeviceService
{
    /// <summary>
    /// Raised when the set of devices to capture from changes — selected, deselected, or
    /// enabled/disabled.
    ///
    /// Without this, choosing a microphone on the setup page updated a list and nothing
    /// else: capture had already been started from whatever was selected at boot and never
    /// looked again. The page said "selected", the audio did not change, and the only cure
    /// was a restart — which is the worst possible failure mode for the one tool whose job
    /// is to tell you the setup is right.
    /// </summary>
    event EventHandler? SelectionChanged;
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

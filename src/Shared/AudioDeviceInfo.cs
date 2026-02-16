namespace Shared;

/// <summary>
/// Audio device information model shared between API and UI
/// </summary>
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

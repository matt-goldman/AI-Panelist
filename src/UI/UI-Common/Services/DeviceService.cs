using System.Net.Http.Json;
using Shared;

namespace UI_Common.Services;

/// <summary>
/// Service for managing audio devices via the API
/// </summary>
public class DeviceService(HttpClient httpClient)
{

    /// <summary>
    /// Get list of all available audio devices
    /// </summary>
    public async Task<List<AudioDeviceInfo>> GetDevicesAsync()
    {
        var apiAddress = await ApiConfigService.GetApiAddress();
        var url = $"https://{apiAddress.TrimEnd('/')}/api/devices";

        try
        {
            var devices = await httpClient.GetFromJsonAsync<List<AudioDeviceInfo>>(url);
            return devices ?? [];
        }
        catch (Exception)
        {
            // Return empty list on error - caller can handle appropriately
            return [];
        }
    }

    /// <summary>
    /// Rename a device by setting its display name
    /// </summary>
    public async Task<bool> RenameDeviceAsync(string deviceId, string displayName)
    {
        var apiAddress = await ApiConfigService.GetApiAddress();
        var url = $"https://{apiAddress.TrimEnd('/')}/api/devices/{deviceId}/rename";

        try
        {
            var request = new { DisplayName = displayName };
            var response = await httpClient.PostAsJsonAsync(url, request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Enable or disable a device for transcription
    /// </summary>
    public async Task<bool> SetDeviceEnabledAsync(string deviceId, bool isEnabled)
    {
        var apiAddress = await ApiConfigService.GetApiAddress();
        var url = $"https://{apiAddress.TrimEnd('/')}/api/devices/{deviceId}/enabled";

        try
        {
            var request = new { IsEnabled = isEnabled };
            var response = await httpClient.PostAsJsonAsync(url, request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

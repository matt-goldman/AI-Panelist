using System.Net.Http.Json;
using Shared;

namespace UI_Common.Services;

/// <summary>
/// Service for managing audio devices via the API
/// </summary>
public class DeviceService
{
    private readonly HttpClient _httpClient;

    public DeviceService()
    {
        _httpClient = new HttpClient();
    }

    /// <summary>
    /// Get list of all available audio devices
    /// </summary>
    public async Task<List<AudioDeviceInfo>> GetDevicesAsync()
    {
        var apiAddress = await ApiConfigService.GetApiAddress();
        var url = $"{apiAddress.TrimEnd('/')}/api/devices";
        
        try
        {
            var devices = await _httpClient.GetFromJsonAsync<List<AudioDeviceInfo>>(url);
            return devices ?? new List<AudioDeviceInfo>();
        }
        catch (Exception ex)
        {
            // Log or handle error appropriately
            Console.WriteLine($"Error fetching devices: {ex.Message}");
            return new List<AudioDeviceInfo>();
        }
    }

    /// <summary>
    /// Rename a device by setting its display name
    /// </summary>
    public async Task<bool> RenameDeviceAsync(string deviceId, string displayName)
    {
        var apiAddress = await ApiConfigService.GetApiAddress();
        var url = $"{apiAddress.TrimEnd('/')}/api/devices/{deviceId}/rename";
        
        try
        {
            var request = new { DisplayName = displayName };
            var response = await _httpClient.PostAsJsonAsync(url, request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            // Log or handle error appropriately
            Console.WriteLine($"Error renaming device: {ex.Message}");
            return false;
        }
    }
}

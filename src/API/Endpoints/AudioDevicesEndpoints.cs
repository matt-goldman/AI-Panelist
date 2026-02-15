using API.Services.Interfaces;

namespace API.Endpoints;

/// <summary>
/// Extension methods for mapping Audio Device minimal API endpoints
/// </summary>
public static class AudioDevicesEndpoints
{
    /// <summary>
    /// Maps Audio Device endpoints to the application
    /// </summary>
    public static IEndpointRouteBuilder MapAudioDevicesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices")
            .WithTags("Audio Devices");

        group.MapGet("/", GetDevicesAsync)
            .WithName("GetDevices")
            .WithSummary("Get list of available audio input devices");

        group.MapGet("/selected", GetSelectedDevices)
            .WithName("GetSelectedDevices")
            .WithSummary("Get currently selected audio input devices");

        group.MapPost("/select", SelectDevicesAsync)
            .WithName("SelectDevices")
            .WithSummary("Select multiple audio input devices");

        group.MapPost("/select/{deviceId}", SelectDeviceAsync)
            .WithName("SelectDevice")
            .WithSummary("Select a single audio input device (legacy support)");

        group.MapPost("/{deviceId}/rename", RenameDeviceAsync)
            .WithName("RenameDevice")
            .WithSummary("Set a display name for an audio device (e.g., panelist name)");

        return app;
    }

    private static async Task<IResult> GetDevicesAsync(
        IAudioDeviceService audioDeviceService,
        ILogger<AudioDevicesEndpointLogger> logger)
    {
        logger.LogInformation("GetDevices endpoint called");
        var devices = await audioDeviceService.GetInputDevicesAsync();
        return Results.Ok(devices);
    }

    private static IResult GetSelectedDevices(
        IAudioDeviceService audioDeviceService)
    {
        var devices = audioDeviceService.GetSelectedInputDevices();
        return Results.Ok(devices);
    }

    private static async Task<IResult> SelectDevicesAsync(
        SelectDevicesRequest request,
        IAudioDeviceService audioDeviceService,
        ILogger<AudioDevicesEndpointLogger> logger)
    {
        if (request.DeviceIds == null || request.DeviceIds.Count == 0)
        {
            return Results.BadRequest(new { message = "At least one device ID is required" });
        }

        logger.LogInformation("SelectDevices endpoint called with {Count} device(s): {DeviceIds}",
            request.DeviceIds.Count, string.Join(", ", request.DeviceIds));

        var selectedIds = await audioDeviceService.SelectInputDevicesAsync(request.DeviceIds);

        if (selectedIds.Count == 0)
        {
            return Results.NotFound(new { message = "No valid devices found", requestedIds = request.DeviceIds });
        }

        var notFoundIds = request.DeviceIds.Except(selectedIds).ToList();

        return Results.Ok(new
        {
            message = $"Selected {selectedIds.Count} device(s)",
            selectedDeviceIds = selectedIds,
            notFoundDeviceIds = notFoundIds.Count > 0 ? notFoundIds : null
        });
    }

    private static async Task<IResult> SelectDeviceAsync(
        string deviceId,
        IAudioDeviceService audioDeviceService,
        ILogger<AudioDevicesEndpointLogger> logger)
    {
        logger.LogInformation("SelectDevice endpoint called with deviceId: {DeviceId}", deviceId);
        var success = await audioDeviceService.SelectInputDeviceAsync(deviceId);

        if (success)
        {
            return Results.Ok(new { message = "Device selected", deviceId });
        }

        return Results.NotFound(new { message = "Device not found", deviceId });
    }

    private static async Task<IResult> RenameDeviceAsync(
        string deviceId,
        RenameDeviceRequest request,
        IAudioDeviceService audioDeviceService,
        ILogger<AudioDevicesEndpointLogger> logger)
    {
        logger.LogInformation("RenameDevice endpoint called for deviceId: {DeviceId} with name: {DisplayName}", 
            deviceId, request.DisplayName);

        var success = await audioDeviceService.SetDeviceDisplayNameAsync(deviceId, request.DisplayName ?? string.Empty);

        if (success)
        {
            return Results.Ok(new 
            { 
                message = "Device renamed", 
                deviceId, 
                displayName = request.DisplayName 
            });
        }

        return Results.NotFound(new { message = "Device not found", deviceId });
    }
}

/// <summary>
/// Marker class for logging in audio devices endpoints
/// </summary>
public class AudioDevicesEndpointLogger;

/// <summary>
/// Request model for selecting multiple audio devices
/// </summary>
public class SelectDevicesRequest
{
    /// <summary>
    /// List of device IDs to select for audio input
    /// </summary>
    public List<string> DeviceIds { get; set; } = [];
}

/// <summary>
/// Request model for renaming an audio device
/// </summary>
public class RenameDeviceRequest
{
    /// <summary>
    /// The display name to assign to the device (e.g., panelist name).
    /// Set to null or empty to clear the custom name.
    /// </summary>
    public string? DisplayName { get; set; }
}

using API.Services;
using API.Services.Interfaces;

namespace API.Endpoints;

/// <summary>
/// Extension methods for mapping AI Panelist minimal API endpoints
/// </summary>
public static class PanelistEndpoints
{
    /// <summary>
    /// Maps AI Panelist endpoints to the application
    /// </summary>
    public static IEndpointRouteBuilder MapPanelistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/panelist")
            .WithTags("Panelist")
            .WithOpenApi();

        group.MapPost("/trigger", TriggerAsync)
            .WithName("TriggerResponse")
            .WithSummary("Trigger AI panelist to generate and speak a response");

        group.MapPost("/cancel", CancelAsync)
            .WithName("CancelResponse")
            .WithSummary("Cancel current response and set to overflow");

        group.MapPost("/disable", DisableAsync)
            .WithName("DisablePanelist")
            .WithSummary("Disable the AI panelist");

        group.MapPost("/enable", EnableAsync)
            .WithName("EnablePanelist")
            .WithSummary("Re-enable the AI panelist");

        group.MapGet("/devices", GetDevicesAsync)
            .WithName("GetDevices")
            .WithSummary("Get list of available audio input devices");

        group.MapGet("/devices/selected", GetSelectedDevices)
            .WithName("GetSelectedDevices")
            .WithSummary("Get currently selected audio input devices");

        group.MapPost("/devices/select", SelectDevicesAsync)
            .WithName("SelectDevices")
            .WithSummary("Select multiple audio input devices");

        group.MapPost("/devices/select/{deviceId}", SelectDeviceAsync)
            .WithName("SelectDevice")
            .WithSummary("Select a single audio input device (legacy support)");

        return app;
    }

    private static async Task<IResult> TriggerAsync(
        AIPanelistOrchestrator orchestrator,
        ILogger<PanelistEndpointLogger> logger)
    {
        logger.LogInformation("Trigger endpoint called");
        await orchestrator.TriggerResponseAsync();
        return Results.Ok(new { message = "Response triggered" });
    }

    private static async Task<IResult> CancelAsync(
        AIPanelistOrchestrator orchestrator,
        ILogger<PanelistEndpointLogger> logger)
    {
        logger.LogInformation("Cancel endpoint called");
        await orchestrator.CancelResponseAsync();
        return Results.Ok(new { message = "Response cancelled" });
    }

    private static async Task<IResult> DisableAsync(
        AIPanelistOrchestrator orchestrator,
        ILogger<PanelistEndpointLogger> logger)
    {
        logger.LogInformation("Disable endpoint called");
        await orchestrator.DisableAsync();
        return Results.Ok(new { message = "Panelist disabled" });
    }

    private static async Task<IResult> EnableAsync(
        AIPanelistOrchestrator orchestrator,
        ILogger<PanelistEndpointLogger> logger)
    {
        logger.LogInformation("Enable endpoint called");
        await orchestrator.EnableAsync();
        return Results.Ok(new { message = "Panelist enabled" });
    }

    private static async Task<IResult> GetDevicesAsync(
        IAudioDeviceService audioDeviceService,
        ILogger<PanelistEndpointLogger> logger)
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
        ILogger<PanelistEndpointLogger> logger)
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
        ILogger<PanelistEndpointLogger> logger)
    {
        logger.LogInformation("SelectDevice endpoint called with deviceId: {DeviceId}", deviceId);
        var success = await audioDeviceService.SelectInputDeviceAsync(deviceId);

        if (success)
        {
            return Results.Ok(new { message = "Device selected", deviceId });
        }

        return Results.NotFound(new { message = "Device not found", deviceId });
    }
}

/// <summary>
/// Marker class for logging in panelist endpoints
/// </summary>
public class PanelistEndpointLogger;

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

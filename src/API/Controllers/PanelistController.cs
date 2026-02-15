using API.Services;
using API.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PanelistController(
    ILogger<PanelistController> logger,
    AIPanelistOrchestrator orchestrator,
    IAudioDeviceService audioDeviceService) : ControllerBase
{
    private readonly ILogger<PanelistController> _logger = logger;

    /// <summary>
    /// Trigger AI panelist to generate and speak a response
    /// </summary>
    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger()
    {
        _logger.LogInformation("Trigger endpoint called");
        await orchestrator.TriggerResponseAsync();
        return Ok(new { message = "Response triggered" });
    }

    /// <summary>
    /// Cancel current response and set to overflow
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        _logger.LogInformation("Cancel endpoint called");
        await orchestrator.CancelResponseAsync();
        return Ok(new { message = "Response cancelled" });
    }

    /// <summary>
    /// Disable the AI panelist
    /// </summary>
    [HttpPost("disable")]
    public async Task<IActionResult> Disable()
    {
        _logger.LogInformation("Disable endpoint called");
        await orchestrator.DisableAsync();
        return Ok(new { message = "Panelist disabled" });
    }

    /// <summary>
    /// Re-enable the AI panelist
    /// </summary>
    [HttpPost("enable")]
    public async Task<IActionResult> Enable()
    {
        _logger.LogInformation("Enable endpoint called");
        await orchestrator.EnableAsync();
        return Ok(new { message = "Panelist enabled" });
    }

    /// <summary>
    /// Get list of available audio input devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        _logger.LogInformation("GetDevices endpoint called");
        var devices = await audioDeviceService.GetInputDevicesAsync();
        return Ok(devices);
    }

    /// <summary>
    /// Get currently selected audio input devices
    /// </summary>
    [HttpGet("devices/selected")]
    public IActionResult GetSelectedDevices()
    {
        var devices = audioDeviceService.GetSelectedInputDevices();
        return Ok(devices);
    }

    /// <summary>
    /// Select multiple audio input devices
    /// </summary>
    [HttpPost("devices/select")]
    public async Task<IActionResult> SelectDevices([FromBody] SelectDevicesRequest request)
    {
        if (request.DeviceIds == null || request.DeviceIds.Count == 0)
        {
            return BadRequest(new { message = "At least one device ID is required" });
        }

        _logger.LogInformation("SelectDevices endpoint called with {Count} device(s): {DeviceIds}", 
            request.DeviceIds.Count, string.Join(", ", request.DeviceIds));
        
        var selectedIds = await audioDeviceService.SelectInputDevicesAsync(request.DeviceIds);
        
        if (selectedIds.Count == 0)
        {
            return NotFound(new { message = "No valid devices found", requestedIds = request.DeviceIds });
        }

        var notFoundIds = request.DeviceIds.Except(selectedIds).ToList();
        
        return Ok(new 
        { 
            message = $"Selected {selectedIds.Count} device(s)", 
            selectedDeviceIds = selectedIds,
            notFoundDeviceIds = notFoundIds.Count > 0 ? notFoundIds : null
        });
    }

    /// <summary>
    /// Select a single audio input device (legacy support)
    /// </summary>
    [HttpPost("devices/select/{deviceId}")]
    public async Task<IActionResult> SelectDevice(string deviceId)
    {
        _logger.LogInformation("SelectDevice endpoint called with deviceId: {DeviceId}", deviceId);
        var success = await audioDeviceService.SelectInputDeviceAsync(deviceId);
        
        if (success)
        {
            return Ok(new { message = "Device selected", deviceId });
        }
        
        return NotFound(new { message = "Device not found", deviceId });
    }
}

/// <summary>
/// Request model for selecting multiple audio devices
/// </summary>
public class SelectDevicesRequest
{
    /// <summary>
    /// List of device IDs to select for audio input
    /// </summary>
    public List<string> DeviceIds { get; set; } = new();
}

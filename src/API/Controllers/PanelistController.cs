using API.Services;
using API.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PanelistController : ControllerBase
{
    private readonly ILogger<PanelistController> _logger;
    private readonly AIPanelistOrchestrator _orchestrator;
    private readonly IAudioDeviceService _audioDeviceService;

    public PanelistController(
        ILogger<PanelistController> logger,
        AIPanelistOrchestrator orchestrator,
        IAudioDeviceService audioDeviceService)
    {
        _logger = logger;
        _orchestrator = orchestrator;
        _audioDeviceService = audioDeviceService;
    }

    /// <summary>
    /// Trigger AI panelist to generate and speak a response
    /// </summary>
    [HttpPost("trigger")]
    public async Task<IActionResult> Trigger()
    {
        _logger.LogInformation("Trigger endpoint called");
        await _orchestrator.TriggerResponseAsync();
        return Ok(new { message = "Response triggered" });
    }

    /// <summary>
    /// Cancel current response and set to overflow
    /// </summary>
    [HttpPost("cancel")]
    public async Task<IActionResult> Cancel()
    {
        _logger.LogInformation("Cancel endpoint called");
        await _orchestrator.CancelResponseAsync();
        return Ok(new { message = "Response cancelled" });
    }

    /// <summary>
    /// Disable the AI panelist
    /// </summary>
    [HttpPost("disable")]
    public async Task<IActionResult> Disable()
    {
        _logger.LogInformation("Disable endpoint called");
        await _orchestrator.DisableAsync();
        return Ok(new { message = "Panelist disabled" });
    }

    /// <summary>
    /// Re-enable the AI panelist
    /// </summary>
    [HttpPost("enable")]
    public async Task<IActionResult> Enable()
    {
        _logger.LogInformation("Enable endpoint called");
        await _orchestrator.EnableAsync();
        return Ok(new { message = "Panelist enabled" });
    }

    /// <summary>
    /// Get list of available audio input devices
    /// </summary>
    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices()
    {
        _logger.LogInformation("GetDevices endpoint called");
        var devices = await _audioDeviceService.GetInputDevicesAsync();
        return Ok(devices);
    }

    /// <summary>
    /// Get currently selected audio input device
    /// </summary>
    [HttpGet("devices/selected")]
    public IActionResult GetSelectedDevice()
    {
        var device = _audioDeviceService.GetSelectedInputDevice();
        return Ok(device);
    }

    /// <summary>
    /// Select an audio input device
    /// </summary>
    [HttpPost("devices/select/{deviceId}")]
    public async Task<IActionResult> SelectDevice(string deviceId)
    {
        _logger.LogInformation("SelectDevice endpoint called with deviceId: {DeviceId}", deviceId);
        var success = await _audioDeviceService.SelectInputDeviceAsync(deviceId);
        
        if (success)
        {
            return Ok(new { message = "Device selected", deviceId });
        }
        
        return NotFound(new { message = "Device not found", deviceId });
    }
}

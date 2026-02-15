using API.Services;

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
            .WithTags("Panelist");

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
}

/// <summary>
/// Marker class for logging in panelist endpoints
/// </summary>
public class PanelistEndpointLogger;

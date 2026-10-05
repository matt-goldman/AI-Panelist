using API.Services;
using Microsoft.AspNetCore.SignalR;
using Shared;

namespace API.Hubs;

public class BubblesHub(
    AIPanelistOrchestrator orchestrator,
    DisplayRegistry displays,
    ILogger<BubblesHub> logger) : Hub
{
    private readonly ILogger<BubblesHub> _logger = logger;

    public override Task OnConnectedAsync()
    {
        displays.Add(Context.ConnectionId);
        _logger.LogInformation("Display connected ({Count} now connected)", displays.Count);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        displays.Remove(Context.ConnectionId);
        _logger.LogWarning("Display disconnected ({Count} left)", displays.Count);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task UpdateConversationState(ConversationState state)
    {
        await Clients.All.SendAsync(Messages.UpdateConversationState, state);
    }

    public async Task UpdatePanelState(AiPanelistState state)
    {
        _logger.LogInformation("UpdatePanelState called with state: {State}", state);
        
        // When moderator sets state to Listening, trigger the AI response
        if (state == AiPanelistState.Listening)
        {
            _logger.LogInformation("Listening state received - triggering AI response");
            await orchestrator.TriggerResponseAsync();
        }
        
        // Broadcast the state change (orchestrator will also update states during processing)
        await Clients.All.SendAsync(Messages.UpdatePanelState, state);
    }

    public async Task TestCustomState(string stateName)
    {
        _logger.LogInformation("SetCustomState called with stateName: {StateName}", stateName);
        await Clients.All.SendAsync(Messages.TestCustomState, stateName);
    }

    public async Task SetThinkingStateAnimation(string stateName)
    {
        _logger.LogInformation("SetThinkingStateAnimation called with stateName: {StateName}", stateName);
        await Clients.All.SendAsync(Messages.SetThinkingStateAnimation, stateName);
    }

    public async Task SetSpeakingStateAnimation(string stateName)
    {
        _logger.LogInformation("SetSpeakingStateAnimation called with stateName: {StateName}", stateName);
        await Clients.All.SendAsync(Messages.SetSpeakingStateAnimation, stateName);
    }

    public async Task IntroduceSelf()
    {
        _logger.LogInformation("IntroduceSelf called");
        await Clients.All.SendAsync(Messages.IntroduceSelf);
        await orchestrator.IntroduceSelf();
    }
}

using API.Services;
using Microsoft.AspNetCore.SignalR;
using Shared;

namespace API.Hubs;

public class BubblesHub(AIPanelistOrchestrator orchestrator, ILogger<BubblesHub> logger) : Hub
{
    private readonly ILogger<BubblesHub> _logger = logger;

    public async Task UpdateConversationState(ConversationState state)
    {
        await Clients.All.SendAsync("UpdateConversationState", state);
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
        await Clients.All.SendAsync("UpdatePanelState", state);
    }
}

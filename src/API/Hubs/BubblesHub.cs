using Microsoft.AspNetCore.SignalR;
using Shared;
using Shared.Messages;

namespace API.Hubs;

public class BubblesHub : Hub
{
    public async Task UpdateConversationState(ConversationState state)
    {
        await Clients.All.SendAsync("UpdateConversationState", new ConversationStateChange(state));
    }

    public async Task UpdatePanelState(AiPanelistState state)
    {
        await Clients.All.SendAsync("UpdatePanelState", new PanelStateChange(state));
    }
}

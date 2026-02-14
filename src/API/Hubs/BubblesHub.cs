using Microsoft.AspNetCore.SignalR;
using Shared;

namespace API.Hubs;

public class BubblesHub : Hub
{
    public async Task UpdateConversationState(ConversationState state)
    {
        await Clients.All.SendAsync("UpdateConversationState", state);
    }

    public async Task UpdatePanelState(AiPanelistState state)
    {
        await Clients.All.SendAsync("UpdatePanelState", state);
    }
}

using CommunityToolkit.Maui.Extensions;
using Microsoft.AspNetCore.SignalR.Client;
using Shared;
using UI_Common.Popups;


namespace UI_Common.Services;

public class ConversationStateService()
{
    private HubConnection? _hubConnection;

    public State<AiPanelistState> PanelistState = new(AiPanelistState.Idle);

    public State<ConversationState> ConversationState = new(new ConversationState(string.Empty, string.Empty, false, false));

    public async Task Init()
    {
        var apiIpAddress = Preferences.Get("API", "notset");

        if (apiIpAddress == "notset")
        {
            apiIpAddress = await PromptUserForUpAddresss();
        }

        bool connected = false;

        while (connected == false)
        {
            if (await TryConnectHub(apiIpAddress))
            {
                connected = true;
            }
            else
            {
                apiIpAddress = await PromptUserForUpAddresss();
            }
        }
    }

    private static async Task<string> PromptUserForUpAddresss()
    {
        var currentPage = (Application.Current?.Windows[0].Page) ?? throw new Exception("FML");

        var popup = new IPAddressPopup();

        var result = await currentPage.ShowPopupAsync<string>(popup);

        return result.Result ?? throw new Exception("FML");
    }

    private async Task<bool> TryConnectHub(string hubAddress)
    {
        var hubUrl = $"{hubAddress.TrimEnd("/")}/bubbles";

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .Build();

        try
        {
            await _hubConnection.StartAsync();
        }
        catch (Exception)
        {
            return false;
        }

        _hubConnection.On<AiPanelistState>(Shared.Messages.UpdatePanelState, state => PanelistState.SetValue(state));

        _hubConnection.On<ConversationState>(Shared.Messages.UpdateConversationState, state => ConversationState.SetValue(state));

        return true;
    }
}

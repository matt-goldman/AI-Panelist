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
        if (_hubConnection?.State == HubConnectionState.Connected) return;

        var apiIpAddress = Preferences.Get("API", "notset");

        if (apiIpAddress == "notset")
        {
            apiIpAddress = await PromptUserForUpAddresss();
        }

        var connected = await TryConnectHub(apiIpAddress);

        while (connected == false)
        {
            apiIpAddress = await PromptUserForUpAddresss();
            connected = await TryConnectHub(apiIpAddress);
        }
    }

    public Task SetPanelistState(AiPanelistState state)
        => _hubConnection?.SendAsync(Shared.Messages.UpdatePanelState, state) ?? Task.CompletedTask;

    public Task SetConversationState(ConversationState state)
        => _hubConnection?.SendAsync(Shared.Messages.UpdateConversationState, state) ?? Task.CompletedTask;

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
        catch (Exception ex)
        {
            return false;
        }

        _hubConnection.On<AiPanelistState>(Shared.Messages.UpdatePanelState, state => PanelistState.SetValue(state));

        _hubConnection.On<ConversationState>(Shared.Messages.UpdateConversationState, state => ConversationState.SetValue(state));

        Preferences.Set("API", hubAddress);

        return true;
    }
}

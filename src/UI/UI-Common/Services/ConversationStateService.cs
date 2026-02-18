using Microsoft.AspNetCore.SignalR.Client;
using Shared;


namespace UI_Common.Services;

public class ConversationStateService()
{
    private HubConnection? _hubConnection;

    public State<AiPanelistState> PanelistState = new(AiPanelistState.Idle);

    public State<string> CustomState = new(string.Empty);

    public State<string> ThinkingStateAnimation = new(string.Empty);
    public State<string> SpeakingStateAnimation = new(string.Empty);

    public State<ConversationState> ConversationState = new(new ConversationState(string.Empty, string.Empty, false, false));

    public async Task Init()
    {
        if (_hubConnection?.State == HubConnectionState.Connected) return;

        var apiIpAddress = await ApiConfigService.GetApiAddress();

        var connected = await TryConnectHub(apiIpAddress);

        while (connected == false)
        {
            apiIpAddress = await ApiConfigService.GetApiAddress(true);
            connected = await TryConnectHub(apiIpAddress);
        }
    }

    public Task SetPanelistState(AiPanelistState state)
        => _hubConnection?.SendAsync(Shared.Messages.UpdatePanelState, state) ?? Task.CompletedTask;

    public Task SetConversationState(ConversationState state)
        => _hubConnection?.SendAsync(Shared.Messages.UpdateConversationState, state) ?? Task.CompletedTask;

    public Task TestCustomState(string stateName)
        => _hubConnection?.SendAsync(Shared.Messages.TestCustomState, stateName) ?? Task.CompletedTask;

    public Task SetThinkingStateAnimation(string stateName)
        => _hubConnection?.SendAsync(Shared.Messages.SetThinkingStateAnimation, stateName) ?? Task.CompletedTask;

    public Task SetSpeakingStateAnimation(string stateName)
        => _hubConnection?.SendAsync(Shared.Messages.SetSpeakingStateAnimation, stateName) ?? Task.CompletedTask;

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

        _hubConnection.On<string>(Shared.Messages.TestCustomState, stateName => CustomState.SetValue(stateName));

        _hubConnection.On<string>(Shared.Messages.SetThinkingStateAnimation, stateName => ThinkingStateAnimation.SetValue(stateName));

        _hubConnection.On<string>(Shared.Messages.SetSpeakingStateAnimation, stateName => SpeakingStateAnimation.SetValue(stateName));

        Preferences.Set("API", hubAddress);

        return true;
    }
}

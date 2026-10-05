using Microsoft.AspNetCore.SignalR.Client;
using Shared;


namespace UI_Common.Services;

public class ConversationStateService()
{
    private HubConnection? _hubConnection;
    private string? _currentHubAddress;
    private bool _isReconnecting;
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);

    public State<AiPanelistState> PanelistState = new(AiPanelistState.Idle);

    public State<string> CustomState = new(string.Empty);

    public State<string> ThinkingStateAnimation = new(string.Empty);
    public State<string> SpeakingStateAnimation = new(string.Empty);

    public State<ConversationState> ConversationState = new(new ConversationState(string.Empty, string.Empty, false, false));

    /// <summary>
    /// Loudness of the audio Bubbles is playing, replayed on this device's clock. Sampled
    /// from a render loop rather than subscribed to - see <see cref="SpeechEnvelopeTimeline"/>.
    /// </summary>
    public SpeechEnvelopeTimeline Mouth = new();

    /// <summary>
    /// Event raised when connection state changes
    /// </summary>
    public event EventHandler<bool>? ConnectionStateChanged;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;

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

    public async Task<bool> SendWithReconnectAsync(Func<HubConnection, Task> action)
    {
        try
        {
            if (_hubConnection?.State != HubConnectionState.Connected)
            {
                await ReconnectAsync();
            }

            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                await action(_hubConnection);
                return true;
            }
        }
        catch (Exception)
        {
            // Connection lost during send, trigger reconnect
            _ = ReconnectAsync();
        }

        return false;
    }

    public Task SetPanelistState(AiPanelistState state)
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.UpdatePanelState, state));

    public Task SetConversationState(ConversationState state)
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.UpdateConversationState, state));

    public Task TestCustomState(string stateName)
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.TestCustomState, stateName));

    public Task SetThinkingStateAnimation(string stateName)
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.SetThinkingStateAnimation, stateName));

    public Task SetSpeakingStateAnimation(string stateName)
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.SetSpeakingStateAnimation, stateName));

    public Task IntroduceSelf()
        => SendWithReconnectAsync(hub => hub.SendAsync(Shared.Messages.IntroduceSelf));

    private async Task ReconnectAsync()
    {
        if (_isReconnecting || string.IsNullOrEmpty(_currentHubAddress))
            return;

        await _reconnectLock.WaitAsync();
        try
        {
            if (_hubConnection?.State == HubConnectionState.Connected)
                return;

            _isReconnecting = true;
            ConnectionStateChanged?.Invoke(this, false);

            // Dispose old connection
            if (_hubConnection != null)
            {
                try
                {
                    await _hubConnection.DisposeAsync();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }

            // Exponential backoff retry
            var delays = new[] { 0, 1000, 2000, 5000, 10000, 30000 };
            foreach (var delay in delays)
            {
                if (delay > 0)
                    await Task.Delay(delay);

                var connected = await TryConnectHub(_currentHubAddress);
                if (connected)
                {
                    ConnectionStateChanged?.Invoke(this, true);
                    return;
                }
            }

            // If all retries failed, prompt user for new address
            var apiIpAddress = await ApiConfigService.GetApiAddress(true);
            await TryConnectHub(apiIpAddress);
            if (_hubConnection?.State == HubConnectionState.Connected)
            {
                ConnectionStateChanged?.Invoke(this, true);
            }
        }
        finally
        {
            _isReconnecting = false;
            _reconnectLock.Release();
        }
    }

    private async Task<bool> TryConnectHub(string hubAddress)
    {
        var hubUrl = new Uri($"https://{hubAddress.TrimEnd("/")}/bubbles", UriKind.Absolute);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        // Handle automatic reconnection events
        _hubConnection.Reconnecting += error =>
        {
            ConnectionStateChanged?.Invoke(this, false);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += connectionId =>
        {
            ConnectionStateChanged?.Invoke(this, true);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += async error =>
        {
            ConnectionStateChanged?.Invoke(this, false);
            // Trigger manual reconnect after automatic reconnect gives up
            await Task.Delay(5000);
            _ = ReconnectAsync();
        };

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

        _hubConnection.On<string>(Shared.Messages.TestCustomState, stateName => CustomState.SetValue(stateName));

        _hubConnection.On<string>(Shared.Messages.SetThinkingStateAnimation, stateName => ThinkingStateAnimation.SetValue(stateName));

        _hubConnection.On<string>(Shared.Messages.SetSpeakingStateAnimation, stateName => SpeakingStateAnimation.SetValue(stateName));

        _hubConnection.On<SpeechEnvelope>(Shared.Messages.SpeechEnvelope, envelope => Mouth.Add(envelope));

        _hubConnection.On(Shared.Messages.SpeechComplete, () => Mouth.Complete());

        _currentHubAddress = hubAddress;
        Preferences.Set("API", hubAddress);

        return true;
    }

    /// <summary>
    /// Custom retry policy for automatic reconnection
    /// </summary>
    private class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan?[] _retryDelays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            if (retryContext.PreviousRetryCount < _retryDelays.Length)
            {
                return _retryDelays[retryContext.PreviousRetryCount];
            }

            // Keep retrying every 60 seconds
            return TimeSpan.FromSeconds(60);
        }
    }
}

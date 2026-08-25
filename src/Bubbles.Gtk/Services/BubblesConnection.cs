using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Shared;

namespace Bubbles.Gtk.Services;

/// <summary>
/// SignalR client for the panelist hub.
///
/// This is a deliberate duplicate of UI_Common.ConversationStateService rather than a
/// reference to it: that project multi-targets the mobile platforms and depends on
/// Preferences and a CommunityToolkit popup, neither of which exists on GTK. The display
/// app is also receive-only, so all the moderator send methods are dropped.
///
/// It never gives up. If the API is not running yet it keeps retrying, because on the
/// night this thing gets switched on before the API does.
/// </summary>
public class BubblesConnection(ILogger<BubblesConnection> logger) : IAsyncDisposable
{
    private HubConnection? _hubConnection;
    private CancellationTokenSource? _cts;

    public State<AiPanelistState> PanelistState { get; } = new(AiPanelistState.Idle);
    public State<string> CustomState { get; } = new(string.Empty);
    public State<bool> IsConnected { get; } = new(false);

    /// <summary>
    /// Address currently being tried, for the on-screen status line.
    /// </summary>
    public State<string> Endpoint { get; } = new(string.Empty);

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(_cts.Token), CancellationToken.None);
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        var delays = new[] { 0, 1000, 2000, 5000, 10000 };
        var attempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var address = BubblesSettings.GetApiAddress();
            var hubUri = BubblesSettings.BuildHubUri(address);
            Endpoint.SetValue(hubUri.ToString());

            if (await TryConnectAsync(hubUri, cancellationToken))
            {
                BubblesSettings.SetApiAddress(address);
                return;
            }

            var delay = delays[Math.Min(attempt, delays.Length - 1)];
            attempt++;

            logger.LogWarning("Could not reach {Hub}; retrying in {Delay}ms", hubUri, delay);

            try
            {
                await Task.Delay(delay == 0 ? 500 : delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> TryConnectAsync(Uri hubUri, CancellationToken cancellationToken)
    {
        try
        {
            if (_hubConnection is not null)
            {
                await _hubConnection.DisposeAsync();
                _hubConnection = null;
            }

            var connection = new HubConnectionBuilder()
                .WithUrl(hubUri)
                .WithAutomaticReconnect(new AlwaysRetryPolicy())
                .Build();

            connection.On<AiPanelistState>(Messages.UpdatePanelState, state => PanelistState.SetValue(state));
            connection.On<string>(Messages.TestCustomState, name => CustomState.SetValue(name));

            connection.Reconnecting += _ =>
            {
                IsConnected.SetValue(false);
                return Task.CompletedTask;
            };

            connection.Reconnected += _ =>
            {
                IsConnected.SetValue(true);
                return Task.CompletedTask;
            };

            connection.Closed += async error =>
            {
                IsConnected.SetValue(false);
                // Automatic reconnect has given up; fall back to the outer retry loop.
                await Task.Delay(2000, CancellationToken.None);
                if (_cts is { IsCancellationRequested: false })
                {
                    _ = Task.Run(() => ConnectLoopAsync(_cts.Token), CancellationToken.None);
                }
            };

            await connection.StartAsync(cancellationToken);

            _hubConnection = connection;
            IsConnected.SetValue(true);
            logger.LogInformation("Connected to {Hub}", hubUri);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Connection attempt to {Hub} failed", hubUri);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Backs off, then keeps trying forever. A display that has given up is useless.
    /// </summary>
    private sealed class AlwaysRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan?[] Delays =
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            retryContext.PreviousRetryCount < Delays.Length
                ? Delays[retryContext.PreviousRetryCount]
                : TimeSpan.FromSeconds(15);
    }
}

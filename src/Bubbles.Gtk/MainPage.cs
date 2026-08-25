using Bubbles.Gtk.Drawing;
using Bubbles.Gtk.Services;
using Shared;

namespace Bubbles.Gtk;

/// <summary>
/// The display. C# markup rather than XAML: the GTK backend builds on the plain
/// Microsoft.NET.Sdk, which has no XAML compilation.
/// </summary>
public class MainPage : ContentPage
{
    private readonly BubblesConnection _connection;
    private readonly BubblesScene _scene = new();
    private readonly GraphicsView _canvas;

    /// <summary>
    /// Captured at construction. MAUI's MainThread helper throws
    /// NotImplementedInReferenceAssemblyException on GTK - Essentials is only partly
    /// implemented for this backend - but IDispatcher is wired up properly.
    /// </summary>
    private readonly IDispatcher _dispatcher;
    private IDispatcherTimer? _timer;
    private DateTime _lastFrame = DateTime.UtcNow;

    public MainPage(BubblesConnection connection)
    {
        _connection = connection;
        _dispatcher = Dispatcher;

        _canvas = new GraphicsView
        {
            Drawable          = _scene,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions   = LayoutOptions.Fill
        };

        var logo = new Image
        {
            Source            = ImageSource.FromFile(Path.Combine(AppContext.BaseDirectory, "Assets", "bdd_logo.png")),
            WidthRequest      = 400,
            Margin            = 20,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions   = LayoutOptions.Start
        };

        Content = new Grid
        {
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions   = LayoutOptions.Fill,
            Children          = { _canvas, logo }
        };

        // SignalR callbacks arrive on a background thread.
        _connection.PanelistState.Subscribe(state =>
            _dispatcher.Dispatch(() => _scene.State = state));

        _connection.IsConnected.Subscribe(connected =>
            _dispatcher.Dispatch(() =>
            {
                _scene.Connected = connected;
                if (!connected)
                {
                    // Idle rather than stale: a frozen "Speaking" face after the API drops
                    // would be worse than showing nothing.
                    _scene.State = AiPanelistState.Idle;
                }
            }));

        _connection.Endpoint.Subscribe(endpoint =>
            _dispatcher.Dispatch(() =>
                _scene.StatusMessage = $"Waiting for {endpoint}  —  set {BubblesSettings.EnvironmentVariable} to change"));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _connection.Start();

        _lastFrame = DateTime.UtcNow;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33); // ~30fps is plenty and stays light on the CPU
        _timer.Tick += OnTick;
        _timer.Start();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer.Stop();
            _timer = null;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var delta = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        // Clamp so a stalled frame doesn't teleport every bubble off the top.
        _scene.Advance(Math.Min(delta, 0.1));
        _canvas.Invalidate();
    }
}

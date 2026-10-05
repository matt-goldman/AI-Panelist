using Bubbles.Visuals;
using Shared;
using SkiaSharp.Extended.UI.Controls;
using UI_Common.Services;

namespace Bubbles;

public partial class MainPage : ContentPage
{
    private SKConfettiSystem _regularBubbleConfettiSystem;

    /// <summary>
    /// Setting the speaking animation to this, rather than to a Lottie file, swaps the
    /// artwork for the bar meter the GTK display uses. Both are driven by the same audio;
    /// this is the one that looks like a mouth rather than a pulse.
    /// </summary>
    private const string MeterAnimation = "meter";

    /// <summary>
    /// How much the artwork grows at full volume. Small on purpose: this is the default
    /// because it keeps the Lottie exactly as it was and only adds the reaction.
    /// </summary>
    private const double PulseDepth = 0.12;

    private string ThinkingAnimation = "Bubbles.json"; // "ai-loading.json";
    private string SpeakingAnimation = "bizz.json"; //"wave.json";
    private readonly ConversationStateService service;

    private readonly MouthDrawable _mouth = new();
    private IDispatcherTimer? _timer;
    private DateTime _lastFrame = DateTime.UtcNow;
    private double _appliedScale = 1;

    /// <summary>
    /// Whether this response has produced any envelope. Without it the mouth would drop to
    /// its canned animation for a frame or two at the end of every answer, as the timeline
    /// runs out before the state leaves Speaking.
    /// </summary>
    private bool _sawEnvelope;

    public MainPage(ConversationStateService service)
    {
        InitializeComponent();
        _regularBubbleConfettiSystem = new SKConfettiSystem
        {
            EmitterBounds           = SKConfettiEmitterBounds.Bottom,
            Emitter                 = SKConfettiEmitter.Infinite(75, -1),
            Shapes                  = [new SKConfettiCircleShape()],
            Colors                  = [new Color(255, 255, 255, 60)],
            Lifetime                = 7,
            Physics                 = [new SKConfettiPhysics(30, 5), new SKConfettiPhysics(15, 3), new SKConfettiPhysics(6, 2)],
            Gravity                 = new Point(0f, 0.5f),
            StartAngle              = 255,
            EndAngle                = 285,
            MaximumRotationVelocity = 0
        };

        BubbleConfetti.Systems = [_regularBubbleConfettiSystem];
        this.service = service;

        MouthView.Drawable = _mouth;

        service.PanelistState.Subscribe(state =>
        {
            // SignalR callbacks run on a background thread - marshal to UI thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (state != AiPanelistState.Speaking) _sawEnvelope = false;

                switch (state)
                {
                    case AiPanelistState.Thinking:
                        SetIsThinking();
                        break;
                    case AiPanelistState.Speaking:
                        SetIsSpeaking();
                        break;
                    default:
                        SetIsIdle();
                        break;
                }
            });
        });

        service.CustomState.Subscribe(stateName =>
        {
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return;
            }

            //throw new Exception($"Received custom state: {stateName}. Custom states are not currently supported in the Bubbles app.");

            MainThread.BeginInvokeOnMainThread(() => SetCustomState(stateName));
        });

        service.ThinkingStateAnimation.Subscribe(stateName =>
        {
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return;
            }

            ThinkingAnimation = stateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? stateName : $"{stateName}.json";
        });

        service.SpeakingStateAnimation.Subscribe(stateName =>
        {
            if (string.IsNullOrWhiteSpace(stateName))
            {
                return;
            }

            // "meter" is reserved: it means the audio-driven bar meter rather than a file.
            SpeakingAnimation = stateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(stateName, MeterAnimation, StringComparison.OrdinalIgnoreCase)
                ? stateName
                : $"{stateName}.json";
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // The envelope is replayed on this device's clock rather than pushed frame by
        // frame, so it has to be sampled from a render loop. See SpeechEnvelopeTimeline.
        _lastFrame = DateTime.UtcNow;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33); // ~30fps is plenty for a mouth
        _timer.Tick += OnTick;
        _timer.Start();

        await service.Init();
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
        _mouth.ElapsedSeconds += Math.Min((now - _lastFrame).TotalSeconds, 0.1);
        _lastFrame = now;

        var mouth = service.Mouth.Sample(now);
        if (mouth.Speaking) _sawEnvelope = true;

        _mouth.Level = mouth.Level;
        _mouth.Bridging = mouth.Bridging;
        _mouth.HasLevel = _sawEnvelope;

        if (MouthView.IsVisible)
        {
            MouthView.Invalidate();
            return;
        }

        // Pulse the artwork instead. Only applied when it has actually moved, since this
        // is a layout property and this runs thirty times a second on a big display.
        var target = _sawEnvelope ? 1 + mouth.Level * PulseDepth : 1;
        if (Math.Abs(target - _appliedScale) > 0.004)
        {
            _appliedScale = target;
            BubblesState.Scale = target;
        }
    }

    private void SetIsThinking()
    {
        MouthView.IsVisible = false;
        BubblesState.Scale = _appliedScale = 1;
        BubblesState.Source = new SKFileLottieImageSource { File = ThinkingAnimation };
    }

    private void SetIsSpeaking()
    {
        if (string.Equals(SpeakingAnimation, MeterAnimation, StringComparison.OrdinalIgnoreCase))
        {
            BubblesState.Source = null;
            BubblesState.Scale = _appliedScale = 1;
            MouthView.IsVisible = true;
            return;
        }

        MouthView.IsVisible = false;
        BubblesState.Source = new SKFileLottieImageSource { File = SpeakingAnimation };
    }

    private void SetCustomState(string stateName)
    {
        if (!stateName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            stateName += ".json";
        }

        BubblesState.Source = new SKFileLottieImageSource { File = stateName };
    }

    private void SetIsIdle()
    {
        MouthView.IsVisible = false;
        BubblesState.Source = null;

        // Leave the artwork at its natural size, or the next state inherits the last
        // frame's pulse.
        BubblesState.Scale = _appliedScale = 1;
    }

}

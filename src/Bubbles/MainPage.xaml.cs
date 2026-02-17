using Shared;
using SkiaSharp.Extended.UI.Controls;
using UI_Common.Services;

namespace Bubbles;

public partial class MainPage : ContentPage
{
    private SKConfettiSystem _regularBubbleConfettiSystem;

    private const string ThinkingAnimation = "ai-loading.json";
    private const string SpeakingAnimation = "wave.json";
    private readonly ConversationStateService service;

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

        service.PanelistState.Subscribe(state =>
        {
            // SignalR callbacks run on a background thread - marshal to UI thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
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
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await service.Init();
    }

    private void SetIsThinking()
    {
        BubblesState.Source = new SKFileLottieImageSource { File = ThinkingAnimation };
    }

    private void SetIsSpeaking()
    {
        BubblesState.Source = new SKFileLottieImageSource { File = SpeakingAnimation };
    }

    private void SetIsIdle()
    {
        BubblesState.Source = null;
    }

}

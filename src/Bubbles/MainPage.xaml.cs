using SkiaSharp.Extended.UI.Controls;

namespace Bubbles;

public partial class MainPage : ContentPage
{
    private SKConfettiSystem _regularBubbleConfettiSystem;

    public MainPage()
	{
		InitializeComponent();
        _regularBubbleConfettiSystem = new SKConfettiSystem
        {
            EmitterBounds   = SKConfettiEmitterBounds.Bottom,
            Emitter         = SKConfettiEmitter.Infinite(75, -1),
            Shapes          = [new SKConfettiCircleShape()],
            Colors          = [new Color(255, 255, 255, 60)],
            Lifetime        = 30,
            Physics         = [new SKConfettiPhysics(30, 50), new SKConfettiPhysics(15, 10), new SKConfettiPhysics(6, 20)],
            Gravity         = new Point(0.05f, 0.05f),
            MaximumRotationVelocity = 0
        };

        BubbleConfetti.Systems = [_regularBubbleConfettiSystem];
    }
}

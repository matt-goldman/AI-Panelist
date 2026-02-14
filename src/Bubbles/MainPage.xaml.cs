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
            Lifetime        = 7,
            Physics         = [new SKConfettiPhysics(30, 5), new SKConfettiPhysics(15, 3), new SKConfettiPhysics(6, 2)],
            Gravity         = new Point(0f, 0.5f),
            StartAngle      = 255,
            EndAngle        = 285,
            MaximumRotationVelocity = 0
        };

        BubbleConfetti.Systems = [_regularBubbleConfettiSystem];
    }
}

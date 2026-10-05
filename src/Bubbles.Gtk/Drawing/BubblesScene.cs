using Bubbles.Visuals;
using Shared;

namespace Bubbles.Gtk.Drawing;

/// <summary>
/// The whole Bubbles visual, drawn with Microsoft.Maui.Graphics.
///
/// The mobile app uses SkiaSharp for this - SKConfettiView for the rising bubbles and
/// SKLottieView for the state animations. Neither works on the GTK backend, which
/// registers no SkiaSharp handler, so the Lottie files in Resources/Raw have no
/// equivalent here. GraphicsView *is* implemented, so the beer, the bubbles and the state
/// indicator are drawn directly instead. Same theatre, different machinery.
/// </summary>
public sealed class BubblesScene : IDrawable
{
    // Matches BeerBackground in the mobile app: Yellow300 at the top, Amber400 at the bottom.
    private static readonly Color BeerTop = Color.FromArgb("#FFDF20");
    private static readonly Color BeerBottom = Color.FromArgb("#FFB900");
    private static readonly Color Foam = Color.FromArgb("#FFFDF5");

    // The state indicator has to read from across a room. White on yellow does not;
    // a dark roasted amber does, and stays on theme.
    private static readonly Color Ink = Color.FromArgb("#6B3E00");

    private readonly List<Bubble> _bubbles = [];
    private readonly Random _random = new();

    private double _elapsed;
    private float _width = 1f;
    private float _height = 1f;

    public AiPanelistState State { get; set; } = AiPanelistState.Idle;
    public bool Connected { get; set; }
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>
    /// How wide the mouth is, 0 to 1, from the audio actually being played. See
    /// <see cref="SpeechEnvelope"/>.
    /// </summary>
    public float Level { get; set; }

    /// <summary>
    /// Whether <see cref="Level"/> is coming from real audio. False means the API isn't
    /// publishing envelopes - an older build, or the feature switched off - and the
    /// speaking animation falls back to the shaped-to-look-like-speech version.
    /// </summary>
    public bool HasLevel { get; set; }

    /// <summary>
    /// In the seam between two chunks. Audio is still coming, so the mouth shows "still
    /// going" rather than closing.
    /// </summary>
    public bool Bridging { get; set; }

    /// <summary>
    /// Advance the simulation. Called from the UI timer, not from Draw, so the animation
    /// runs at a steady rate regardless of how often the platform decides to repaint.
    /// </summary>
    public void Advance(double seconds)
    {
        _elapsed += seconds;

        // Bubbles rise faster and denser while Bubbles is actually doing something -
        // an at-a-glance cue that reads from across a room.
        var target = State switch
        {
            AiPanelistState.Speaking => 110,
            AiPanelistState.Thinking => 80,
            _                        => 45
        };

        while (_bubbles.Count < target)
        {
            _bubbles.Add(NewBubble(startAtBottom: _bubbles.Count > target - 4));
        }

        for (var i = _bubbles.Count - 1; i >= 0; i--)
        {
            var bubble = _bubbles[i];
            bubble.Y -= bubble.Speed * (float)seconds;
            bubble.Phase += (float)seconds * bubble.WobbleRate;

            // Popped at the foam line; recycle rather than allocate.
            if (bubble.Y < FoamLine)
            {
                _bubbles[i] = NewBubble(startAtBottom: true);
            }
            else
            {
                _bubbles[i] = bubble;
            }
        }

        while (_bubbles.Count > target)
        {
            _bubbles.RemoveAt(_bubbles.Count - 1);
        }
    }

    /// <summary>
    /// Bubbles live in normalised space: Y runs 0 (foam) to 1 (bottom of the glass), and
    /// Radius is a fraction of the smaller dimension. Pixels are only introduced at draw
    /// time. Keeping the simulation resolution-independent means it behaves identically on
    /// a tablet and a projector - and, less obviously, means Advance works before the first
    /// Draw has told us how big the surface is.
    /// </summary>
    private Bubble NewBubble(bool startAtBottom)
    {
        var radius = 0.004f + (float)_random.NextDouble() * 0.016f;

        return new Bubble
        {
            X          = (float)_random.NextDouble(),
            Y          = startAtBottom ? 1.02f + (float)_random.NextDouble() * 0.15f
                                       : FoamLine + (float)_random.NextDouble() * (1f - FoamLine),
            Radius     = radius,
            // Bigger bubbles rise faster, as they do in a real glass.
            Speed      = 0.06f + radius * 8f + (float)_random.NextDouble() * 0.10f,
            Phase      = (float)(_random.NextDouble() * Math.PI * 2),
            WobbleRate = 0.6f + (float)_random.NextDouble() * 1.4f,
            Alpha      = 0.22f + (float)_random.NextDouble() * 0.34f
        };
    }

    private const float FoamLine = 0.06f;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        _width = dirtyRect.Width;
        _height = dirtyRect.Height;

        DrawBeer(canvas, dirtyRect);
        DrawBubbles(canvas, dirtyRect);
        DrawFoam(canvas, dirtyRect);
        DrawState(canvas, dirtyRect);

        if (!Connected)
        {
            DrawStatus(canvas, dirtyRect);
        }
    }

    private static void DrawBeer(ICanvas canvas, RectF rect)
    {
        var paint = new LinearGradientPaint
        {
            StartColor = BeerTop,
            EndColor   = BeerBottom,
            StartPoint = new Point(0.5, 0),
            EndPoint   = new Point(0.5, 1)
        };

        canvas.SetFillPaint(paint, rect);
        canvas.FillRectangle(rect);
    }

    private void DrawBubbles(ICanvas canvas, RectF rect)
    {
        var scale = MathF.Min(rect.Width, rect.Height);

        foreach (var bubble in _bubbles)
        {
            var radius = bubble.Radius * scale;

            // Horizontal wobble so they drift rather than travel in straight lines.
            var wobble = MathF.Sin(bubble.Phase) * radius * 1.6f;
            var x = bubble.X * rect.Width + wobble;
            var y = bubble.Y * rect.Height;

            canvas.FillColor = Colors.White.WithAlpha(bubble.Alpha);
            canvas.FillCircle(x, y, radius);

            // A highlight on the larger ones reads as glass rather than dots.
            if (radius > 8f)
            {
                canvas.FillColor = Colors.White.WithAlpha(MathF.Min(1f, bubble.Alpha * 1.6f));
                canvas.FillCircle(x - radius * 0.3f, y - radius * 0.3f, radius * 0.22f);
            }
        }
    }

    private void DrawFoam(ICanvas canvas, RectF rect)
    {
        var foamHeight = rect.Height * 0.06f;

        canvas.FillColor = Foam.WithAlpha(0.92f);
        canvas.FillRectangle(0, 0, rect.Width, foamHeight);

        // Scalloped underside, so the head of the beer isn't a straight line.
        var bubbleCount = Math.Max(8, (int)(rect.Width / 46f));
        for (var i = 0; i <= bubbleCount; i++)
        {
            var x = i / (float)bubbleCount * rect.Width;
            var radius = 14f + MathF.Sin(i * 1.7f + (float)_elapsed * 0.4f) * 7f;
            canvas.FillCircle(x, foamHeight, radius);
        }
    }

    private void DrawState(ICanvas canvas, RectF rect)
    {
        var centre = new PointF(rect.Width / 2f, rect.Height / 2f);
        var scale = MathF.Min(rect.Width, rect.Height);

        switch (State)
        {
            case AiPanelistState.Thinking:
                DrawThinking(canvas, centre, scale);
                break;
            case AiPanelistState.Speaking:
                DrawSpeaking(canvas, centre, scale);
                break;
            default:
                // Idle and Listening show nothing, matching the mobile app where the
                // Lottie source is cleared. The beer alone carries it.
                break;
        }
    }

    /// <summary>
    /// Orbiting dots inside a slowly breathing ring.
    /// </summary>
    private void DrawThinking(ICanvas canvas, PointF centre, float scale)
    {
        var radius = scale * 0.18f;
        var breathe = 1f + MathF.Sin((float)_elapsed * 1.6f) * 0.06f;

        canvas.StrokeColor = Ink.WithAlpha(0.35f);
        canvas.StrokeSize = scale * 0.012f;
        canvas.DrawCircle(centre.X, centre.Y, radius * breathe);

        const int dots = 8;
        for (var i = 0; i < dots; i++)
        {
            var angle = (float)_elapsed * 1.9f + i * MathF.Tau / dots;
            var x = centre.X + MathF.Cos(angle) * radius * breathe;
            var y = centre.Y + MathF.Sin(angle) * radius * breathe;

            // Trailing fade around the ring gives it a direction of travel.
            var alpha = 0.20f + 0.80f * (i / (float)dots);
            canvas.FillColor = Ink.WithAlpha(alpha);
            canvas.FillCircle(x, y, scale * 0.022f);
        }
    }

    /// <summary>
    /// The mouth, drawn by the shared <see cref="MouthMeter"/> so this display and the
    /// MAUI one cannot drift apart.
    /// </summary>
    private void DrawSpeaking(ICanvas canvas, PointF centre, float scale)
        => MouthMeter.Draw(canvas, centre, scale, Ink.WithAlpha(0.85f), Level, HasLevel, Bridging, _elapsed);

    private void DrawStatus(ICanvas canvas, RectF rect)
    {
        if (string.IsNullOrWhiteSpace(StatusMessage)) return;

        var height = rect.Height * 0.07f;
        canvas.FillColor = Colors.Black.WithAlpha(0.35f);
        canvas.FillRectangle(0, rect.Height - height, rect.Width, height);

        canvas.FontColor = Colors.White;
        canvas.FontSize = MathF.Max(12f, rect.Height * 0.022f);
        canvas.DrawString(
            StatusMessage,
            0, rect.Height - height, rect.Width, height,
            HorizontalAlignment.Center, VerticalAlignment.Center);
    }

    private struct Bubble
    {
        public float X;          // 0..1 across the width
        public float Y;          // pixels from the top
        public float Radius;
        public float Speed;      // pixels per second
        public float Phase;
        public float WobbleRate;
        public float Alpha;
    }
}

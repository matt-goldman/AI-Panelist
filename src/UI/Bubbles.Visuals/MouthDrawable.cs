using Microsoft.Maui.Graphics;

namespace Bubbles.Visuals;

/// <summary>
/// <see cref="MouthMeter"/> as an <see cref="IDrawable"/>, for anywhere a GraphicsView can
/// be dropped in — which is the MAUI display, where the rest of the scene is SkiaSharp and
/// Lottie and only the mouth needs to be driven by the audio.
///
/// Set the properties from the render loop, then invalidate.
/// </summary>
public sealed class MouthDrawable : IDrawable
{
    /// <summary>Loudness, 0 to 1.</summary>
    public float Level { get; set; }

    /// <summary>Whether <see cref="Level"/> is real. False falls back to a shaped animation.</summary>
    public bool HasLevel { get; set; }

    /// <summary>In the gap between two chunks, with more audio still to come.</summary>
    public bool Bridging { get; set; }

    /// <summary>Running time, for the movement that is not driven by the audio.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>
    /// Bar colour. Defaults to the dark roasted amber the GTK scene uses, which is the one
    /// that reads against the beer from across a room.
    /// </summary>
    public Color Color { get; set; } = Color.FromArgb("#6B3E00").WithAlpha(0.85f);

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        var centre = new PointF(dirtyRect.Center.X, dirtyRect.Center.Y);
        var scale = MathF.Min(dirtyRect.Width, dirtyRect.Height);

        MouthMeter.Draw(canvas, centre, scale, Color, Level, HasLevel, Bridging, ElapsedSeconds);
    }
}

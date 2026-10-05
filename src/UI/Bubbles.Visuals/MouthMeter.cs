using Microsoft.Maui.Graphics;

namespace Bubbles.Visuals;

/// <summary>
/// Bubbles' mouth: a symmetric bar meter driven by the loudness of the audio actually
/// being played.
///
/// Drawn with Microsoft.Maui.Graphics and nothing else, so one implementation serves both
/// displays. The MAUI app draws it in a GraphicsView; the GTK app, which has no SkiaSharp
/// handler and therefore no Lottie, draws it as part of its hand-drawn scene. Two copies
/// of this would drift apart, and the mouth is the thing the audience is looking at.
/// </summary>
public static class MouthMeter
{
    public const int Bars = 9;

    /// <summary>
    /// Draw the mouth centred on <paramref name="centre"/>.
    /// </summary>
    /// <param name="scale">
    /// The smaller of the two surface dimensions. Everything is sized from this, so the
    /// mouth is the same shape on a phone and on a stage screen.
    /// </param>
    /// <param name="level">Loudness, 0 (shut) to 1 (wide), already smoothed.</param>
    /// <param name="hasLevel">
    /// False when no envelope is arriving — an older API, or the feature switched off — in
    /// which case this falls back to bars shaped to look like speech. A display that goes
    /// still because the API is a version behind would look broken.
    /// </param>
    /// <param name="bridging">
    /// In the seam between two chunks. Audio is still coming, so the mouth breathes rather
    /// than closing, which makes a 300ms gap read as a pause instead of a fault.
    /// </param>
    /// <param name="elapsedSeconds">
    /// Running time, for the small amount of movement that is not driven by the audio.
    /// </param>
    public static void Draw(
        ICanvas canvas,
        PointF centre,
        float scale,
        Color color,
        float level,
        bool hasLevel,
        bool bridging,
        double elapsedSeconds)
    {
        var barWidth = scale * 0.028f;
        var gap = barWidth * 0.9f;
        var totalWidth = Bars * barWidth + (Bars - 1) * gap;
        var left = centre.X - totalWidth / 2f;
        var maxHeight = scale * 0.30f;

        canvas.FillColor = color;

        // A touch of breathing in the seams only. During speech the audio supplies all the
        // movement, and adding more on top makes the mouth look unrelated to the sound.
        var amplitude = bridging
            ? level * (0.88f + 0.12f * MathF.Sin((float)elapsedSeconds * 4.2f))
            : level;

        for (var i = 0; i < Bars; i++)
        {
            // Two detuned sines per bar so the pattern doesn't visibly repeat.
            var t = (float)elapsedSeconds * 6f + i * 0.7f;
            var wobble = 0.35f + 0.65f * MathF.Abs(MathF.Sin(t) * 0.6f + MathF.Sin(t * 0.37f) * 0.4f);

            // Taller in the middle, like a mouth.
            var falloff = 1f - MathF.Abs(i - (Bars - 1) / 2f) / Bars;
            var shape = 0.45f + falloff * 0.55f;

            float height;
            if (hasLevel)
            {
                // Most of the height is the measured level; the rest is a small per-bar
                // variation that stops it reading as one rising block.
                height = maxHeight * amplitude * shape * (0.78f + 0.22f * wobble);

                // Never quite shut while there is sound, or consonants read as dropouts.
                if (amplitude > 0.02f)
                {
                    height = MathF.Max(height, barWidth * 0.9f);
                }
            }
            else
            {
                height = maxHeight * wobble * shape;
            }

            var x = left + i * (barWidth + gap);
            canvas.FillRoundedRectangle(x, centre.Y - height / 2f, barWidth, height, barWidth / 2f);
        }
    }
}

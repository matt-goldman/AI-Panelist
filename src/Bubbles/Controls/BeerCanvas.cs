using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace Bubbles.Controls;

internal class BeerCanvas : SKCanvasView
{
    private SKColor _gradientStart;
    private SKColor _gradientEnd;

    public BeerCanvas()
    {
        EnableTouchEvents = false;
        PaintSurface += OnPaintSurface;

        // Get Amber400 and Yellow300 static resource colors from the app resources
        var amber400 = (Color)Application.Current!.Resources["Amber400pc15"];
        var yellow300 = (Color)Application.Current!.Resources["Yellow300pc15"];

        _gradientStart = amber400.ToSKColor();
        _gradientEnd = yellow300.ToSKColor();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var info = e.Info;
        canvas.Clear();

        // Gray muted overlay
        using var grayPaint = new SKPaint { Color = new SKColor(128, 128, 128, 38) }; // ~15% opacity
        var grayRect = new SKRect(0, 0, info.Width, info.Height);
        canvas.DrawRect(grayRect, grayPaint);


        // Beer gradient overlay
        var beerRect = new SKRect(0, 0, info.Width, info.Height);
        using var beerPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, beerRect.Top),
                new SKPoint(0, beerRect.Bottom),
                [_gradientStart, _gradientEnd],
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(beerRect, beerPaint);
    }
}
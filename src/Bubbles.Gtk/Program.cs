using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;

namespace Bubbles.Gtk;

/// <summary>
/// Entry point for the GTK4 build. Unlike the mobile Bubbles app there is no platform
/// head project - the GTK backend runs the app as an ordinary .NET executable.
/// </summary>
public class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public static void Main(string[] args)
    {
        var app = new Program();
        app.Run(args);
    }
}

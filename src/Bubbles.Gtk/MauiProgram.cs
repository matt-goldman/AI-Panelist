using Bubbles.Gtk.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;

namespace Bubbles.Gtk;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp
            .CreateBuilder()
            .UseMauiAppLinuxGtk4<App>();

        builder.Services.AddSingleton<BubblesConnection>();
        builder.Services.AddSingleton<MainPage>();

        builder.Logging.AddConsole();

        return builder.Build();
    }
}

using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using UI_Common;

namespace Bubbles;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
#pragma warning disable MCT001 // `.UseMauiCommunityToolkit()` added by .AddUICommon()
        builder
            .UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			})
			.UseSkiaSharp()
			.AddUICommon();
#pragma warning restore MCT001 // `.UseMauiCommunityToolkit()` Not Found on MauiAppBuilder

		builder.Services.AddTransient<MainPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}

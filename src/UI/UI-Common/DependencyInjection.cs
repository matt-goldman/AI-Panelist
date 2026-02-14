using CommunityToolkit.Maui;
using UI_Common.Services;

namespace UI_Common;

public static class DependencyInjection
{
    public static MauiAppBuilder AddUICommon(this MauiAppBuilder builder)
    {
        builder.UseMauiCommunityToolkit();
        builder.Services.AddSingleton<ConversationStateService>();

        return builder;
    }
}

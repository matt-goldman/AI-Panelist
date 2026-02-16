using CommunityToolkit.Maui.Extensions;
using UI_Common.Popups;

namespace UI_Common.Services;

public static class ApiConfigService
{
    public static async Task<string> GetApiAddress(bool? forceUiPrompt = null)
    {
        var apiIpAddress = Preferences.Get("API", "notset");
        if (apiIpAddress == "notset" || forceUiPrompt == true)
        {
            apiIpAddress = await PromptUserForUpAddresss();
        }
        return apiIpAddress;
    }

    private static async Task<string> PromptUserForUpAddresss()
    {
        var currentPage = (Application.Current?.Windows[0].Page) ?? throw new Exception("FML");

        var popup = new IPAddressPopup();

        var result = await currentPage.ShowPopupAsync<string>(popup);

        return result.Result ?? throw new Exception("FML");
    }
}

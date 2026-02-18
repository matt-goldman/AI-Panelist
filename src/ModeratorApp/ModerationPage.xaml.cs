using UI_Common.Services;

namespace ModeratorApp;

public partial class ModerationPage : ContentPage
{
    private readonly ConversationStateService service;

    public ModerationPage(ConversationStateService service)
	{
		InitializeComponent();
        this.service = service;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await service.Init();
    }

    private void ListenBtn_Clicked(object sender, EventArgs e)
    {
        service.SetPanelistState(Shared.AiPanelistState.Listening);
    }

    private void ThinkingButton_Clicked(object sender, EventArgs e)
    {
        service.SetPanelistState(Shared.AiPanelistState.Thinking);
    }

    private void SpeakingButton_Clicked(object sender, EventArgs e)
    {
        service.SetPanelistState(Shared.AiPanelistState.Speaking);
    }

    private void IdleButton_Clicked(object sender, EventArgs e)
    {
        service.SetPanelistState(Shared.AiPanelistState.Idle);
    }

    private async void CustomStateButton_Clicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CustomStateEntry.Text))
        {
            await DisplayAlertAsync("Error", "Please enter a custom state name.", "OK");
            return;
        }

        await service.TestCustomState(CustomStateEntry.Text);
    }
}

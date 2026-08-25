namespace Bubbles.Gtk;

public class App : Application
{
    private readonly MainPage _page;

    public App(MainPage page) => _page = page;

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(_page)
        {
            Title = "Bubbles"
        };
}

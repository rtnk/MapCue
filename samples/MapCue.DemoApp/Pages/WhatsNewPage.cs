namespace MapCue.DemoApp.Pages;

/// <summary>Screen 8: a modal page with a single safe way out ("Close" or the system back).</summary>
public sealed class WhatsNewPage : ContentPage
{
    public WhatsNewPage()
    {
        Title = "What's new";
        Content = Ui.Stack(
            Ui.Title("What's new", AutomationIds.ScreenWhatsNew),
            Ui.Text("Version 0.1.0 of the demo app."),
            Ui.Button("Close", AutomationIds.WhatsNewClose, () => Navigation.PopModalAsync()));
    }
}

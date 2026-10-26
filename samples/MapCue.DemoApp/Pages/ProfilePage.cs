namespace MapCue.DemoApp.Pages;

/// <summary>Screen 1: test profile picker. Setup flows choose the profile here; there is no login and no backend.</summary>
public sealed class ProfilePage : ContentPage
{
    public ProfilePage()
    {
        Title = "Choose a test profile";
        var empty = new Switch { AutomationId = AutomationIds.ScenarioEmpty, IsToggled = false };

        Content = new ScrollView
        {
            Content = Ui.Stack(
                Ui.Title("Choose a test profile", AutomationIds.ScreenProfile),
                Ui.Text("Demo data only. Nothing leaves this device."),
                new HorizontalStackLayout { Spacing = 12, Children = { Ui.Text("Empty accounts scenario"), empty } },
                Ui.Button("Basic", AutomationIds.ProfileBasic, () => Start(DemoProfile.Basic, empty.IsToggled)),
                Ui.Button("Investor", AutomationIds.ProfileInvestor, () => Start(DemoProfile.Investor, empty.IsToggled))),
        };
    }

    private void Start(DemoProfile profile, bool emptyAccounts)
    {
        if (Window is { } window)
        {
            window.Page = new LoadingPage(DemoSession.Start(profile, emptyAccounts));
        }
    }
}

namespace MapCue.DemoApp.Pages;

/// <summary>
/// Screen 9: the deliberate unknown popup (a blocker for exploration). It is not announced anywhere, ignores the system
/// back and offers only risky actions: a communication action and an account change. Nothing is sent and no account changes.
/// </summary>
public sealed class VerifyPage : ContentPage
{
    public VerifyPage()
    {
        Title = "Verify your identity";
        var status = Ui.Text(string.Empty, AutomationIds.VerifyStatus);
        Content = Ui.Stack(
            Ui.Title("Verify your identity", AutomationIds.ScreenVerify),
            Ui.Text("Unusual activity. Confirm it is you to continue."),
            Ui.Button("Send code", AutomationIds.VerifySendCode, () => status.Text = "Demo: no code was sent."),
            Ui.Button("Sign out", AutomationIds.VerifySignOut, async () =>
            {
                var window = Window;
                await Navigation.PopModalAsync();
                if (window is not null)
                {
                    window.Page = new ProfilePage();
                }
            }),
            status);
#if IOS
        // Full screen, so the iOS sheet cannot be swiped away either.
        Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.Page.SetModalPresentationStyle(
            this.On<Microsoft.Maui.Controls.PlatformConfiguration.iOS>(),
            Microsoft.Maui.Controls.PlatformConfiguration.iOSSpecific.UIModalPresentationStyle.FullScreen);
#endif
    }

    protected override bool OnBackButtonPressed() => true;
}

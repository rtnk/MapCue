namespace MapCue.DemoApp.Pages;

/// <summary>Screen 7: account details. "Home" closes the stack and selects the Home tab (cycle Details → Home).</summary>
public sealed class DetailsPage : ContentPage
{
    public DetailsPage(DemoSession session, DemoAccount account, MainPage main)
    {
        Title = "Account details";
        Content = new ScrollView
        {
            Content = Ui.Stack(
                Ui.Title("Account details", AutomationIds.ScreenDetails),
                Ui.Text(account.Name, AutomationIds.DetailsName),
                Ui.Text(account.MaskedNumber),
                Ui.Text(account.Balance, AutomationIds.DetailsBalance),
                Ui.Button("Home", AutomationIds.DetailsHome, async () =>
                {
                    main.Select(DemoTab.Home);
                    await Navigation.PopToRootAsync();
                }),
                // Destructive action for the risk policy test. It asks first and only changes the in-memory list.
                Ui.Button("Delete account", AutomationIds.DetailsDelete, async () =>
                {
                    if (await DisplayAlertAsync("Delete this account?", "The account is removed until the app restarts.", "Delete", "Cancel"))
                    {
                        session.Accounts.Remove(account);
                        main.Select(DemoTab.Accounts);
                        await Navigation.PopAsync();
                    }
                })),
        };
    }
}

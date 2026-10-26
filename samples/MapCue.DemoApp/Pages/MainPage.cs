using System.Collections.ObjectModel;

namespace MapCue.DemoApp.Pages;

public enum DemoTab
{
    Home,
    Accounts,
    Investments,
    Settings,
}

/// <summary>
/// Screens 3–6: Home with a tab bar (Home, Accounts, Investments for the investor profile only, Settings).
/// A tab swaps the content in place; the Investments tab is not in the view tree at all for the basic profile.
/// </summary>
public sealed class MainPage : ContentPage
{
    private readonly DemoSession _session;
    private readonly ContentView _host = new();
    private readonly Dictionary<DemoTab, Button> _tabs = new();

    public MainPage(DemoSession session)
    {
        _session = session;
        Title = "MapCue Demo";
        NavigationPage.SetHasNavigationBar(this, false);

        var bar = new Grid { Padding = new Thickness(8), ColumnSpacing = 4 };
        AddTab(bar, DemoTab.Home, "Home", AutomationIds.TabHome);
        AddTab(bar, DemoTab.Accounts, "Accounts", AutomationIds.TabAccounts);
        if (session.HasInvestments)
        {
            AddTab(bar, DemoTab.Investments, "Investments", AutomationIds.TabInvestments);
        }

        AddTab(bar, DemoTab.Settings, "Settings", AutomationIds.TabSettings);

        var root = new Grid { RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) } };
        root.Add(_host, 0, 0);
        root.Add(bar, 0, 1);
        Content = root;
        Select(DemoTab.Home);
    }

    public void Select(DemoTab tab)
    {
        if (tab == DemoTab.Investments && !_session.HasInvestments)
        {
            tab = DemoTab.Home;
        }

        _host.Content = tab switch
        {
            DemoTab.Accounts => AccountsView(),
            DemoTab.Investments => InvestmentsView(),
            DemoTab.Settings => SettingsView(),
            _ => HomeView(),
        };
        foreach (var (key, button) in _tabs)
        {
            button.FontAttributes = key == tab ? FontAttributes.Bold : FontAttributes.None;
        }
    }

    private void AddTab(Grid bar, DemoTab tab, string text, string automationId)
    {
        var column = bar.ColumnDefinitions.Count;
        bar.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        var button = Ui.Button(text, automationId, () => Select(tab));
        button.Padding = new Thickness(2);
        _tabs[tab] = button;
        bar.Add(button, column, 0);
    }

    private ScrollView HomeView() => new()
    {
        Content = Ui.Stack(
            Ui.Title("Home", AutomationIds.ScreenHome),
            Ui.Text(_session.Profile == DemoProfile.Investor ? "Profile: investor" : "Profile: basic"),
            Ui.Button("View accounts", AutomationIds.HomeAccounts, () => Select(DemoTab.Accounts)),
            Ui.Button("What's new", AutomationIds.HomeWhatsNew, () => Navigation.PushModalAsync(new WhatsNewPage()))),
    };

    private Grid AccountsView()
    {
        var empty = Ui.Text("No accounts yet", AutomationIds.AccountsEmpty);
        empty.IsVisible = _session.Accounts.Count == 0;
        var list = new CollectionView
        {
            AutomationId = AutomationIds.AccountsList,
            ItemsSource = _session.Accounts,
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var row = new Button { Margin = new Thickness(0, 4) };
                row.SetBinding(Button.TextProperty, static (DemoAccount a) => a.RowText);
                row.Clicked += async (sender, _) =>
                {
                    if (((Button)sender!).BindingContext is DemoAccount account)
                    {
                        await Navigation.PushAsync(new DetailsPage(_session, account, this));
                    }
                };
                return row;
            }),
        };

        var grid = new Grid
        {
            Padding = new Thickness(20, 20, 20, 0),
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
        };
        grid.Add(Ui.Title("Accounts", AutomationIds.ScreenAccounts), 0, 0);
        grid.Add(empty, 0, 1);
        grid.Add(list, 0, 2);
        return grid;
    }

    private Grid InvestmentsView()
    {
        var list = new CollectionView
        {
            AutomationId = AutomationIds.InvestmentsList,
            ItemsSource = new ObservableCollection<DemoHolding>(DemoData.Holdings),
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var row = new Label { FontSize = 16, Margin = new Thickness(0, 6) };
                row.SetBinding(Label.TextProperty, static (DemoHolding h) => h.RowText);
                return row;
            }),
        };

        var grid = new Grid
        {
            Padding = new Thickness(20, 20, 20, 0),
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
        };
        grid.Add(Ui.Title("Investments", AutomationIds.ScreenInvestments), 0, 0);
        // Financial action for the risk policy test: it only shows a notice, nothing is bought.
        grid.Add(Ui.Button("Buy units", AutomationIds.InvestmentsBuy, () => DisplayAlertAsync("Demo", "Nothing was bought. This is a demo app.", "OK")), 0, 1);
        grid.Add(list, 0, 2);
        return grid;
    }

    private ScrollView SettingsView()
    {
        var notifications = new Switch { AutomationId = AutomationIds.SettingsNotifications, IsToggled = true };
        return new ScrollView
        {
            Content = Ui.Stack(
                Ui.Title("Settings", AutomationIds.ScreenSettings),
                new HorizontalStackLayout { Spacing = 12, Children = { Ui.Text("Notifications"), notifications } },
                // Deliberate blocker: instead of an update check the app shows an unexpected verification popup.
                Ui.Button("Check for updates", AutomationIds.SettingsCheckUpdates, () => Navigation.PushModalAsync(new VerifyPage())),
                Ui.Button("Switch profile", AutomationIds.SettingsSwitchProfile, () =>
                {
                    if (Window is { } window)
                    {
                        window.Page = new ProfilePage();
                    }
                })),
        };
    }
}

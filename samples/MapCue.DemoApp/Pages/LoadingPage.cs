namespace MapCue.DemoApp.Pages;

/// <summary>Screen 2: loading state with a fixed delay (<see cref="DemoData.LoadingDelay"/>), then Home.</summary>
public sealed class LoadingPage : ContentPage
{
    private readonly DemoSession _session;
    private bool _started;

    public LoadingPage(DemoSession session)
    {
        _session = session;
        Title = "Loading";
        Content = Ui.Stack(
            Ui.Title("Loading your accounts", AutomationIds.ScreenLoading),
            new ActivityIndicator { IsRunning = true, AutomationId = AutomationIds.LoadingIndicator });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_started)
        {
            return;
        }

        _started = true;
        await Task.Delay(DemoData.LoadingDelay);
        if (Window is { } window)
        {
            window.Page = new NavigationPage(new MainPage(_session));
        }
    }
}

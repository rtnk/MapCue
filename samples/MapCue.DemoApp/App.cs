using MapCue.DemoApp.Pages;

namespace MapCue.DemoApp;

public sealed class App : Application
{
    // Every launch starts at the profile picker; nothing is persisted between launches.
    protected override Window CreateWindow(IActivationState? activationState) => new(new ProfilePage()) { Title = "MapCue Demo" };
}

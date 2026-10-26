namespace MapCue.Testing;

/// <summary>
/// <see cref="FakeAppModel"/> of <c>samples/MapCue.DemoApp</c> as TapCue sees it on Android (expected-graph.md): MAUI
/// <c>AutomationId</c> as <c>com.mapcue.demoapp:id/&lt;id&gt;</c>, the MAUI layout containers on every screen, tabs that swap the
/// content in place (back on a root screen leaves the app to the launcher), account rows without ids, the What's new modal page,
/// the delete confirmation <c>AlertDialog</c> (<c>android:id/*</c>), the 1500 ms loading screen and the Verify popup that ignores back.
/// For the tests it adds one safe <c>AlertDialog</c> (Settings → About, not in the real app) so a <c>modal</c> state is reachable under
/// the default policy. The basic profile is set up by <see cref="FakeAppModel.SetupScreenId"/> (Home), like <c>setup-basic.yaml</c>.
/// </summary>
public static class FakeDemoApp
{
    public const string AppId = "com.mapcue.demoapp";
    public const string Launcher = "com.google.android.apps.nexuslauncher";

    public static string Id(string automationId) => $"{AppId}:id/{automationId}";

    public static FakeAppModel Basic(bool withAboutDialog = true)
    {
        static FakeElement Title(string automationId, string text) =>
            new(Id(automationId), text) { Clickable = false, ClassName = "android.widget.TextView" };

        static FakeElement Container(string id) =>
            new(id, string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" };

        FakeElement[] Maui() =>
        [
            Container("android:id/content"),
            Container(Id("action_bar_root")),
            Container(Id("navigation_layout")),
            Container(Id("navigationlayout_content")),
            Container(Id("nav_host")),
        ];

        FakeElement[] Tabs() =>
        [
            new(Id("tab.home"), "Home", "home") { Navigation = FakeNavigation.Replace },
            new(Id("tab.accounts"), "Accounts", "accounts") { Navigation = FakeNavigation.Replace },
            new(Id("tab.settings"), "Settings", "settings") { Navigation = FakeNavigation.Replace },
        ];

        var settings = new List<FakeElement>
        {
            Title("screen.settings", "Settings"),
            new(Id("settings.notifications"), string.Empty) { ClassName = "android.widget.Switch" },
            new(Id("settings.checkupdates"), "Check for updates", "verify"),
            new(Id("settings.switchprofile"), "Switch profile", "profile") { Navigation = FakeNavigation.Root },
        };
        if (withAboutDialog)
        {
            settings.Add(new(Id("settings.about"), "About", "about"));
        }

        return new FakeAppModel(
            AppId,
            "profile",
            [
                new("profile", [.. Maui(), Title("screen.profile", "Choose a test profile"), new(Id("scenario.empty"), string.Empty) { ClassName = "android.widget.Switch" }, new(Id("profile.basic"), "Basic", "loading") { Navigation = FakeNavigation.Root }, new(Id("profile.investor"), "Investor", "loading") { Navigation = FakeNavigation.Root }]),
                new("loading", [.. Maui(), Title("screen.loading", "Loading"), new(Id("loading.indicator"), string.Empty) { Clickable = false, ClassName = "android.widget.ProgressBar" }]) { AdvanceAfterObservations = 2, AdvanceTo = "home" },
                new("home", [.. Maui(), Title("screen.home", "Home"), new FakeElement(string.Empty, "Profile: basic") { Clickable = false, ClassName = "android.widget.TextView" }, new(Id("home.accounts"), "View accounts", "accounts") { Navigation = FakeNavigation.Replace }, new(Id("home.whatsnew"), "What's new", "whatsnew"), .. Tabs()]),
                new("accounts", [.. Maui(), Title("screen.accounts", "Accounts"), new(string.Empty, "Everyday account · PLN 1,250.00", "details"), new(string.Empty, "Savings account · PLN 8,400.50", "details"), .. Tabs()]),
                new("details", [.. Maui(), Title("screen.details", "Account details"), new(string.Empty, string.Empty) { AccessibilityText = "Navigate up", ClassName = "android.widget.ImageButton", Navigation = FakeNavigation.Pop }, new(Id("details.home"), "Home", "home") { Navigation = FakeNavigation.Root }, new(Id("details.delete"), "Delete account", "confirm")]),
                new("confirm", [new(string.Empty, string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" }, new("android:id/alertTitle", "Delete this account?") { Clickable = false, ClassName = "android.widget.TextView" }, new("android:id/button1", "Delete", "accounts") { Navigation = FakeNavigation.Root }, new("android:id/button2", "Cancel") { Navigation = FakeNavigation.Pop }]),
                new("whatsnew", [.. Maui(), Title("screen.whatsnew", "What's new"), new(Id("whatsnew.close"), "Close") { Navigation = FakeNavigation.Pop }]),
                new("settings", [.. Maui(), .. settings, .. Tabs()]),
                new("verify", [.. Maui(), Title("screen.verify", "Verify your identity"), new(Id("verify.sendcode"), "Send code"), new(Id("verify.signout"), "Sign out", "profile") { Navigation = FakeNavigation.Root }]) { IgnoresBack = true },
                new("about", [new("android:id/alertTitle", "About") { Clickable = false, ClassName = "android.widget.TextView" }, new("android:id/button1", "OK") { Navigation = FakeNavigation.Pop }]),
                new("launcher", [new($"{Launcher}:id/workspace", string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" }, new($"{Launcher}:id/icon", "MapCue Demo") { ClassName = "android.widget.TextView" }]) { Package = Launcher },
            ])
        {
            LauncherScreenId = "launcher",
            SetupScreenId = "home",
        };
    }
}

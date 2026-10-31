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

/// <summary>
/// <see cref="FakeAppModel"/> of the demo app with both test profiles (expected-graph.md, "Profile i scenariusze"): the profile picker leads
/// to the <c>basic</c> or the <c>investor</c> app (screen ids prefixed with the profile). <c>investor</c> has the <c>tab.investments</c>
/// tab, the Investments screen (<c>investments.buy</c> opens the "Demo" <c>AlertDialog</c>) and three accounts; everything else is shared.
/// <c>setup-basic.yaml</c> and <c>setup-investor.yaml</c> land on the Home of their profile, like the real setup flows. There is no Settings
/// → About dialog here (the real app has none).
/// </summary>
public static class FakeDemoProfiles
{
    public static string Id(string automationId) => FakeDemoApp.Id(automationId);

    public static FakeAppModel Create()
    {
        var screens = new List<FakeScreen>
        {
            new("profile", [.. Maui(), Title("screen.profile", "Choose a test profile"), new(Id("scenario.empty"), string.Empty) { ClassName = "android.widget.Switch" }, new(Id("profile.basic"), "Basic", "basic.loading") { Navigation = FakeNavigation.Root }, new(Id("profile.investor"), "Investor", "investor.loading") { Navigation = FakeNavigation.Root }]),
            new("launcher", [new($"{FakeDemoApp.Launcher}:id/workspace", string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" }, new($"{FakeDemoApp.Launcher}:id/icon", "MapCue Demo") { ClassName = "android.widget.TextView" }]) { Package = FakeDemoApp.Launcher },
        };
        screens.AddRange(Profile("basic", investor: false));
        screens.AddRange(Profile("investor", investor: true));
        return new FakeAppModel(FakeDemoApp.AppId, "profile", screens)
        {
            LauncherScreenId = "launcher",
            SetupScreenId = "basic.home",
            SetupScreens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["setup-basic.yaml"] = "basic.home",
                ["setup-investor.yaml"] = "investor.home",
            },
        };
    }

    private static FakeElement Title(string automationId, string text) =>
        new(Id(automationId), text) { Clickable = false, ClassName = "android.widget.TextView" };

    private static FakeElement Container(string id) =>
        new(id, string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" };

    private static FakeElement[] Maui() =>
    [
        Container("android:id/content"),
        Container(Id("action_bar_root")),
        Container(Id("navigation_layout")),
        Container(Id("navigationlayout_content")),
        Container(Id("nav_host")),
    ];

    private static IEnumerable<FakeScreen> Profile(string p, bool investor)
    {
        string S(string id) => $"{p}.{id}";
        FakeElement[] Tabs() =>
        [
            new(Id("tab.home"), "Home", S("home")) { Navigation = FakeNavigation.Replace },
            new(Id("tab.accounts"), "Accounts", S("accounts")) { Navigation = FakeNavigation.Replace },
            .. investor ? new FakeElement[] { new(Id("tab.investments"), "Investments", S("investments")) { Navigation = FakeNavigation.Replace } } : [],
            new(Id("tab.settings"), "Settings", S("settings")) { Navigation = FakeNavigation.Replace },
        ];
        // The accounts list is a RecyclerView (MAUI CollectionView, accounts.list) whose rows fit, so it is not scrollable: two rows (basic)
        // and three (investor) in a container that does not scroll (seen on the emulator, STEP-07: fingerprint version 2 made only the
        // three rows a list, so the investor Accounts tab was another state).
        FakeElement Row(string text) => new(string.Empty, text, S("details")) { Depth = 1 };
        var rows = new List<FakeElement> { Row("Everyday account · PLN 1,250.00"), Row("Savings account · PLN 8,400.50") };
        if (investor)
        {
            rows.Add(Row("Brokerage account · PLN 52,310.75"));
        }

        rows.Insert(0, new(Id("accounts.list"), string.Empty) { Clickable = false, ClassName = "androidx.recyclerview.widget.RecyclerView" });

        yield return new(S("loading"), [.. Maui(), Title("screen.loading", "Loading"), new(Id("loading.indicator"), string.Empty) { Clickable = false, ClassName = "android.widget.ProgressBar" }]) { AdvanceAfterObservations = 2, AdvanceTo = S("home") };
        yield return new(S("home"), [.. Maui(), Title("screen.home", "Home"), new(string.Empty, $"Profile: {p}") { Clickable = false, ClassName = "android.widget.TextView" }, new(Id("home.accounts"), "View accounts", S("accounts")) { Navigation = FakeNavigation.Replace }, new(Id("home.whatsnew"), "What's new", S("whatsnew")), .. Tabs()]);
        yield return new(S("accounts"), [.. Maui(), Title("screen.accounts", "Accounts"), .. rows, .. Tabs()]);
        yield return new(S("details"), [.. Maui(), Title("screen.details", "Account details"), new(string.Empty, string.Empty) { AccessibilityText = "Navigate up", ClassName = "android.widget.ImageButton", Navigation = FakeNavigation.Pop }, new(Id("details.home"), "Home", S("home")) { Navigation = FakeNavigation.Root }, new(Id("details.delete"), "Delete account", S("confirm"))]);
        yield return new(S("confirm"), [new(string.Empty, string.Empty) { Clickable = false, ClassName = "android.widget.FrameLayout" }, new("android:id/alertTitle", "Delete this account?") { Clickable = false, ClassName = "android.widget.TextView" }, new("android:id/button1", "Delete", S("accounts")) { Navigation = FakeNavigation.Root }, new("android:id/button2", "Cancel") { Navigation = FakeNavigation.Pop }]);
        yield return new(S("whatsnew"), [.. Maui(), Title("screen.whatsnew", "What's new"), new(Id("whatsnew.close"), "Close") { Navigation = FakeNavigation.Pop }]);
        yield return new(S("settings"), [.. Maui(), Title("screen.settings", "Settings"), new(Id("settings.notifications"), string.Empty) { ClassName = "android.widget.Switch" }, new(Id("settings.checkupdates"), "Check for updates", S("verify")), new(Id("settings.switchprofile"), "Switch profile", "profile") { Navigation = FakeNavigation.Root }, .. Tabs()]);
        yield return new(S("verify"), [.. Maui(), Title("screen.verify", "Verify your identity"), new(Id("verify.sendcode"), "Send code"), new(Id("verify.signout"), "Sign out", "profile") { Navigation = FakeNavigation.Root }]) { IgnoresBack = true };
        if (investor)
        {
            yield return new(S("investments"), [.. Maui(), Title("screen.investments", "Investments"), new(Id("investments.buy"), "Buy units", S("purchase")), new(string.Empty, "Global equity fund · PLN 10,000.00") { Clickable = false, ClassName = "android.widget.TextView" }, new(string.Empty, "Bond fund · PLN 5,000.00") { Clickable = false, ClassName = "android.widget.TextView" }, .. Tabs()]);
            yield return new(S("purchase"), [new("android:id/alertTitle", "Demo") { Clickable = false, ClassName = "android.widget.TextView" }, new("android:id/button1", "OK") { Navigation = FakeNavigation.Pop }]);
        }
    }
}

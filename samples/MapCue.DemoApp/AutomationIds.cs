namespace MapCue.DemoApp;

/// <summary>
/// Stable automation ids of the demo app. They are the contract with the TapCue flows (<c>.tapcue/</c>) and the oracle
/// (<c>expected-graph.md</c>); <c>check-consistency.py</c> fails when the three drift apart.
/// On Android MAUI writes <c>AutomationId</c> to the view's content description, so TapCue matches it with a text selector
/// and MapCue sees it as the element label. Ids contain no digits, so MapCue's text masking keeps them intact.
/// </summary>
public static class AutomationIds
{
    // Screen markers (title labels).
    public const string ScreenProfile = "screen.profile";
    public const string ScreenLoading = "screen.loading";
    public const string ScreenHome = "screen.home";
    public const string ScreenAccounts = "screen.accounts";
    public const string ScreenDetails = "screen.details";
    public const string ScreenInvestments = "screen.investments";
    public const string ScreenSettings = "screen.settings";
    public const string ScreenWhatsNew = "screen.whatsnew";
    public const string ScreenVerify = "screen.verify";

    // Profile picker.
    public const string ProfileBasic = "profile.basic";
    public const string ProfileInvestor = "profile.investor";
    public const string ScenarioEmpty = "scenario.empty";

    // Loading.
    public const string LoadingIndicator = "loading.indicator";

    // Tab bar.
    public const string TabHome = "tab.home";
    public const string TabAccounts = "tab.accounts";
    public const string TabInvestments = "tab.investments";
    public const string TabSettings = "tab.settings";

    // Home.
    public const string HomeAccounts = "home.accounts";
    public const string HomeWhatsNew = "home.whatsnew";

    // What's new (modal page).
    public const string WhatsNewClose = "whatsnew.close";

    // Accounts.
    public const string AccountsList = "accounts.list";
    public const string AccountsEmpty = "accounts.empty";

    // Account details.
    public const string DetailsName = "details.name";
    public const string DetailsBalance = "details.balance";
    public const string DetailsHome = "details.home";
    public const string DetailsDelete = "details.delete";

    // Investments (investor profile only).
    public const string InvestmentsList = "investments.list";
    public const string InvestmentsBuy = "investments.buy";

    // Settings.
    public const string SettingsNotifications = "settings.notifications";
    public const string SettingsCheckUpdates = "settings.checkupdates";
    public const string SettingsSwitchProfile = "settings.switchprofile";

    // Unknown popup (deliberate blocker).
    public const string VerifySendCode = "verify.sendcode";
    public const string VerifySignOut = "verify.signout";
    public const string VerifyStatus = "verify.status";
}

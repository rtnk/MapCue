using System.Text.RegularExpressions;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Application.Exploration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.ScreenContracts;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>
/// Calibration of normalization and identity on the STEP-02B emulator recordings of the MAUI demo app (R-04,
/// samples/MapCue.DemoApp/recordings: Home after setup-basic and setup-investor, TapCue 0.1.0 <c>hierarchy</c>, API 34).
/// </summary>
public class DemoAppRecordingCalibrationTests
{
    private const string App = "com.mapcue.demoapp";

    private static readonly NormalizationOptions _android = new(Platform.Android);

    private static IReadOnlyList<RawElement> Recording(string name) =>
        TapCueParsers.ParseHierarchy(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DemoRecordings", name + ".hierarchy.json")), out _);

    private static IReadOnlyList<RawElement> Basic => Recording("home-basic");

    private static IReadOnlyList<RawElement> Investor => Recording("home-investor");

    private static NormalizedSnapshot Normalize(IReadOnlyList<RawElement> elements) => SnapshotNormalizer.Normalize(elements, _android);

    private static string Id(string automationId) => $"{App}:id/{automationId}";

    /// <summary>The Accounts tab as it differs from Home in expected-graph.md: other title, no Home buttons, same tab bar and containers.</summary>
    private static IReadOnlyList<RawElement> AccountsLike() =>
        UiTrees.Flatten([.. UiTrees.Map(UiTrees.FromPreOrder(Basic), e => e.ResourceId switch
        {
            var id when id == Id("screen.home") => e with { ResourceId = Id("screen.accounts"), Text = "Accounts" },
            var id when id == Id("home.accounts") || id == Id("home.whatsnew") => e with { ResourceId = null, Text = null, Clickable = false, ClassName = "android.view.View" },
            _ => e,
        })]);

    private static KnownState Known(string id, NormalizedSnapshot snapshot) => new(id, "scr_" + id, snapshot.IdentityHash, snapshot.FingerprintVersion, snapshot.IdentityTokens);

    [Fact]
    public void The_full_hierarchy_carries_bounds_and_important_for_accessibility_for_contracts()
    {
        // STEP-08: the CLI hierarchy reports important-for-accessibility (the fast MCP snapshot does not, G-02); normalization keeps it with
        // the bounds on the elements (never in a hash: the pinned identity of the recording is unchanged).
        Assert.All(Basic.Where(e => e.ClassName is not null), e => Assert.NotNull(e.ImportantForAccessibility));
        Assert.Contains(Basic, e => e.ImportantForAccessibility == false);
        var home = Normalize(Basic);
        var targets = KeyboardTargets.Of(home);
        Assert.Contains("id:" + Id("home.accounts"), targets);
        Assert.Contains("id:" + Id("tab.settings"), targets);
        Assert.DoesNotContain("id:" + Id("screen.home"), targets);
        Assert.All(home.Elements.Where(KeyboardTargets.IsTarget), e => Assert.Matches(@"^\[\d+,\d+\]\[\d+,\d+\]$", e.Bounds!));
        Assert.All(home.Elements.Where(KeyboardTargets.IsTarget), e => Assert.True(e.ImportantForAccessibility));

        // Predicted order (unverified): the Home buttons come before the tab bar at the bottom of the screen.
        var order = ScreenContractBuilder.PredictedOrder(home);
        Assert.Equal(targets.Count, order.Count);
        Assert.True(order.ToList().IndexOf("id:" + Id("home.accounts")) < order.ToList().IndexOf("id:" + Id("tab.home")));
    }

    [Fact]
    public void Maui_automation_ids_are_resource_id_keys_and_system_ui_is_removed()
    {
        var home = Normalize(Basic);

        Assert.Equal(StateKind.Default, home.StateKind);
        Assert.Equal(26, home.SystemUiElementsRemoved);
        Assert.Equal(0, home.WeakKeyCount);
        Assert.Equal(["android", App], home.ObservedPackages);
        Assert.Equal(
            [
                "a:id:" + Id("screen.home"),
                "i:id:" + Id("home.accounts"),
                "i:id:" + Id("home.whatsnew"),
                "i:id:" + Id("tab.accounts"),
                "i:id:" + Id("tab.home"),
                "i:id:" + Id("tab.settings"),
            ],
            home.IdentityTokens);
        Assert.All(home.Elements.Where(e => e.Interactive), e => Assert.Equal(ElementKeyStrength.ResourceId, e.KeyStrength));
    }

    [Fact]
    public void Maui_layout_containers_stay_in_the_element_set_but_are_not_identity_anchors()
    {
        var home = Normalize(Basic);

        foreach (var container in new[] { "android:id/content", Id("action_bar_root"), Id("navigation_layout"), Id("navigationlayout_content"), Id("nav_host") })
        {
            Assert.Contains(home.Elements, e => e.ResourceId == container);
            Assert.Contains(home.ElementSetKeys, k => k.StartsWith("id:" + container, StringComparison.Ordinal));
            Assert.DoesNotContain(home.IdentityTokens, t => t.EndsWith(container, StringComparison.Ordinal));
            Assert.True(FrameworkContainerRules.IsFrameworkContainer(Platform.Android, container));
        }

        Assert.False(FrameworkContainerRules.IsFrameworkContainer(Platform.Android, Id("screen.home")));
        Assert.False(FrameworkContainerRules.IsFrameworkContainer(Platform.Ios, "android:id/content"));
    }

    [Fact]
    public void Without_the_containers_a_tab_is_no_longer_mistaken_for_the_home_screen()
    {
        var home = Normalize(Basic);
        var accounts = Normalize(AccountsLike());
        string[] containers = ["a:id:android:id/content", "a:id:" + Id("action_bar_root"), "a:id:" + Id("nav_host"), "a:id:" + Id("nav_host"), "a:id:" + Id("navigation_layout"), "a:id:" + Id("navigationlayout_content")];

        // Fingerprint version 1 kept the six container anchors (nav_host twice): 9/13 = 0.69 ≥ τ_screen made Accounts a state of Home.
        var withContainers = ScreenIdentityResolver.Jaccard([.. home.IdentityTokens, .. containers], [.. accounts.IdentityTokens, .. containers]);
        Assert.True(withContainers >= IdentityResolverOptions.Default.ScreenThreshold, $"v1 similarity {withContainers}");

        var resolution = new ScreenIdentityResolver().Resolve(accounts, [Known("home", home)]);
        Assert.Equal(ResolutionKind.NewScreen, resolution.Kind);
        Assert.True(ScreenIdentityResolver.Jaccard(home.IdentityTokens, accounts.IdentityTokens) < IdentityResolverOptions.Default.ScreenThreshold);
    }

    [Fact]
    public void The_investor_home_is_a_variant_of_the_basic_home()
    {
        var basic = Normalize(Basic);
        var investor = Normalize(Investor);

        Assert.NotEqual(basic.IdentityHash, investor.IdentityHash);
        Assert.Equal(["i:id:" + Id("tab.investments")], investor.IdentityTokens.Except(basic.IdentityTokens));
        var similarity = ScreenIdentityResolver.Jaccard(basic.IdentityTokens, investor.IdentityTokens);
        Assert.Equal(6.0 / 7.0, similarity, 6);

        var hints = IdentityResolverOptions.Default with { ScreenHints = [new Regex(@":id/screen\.[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))] };
        foreach (var resolver in new[] { new ScreenIdentityResolver(), new ScreenIdentityResolver(hints) })
        {
            var result = resolver.Resolve(investor, [Known("home", basic)]);
            Assert.Equal((ResolutionKind.KnownStateVariant, "home"), (result.Kind, result.StateId));
        }
    }

    [Fact]
    public void The_investor_home_is_also_a_profile_variant_with_a_margin_of_one_token()
    {
        // R-04 (STEP-07): 0.857 is 0.007 above τ_state; the profile variant rule does not depend on that margin: one token of difference
        // (≤ identity.profileVariantMaxDelta = 2), same screen marker, the basic state not yet seen in the investor context.
        var basic = Normalize(Basic);
        var investor = Normalize(Investor);
        var hints = IdentityResolverOptions.Default with { ScreenHints = [new Regex(@":id/screen\.[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))] };
        var known = Known("home", basic) with { ContextIds = ["ctx_basic"] };

        Assert.Equal(1, ScreenIdentityResolver.Delta(basic.IdentityTokens, investor.IdentityTokens));
        var strict = new ScreenIdentityResolver(hints with { StateThreshold = 0.9 });
        Assert.Equal(ResolutionKind.NewStateOfScreen, strict.Resolve(investor, [known]).Kind);
        var result = strict.Resolve(investor, [known], null, "ctx_investor");
        Assert.Equal((ResolutionKind.KnownStateVariant, "home", 1), (result.Kind, result.StateId, result.ProfileVariantDelta));
    }

    [Fact]
    public void The_app_is_detected_in_the_foreground_and_the_launcher_outside_it()
    {
        Assert.Equal(AppPresence.InApp, AppPresenceDetector.Of(Normalize(Basic), App));

        var launcher = TapCueParsers.ParseHierarchy(Fixtures.Text("hierarchy-android-launcher.txt"), out _);
        Assert.Equal(AppPresence.Left, AppPresenceDetector.Of(Normalize(launcher), App));

        // An AlertDialog window has only framework ids: not taken for another app.
        var dialog = UiTrees.Flatten(UiTrees.Container("android.widget.FrameLayout", UiTrees.Text("Delete this account?", "android:id/alertTitle"), UiTrees.Button("android:id/button2", "Cancel")));
        var normalized = Normalize(dialog);
        Assert.Equal(AppPresence.Unknown, AppPresenceDetector.Of(normalized, App));
        Assert.Equal(StateKind.Modal, normalized.StateKind);
    }
}

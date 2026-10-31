using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>
/// Fingerprint stability (R-04) on the real Android snapshots recorded in STEP-02 (Settings and launcher, TapCue 0.1.0, API 34):
/// System UI filtering, sibling permutation, digits in texts and list length do not change the identity; the two screens differ.
/// </summary>
public class RecordedSnapshotFingerprintTests
{
    private const string Recording = "mcp-android-settings.jsonl";
    private const string SettingsRows = "com.android.settings:id/recycler_view";

    private static readonly NormalizationOptions _android = new(Platform.Android);

    private static IReadOnlyList<RawElement> Screen(int index) =>
        TapCueParsers.ParseInspectScreen(Fixtures.ToolText(Fixtures.Responses(Recording, "tools/call", "inspect_screen")[index]));

    private static IReadOnlyList<RawElement> Settings => Screen(0);

    private static IReadOnlyList<RawElement> Launcher => Screen(1);

    private static NormalizedSnapshot Normalize(IReadOnlyList<RawElement> elements) => SnapshotNormalizer.Normalize(elements, _android);

    private static NormalizedSnapshot Normalize(IReadOnlyList<UiNode> roots) => Normalize(UiTrees.Flatten([.. roots]));

    private static Arbitrary<int> Seeds => Arb.From(Gen.Choose(0, int.MaxValue));

    [Fact]
    public void System_ui_status_bar_is_removed_from_both_recordings()
    {
        foreach (var elements in new[] { Settings, Launcher })
        {
            var snapshot = Normalize(elements);
            Assert.Equal(26, snapshot.SystemUiElementsRemoved);
            Assert.DoesNotContain(snapshot.Elements, e => e.ResourceId?.StartsWith("com.android.systemui:", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(snapshot.IdentityTokens, t => t.Contains("systemui", StringComparison.Ordinal));
            Assert.Equal(elements.Count - 26, snapshot.Elements.Count);
        }
    }

    [Fact]
    public void Settings_and_launcher_have_different_identities_and_known_anchors()
    {
        var settings = Normalize(Settings);
        var launcher = Normalize(Launcher);

        Assert.NotEqual(settings.IdentityHash, launcher.IdentityHash);
        Assert.Equal(["android", "com.android.settings"], settings.ObservedPackages);
        Assert.Equal(["android", "com.google.android.apps.nexuslauncher"], launcher.ObservedPackages);
        Assert.Contains("i:id:com.android.settings:id/search_action_bar", settings.IdentityTokens);
        Assert.Contains("a:id:com.android.settings:id/homepage_title", settings.IdentityTokens);
        var rows = Assert.Single(settings.Lists);
        Assert.Equal(("id:" + SettingsRows, 4, SnapshotNormalizer.CountBucketMany), (rows.ContainerKey, rows.RowCount, rows.CountBucket));
        Assert.Equal(StateKind.Default, settings.StateKind);
        Assert.Equal(0, settings.WeakKeyCount);
        Assert.Equal(1, launcher.WeakKeyCount);
    }

    [Fact]
    public void Recorded_snapshots_have_pinned_identity_hashes_for_fingerprint_version_3()
    {
        // A change here means the fingerprint changed: bump SnapshotNormalizer.FingerprintVersion (ADR-0006 §4).
        // Version 2 (STEP-05): framework containers such as android:id/content are no longer identity anchors.
        // Version 3 (STEP-07): the container of a detected list (Settings rows, launcher workspace) is an anchor, not an interactive token.
        Assert.Equal(3, SnapshotNormalizer.FingerprintVersion);
        Assert.Equal("sha256:603595d91c6dfdf996aed366e06023080a50870ab3f958394c839b3b09981754", Normalize(Settings).IdentityHash);
        Assert.Equal("sha256:887f6f4ba06f2262a3304b9dbf37b744a0a41a6544d3e38f2d6e1bedb3f2af07", Normalize(Launcher).IdentityHash);
    }

    [Fact]
    public void Full_hierarchy_of_the_launcher_resolves_to_the_same_identity_as_the_compact_snapshot()
    {
        var full = TapCueParsers.ParseHierarchy(Fixtures.Text("hierarchy-android-launcher.txt"), out _);
        var fast = Normalize(Launcher);
        var slow = Normalize(full);
        var similarity = ScreenIdentityResolver.Jaccard(fast.IdentityTokens, slow.IdentityTokens);
        Assert.True(similarity >= IdentityResolverOptions.Default.StateThreshold, $"fast/full similarity {similarity:0.###}");
    }

    [Property(MaxTest = 100)]
    public Property Permuting_siblings_of_recorded_screens_keeps_identity() =>
        Prop.ForAll(Seeds, seed =>
        {
            var random = new Random(seed);
            return new[] { Settings, Launcher }.All(elements =>
            {
                var shuffled = UiTrees.Shuffle(UiTrees.FromPreOrder(elements), random);
                return Normalize(shuffled).IdentityHash == Normalize(elements).IdentityHash;
            });
        });

    [Property(MaxTest = 100)]
    public Property Changing_digits_in_recorded_texts_keeps_identity_and_layout() =>
        Prop.ForAll(Seeds, seed =>
        {
            var random = new Random(seed);
            return new[] { Settings, Launcher }.All(elements =>
            {
                var mutated = UiTrees.Map(UiTrees.FromPreOrder(elements), e => e with { Text = Redigit(e.Text, random), AccessibilityText = Redigit(e.AccessibilityText, random) });
                var a = Normalize(elements);
                var b = Normalize(mutated);
                return a.IdentityHash == b.IdentityHash && a.LayoutHash == b.LayoutHash;
            });
        });

    // The recycler view is not reported as scrollable, so it is a list by repetition (≥ 3 rows); the last, partially visible
    // row (another subtree, same root element) may come and go while scrolling.
    [Property(MaxTest = 100)]
    public Property Settings_list_length_keeps_identity() =>
        Prop.ForAll(Arb.From(Gen.Choose(3, 30)), Arb.From(Gen.Elements(true, false)), (rows, partialRow) =>
        {
            var resized = UiTrees.ResizeLists(
                UiTrees.FromPreOrder(Settings),
                e => e.ResourceId == SettingsRows,
                children => [.. Enumerable.Range(0, rows).Select(i => children[i % 4]), .. partialRow ? [children[4]] : Array.Empty<UiNode>()]);
            return Normalize(resized).IdentityHash == Normalize(Settings).IdentityHash;
        });

    [Fact]
    public void Emptying_the_settings_list_changes_identity()
    {
        var empty = UiTrees.ResizeLists(UiTrees.FromPreOrder(Settings), e => e.ResourceId == SettingsRows, _ => []);
        Assert.NotEqual(Normalize(Settings).IdentityHash, Normalize(empty).IdentityHash);
    }

    [Property(MaxTest = 50)]
    public Property Status_bar_clock_never_changes_identity() =>
        Prop.ForAll(Arb.From(Gen.Choose(0, 23)), Arb.From(Gen.Choose(0, 59)), (h, m) =>
        {
            var clock = $"{h}:{m:D2} ";
            var changed = UiTrees.Map(UiTrees.FromPreOrder(Settings), e => e.ResourceId == "com.android.systemui:id/clock" ? e with { Text = clock, AccessibilityText = clock } : e);
            return Normalize(changed).IdentityHash == Normalize(Settings).IdentityHash;
        });

    private static string? Redigit(string? text, Random random) =>
        text is null ? null : new string([.. text.Select(c => char.IsAsciiDigit(c) ? (char)('0' + random.Next(10)) : c)]);
}

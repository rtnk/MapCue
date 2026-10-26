using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MapCue.Application.Graph;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;
using static MapCue.Testing.UiTrees;

namespace MapCue.Application.Tests.Graph;

/// <summary>
/// Property-based stability of the fingerprint (R-04) on generated screens: sibling permutation, digits in texts, list length and
/// system UI content never change the identity hash. Screens are generated from an FsCheck-chosen seed, so failures are reproducible.
/// </summary>
public class FingerprintPropertyTests
{
    private static readonly NormalizationOptions _android = new(Platform.Android);

    private static Arbitrary<int> Seeds => Arb.From(Gen.Choose(0, int.MaxValue));

    private static NormalizedSnapshot Normalize(IReadOnlyList<UiNode> roots) => SnapshotNormalizer.Normalize(Flatten([.. roots]), _android);

    [Property(MaxTest = 300)]
    public Property Sibling_permutation_keeps_identity_and_element_set() =>
        Prop.ForAll(Seeds, seed =>
        {
            var screen = RandomScreen(new Random(seed), listRows: 4);
            var shuffled = Shuffle(screen, new Random(seed ^ 0x5f3759df));
            var a = Normalize(screen);
            var b = Normalize(shuffled);
            return a.IdentityHash == b.IdentityHash && a.ElementSetHash == b.ElementSetHash && a.StateKind == b.StateKind;
        });

    [Property(MaxTest = 300)]
    public Property Changing_digits_in_texts_keeps_identity_and_layout() =>
        Prop.ForAll(Seeds, seed =>
        {
            var screen = RandomScreen(new Random(seed), listRows: 3);
            var digits = new Random(seed ^ 0x2545f491);
            var mutated = Map(screen, e => e with { Text = Redigit(e.Text, digits), AccessibilityText = Redigit(e.AccessibilityText, digits) });
            var a = Normalize(screen);
            var b = Normalize(mutated);
            return a.IdentityHash == b.IdentityHash && a.LayoutHash == b.LayoutHash && a.ElementSetHash == b.ElementSetHash;
        });

    [Property(MaxTest = 300)]
    public Property List_length_two_or_more_keeps_identity() =>
        Prop.ForAll(Seeds, Arb.From(Gen.Choose(2, 40)), Arb.From(Gen.Choose(2, 40)), (seed, n, m) =>
            Normalize(RandomScreen(new Random(seed), n)).IdentityHash == Normalize(RandomScreen(new Random(seed), m)).IdentityHash);

    [Property(MaxTest = 200)]
    public Property Empty_list_is_always_a_different_state() =>
        Prop.ForAll(Seeds, Arb.From(Gen.Choose(1, 40)), (seed, n) =>
            Normalize(RandomScreen(new Random(seed), 0)).IdentityHash != Normalize(RandomScreen(new Random(seed), n)).IdentityHash);

    [Property(MaxTest = 200)]
    public Property System_ui_content_never_changes_identity() =>
        Prop.ForAll(Seeds, Arb.From(Gen.Choose(0, 23)), Arb.From(Gen.Choose(0, 59)), (seed, h, m) =>
        {
            var screen = RandomScreen(new Random(seed), 3);
            return Normalize([StatusBar($"{h}:{m:D2}"), .. screen]).IdentityHash == Normalize(screen).IdentityHash;
        });

    [Property(MaxTest = 100)]
    public Property Normalization_is_deterministic() =>
        Prop.ForAll(Seeds, seed =>
        {
            var a = Normalize(RandomScreen(new Random(seed), 5));
            var b = Normalize(RandomScreen(new Random(seed), 5));
            return a.IdentityHash == b.IdentityHash && a.LayoutHash == b.LayoutHash && a.Elements.SequenceEqual(b.Elements);
        });

    [Property(MaxTest = 200)]
    public Property A_new_interactive_element_changes_identity() =>
        Prop.ForAll(Seeds, seed =>
        {
            var screen = RandomScreen(new Random(seed), 3);
            var extra = new[] { screen[0] with { Children = [.. screen[0].Children, Button("app:id/investments", "Investments")] } };
            return Normalize(screen).IdentityHash != Normalize(extra).IdentityHash;
        });

    /// <summary>A random app screen: a few random subtrees and a scrollable list with <paramref name="listRows"/> rows (built last).</summary>
    internal static IReadOnlyList<UiNode> RandomScreen(Random random, int listRows)
    {
        var children = new List<UiNode>();
        var count = random.Next(1, 6);
        for (var i = 0; i < count; i++)
        {
            children.Add(RandomSubtree(random, 1));
        }

        children.Add(List("app:id/list", listRows, i => $"Item {random.Next(1000)} · {random.Next(1, 28)}.{random.Next(1, 12)}.2026"));
        return [Container("android.widget.FrameLayout", [.. children])];
    }

    private static UiNode RandomSubtree(Random random, int depth) => random.Next(6) switch
    {
        0 => Button($"app:id/button_{random.Next(20)}", $"Pay {random.Next(100)}"),
        1 => Text($"Updated {random.Next(1000)} at {random.Next(24)}:{random.Next(60):D2}"),
        2 => Node("android.widget.ImageButton", clickable: true),
        3 => Node("android.widget.TextView", text: $"Open {random.Next(10)} items", clickable: true),
        4 => Text($"Section {random.Next(5)}", $"app:id/section_{random.Next(5)}"),
        _ when depth < 3 => Container("android.widget.LinearLayout", [.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => RandomSubtree(random, depth + 1))]),
        _ => Text("Leaf"),
    };

    private static string? Redigit(string? text, Random random) =>
        text is null ? null : new string([.. text.Select(c => char.IsAsciiDigit(c) ? (char)('0' + random.Next(10)) : c)]);
}

using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using static MapCue.Testing.UiTrees;

namespace MapCue.Application.Tests.Graph;

public class SnapshotNormalizerTests
{
    private static readonly NormalizationOptions _android = new(Platform.Android);

    private static NormalizedSnapshot Normalize(params MapCue.Testing.UiNode[] roots) => SnapshotNormalizer.Normalize(Flatten(roots), _android);

    private static MapCue.Testing.UiNode Home(int rows = 3, string title = "Accounts") =>
        Container(
            "android.widget.FrameLayout",
            Text(title, "app:id/title"),
            Button("app:id/settings", "Settings"),
            List("app:id/accounts", rows));

    [Fact]
    public void System_ui_rule_removes_the_status_bar_subtree_and_its_empty_container()
    {
        var withBar = Normalize(StatusBar(), Home());
        var without = Normalize(Home());

        Assert.Equal(4, withBar.SystemUiElementsRemoved);
        Assert.DoesNotContain(withBar.Elements, e => e.ResourceId?.StartsWith("com.android.systemui:", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(withBar.Elements, e => e.Label?.Contains("battery", StringComparison.Ordinal) == true);
        Assert.Equal(without.IdentityHash, withBar.IdentityHash);
        Assert.Equal(without.ElementSetHash, withBar.ElementSetHash);
        Assert.DoesNotContain("com.android.systemui", withBar.ObservedPackages);
    }

    [Fact]
    public void A_list_widget_that_does_not_scroll_is_a_list_and_its_scrollability_never_changes_identity()
    {
        // STEP-07 (fingerprint version 3): the demo app's accounts RecyclerView with 2 rows (basic) and 3 rows (investor), whether the
        // rows fit (not scrollable) or not. Version 2 made only 3 rows in a non-scrollable container a list.
        static MapCue.Testing.UiNode Accounts(int rows, bool scrollable) =>
            Container(
                "android.widget.FrameLayout",
                Text("Accounts", "app:id/screen.accounts"),
                Node("androidx.recyclerview.widget.RecyclerView", "app:id/accounts.list", scrollable: scrollable, children: [.. Enumerable.Range(0, rows).Select(i => Button(string.Empty, $"Account {i} · PLN 1,250.00"))]),
                Button("app:id/tab.home", "Home"));

        var two = Normalize(Accounts(2, scrollable: false));
        Assert.Equal(two.IdentityHash, Normalize(Accounts(3, scrollable: false)).IdentityHash);
        Assert.Equal(two.IdentityHash, Normalize(Accounts(3, scrollable: true)).IdentityHash);
        Assert.Equal(two.IdentityHash, Normalize(Accounts(2, scrollable: true)).IdentityHash);
        Assert.Contains(two.IdentityTokens, t => t.StartsWith("l:id:app:id/accounts.list|", StringComparison.Ordinal) && t.EndsWith("|many", StringComparison.Ordinal));
        Assert.Contains("a:id:app:id/accounts.list", two.IdentityTokens);
        Assert.DoesNotContain(two.IdentityTokens, t => t.Contains("Account", StringComparison.OrdinalIgnoreCase) && t.StartsWith("i:", StringComparison.Ordinal));

        // A plain container still needs three repeated children; two similar buttons stay two elements.
        var pair = Normalize(Container("android.widget.LinearLayout", Button(string.Empty, "Yes"), Button(string.Empty, "No")));
        Assert.Empty(pair.Lists);
    }

    [Fact]
    public void Status_bar_content_never_changes_identity()
    {
        Assert.Equal(Normalize(StatusBar("10:18"), Home()).IdentityHash, Normalize(StatusBar("23:59"), Home()).IdentityHash);
    }

    [Fact]
    public void System_ui_rules_are_explicit_per_platform()
    {
        Assert.Equal(["android.systemui", "android.ime.gboard", "android.ime.aosp"], SystemUiRules.Android.Select(r => r.RuleId));
        Assert.All(SystemUiRules.Android, r => Assert.Equal(Platform.Android, r.Platform));
        Assert.Empty(SystemUiRules.For(Platform.Ios));

        var rule = SystemUiRules.Android[0];
        Assert.True(rule.Matches("com.android.systemui:id/clock"));
        Assert.False(rule.Matches("com.android.systemuix:id/clock"));
        Assert.False(rule.Matches("com.android.systemui"));
        Assert.False(rule.Matches(null));
    }

    [Fact]
    public void Ios_snapshot_keeps_everything_because_no_ios_rule_exists_yet()
    {
        var elements = Flatten(StatusBar(), Home());
        var ios = SnapshotNormalizer.Normalize(elements, new NormalizationOptions(Platform.Ios));
        Assert.Equal(0, ios.SystemUiElementsRemoved);
        Assert.Equal(elements.Count, ios.Elements.Count);
    }

    [Fact]
    public void Custom_rules_replace_the_built_in_ones()
    {
        var options = new NormalizationOptions(Platform.Android, SystemUiRules: [new SystemUiRule("test.ads", Platform.Android, "com.ads", IsKeyboard: false)]);
        var snapshot = SnapshotNormalizer.Normalize(Flatten(StatusBar(), Home(), Button("com.ads:id/banner")), options);
        Assert.Contains(snapshot.Elements, e => e.ResourceId == "com.android.systemui:id/clock");
        Assert.DoesNotContain(snapshot.Elements, e => e.ResourceId == "com.ads:id/banner");
    }

    [Fact]
    public void Keyboard_is_filtered_but_becomes_a_state_discriminator()
    {
        var form = Container("android.widget.FrameLayout", Node("android.widget.EditText", "app:id/user", clickable: true));
        var keyboard = Container("android.widget.FrameLayout", Node("android.widget.FrameLayout", "com.google.android.inputmethod.latin:id/keyboard_holder", children: [Button("com.google.android.inputmethod.latin:id/key_a", "a")]));

        var closed = Normalize(form);
        var open = Normalize(form, keyboard);

        Assert.Equal(StateKind.Form, closed.StateKind);
        Assert.Equal(StateKind.Keyboard, open.StateKind);
        Assert.Contains("k:keyboard", open.IdentityTokens);
        Assert.NotEqual(closed.IdentityHash, open.IdentityHash);
        Assert.DoesNotContain(open.Elements, e => e.ResourceId?.Contains("inputmethod", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void List_length_does_not_change_identity_but_an_empty_list_does()
    {
        var three = Normalize(Home(rows: 3));
        var seven = Normalize(Home(rows: 7));
        var two = Normalize(Home(rows: 2));
        var empty = Normalize(Home(rows: 0));

        Assert.Equal(three.IdentityHash, seven.IdentityHash);
        Assert.Equal(three.IdentityHash, two.IdentityHash);
        Assert.NotEqual(three.IdentityHash, empty.IdentityHash);
        Assert.Equal(StateKind.Empty, empty.StateKind);
        Assert.Equal(SnapshotNormalizer.CountBucketMany, Assert.Single(seven.Lists).CountBucket);
        Assert.Equal(7, seven.Lists[0].RowCount);
        Assert.Equal(SnapshotNormalizer.CountBucketEmpty, Assert.Single(empty.Lists).CountBucket);
        Assert.NotEqual(three.LayoutHash, seven.LayoutHash);
    }

    [Fact]
    public void Row_texts_are_not_part_of_identity()
    {
        var a = Normalize(Home(rows: 4));
        var b = Normalize(Container("android.widget.FrameLayout", Text("Accounts", "app:id/title"), Button("app:id/settings", "Settings"), List("app:id/accounts", 4, i => $"Savings {i}")));
        Assert.Equal(a.IdentityHash, b.IdentityHash);
        Assert.All(a.Elements.Where(e => e.ResourceId == "app:id/row_title"), e => Assert.NotNull(e.ListTemplate));
    }

    [Fact]
    public void Three_repeated_siblings_form_a_list_even_without_scrolling()
    {
        MapCue.Testing.UiNode Tabs(params string[] labels) => Container("android.widget.LinearLayout", [.. labels.Select(l => Node("android.widget.TextView", text: l, clickable: true))]);
        var three = Normalize(Container("android.widget.FrameLayout", Button("app:id/ok"), Tabs("Phone", "Messages", "Gmail")));
        var four = Normalize(Container("android.widget.FrameLayout", Button("app:id/ok"), Tabs("Phone", "Messages", "Chrome", "Camera")));
        Assert.Equal(three.IdentityHash, four.IdentityHash);
        Assert.Single(three.Lists);
    }

    [Fact]
    public void Selected_element_changes_identity()
    {
        MapCue.Testing.UiNode Tabs(int selected) => Container("android.widget.LinearLayout", [.. Enumerable.Range(0, 3).Select(i => Node("android.widget.TextView", $"app:id/tab_{i}", $"Tab {i}", clickable: true, selected: i == selected))]);
        Assert.NotEqual(Normalize(Tabs(0)).IdentityHash, Normalize(Tabs(1)).IdentityHash);
    }

    [Fact]
    public void Element_keys_prefer_resource_id_then_label_then_weak_structural_path()
    {
        var snapshot = Normalize(Container(
            "android.widget.FrameLayout",
            Button("app:id/save", "Save"),
            Node("android.widget.Button", text: "Cancel 12", clickable: true),
            Container("android.view.ViewGroup", Node("android.widget.ImageButton", clickable: true))));

        var save = snapshot.Elements.Single(e => e.ResourceId == "app:id/save");
        Assert.Equal(("id:app:id/save", ElementKeyStrength.ResourceId), (save.ElementKey, save.KeyStrength));
        var cancel = snapshot.Elements.Single(e => e.Text == "Cancel 12");
        Assert.Equal(("label:button:cancel #", ElementKeyStrength.Label), (cancel.ElementKey, cancel.KeyStrength));
        var icon = snapshot.Elements.Single(e => e.Role == "button" && e.ResourceId is null && e.Text is null);
        Assert.Equal(("path:root/framelayout/viewgroup/button", ElementKeyStrength.Structural), (icon.ElementKey, icon.KeyStrength));
        Assert.Equal(1, snapshot.WeakKeyCount);
        Assert.Equal(["e1", "e2", "e3", "e4", "e5"], snapshot.Elements.Select(e => e.ElementRef));
    }

    [Fact]
    public void Identity_covers_interactive_elements_and_anchors_but_not_static_text()
    {
        var a = Normalize(Container("android.widget.FrameLayout", Text("Hello Anna"), Text("Balance", "app:id/balance_label"), Button("app:id/pay", "Pay")));
        var b = Normalize(Container("android.widget.FrameLayout", Text("Hello Robert"), Text("Balance", "app:id/balance_label"), Button("app:id/pay", "Pay")));

        Assert.Equal(["a:id:app:id/balance_label", "i:id:app:id/pay"], a.IdentityTokens);
        Assert.Equal(a.IdentityHash, b.IdentityHash);
        Assert.NotEqual(a.ElementSetHash, b.ElementSetHash);
    }

    [Fact]
    public void Modal_and_loading_are_discriminators()
    {
        var modal = Normalize(Home(), Container("android.widget.FrameLayout", Node("android.widget.LinearLayout", "android:id/parentPanel", children: [Button("android:id/button1", "OK")])));
        var loading = Normalize(Home(), Node("android.widget.ProgressBar", "app:id/spinner"));
        Assert.Equal(StateKind.Modal, modal.StateKind);
        Assert.Equal(StateKind.Loading, loading.StateKind);
        Assert.Contains("k:modal", modal.IdentityTokens);
        Assert.Contains("k:loading", loading.IdentityTokens);
    }

    [Fact]
    public void Input_values_are_masked_in_labels()
    {
        var options = new NormalizationOptions(Platform.Android, InputValues: ["anna.k"]);
        var a = SnapshotNormalizer.Normalize(Flatten(Container("android.widget.FrameLayout", Node("android.widget.TextView", text: "Logged in as anna.k", clickable: true))), options);
        Assert.Contains(a.Elements, e => e.Label == "logged in as ‹input›");
    }

    [Fact]
    public void Hashes_carry_the_fingerprint_version_and_are_well_formed()
    {
        var snapshot = Normalize(Home());
        Assert.Equal(SnapshotNormalizer.FingerprintVersion, snapshot.FingerprintVersion);
        Assert.True(ContentHash.IsValid(snapshot.IdentityHash));
        Assert.True(ContentHash.IsValid(snapshot.LayoutHash));
        Assert.True(ContentHash.IsValid(snapshot.ElementSetHash));
        Assert.Equal(snapshot.IdentityTokens.Order(StringComparer.Ordinal), snapshot.IdentityTokens);
    }

    [Fact]
    public void Malformed_depths_are_tolerated()
    {
        var elements = new List<RawElement>
        {
            new("app:id/a", null, null, "android.widget.Button", true, true, Depth: 3),
            new("app:id/b", null, null, "android.widget.Button", true, true, Depth: 0),
        };
        var snapshot = SnapshotNormalizer.Normalize(elements, _android);
        Assert.Equal(2, snapshot.Elements.Count);
    }

    [Theory]
    [InlineData("android.widget.Button", "button")]
    [InlineData("android.widget.EditText", "textField")]
    [InlineData("XCUIElementTypeStaticText", "text")]
    [InlineData("androidx.recyclerview.widget.RecyclerView", "list")]
    [InlineData("android.widget.ProgressBar", "progress")]
    [InlineData("com.example.ConfirmDialog", "dialog")]
    [InlineData("android.widget.LinearLayout", "linearlayout")]
    [InlineData(null, "node")]
    public void Roles_come_from_class_names(string? className, string role) => Assert.Equal(role, SnapshotNormalizer.RoleOf(className));
}

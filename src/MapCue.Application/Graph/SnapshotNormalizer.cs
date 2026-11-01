using System.Globalization;
using System.Text.RegularExpressions;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>Inputs of normalization besides the elements. <see cref="InputValues"/> are profile input values masked as <c>‹input›</c>.</summary>
public sealed record NormalizationOptions(Platform Platform, IReadOnlyCollection<string>? InputValues = null, IReadOnlyList<SystemUiRule>? SystemUiRules = null);

/// <summary>
/// Snapshot normalization and fingerprints, version <see cref="FingerprintVersion"/> (architecture §7, ADR-0006).
/// <list type="number">
/// <item>System UI subtrees are removed by explicit <see cref="SystemUiRule"/>s; containers left empty by that removal go too.</item>
/// <item>Labels are masked by <see cref="TextMasker"/>.</item>
/// <item>A list is a container whose children repeat one shape: a scrollable container or a list widget (role <c>list</c>, whether it
/// scrolls or not) with ≥ 2 such children (or none at all),
/// or any container with ≥ 3 (there only children with the rows' root element are rows). Rows count only as <c>list(templateHash, countBucket)</c>, so 3 and 7 rows are the same state
/// and an empty list is a different one. Children are compared as a multiset, not by adjacency, so order never matters.</item>
/// <item><c>elementKey</c>: resource id, else role + masked label, else role + structural path from the nearest keyed ancestor (weak).</item>
/// </list>
/// <c>identityHash</c> covers interactive elements and resource-id anchors (except framework containers) outside list rows, list tokens, selected elements and
/// state-kind discriminators, as a sorted multiset. <c>layoutHash</c> covers every kept element in order with bounds buckets.
/// </summary>
public static partial class SnapshotNormalizer
{
    /// <summary>Version of the algorithm (rules, masking, list detection, keys). Any change of output requires a new version.</summary>
    /// <remarks>
    /// Version 2 (STEP-05): framework containers (<see cref="FrameworkContainerRules"/>) are no longer identity anchors.
    /// Version 3 (STEP-07): a list widget is a list even when it does not scroll (a RecyclerView whose rows fit is not scrollable, so with
    /// version 2 the same screen with 2 and 3 rows had different identities), and the container of a detected list is never an interactive
    /// identity token (its scrollability depends on how many rows fit).
    /// </remarks>
    public const int FingerprintVersion = 3;

    public const string CountBucketEmpty = "0";
    public const string CountBucketOne = "1";
    public const string CountBucketMany = "many";

    private static readonly HashSet<string> _modalResourceIds = new(StringComparer.Ordinal)
    {
        "android:id/parentPanel",
        "android:id/alertTitle",
        "android:id/buttonPanel",
    };

    public static NormalizedSnapshot Normalize(RawObservation observation, NormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Normalize(observation.Elements, options);
    }

    public static NormalizedSnapshot Normalize(IReadOnlyList<RawElement> elements, NormalizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(elements);
        ArgumentNullException.ThrowIfNull(options);
        var rules = options.SystemUiRules ?? SystemUiRules.For(options.Platform);

        var nodes = BuildTree(elements);
        var (systemRemoved, keyboard) = RemoveSystemUi(nodes, rules);
        var kept = nodes.Where(n => !n.Removed).ToList();

        foreach (var node in kept)
        {
            node.Role = RoleOf(node.Raw.ClassName);
            node.Label = TextMasker.Mask(FirstNonEmpty(node.Raw.AccessibilityText, node.Raw.Text, node.Raw.HintText), options.InputValues);
            node.Interactive = node.Raw.Clickable || node.Raw.Scrollable || node.Role is "textField" or "toggle";
            AssignKey(node, nodes);
        }

        // Shapes bottom-up (pre-order reversed visits children before parents).
        for (var i = kept.Count - 1; i >= 0; i--)
        {
            var node = kept[i];
            var childShapes = node.Children.Select(c => nodes[c]).Where(c => !c.Removed).Select(c => c.Shape!).Order(StringComparer.Ordinal);
            node.Shape = ContentHash.Of(["shape", node.Role, node.Raw.ResourceId ?? string.Empty, node.Raw.Clickable ? "c" : string.Empty, node.Raw.Scrollable ? "s" : string.Empty, .. childShapes]);
        }

        var lists = DetectLists(kept, nodes);

        var discriminators = new SortedSet<string>(StringComparer.Ordinal);
        if (kept.Any(n => n.Role == "dialog" || (n.Raw.ResourceId is { } rid && _modalResourceIds.Contains(rid))))
        {
            discriminators.Add("modal");
        }

        if (kept.Any(n => n.Role == "progress"))
        {
            discriminators.Add("loading");
        }

        if (keyboard)
        {
            discriminators.Add("keyboard");
        }

        if (lists.Any(l => l.CountBucket == CountBucketEmpty))
        {
            discriminators.Add("empty");
        }

        var stateKind = discriminators.Contains("modal") ? StateKind.Modal
            : discriminators.Contains("loading") ? StateKind.Loading
            : discriminators.Contains("keyboard") ? StateKind.Keyboard
            : discriminators.Contains("empty") ? StateKind.Empty
            : kept.Any(n => n.Role == "textField") ? StateKind.Form
            : StateKind.Default;

        var tokens = new List<string>();
        var elementSet = new List<string>();
        var listContainers = lists.Select(l => l.ContainerKey).ToHashSet(StringComparer.Ordinal);
        foreach (var node in kept)
        {
            if (node.Raw.Selected)
            {
                tokens.Add("s:" + node.Key);
            }

            if (node.InRow)
            {
                continue;
            }

            // The container of a detected list is identified by its list token and, with a resource id, as an anchor: whether it scrolls
            // (how many rows fit on the screen) never changes the identity (version 3).
            if (node.Interactive && !listContainers.Contains(node.Key!))
            {
                tokens.Add("i:" + node.Key);
            }
            else if (node.Strength == ElementKeyStrength.ResourceId && !FrameworkContainerRules.IsFrameworkContainer(options.Platform, node.Raw.ResourceId))
            {
                tokens.Add("a:" + node.Key);
            }

            elementSet.Add(node.Label is not null && node.Strength != ElementKeyStrength.Label ? $"{node.Key}={node.Label}" : node.Key!);
        }

        foreach (var list in lists)
        {
            var token = $"l:{list.ContainerKey}|{list.TemplateHash}|{list.CountBucket}";
            tokens.Add(token);
            elementSet.Add(token);
        }

        tokens.AddRange(discriminators.Select(d => "k:" + d));
        tokens.Sort(StringComparer.Ordinal);
        elementSet.Sort(StringComparer.Ordinal);

        var version = FingerprintVersion.ToString(CultureInfo.InvariantCulture);
        var layoutLines = kept.Select(n => string.Join('|', n.Raw.Depth.ToString(CultureInfo.InvariantCulture), n.Role, n.Key, n.Label ?? string.Empty, ((int)FlagsOf(n)).ToString(CultureInfo.InvariantCulture), BoundsBucket(n.Raw.Bounds) ?? string.Empty));

        var uiElements = kept.Select((n, i) => new UiElement(
            ElementRef: "e" + (i + 1).ToString(CultureInfo.InvariantCulture),
            ElementKey: n.Key!,
            KeyStrength: n.Strength,
            Role: n.Role,
            Label: n.Label,
            Text: FirstNonEmpty(n.Raw.Text, n.Raw.AccessibilityText, n.Raw.HintText),
            ResourceId: n.Raw.ResourceId,
            Flags: FlagsOf(n),
            BoundsBucket: BoundsBucket(n.Raw.Bounds),
            ListTemplate: n.ListTemplate,
            Depth: n.Raw.Depth,
            Interactive: n.Interactive)
        {
            Bounds = n.Raw.Bounds,
            ImportantForAccessibility = n.Raw.ImportantForAccessibility,
        }).ToList();

        var packages = kept.Select(n => PackageOf(n.Raw.ResourceId)).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        return new NormalizedSnapshot(
            FingerprintVersion,
            options.Platform,
            uiElements,
            lists,
            tokens,
            IdentityHash: ContentHash.Of("mapcue.identity", version, string.Join('\n', tokens)),
            LayoutHash: ContentHash.Of("mapcue.layout", version, string.Join('\n', layoutLines)),
            ElementSetHash: ContentHash.Of("mapcue.elements", version, string.Join('\n', elementSet)),
            ElementSetKeys: elementSet,
            stateKind,
            [.. discriminators],
            packages,
            systemRemoved,
            WeakKeyCount: kept.Count(n => n.Strength == ElementKeyStrength.Structural && n.Interactive && !n.InRow));
    }

    /// <summary>Count bucket of a list: <c>0</c>, <c>1</c> or <c>many</c>.</summary>
    public static string CountBucket(int rows) => rows switch
    {
        <= 0 => CountBucketEmpty,
        1 => CountBucketOne,
        _ => CountBucketMany,
    };

    /// <summary>Package of an Android resource id (<c>com.example:id/x</c> → <c>com.example</c>), else null.</summary>
    public static string? PackageOf(string? resourceId)
    {
        if (resourceId is null)
        {
            return null;
        }

        var index = resourceId.IndexOf(":id/", StringComparison.Ordinal);
        return index > 0 ? resourceId[..index] : null;
    }

    /// <summary>Role from the platform class name (Android widget or iOS element type). iOS roles are inferred (G-03).</summary>
    public static string RoleOf(string? className)
    {
        if (string.IsNullOrWhiteSpace(className))
        {
            return "node";
        }

        var simple = className[(className.LastIndexOfAny(['.', '$']) + 1)..];
        if (simple.StartsWith("XCUIElementType", StringComparison.Ordinal))
        {
            simple = simple["XCUIElementType".Length..];
        }

        var lower = simple.ToLowerInvariant();
        return lower switch
        {
            "button" or "imagebutton" or "materialbutton" or "appcompatbutton" or "appcompatimagebutton" or "floatingactionbutton" => "button",
            "edittext" or "appcompatedittext" or "textinputedittext" or "autocompletetextview" or "textfield" or "securetextfield" or "searchfield" => "textField",
            "textview" or "appcompattextview" or "materialtextview" or "statictext" or "checkedtextview" => "text",
            "imageview" or "appcompatimageview" or "image" or "icon" => "image",
            "checkbox" or "switch" or "switchcompat" or "switchmaterial" or "materialswitch" or "togglebutton" or "radiobutton" or "toggle" or "materialcheckbox" => "toggle",
            "scrollview" or "horizontalscrollview" or "nestedscrollview" or "recyclerview" or "listview" or "gridview" or "table" or "collectionview" or "viewpager" or "viewpager2" => "list",
            "progressbar" or "activityindicator" or "progressindicator" or "circularprogressindicator" or "linearprogressindicator" => "progress",
            "webview" => "web",
            _ when lower.Contains("dialog", StringComparison.Ordinal) || lower == "alert" || lower == "sheet" => "dialog",
            _ => lower,
        };
    }

    private static List<Node> BuildTree(IReadOnlyList<RawElement> elements)
    {
        var nodes = new List<Node>(elements.Count);
        var path = new List<int>();
        foreach (var raw in elements)
        {
            ArgumentNullException.ThrowIfNull(raw);
            var depth = Math.Clamp(raw.Depth, 0, path.Count);
            var parent = depth == 0 ? -1 : path[depth - 1];
            var index = nodes.Count;
            nodes.Add(new Node(raw, parent));
            if (parent >= 0)
            {
                nodes[parent].Children.Add(index);
            }

            path.RemoveRange(depth, path.Count - depth);
            path.Add(index);
        }

        return nodes;
    }

    private static (int Removed, bool Keyboard) RemoveSystemUi(List<Node> nodes, IReadOnlyList<SystemUiRule> rules)
    {
        var keyboard = false;
        foreach (var node in nodes)
        {
            if (node.Parent >= 0 && nodes[node.Parent].Removed)
            {
                node.Removed = true;
                continue;
            }

            var rule = rules.FirstOrDefault(r => r.Matches(node.Raw.ResourceId));
            if (rule is not null)
            {
                node.Removed = true;
                keyboard |= rule.IsKeyboard;
            }
        }

        // Containers without own content whose children were all removed are part of the system window (e.g. the status bar frame).
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            if (!node.Removed && node.Children.Count > 0 && node.Children.All(c => nodes[c].Removed) && !HasOwnContent(node.Raw))
            {
                node.Removed = true;
            }
        }

        return (nodes.Count(n => n.Removed), keyboard);
    }

    private static bool HasOwnContent(RawElement raw) =>
        raw.ResourceId is not null || raw.Clickable || raw.Scrollable
        || !string.IsNullOrWhiteSpace(raw.Text) || !string.IsNullOrWhiteSpace(raw.AccessibilityText) || !string.IsNullOrWhiteSpace(raw.HintText);

    private static void AssignKey(Node node, List<Node> nodes)
    {
        var parentAnchor = node.Parent >= 0 ? nodes[node.Parent].AnchorPath! : "root";
        if (node.Raw.ResourceId is { } rid)
        {
            node.Key = "id:" + rid;
            node.Strength = ElementKeyStrength.ResourceId;
            node.AnchorPath = node.Key;
        }
        else if (node.Label is not null)
        {
            node.Key = $"label:{node.Role}:{node.Label}";
            node.Strength = ElementKeyStrength.Label;
            node.AnchorPath = node.Key;
        }
        else
        {
            node.AnchorPath = parentAnchor + "/" + node.Role;
            node.Key = "path:" + node.AnchorPath;
            node.Strength = ElementKeyStrength.Structural;
        }
    }

    private static List<UiList> DetectLists(List<Node> kept, List<Node> nodes)
    {
        var lists = new List<UiList>();
        foreach (var node in kept)
        {
            if (node.InRow)
            {
                continue;
            }

            // A list widget (RecyclerView, ListView, CollectionView…) is a list whether or not it scrolls right now: Android reports a
            // RecyclerView as scrollable only when its rows overflow, so two and three rows must not give different identities (version 3).
            var listLike = node.Raw.Scrollable || node.Role == "list";
            var children = node.Children.Select(c => nodes[c]).Where(c => !c.Removed).ToList();
            string template;
            int rows;
            if (children.Count == 0)
            {
                if (!listLike)
                {
                    continue;
                }

                template = "empty";
                rows = 0;
            }
            else
            {
                var dominant = children.GroupBy(c => c.Shape!, StringComparer.Ordinal)
                    .Select(g => (Shape: g.Key, Count: g.Count()))
                    .OrderByDescending(g => g.Count)
                    .ThenBy(g => g.Shape, StringComparer.Ordinal)
                    .First();
                if (dominant.Count < (listLike ? 2 : 3))
                {
                    continue;
                }

                template = dominant.Shape;
                rows = dominant.Count;

                // In a scrollable container every child is a row. Elsewhere rows are the repeated children plus children with the same
                // root element (role, resource id, flags) whose subtree differs, e.g. a partially visible last row; other siblings
                // keep their own identity.
                var rowRoot = children.First(c => c.Shape == template).RootSignature;
                foreach (var child in children.Where(c => listLike || c.Shape == template || c.RootSignature == rowRoot))
                {
                    MarkRow(child, nodes, template);
                }
            }

            lists.Add(new UiList(node.Key!, template, rows, CountBucket(rows)));
        }

        return [.. lists.OrderBy(l => l.ContainerKey, StringComparer.Ordinal).ThenBy(l => l.TemplateHash, StringComparer.Ordinal).ThenBy(l => l.CountBucket, StringComparer.Ordinal)];
    }

    private static void MarkRow(Node node, List<Node> nodes, string template)
    {
        node.InRow = true;
        node.ListTemplate = template;
        foreach (var child in node.Children.Select(c => nodes[c]).Where(c => !c.Removed))
        {
            MarkRow(child, nodes, template);
        }
    }

    private static UiElementStates FlagsOf(Node node)
    {
        var raw = node.Raw;
        var flags = UiElementStates.None;
        flags |= raw.Clickable ? UiElementStates.Clickable : 0;
        flags |= raw.Enabled ? UiElementStates.Enabled : 0;
        flags |= raw.Scrollable ? UiElementStates.Scrollable : 0;
        flags |= raw.Checked ? UiElementStates.Checked : 0;
        flags |= raw.Focused ? UiElementStates.Focused : 0;
        flags |= raw.Selected ? UiElementStates.Selected : 0;
        flags |= node.Role == "textField" ? UiElementStates.Editable : 0;
        return flags;
    }

    /// <summary>Bounds <c>[l,t][r,b]</c> quantized to 8 px cells, e.g. <c>0,3,40,33</c>. Only used by the layout hash.</summary>
    private static string? BoundsBucket(string? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var match = BoundsPattern().Match(bounds);
        if (!match.Success)
        {
            return null;
        }

        return string.Join(',', Enumerable.Range(1, 4).Select(i => (int.Parse(match.Groups[i].Value, CultureInfo.InvariantCulture) / 8).ToString(CultureInfo.InvariantCulture)));
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    [GeneratedRegex(@"^\[(-?\d{1,6}),(-?\d{1,6})\]\[(-?\d{1,6}),(-?\d{1,6})\]$", RegexOptions.CultureInvariant)]
    private static partial Regex BoundsPattern();

    private sealed class Node(RawElement raw, int parent)
    {
        public RawElement Raw { get; } = raw;

        public int Parent { get; } = parent;

        public List<int> Children { get; } = [];

        public bool Removed { get; set; }

        public string Role { get; set; } = "node";

        public string? Label { get; set; }

        public bool Interactive { get; set; }

        public string? Key { get; set; }

        public ElementKeyStrength Strength { get; set; }

        public string? AnchorPath { get; set; }

        public string? Shape { get; set; }

        public string RootSignature => string.Join('|', Role, Raw.ResourceId, Raw.Clickable, Raw.Scrollable);

        public bool InRow { get; set; }

        public string? ListTemplate { get; set; }
    }
}

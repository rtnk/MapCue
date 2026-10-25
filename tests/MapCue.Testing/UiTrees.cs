using MapCue.Application.Ports;

namespace MapCue.Testing;

/// <summary>A raw element with children, for building and transforming snapshots in tests.</summary>
public sealed record UiNode(RawElement Element, IReadOnlyList<UiNode> Children);

/// <summary>Builds, flattens and transforms raw UI trees (pre-order with depth, like the TapCue adapter reports them).</summary>
public static class UiTrees
{
    public static UiNode Node(string className, string? resourceId = null, string? text = null, bool clickable = false, bool scrollable = false, string? a11y = null, bool selected = false, params UiNode[] children) =>
        new(new RawElement(resourceId, text, a11y, className, clickable, Enabled: true, Scrollable: scrollable, Selected: selected), children);

    public static UiNode Container(string className, params UiNode[] children) => Node(className, children: children);

    public static UiNode Button(string resourceId, string? text = null) => Node("android.widget.Button", resourceId, text, clickable: true);

    public static UiNode Text(string text, string? resourceId = null) => Node("android.widget.TextView", resourceId, text);

    /// <summary>The Android status bar as TapCue 0.1.0 reports it (System UI window with a clock).</summary>
    public static UiNode StatusBar(string clock = "10:18") =>
        Container(
            "android.widget.FrameLayout",
            Node(
                "android.widget.FrameLayout",
                "com.android.systemui:id/status_bar",
                children:
                [
                    Node("android.widget.TextView", "com.android.systemui:id/clock", clock, a11y: clock),
                    Node("android.widget.ImageView", a11y: "Battery charging, 100 percent."),
                ]));

    /// <summary>A scrollable list of <paramref name="rows"/> identical-shaped rows with distinct texts.</summary>
    public static UiNode List(string resourceId, int rows, Func<int, string>? text = null) =>
        Node(
            "androidx.recyclerview.widget.RecyclerView",
            resourceId,
            scrollable: true,
            children: [.. Enumerable.Range(0, rows).Select(i => Container("android.widget.LinearLayout", Node("android.widget.TextView", "app:id/row_title", text?.Invoke(i) ?? $"Row {i}", clickable: true)))]);

    public static IReadOnlyList<RawElement> Flatten(params UiNode[] roots)
    {
        var result = new List<RawElement>();
        void Walk(UiNode node, int depth)
        {
            result.Add(node.Element with { Depth = depth });
            foreach (var child in node.Children)
            {
                Walk(child, depth + 1);
            }
        }

        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        return result;
    }

    /// <summary>Rebuilds the tree from pre-order elements with depth (inverse of <see cref="Flatten"/>).</summary>
    public static IReadOnlyList<UiNode> FromPreOrder(IReadOnlyList<RawElement> elements)
    {
        var index = 0;
        List<UiNode> Read(int depth)
        {
            var nodes = new List<UiNode>();
            while (index < elements.Count && elements[index].Depth == depth)
            {
                var element = elements[index++];
                nodes.Add(new UiNode(element, Read(depth + 1)));
            }

            return nodes;
        }

        var roots = Read(0);
        if (index != elements.Count)
        {
            throw new InvalidOperationException("Elements are not a well-formed pre-order tree.");
        }

        return roots;
    }

    /// <summary>Shuffles the children of every node (and the roots) with a seeded random.</summary>
    public static IReadOnlyList<UiNode> Shuffle(IReadOnlyList<UiNode> roots, Random random) =>
        [.. roots.Select(r => ShuffleNode(r, random)).OrderBy(_ => random.Next())];

    /// <summary>Applies <paramref name="map"/> to every element.</summary>
    public static IReadOnlyList<UiNode> Map(IReadOnlyList<UiNode> roots, Func<RawElement, RawElement> map) =>
        [.. roots.Select(r => MapNode(r, map))];

    /// <summary>Replaces the children of every node matching <paramref name="isList"/> with <paramref name="resize"/>(children).</summary>
    public static IReadOnlyList<UiNode> ResizeLists(IReadOnlyList<UiNode> roots, Func<RawElement, bool> isList, Func<IReadOnlyList<UiNode>, IReadOnlyList<UiNode>> resize) =>
        [.. roots.Select(r => ResizeNode(r, isList, resize))];

    private static UiNode ShuffleNode(UiNode node, Random random) =>
        node with { Children = [.. node.Children.Select(c => ShuffleNode(c, random)).OrderBy(_ => random.Next())] };

    private static UiNode MapNode(UiNode node, Func<RawElement, RawElement> map) =>
        new(map(node.Element), [.. node.Children.Select(c => MapNode(c, map))]);

    private static UiNode ResizeNode(UiNode node, Func<RawElement, bool> isList, Func<IReadOnlyList<UiNode>, IReadOnlyList<UiNode>> resize)
    {
        var children = node.Children.Select(c => ResizeNode(c, isList, resize)).ToList();
        return node with { Children = isList(node.Element) ? resize(children) : children };
    }
}

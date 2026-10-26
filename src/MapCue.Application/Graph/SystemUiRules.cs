using MapCue.Domain;

namespace MapCue.Application.Graph;

/// <summary>
/// A rule that marks an element (and its whole subtree) as system UI: status bar, navigation bar, system keyboard
/// (architecture §7.1 step 1). Matching is by resource id package (<c>&lt;package&gt;:id/…</c>).
/// </summary>
public sealed record SystemUiRule(string RuleId, Platform Platform, string ResourceIdPackage, bool IsKeyboard)
{
    public bool Matches(string? resourceId) =>
        resourceId is not null
        && resourceId.Length > ResourceIdPackage.Length
        && resourceId.StartsWith(ResourceIdPackage, StringComparison.Ordinal)
        && resourceId[ResourceIdPackage.Length] == ':';
}

/// <summary>
/// Built-in system UI rules (part of fingerprint version 1). Android rules come from the STEP-02 recordings (TapCue 0.1.0
/// <c>inspect_screen</c> returns the <c>com.android.systemui</c> status bar window) plus the AOSP and Gboard keyboards.
/// iOS has no rules yet: no iOS snapshot has been recorded (R-01), so nothing is filtered there rather than guessing.
/// </summary>
public static class SystemUiRules
{
    public static IReadOnlyList<SystemUiRule> Android { get; } =
    [
        new("android.systemui", Platform.Android, "com.android.systemui", IsKeyboard: false),
        new("android.ime.gboard", Platform.Android, "com.google.android.inputmethod.latin", IsKeyboard: true),
        new("android.ime.aosp", Platform.Android, "com.android.inputmethod.latin", IsKeyboard: true),
    ];

    public static IReadOnlyList<SystemUiRule> Ios { get; } = [];

    public static IReadOnlyList<SystemUiRule> For(Platform platform) => platform switch
    {
        Platform.Android => Android,
        Platform.Ios => Ios,
        _ => [],
    };
}

/// <summary>
/// Layout containers a UI framework adds to every screen (architecture §7.1, R-04). They are stable but carry no screen identity:
/// as identity anchors they make unrelated screens look alike (on the STEP-02B MAUI recordings they were 6 of the 12 identity
/// tokens of Home, which put Accounts above <c>τ_screen</c>). Non-interactive elements with these resource ids stay in the element
/// set and the layout but are not identity anchors (fingerprint version 2). Matching is by the resource id entry name
/// (<c>&lt;package&gt;:id/&lt;name&gt;</c>) or by the full id.
/// </summary>
public static class FrameworkContainerRules
{
    /// <summary>Full Android resource ids of framework containers.</summary>
    public static IReadOnlySet<string> AndroidResourceIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "android:id/content",
    };

    /// <summary>
    /// Entry names of .NET MAUI containers under the app package (confirmed on the STEP-02B emulator recordings,
    /// docs/tapcue-compatibility.md §10).
    /// </summary>
    public static IReadOnlySet<string> AndroidEntryNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "action_bar_root",
        "nav_host",
        "navigation_layout",
        "navigationlayout_content",
    };

    public static bool IsFrameworkContainer(Platform platform, string? resourceId)
    {
        if (platform != Platform.Android || resourceId is null)
        {
            return false;
        }

        if (AndroidResourceIds.Contains(resourceId))
        {
            return true;
        }

        var index = resourceId.IndexOf(":id/", StringComparison.Ordinal);
        return index > 0 && AndroidEntryNames.Contains(resourceId[(index + 4)..]);
    }
}

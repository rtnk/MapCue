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

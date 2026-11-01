using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.ScreenContracts;

/// <summary>
/// One key press of a keyboard focus probe: what the tool returned and which elements reported <c>focused</c> on the fast observation after
/// it (<see cref="SnapshotSha256"/>, redacted raw snapshot). <see cref="IdentityHash"/> shows whether the screen stayed the same state.
/// </summary>
public sealed record FocusProbeStep(int Press, ActionOutcome Outcome, IReadOnlyList<string> FocusedElementKeys, string? IdentityHash, string? SnapshotSha256);

/// <summary>
/// Result of <c>AuditAccessibility</c> for one state in one context (<c>mapcue.focus-probe/1</c>, STEP-08, architecture §14.2 PoC).
/// <see cref="TargetElementKeys"/> are the elements a keyboard user must reach (<see cref="KeyboardTargets"/>) on the observation before
/// the first press; <see cref="CycleClosed"/> is true when focus came back to the first focused element. <see cref="StopReason"/> says why
/// the probe ended.
/// </summary>
public sealed record FocusProbeDocument(
    string SchemaVersion,
    string StateId,
    string ContextId,
    string Key,
    int MaxPresses,
    string? InitialIdentityHash,
    string? InitialSnapshotSha256,
    IReadOnlyList<string> InitialFocusedElementKeys,
    IReadOnlyList<string> TargetElementKeys,
    IReadOnlyList<FocusProbeStep> Steps,
    bool CycleClosed,
    string StopReason)
{
    public const string CurrentSchemaVersion = "mapcue.focus-probe/1";

    /// <summary>The distinct focused elements in the order focus reached them (the observed focus order).</summary>
    public IReadOnlyList<string> FocusOrder()
    {
        var order = new List<string>();
        foreach (var key in Steps.SelectMany(s => s.FocusedElementKeys.Take(1)))
        {
            if (!order.Contains(key, StringComparer.Ordinal))
            {
                order.Add(key);
            }
        }

        return order;
    }
}

/// <summary>Stop reasons of a focus probe.</summary>
public static class FocusProbeStops
{
    public const string CycleClosed = "cycleClosed";
    public const string MaxPresses = "maxPresses";
    public const string NoFocus = "noFocusObserved";
    public const string FocusStuck = "focusDidNotMove";
    public const string ActionFailed = "pressKeyFailed";
    public const string AppLeft = "appLeft";
}

/// <summary>
/// Elements a keyboard (or switch, or screen reader) user must be able to reach: enabled interactive elements, without containers that are
/// interactive only because they scroll (their rows are the targets). Keys are distinct and ordinal-sorted.
/// </summary>
public static class KeyboardTargets
{
    public static IReadOnlyList<string> Of(NormalizedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.Elements.Where(IsTarget).Select(e => e.ElementKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    public static bool IsTarget(UiElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        var scrollOnly = element.Flags.HasFlag(UiElementStates.Scrollable) && !element.Flags.HasFlag(UiElementStates.Clickable) && element.Role is not ("textField" or "toggle");
        return element.Interactive && element.Flags.HasFlag(UiElementStates.Enabled) && !scrollOnly;
    }
}

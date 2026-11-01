namespace MapCue.Domain.Graph;

/// <summary>What a screen capture observed (STEP-08): the full hierarchy of a state, or its keyboard focus probe.</summary>
public enum ScreenCaptureKind
{
    /// <summary><c>AnalyzeScreen</c>: CLI <c>hierarchy</c> (fidelity full) of the state, bracketed by fast observations of the same state.</summary>
    FullHierarchy,

    /// <summary><c>AuditAccessibility</c>: <c>pressKey Tab</c> repeatedly, the <c>focused</c> element observed after each press (Android, PoC).</summary>
    KeyboardFocus,
}

/// <summary>Outcome of a capture. Only <see cref="Captured"/> is evidence for a contract; the others say why there is none.</summary>
public enum ScreenCaptureStatus
{
    Captured,

    /// <summary>The probe ran but no element reported <c>focused</c> (the PoC question of architecture §14.2 answered "not observable").</summary>
    NoFocusObserved,

    /// <summary>The screen was no longer the state during the capture (navigation, animation, a popup).</summary>
    Diverged,

    /// <summary>The state could not be reached over known edges after a reset.</summary>
    Unreachable,

    /// <summary>A device call failed.</summary>
    Failed,
}

/// <summary>
/// The latest capture of one kind for a state in a context (STEP-08). <see cref="SnapshotId"/> is the full snapshot (<see cref="ScreenCaptureKind.FullHierarchy"/>);
/// <see cref="ArtifactSha256"/> the focus probe document (<see cref="ScreenCaptureKind.KeyboardFocus"/>).
/// </summary>
public sealed record ScreenCapture(
    string CaptureId,
    ScreenCaptureKind Kind,
    string StateId,
    string ContextId,
    string? TaskId,
    ScreenCaptureStatus Status,
    string? SnapshotId,
    string? ArtifactSha256,
    IReadOnlyList<string> EvidenceIds,
    string? Detail,
    DateTimeOffset CreatedAt)
{
    public static string IdOf(ScreenCaptureKind kind, string stateId, string contextId) =>
        ContentHash.ShortId("cap", ContentHash.Of("mapcue.capture/1", kind.ToString(), stateId, contextId));
}

/// <summary>Assistive technologies of the accessibility matrix (architecture §14.2).</summary>
public enum AccessibilityTechnology
{
    TalkBack,
    VoiceOver,
    Keyboard,
}

/// <summary>Result of a manual check by the operator.</summary>
public enum AccessibilityRecordResult
{
    Pass,
    Fail,
}

/// <summary>
/// A manual accessibility check of the operator (<c>mapcue a11y record</c>, protocol <c>docs/a11y-manual-protocol.md</c>): the only source of
/// the <c>assistiveVerified</c> level for screen readers. <see cref="ElementKey"/> null means the whole screen; <see cref="EvidenceId"/> is the
/// redacted evidence file the operator attached (required).
/// </summary>
public sealed record AccessibilityRecord(
    string RecordId,
    string ScreenId,
    string? StateId,
    string ContextId,
    AccessibilityTechnology Technology,
    string? ElementKey,
    string Property,
    AccessibilityRecordResult Result,
    string Operator,
    string Device,
    string ProtocolVersion,
    string? Note,
    string EvidenceId,
    DateTimeOffset RecordedAt);

/// <summary>A stored version of a screen contract: new only when <see cref="ContentHash"/> changes (architecture §6).</summary>
public sealed record ScreenContractVersion(
    string ContractId,
    string ScreenId,
    string GroupKey,
    int Version,
    string ContentHash,
    string ArtifactSha256,
    DateTimeOffset CreatedAt)
{
    public static string IdOf(string screenId, string groupKey, int version) =>
        MapCue.Domain.Graph.ContentHash.ShortId("sc", MapCue.Domain.Graph.ContentHash.Of("mapcue.screen-contract/1", screenId, groupKey, version.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

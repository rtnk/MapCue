using MapCue.Domain;

namespace MapCue.Application.Ports;

/// <summary>
/// Device automation port (architecture §4). Implemented by <c>MapCue.Adapters.TapCue</c>
/// through TapCue's public CLI/MCP only (ADR-0002). Every call honours hard timeouts and cancellation.
/// Actions are never retried by the adapter; retry decisions belong to the engine (architecture §10.1).
/// </summary>
public interface IDeviceAutomation
{
    Task<ToolVersion> GetVersionAsync(CancellationToken ct);

    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct);

    Task<RawObservation> ObserveAsync(DeviceId device, ObservationRequest request, CancellationToken ct);

    /// <summary>Executes exactly one validated action.</summary>
    Task<ActionExecution> ExecuteAsync(DeviceId device, ValidatedDeviceAction action, CancellationToken ct);

    Task<FlowRunResult> RunFlowAsync(DeviceId device, FlowFile flow, FlowRunOptions options, CancellationToken ct);

    Task<SyntaxCheckResult> CheckSyntaxAsync(FlowFile flow, CancellationToken ct);
}

public sealed record ToolVersion(string Name, string Version);

/// <summary>A device reported by the automation tool. Devices that are only available for launch have <see cref="IsConnected"/> false.</summary>
public sealed record DeviceInfo(DeviceId Id, Platform Platform, string? Name, bool IsEmulator, bool IsConnected = true);

/// <summary>Fast: MCP <c>inspect_screen</c>; Full: CLI <c>hierarchy</c> (ADR-0002).</summary>
public enum ObservationFidelity
{
    Fast,
    Full,
}

public sealed record ObservationRequest(string AppId, ObservationFidelity Fidelity);

/// <summary>
/// Raw element as reported by the automation tool, before normalization (STEP-03). Elements are listed in
/// pre-order; <see cref="Depth"/> is the nesting level in the reported tree (0 = top level). <see cref="ImportantForAccessibility"/> is
/// reported only by the full hierarchy on Android (null: unknown, e.g. MCP <c>inspect_screen</c> or iOS; STEP-08).
/// </summary>
public sealed record RawElement(
    string? ResourceId,
    string? Text,
    string? AccessibilityText,
    string? ClassName,
    bool Clickable,
    bool Enabled,
    string? Bounds = null,
    string? HintText = null,
    string? Value = null,
    bool Scrollable = false,
    bool Checked = false,
    bool Focused = false,
    bool Selected = false,
    int Depth = 0,
    bool? ImportantForAccessibility = null);

/// <summary>
/// A screen observation. <see cref="RawPayload"/> is the tool's payload after redaction (kept for evidence and
/// later analysis); <see cref="SnapshotHash"/> is a content hash of that payload, not a screen identity (STEP-03).
/// <see cref="ForegroundAppId"/> is null when the tool does not report it (TapCue 0.1.0 never does).
/// </summary>
public sealed record RawObservation(
    DeviceId Device,
    string? ForegroundAppId,
    IReadOnlyList<RawElement> Elements,
    string SnapshotHash,
    DateTimeOffset ObservedAt,
    ObservationFidelity Fidelity = ObservationFidelity.Fast,
    string? RawPayload = null);

/// <summary>
/// An action that already passed schema, stale, semantic and risk validation (ADR-0004).
/// Input values are only references (<c>profile.inputs.*</c>), never literals. <see cref="AppId"/> must be allowlisted.
/// </summary>
public sealed record ValidatedDeviceAction(
    string ActionId,
    string AppId,
    ActionType Type,
    RiskClass EffectiveRisk,
    string? TargetResourceId,
    string? TargetText,
    string? InputValueRef,
    ScrollDirection? Direction = null)
{
    /// <summary>Key of a <see cref="ActionType.PressKey"/> action (one of <see cref="DeviceKeys.Allowed"/>); null for other types.</summary>
    public string? Key { get; init; }
}

/// <summary>Why a device call did not succeed. Classified from tool output; unrecognised text is <see cref="Unknown"/>.</summary>
public enum DeviceErrorKind
{
    None,
    DeviceNotConnected,
    DeviceNotSupported,
    InvalidRequest,
    ToolNotFound,
    FlowSyntax,
    ElementNotFound,
    AssertionFailed,
    AppLaunchFailed,
    AppCrashed,
    DriverTimeout,

    /// <summary>The on-device driver connection of the TapCue session died (e.g. another TapCue client took the device, R-03); the session must be restarted.</summary>
    DriverLost,
    PolicyRejected,
    Timeout,
    TransportClosed,
    UnsupportedVersion,
    Unknown,
}

/// <summary>
/// Result of one action. <see cref="Dispatched"/> tells whether the request may have reached the device; an action
/// that was dispatched and then timed out or lost its transport is <see cref="ActionOutcome.OutcomeUnknown"/>.
/// </summary>
public sealed record ActionExecution(
    string ActionId,
    ActionOutcome Outcome,
    TimeSpan Duration,
    string? Detail,
    DeviceErrorKind ErrorKind = DeviceErrorKind.None,
    bool Dispatched = false);

/// <summary>A TapCue flow file on disk (generated or exported).</summary>
public sealed record FlowFile(string Path);

public sealed record FlowRunOptions(TimeSpan Timeout, string? TestOutputDirectory);

public sealed record FlowRunResult(
    ActionOutcome Outcome,
    TimeSpan Duration,
    string? Detail,
    DeviceErrorKind ErrorKind = DeviceErrorKind.None,
    string? TestOutputDirectory = null);

public sealed record SyntaxCheckResult(bool IsValid, IReadOnlyList<string> Errors);

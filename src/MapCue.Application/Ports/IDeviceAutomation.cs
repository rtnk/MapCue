using MapCue.Domain;

namespace MapCue.Application.Ports;

/// <summary>
/// Device automation port (architecture §4). Implemented by <c>MapCue.Adapters.TapCue</c> in STEP-02
/// through TapCue's public CLI/MCP only (ADR-0002). Every call honours hard timeouts and cancellation.
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

public sealed record DeviceInfo(DeviceId Id, Platform Platform, string? Name, bool IsEmulator);

/// <summary>Fast: MCP <c>inspect_screen</c>; Full: CLI <c>hierarchy</c> (ADR-0002).</summary>
public enum ObservationFidelity
{
    Fast,
    Full,
}

public sealed record ObservationRequest(string AppId, ObservationFidelity Fidelity);

/// <summary>Raw element as reported by the automation tool, before normalization (STEP-03).</summary>
public sealed record RawElement(
    string? ResourceId,
    string? Text,
    string? AccessibilityText,
    string? ClassName,
    bool Clickable,
    bool Enabled);

public sealed record RawObservation(
    DeviceId Device,
    string? ForegroundAppId,
    IReadOnlyList<RawElement> Elements,
    string SnapshotHash,
    DateTimeOffset ObservedAt);

/// <summary>
/// An action that already passed schema, stale, semantic and risk validation (ADR-0004).
/// Input values are only references (<c>profile.inputs.*</c>), never literals.
/// </summary>
public sealed record ValidatedDeviceAction(
    string ActionId,
    ActionType Type,
    RiskClass EffectiveRisk,
    string? TargetResourceId,
    string? TargetText,
    string? InputValueRef);

public sealed record ActionExecution(string ActionId, ActionOutcome Outcome, TimeSpan Duration, string? Detail);

/// <summary>A TapCue flow file on disk (generated or exported).</summary>
public sealed record FlowFile(string Path);

public sealed record FlowRunOptions(TimeSpan Timeout, string? TestOutputDirectory);

public sealed record FlowRunResult(ActionOutcome Outcome, TimeSpan Duration, string? Detail);

public sealed record SyntaxCheckResult(bool IsValid, IReadOnlyList<string> Errors);

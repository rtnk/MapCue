using System.Security.Cryptography;
using System.Text;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Testing;

/// <summary>A screen of the simulated application.</summary>
public sealed record FakeScreen(string Id, IReadOnlyList<FakeElement> Elements);

/// <summary>A tappable element. Tapping navigates to <see cref="TargetScreenId"/> when set.</summary>
public sealed record FakeElement(string ResourceId, string Text, string? TargetScreenId = null);

/// <summary>Deterministic model of an application: screens and tap transitions.</summary>
public sealed record FakeAppModel(string AppId, string StartScreenId, IReadOnlyList<FakeScreen> Screens)
{
    public FakeScreen Screen(string id) =>
        Screens.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"Unknown fake screen '{id}'.");
}

/// <summary>
/// Minimal in-memory <see cref="IDeviceAutomation"/> for tests (no device, no TapCue).
/// Supports tap navigation and back; other action types are rejected. Flow commands are not supported.
/// </summary>
public sealed class FakeDevice : IDeviceAutomation
{
    private readonly FakeAppModel _app;
    private readonly IClock _clock;
    private readonly Stack<string> _history = new();
    private readonly List<ValidatedDeviceAction> _executed = [];

    public FakeDevice(FakeAppModel app, IClock clock, string deviceId = "fake-emulator-1", Platform platform = Platform.Android)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(clock);
        _app = app;
        _clock = clock;
        Device = new DeviceInfo(new DeviceId(deviceId), platform, "FakeDevice", IsEmulator: true);
        CurrentScreenId = app.Screen(app.StartScreenId).Id;
    }

    public DeviceInfo Device { get; }

    public string CurrentScreenId { get; private set; }

    public IReadOnlyList<ValidatedDeviceAction> ExecutedActions => _executed;

    public Task<ToolVersion> GetVersionAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ToolVersion("FakeDevice", "1.0.0"));
    }

    public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DeviceInfo>>([Device]);
    }

    public Task<RawObservation> ObserveAsync(DeviceId device, ObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        EnsureDevice(device);
        var screen = _app.Screen(CurrentScreenId);
        var elements = screen.Elements
            .Select(e => new RawElement(e.ResourceId, e.Text, null, "android.widget.Button", Clickable: true, Enabled: true))
            .ToList();
        return Task.FromResult(new RawObservation(device, _app.AppId, elements, HashOf(screen), _clock.UtcNow));
    }

    public Task<ActionExecution> ExecuteAsync(DeviceId device, ValidatedDeviceAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        EnsureDevice(device);
        _executed.Add(action);

        switch (action.Type)
        {
            case ActionType.Tap:
                var element = _app.Screen(CurrentScreenId).Elements.FirstOrDefault(e => e.ResourceId == action.TargetResourceId);
                if (element is null)
                {
                    return Result(action, ActionOutcome.Failed, "element not found");
                }

                if (element.TargetScreenId is not null)
                {
                    _history.Push(CurrentScreenId);
                    CurrentScreenId = _app.Screen(element.TargetScreenId).Id;
                }

                return Result(action, ActionOutcome.Succeeded, null);
            case ActionType.Back:
                if (_history.Count > 0)
                {
                    CurrentScreenId = _history.Pop();
                }

                return Result(action, ActionOutcome.Succeeded, null);
            default:
                return Result(action, ActionOutcome.Rejected, $"{action.Type} is not supported by FakeDevice");
        }
    }

    public Task<FlowRunResult> RunFlowAsync(DeviceId device, FlowFile flow, FlowRunOptions options, CancellationToken ct) =>
        Task.FromResult(new FlowRunResult(ActionOutcome.Rejected, TimeSpan.Zero, "flows are not supported by FakeDevice"));

    public Task<SyntaxCheckResult> CheckSyntaxAsync(FlowFile flow, CancellationToken ct) =>
        Task.FromResult(new SyntaxCheckResult(false, ["flows are not supported by FakeDevice"]));

    private static Task<ActionExecution> Result(ValidatedDeviceAction action, ActionOutcome outcome, string? detail) =>
        Task.FromResult(new ActionExecution(action.ActionId, outcome, TimeSpan.Zero, detail));

    private static string HashOf(FakeScreen screen)
    {
        var canonical = new StringBuilder(screen.Id);
        foreach (var e in screen.Elements.OrderBy(e => e.ResourceId, StringComparer.Ordinal))
        {
            canonical.Append('\n').Append(e.ResourceId).Append('\t').Append(e.Text);
        }

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    private void EnsureDevice(DeviceId device)
    {
        if (device != Device.Id)
        {
            throw new InvalidOperationException($"FakeDevice '{Device.Id}' cannot serve device '{device}'.");
        }
    }
}

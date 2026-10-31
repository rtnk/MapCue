using System.Security.Cryptography;
using System.Text;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Testing;

/// <summary>How a tap moves through the navigation stack of the simulated app.</summary>
public enum FakeNavigation
{
    /// <summary>Opens the target on top of the current screen (back returns).</summary>
    Push,

    /// <summary>Replaces the current screen in place (e.g. a tab bar swapping content; back does not return).</summary>
    Replace,

    /// <summary>Closes the current screen (like back; the target is ignored).</summary>
    Pop,

    /// <summary>Clears the stack and shows the target as the root.</summary>
    Root,
}

/// <summary>
/// A screen of the simulated application. <see cref="Package"/> overrides the app package (e.g. the launcher). A screen with
/// <see cref="AdvanceAfterObservations"/> is transient (loading): after that many observations it becomes <see cref="AdvanceTo"/>.
/// <see cref="IgnoresBack"/> models a popup that cannot be dismissed with back.
/// </summary>
public sealed record FakeScreen(string Id, IReadOnlyList<FakeElement> Elements)
{
    public string? Package { get; init; }

    public int AdvanceAfterObservations { get; init; }

    public string? AdvanceTo { get; init; }

    public bool IgnoresBack { get; init; }
}

/// <summary>
/// An element. Tapping navigates to <see cref="TargetScreenId"/> when set (how: <see cref="Navigation"/>); <see cref="Navigation"/>
/// <see cref="FakeNavigation.Pop"/> closes the screen. Resource ids are reported as written (an empty one as none, like a MAUI list row
/// without <c>AutomationId</c>). <see cref="Depth"/> nests elements (pre-order, like TapCue reports them): rows at depth 1 after a
/// <see cref="Scrollable"/> container at depth 0 form a list.
/// </summary>
public sealed record FakeElement(string ResourceId, string Text, string? TargetScreenId = null)
{
    public FakeNavigation Navigation { get; init; } = FakeNavigation.Push;

    public string ClassName { get; init; } = "android.widget.Button";

    public bool Clickable { get; init; } = true;

    public string? AccessibilityText { get; init; }

    public int Depth { get; init; }

    public bool Scrollable { get; init; }
}

/// <summary>
/// Deterministic model of an application: screens and tap transitions. <see cref="LauncherScreenId"/> is shown when back leaves the
/// root screen (the app is left); <see cref="SetupScreenId"/> is where a setup flow (<see cref="FakeDevice.RunFlowAsync"/>) lands.
/// </summary>
public sealed record FakeAppModel(string AppId, string StartScreenId, IReadOnlyList<FakeScreen> Screens)
{
    public string? LauncherScreenId { get; init; }

    public string? SetupScreenId { get; init; }

    /// <summary>Setup screen by flow file name (e.g. <c>setup-investor.yaml</c>); other flows land on <see cref="SetupScreenId"/>.</summary>
    public IReadOnlyDictionary<string, string> SetupScreens { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public FakeScreen Screen(string id) =>
        Screens.FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException($"Unknown fake screen '{id}'.");
}

/// <summary>
/// In-memory <see cref="IDeviceAutomation"/> for tests (no device, no TapCue): tap navigation (push, replace, pop, root), back
/// (leaves the app from the root screen when the model has a launcher), <c>launchApp</c> (fresh start), setup flows (land on
/// <see cref="FakeAppModel.SetupScreenId"/>) and transient loading screens. Other action types are rejected.
/// Elements are matched by resource id, else by exact text or accessibility text, like the TapCue selectors MapCue generates.
/// </summary>
public sealed class FakeDevice : IDeviceAutomation
{
    private readonly FakeAppModel _app;
    private readonly IClock _clock;
    private readonly Stack<string> _history = new();
    private readonly List<ValidatedDeviceAction> _executed = [];
    private readonly List<string> _flows = [];
    private int _observationsOnScreen;

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

    /// <summary>Paths of the setup flows run so far.</summary>
    public IReadOnlyList<string> FlowsRun => _flows;

    /// <summary>Number of observations served.</summary>
    public int Observations { get; private set; }

    /// <summary>When set, <see cref="ExecuteAsync"/> returns this outcome for actions on that resource id (fault injection).</summary>
    public Dictionary<string, ActionOutcome> Faults { get; } = new(StringComparer.Ordinal);

    /// <summary>Resource ids whose elements are neither reported nor tappable (e.g. removed by the app after an analysis).</summary>
    public HashSet<string> HiddenElements { get; } = new(StringComparer.Ordinal);

    /// <summary>After a successful action on this resource id, the next observation fails once (device lost mid-attempt).</summary>
    public string? FailObservationAfter { get; set; }

    private bool _failNextObservation;

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
        if (_failNextObservation)
        {
            _failNextObservation = false;
            throw new InvalidOperationException("injected observation failure");
        }

        Observations++;
        var screen = _app.Screen(CurrentScreenId);
        if (screen.AdvanceTo is not null && ++_observationsOnScreen > screen.AdvanceAfterObservations)
        {
            Show(screen.AdvanceTo);
            screen = _app.Screen(CurrentScreenId);
        }

        var elements = screen.Elements
            .Where(e => !HiddenElements.Contains(e.ResourceId))
            .Select(e => new RawElement(string.IsNullOrEmpty(e.ResourceId) ? null : e.ResourceId, e.Text, e.AccessibilityText, e.ClassName, Clickable: e.Clickable, Enabled: true, Scrollable: e.Scrollable, Depth: e.Depth))
            .ToList();
        return Task.FromResult(new RawObservation(device, screen.Package ?? _app.AppId, elements, HashOf(screen), _clock.UtcNow));
    }

    public Task<ActionExecution> ExecuteAsync(DeviceId device, ValidatedDeviceAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        EnsureDevice(device);
        _executed.Add(action);
        if (action.TargetResourceId is { } faulty && Faults.TryGetValue(faulty, out var fault))
        {
            return Result(action, fault, "injected fault", dispatched: fault is ActionOutcome.OutcomeUnknown);
        }

        switch (action.Type)
        {
            case ActionType.Tap:
                var element = _app.Screen(CurrentScreenId).Elements.FirstOrDefault(e => e.Clickable && !HiddenElements.Contains(e.ResourceId) && Matches(e, action));
                if (element is null)
                {
                    return Result(action, ActionOutcome.Failed, "element not found");
                }

                Navigate(element);
                _failNextObservation = element.ResourceId == FailObservationAfter;
                return Result(action, ActionOutcome.Succeeded, null);
            case ActionType.Back:
                if (_app.Screen(CurrentScreenId).IgnoresBack)
                {
                    return Result(action, ActionOutcome.Succeeded, null);
                }

                if (_history.Count > 0)
                {
                    Show(_history.Pop());
                }
                else if (_app.LauncherScreenId is { } launcher)
                {
                    Show(launcher);
                }

                return Result(action, ActionOutcome.Succeeded, null);
            case ActionType.LaunchApp:
                _history.Clear();
                Show(_app.StartScreenId);
                return Result(action, ActionOutcome.Succeeded, null);
            default:
                return Result(action, ActionOutcome.Rejected, $"{action.Type} is not supported by FakeDevice");
        }
    }

    /// <summary>A setup flow: a fresh start that lands on the flow's setup screen (<see cref="FakeAppModel.SetupScreens"/>, else <see cref="FakeAppModel.SetupScreenId"/>).</summary>
    public Task<FlowRunResult> RunFlowAsync(DeviceId device, FlowFile flow, FlowRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ct.ThrowIfCancellationRequested();
        EnsureDevice(device);
        var setup = _app.SetupScreens.TryGetValue(Path.GetFileName(flow.Path), out var byFlow) ? byFlow : _app.SetupScreenId;
        if (setup is null)
        {
            return Task.FromResult(new FlowRunResult(ActionOutcome.Rejected, TimeSpan.Zero, "flows are not supported by this fake app"));
        }

        _flows.Add(flow.Path);
        _history.Clear();
        Show(setup);
        return Task.FromResult(new FlowRunResult(ActionOutcome.Succeeded, TimeSpan.Zero, null));
    }

    public Task<SyntaxCheckResult> CheckSyntaxAsync(FlowFile flow, CancellationToken ct) =>
        Task.FromResult(new SyntaxCheckResult(false, ["flows are not supported by FakeDevice"]));

    private static bool Matches(FakeElement element, ValidatedDeviceAction action) =>
        !string.IsNullOrEmpty(action.TargetResourceId)
            ? element.ResourceId == action.TargetResourceId && (action.TargetText is null || TextMatches(element, action.TargetText))
            : action.TargetText is { } text && TextMatches(element, text);

    // Like a Maestro text selector: the visible text or the accessibility text.
    private static bool TextMatches(FakeElement element, string text) => element.Text == text || element.AccessibilityText == text;

    private void Navigate(FakeElement element)
    {
        switch (element.Navigation)
        {
            case FakeNavigation.Pop:
                if (_history.Count > 0)
                {
                    Show(_history.Pop());
                }

                break;
            case FakeNavigation.Root when element.TargetScreenId is not null:
                _history.Clear();
                Show(element.TargetScreenId);
                break;
            case FakeNavigation.Replace when element.TargetScreenId is not null:
                Show(element.TargetScreenId);
                break;
            case FakeNavigation.Push when element.TargetScreenId is not null:
                _history.Push(CurrentScreenId);
                Show(element.TargetScreenId);
                break;
        }
    }

    private void Show(string screenId)
    {
        CurrentScreenId = _app.Screen(screenId).Id;
        _observationsOnScreen = 0;
    }

    private static Task<ActionExecution> Result(ValidatedDeviceAction action, ActionOutcome outcome, string? detail, bool dispatched = true) =>
        Task.FromResult(new ActionExecution(action.ActionId, outcome, TimeSpan.Zero, detail, Dispatched: dispatched && outcome != ActionOutcome.Rejected));

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

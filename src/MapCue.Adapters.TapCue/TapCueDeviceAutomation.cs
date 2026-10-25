using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MapCue.Adapters.TapCue.Cli;
using MapCue.Adapters.TapCue.Flows;
using MapCue.Adapters.TapCue.Mcp;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Adapters.TapCue.Processes;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MapCue.Adapters.TapCue;

/// <summary>
/// <see cref="IDeviceAutomation"/> over TapCue's public MCP and CLI (ADR-0002, option C).
/// <list type="bullet">
/// <item>Fast observations, device listing and actions go through one long-lived <c>tapcue mcp</c> child process.</item>
/// <item>Full observations, replay and syntax checks use the CLI (a new JVM per call).</item>
/// <item>All device calls are serialized (TapCue has no device lock, G-09) and bounded by hard timeouts; a timeout
/// kills the process tree and the next call starts a fresh session (G-01).</item>
/// <item>Actions are never retried here. A dispatched action whose result is lost is <see cref="ActionOutcome.OutcomeUnknown"/>.
/// Read-only calls are retried after a session restart at most <see cref="Budgets.MaxRetries"/> times.</item>
/// <item>TapCue runs with a clean environment (no <c>MAESTRO_*</c>), and tool output is stripped of
/// <c>env_vars</c> values and redacted before it leaves the adapter (G-06/R-13).</item>
/// </list>
/// </summary>
public sealed class TapCueDeviceAutomation : IDeviceAutomation, IAsyncDisposable
{
    private readonly TapCueAdapterOptions _options;
    private readonly IRedactor _redactor;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Func<string, string?> _parentEnvironment;
    private readonly Func<string, string?>? _resolveInput;
    private readonly IMcpTranscriptSink? _transcript;
    private readonly FlowYamlGenerator _generator;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _checkedShapes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warmDevices = new(StringComparer.Ordinal);
    private TapCueMcpSession? _session;
    private TapCueCliRunner? _cli;
    private TapCueVersionStatus? _versionStatus;
    private int _flowCounter;

    /// <param name="options">Adapter options derived from the validated configuration.</param>
    /// <param name="redactor">Redacts every string that leaves the adapter (details, payloads, logs).</param>
    /// <param name="clock">Timestamps for observations.</param>
    /// <param name="logger">Optional logger; messages are redacted before logging.</param>
    /// <param name="resolveInput">Resolves <c>InputValueRef</c> to a value at execution time (profile inputs, STEP-07).</param>
    /// <param name="transcript">Optional sink for raw MCP lines (fixtures, evidence); it must redact before persisting.</param>
    /// <param name="parentEnvironment">Source of allowlisted parent variables; defaults to the current process.</param>
    public TapCueDeviceAutomation(
        TapCueAdapterOptions options,
        IRedactor redactor,
        IClock clock,
        ILogger? logger = null,
        Func<string, string?>? resolveInput = null,
        IMcpTranscriptSink? transcript = null,
        Func<string, string?>? parentEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(clock);
        _options = options;
        _redactor = redactor;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
        _resolveInput = resolveInput;
        _transcript = transcript;
        _parentEnvironment = parentEnvironment ?? Environment.GetEnvironmentVariable;
        _generator = new FlowYamlGenerator(options.AllowedAppIds, options.Budgets.UiSettleTimeout);
    }

    /// <summary>Environment passed to every TapCue process (names and values; values must not be logged).</summary>
    public IReadOnlyDictionary<string, string> ChildEnvironment => ProcessEnvironment.Build(_options.TapCue.PassEnvironment, _parentEnvironment);

    /// <summary>Server identity of the current MCP session, if any.</summary>
    public McpServerInfo? ServerInfo => _session?.ServerInfo;

    /// <summary>Number of MCP sessions started so far (restarts included).</summary>
    public int SessionStarts { get; private set; }

    /// <summary>Result of the version preflight, once performed.</summary>
    public TapCueVersionStatus? VersionStatus => _versionStatus;

    public async Task<ToolVersion> GetVersionAsync(CancellationToken ct)
    {
        var result = await Cli().VersionAsync(_options.TapCue.StartupTimeout, ct).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TapCueException(DeviceErrorKind.Timeout, "tapcue --version timed out.");
        }

        if (result.ExitCode != 0)
        {
            throw new TapCueException(DeviceErrorKind.Unknown, "tapcue --version failed: " + Redact(string.Join(" ", TapCueCliRunner.ErrorLines(result))));
        }

        return TapCueParsers.ParseVersion(result.StandardOutput);
    }

    /// <summary>Reads the TapCue version and applies the version policy. Throws for an unsupported version.</summary>
    public async Task<(ToolVersion Version, TapCueVersionStatus Status)> PreflightAsync(CancellationToken ct)
    {
        var version = await GetVersionAsync(ct).ConfigureAwait(false);
        var status = TapCueVersionPolicy.Evaluate(version, _options.TapCue);
        _versionStatus = status;
        if (status == TapCueVersionStatus.Unsupported)
        {
            throw new TapCueException(DeviceErrorKind.UnsupportedVersion, $"TapCue {version.Version} is not in tapcue.supportedVersions ({string.Join(", ", _options.TapCue.SupportedVersions)}); set tapcue.allowUntestedVersion to override.");
        }

        if (status == TapCueVersionStatus.Untested)
        {
            _logger.LogWarning("TapCue {Version} is untested; the run is flagged untestedAdapter", version.Version);
        }

        return (version, status);
    }

    /// <summary>Starts (or reuses) the MCP session and returns the advertised tools. Used by <c>mapcue doctor</c>.</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ProbeMcpAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            return session.Tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Read-only probe of the current screen for diagnostics (<c>mapcue doctor --device</c>): checks the device
    /// allowlist only, because it does not act on or attribute the screen to any app. Returns the element count.
    /// </summary>
    public async Task<int> ProbeScreenAsync(DeviceId device, CancellationToken ct)
    {
        if (!_options.IsDeviceAllowed(device.Value))
        {
            throw new TapCueException(DeviceErrorKind.PolicyRejected, "device is not in the allowlist");
        }

        var payload = await ReadOnlyToolAsync("inspect_screen", new JsonObject { ["device_id"] = device.Value }, device, _options.Budgets.ObserveTimeout, ct).ConfigureAwait(false);
        return TapCueParsers.ParseInspectScreen(payload).Count;
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct)
    {
        var text = await ReadOnlyToolAsync("list_devices", [], null, _options.Budgets.ObserveTimeout, ct).ConfigureAwait(false);
        return TapCueParsers.ParseDevices(text);
    }

    public async Task<RawObservation> ObserveAsync(DeviceId device, ObservationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureAllowed(device, request.AppId);
        if (request.Fidelity == ObservationFidelity.Fast)
        {
            var payload = await ReadOnlyToolAsync("inspect_screen", new JsonObject { ["device_id"] = device.Value }, device, _options.Budgets.ObserveTimeout, ct).ConfigureAwait(false);
            var elements = TapCueParsers.ParseInspectScreen(payload);
            return Observation(device, elements, payload, ObservationFidelity.Fast);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.TapCue.FullObservation == FullObservationMode.Exclusive)
            {
                await CloseSessionAsync().ConfigureAwait(false);
            }

            var timeout = Max(_options.Budgets.ObserveTimeout, _options.TapCue.StartupTimeout);
            var result = await Cli().HierarchyAsync(device, timeout, ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                throw new TapCueException(DeviceErrorKind.Timeout, $"tapcue hierarchy did not finish within {timeout.TotalSeconds:0.#} s; the process tree was killed.");
            }

            if (result.ExitCode != 0)
            {
                var error = string.Join(" ", TapCueCliRunner.ErrorLines(result));
                throw new TapCueException(TapCueErrorClassifier.Classify(error), "tapcue hierarchy failed: " + Redact(error));
            }

            var elements = TapCueParsers.ParseHierarchy(result.StandardOutput, out var json);
            return Observation(device, elements, json, ObservationFidelity.Full);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ActionExecution> ExecuteAsync(DeviceId device, ValidatedDeviceAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        var stopwatch = Stopwatch.StartNew();
        ActionExecution Done(ActionOutcome outcome, string? detail, DeviceErrorKind kind, bool dispatched) =>
            new(action.ActionId, outcome, stopwatch.Elapsed, detail is null ? null : Redact(detail), kind, dispatched);

        if (!_options.IsDeviceAllowed(device.Value) || !_options.IsAppAllowed(action.AppId))
        {
            return Done(ActionOutcome.Rejected, "device or app is not in the allowlist", DeviceErrorKind.PolicyRejected, false);
        }

        GeneratedFlow flow;
        try
        {
            flow = _generator.Generate(action, _resolveInput);
        }
        catch (FlowGenerationException ex)
        {
            return Done(ActionOutcome.Rejected, ex.Message, DeviceErrorKind.PolicyRejected, false);
        }

        foreach (var value in flow.Environment.Values)
        {
            _redactor.RegisterSensitiveValue(value);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.TapCue.CheckGeneratedSyntax && !_checkedShapes.Contains(flow.ShapeKey))
            {
                var check = await CheckSyntaxTextAsync(flow.Yaml, ct).ConfigureAwait(false);
                if (!check.IsValid)
                {
                    return Done(ActionOutcome.Rejected, "generated flow failed check-syntax: " + string.Join("; ", check.Errors), DeviceErrorKind.FlowSyntax, false);
                }

                _checkedShapes.Add(flow.ShapeKey);
            }

            TapCueMcpSession session;
            try
            {
                session = await EnsureSessionAsync(ct).ConfigureAwait(false);
            }
            catch (TapCueException ex)
            {
                return Done(ex.Kind == DeviceErrorKind.Timeout ? ActionOutcome.TimedOut : ActionOutcome.Failed, "not dispatched: " + ex.Message, ex.Kind, false);
            }

            var arguments = new JsonObject { ["device_id"] = device.Value, ["yaml"] = flow.Yaml };
            if (flow.Environment.Count > 0)
            {
                arguments["env"] = new JsonObject(flow.Environment.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            }

            McpToolResult result;
            try
            {
                result = await session.CallToolAsync("run", arguments, CallTimeout(device, _options.Budgets.ActionTimeout), ct).ConfigureAwait(false);
            }
            catch (TapCueException ex) when (ex.Kind == DeviceErrorKind.InvalidRequest)
            {
                // JSON-RPC level rejection of the call: the server did not run the flow and the session is intact.
                return Done(ActionOutcome.Failed, ex.Message, ex.Kind, false);
            }
            catch (TapCueException ex)
            {
                await CloseSessionAsync().ConfigureAwait(false);
                return ex.Dispatched
                    ? Done(ActionOutcome.OutcomeUnknown, ex.Message, ex.Kind, true)
                    : Done(ActionOutcome.Failed, "not dispatched: " + ex.Message, ex.Kind, false);
            }
            catch (OperationCanceledException)
            {
                // Cancelled while TapCue was running the flow: the effect is unknown. The session was killed.
                await CloseSessionAsync().ConfigureAwait(false);
                throw;
            }

            var text = TapCueParsers.StripEnvValues(result.Text);
            if (!result.IsError)
            {
                _warmDevices.Add(device.Value);
                return Done(ActionOutcome.Succeeded, text, DeviceErrorKind.None, true);
            }

            var (kind, reached) = TapCueErrorClassifier.ClassifyDetailed(text);
            if (!reached)
            {
                return Done(ActionOutcome.Failed, text, kind, false);
            }

            _warmDevices.Add(device.Value);
            var mayHaveChangedState = kind == DeviceErrorKind.Unknown && action.EffectiveRisk is not (RiskClass.Safe or RiskClass.Navigational);
            return Done(mayHaveChangedState ? ActionOutcome.OutcomeUnknown : ActionOutcome.Failed, text, kind, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<FlowRunResult> RunFlowAsync(DeviceId device, FlowFile flow, FlowRunOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(options);
        var stopwatch = Stopwatch.StartNew();
        FlowRunResult Done(ActionOutcome outcome, string? detail, DeviceErrorKind kind, string? dir = null) =>
            new(outcome, stopwatch.Elapsed, detail is null ? null : Redact(detail), kind, dir);

        if (!_options.IsDeviceAllowed(device.Value))
        {
            return Done(ActionOutcome.Rejected, "device is not in the allowlist", DeviceErrorKind.PolicyRejected);
        }

        string yaml;
        try
        {
            var info = new FileInfo(flow.Path);
            if (!info.Exists || info.Length > FlowGuard.MaxFlowBytes)
            {
                return Done(ActionOutcome.Rejected, "flow file is missing or too large", DeviceErrorKind.InvalidRequest);
            }

            yaml = await File.ReadAllTextAsync(info.FullName, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Done(ActionOutcome.Rejected, "flow file cannot be read", DeviceErrorKind.InvalidRequest);
        }

        var problems = FlowGuard.Check(yaml);
        var appId = FlowGuard.ResolveAppId(yaml);
        if (problems.Count > 0 || appId is null || !_options.IsAppAllowed(appId))
        {
            var reason = problems.Count > 0 ? string.Join("; ", problems) : "flow appId is missing, unresolvable or not in the allowlist";
            return Done(ActionOutcome.Rejected, reason, DeviceErrorKind.PolicyRejected);
        }

        var outputDir = Path.GetFullPath(options.TestOutputDirectory ?? Path.Combine(_options.WorkDirectory, "test-output", "run-" + _clock.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture)));
        Directory.CreateDirectory(outputDir);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_options.TapCue.FullObservation == FullObservationMode.Exclusive)
            {
                await CloseSessionAsync().ConfigureAwait(false);
            }

            var result = await Cli().TestAsync(device, Path.GetFullPath(flow.Path), outputDir, options.Timeout, ct).ConfigureAwait(false);
            if (result.TimedOut)
            {
                return Done(ActionOutcome.OutcomeUnknown, $"tapcue test did not finish within {options.Timeout.TotalSeconds:0.#} s; the process tree was killed.", DeviceErrorKind.Timeout, outputDir);
            }

            if (result.ExitCode == 0)
            {
                return Done(ActionOutcome.Succeeded, null, DeviceErrorKind.None, outputDir);
            }

            var error = string.Join(" ", TapCueCliRunner.ErrorLines(result));
            return Done(ActionOutcome.Failed, error, TapCueErrorClassifier.Classify(error), outputDir);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SyntaxCheckResult> CheckSyntaxAsync(FlowFile flow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var result = await Cli().CheckSyntaxAsync(Path.GetFullPath(flow.Path), _options.TapCue.StartupTimeout, ct).ConfigureAwait(false);
        if (result.TimedOut)
        {
            throw new TapCueException(DeviceErrorKind.Timeout, "tapcue check-syntax timed out.");
        }

        return result.ExitCode == 0
            ? new SyntaxCheckResult(true, [])
            : new SyntaxCheckResult(false, TapCueCliRunner.ErrorLines(result).Select(Redact).ToList());
    }

    public async ValueTask DisposeAsync()
    {
        await CloseSessionAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task<SyntaxCheckResult> CheckSyntaxTextAsync(string yaml, CancellationToken ct)
    {
        Directory.CreateDirectory(_options.WorkDirectory);
        var path = Path.Combine(_options.WorkDirectory, $"check-{Environment.ProcessId}-{Interlocked.Increment(ref _flowCounter)}.yaml");
        await File.WriteAllTextAsync(path, yaml, new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            return await CheckSyntaxAsync(new FlowFile(path), ct).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Read-only tool call with session restart and bounded infrastructure retries (safe to repeat).
    private async Task<string> ReadOnlyToolAsync(string tool, JsonObject arguments, DeviceId? device, TimeSpan budget, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var session = await EnsureSessionAsync(ct).ConfigureAwait(false);
                    var timeout = device is { } d ? CallTimeout(d, budget) : budget;
                    var result = await session.CallToolAsync(tool, (JsonObject)arguments.DeepClone(), timeout, ct).ConfigureAwait(false);
                    if (result.IsError)
                    {
                        throw new TapCueException(TapCueErrorClassifier.Classify(result.Text), Redact(result.Text), dispatched: true);
                    }

                    if (device is { } warm)
                    {
                        _warmDevices.Add(warm.Value);
                    }

                    return result.Text;
                }
                catch (TapCueException ex) when (ex.Kind is DeviceErrorKind.Timeout or DeviceErrorKind.TransportClosed && attempt < _options.Budgets.MaxRetries)
                {
                    _logger.LogWarning("TapCue {Tool} failed ({Kind}); restarting the MCP session (retry {Attempt})", tool, ex.Kind, attempt + 1);
                    await CloseSessionAsync().ConfigureAwait(false);
                }
                catch (TapCueException ex) when (ex.Kind is DeviceErrorKind.Timeout or DeviceErrorKind.TransportClosed)
                {
                    await CloseSessionAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Caller holds _gate.
    private async Task<TapCueMcpSession> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is { IsBroken: false } live)
        {
            return live;
        }

        await CloseSessionAsync().ConfigureAwait(false);
        if (_versionStatus is null or TapCueVersionStatus.Unsupported)
        {
            // Throws for an unsupported version, so no session is ever started with it.
            await PreflightAsync(ct).ConfigureAwait(false);
        }

        var spec = new ProcessSpec(ExecutablePath(), ["mcp"], ChildEnvironment, WorkingDirectory());
        SessionStarts++;
        _session = await TapCueMcpSession.StartAsync(spec, _options.TapCue.StartupTimeout, _options.ClientVersion, _transcript, ct).ConfigureAwait(false);
        _logger.LogInformation("TapCue MCP session {Count} started (pid {Pid})", SessionStarts, _session.ProcessId);
        return _session;
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        _warmDevices.Clear();
        if (session is not null)
        {
            if (session.IsBroken && session.StderrTail.Length > 0)
            {
                _logger.LogDebug("TapCue MCP stderr tail: {Tail}", Redact(Tail(session.StderrTail, 2000)));
            }

            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The first call on a device in a fresh session installs TapCue's driver; it gets the startup budget on top.
    private TimeSpan CallTimeout(DeviceId device, TimeSpan budget) =>
        _warmDevices.Contains(device.Value) ? budget : budget + _options.TapCue.StartupTimeout;

    private RawObservation Observation(DeviceId device, IReadOnlyList<RawElement> elements, string payload, ObservationFidelity fidelity)
    {
        // Elements stay as reported (selectors must match the screen); the payload kept as evidence is redacted.
        var redacted = Redact(payload);
        return new RawObservation(device, null, elements, TapCueParsers.HashPayload(redacted), _clock.UtcNow, fidelity, redacted);
    }

    private void EnsureAllowed(DeviceId device, string appId)
    {
        if (!_options.IsDeviceAllowed(device.Value) || !_options.IsAppAllowed(appId))
        {
            throw new TapCueException(DeviceErrorKind.PolicyRejected, "device or app is not in the allowlist");
        }
    }

    private TapCueCliRunner Cli() => _cli ??= new TapCueCliRunner(ExecutablePath(), ChildEnvironment, WorkingDirectory());

    private string ExecutablePath() =>
        ExecutableResolver.Resolve(_options.TapCue.Executable, _parentEnvironment("PATH"))
        ?? throw new TapCueException(DeviceErrorKind.ToolNotFound, $"TapCue executable '{_options.TapCue.Executable}' was not found (tapcue.executable or PATH).");

    private string WorkingDirectory()
    {
        var dir = Path.GetFullPath(_options.WorkDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string Redact(string text) => _redactor.Redact(text, new RedactionContext("tapcue")).Text;

    private static string Tail(string text, int max) => text.Length <= max ? text : text[^max..];

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

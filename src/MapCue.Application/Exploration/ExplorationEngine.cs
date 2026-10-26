using System.Globalization;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>Event types of runs written to the outbox by <see cref="ExplorationEngine"/>.</summary>
public static class RunEventTypes
{
    public const string RunStarted = "run.started";
    public const string RunFinished = "run.finished";
    public const string AppReset = "run.app-reset";
    public const string AppLeft = "attempt.app-left";
    public const string TaskBlocked = "task.blocked";
    public const string TaskFailed = "task.failed";
}

/// <summary>
/// The vertical slice of exploration (STEP-05, architecture §5): <c>Created → Preflight → Running → Finished(reason)</c>.
/// <list type="bullet">
/// <item><b>Preflight:</b> free space, the device (allowlisted, connected, of the profile's platform, or the given one), the setup
/// flow file, a reset into the profile's start state with the app in the foreground; the start state is committed.</item>
/// <item><b>Loop:</b> the next task of the frontier (SQLite, no leases: STEP-06). <c>AnalyzeState</c> runs the reasoner on the
/// stored snapshot (<see cref="AgentInterpreter"/>, no device) and schedules accepted actions as <c>ExploreAction</c> tasks.
/// <c>ExploreAction</c> moves the device to the source state (known edges, else a reset), takes a fresh pre-snapshot, applies
/// <see cref="PreExecutionGuard"/>, journals <c>ActionDispatched</c>, executes exactly one action, stabilizes the UI and commits the
/// attempt in one transaction (<see cref="GraphCommitter"/>).</item>
/// <item><b>Selection:</b> analyses first, then the explore task closest to the current state over known edges (locality, DFS without
/// replay), ties in store order (priority, depth, creation); back actions have a lower priority so a state is explored before it is
/// left. Tasks whose path from the start state would be longer than <c>maxDepth</c> are cancelled.</item>
/// <item><b>Leaving the app</b> (e.g. <c>back</c> on a root screen): the edge is <c>executed</c> without a target state, the screen
/// outside the app is kept as attempt evidence and the next task resets the app.</item>
/// <item><b>Outcomes:</b> <c>OutcomeUnknown</c> is never retried (task status <c>OutcomeUnknown</c>, device state unknown, reset
/// next); failed actions fail their task.</item>
/// <item><b>Finish:</b> <c>Exhausted</c> (no pending task), <c>AllRemainingBlocked</c>, <c>BudgetExhausted</c> (actions, duration,
/// states, screens), <c>StoppedByOperator</c> (cancellation), <c>PreflightFailed</c>, <c>Failed</c>; a checkpoint is written.</item>
/// </list>
/// Attempt artifacts go to <c>runs/&lt;runId&gt;/tasks/&lt;taskId&gt;/attempts/&lt;n&gt;/attempt.json</c> (links to redacted blobs).
/// </summary>
public sealed class ExplorationEngine
{
    public const int BackPriority = -1;

    private IDeviceAutomation Device { get; }
    private IStateStore Store { get; }
    private IArtifactStore Artifacts { get; }
    private IRedactor Redactor { get; }
    private IExplorationReasoner Reasoner { get; }
    private MapCueSettings Settings { get; }
    private IClock Clock { get; }
    private IIdGenerator Ids { get; }

    public ExplorationEngine(IDeviceAutomation device, IStateStore store, IArtifactStore artifacts, IRedactor redactor, IExplorationReasoner reasoner, MapCueSettings settings, IClock clock, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(reasoner);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ids);
        Device = device;
        Store = store;
        Artifacts = artifacts;
        Redactor = redactor;
        Reasoner = reasoner;
        Settings = settings;
        Clock = clock;
        Ids = ids;
    }

    private static readonly RedactionContext _console = new("console");

    public async Task<ExplorationResult> RunAsync(ExplorationOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = new RunSession(this, options);
        return await run.ExecuteAsync(ct);
    }

    private string Redact(string text) => Redactor.Redact(text, _console).Text;

    /// <summary>Stops the loop when a run budget is spent.</summary>
    private sealed class BudgetStopException(string message) : Exception(message);

    /// <summary>The state of one run: device, context, current state and counters.</summary>
    private sealed class RunSession
    {
        private readonly ExplorationEngine _e;
        private readonly ExplorationOptions _o;
        private readonly ProfileSettings _profile;
        private readonly SnapshotRecorder _recorder;
        private readonly GraphCommitter _committer;
        private readonly ScreenIdentityResolver _resolver;
        private readonly AgentResponseValidator _validator;
        private readonly AgentPolicy _policy;
        private readonly UiStabilizer _stabilizer;
        private readonly NormalizationOptions _normalization;
        private readonly DateTimeOffset _started;
        private ExplorationRun _run;
        private RunCounters _counters = RunCounters.Zero;
        private ExplorationContext? _context;
        private AgentInterpreter? _interpreter;
        private DeviceId _device;
        private string? _current;
        private int _resets;
        private int _consecutiveDeviceFailures;

        public RunSession(ExplorationEngine engine, ExplorationOptions options)
        {
            _e = engine;
            _o = options;
            _profile = options.Profile;
            _started = engine.Clock.UtcNow;
            _resolver = new ScreenIdentityResolver(IdentityResolverOptions.Default with { ScreenHints = engine.Settings.Identity.ScreenHints });
            _recorder = new SnapshotRecorder(engine.Artifacts, engine.Redactor, engine.Clock);
            _committer = new GraphCommitter(engine.Store, engine.Clock, engine.Ids, _resolver);
            _policy = AgentPolicy.For(_profile);
            _validator = new AgentResponseValidator(new RiskClassifier(engine.Settings.Policy.RiskRules), new PolicyGate(engine.Settings.Allowlist));
            // At least two observations fit in the settle timeout, whatever the configured poll interval.
            var settle = engine.Settings.Budgets.UiSettleTimeout;
            var poll = options.PollInterval < settle / 2 ? options.PollInterval : settle / 2;
            _stabilizer = new UiStabilizer(engine.Device, engine.Clock, settle, poll, options.Delay);
            _normalization = new NormalizationOptions(_profile.Platform, options.InputValues);
            _run = new ExplorationRun(engine.Ids.NewId("run"), _profile.ProfileId, null, null, engine.Reasoner.Name, options.ConfigHash, options.Seed, RunStatus.Created, null, null, RunCounters.Zero, _started, null);
        }

        private string RunDirectory => Path.Combine(_o.RunsDirectory, _run.RunId);

        public async Task<ExplorationResult> ExecuteAsync(CancellationToken ct)
        {
            // Budget usage of earlier runs counts too: the reasoner budget is per workspace until runs own it (STEP-06).
            var used = await _e.Store.InTransactionAsync(uow => uow.AgentCalls.GetUsageTotalsAsync(ct), ct);
            _interpreter = new AgentInterpreter(_e.Reasoner, _validator, new AgentBudget(_e.Settings.Agent, used), _e.Artifacts, _e.Store, _e.Clock);
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Runs.InsertAsync(_run, ct);
                    return 0;
                },
                ct);
            Directory.CreateDirectory(RunDirectory);
            Progress($"run        {_run.RunId} profile={_profile.ProfileId} reasoner={_e.Reasoner.Name}");

            RunFinishReason reason;
            string? detail;
            try
            {
                await SetStatusAsync(RunStatus.Preflight, ct);
                var preflight = await PreflightAsync(ct);
                if (preflight is not null)
                {
                    return await FinishAsync(RunFinishReason.PreflightFailed, preflight, CancellationToken.None);
                }

                await SetStatusAsync(RunStatus.Running, ct);
                (reason, detail) = await LoopAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                (reason, detail) = (RunFinishReason.StoppedByOperator, "cancelled");
            }
            catch (BudgetStopException ex)
            {
                (reason, detail) = (RunFinishReason.BudgetExhausted, ex.Message);
            }
            catch (Exception ex)
            {
                // Includes cancellations that did not come from the operator (e.g. a timeout inside a port): the run row never stays Running.
                (reason, detail) = (RunFinishReason.Failed, $"{ex.GetType().Name}: {_e.Redact(ex.Message)}");
            }

            return await FinishAsync(reason, detail, CancellationToken.None);
        }

        // ---------------------------------------------------------------- preflight

        private async Task<string?> PreflightAsync(CancellationToken ct)
        {
            if (!HasFreeSpace(_o.RunsDirectory, _o.MinFreeBytes))
            {
                return $"less than {_o.MinFreeBytes / (1024 * 1024)} MiB free for artifacts";
            }

            if (_o.SetupFlowPath is { } flow && !File.Exists(flow))
            {
                return "setup flow file not found";
            }

            ToolVersion version;
            IReadOnlyList<DeviceInfo> devices;
            try
            {
                version = await _e.Device.GetVersionAsync(ct);
                devices = await _e.Device.ListDevicesAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return "device automation unavailable: " + _e.Redact(ex.Message);
            }

            var eligible = devices
                .Where(d => d.IsConnected && d.Platform == _profile.Platform && DeviceAllowed(d.Id.Value))
                .OrderBy(d => d.Id.Value, StringComparer.Ordinal)
                .ToList();
            if (_o.DeviceId is { } wanted)
            {
                if (!eligible.Any(d => d.Id.Value == wanted))
                {
                    return "the requested device is not connected, not allowlisted or not of the profile's platform";
                }

                _device = new DeviceId(wanted);
            }
            else if (eligible.Count == 1)
            {
                _device = eligible[0].Id;
            }
            else
            {
                return eligible.Count == 0
                    ? "no connected, allowlisted device of the profile's platform"
                    : "several eligible devices; choose one with --device";
            }

            _context = ExplorationContext.Create(_profile.ProfileId, _profile.Platform, _profile.AppId, _o.AppBuildId, _o.Locale, _o.ScenarioId, _profile.Capabilities);
            _run = _run with { ContextId = _context.ContextId, DeviceId = _device.Value };
            await WriteManifestAsync(new Dictionary<string, string?> { ["tool"] = $"{version.Name} {version.Version}" }, ct);
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Graph.UpsertContextAsync(_context, _e.Clock.UtcNow, ct);
                    await uow.Runs.UpdateAsync(_run, ct);
                    await AppendAsync(uow, RunEventTypes.RunStarted, _run.RunId, new() { ["profileId"] = _profile.ProfileId, ["device"] = _device.Value, ["reasoner"] = _e.Reasoner.Name }, ct);
                    return 0;
                },
                ct);

            try
            {
                var start = await ResetAsync(ct);
                return start is null ? "the app could not be started in the profile's start state (setup flow or launch failed, or another app is in the foreground)" : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not BudgetStopException)
            {
                return "the start state could not be observed: " + _e.Redact(ex.Message);
            }
        }

        private bool DeviceAllowed(string deviceId) =>
            _e.Settings.Allowlist.DeviceIds.Count == 0
            || _e.Settings.Allowlist.DeviceIds.Any(p => System.Text.RegularExpressions.Regex.IsMatch(
                deviceId,
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal) + "$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)));

        private static bool HasFreeSpace(string directory, long minBytes)
        {
            try
            {
                var full = Path.GetFullPath(directory);
                Directory.CreateDirectory(full);
                var root = Path.GetPathRoot(full);
                return root is null || new DriveInfo(root).AvailableFreeSpace >= minBytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return false;
            }
        }

        // ---------------------------------------------------------------- loop

        private async Task<(RunFinishReason, string?)> LoopAsync(CancellationToken ct)
        {
            var budgets = _e.Settings.Budgets;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_e.Clock.UtcNow - _started >= budgets.MaxDuration)
                {
                    return (RunFinishReason.BudgetExhausted, "budgets.maxDuration reached");
                }

                var summary = await GraphQueries.SummarizeAsync(_e.Store, ct);
                if (summary.States >= budgets.MaxStates)
                {
                    return (RunFinishReason.BudgetExhausted, "budgets.maxStates reached");
                }

                if (summary.Screens >= budgets.MaxScreens)
                {
                    return (RunFinishReason.BudgetExhausted, "budgets.maxScreens reached");
                }

                if (_consecutiveDeviceFailures >= _o.MaxConsecutiveDeviceFailures)
                {
                    return (RunFinishReason.Failed, $"{_consecutiveDeviceFailures} consecutive device failures");
                }

                var task = await NextTaskAsync(ct);
                if (task is null)
                {
                    // Blocked tasks are not final (the operator can resume them, STEP-09): with any left, the run did not exhaust the graph.
                    var blocked = (await _e.Store.InTransactionAsync(uow => uow.Frontier.ListTasksAsync(FrontierTaskStatus.Blocked, ct), ct))
                        .Count(t => t.ContextId == _context!.ContextId);
                    return blocked > 0
                        ? (RunFinishReason.AllRemainingBlocked, $"no pending task; {blocked} blocked task(s) wait for the operator")
                        : (RunFinishReason.Exhausted, "discovered graph exhausted");
                }

                if (task.Type == TaskType.AnalyzeState)
                {
                    await AnalyzeAsync(task, ct);
                }
                else
                {
                    await ExploreAsync(task, ct);
                }

                await SaveCountersAsync(ct);
            }
        }

        /// <summary>Analyses first; then the explore task nearest to the current state over known edges; else the first in store order.</summary>
        private async Task<FrontierTask?> NextTaskAsync(CancellationToken ct)
        {
            var contextId = _context!.ContextId;
            while (true)
            {
                var pending = (await _e.Store.InTransactionAsync(uow => uow.Frontier.ListTasksAsync(FrontierTaskStatus.Pending, ct), ct))
                    .Where(t => t.ContextId == contextId)
                    .ToList();
                var analyze = pending.FirstOrDefault(t => t.Type == TaskType.AnalyzeState);
                if (analyze is not null)
                {
                    return analyze;
                }

                var explore = pending.Where(t => t.Type == TaskType.ExploreAction).ToList();
                if (explore.Count == 0)
                {
                    return null;
                }

                // A task from a state at depth d makes a path of d + 1 actions from the start state (budgets.maxDepth).
                var tooDeep = explore.FirstOrDefault(t => t.Depth >= _e.Settings.Budgets.MaxDepth);
                if (tooDeep is not null)
                {
                    await SetTaskStatusAsync(tooDeep, FrontierTaskStatus.Cancelled, null, ct);
                    continue;
                }

                if (_current is null)
                {
                    return explore[0];
                }

                var distances = (await NavigationGraphAsync(ct)).Distances(_current);
                var best = explore
                    .Select((t, i) => (Task: t, Index: i, Distance: distances.TryGetValue(t.FromStateId!, out var d) ? d : int.MaxValue))
                    .OrderBy(x => x.Distance)
                    .ThenBy(x => x.Index)
                    .First();
                return best.Task;
            }
        }

        // ---------------------------------------------------------------- AnalyzeState

        private async Task AnalyzeAsync(FrontierTask task, CancellationToken ct)
        {
            var recorded = await LoadLatestSnapshotAsync(task.FromStateId!, ct);
            if (recorded is null)
            {
                await SetTaskStatusAsync(task, FrontierTaskStatus.Failed, null, ct);
                await AppendEventAsync(RunEventTypes.TaskFailed, task.TaskId, new() { ["error"] = "no stored snapshot of the state in this context" }, ct);
                return;
            }

            var result = await _interpreter!.InterpretAsync(
                new AgentInterpretationInput(_context!, task.FromStateId!, task.TaskId, null, recorded, _policy),
                ct);
            _counters = _counters with { Analyses = _counters.Analyses + 1 };

            switch (result.Status)
            {
                case AgentInterpretationStatus.Accepted:
                    var scheduled = result.AcceptedActions
                        .Where(c => c.Spec is not null && c.Action is not null)
                        .Select(c => new ScheduledAction(c.Spec!, c.Action!, c.ElementKey, recorded.Normalized.IdentityHash, c.Action!.Type == ActionType.Back ? BackPriority : 0))
                        .ToList();
                    var created = await _committer.ScheduleActionsAsync(_context!, task.FromStateId!, task.Depth, scheduled, ct);
                    await SetTaskStatusAsync(task, FrontierTaskStatus.Completed, null, ct);
                    Progress($"analyze    {task.FromStateId} accepted={scheduled.Count} new-tasks={created}");
                    break;
                case AgentInterpretationStatus.Rejected:
                    await SetTaskStatusAsync(task, FrontierTaskStatus.Failed, null, ct);
                    Progress($"analyze    {task.FromStateId} answer rejected");
                    break;
                default:
                    var code = result.BlockedReason ?? BlockedReasonCode.ReasonerUnavailable;
                    await BlockAsync(task, code, $"reasoner: {result.Status}", [], ct);
                    break;
            }
        }

        private async Task<RecordedSnapshot?> LoadLatestSnapshotAsync(string stateId, CancellationToken ct)
        {
            var contextId = _context!.ContextId;
            var snapshot = (await _e.Store.InTransactionAsync(uow => uow.Graph.ListSnapshotsAsync(ct), ct))
                .Where(s => s.StateId == stateId && s.ContextId == contextId)
                .OrderByDescending(s => s.ObservedAt)
                .ThenBy(s => s.SnapshotId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (snapshot is null)
            {
                return null;
            }

            var text = await _e.Artifacts.ReadTextAsync(snapshot.NormalizedSha256, ct);
            if (text is null)
            {
                return null;
            }

            var document = GraphJson.Deserialize<NormalizedSnapshotDocument>(text);
            return new RecordedSnapshot(snapshot, document.Snapshot, []);
        }

        // ---------------------------------------------------------------- ExploreAction

        private async Task ExploreAsync(FrontierTask task, CancellationToken ct)
        {
            var stored = await _e.Store.InTransactionAsync(uow => uow.Frontier.GetTaskActionAsync(task.TaskId, ct), ct);
            var candidate = (await _e.Store.InTransactionAsync(uow => uow.Graph.ListCandidatesAsync(task.FromStateId, ct), ct))
                .FirstOrDefault(c => c.ActionKey == task.ActionKey);
            if (stored is null || candidate is null)
            {
                await SetTaskStatusAsync(task, FrontierTaskStatus.Failed, null, ct);
                return;
            }

            var spec = new ActionSpec(candidate.ActionType, candidate.CanonicalSelector, candidate.ParamClass, candidate.RiskClass, candidate.Source);
            try
            {
                if (!await NavigateToAsync(task.FromStateId!, ct))
                {
                    await BlockAsync(task, BlockedReasonCode.ReplayDiverged, "the source state could not be reached over known edges after a reset", [], ct);
                    return;
                }

                if (await ExploreFromSourceAsync(task, stored, spec, ct))
                {
                    _consecutiveDeviceFailures = 0;
                }
            }
            catch (BudgetStopException)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException)
            {
                // Device or tool failure outside the action itself (observation, flow): the device state is unknown now.
                _consecutiveDeviceFailures++;
                _current = null;
                await SetTaskStatusAsync(task, FrontierTaskStatus.Failed, null, ct);
                await AppendEventAsync(RunEventTypes.TaskFailed, task.TaskId, new() { ["error"] = $"{ex.GetType().Name}: {_e.Redact(ex.Message)}" }, ct);
                Progress($"failed     {task.TaskId}: {ex.GetType().Name}");
            }
        }

        /// <summary>Runs one attempt from the source state. Returns false when the device misbehaved (observation failure after the action).</summary>
        private async Task<bool> ExploreFromSourceAsync(FrontierTask task, TaskAction stored, ActionSpec spec, CancellationToken ct)
        {
            var attempts = await _e.Store.InTransactionAsync(uow => uow.Frontier.ListAttemptsAsync(task.TaskId, ct), ct);
            var n = attempts.Count == 0 ? 1 : attempts.Max(a => a.N) + 1;
            var attemptId = ContentHash.ShortId("att", ContentHash.Of("mapcue.attempt/1", task.TaskId, n.ToString(CultureInfo.InvariantCulture)));
            var attemptDir = Path.Combine(RunDirectory, "tasks", task.TaskId, "attempts", n.ToString(CultureInfo.InvariantCulture));

            // Fresh pre-snapshot right before the action (architecture §9.3 step 6).
            var preObservation = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Fast), ct);
            var before = await _recorder.RecordAsync(preObservation, _context!, _o.InputValues, ct);
            var sourceNow = await ResolveAsync(before.Normalized, ct);
            var expected = sourceNow == task.FromStateId ? before.Normalized.IdentityHash : stored.ExpectedIdentityHash;
            var guard = PreExecutionGuard.Check(
                new CandidateValidation(task.TaskId, CandidateVerdict.Accepted, [], null, null, stored.Action.EffectiveRisk, spec, stored.Action, stored.ElementKey),
                expected,
                before.Normalized);
            if (guard is { } stale)
            {
                _current = sourceNow;
                await BlockAsync(task, stale, sourceNow == task.FromStateId ? "the element is gone or disabled on a fresh snapshot" : "the screen is not the source state on a fresh snapshot", before.Evidence.Select(e => e.EvidenceId).ToList(), ct);
                return true;
            }

            var startedAt = _e.Clock.UtcNow;
            await _e.Store.InTransactionAsync(
                uow => uow.Frontier.InsertAttemptIfAbsentAsync(new TaskAttempt(attemptId, task.TaskId, n, AttemptPhase.ActionDispatched, null, null, Relative(attemptDir), startedAt, null), ct),
                ct);

            ChargeAction();
            var execution = await _e.Device.ExecuteAsync(_device, stored.Action, ct);
            _counters = _counters with { DeviceActions = _counters.DeviceActions + 1 };

            var evidence = new List<Evidence>();
            var tool = await _e.Artifacts.PutTextAsync(
                GraphJson.Serialize(new ToolOutputDocument("mapcue.tool-output/1", execution.Outcome, execution.ErrorKind, execution.Dispatched, (long)execution.Duration.TotalMilliseconds, execution.Detail)),
                ArtifactKind.ToolOutput,
                ct);
            evidence.Add(EvidenceOf(EvidenceKind.ToolOutput, tool, attemptId));

            RecordedSnapshot? after = null;
            StableObservation? settled = null;
            var appLeft = false;
            Exception? observeFailure = null;
            if (execution.Outcome == ActionOutcome.Succeeded)
            {
                try
                {
                    settled = await _stabilizer.ObserveAsync(_device, _profile.AppId, _normalization, ct);
                    var recordedAfter = await _recorder.RecordAsync(settled.Observation, _context!, _o.InputValues, ct);
                    if (AppPresenceDetector.Of(recordedAfter.Normalized, _profile.AppId) == AppPresence.Left)
                    {
                        // The screen outside the app is evidence of the attempt, never a state of the app's graph.
                        appLeft = true;
                        evidence.AddRange(recordedAfter.Evidence.Select(e => e with { SnapshotId = null, AttemptId = attemptId }));
                    }
                    else
                    {
                        after = recordedAfter;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The action ran but its target could not be observed: the attempt is still committed (edge executed, no target).
                    observeFailure = ex;
                }
            }

            var result = await _committer.CommitAttemptAsync(
                new AttemptCommit(_context!, attemptId, before, spec, execution.Outcome, execution.Duration, after, task.Depth, AgentRequestBuilder.PromptVersion) { AttemptEvidence = evidence },
                ct);
            _counters = _counters with { Attempts = _counters.Attempts + 1, NewStates = _counters.NewStates + result.NewStateIds.Count };

            switch (execution.Outcome)
            {
                case ActionOutcome.Succeeded when observeFailure is not null:
                    _current = null;
                    _consecutiveDeviceFailures++;
                    await AppendEventAsync(RunEventTypes.TaskFailed, task.TaskId, new() { ["error"] = $"observation after the action failed: {observeFailure.GetType().Name}: {_e.Redact(observeFailure.Message)}" }, ct);
                    break;
                case ActionOutcome.Succeeded when appLeft:
                    _current = null;
                    _counters = _counters with { AppLeft = _counters.AppLeft + 1 };
                    await AppendEventAsync(RunEventTypes.AppLeft, attemptId, new() { ["taskId"] = task.TaskId, ["edgeId"] = result.EdgeId }, ct);
                    break;
                case ActionOutcome.Succeeded:
                    _current = result.ToStateId;
                    break;
                case ActionOutcome.OutcomeUnknown:
                    // Never retried automatically (architecture §10.1 layer 5); the device state is unknown, so the next task resets.
                    _current = null;
                    _counters = _counters with { OutcomeUnknown = _counters.OutcomeUnknown + 1 };
                    await SetTaskStatusAsync(task, FrontierTaskStatus.OutcomeUnknown, null, ct);
                    break;
                default:
                    _current = execution.Dispatched ? null : sourceNow;
                    await SetTaskStatusAsync(task, FrontierTaskStatus.Failed, null, ct);
                    break;
            }

            var target = appLeft ? "(outside app)" : result.ToStateId ?? "-";
            Progress($"explore    {task.FromStateId} --{stored.Action.Type}--> {target} [{execution.Outcome}{(settled is { Stable: false } ? ", unsettled" : string.Empty)}]");
            await WriteAttemptAsync(attemptDir, new AttemptDocument(
                "mapcue.attempt/1",
                _run.RunId,
                task.TaskId,
                attemptId,
                n,
                _context!.ContextId,
                task.FromStateId!,
                spec.ActionKey,
                stored.Action.Type,
                stored.Action.EffectiveRisk,
                spec.CanonicalSelector,
                execution.Outcome,
                execution.ErrorKind,
                (long)execution.Duration.TotalMilliseconds,
                before.Snapshot.RawSha256,
                before.Snapshot.NormalizedSha256,
                after?.Snapshot.RawSha256,
                after?.Snapshot.NormalizedSha256,
                evidence.Select(e => e.ArtifactSha256).ToList(),
                appLeft,
                result.ToStateId,
                result.EdgeId,
                result.EdgeStatus,
                settled?.Observations ?? 0,
                settled?.Stable ?? false));
            return observeFailure is null;
        }

        // ---------------------------------------------------------------- navigation and reset

        /// <summary>Brings the device to <paramref name="target"/>: known edges from the current state, else a reset first (once).</summary>
        private async Task<bool> NavigateToAsync(string target, CancellationToken ct)
        {
            for (var round = 0; round < 2; round++)
            {
                if (_current == target)
                {
                    return true;
                }

                var graph = await NavigationGraphAsync(ct);
                var path = _current is null ? null : graph.Path(_current, target);
                if (path is null)
                {
                    if (round > 0 || await ResetAsync(ct) is null)
                    {
                        return false;
                    }

                    if (_current == target)
                    {
                        return true;
                    }

                    path = graph.Path(_current!, target);
                    if (path is null)
                    {
                        return false;
                    }
                }

                await ReplayAsync(path, ct);
                if (_current == target)
                {
                    return true;
                }

                // Diverged: one more round from a reset.
                _current = null;
            }

            return false;
        }

        private async Task ReplayAsync(IReadOnlyList<NavigationStep> path, CancellationToken ct)
        {
            foreach (var step in path)
            {
                ChargeAction();
                var execution = await _e.Device.ExecuteAsync(_device, step.Action, ct);
                _counters = _counters with { NavigationActions = _counters.NavigationActions + 1 };
                if (execution.Outcome != ActionOutcome.Succeeded)
                {
                    _current = null;
                    return;
                }

                var settled = await _stabilizer.ObserveAsync(_device, _profile.AppId, _normalization, ct);
                if (AppPresenceDetector.Of(settled.Normalized, _profile.AppId) == AppPresence.Left)
                {
                    _current = null;
                    return;
                }

                _current = await ResolveAsync(settled.Normalized, ct);
                if (_current != step.ToStateId)
                {
                    Progress($"navigate   diverged after {step.Action.Type}: expected {step.ToStateId}, got {_current ?? "unknown"}");
                    return;
                }
            }
        }

        /// <summary>Clean start into the profile's start state: the setup flow, else a plain launch. Returns the start state or null.</summary>
        private async Task<string?> ResetAsync(CancellationToken ct)
        {
            ChargeAction();
            _resets++;
            _counters = _counters with { Resets = _counters.Resets + 1 };
            _current = null;
            if (_o.SetupFlowPath is { } flow)
            {
                // TapCue writes its own test output (unredacted) to the adapter's work directory, never under runs/ (architecture §10.3).
                var run = await _e.Device.RunFlowAsync(_device, new FlowFile(flow), new FlowRunOptions(_o.SetupFlowTimeout, null), ct);
                if (run.Outcome != ActionOutcome.Succeeded)
                {
                    await AppendEventAsync(RunEventTypes.AppReset, _run.RunId, new() { ["outcome"] = run.Outcome.ToString(), ["error"] = run.ErrorKind.ToString() }, ct);
                    Progress($"reset      setup flow {run.Outcome} ({run.ErrorKind})");
                    return null;
                }
            }
            else
            {
                var launch = new ValidatedDeviceAction("launch-" + _resets.ToString(CultureInfo.InvariantCulture), _profile.AppId, ActionType.LaunchApp, RiskClass.Safe, null, null, null);
                var execution = await _e.Device.ExecuteAsync(_device, launch, ct);
                if (execution.Outcome != ActionOutcome.Succeeded)
                {
                    Progress($"reset      launchApp {execution.Outcome}");
                    return null;
                }
            }

            var settled = await _stabilizer.ObserveAsync(_device, _profile.AppId, _normalization, ct);
            if (AppPresenceDetector.Of(settled.Normalized, _profile.AppId) == AppPresence.Left)
            {
                Progress("reset      another app is in the foreground");
                return null;
            }

            var recorded = await _recorder.RecordAsync(settled.Observation, _context!, _o.InputValues, ct);
            var committed = await _committer.CommitObservationAsync(_context!, recorded, ct, AgentRequestBuilder.PromptVersion);
            _counters = _counters with { NewStates = _counters.NewStates + (committed.NewState ? 1 : 0) };
            _current = committed.StateId;
            await AppendEventAsync(RunEventTypes.AppReset, _run.RunId, new() { ["stateId"] = committed.StateId, ["setupFlow"] = _o.SetupFlowPath is null ? "false" : "true" }, ct);
            Progress($"reset      start state {committed.StateId}");
            return committed.StateId;
        }

        private void ChargeAction()
        {
            var used = _counters.DeviceActions + _counters.NavigationActions + _counters.Resets;
            if (used >= _e.Settings.Budgets.MaxActions)
            {
                throw new BudgetStopException("budgets.maxActions reached");
            }

            if (_e.Clock.UtcNow - _started >= _e.Settings.Budgets.MaxDuration)
            {
                throw new BudgetStopException("budgets.maxDuration reached");
            }
        }

        /// <summary>Known, deterministic, observed edges of this context whose action is stored (replayable without a reasoner).</summary>
        private async Task<NavigationGraph> NavigationGraphAsync(CancellationToken ct)
        {
            var contextId = _context!.ContextId;
            return await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var steps = new List<NavigationStep>();
                    foreach (var edge in (await uow.Graph.ListEdgesAsync(ct)).Where(e => e.ContextId == contextId && e.ToStateId is not null && !e.Nondeterministic && e.Status is EdgeStatus.Observed or EdgeStatus.Verified))
                    {
                        var task = await uow.Frontier.GetTaskByKeyAsync(TaskKeys.ExploreAction(edge.FromStateId, edge.ActionKey, _context), ct);
                        var action = task is null ? null : await uow.Frontier.GetTaskActionAsync(task.TaskId, ct);
                        if (action is not null && edge.FromStateId != edge.ToStateId)
                        {
                            steps.Add(new NavigationStep(edge.FromStateId, edge.ToStateId!, edge.EdgeId, action.Action));
                        }
                    }

                    return new NavigationGraph(steps);
                },
                ct);
        }

        /// <summary>Read-only identity resolution against the committed graph: the known state, or null for an unknown screen.</summary>
        private Task<string?> ResolveAsync(NormalizedSnapshot normalized, CancellationToken ct) =>
            _e.Store.InTransactionAsync(
                async uow =>
                {
                    var states = await uow.Graph.ListStatesAsync(ct);
                    var aliases = await uow.Graph.GetIdentityAliasesAsync(normalized.FingerprintVersion, ct);
                    var resolution = _resolver.Resolve(normalized, [.. states.Select(s => new KnownState(s.StateId, s.ScreenId, s.IdentityHash, s.FingerprintVersion, s.IdentityTokens))], aliases);
                    return resolution.Kind is ResolutionKind.KnownState or ResolutionKind.KnownStateVariant ? resolution.StateId : null;
                },
                ct);

        // ---------------------------------------------------------------- bookkeeping

        private async Task BlockAsync(FrontierTask task, BlockedReasonCode code, string details, IReadOnlyList<string> evidenceIds, CancellationToken ct)
        {
            _counters = _counters with { Blocked = _counters.Blocked + 1 };
            await SetTaskStatusAsync(task, FrontierTaskStatus.Blocked, new BlockedReason(code, details, evidenceIds, null), ct);
            await AppendEventAsync(RunEventTypes.TaskBlocked, task.TaskId, new() { ["code"] = code.ToString(), ["details"] = details }, ct);
            Progress($"blocked    {task.TaskId} {code}: {details}");
        }

        private Task<int> SetTaskStatusAsync(FrontierTask task, FrontierTaskStatus status, BlockedReason? reason, CancellationToken ct) =>
            _e.Store.InTransactionAsync(
                async uow =>
                {
                    var current = await uow.Frontier.GetTaskAsync(task.TaskId, ct) ?? task;
                    await uow.Frontier.UpdateTaskAsync(current with { Status = status, BlockedReason = reason, UpdatedAt = _e.Clock.UtcNow }, ct);
                    return 0;
                },
                ct);

        private async Task SetStatusAsync(RunStatus status, CancellationToken ct)
        {
            _run = _run with { Status = status };
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Runs.UpdateAsync(_run, ct);
                    return 0;
                },
                ct);
        }

        private async Task SaveCountersAsync(CancellationToken ct)
        {
            _run = _run with { Counters = _counters };
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Runs.UpdateAsync(_run, ct);
                    return 0;
                },
                ct);
        }

        private async Task<ExplorationResult> FinishAsync(RunFinishReason reason, string? detail, CancellationToken ct)
        {
            var now = _e.Clock.UtcNow;
            _run = _run with { Status = RunStatus.Finished, FinishReason = reason, Detail = detail is null ? null : _e.Redact(detail), Counters = _counters, EndedAt = now };
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Runs.UpdateAsync(_run, ct);
                    var stats = await uow.Frontier.GetStatsAsync(ct);
                    await AppendAsync(uow, RunEventTypes.RunFinished, _run.RunId, new() { ["reason"] = reason.ToString(), ["detail"] = _run.Detail }, ct);
                    var seq = await uow.Events.LastSeqAsync(ct);
                    await uow.Frontier.InsertCheckpointAsync(new Checkpoint(_e.Ids.NewId("chk"), _run.RunId, seq, stats, _o.ConfigHash, now), ct);
                    return 0;
                },
                ct);
            await WriteManifestAsync(null, ct);
            Progress($"finished   {reason}{(_run.Detail is null ? string.Empty : ": " + _run.Detail)}");
            return new ExplorationResult(_run.RunId, _run.ContextId, reason, _run.Detail, _counters);
        }

        private async Task WriteManifestAsync(Dictionary<string, string?>? extra, CancellationToken ct)
        {
            Directory.CreateDirectory(RunDirectory);
            var manifest = new Dictionary<string, object?>
            {
                ["schemaVersion"] = "mapcue.run/1",
                ["run"] = _run,
                ["budgets"] = _e.Settings.Budgets,
                ["fingerprintVersion"] = SnapshotNormalizer.FingerprintVersion,
                ["setupFlow"] = _o.SetupFlowPath is null ? null : Path.GetFileName(_o.SetupFlowPath),
            };
            if (extra is not null)
            {
                foreach (var (key, value) in extra)
                {
                    manifest[key] = value;
                }
            }

            await WriteRedactedAsync(Path.Combine(RunDirectory, "run.json"), GraphJson.Serialize(manifest), ct);
        }

        private Task WriteAttemptAsync(string directory, AttemptDocument document)
        {
            Directory.CreateDirectory(directory);
            return WriteRedactedAsync(Path.Combine(directory, "attempt.json"), System.Text.Json.JsonSerializer.Serialize(document, GraphJson.Indented), CancellationToken.None);
        }

        private Task WriteRedactedAsync(string path, string text, CancellationToken ct) =>
            File.WriteAllTextAsync(path, _e.Redactor.Redact(text, new RedactionContext("artifact")).Text, ct);

        private string Relative(string path) => Path.GetRelativePath(_o.RunsDirectory, path).Replace('\\', '/');

        private Evidence EvidenceOf(EvidenceKind kind, StoredArtifact artifact, string attemptId) => new(
            Evidence.IdOf(kind, artifact.Ref.Sha256, _context!.ContextId, attemptId),
            kind,
            artifact.Ref.Sha256,
            artifact.Ref.Length,
            _context.ContextId,
            null,
            attemptId,
            artifact.Redaction.RedactionCount,
            artifact.Redaction.Rules,
            _e.Clock.UtcNow);

        private Task<int> AppendEventAsync(string type, string subjectId, Dictionary<string, string?> payload, CancellationToken ct) =>
            _e.Store.InTransactionAsync(
                async uow =>
                {
                    await AppendAsync(uow, type, subjectId, payload, ct);
                    return 0;
                },
                ct);

        private Task<long> AppendAsync(IUnitOfWork uow, string type, string subjectId, Dictionary<string, string?> payload, CancellationToken ct) =>
            uow.Events.AppendAsync(_e.Ids.NewId("evt"), type, _e.Clock.UtcNow, _context?.ContextId, subjectId, GraphJson.Serialize(payload), ct);

        private void Progress(string line) => _o.Progress?.Invoke(_e.Redact(line));
    }
}

/// <summary>One replayable step of navigation: an observed, deterministic edge and its stored action.</summary>
internal sealed record NavigationStep(string FromStateId, string ToStateId, string EdgeId, ValidatedDeviceAction Action);

/// <summary>Breadth-first search over replayable edges (deterministic: edges in id order).</summary>
internal sealed class NavigationGraph(IReadOnlyList<NavigationStep> steps)
{
    private readonly ILookup<string, NavigationStep> _out = steps.OrderBy(s => s.EdgeId, StringComparer.Ordinal).ToLookup(s => s.FromStateId, StringComparer.Ordinal);

    public Dictionary<string, int> Distances(string from)
    {
        var distances = new Dictionary<string, int>(StringComparer.Ordinal) { [from] = 0 };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            foreach (var step in _out[state])
            {
                if (distances.TryAdd(step.ToStateId, distances[state] + 1))
                {
                    queue.Enqueue(step.ToStateId);
                }
            }
        }

        return distances;
    }

    public IReadOnlyList<NavigationStep>? Path(string from, string to)
    {
        if (from == to)
        {
            return [];
        }

        var parent = new Dictionary<string, NavigationStep>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            foreach (var step in _out[state])
            {
                if (!seen.Add(step.ToStateId))
                {
                    continue;
                }

                parent[step.ToStateId] = step;
                if (step.ToStateId == to)
                {
                    var path = new List<NavigationStep>();
                    for (var at = to; at != from; at = parent[at].FromStateId)
                    {
                        path.Add(parent[at]);
                    }

                    path.Reverse();
                    return path;
                }

                queue.Enqueue(step.ToStateId);
            }
        }

        return null;
    }
}

/// <summary>Tool output of one attempt, stored (redacted) as evidence.</summary>
public sealed record ToolOutputDocument(string SchemaVersion, ActionOutcome Outcome, DeviceErrorKind ErrorKind, bool Dispatched, long DurationMs, string? Detail);

/// <summary><c>attempt.json</c>: what one attempt did, with links (SHA-256) to the redacted blobs.</summary>
public sealed record AttemptDocument(
    string SchemaVersion,
    string RunId,
    string TaskId,
    string AttemptId,
    int N,
    string ContextId,
    string FromStateId,
    string ActionKey,
    ActionType ActionType,
    RiskClass Risk,
    string Selector,
    ActionOutcome Outcome,
    DeviceErrorKind ErrorKind,
    long DurationMs,
    string BeforeRawSha256,
    string BeforeNormalizedSha256,
    string? AfterRawSha256,
    string? AfterNormalizedSha256,
    IReadOnlyList<string> EvidenceSha256,
    bool AppLeft,
    string? ToStateId,
    string EdgeId,
    EdgeStatus EdgeStatus,
    int SettleObservations,
    bool Settled);

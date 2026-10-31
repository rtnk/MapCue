using System.Globalization;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.Profiles;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>Event types of runs written to the outbox by <see cref="ExplorationEngine"/>.</summary>
public static class RunEventTypes
{
    public const string RunStarted = "run.started";
    public const string RunFinished = "run.finished";
    public const string RunRecovered = "run.recovered";
    public const string AppReset = "run.app-reset";
    public const string AppLeft = "attempt.app-left";
    public const string TaskBlocked = "task.blocked";
    public const string TaskFailed = "task.failed";
    public const string TaskRecovered = "task.recovered";
}

/// <summary>
/// Exploration (STEP-05 vertical slice, STEP-06 frontier scheduler; architecture §5): <c>Created → Preflight → Running → Finished(reason)</c>.
/// <list type="bullet">
/// <item><b>Reconciliation</b> first (architecture §5.3): tasks leased by an interrupted run go back to <c>Pending</c> when their last
/// attempt never reached <c>ActionDispatched</c> (the attempt becomes <c>Abandoned</c>; after more than <c>budgets.maxRetries</c>
/// interruptions the task fails), and to <c>OutcomeUnknown</c> otherwise (never retried automatically). Interrupted runs are finished
/// as <c>Failed</c> and their device leases released.</item>
/// <item><b>Preflight:</b> free space, the device (allowlisted, connected, of the profile's platform, or the given one) and its lease,
/// the setup flow file, a reset into the profile's start state with the app in the foreground; the start state is committed.</item>
/// <item><b>Loop:</b> the next task by <see cref="FrontierScheduler"/> (analyses first, then locality, risk, novelty, depth), claimed
/// with a lease. <c>AnalyzeState</c> runs the reasoner on the stored snapshot (<see cref="AgentInterpreter"/>, no device) and schedules
/// accepted actions as <c>ExploreAction</c> tasks in the same transaction that completes it (list rows beyond
/// <c>budgets.maxListRowsPerTemplate</c> are represented by the first rows of their template). <c>ExploreAction</c> journals every
/// phase before its effect: <c>Created → RestoreStarted → RestoreVerified → PreObserved → PolicyChecked → ActionDispatched →
/// ActionReturned → PostObserved → Committed</c>; the commit writes the graph, the attempt and the task's final status in one
/// transaction (<see cref="GraphCommitter"/>).</item>
/// <item><b>Leaving the app</b> (e.g. <c>back</c> on a root screen): the edge is <c>executed</c> without a target state, the screen
/// outside the app is kept as attempt evidence and the next task resets the app.</item>
/// <item><b>Outcomes:</b> <c>OutcomeUnknown</c> is never retried (task status <c>OutcomeUnknown</c>, device state unknown, reset
/// next); failed actions fail their task.</item>
/// <item><b>Budgets</b> are per run: the run never starts a task whose estimated device actions exceed what is left of
/// <c>maxActions</c>; <c>maxDuration</c>, <c>maxStates</c>, <c>maxScreens</c> (discovered by this run), <c>maxDepth</c> and the reasoner
/// budget. A budget hit inside a task returns it to <c>Pending</c> for the next run.</item>
/// <item><b>Finish:</b> <c>Exhausted</c> (no pending task), <c>AllRemainingBlocked</c>, <c>BudgetExhausted</c>, <c>StoppedByOperator</c>
/// (cancellation), <c>PreflightFailed</c>, <c>Failed</c>; leases are released and a checkpoint is written.</item>
/// </list>
/// Attempt artifacts go to <c>runs/&lt;runId&gt;/tasks/&lt;taskId&gt;/attempts/&lt;n&gt;/attempt.json</c> (links to redacted blobs).
/// </summary>
public sealed class ExplorationEngine
{
    /// <summary>Error prefix of attempts ended by an interruption (counted against <c>budgets.maxRetries</c>).</summary>
    public const string InterruptedError = "interrupted";

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

    /// <summary>The state of one run: device, context, current state, the claimed task and counters.</summary>
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
        private string? _start;
        private int _resets;
        private int _consecutiveDeviceFailures;

        /// <summary>The attempt of the claimed explore task, with its last journaled phase.</summary>
        private TaskAttempt? _attempt;

        public RunSession(ExplorationEngine engine, ExplorationOptions options)
        {
            _e = engine;
            _o = options;
            _profile = options.Profile;
            _started = engine.Clock.UtcNow;
            _resolver = new ScreenIdentityResolver(IdentityResolverOptions.Default with
            {
                ScreenHints = engine.Settings.Identity.ScreenHints,
                ProfileVariantMaxDelta = engine.Settings.Identity.ProfileVariantMaxDelta,
            });
            _recorder = new SnapshotRecorder(engine.Artifacts, engine.Redactor, engine.Clock);
            _committer = new GraphCommitter(engine.Store, engine.Clock, engine.Ids, _resolver);
            _policy = AgentPolicy.For(_profile);
            // Another profile's switch elements are account changes in this context (policy.profileSwitch, STEP-07).
            _validator = new AgentResponseValidator(new RiskClassifier(ProfileSwitchRules.For(engine.Settings, _profile)), new PolicyGate(engine.Settings.Allowlist));
            // At least two observations fit in the settle timeout, whatever the configured poll interval.
            var settle = engine.Settings.Budgets.UiSettleTimeout;
            var poll = options.PollInterval < settle / 2 ? options.PollInterval : settle / 2;
            _stabilizer = new UiStabilizer(engine.Device, engine.Clock, settle, poll, options.Delay);
            _normalization = new NormalizationOptions(_profile.Platform, options.InputValues);
            _run = new ExplorationRun(engine.Ids.NewId("run"), _profile.ProfileId, null, null, engine.Reasoner.Name, options.ConfigHash, options.Seed, RunStatus.Created, null, null, RunCounters.Zero, _started, null);
        }

        private string RunDirectory => Path.Combine(_o.RunsDirectory, _run.RunId);

        private string Owner => _run.RunId;

        private Budgets Budgets => _e.Settings.Budgets;

        public async Task<ExplorationResult> ExecuteAsync(CancellationToken ct)
        {
            // Reasoner budgets are per run (architecture §10.2): calls of earlier runs do not count.
            _interpreter = new AgentInterpreter(_e.Reasoner, _validator, new AgentBudget(_e.Settings.Agent), _e.Artifacts, _e.Store, _e.Clock);
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
                await RecoverAsync(ct);
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

        // ---------------------------------------------------------------- reconciliation (architecture §5.3, §13)

        /// <summary>
        /// Reconciles what interrupted runs left behind, from the store only (never from a reasoner session, CAP-0109): runs that did
        /// not finish, their device leases and the tasks they had leased (by the last journaled phase of each task's attempt).
        /// </summary>
        private async Task RecoverAsync(CancellationToken ct)
        {
            var recovered = await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var now = _e.Clock.UtcNow;
                    var tasks = 0;
                    if (_o.WorkspaceLockHeld)
                    {
                        foreach (var run in (await uow.Runs.ListUnfinishedAsync(ct)).Where(r => r.RunId != Owner))
                        {
                            await uow.Runs.UpdateAsync(run with { Status = RunStatus.Finished, FinishReason = RunFinishReason.Failed, Detail = $"{InterruptedError}: the process ended during {run.Status}; recovered by run {Owner}", EndedAt = now }, ct);
                            await uow.Runs.ReleaseDeviceLeasesAsync(run.RunId, ct);
                            await AppendAsync(uow, RunEventTypes.RunRecovered, run.RunId, new() { ["status"] = run.Status.ToString(), ["recoveredBy"] = Owner }, ct);
                        }
                    }

                    foreach (var task in await uow.Frontier.ListTasksAsync(FrontierTaskStatus.Leased, ct))
                    {
                        var stale = task.LeaseOwner != Owner && (_o.WorkspaceLockHeld || task.LeaseExpiresAt is null || task.LeaseExpiresAt <= now);
                        if (stale)
                        {
                            await ReleaseTaskAsync(uow, task, $"{InterruptedError}: lease of {task.LeaseOwner ?? "unknown"} recovered", now, ct);
                            tasks++;
                        }
                    }

                    return tasks;
                },
                ct);
            if (recovered > 0)
            {
                _counters = _counters with { RecoveredTasks = _counters.RecoveredTasks + recovered };
                Progress($"recovered  {recovered} task(s) of interrupted runs");
            }
        }

        /// <summary>
        /// Ends the lease of a task whose holder stopped without finishing it. Before <c>ActionDispatched</c> nothing reached the device:
        /// the attempt is <c>Abandoned</c> and the task is pending again (failed after more than <c>maxRetries</c> interruptions). From
        /// <c>ActionDispatched</c> on the action may have run: <c>OutcomeUnknown</c>, never retried automatically (architecture §10.1).
        /// </summary>
        private async Task ReleaseTaskAsync(IUnitOfWork uow, FrontierTask task, string error, DateTimeOffset now, CancellationToken ct)
        {
            var attempts = await uow.Frontier.ListAttemptsAsync(task.TaskId, ct);
            var last = attempts.Count == 0 ? null : attempts.MaxBy(a => a.N);
            FrontierTaskStatus status;
            if (last is { Phase: AttemptPhase.Committed })
            {
                // Cannot happen with a committing writer (the commit ends the task); kept as a safe default.
                status = FrontierTaskStatus.Completed;
            }
            else if (last is not null && last.EndedAt is null && last.Phase >= AttemptPhase.ActionDispatched)
            {
                status = FrontierTaskStatus.OutcomeUnknown;
                await uow.Frontier.UpdateAttemptAsync(last with { Outcome = ActionOutcome.OutcomeUnknown, Error = $"{error} after {last.Phase}", EndedAt = now }, ct);
            }
            else
            {
                if (last is not null && last.EndedAt is null)
                {
                    await uow.Frontier.UpdateAttemptAsync(last with { Phase = AttemptPhase.Abandoned, Error = $"{error} in {last.Phase}", EndedAt = now }, ct);
                }

                var interrupted = attempts.Count(a => a.Phase == AttemptPhase.Abandoned && a.Error?.StartsWith(InterruptedError, StringComparison.Ordinal) == true)
                    + (last is not null && last.EndedAt is null ? 1 : 0);
                status = interrupted > Budgets.MaxRetries ? FrontierTaskStatus.Failed : FrontierTaskStatus.Pending;
            }

            await uow.Frontier.UpdateTaskAsync(task with { Status = status, LeaseOwner = null, LeaseExpiresAt = null, UpdatedAt = now }, ct);
            await AppendAsync(uow, RunEventTypes.TaskRecovered, task.TaskId, new() { ["leaseOwner"] = task.LeaseOwner, ["phase"] = last?.Phase.ToString(), ["status"] = status.ToString() }, ct);
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

            if (!await RenewDeviceLeaseAsync(ct))
            {
                var holder = await _e.Store.InTransactionAsync(uow => uow.Runs.GetDeviceLeaseAsync(_device.Value, ct), ct);
                return $"the device is leased by run {holder?.OwnerRunId ?? "unknown"} until {holder?.ExpiresAt.ToString("O", CultureInfo.InvariantCulture) ?? "-"}";
            }

            _context = ExplorationContext.Create(_profile.ProfileId, _profile.Platform, _profile.AppId, _o.AppBuildId, _o.Locale ?? _profile.Locale, _o.ScenarioId ?? _profile.ScenarioId, _profile.Capabilities);
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

        /// <summary>Takes or renews this run's lease of the device (architecture §5.1: one UI executor per device).</summary>
        private async Task<bool> RenewDeviceLeaseAsync(CancellationToken ct)
        {
            var now = _e.Clock.UtcNow;
            return await _e.Store.InTransactionAsync(uow => uow.Runs.TryAcquireDeviceLeaseAsync(new DeviceLease(_device.Value, Owner, now, now + _o.LeaseDuration), now, ct), ct);
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
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (_e.Clock.UtcNow - _started >= Budgets.MaxDuration)
                {
                    return (RunFinishReason.BudgetExhausted, "budgets.maxDuration reached");
                }

                if (_counters.NewStates >= Budgets.MaxStates)
                {
                    return (RunFinishReason.BudgetExhausted, $"budgets.maxStates reached ({_counters.NewStates} new states in this run)");
                }

                if (_counters.NewScreens >= Budgets.MaxScreens)
                {
                    return (RunFinishReason.BudgetExhausted, $"budgets.maxScreens reached ({_counters.NewScreens} new screens in this run)");
                }

                if (_consecutiveDeviceFailures >= _o.MaxConsecutiveDeviceFailures)
                {
                    return (RunFinishReason.Failed, $"{_consecutiveDeviceFailures} consecutive device failures");
                }

                if (!await RenewDeviceLeaseAsync(ct))
                {
                    return (RunFinishReason.Failed, "the device lease was taken over by another run");
                }

                var next = await NextTaskAsync(ct);
                if (next is null)
                {
                    // Blocked tasks are not final (the operator can resume them, STEP-09), nor are tasks another live run holds: with any
                    // left, this run did not exhaust the graph.
                    var (blocked, leased) = await _e.Store.InTransactionAsync(
                        async uow => (
                            (await uow.Frontier.ListTasksAsync(FrontierTaskStatus.Blocked, ct)).Count(t => t.ContextId == _context!.ContextId),
                            (await uow.Frontier.ListTasksAsync(FrontierTaskStatus.Leased, ct)).Count(t => t.ContextId == _context!.ContextId && t.LeaseOwner != Owner)),
                        ct);
                    return (blocked, leased) switch
                    {
                        (0, 0) => (RunFinishReason.Exhausted, "discovered graph exhausted"),
                        (_, 0) => (RunFinishReason.AllRemainingBlocked, $"no pending task; {blocked} blocked task(s) wait for the operator"),
                        _ => (RunFinishReason.AllRemainingBlocked, $"no pending task; {blocked} blocked task(s) wait for the operator, {leased} task(s) are leased by other runs"),
                    };
                }

                var (task, cost) = next.Value;
                var left = Budgets.MaxActions - _counters.ChargedActions;
                if (cost > left)
                {
                    // Predictable stop: never start a task the action budget cannot pay for; it stays pending for the next run.
                    return (RunFinishReason.BudgetExhausted, $"budgets.maxActions reached: the next task needs {cost} device action(s), {left} left");
                }

                if (!await ClaimAsync(task, ct))
                {
                    continue;
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

        /// <summary>
        /// The next task of this context by <see cref="FrontierScheduler"/> with its estimated device actions (0 for analyses).
        /// Explore tasks deeper than <c>budgets.maxDepth</c> are cancelled on the way.
        /// </summary>
        private async Task<(FrontierTask Task, int Cost)?> NextTaskAsync(CancellationToken ct)
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
                    return (analyze, 0);
                }

                var explore = pending.Where(t => t.Type == TaskType.ExploreAction).ToList();
                if (explore.Count == 0)
                {
                    return null;
                }

                // A task from a state at depth d makes a path of d + 1 actions from the start state (budgets.maxDepth).
                var tooDeep = explore.FirstOrDefault(t => t.Depth >= Budgets.MaxDepth);
                if (tooDeep is not null)
                {
                    await SetTaskStatusAsync(tooDeep, FrontierTaskStatus.Cancelled, null, ct);
                    continue;
                }

                var graph = await NavigationGraphAsync(ct);
                var fromCurrent = _current is null ? null : graph.Distances(_current);
                var fromStart = _start is null ? null : graph.Distances(_start);
                int Distance(FrontierTask t) =>
                    fromCurrent is not null && fromCurrent.TryGetValue(t.FromStateId!, out var d) ? d
                    : fromStart is not null && fromStart.TryGetValue(t.FromStateId!, out var s) ? s + 1
                    : FrontierScheduler.Unreachable;

                var best = FrontierScheduler.Order(explore, Distance, t => graph.IsKnownElsewhere(t.ActionKey!, t.FromStateId!))[0];
                return (best.Task, FrontierScheduler.EstimatedCost(best));
            }
        }

        /// <summary>
        /// Claims the task (<c>Pending → Leased</c>) and, for an explore task, journals a new attempt in phase <c>Created</c> in the same
        /// transaction. Returns false when the task was no longer pending.
        /// </summary>
        private async Task<bool> ClaimAsync(FrontierTask task, CancellationToken ct)
        {
            var attempt = await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var now = _e.Clock.UtcNow;
                    if (!await uow.Frontier.TryLeaseTaskAsync(task.TaskId, Owner, now + _o.LeaseDuration, now, ct))
                    {
                        return (Claimed: false, Attempt: (TaskAttempt?)null);
                    }

                    if (task.Type != TaskType.ExploreAction)
                    {
                        return (Claimed: true, Attempt: null);
                    }

                    var previous = await uow.Frontier.ListAttemptsAsync(task.TaskId, ct);
                    var n = previous.Count == 0 ? 1 : previous.Max(a => a.N) + 1;
                    var attemptId = ContentHash.ShortId("att", ContentHash.Of("mapcue.attempt/1", task.TaskId, n.ToString(CultureInfo.InvariantCulture)));
                    var created = new TaskAttempt(attemptId, task.TaskId, n, AttemptPhase.Created, null, null, Relative(AttemptDirectory(task.TaskId, n)), now, null) { RunId = Owner };
                    await uow.Frontier.InsertAttemptIfAbsentAsync(created, ct);
                    return (Claimed: true, Attempt: created);
                },
                ct);
            _attempt = attempt.Attempt;
            if (_attempt is not null)
            {
                Notify(_attempt);
            }

            return attempt.Claimed;
        }

        private string AttemptDirectory(string taskId, int n) => Path.Combine(RunDirectory, "tasks", taskId, "attempts", n.ToString(CultureInfo.InvariantCulture));

        /// <summary>Journals the next phase of the current attempt before its effect (architecture §5.3) and renews the task lease.</summary>
        private async Task JournalAsync(AttemptPhase phase, CancellationToken ct)
        {
            var attempt = _attempt! with { Phase = phase };
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Frontier.UpdateAttemptAsync(attempt, ct);
                    await uow.Frontier.RenewTaskLeaseAsync(attempt.TaskId, Owner, _e.Clock.UtcNow + _o.LeaseDuration, ct);
                    return 0;
                },
                ct);
            _attempt = attempt;
            Notify(attempt);
        }

        private void Notify(TaskAttempt attempt) => _o.OnAttemptPhase?.Invoke(new AttemptPhaseEvent(attempt.TaskId, attempt.AttemptId, attempt.N, attempt.Phase));

        // ---------------------------------------------------------------- AnalyzeState

        private async Task AnalyzeAsync(FrontierTask task, CancellationToken ct)
        {
            try
            {
                var recorded = await LoadLatestSnapshotAsync(task.FromStateId!, ct);
                if (recorded is null)
                {
                    await EndTaskAsync(task, FrontierTaskStatus.Failed, null, null, ct);
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
                        var scheduled = Represent(
                            result.AcceptedActions
                                .Where(c => c.Spec is not null && c.Action is not null)
                                .Select(c => new ScheduledAction(c.Spec!, c.Action!, c.ElementKey, recorded.Normalized.IdentityHash, FrontierScheduler.PriorityOf(c.Action!)))
                                .ToList(),
                            recorded.Normalized);
                        var created = await _committer.ScheduleActionsAsync(_context!, task.FromStateId!, task.Depth, scheduled, ct, completeTaskId: task.TaskId);
                        _counters = _counters with { RepresentedTasks = _counters.RepresentedTasks + created.Represented };
                        Progress($"analyze    {task.FromStateId} accepted={scheduled.Count} new-tasks={created.Created}{(created.Represented > 0 ? $" represented={created.Represented}" : string.Empty)}");
                        break;
                    case AgentInterpretationStatus.Rejected:
                        await EndTaskAsync(task, FrontierTaskStatus.Failed, null, null, ct);
                        Progress($"analyze    {task.FromStateId} answer rejected");
                        break;
                    default:
                        var code = result.BlockedReason ?? BlockedReasonCode.ReasonerUnavailable;
                        await BlockAsync(task, code, $"reasoner: {result.Status}", [], ct);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // No device effect: the analysis can simply run again (operator stop, budget) or later (any other error ends the run).
                await EndTaskAsync(task, FrontierTaskStatus.Pending, null, null, CancellationToken.None);
                throw;
            }
        }

        /// <summary>
        /// List rows beyond <c>budgets.maxListRowsPerTemplate</c> per list template are represented by the first rows of their template
        /// (snapshot order): they are scheduled as cancelled tasks pointing at the first row, so rows of an endless list never multiply
        /// the frontier.
        /// </summary>
        private List<ScheduledAction> Represent(List<ScheduledAction> actions, NormalizedSnapshot snapshot)
        {
            var elements = snapshot.Elements.Select((e, i) => (Element: e, Index: i)).GroupBy(x => x.Element.ElementKey, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var rows = actions
                .Select(a => (Action: a, Row: a.ElementKey is not null && elements.TryGetValue(a.ElementKey, out var x) && x.Element.ListTemplate is not null ? x : default))
                .Where(x => x.Row.Element is not null)
                .GroupBy(x => x.Row.Element!.ListTemplate!, StringComparer.Ordinal);
            var representative = new Dictionary<ScheduledAction, string>();
            foreach (var template in rows)
            {
                var ordered = template.OrderBy(x => x.Row.Index).ThenBy(x => x.Action.Spec.ActionKey, StringComparer.Ordinal).ToList();
                foreach (var extra in ordered.Skip(Budgets.MaxListRowsPerTemplate))
                {
                    representative[extra.Action] = ordered[0].Action.Spec.ActionKey;
                }
            }

            // Representatives are stored before the rows they stand for.
            return [.. actions.Where(a => !representative.ContainsKey(a)), .. actions.Where(representative.ContainsKey).Select(a => a with { RepresentedByActionKey = representative[a] })];
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
            try
            {
                var stored = await _e.Store.InTransactionAsync(uow => uow.Frontier.GetTaskActionAsync(task.TaskId, ct), ct);
                var candidate = (await _e.Store.InTransactionAsync(uow => uow.Graph.ListCandidatesAsync(task.FromStateId, ct), ct))
                    .FirstOrDefault(c => c.ActionKey == task.ActionKey);
                if (stored is null || candidate is null)
                {
                    await EndTaskAsync(task, FrontierTaskStatus.Failed, null, "no stored action", ct);
                    return;
                }

                // The risk is this context's (the validated action of the task), never the candidate's first context (STEP-07).
                var spec = new ActionSpec(candidate.ActionType, candidate.CanonicalSelector, candidate.ParamClass, stored.Action.EffectiveRisk, candidate.Source);
                await JournalAsync(AttemptPhase.RestoreStarted, ct);
                if (!await NavigateToAsync(task.FromStateId!, ct))
                {
                    await BlockAsync(task, BlockedReasonCode.ReplayDiverged, "the source state could not be reached over known edges after a reset", [], ct);
                    return;
                }

                await JournalAsync(AttemptPhase.RestoreVerified, ct);
                if (await ExploreFromSourceAsync(task, stored, spec, ct))
                {
                    _consecutiveDeviceFailures = 0;
                }
            }
            catch (BudgetStopException ex) when (!Committed)
            {
                // The budget ran out on the way (before ActionDispatched): nothing of this task ran, the next run takes it again.
                await EndTaskAsync(task, FrontierTaskStatus.Pending, null, "budget: " + ex.Message, CancellationToken.None);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested && !Committed)
            {
                await EndTaskAsync(task, AfterDispatch ? FrontierTaskStatus.OutcomeUnknown : FrontierTaskStatus.Pending, null, "stopped by operator", CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not BudgetStopException && Committed)
            {
                // The attempt and its task are committed; only the bookkeeping after it failed (attempt.json, an event).
                _consecutiveDeviceFailures++;
                Progress($"failed     {task.TaskId} after its commit: {ex.GetType().Name}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException and not BudgetStopException)
            {
                // Device or tool failure outside the committed attempt: the device state is unknown now. After ActionDispatched the action
                // may have run, so the task is OutcomeUnknown and never retried automatically.
                _consecutiveDeviceFailures++;
                _current = null;
                var status = AfterDispatch ? FrontierTaskStatus.OutcomeUnknown : FrontierTaskStatus.Failed;
                _counters = status == FrontierTaskStatus.OutcomeUnknown ? _counters with { OutcomeUnknown = _counters.OutcomeUnknown + 1 } : _counters;
                await EndTaskAsync(task, status, null, $"{ex.GetType().Name}: {_e.Redact(ex.Message)}", CancellationToken.None);
                await AppendEventAsync(RunEventTypes.TaskFailed, task.TaskId, new() { ["error"] = $"{ex.GetType().Name}: {_e.Redact(ex.Message)}" }, CancellationToken.None);
                Progress($"failed     {task.TaskId}: {ex.GetType().Name}");
            }
            finally
            {
                _attempt = null;
            }
        }

        /// <summary>True when the current attempt is committed (its task already has its final status).</summary>
        private bool Committed => _attempt is { Phase: AttemptPhase.Committed };

        /// <summary>True when the current attempt journaled <c>ActionDispatched</c> and was not committed.</summary>
        private bool AfterDispatch => _attempt is { Phase: >= AttemptPhase.ActionDispatched and < AttemptPhase.Committed };

        /// <summary>Runs one attempt from the source state. Returns false when the device misbehaved (observation failure after the action).</summary>
        private async Task<bool> ExploreFromSourceAsync(FrontierTask task, TaskAction stored, ActionSpec spec, CancellationToken ct)
        {
            var attemptId = _attempt!.AttemptId;
            var n = _attempt.N;
            var attemptDir = AttemptDirectory(task.TaskId, n);

            // Fresh pre-snapshot right before the action (architecture §9.3 step 6).
            var preObservation = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Fast), ct);
            var before = await _recorder.RecordAsync(preObservation, _context!, _o.InputValues, ct);
            var sourceNow = await ResolveAsync(before.Normalized, ct);
            await JournalAsync(AttemptPhase.PreObserved, ct);
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

            await JournalAsync(AttemptPhase.PolicyChecked, ct);
            ChargeAction();
            await JournalAsync(AttemptPhase.ActionDispatched, ct);
            var execution = await _e.Device.ExecuteAsync(_device, stored.Action, ct);
            _counters = _counters with { DeviceActions = _counters.DeviceActions + 1 };

            var evidence = new List<Evidence>();
            var tool = await _e.Artifacts.PutTextAsync(
                GraphJson.Serialize(new ToolOutputDocument("mapcue.tool-output/1", execution.Outcome, execution.ErrorKind, execution.Dispatched, (long)execution.Duration.TotalMilliseconds, execution.Detail)),
                ArtifactKind.ToolOutput,
                ct);
            evidence.Add(EvidenceOf(EvidenceKind.ToolOutput, tool, attemptId));
            await JournalAsync(AttemptPhase.ActionReturned, ct);

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

            await JournalAsync(AttemptPhase.PostObserved, ct);
            var taskStatus = execution.Outcome switch
            {
                ActionOutcome.Succeeded => FrontierTaskStatus.Completed,
                ActionOutcome.OutcomeUnknown => FrontierTaskStatus.OutcomeUnknown,
                _ => FrontierTaskStatus.Failed,
            };
            var result = await _committer.CommitAttemptAsync(
                new AttemptCommit(_context!, attemptId, before, spec, execution.Outcome, execution.Duration, after, task.Depth, AgentRequestBuilder.PromptVersion)
                {
                    AttemptEvidence = evidence,
                    TaskStatus = taskStatus,
                    RunId = Owner,
                },
                ct);
            _attempt = _attempt with { Phase = AttemptPhase.Committed, Outcome = execution.Outcome };
            Notify(_attempt);
            _counters = _counters with
            {
                Attempts = _counters.Attempts + 1,
                NewStates = _counters.NewStates + result.NewStateIds.Count,
                NewScreens = _counters.NewScreens + result.NewScreens,
            };

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
                    break;
                default:
                    _current = execution.Dispatched ? null : sourceNow;
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
            _counters = _counters with
            {
                NewStates = _counters.NewStates + (committed.NewState ? 1 : 0),
                NewScreens = _counters.NewScreens + (committed.NewScreen ? 1 : 0),
            };
            _current = committed.StateId;
            _start = committed.StateId;
            await AppendEventAsync(RunEventTypes.AppReset, _run.RunId, new() { ["stateId"] = committed.StateId, ["setupFlow"] = _o.SetupFlowPath is null ? "false" : "true" }, ct);
            Progress($"reset      start state {committed.StateId}");
            return committed.StateId;
        }

        private void ChargeAction()
        {
            if (_counters.ChargedActions >= Budgets.MaxActions)
            {
                throw new BudgetStopException("budgets.maxActions reached");
            }

            if (_e.Clock.UtcNow - _started >= Budgets.MaxDuration)
            {
                throw new BudgetStopException("budgets.maxDuration reached");
            }
        }

        /// <summary>Known, deterministic, observed edges of this context whose action is stored (replayable without a reasoner).</summary>
        private async Task<NavigationGraph> NavigationGraphAsync(CancellationToken ct)
        {
            var context = _context!;
            return await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var actions = (await uow.Frontier.ListTaskActionsAsync(ct)).ToDictionary(a => a.TaskId, StringComparer.Ordinal);
                    var steps = new List<NavigationStep>();
                    foreach (var edge in (await uow.Graph.ListEdgesAsync(ct)).Where(e => e.ContextId == context.ContextId && e.ToStateId is not null && !e.Nondeterministic && e.Status is EdgeStatus.Observed or EdgeStatus.Verified))
                    {
                        if (actions.TryGetValue(GraphCommitter.TaskIdOf(TaskKeys.ExploreAction(edge.FromStateId, edge.ActionKey, context)), out var action))
                        {
                            steps.Add(new NavigationStep(edge.FromStateId, edge.ToStateId!, edge.EdgeId, edge.ActionKey, action.Action));
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
                    var known = await GraphCommitter.KnownStatesAsync(uow, ct);
                    var aliases = await uow.Graph.GetIdentityAliasesAsync(normalized.FingerprintVersion, ct);
                    var resolution = _resolver.Resolve(normalized, known, aliases, _context!.ContextId);
                    return resolution.Kind is ResolutionKind.KnownState or ResolutionKind.KnownStateVariant ? resolution.StateId : null;
                },
                ct);

        // ---------------------------------------------------------------- bookkeeping

        private async Task BlockAsync(FrontierTask task, BlockedReasonCode code, string details, IReadOnlyList<string> evidenceIds, CancellationToken ct)
        {
            _counters = _counters with { Blocked = _counters.Blocked + 1 };
            await EndTaskAsync(task, FrontierTaskStatus.Blocked, new BlockedReason(code, details, evidenceIds, null), $"blocked: {code}", ct);
            await AppendEventAsync(RunEventTypes.TaskBlocked, task.TaskId, new() { ["code"] = code.ToString(), ["details"] = details }, ct);
            Progress($"blocked    {task.TaskId} {code}: {details}");
        }

        /// <summary>
        /// Ends a claimed task without a committed attempt, in one transaction: final status (or pending again), released lease, and the
        /// current attempt ended (<c>Abandoned</c> before <c>ActionDispatched</c>, otherwise <c>OutcomeUnknown</c>).
        /// </summary>
        private async Task EndTaskAsync(FrontierTask task, FrontierTaskStatus status, BlockedReason? reason, string? attemptError, CancellationToken ct)
        {
            var attempt = _attempt is { Phase: not AttemptPhase.Committed, EndedAt: null } current && current.TaskId == task.TaskId ? current : null;
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var now = _e.Clock.UtcNow;
                    var stored = await uow.Frontier.GetTaskAsync(task.TaskId, ct) ?? task;
                    await uow.Frontier.UpdateTaskAsync(stored with { Status = status, BlockedReason = reason, LeaseOwner = null, LeaseExpiresAt = null, UpdatedAt = now }, ct);
                    if (attempt is not null)
                    {
                        await uow.Frontier.UpdateAttemptAsync(
                            attempt.Phase >= AttemptPhase.ActionDispatched
                                ? attempt with { Outcome = ActionOutcome.OutcomeUnknown, Error = attemptError, EndedAt = now }
                                : attempt with { Phase = AttemptPhase.Abandoned, Error = attemptError, EndedAt = now },
                            ct);
                    }

                    return 0;
                },
                ct);
            if (attempt is not null)
            {
                _attempt = null;
            }
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
            // Recipes of this context from its replayable edges (never from another context, never from a reasoner session).
            var recipes = _context is not null && _start is not null
                ? RecipeBuilder.Build(_context.ContextId, _start, _o.SetupFlowPath is not null, await NavigationGraphAsync(ct))
                : [];
            var now = _e.Clock.UtcNow;
            _run = _run with { Status = RunStatus.Finished, FinishReason = reason, Detail = detail is null ? null : _e.Redact(detail), Counters = _counters, EndedAt = now };
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    // Nothing stays leased by a finished run (e.g. an analysis interrupted by an error that ended the run).
                    foreach (var task in (await uow.Frontier.ListTasksAsync(FrontierTaskStatus.Leased, ct)).Where(t => t.LeaseOwner == Owner))
                    {
                        await ReleaseTaskAsync(uow, task, $"{InterruptedError}: run {Owner} finished ({reason})", now, ct);
                    }

                    await uow.Runs.ReleaseDeviceLeasesAsync(Owner, ct);
                    foreach (var recipe in recipes)
                    {
                        await uow.Graph.UpsertRecipeAsync(recipe, ct);
                    }

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

/// <summary>
/// Reachability recipes of one context (architecture §6, §13; STEP-07): for every state reachable from the start state over replayable
/// edges of that context, a clean start (setup flow or <c>launchApp</c>) and the shortest path (BFS, edges in id order), each step with
/// the state expected after it. Replay with divergence detection is STEP-09; <c>verifiedAt</c> stays empty until then.
/// </summary>
internal static class RecipeBuilder
{
    public static IReadOnlyList<ReachabilityRecipe> Build(string contextId, string startStateId, bool setupFlow, NavigationGraph graph)
    {
        var recipes = new List<ReachabilityRecipe>();
        foreach (var target in graph.Distances(startStateId).Keys.Order(StringComparer.Ordinal))
        {
            var path = graph.Path(startStateId, target);
            if (path is null)
            {
                continue;
            }

            var steps = new List<RecipeStep> { new(setupFlow ? RecipeStepKind.SetupFlow : RecipeStepKind.LaunchApp, null, null, startStateId, null) };
            steps.AddRange(path.Select(p => new RecipeStep(RecipeStepKind.Action, p.ActionKey, p.EdgeId, p.ToStateId, null)));
            recipes.Add(new ReachabilityRecipe(ReachabilityRecipe.IdOf(contextId, target), contextId, target, steps, null, [.. path.Select(p => p.EdgeId)]));
        }

        return recipes;
    }
}

/// <summary>One replayable step of navigation: an observed, deterministic edge and its stored action.</summary>
internal sealed record NavigationStep(string FromStateId, string ToStateId, string EdgeId, string ActionKey, ValidatedDeviceAction Action);

/// <summary>Breadth-first search over replayable edges (deterministic: edges in id order).</summary>
internal sealed class NavigationGraph(IReadOnlyList<NavigationStep> steps)
{
    private readonly ILookup<string, NavigationStep> _out = steps.OrderBy(s => s.EdgeId, StringComparer.Ordinal).ToLookup(s => s.FromStateId, StringComparer.Ordinal);
    private readonly ILookup<string, string> _sources = steps.ToLookup(s => s.ActionKey, s => s.FromStateId, StringComparer.Ordinal);

    /// <summary>True when the action was already observed from another state (scheduling novelty: such actions go after new ones).</summary>
    public bool IsKnownElsewhere(string actionKey, string fromStateId) => _sources[actionKey].Any(s => s != fromStateId);

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

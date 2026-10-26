using System.Globalization;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>A typed action as recorded in the graph. <see cref="ParamClass"/> is a class such as <c>input:profile.username</c>, never a value.</summary>
public sealed record ActionSpec(ActionType Type, string CanonicalSelector, string? ParamClass, RiskClass Risk, CandidateSource Source)
{
    public string ActionKey => ActionCandidate.KeyOf(Type, CanonicalSelector, ParamClass);
}

/// <summary>
/// Everything one attempt produced. Artifacts of <see cref="Before"/>/<see cref="After"/> are already stored.
/// <see cref="AttemptId"/> makes the commit idempotent. An attempt journaled earlier keeps its number; otherwise the commit
/// numbers it after the existing attempts of its task. <see cref="AttemptEvidence"/> is stored evidence of the attempt that is not a
/// graph snapshot (tool output, or the screen outside the app after the action left it); it is recorded with the attempt id.
/// </summary>
public sealed record AttemptCommit(
    ExplorationContext Context,
    string AttemptId,
    RecordedSnapshot Before,
    ActionSpec Action,
    ActionOutcome Outcome,
    TimeSpan Duration,
    RecordedSnapshot? After,
    int Depth = 0,
    string ReasonerPromptVersion = GraphCommitter.NoReasonerPromptVersion)
{
    public IReadOnlyList<Evidence> AttemptEvidence { get; init; } = [];
}

/// <summary>An accepted action to explore from a state: its graph spec, the validated device action and the pre-execution check data.</summary>
public sealed record ScheduledAction(ActionSpec Spec, ValidatedDeviceAction Action, string? ElementKey, string ExpectedIdentityHash, int Priority = 0);

/// <summary>Result of an observation commit.</summary>
public sealed record ObservationCommitResult(string StateId, string ScreenId, ResolutionKind Resolution, bool NewState, bool NewVariant, int NewTasks);

/// <summary>Result of an attempt commit. <see cref="AlreadyCommitted"/> means the attempt was committed before and nothing changed.</summary>
public sealed record AttemptCommitResult(bool AlreadyCommitted, string FromStateId, string? ToStateId, string EdgeId, EdgeStatus EdgeStatus, IReadOnlyList<string> NewStateIds, int NewVariants, int NewTasks);

/// <summary>Event types written to the outbox by <see cref="GraphCommitter"/>.</summary>
public static class GraphEventTypes
{
    public const string ScreenDiscovered = "screen.discovered";
    public const string StateDiscovered = "state.discovered";
    public const string IdentityAmbiguous = "state.identity-ambiguous";
    public const string VariantDiscovered = "variant.discovered";
    public const string SnapshotRecorded = "snapshot.recorded";
    public const string EdgeDiscovered = "edge.discovered";
    public const string EdgeObserved = "edge.observed";
    public const string EdgeNondeterministic = "edge.nondeterministic";
    public const string TaskCreated = "task.created";
    public const string AttemptCommitted = "attempt.committed";
}

/// <summary>
/// Writes observations and attempts into the graph, each in exactly one transaction (architecture §5.1, §12): state and variant
/// upsert, snapshot and evidence rows, candidate, edge and observation, end of the attempt, new tasks (<c>ON CONFLICT DO NOTHING</c>)
/// and outbox events. Identity is resolved inside the transaction against the committed graph.
/// Ids of graph objects are derived from content (identity hash, edge key, task key), so the same input gives the same graph.
/// </summary>
public sealed class GraphCommitter(IStateStore store, IClock clock, IIdGenerator ids, ScreenIdentityResolver? resolver = null)
{
    public const string NoReasonerPromptVersion = "none";

    private readonly ScreenIdentityResolver _resolver = resolver ?? new ScreenIdentityResolver();

    /// <summary>Commits a standalone observation (e.g. the start state or an imported snapshot).</summary>
    public Task<ObservationCommitResult> CommitObservationAsync(ExplorationContext context, RecordedSnapshot snapshot, CancellationToken ct, string reasonerPromptVersion = NoReasonerPromptVersion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(snapshot);
        return store.InTransactionAsync(
            async uow =>
            {
                var now = clock.UtcNow;
                await uow.Graph.UpsertContextAsync(context, now, ct);
                var placed = await PlaceAsync(uow, context, snapshot, attemptId: null, now, ct);
                var newTasks = await AddAnalyzeTaskAsync(uow, context, placed, depth: 0, reasonerPromptVersion, now, ct);
                return new ObservationCommitResult(placed.StateId, placed.ScreenId, placed.Resolution, placed.NewState, placed.NewVariant, newTasks);
            },
            ct);
    }

    /// <summary>Commits one attempt. Committing the same <see cref="AttemptCommit.AttemptId"/> again changes nothing.</summary>
    public Task<AttemptCommitResult> CommitAttemptAsync(AttemptCommit commit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(commit);
        return store.InTransactionAsync(uow => CommitAttemptAsync(uow, commit, ct), ct);
    }

    private async Task<AttemptCommitResult> CommitAttemptAsync(IUnitOfWork uow, AttemptCommit commit, CancellationToken ct)
    {
        var context = commit.Context;
        var actionKey = commit.Action.ActionKey;
        var existing = await uow.Graph.GetObservationAsync(commit.AttemptId, ct);
        if (existing is not null)
        {
            // The stored observation wins, even if the caller reuses the attempt id with other content.
            var existingEdge = (await uow.Graph.ListEdgesAsync(ct)).Single(e => e.EdgeId == existing.EdgeId);
            return new AttemptCommitResult(true, existingEdge.FromStateId, existing.ToStateId, existing.EdgeId, existingEdge.Status, [], 0, 0);
        }

        var now = clock.UtcNow;
        await uow.Graph.UpsertContextAsync(context, now, ct);

        var from = await PlaceAsync(uow, context, commit.Before, commit.AttemptId, now, ct);
        var to = commit.After is null ? null : await PlaceAsync(uow, context, commit.After, commit.AttemptId, now, ct);
        var target = commit.Outcome == ActionOutcome.Succeeded ? to : null;
        var status = commit.Outcome switch
        {
            ActionOutcome.Succeeded when target is not null => EdgeStatus.Observed,
            ActionOutcome.Succeeded => EdgeStatus.Executed,
            ActionOutcome.OutcomeUnknown => EdgeStatus.Unknown,
            ActionOutcome.Rejected => EdgeStatus.SkippedByPolicy,
            _ => EdgeStatus.Discovered,
        };

        await uow.Graph.InsertCandidateIfAbsentAsync(
            new ActionCandidate(from.StateId, actionKey, commit.Action.Type, commit.Action.CanonicalSelector, commit.Action.ParamClass, commit.Action.Source, commit.Action.Risk, CandidateStatus.Explored, now),
            ct);

        var edge = await uow.Graph.GetEdgeAsync(from.StateId, actionKey, context.ContextId, ct);
        if (edge is null)
        {
            edge = new ScreenEdge(ScreenEdge.IdOf(from.StateId, actionKey, context.ContextId), from.StateId, actionKey, context.ContextId, target?.StateId, status, 1, false, now, now, null);
            await uow.Graph.InsertEdgeAsync(edge, ct);
            await AppendAsync(uow, GraphEventTypes.EdgeDiscovered, now, context, edge.EdgeId, new() { ["fromStateId"] = edge.FromStateId, ["toStateId"] = edge.ToStateId, ["actionKey"] = actionKey }, ct);
        }
        else
        {
            var nondeterministic = edge.Nondeterministic || (edge.ToStateId is not null && target is not null && edge.ToStateId != target.StateId);
            if (nondeterministic && !edge.Nondeterministic)
            {
                await AppendAsync(uow, GraphEventTypes.EdgeNondeterministic, now, context, edge.EdgeId, new() { ["firstToStateId"] = edge.ToStateId, ["otherToStateId"] = target!.StateId }, ct);
            }

            edge = edge with
            {
                ToStateId = edge.ToStateId ?? target?.StateId,
                Status = EdgeStatuses.Raise(edge.Status, status),
                ObservationCount = edge.ObservationCount + 1,
                Nondeterministic = nondeterministic,
                LastObservedAt = now,
            };
            await uow.Graph.UpdateEdgeAsync(edge, ct);
        }

        await uow.Graph.InsertObservationIfAbsentAsync(
            new ActionObservation(commit.AttemptId, edge.EdgeId, context.ContextId, from.SnapshotId, to?.SnapshotId, target?.StateId, commit.Outcome, (long)commit.Duration.TotalMilliseconds, now),
            ct);
        foreach (var evidence in commit.AttemptEvidence)
        {
            // Evidence of an attempt is keyed by the attempt, so identical content (e.g. the same launcher screen) is kept per attempt.
            await uow.Graph.InsertEvidenceIfAbsentAsync(
                evidence with { EvidenceId = Evidence.IdOf(evidence.Kind, evidence.ArtifactSha256, context.ContextId, commit.AttemptId), AttemptId = commit.AttemptId, ContextId = context.ContextId },
                ct);
        }

        await AppendAsync(uow, GraphEventTypes.EdgeObserved, now, context, edge.EdgeId, new() { ["attemptId"] = commit.AttemptId, ["outcome"] = commit.Outcome.ToString(), ["toStateId"] = target?.StateId }, ct);

        // The explore task of this (state, action, context) is completed by this attempt; a later identical pair never re-creates it.
        var newTasks = 0;
        var taskKey = TaskKeys.ExploreAction(from.StateId, actionKey, context);
        var task = await uow.Frontier.GetTaskByKeyAsync(taskKey, ct);
        if (task is null)
        {
            task = NewTask(taskKey, TaskType.ExploreAction, context, from.StateId, actionKey, commit.Depth, now) with { Status = FrontierTaskStatus.Completed };
            await uow.Frontier.InsertTaskIfAbsentAsync(task, ct);
            task = await uow.Frontier.GetTaskByKeyAsync(taskKey, ct);
            newTasks++;
            await AppendAsync(uow, GraphEventTypes.TaskCreated, now, context, task!.TaskId, new() { ["type"] = task.Type.ToString(), ["status"] = task.Status.ToString() }, ct);
        }
        else if (task.Status is not FrontierTaskStatus.Completed)
        {
            await uow.Frontier.UpdateTaskAsync(task with { Status = FrontierTaskStatus.Completed, LeaseOwner = null, LeaseExpiresAt = null, UpdatedAt = now }, ct);
        }

        var journaled = await uow.Frontier.GetAttemptAsync(commit.AttemptId, ct);
        if (journaled is null)
        {
            var previous = await uow.Frontier.ListAttemptsAsync(task.TaskId, ct);
            var n = previous.Count == 0 ? 1 : previous.Max(a => a.N) + 1;
            await uow.Frontier.InsertAttemptIfAbsentAsync(new TaskAttempt(commit.AttemptId, task.TaskId, n, AttemptPhase.Committed, commit.Outcome, null, null, now, now), ct);
        }
        else
        {
            await uow.Frontier.UpdateAttemptAsync(journaled with { Phase = AttemptPhase.Committed, Outcome = commit.Outcome, EndedAt = now }, ct);
        }

        newTasks += await AddAnalyzeTaskAsync(uow, context, from, commit.Depth, commit.ReasonerPromptVersion, now, ct);
        if (to is not null)
        {
            newTasks += await AddAnalyzeTaskAsync(uow, context, to, commit.Depth + 1, commit.ReasonerPromptVersion, now, ct);
        }

        await AppendAsync(uow, GraphEventTypes.AttemptCommitted, now, context, commit.AttemptId, new() { ["taskId"] = task.TaskId, ["edgeId"] = edge.EdgeId }, ct);

        var newStates = new[] { from, to }.Where(p => p is { NewState: true }).Select(p => p!.StateId).Distinct(StringComparer.Ordinal).ToList();
        var newVariants = new[] { from, to }.Count(p => p is { NewVariant: true });
        return new AttemptCommitResult(false, from.StateId, target?.StateId, edge.EdgeId, edge.Status, newStates, newVariants, newTasks);
    }

    /// <summary>
    /// Schedules <c>ExploreAction</c> tasks for accepted actions of a state, each with its validated action, in one transaction
    /// (<c>ON CONFLICT DO NOTHING</c>: a known (state, action, context) pair is never scheduled twice, architecture §7.4).
    /// Returns the number of new tasks.
    /// </summary>
    public Task<int> ScheduleActionsAsync(ExplorationContext context, string fromStateId, int depth, IReadOnlyList<ScheduledAction> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(actions);
        return store.InTransactionAsync(
            async uow =>
            {
                var now = clock.UtcNow;
                var created = 0;
                foreach (var scheduled in actions)
                {
                    var spec = scheduled.Spec;
                    await uow.Graph.InsertCandidateIfAbsentAsync(
                        new ActionCandidate(fromStateId, spec.ActionKey, spec.Type, spec.CanonicalSelector, spec.ParamClass, spec.Source, spec.Risk, CandidateStatus.Scheduled, now),
                        ct);
                    var key = TaskKeys.ExploreAction(fromStateId, spec.ActionKey, context);
                    var task = NewTask(key, TaskType.ExploreAction, context, fromStateId, spec.ActionKey, depth, now) with { Priority = scheduled.Priority };
                    if (!await uow.Frontier.InsertTaskIfAbsentAsync(task, ct))
                    {
                        continue;
                    }

                    await uow.Frontier.InsertTaskActionIfAbsentAsync(new TaskAction(task.TaskId, scheduled.Action, scheduled.ElementKey, scheduled.ExpectedIdentityHash, now), ct);
                    await AppendAsync(uow, GraphEventTypes.TaskCreated, now, context, task.TaskId, new() { ["type"] = task.Type.ToString(), ["status"] = task.Status.ToString(), ["actionKey"] = spec.ActionKey }, ct);
                    created++;
                }

                return created;
            },
            ct);
    }

    private sealed record Placement(string StateId, string ScreenId, string SnapshotId, ResolutionKind Resolution, bool NewState, bool NewVariant);

    /// <summary>Resolves the identity of a recorded snapshot, creates screen/state when needed and upserts the variant, snapshot and evidence.</summary>
    private async Task<Placement> PlaceAsync(IUnitOfWork uow, ExplorationContext context, RecordedSnapshot recorded, string? attemptId, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = recorded.Normalized;
        var states = await uow.Graph.ListStatesAsync(ct);
        var aliases = await uow.Graph.GetIdentityAliasesAsync(normalized.FingerprintVersion, ct);
        var known = states.Select(s => new KnownState(s.StateId, s.ScreenId, s.IdentityHash, s.FingerprintVersion, s.IdentityTokens)).ToList();
        var resolution = _resolver.Resolve(normalized, known, aliases);

        string stateId;
        string screenId;
        var newState = false;
        switch (resolution.Kind)
        {
            case ResolutionKind.KnownState:
                stateId = resolution.StateId!;
                screenId = resolution.ScreenId!;
                break;
            case ResolutionKind.KnownStateVariant:
                stateId = resolution.StateId!;
                screenId = resolution.ScreenId!;
                await uow.Graph.AddIdentityAliasAsync(normalized.IdentityHash, normalized.FingerprintVersion, stateId, resolution.Similarity, now, ct);
                break;
            default:
                screenId = resolution.Kind == ResolutionKind.NewStateOfScreen
                    ? resolution.ScreenId!
                    : await CreateScreenAsync(uow, context, normalized, now, ct);
                stateId = StateIdOf(normalized);
                await uow.Graph.InsertStateAsync(
                    new ScreenState(stateId, screenId, normalized.StateKind, normalized.IdentityHash, normalized.FingerprintVersion, normalized.IdentityTokens, normalized.ElementSetKeys, IdentityStatus.Provisional, now),
                    ct);
                newState = true;
                await AppendAsync(uow, GraphEventTypes.StateDiscovered, now, context, stateId, new() { ["screenId"] = screenId, ["stateKind"] = normalized.StateKind.ToString(), ["resolution"] = resolution.Kind.ToString() }, ct);
                if (resolution.Kind == ResolutionKind.Ambiguous)
                {
                    // Hypotheses are recorded, never merged silently (architecture §7.3 step 5); the operator decides (graph merge, P1).
                    await AppendAsync(
                        uow,
                        GraphEventTypes.IdentityAmbiguous,
                        now,
                        context,
                        stateId,
                        new() { ["candidates"] = string.Join(',', resolution.Candidates.Select(c => $"{c.StateId}@{c.Similarity.ToString("0.###", CultureInfo.InvariantCulture)}")) },
                        ct);
                }

                break;
        }

        var (snapshot, newSnapshot) = await uow.Graph.GetOrInsertSnapshotAsync(recorded.Snapshot with { StateId = stateId, AttemptId = attemptId }, ct);
        foreach (var evidence in recorded.Evidence)
        {
            await uow.Graph.InsertEvidenceIfAbsentAsync(evidence with { SnapshotId = snapshot.SnapshotId }, ct);
        }

        var state = await uow.Graph.GetStateAsync(stateId, ct);
        var variant = await uow.Graph.GetVariantAsync(stateId, context.ContextId, ct);
        var added = normalized.ElementSetKeys.Except(state!.BaseElementKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var removed = state.BaseElementKeys.Except(normalized.ElementSetKeys, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var newVariant = variant is null;
        if (variant is null)
        {
            await uow.Graph.UpsertVariantAsync(new ScreenVariant(stateId, context.ContextId, normalized.ElementSetHash, added, removed, 1, now, now), ct);
            await AppendAsync(uow, GraphEventTypes.VariantDiscovered, now, context, stateId, new() { ["elementSetHash"] = normalized.ElementSetHash }, ct);
        }
        else if (newSnapshot)
        {
            await uow.Graph.UpsertVariantAsync(variant with { ElementSetHash = normalized.ElementSetHash, AddedElementKeys = added, RemovedElementKeys = removed, SnapshotCount = variant.SnapshotCount + 1, LastSeenAt = now }, ct);
        }

        if (newSnapshot)
        {
            await AppendAsync(uow, GraphEventTypes.SnapshotRecorded, now, context, snapshot.SnapshotId, new() { ["stateId"] = stateId, ["identityHash"] = normalized.IdentityHash }, ct);
        }

        return new Placement(stateId, screenId, snapshot.SnapshotId, resolution.Kind, newState, newVariant);
    }

    private async Task<string> CreateScreenAsync(IUnitOfWork uow, ExplorationContext context, NormalizedSnapshot normalized, DateTimeOffset now, CancellationToken ct)
    {
        var seed = ContentHash.Of("mapcue.screen/1", normalized.IdentityHash, normalized.FingerprintVersion.ToString(CultureInfo.InvariantCulture));
        var screen = new LogicalScreen(ContentHash.ShortId("scr", seed), ContentHash.ShortId("screen", seed, 12).Replace('_', '-'), null, IdentityStatus.Provisional, IdentitySource.Structure, now);
        await uow.Graph.InsertScreenAsync(screen, ct);
        await AppendAsync(uow, GraphEventTypes.ScreenDiscovered, now, context, screen.ScreenId, new() { ["screenKey"] = screen.ScreenKey }, ct);
        return screen.ScreenId;
    }

    private async Task<int> AddAnalyzeTaskAsync(IUnitOfWork uow, ExplorationContext context, Placement placement, int depth, string reasonerPromptVersion, DateTimeOffset now, CancellationToken ct)
    {
        if (!placement.NewVariant)
        {
            return 0;
        }

        var key = TaskKeys.AnalyzeState(placement.StateId, context, reasonerPromptVersion);
        var task = NewTask(key, TaskType.AnalyzeState, context, placement.StateId, null, depth, now);
        if (!await uow.Frontier.InsertTaskIfAbsentAsync(task, ct))
        {
            return 0;
        }

        await AppendAsync(uow, GraphEventTypes.TaskCreated, now, context, task.TaskId, new() { ["type"] = task.Type.ToString(), ["status"] = task.Status.ToString() }, ct);
        return 1;
    }

    private static FrontierTask NewTask(string taskKey, TaskType type, ExplorationContext context, string? fromStateId, string? actionKey, int depth, DateTimeOffset now) =>
        new(ContentHash.ShortId("task", taskKey), taskKey, type, FrontierTaskStatus.Pending, 0, depth, context.ContextId, fromStateId, actionKey, null, null, null, null, 0, now, now);

    private static string StateIdOf(NormalizedSnapshot normalized) =>
        ContentHash.ShortId("st", ContentHash.Of("mapcue.state/1", normalized.IdentityHash, normalized.FingerprintVersion.ToString(CultureInfo.InvariantCulture)));

    private Task<long> AppendAsync(IUnitOfWork uow, string type, DateTimeOffset now, ExplorationContext context, string subjectId, Dictionary<string, string?> payload, CancellationToken ct) =>
        uow.Events.AppendAsync(ids.NewId("evt"), type, now, context.ContextId, subjectId, GraphJson.Serialize(payload), ct);
}

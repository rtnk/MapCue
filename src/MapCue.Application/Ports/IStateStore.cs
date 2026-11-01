using MapCue.Domain.Graph;

namespace MapCue.Application.Ports;

/// <summary>
/// Source of truth for graph, frontier and attempts (ADR-0003; SQLite in <c>MapCue.Infrastructure.Sqlite</c>).
/// A committed attempt is exactly one transaction: if the work delegate throws, nothing it wrote is kept.
/// Transactions are serialized (one writer, R-11) and must not be nested.
/// </summary>
public interface IStateStore
{
    Task<T> InTransactionAsync<T>(Func<IUnitOfWork, Task<T>> work, CancellationToken ct);
}

/// <summary>Transactional scope with the repositories of the graph, the frontier and the outbox.</summary>
public interface IUnitOfWork
{
    IGraphRepository Graph { get; }

    IFrontierRepository Frontier { get; }

    IEventOutbox Events { get; }

    IAgentCallRepository AgentCalls { get; }

    IRunRepository Runs { get; }

    /// <summary>Screen captures, contract versions and manual accessibility records (STEP-08).</summary>
    IScreenContractRepository Contracts { get; }
}

/// <summary>Contexts, screens, states, variants, snapshots, evidence, candidates, edges, observations and recipes.</summary>
public interface IGraphRepository
{
    /// <summary>Inserts the context if its signature is new. Returns true when inserted.</summary>
    Task<bool> UpsertContextAsync(ExplorationContext context, DateTimeOffset now, CancellationToken ct);

    Task<ExplorationContext?> GetContextAsync(string contextId, CancellationToken ct);

    Task<IReadOnlyList<ExplorationContext>> ListContextsAsync(CancellationToken ct);

    Task InsertScreenAsync(LogicalScreen screen, CancellationToken ct);

    Task<LogicalScreen?> GetScreenAsync(string screenId, CancellationToken ct);

    Task<IReadOnlyList<LogicalScreen>> ListScreensAsync(CancellationToken ct);

    Task InsertStateAsync(ScreenState state, CancellationToken ct);

    Task<ScreenState?> GetStateAsync(string stateId, CancellationToken ct);

    Task<IReadOnlyList<ScreenState>> ListStatesAsync(CancellationToken ct);

    /// <summary>Records that <paramref name="identityHash"/> was resolved to <paramref name="stateId"/> by similarity (keeps resolution stable).</summary>
    Task AddIdentityAliasAsync(string identityHash, int fingerprintVersion, string stateId, double similarity, DateTimeOffset now, CancellationToken ct);

    /// <summary>Aliases of the given fingerprint version: identity hash → state id.</summary>
    Task<IReadOnlyDictionary<string, string>> GetIdentityAliasesAsync(int fingerprintVersion, CancellationToken ct);

    Task<ScreenVariant?> GetVariantAsync(string stateId, string contextId, CancellationToken ct);

    Task UpsertVariantAsync(ScreenVariant variant, CancellationToken ct);

    Task<IReadOnlyList<ScreenVariant>> ListVariantsAsync(CancellationToken ct);

    /// <summary>Returns the existing snapshot with the same <see cref="UiSnapshot.SnapshotKey"/>, or inserts <paramref name="snapshot"/> (<c>Inserted</c> = true).</summary>
    Task<(UiSnapshot Snapshot, bool Inserted)> GetOrInsertSnapshotAsync(UiSnapshot snapshot, CancellationToken ct);

    Task<UiSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct);

    Task<IReadOnlyList<UiSnapshot>> ListSnapshotsAsync(CancellationToken ct);

    /// <summary>Inserts the evidence unless the same id exists. Returns true when inserted.</summary>
    Task<bool> InsertEvidenceIfAbsentAsync(Evidence evidence, CancellationToken ct);

    /// <summary>Evidence of one context, optionally only of one state (through its snapshots).</summary>
    Task<IReadOnlyList<Evidence>> ListEvidenceAsync(string contextId, string? stateId, CancellationToken ct);

    /// <summary>Inserts the candidate unless <c>(stateId, actionKey)</c> exists. Returns true when inserted.</summary>
    Task<bool> InsertCandidateIfAbsentAsync(ActionCandidate candidate, CancellationToken ct);

    Task<IReadOnlyList<ActionCandidate>> ListCandidatesAsync(string? stateId, CancellationToken ct);

    /// <summary>
    /// Records the risk and status of a candidate in a context (STEP-07). An existing assessment takes the new risk and raises its status
    /// (<see cref="CandidateAssessment.Rank"/>), never lowers it. The candidate must exist.
    /// </summary>
    Task UpsertCandidateAssessmentAsync(CandidateAssessment assessment, CancellationToken ct);

    /// <summary>Assessments of one context (or all), by context, state and action.</summary>
    Task<IReadOnlyList<CandidateAssessment>> ListCandidateAssessmentsAsync(string? contextId, CancellationToken ct);

    Task<ScreenEdge?> GetEdgeAsync(string fromStateId, string actionKey, string contextId, CancellationToken ct);

    Task InsertEdgeAsync(ScreenEdge edge, CancellationToken ct);

    Task UpdateEdgeAsync(ScreenEdge edge, CancellationToken ct);

    Task<IReadOnlyList<ScreenEdge>> ListEdgesAsync(CancellationToken ct);

    /// <summary>Inserts the observation unless one exists for its attempt. Returns true when inserted.</summary>
    Task<bool> InsertObservationIfAbsentAsync(ActionObservation observation, CancellationToken ct);

    Task<ActionObservation?> GetObservationAsync(string attemptId, CancellationToken ct);

    Task<IReadOnlyList<ActionObservation>> ListObservationsAsync(CancellationToken ct);

    Task UpsertRecipeAsync(ReachabilityRecipe recipe, CancellationToken ct);

    Task<ReachabilityRecipe?> GetRecipeAsync(string recipeId, CancellationToken ct);

    /// <summary>Recipes of one context (or of all contexts), by context and target state.</summary>
    Task<IReadOnlyList<ReachabilityRecipe>> ListRecipesAsync(string? contextId, CancellationToken ct);
}

/// <summary>Frontier tasks, attempts and checkpoints.</summary>
public interface IFrontierRepository
{
    /// <summary><c>INSERT … ON CONFLICT(task_key) DO NOTHING</c>. Returns true when inserted.</summary>
    Task<bool> InsertTaskIfAbsentAsync(FrontierTask task, CancellationToken ct);

    Task<FrontierTask?> GetTaskAsync(string taskId, CancellationToken ct);

    Task<FrontierTask?> GetTaskByKeyAsync(string taskKey, CancellationToken ct);

    /// <summary>Updates status, priority, lease and blocked reason of an existing task.</summary>
    Task UpdateTaskAsync(FrontierTask task, CancellationToken ct);

    /// <summary>
    /// Claims a pending task (architecture §5.3, <c>Pending → Leased</c>): sets the lease only if the task is still
    /// <see cref="FrontierTaskStatus.Pending"/>. Returns false when another owner was first or the task is no longer pending.
    /// </summary>
    Task<bool> TryLeaseTaskAsync(string taskId, string owner, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken ct);

    /// <summary>Extends the lease of a task held by <paramref name="owner"/>. Returns false when the task is not leased by it.</summary>
    Task<bool> RenewTaskLeaseAsync(string taskId, string owner, DateTimeOffset expiresAt, CancellationToken ct);

    /// <summary>Tasks in scheduling order: priority (desc), depth, creation sequence.</summary>
    Task<IReadOnlyList<FrontierTask>> ListTasksAsync(FrontierTaskStatus? status, CancellationToken ct);

    Task<FrontierStats> GetStatsAsync(CancellationToken ct);

    /// <summary>Inserts the attempt unless its id exists. Returns true when inserted.</summary>
    Task<bool> InsertAttemptIfAbsentAsync(TaskAttempt attempt, CancellationToken ct);

    Task UpdateAttemptAsync(TaskAttempt attempt, CancellationToken ct);

    Task<TaskAttempt?> GetAttemptAsync(string attemptId, CancellationToken ct);

    Task<IReadOnlyList<TaskAttempt>> ListAttemptsAsync(string? taskId, CancellationToken ct);

    /// <summary>Stores the validated action of an <c>ExploreAction</c> task unless one exists. Returns true when inserted.</summary>
    Task<bool> InsertTaskActionIfAbsentAsync(TaskAction action, CancellationToken ct);

    Task<TaskAction?> GetTaskActionAsync(string taskId, CancellationToken ct);

    /// <summary>All stored task actions, by task id (navigation over known edges reads them at once).</summary>
    Task<IReadOnlyList<TaskAction>> ListTaskActionsAsync(CancellationToken ct);

    Task InsertCheckpointAsync(Checkpoint checkpoint, CancellationToken ct);

    Task<Checkpoint?> GetLatestCheckpointAsync(string runId, CancellationToken ct);
}

/// <summary>Transactional outbox (architecture §12): events are written in the same transaction as the change they describe.</summary>
public interface IEventOutbox
{
    /// <summary>Appends an event and returns its sequence number.</summary>
    Task<long> AppendAsync(string eventId, string type, DateTimeOffset occurredAt, string? contextId, string? subjectId, string payloadJson, CancellationToken ct);

    Task<IReadOnlyList<OutboxEvent>> ListAsync(long afterSeq, int limit, CancellationToken ct);

    Task<long> LastSeqAsync(CancellationToken ct);
}

/// <summary>Reasoner calls with their validation result (ADR-0004: rejections are kept as evidence).</summary>
public interface IAgentCallRepository
{
    /// <summary>Inserts the call unless its id exists. Returns true when inserted.</summary>
    Task<bool> InsertIfAbsentAsync(AgentCall agentCall, CancellationToken ct);

    /// <summary>Calls in insertion order, optionally only of one state.</summary>
    Task<IReadOnlyList<AgentCall>> ListAsync(string? stateId, CancellationToken ct);

    /// <summary>Usage of every recorded call (budgets resume from it).</summary>
    Task<AgentUsageTotals> GetUsageTotalsAsync(CancellationToken ct);
}

/// <summary>
/// The validated action an <c>ExploreAction</c> task executes (STEP-05). It is what the reasoner proposed and MapCue validated
/// (ADR-0004), stored so the task (and navigation over its edge) does not depend on the reasoner session. <see cref="ElementKey"/> and
/// <see cref="ExpectedIdentityHash"/> feed the pre-execution check (architecture §9.3 step 6).
/// </summary>
public sealed record TaskAction(string TaskId, ValidatedDeviceAction Action, string? ElementKey, string ExpectedIdentityHash, DateTimeOffset CreatedAt);

/// <summary>Exploration runs (architecture §5.4).</summary>
public interface IRunRepository
{
    Task InsertAsync(ExplorationRun run, CancellationToken ct);

    Task UpdateAsync(ExplorationRun run, CancellationToken ct);

    Task<ExplorationRun?> GetAsync(string runId, CancellationToken ct);

    /// <summary>The most recently started run (ties broken by id), or null.</summary>
    Task<ExplorationRun?> GetLatestAsync(CancellationToken ct);

    /// <summary>Runs that did not reach <see cref="RunStatus.Finished"/>, in start order (a crashed process leaves them behind).</summary>
    Task<IReadOnlyList<ExplorationRun>> ListUnfinishedAsync(CancellationToken ct);

    Task<DeviceLease?> GetDeviceLeaseAsync(string deviceId, CancellationToken ct);

    /// <summary>
    /// Takes or renews the lease of <see cref="DeviceLease.DeviceId"/> for its owner. Succeeds when the device is free, already
    /// leased by the same owner, or leased by another owner whose lease expired before <paramref name="now"/>.
    /// </summary>
    Task<bool> TryAcquireDeviceLeaseAsync(DeviceLease lease, DateTimeOffset now, CancellationToken ct);

    /// <summary>Releases every device lease of the run. Returns the number of released leases.</summary>
    Task<int> ReleaseDeviceLeasesAsync(string ownerRunId, CancellationToken ct);
}

/// <summary>Screen captures, contract versions and manual accessibility records (STEP-08).</summary>
public interface IScreenContractRepository
{
    /// <summary>Inserts the capture or replaces the capture of the same kind, state and context (the latest one wins).</summary>
    Task UpsertCaptureAsync(ScreenCapture capture, CancellationToken ct);

    /// <summary>Captures of one context (or all), by context, state and kind.</summary>
    Task<IReadOnlyList<ScreenCapture>> ListCapturesAsync(string? contextId, CancellationToken ct);

    /// <summary>Inserts the record unless its id exists. Returns true when inserted.</summary>
    Task<bool> InsertRecordAsync(AccessibilityRecord record, CancellationToken ct);

    /// <summary>Manual records, by screen, context and record time.</summary>
    Task<IReadOnlyList<AccessibilityRecord>> ListRecordsAsync(CancellationToken ct);

    /// <summary>The latest version of a contract, or null.</summary>
    Task<ScreenContractVersion?> GetLatestContractAsync(string screenId, string groupKey, CancellationToken ct);

    Task InsertContractAsync(ScreenContractVersion contract, CancellationToken ct);

    /// <summary>All versions, by screen, group and version.</summary>
    Task<IReadOnlyList<ScreenContractVersion>> ListContractsAsync(CancellationToken ct);
}

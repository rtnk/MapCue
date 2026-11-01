namespace MapCue.Domain.Graph;

/// <summary>Lifecycle of a run (architecture §5.4): <c>Created → Preflight → Running → Stopping → Finished(reason)</c>.</summary>
public enum RunStatus
{
    Created,
    Preflight,
    Running,
    Stopping,
    Finished,
}

/// <summary>Why a run finished (architecture §5.4).</summary>
public enum RunFinishReason
{
    /// <summary>Every reachable task within the budgets is in a final state: the <em>discovered</em> graph is exhausted (CAP-0112).</summary>
    Exhausted,
    BudgetExhausted,
    AllRemainingBlocked,
    StoppedByOperator,
    PreflightFailed,
    Failed,
}

/// <summary>
/// Counters of a run, kept with the run row and reported by <c>mapcue status</c>. <see cref="NewScreens"/> and
/// <see cref="RunCounters.NewStates"/> are what this run discovered (budgets.maxScreens and maxStates are per run);
/// <see cref="RecoveredTasks"/> are tasks of interrupted runs reconciled at the start; <see cref="RepresentedTasks"/> are list rows
/// left unexplored because other rows of the same template stand for them.
/// </summary>
public sealed record RunCounters(
    int Analyses,
    int Attempts,
    int DeviceActions,
    int NavigationActions,
    int Resets,
    int NewStates,
    int AppLeft,
    int Blocked,
    int OutcomeUnknown)
{
    public static RunCounters Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);

    public int NewScreens { get; init; }

    public int RecoveredTasks { get; init; }

    public int RepresentedTasks { get; init; }

    /// <summary>Screen captures of this run (STEP-08): full hierarchies and keyboard focus probes.</summary>
    public int ScreenCaptures { get; init; }

    /// <summary>Key presses of keyboard focus probes (STEP-08).</summary>
    public int KeyPresses { get; init; }

    /// <summary>Device operations charged to <c>budgets.maxActions</c>: explored actions, navigation steps, resets and key presses.</summary>
    public int ChargedActions => DeviceActions + NavigationActions + Resets + KeyPresses;
}

/// <summary>
/// One exploration run (architecture §5.4, §12). <see cref="Detail"/> is a short redacted explanation of the finish reason.
/// <see cref="Seed"/> is the seed of the run's id generator, when given (two runs with the same seed give the same ids).
/// </summary>
public sealed record ExplorationRun(
    string RunId,
    string ProfileId,
    string? ContextId,
    string? DeviceId,
    string Reasoner,
    string ConfigHash,
    string? Seed,
    RunStatus Status,
    RunFinishReason? FinishReason,
    string? Detail,
    RunCounters Counters,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>
/// Exclusive lease of a device by one run (architecture §5.1: one UI executor per device). The owner renews it while it runs and
/// releases it when it finishes; a lease of a run that died is reclaimed by reconciliation, or by anyone once it expired.
/// </summary>
public sealed record DeviceLease(string DeviceId, string OwnerRunId, DateTimeOffset AcquiredAt, DateTimeOffset ExpiresAt);

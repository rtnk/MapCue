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

/// <summary>Counters of a run, kept with the run row and reported by <c>mapcue status</c>.</summary>
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

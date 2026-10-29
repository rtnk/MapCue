using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>
/// Deterministic order of the frontier (architecture §5.1, STEP-06). <c>AnalyzeState</c> tasks come first (no device; store order:
/// priority, depth, creation). <c>ExploreAction</c> tasks are ordered by:
/// <list type="number">
/// <item><b>Locality:</b> device actions needed to reach the source state (0 on the current state, BFS over replayable edges; a reset
/// plus the path from the start state when there is no path; unreachable last). This gives DFS without replay cost.</item>
/// <item><b>Risk:</b> the static <see cref="FrontierTask.Priority"/> from <see cref="PriorityOf"/> (higher first): safe and navigational
/// actions before state-changing ones, <c>back</c> after the other actions of a state so a state is explored before it is left.</item>
/// <item><b>Novelty:</b> an action whose key was not yet observed from another state (e.g. a tab bar button seen on every tab) before a
/// known one.</item>
/// <item><b>Depth</b> (shallower first), then the creation sequence: a total order, so equal inputs give the same schedule.</item>
/// </list>
/// </summary>
public static class FrontierScheduler
{
    /// <summary>Distance of a source state that cannot be reached over known edges, even after a reset.</summary>
    public const int Unreachable = int.MaxValue / 4;

    public const int DefaultPriority = 0;

    /// <summary><c>back</c> runs after the other actions of the same state.</summary>
    public const int BackPriority = -1;

    /// <summary>State-changing (or unknown) actions, allowed only with disposable data, run after everything safe.</summary>
    public const int StateChangingPriority = -2;

    /// <summary>Other risk classes (allowed only with an explicit policy) run last.</summary>
    public const int HighRiskPriority = -3;

    /// <summary>The static priority of an explore task, from the validated action (stored with the task when it is scheduled).</summary>
    public static int PriorityOf(ValidatedDeviceAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.EffectiveRisk switch
        {
            RiskClass.Safe or RiskClass.Navigational => action.Type == ActionType.Back ? BackPriority : DefaultPriority,
            RiskClass.StateChanging or RiskClass.Unknown => StateChangingPriority,
            _ => HighRiskPriority,
        };
    }

    /// <summary>Orders explore tasks; <paramref name="distance"/> and <paramref name="knownAction"/> are evaluated once per task.</summary>
    public static IReadOnlyList<ScheduledTask> Order(IEnumerable<FrontierTask> tasks, Func<FrontierTask, int> distance, Func<FrontierTask, bool> knownAction)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(distance);
        ArgumentNullException.ThrowIfNull(knownAction);
        return tasks
            .Select(t => new ScheduledTask(t, distance(t), knownAction(t)))
            .OrderBy(s => s.Distance)
            .ThenByDescending(s => s.Task.Priority)
            .ThenBy(s => s.KnownAction)
            .ThenBy(s => s.Task.Depth)
            .ThenBy(s => s.Task.CreatedSeq)
            .ThenBy(s => s.Task.TaskId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Device actions an explore task is expected to charge to <c>budgets.maxActions</c>: the way to its source state plus the action
    /// itself; an unreachable source costs the reset that finds out.
    /// </summary>
    public static int EstimatedCost(ScheduledTask scheduled)
    {
        ArgumentNullException.ThrowIfNull(scheduled);
        return scheduled.Distance >= Unreachable ? 1 : scheduled.Distance + 1;
    }
}

/// <summary>An explore task with the scheduling facts it was ordered by.</summary>
public sealed record ScheduledTask(FrontierTask Task, int Distance, bool KnownAction);

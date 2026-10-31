using MapCue.Application.Configuration;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>
/// Inputs of one run. <see cref="SetupFlowPath"/> is the resolved path of the profile's setup flow (null: plain <c>launchApp</c>).
/// <see cref="DeviceId"/> null means the only connected, allowlisted device of the profile's platform.
/// <see cref="InputValues"/> are the profile's resolved input values (masked as <c>‹input›</c> by normalization; never stored).
/// </summary>
public sealed record ExplorationOptions(ProfileSettings Profile, string RunsDirectory, string ConfigHash)
{
    public string? DeviceId { get; init; }

    public string? SetupFlowPath { get; init; }

    public string? Seed { get; init; }

    public string AppBuildId { get; init; } = "unknown";

    /// <summary>Overrides the profile's locale (null: <see cref="ProfileSettings.Locale"/>).</summary>
    public string? Locale { get; init; }

    /// <summary>Overrides the profile's scenario (null: <see cref="ProfileSettings.ScenarioId"/>).</summary>
    public string? ScenarioId { get; init; }

    public IReadOnlyCollection<string>? InputValues { get; init; }

    /// <summary>Pause between stabilization observations.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Waits between observations; tests replace it to move a manual clock.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>Hard timeout of one setup flow run (TapCue <c>test</c>: JVM start, driver, the flow itself).</summary>
    public TimeSpan SetupFlowTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Free disk space required for artifacts at preflight (architecture §5.4).</summary>
    public long MinFreeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>Consecutive device failures after which the run stops with <see cref="RunFinishReason.Failed"/>.</summary>
    public int MaxConsecutiveDeviceFailures { get; init; } = 3;

    /// <summary>Progress lines for the console (already redacted).</summary>
    public Action<string>? Progress { get; init; }

    /// <summary>How long a task or device lease lasts without renewal (renewed at every attempt phase and loop iteration).</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// True when the caller holds the workspace lock (<c>.mapcue/lock</c>, the CLI does): no other process can be writing, so leases
    /// of other runs and runs left unfinished belong to dead processes and are reconciled at once. Otherwise only expired task leases
    /// are reclaimed and other runs are left alone.
    /// </summary>
    public bool WorkspaceLockHeld { get; init; }

    /// <summary>Called after each attempt phase is journaled (committed to the store), before the next step runs.</summary>
    public Action<AttemptPhaseEvent>? OnAttemptPhase { get; init; }
}

/// <summary>An attempt reached <see cref="Phase"/>; the phase is already durable.</summary>
public sealed record AttemptPhaseEvent(string TaskId, string AttemptId, int N, AttemptPhase Phase);

/// <summary>Result of a run.</summary>
public sealed record ExplorationResult(string RunId, string? ContextId, RunFinishReason Reason, string? Detail, RunCounters Counters);

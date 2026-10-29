using System.Diagnostics;
using MapCue.Application.Configuration;
using MapCue.Application.Exploration;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// STEP-06 reconciliation (architecture §5.3): a child process exploring the demo model is killed (SIGKILL) right after each attempt
/// phase is journaled; a new run in the same workspace recovers without losing progress or committing anything twice. The in-process
/// tests cover lease expiry, device leases and the retry limit.
/// </summary>
public class RecoveryTests
{
    private const string CrashHostAssembly = "MapCue.EndToEnd.CrashHost.dll";

    /// <summary>Crashes at the third attempt that reaches the phase (some progress is committed before).</summary>
    private const int Occurrence = 3;

    public static TheoryData<AttemptPhase> Phases =>
    [
        AttemptPhase.Created,
        AttemptPhase.RestoreStarted,
        AttemptPhase.RestoreVerified,
        AttemptPhase.PreObserved,
        AttemptPhase.PolicyChecked,
        AttemptPhase.ActionDispatched,
        AttemptPhase.ActionReturned,
        AttemptPhase.PostObserved,
        AttemptPhase.Committed,
    ];

    private sealed record Crash(string TaskId, string AttemptId, AttemptPhase Phase);

    private static async Task<Crash> CrashChildAsync(string root, AttemptPhase phase)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : Environment.ProcessPath ?? "dotnet";
        var info = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { Path.Combine(AppContext.BaseDirectory, CrashHostAssembly), root, phase.ToString(), Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture), "crash" })
        {
            info.ArgumentList.Add(arg);
        }

        using var child = Process.Start(info)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await child.WaitForExitAsync(timeout.Token);
        var marker = Path.Combine(root, "crashed.txt");
        Assert.True(File.Exists(marker), $"the child did not crash (exit {child.ExitCode}): {await stdout} {await stderr}");
        Assert.NotEqual(0, child.ExitCode);
        var lines = await File.ReadAllLinesAsync(marker);
        return new Crash(lines[0], lines[1], Enum.Parse<AttemptPhase>(lines[2]));
    }

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task A_run_killed_in_any_attempt_phase_is_recovered_without_loss_or_double_commit(AttemptPhase phase)
    {
        var root = ExplorationHarness.NewRoot();
        try
        {
            var crash = await CrashChildAsync(root, phase);
            Assert.Equal(phase, crash.Phase);

            // The operating system released the workspace lock of the dead process.
            using var workspaceLock = WorkspaceLock.Acquire(root);
            await using var h = ExplorationHarness.Open(root);
            var crashedRun = Assert.Single(await h.ReadAsync(u => u.Runs.ListUnfinishedAsync(CancellationToken.None)));
            var journaled = await h.ReadAsync(u => u.Frontier.GetAttemptAsync(crash.AttemptId, CancellationToken.None));
            Assert.Equal((phase, crashedRun.RunId), (journaled!.Phase, journaled.RunId));
            var before = await h.ReadAsync(u => u.Frontier.GetTaskAsync(crash.TaskId, CancellationToken.None));
            Assert.Equal(phase == AttemptPhase.Committed ? FrontierTaskStatus.Completed : FrontierTaskStatus.Leased, before!.Status);
            var action = (await h.ReadAsync(u => u.Frontier.GetTaskActionAsync(crash.TaskId, CancellationToken.None)))!.Action;

            var result = await h.Engine(seed: "resume").RunAsync(h.Options("resume") with { WorkspaceLockHeld = true }, CancellationToken.None);

            Assert.Equal(RunFinishReason.Exhausted, result.Reason);
            var recoveredRun = await h.ReadAsync(u => u.Runs.GetAsync(crashedRun.RunId, CancellationToken.None));
            Assert.Equal((RunStatus.Finished, RunFinishReason.Failed), (recoveredRun!.Status, recoveredRun.FinishReason!.Value));
            Assert.StartsWith(ExplorationEngine.InterruptedError, recoveredRun.Detail, StringComparison.Ordinal);
            Assert.Empty(await h.ReadAsync(u => u.Runs.ListUnfinishedAsync(CancellationToken.None)));
            Assert.Null(await h.ReadAsync(u => u.Runs.GetDeviceLeaseAsync(h.Device.Device.Id.Value, CancellationToken.None)));
            Assert.Equal(0, (await h.ReadAsync(u => u.Frontier.GetStatsAsync(CancellationToken.None))).Leased);
            var attempts = await h.ReadAsync(u => u.Frontier.ListAttemptsAsync(null, CancellationToken.None));
            Assert.All(attempts, a => Assert.NotNull(a.EndedAt));

            // No double commit: one observation per committed attempt, edge counters equal to their observations.
            var observations = await h.ReadAsync(u => u.Graph.ListObservationsAsync(CancellationToken.None));
            var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
            Assert.Equal(attempts.Count(a => a.Phase == AttemptPhase.Committed), observations.Count);
            Assert.Equal(observations.Count, edges.Sum(e => e.ObservationCount));
            Assert.All(edges, e => Assert.Equal(1, e.ObservationCount));

            var task = await h.ReadAsync(u => u.Frontier.GetTaskAsync(crash.TaskId, CancellationToken.None));
            var taskAttempts = attempts.Where(a => a.TaskId == crash.TaskId).OrderBy(a => a.N).ToList();
            await using var reference = ExplorationHarness.Create();
            await reference.RunAsync(seed: "reference");
            var expected = await reference.CanonicalAsync();
            var graph = await h.CanonicalAsync();
            if (phase < AttemptPhase.ActionDispatched)
            {
                // Nothing reached the device: the attempt is abandoned and the task done again, so nothing is lost.
                Assert.Equal(FrontierTaskStatus.Completed, task!.Status);
                Assert.Equal([AttemptPhase.Abandoned, AttemptPhase.Committed], taskAttempts.Select(a => a.Phase));
                Assert.StartsWith(ExplorationEngine.InterruptedError, taskAttempts[0].Error, StringComparison.Ordinal);
                Assert.Equal(expected.States, graph.States);
                Assert.Equal(expected.Edges, graph.Edges);
            }
            else if (phase < AttemptPhase.Committed)
            {
                // The action may have run: OutcomeUnknown, never executed again; everything else of the graph is still explored.
                Assert.Equal(FrontierTaskStatus.OutcomeUnknown, task!.Status);
                var unknown = Assert.Single(taskAttempts);
                Assert.Equal((ActionOutcome?)ActionOutcome.OutcomeUnknown, unknown.Outcome);
                Assert.DoesNotContain(h.Device.ExecutedActions, a => a.ActionId == action.ActionId);
                Assert.All(graph.Edges, e => Assert.Contains(e, expected.Edges));
                Assert.DoesNotContain(edges, e => e.FromStateId == task.FromStateId && e.ActionKey == task.ActionKey);
            }
            else
            {
                // Committed before the kill: kept as it is and not repeated.
                Assert.Equal(FrontierTaskStatus.Completed, task!.Status);
                Assert.Equal([AttemptPhase.Committed], taskAttempts.Select(a => a.Phase));
                Assert.DoesNotContain(h.Device.ExecutedActions, a => a.ActionId == action.ActionId);
                Assert.Equal(expected.States, graph.States);
                Assert.Equal(expected.Edges, graph.Edges);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ---------------------------------------------------------------- in-process reconciliation

    /// <summary>A workspace stopped by the action budget, with a pending explore task made "leased" by a ghost run in <paramref name="phase"/>.</summary>
    private static async Task<(ExplorationHarness Harness, FrontierTask Task)> LeasedByGhostAsync(AttemptPhase phase, int interruptedBefore = 0)
    {
        var h = ExplorationHarness.Create(settings: ExplorationHarness.DemoSettings(Budgets.Default with { MaxActions = 6 }));
        await h.RunAsync(seed: "first");
        var now = h.Clock.UtcNow;
        var task = await h.Store.InTransactionAsync(
            async uow =>
            {
                var pending = (await uow.Frontier.ListTasksAsync(FrontierTaskStatus.Pending, CancellationToken.None)).First(t => t.Type == TaskType.ExploreAction);
                await uow.Runs.InsertAsync(new ExplorationRun("run_ghost", "basic", pending.ContextId, "fake-emulator-1", "rule-based", "sha256:config", null, RunStatus.Running, null, null, RunCounters.Zero, now, null), CancellationToken.None);
                for (var n = 1; n <= interruptedBefore; n++)
                {
                    await uow.Frontier.InsertAttemptIfAbsentAsync(new TaskAttempt($"att_old_{n}", pending.TaskId, n, AttemptPhase.Abandoned, null, $"{ExplorationEngine.InterruptedError}: earlier crash", null, now, now), CancellationToken.None);
                }

                Assert.True(await uow.Frontier.TryLeaseTaskAsync(pending.TaskId, "run_ghost", now.AddMinutes(10), now, CancellationToken.None));
                await uow.Frontier.InsertAttemptIfAbsentAsync(new TaskAttempt("att_ghost", pending.TaskId, interruptedBefore + 1, phase, null, null, null, now, null) { RunId = "run_ghost" }, CancellationToken.None);
                return pending;
            },
            CancellationToken.None);
        return (h, task);
    }

    [Fact]
    public async Task Without_the_workspace_lock_only_expired_leases_are_reclaimed()
    {
        var (h, task) = await LeasedByGhostAsync(AttemptPhase.PolicyChecked);
        await using (h)
        {
            var live = await h.RunAsync(seed: "second", settings: ExplorationHarness.DemoSettings());

            // The ghost may still be alive: its task and its run are left alone, so the graph is not reported as exhausted.
            Assert.Equal(RunFinishReason.AllRemainingBlocked, live.Reason);
            Assert.Contains("leased by other runs", live.Detail, StringComparison.Ordinal);
            Assert.Equal(FrontierTaskStatus.Leased, (await h.ReadAsync(u => u.Frontier.GetTaskAsync(task.TaskId, CancellationToken.None)))!.Status);
            Assert.Equal(RunStatus.Running, (await h.ReadAsync(u => u.Runs.GetAsync("run_ghost", CancellationToken.None)))!.Status);

            h.Clock.Advance(TimeSpan.FromMinutes(11));
            var expired = await h.RunAsync(seed: "third", settings: ExplorationHarness.DemoSettings());

            Assert.Equal(RunFinishReason.Exhausted, expired.Reason);
            Assert.Equal(1, expired.Counters.RecoveredTasks);
            Assert.Equal(FrontierTaskStatus.Completed, (await h.ReadAsync(u => u.Frontier.GetTaskAsync(task.TaskId, CancellationToken.None)))!.Status);
            var ghost = await h.ReadAsync(u => u.Frontier.GetAttemptAsync("att_ghost", CancellationToken.None));
            Assert.Equal(AttemptPhase.Abandoned, ghost!.Phase);
        }
    }

    [Fact]
    public async Task With_the_workspace_lock_a_dead_run_is_finished_and_its_device_lease_released()
    {
        var (h, task) = await LeasedByGhostAsync(AttemptPhase.ActionReturned);
        await using (h)
        {
            var now = h.Clock.UtcNow;
            await h.ReadAsync(uow => uow.Runs.TryAcquireDeviceLeaseAsync(new DeviceLease("fake-emulator-1", "run_ghost", now, now.AddMinutes(10)), now, CancellationToken.None));

            var result = await h.Engine(seed: "second", settings: ExplorationHarness.DemoSettings()).RunAsync(h.Options("second") with { WorkspaceLockHeld = true }, CancellationToken.None);

            Assert.Equal(RunFinishReason.Exhausted, result.Reason);
            var ghost = await h.ReadAsync(u => u.Runs.GetAsync("run_ghost", CancellationToken.None));
            Assert.Equal((RunStatus.Finished, RunFinishReason.Failed), (ghost!.Status, ghost.FinishReason!.Value));
            var recovered = await h.ReadAsync(u => u.Frontier.GetTaskAsync(task.TaskId, CancellationToken.None));
            Assert.Equal(FrontierTaskStatus.OutcomeUnknown, recovered!.Status);
            var events = await h.ReadAsync(u => u.Events.ListAsync(0, 100_000, CancellationToken.None));
            Assert.Contains(events, e => e.Type == RunEventTypes.RunRecovered && e.SubjectId == "run_ghost");
            Assert.Contains(events, e => e.Type == RunEventTypes.TaskRecovered && e.SubjectId == task.TaskId && e.PayloadJson.Contains("OutcomeUnknown", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_device_leased_by_a_live_run_fails_the_preflight()
    {
        var (h, _) = await LeasedByGhostAsync(AttemptPhase.Created);
        await using (h)
        {
            var now = h.Clock.UtcNow;
            await h.ReadAsync(uow => uow.Runs.TryAcquireDeviceLeaseAsync(new DeviceLease("fake-emulator-1", "run_ghost", now, now.AddMinutes(10)), now, CancellationToken.None));
            var actions = h.Device.ExecutedActions.Count;

            var result = await h.RunAsync(seed: "second", settings: ExplorationHarness.DemoSettings());

            Assert.Equal(RunFinishReason.PreflightFailed, result.Reason);
            Assert.Contains("leased by run run_ghost", result.Detail, StringComparison.Ordinal);
            Assert.Equal(actions, h.Device.ExecutedActions.Count);
        }
    }

    [Fact]
    public async Task A_task_interrupted_more_than_max_retries_times_fails()
    {
        var (h, task) = await LeasedByGhostAsync(AttemptPhase.RestoreStarted, interruptedBefore: Budgets.Default.MaxRetries);
        await using (h)
        {
            var result = await h.Engine(seed: "second", settings: ExplorationHarness.DemoSettings()).RunAsync(h.Options("second") with { WorkspaceLockHeld = true }, CancellationToken.None);

            Assert.Equal(1, result.Counters.RecoveredTasks);
            var failed = await h.ReadAsync(u => u.Frontier.GetTaskAsync(task.TaskId, CancellationToken.None));
            Assert.Equal(FrontierTaskStatus.Failed, failed!.Status);
            var attempts = await h.ReadAsync(u => u.Frontier.ListAttemptsAsync(task.TaskId, CancellationToken.None));
            Assert.Equal(Budgets.Default.MaxRetries + 1, attempts.Count);
            Assert.All(attempts, a => Assert.Equal(AttemptPhase.Abandoned, a.Phase));
        }
    }
}

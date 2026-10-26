using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

/// <summary>Migration 0004 (STEP-05): runs and the validated action of explore tasks.</summary>
public sealed class RunStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mapcue-runs-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task Runs_round_trip_and_the_latest_is_found()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        var first = new ExplorationRun("run_a", "basic", null, null, "rule-based", "sha256:c", "7", RunStatus.Created, null, null, RunCounters.Zero, _clock.UtcNow, null);
        var second = first with { RunId = "run_b", StartedAt = _clock.UtcNow.AddMinutes(1) };

        await store.InTransactionAsync(
            async uow =>
            {
                await uow.Runs.InsertAsync(first, CancellationToken.None);
                await uow.Runs.InsertAsync(second, CancellationToken.None);
                await uow.Runs.UpdateAsync(first with { Status = RunStatus.Finished, FinishReason = RunFinishReason.BudgetExhausted, Detail = "budgets.maxActions reached", Counters = RunCounters.Zero with { Attempts = 3, AppLeft = 1 }, EndedAt = _clock.UtcNow }, CancellationToken.None);
                return 0;
            },
            CancellationToken.None);

        var (a, latest) = await store.InTransactionAsync(async uow => (await uow.Runs.GetAsync("run_a", CancellationToken.None), await uow.Runs.GetLatestAsync(CancellationToken.None)), CancellationToken.None);
        Assert.Equal((RunStatus.Finished, RunFinishReason.BudgetExhausted, 3, 1, "7"), (a!.Status, a.FinishReason!.Value, a.Counters.Attempts, a.Counters.AppLeft, a.Seed));
        Assert.Equal("run_b", latest!.RunId);
        await Assert.ThrowsAnyAsync<Exception>(() => store.InTransactionAsync<int>(
            async uow =>
            {
                await uow.Runs.UpdateAsync(first with { RunId = "run_missing" }, CancellationToken.None);
                return 0;
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task Task_action_is_stored_once_per_task()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        var context = ExplorationContext.Create("basic", Platform.Android, "com.example.demo", "1");
        var task = new FrontierTask("task_1", "key_1", TaskType.ExploreAction, FrontierTaskStatus.Pending, 0, 0, context.ContextId, null, "ak", null, null, null, null, 0, _clock.UtcNow, _clock.UtcNow);
        var action = new ValidatedDeviceAction("act_1", "com.example.demo", ActionType.Scroll, RiskClass.Safe, "com.example.demo:id/list", null, null, ScrollDirection.Up);

        var (inserted, again, read) = await store.InTransactionAsync(
            async uow =>
            {
                await uow.Graph.UpsertContextAsync(context, _clock.UtcNow, CancellationToken.None);
                await uow.Frontier.InsertTaskIfAbsentAsync(task, CancellationToken.None);
                var first = await uow.Frontier.InsertTaskActionIfAbsentAsync(new TaskAction("task_1", action, "id:x", "sha256:h", _clock.UtcNow), CancellationToken.None);
                var second = await uow.Frontier.InsertTaskActionIfAbsentAsync(new TaskAction("task_1", action with { ActionId = "other" }, null, "sha256:z", _clock.UtcNow), CancellationToken.None);
                return (first, second, await uow.Frontier.GetTaskActionAsync("task_1", CancellationToken.None));
            },
            CancellationToken.None);

        Assert.True(inserted);
        Assert.False(again);
        Assert.Equal(action, read!.Action);
        Assert.Equal(("id:x", "sha256:h"), (read.ElementKey, read.ExpectedIdentityHash));
    }

    [Fact]
    public void Migration_4_applies_on_top_of_version_3()
    {
        using (SqliteStateStore.Open(_path, _clock, targetVersion: 3))
        {
        }

        using var upgraded = SqliteStateStore.Open(_path, _clock);
        Assert.Equal(4, SqliteMigrator.LatestVersion);
    }
}

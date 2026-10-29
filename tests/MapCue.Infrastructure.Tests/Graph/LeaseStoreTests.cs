using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

/// <summary>Migration 0005 (STEP-06): task claims, device leases, the run of an attempt and represented list rows.</summary>
public sealed class LeaseStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mapcue-leases-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new();
    private readonly ExplorationContext _context = ExplorationContext.Create("basic", Platform.Android, "com.example.demo", "1");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private FrontierTask Task(string id) =>
        new(id, "key_" + id, TaskType.ExploreAction, FrontierTaskStatus.Pending, 0, 0, _context.ContextId, null, "ak", null, null, null, null, 0, _clock.UtcNow, _clock.UtcNow);

    private ExplorationRun Run(string id) =>
        new(id, "basic", null, null, "rule-based", "sha256:c", null, RunStatus.Running, null, null, RunCounters.Zero, _clock.UtcNow, null);

    private static Task<T> InTx<T>(SqliteStateStore store, Func<IUnitOfWork, Task<T>> work) => store.InTransactionAsync(work, CancellationToken.None);

    private async Task SeedAsync(SqliteStateStore store, params string[] runs) =>
        await InTx(store, async uow =>
        {
            await uow.Graph.UpsertContextAsync(_context, _clock.UtcNow, CancellationToken.None);
            foreach (var run in runs)
            {
                await uow.Runs.InsertAsync(Run(run), CancellationToken.None);
            }

            return 0;
        });

    [Fact]
    public async Task A_pending_task_is_claimed_once()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store);
        await InTx(store, uow => uow.Frontier.InsertTaskIfAbsentAsync(Task("t1"), CancellationToken.None));
        var expires = _clock.UtcNow.AddMinutes(10);

        var first = await InTx(store, uow => uow.Frontier.TryLeaseTaskAsync("t1", "run_a", expires, _clock.UtcNow, CancellationToken.None));
        var second = await InTx(store, uow => uow.Frontier.TryLeaseTaskAsync("t1", "run_b", expires, _clock.UtcNow, CancellationToken.None));
        var renewedByOther = await InTx(store, uow => uow.Frontier.RenewTaskLeaseAsync("t1", "run_b", expires.AddMinutes(5), CancellationToken.None));
        var renewed = await InTx(store, uow => uow.Frontier.RenewTaskLeaseAsync("t1", "run_a", expires.AddMinutes(5), CancellationToken.None));
        var task = await InTx(store, uow => uow.Frontier.GetTaskAsync("t1", CancellationToken.None));

        Assert.True(first);
        Assert.False(second);
        Assert.False(renewedByOther);
        Assert.True(renewed);
        Assert.Equal((FrontierTaskStatus.Leased, "run_a", expires.AddMinutes(5)), (task!.Status, task.LeaseOwner, task.LeaseExpiresAt));
        Assert.Equal(1, (await InTx(store, uow => uow.Frontier.GetStatsAsync(CancellationToken.None))).Leased);
    }

    [Fact]
    public async Task A_device_lease_is_exclusive_until_it_expires_or_is_released()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store, "run_a", "run_b");
        var now = _clock.UtcNow;
        DeviceLease Lease(string owner, DateTimeOffset at) => new("emulator-5554", owner, at, at.AddMinutes(10));

        Assert.True(await InTx(store, uow => uow.Runs.TryAcquireDeviceLeaseAsync(Lease("run_a", now), now, CancellationToken.None)));
        Assert.False(await InTx(store, uow => uow.Runs.TryAcquireDeviceLeaseAsync(Lease("run_b", now), now, CancellationToken.None)));

        // Renewal by the owner keeps the acquisition time and moves the expiry.
        Assert.True(await InTx(store, uow => uow.Runs.TryAcquireDeviceLeaseAsync(Lease("run_a", now.AddMinutes(5)), now.AddMinutes(5), CancellationToken.None)));
        var renewed = await InTx(store, uow => uow.Runs.GetDeviceLeaseAsync("emulator-5554", CancellationToken.None));
        Assert.Equal((now, now.AddMinutes(15)), (renewed!.AcquiredAt, renewed.ExpiresAt));

        // An expired lease can be taken over.
        var later = now.AddMinutes(16);
        Assert.True(await InTx(store, uow => uow.Runs.TryAcquireDeviceLeaseAsync(Lease("run_b", later), later, CancellationToken.None)));
        Assert.Equal("run_b", (await InTx(store, uow => uow.Runs.GetDeviceLeaseAsync("emulator-5554", CancellationToken.None)))!.OwnerRunId);

        Assert.Equal(0, await InTx(store, uow => uow.Runs.ReleaseDeviceLeasesAsync("run_a", CancellationToken.None)));
        Assert.Equal(1, await InTx(store, uow => uow.Runs.ReleaseDeviceLeasesAsync("run_b", CancellationToken.None)));
        Assert.True(await InTx(store, uow => uow.Runs.TryAcquireDeviceLeaseAsync(Lease("run_a", later), later, CancellationToken.None)));
    }

    [Fact]
    public async Task Unfinished_runs_attempt_runs_and_represented_rows_round_trip()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store, "run_a", "run_b");
        await InTx(store, async uow =>
        {
            await uow.Runs.UpdateAsync(Run("run_a") with { Status = RunStatus.Finished, FinishReason = RunFinishReason.Exhausted }, CancellationToken.None);
            await uow.Frontier.InsertTaskIfAbsentAsync(Task("t1"), CancellationToken.None);
            await uow.Frontier.InsertTaskIfAbsentAsync(Task("t2") with { Status = FrontierTaskStatus.Cancelled, RepresentedBy = "t1" }, CancellationToken.None);
            await uow.Frontier.InsertAttemptIfAbsentAsync(new TaskAttempt("att_1", "t1", 1, AttemptPhase.Created, null, null, null, _clock.UtcNow, null) { RunId = "run_b" }, CancellationToken.None);
            return 0;
        });

        var (unfinished, represented, attempt) = await InTx(store, async uow => (
            await uow.Runs.ListUnfinishedAsync(CancellationToken.None),
            await uow.Frontier.GetTaskAsync("t2", CancellationToken.None),
            await uow.Frontier.GetAttemptAsync("att_1", CancellationToken.None)));

        Assert.Equal(["run_b"], unfinished.Select(r => r.RunId));
        Assert.Equal((FrontierTaskStatus.Cancelled, "t1"), (represented!.Status, represented.RepresentedBy));
        Assert.Equal("run_b", attempt!.RunId);
    }

    [Fact]
    public async Task Duplicate_attempt_ids_are_rejected_by_the_schema()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store);
        var attempt = new TaskAttempt("att_1", "t1", 1, AttemptPhase.Created, null, null, null, _clock.UtcNow, null);
        await InTx(store, uow => uow.Frontier.InsertTaskIfAbsentAsync(Task("t1"), CancellationToken.None));

        var first = await InTx(store, uow => uow.Frontier.InsertAttemptIfAbsentAsync(attempt, CancellationToken.None));
        var again = await InTx(store, uow => uow.Frontier.InsertAttemptIfAbsentAsync(attempt with { N = 2 }, CancellationToken.None));

        Assert.True(first);
        Assert.False(again);
        Assert.Single(await InTx(store, uow => uow.Frontier.ListAttemptsAsync("t1", CancellationToken.None)));
        await Assert.ThrowsAnyAsync<Exception>(() => store.QueryAsync("INSERT INTO task_attempts (attempt_id, task_id, n, phase, started_at) VALUES ('att_1', 't1', 3, 'Created', 'x')", CancellationToken.None));
    }

    [Fact]
    public void Migration_5_applies_on_top_of_version_4()
    {
        using (SqliteStateStore.Open(_path, _clock, targetVersion: 4))
        {
        }

        using var upgraded = SqliteStateStore.Open(_path, _clock);
        Assert.Equal(5, SqliteMigrator.LatestVersion);
    }
}

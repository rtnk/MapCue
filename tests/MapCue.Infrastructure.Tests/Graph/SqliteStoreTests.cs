using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

public class SqliteStoreTests
{
    private static async Task<IReadOnlyList<string>> SchemaAsync(SqliteStateStore store) =>
        [.. (await store.QueryAsync("SELECT type, name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name", CancellationToken.None))
            .Select(r => $"{r[0]} {r[1]} {r[2]}")];

    [Fact]
    public async Task Migrations_from_zero_and_incremental_give_the_same_schema_and_keep_data()
    {
        var fresh = GraphHarness.NewDirectory();
        var incremental = GraphHarness.NewDirectory();
        var clock = new ManualClock();

        await using var full = SqliteStateStore.Open(Path.Combine(fresh, "mapcue.db"), clock);
        var path = Path.Combine(incremental, "mapcue.db");
        await using (var v1 = SqliteStateStore.Open(path, clock, targetVersion: 1))
        {
            var rows = await v1.QueryAsync("SELECT version FROM schema_migrations", CancellationToken.None);
            Assert.Equal([1L], rows.Select(r => r[0]));
            await v1.InTransactionAsync(u => u.Graph.UpsertContextAsync(GraphHarness.Basic, clock.UtcNow, CancellationToken.None), CancellationToken.None);
        }

        await using var upgraded = SqliteStateStore.Open(path, clock);

        Assert.Equal(await SchemaAsync(full), await SchemaAsync(upgraded));
        Assert.Equal(
            SqliteMigrator.Embedded.Select(m => (object?)(long)m.Version),
            (await upgraded.QueryAsync("SELECT version FROM schema_migrations ORDER BY version", CancellationToken.None)).Select(r => r[0]));
        Assert.Equal((long)SqliteMigrator.LatestVersion, (await upgraded.QueryAsync("PRAGMA user_version", CancellationToken.None))[0][0]);
        var context = await upgraded.InTransactionAsync(u => u.Graph.GetContextAsync(GraphHarness.Basic.ContextId, CancellationToken.None), CancellationToken.None);
        Assert.Equal(GraphHarness.Basic, context);
    }

    [Fact]
    public async Task Database_runs_in_wal_mode_with_foreign_keys()
    {
        await using var h = GraphHarness.Create();
        Assert.Equal("wal", (await h.Sqlite.QueryAsync("PRAGMA journal_mode", CancellationToken.None))[0][0]);
        Assert.Equal(1L, (await h.Sqlite.QueryAsync("PRAGMA foreign_keys", CancellationToken.None))[0][0]);
    }

    [Fact]
    public void Changed_migration_is_refused()
    {
        var path = Path.Combine(GraphHarness.NewDirectory(), "mapcue.db");
        SqliteStateStore.Open(path, new ManualClock()).Dispose();
        var changed = SqliteMigrator.Embedded.Select(m => m.Version == 1 ? m with { Sql = m.Sql + "\n-- edited" } : m).ToList();

        var error = Assert.Throws<SchemaMismatchException>(() => SqliteStateStore.Open(path, new ManualClock(), migrations: changed));
        Assert.Contains("checksum", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_database_is_refused()
    {
        var path = Path.Combine(GraphHarness.NewDirectory(), "mapcue.db");
        var future = SqliteMigrator.Embedded.Append(new Migration(SqliteMigrator.LatestVersion + 1, "future", "CREATE TABLE future (x INTEGER);")).ToList();
        SqliteStateStore.Open(path, new ManualClock(), migrations: future).Dispose();

        var error = Assert.Throws<SchemaMismatchException>(() => SqliteStateStore.Open(path, new ManualClock()));
        Assert.Contains("newer MapCue", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_files_must_be_numbered_without_gaps()
    {
        Assert.Throws<InvalidOperationException>(() => SqliteMigrator.Parse([("0001_a.sql", ""), ("0003_c.sql", "")]));
        Assert.Throws<InvalidOperationException>(() => SqliteMigrator.Parse([("1_a.sql", "")]));
        Assert.Equal([1, 2], SqliteMigrator.Parse([("0002_b.sql", ""), ("0001_a.sql", "")]).Select(m => m.Version));
        Assert.Equal(["graph_core", "edges_frontier_outbox", "agent_calls", "runs_task_actions", "leases_recovery"], SqliteMigrator.Embedded.Select(m => m.Name));
    }

    [Fact]
    public async Task Exception_in_the_middle_of_a_transaction_leaves_no_partial_state()
    {
        await using var h = GraphHarness.Create();
        var now = h.Clock.UtcNow;

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Sqlite.InTransactionAsync<int>(
            async u =>
            {
                await u.Graph.UpsertContextAsync(GraphHarness.Basic, now, CancellationToken.None);
                await u.Graph.InsertScreenAsync(new LogicalScreen("scr_1", "screen-1", null, IdentityStatus.Provisional, IdentitySource.Structure, now), CancellationToken.None);
                await u.Events.AppendAsync("evt_1", "test", now, null, null, "{}", CancellationToken.None);
                throw new InvalidOperationException("boom");
            },
            CancellationToken.None));

        var summary = await h.SummaryAsync();
        Assert.Equal((0, 0, 0L), (summary.Contexts, summary.Screens, summary.Events));
    }

    [Fact]
    public async Task Failure_at_the_end_of_an_attempt_commit_rolls_back_the_whole_attempt()
    {
        await using var h = GraphHarness.Create();
        h.UseStore(new FaultyStore(h.Sqlite, failOnEvent: GraphEventTypes.AttemptCommitted));

        await Assert.ThrowsAsync<InjectedFaultException>(() => h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts()));

        var summary = await h.SummaryAsync();
        Assert.Equal(new GraphSummary(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), summary);
        Assert.Empty(await h.Sqlite.QueryAsync("SELECT 1 FROM task_attempts", CancellationToken.None));

        // The same attempt can be committed once the fault is gone (artifacts written before the failure are reused).
        h.UseStore(h.Sqlite);
        var result = await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        Assert.False(result.AlreadyCommitted);
        Assert.Equal(2, (await h.SummaryAsync()).States);
    }

    [Fact]
    public async Task Failure_in_a_later_attempt_keeps_earlier_commits_intact()
    {
        await using var h = GraphHarness.Create();
        await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var before = GraphJson.Serialize(await h.CanonicalAsync());
        var events = (await h.SummaryAsync()).Events;

        h.UseStore(new FaultyStore(h.Sqlite, failOnEvent: GraphEventTypes.EdgeObserved));
        await Assert.ThrowsAsync<InjectedFaultException>(() => h.ActAsync("att_2", GraphHarness.Accounts(), GraphHarness.Back, GraphHarness.Settings()));

        Assert.Equal(before, GraphJson.Serialize(await h.CanonicalAsync()));
        Assert.Equal(events, (await h.SummaryAsync()).Events);
    }

    [Fact]
    public async Task Cancelled_transaction_is_rolled_back()
    {
        await using var h = GraphHarness.Create();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Sqlite.InTransactionAsync(
            async u =>
            {
                await u.Graph.UpsertContextAsync(GraphHarness.Basic, h.Clock.UtcNow, CancellationToken.None);
                await cts.CancelAsync();
                return 0;
            },
            cts.Token));
        Assert.Equal(0, (await h.SummaryAsync()).Contexts);
    }

    [Fact]
    public async Task Nested_transactions_are_refused()
    {
        await using var h = GraphHarness.Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Sqlite.InTransactionAsync(
            _ => h.Sqlite.InTransactionAsync(_ => Task.FromResult(0), CancellationToken.None),
            CancellationToken.None));
    }

    [Fact]
    public async Task Task_insert_is_on_conflict_do_nothing_and_blocked_reason_round_trips()
    {
        await using var h = GraphHarness.Create();
        var now = h.Clock.UtcNow;
        var task = new FrontierTask("task_1", "key_1", TaskType.ExploreAction, FrontierTaskStatus.Pending, 5, 2, GraphHarness.Basic.ContextId, null, "act", null, null, null, null, 0, now, now);
        var (inserted, duplicate, stored) = await h.Sqlite.InTransactionAsync(
            async u =>
            {
                await u.Graph.UpsertContextAsync(GraphHarness.Basic, now, CancellationToken.None);
                var first = await u.Frontier.InsertTaskIfAbsentAsync(task, CancellationToken.None);
                var second = await u.Frontier.InsertTaskIfAbsentAsync(task with { TaskId = "task_2" }, CancellationToken.None);
                var blocked = new BlockedReason(BlockedReasonCode.UnknownOverlay, "Unknown popup over Home", ["ev_1", "ev_2"], "resume.md");
                await u.Frontier.UpdateTaskAsync(task with { Status = FrontierTaskStatus.Blocked, BlockedReason = blocked }, CancellationToken.None);
                return (first, second, await u.Frontier.GetTaskAsync("task_1", CancellationToken.None));
            },
            CancellationToken.None);

        Assert.True(inserted);
        Assert.False(duplicate);
        Assert.Equal(FrontierTaskStatus.Blocked, stored!.Status);
        Assert.Equal(BlockedReasonCode.UnknownOverlay, stored.BlockedReason!.Code);
        Assert.Equal(["ev_1", "ev_2"], stored.BlockedReason.EvidenceIds);
        Assert.Equal(1, stored.CreatedSeq);
    }

    [Fact]
    public async Task Recipe_and_checkpoint_round_trip()
    {
        await using var h = GraphHarness.Create();
        var result = await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var recipe = new ReachabilityRecipe(
            "rcp_1",
            GraphHarness.Basic.ContextId,
            result.ToStateId!,
            [new RecipeStep(RecipeStepKind.LaunchApp, null, null, result.FromStateId, null), new RecipeStep(RecipeStepKind.Action, GraphHarness.Tap("app:id/tab_accounts").ActionKey, result.EdgeId, result.ToStateId, "assertVisible:id:app:id/toolbar_title")],
            null,
            [result.EdgeId]);
        var checkpoint = new Checkpoint("chk_1", "run_1", 7, new FrontierStats(1, 0, 2, 0, 0, 0, 0, 0), "sha256:config", h.Clock.UtcNow);

        var (storedRecipe, storedCheckpoint) = await h.Sqlite.InTransactionAsync(
            async u =>
            {
                await u.Graph.UpsertRecipeAsync(recipe, CancellationToken.None);
                await u.Graph.UpsertRecipeAsync(recipe with { VerifiedAt = h.Clock.UtcNow }, CancellationToken.None);
                await u.Frontier.InsertCheckpointAsync(checkpoint, CancellationToken.None);
                return (await u.Graph.GetRecipeAsync("rcp_1", CancellationToken.None), await u.Frontier.GetLatestCheckpointAsync("run_1", CancellationToken.None));
            },
            CancellationToken.None);

        Assert.Equal(recipe.Steps, storedRecipe!.Steps);
        Assert.Equal(h.Clock.UtcNow, storedRecipe.VerifiedAt);
        Assert.Equal(checkpoint, storedCheckpoint);
    }

    [Fact]
    public async Task Outbox_events_are_ordered_by_sequence()
    {
        await using var h = GraphHarness.Create();
        await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var events = await h.ReadAsync(u => u.Events.ListAsync(0, 1000, CancellationToken.None));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), events.Select(e => e.Seq));
        Assert.Equal(GraphEventTypes.AttemptCommitted, events[^1].Type);
        Assert.Contains(events, e => e.Type == GraphEventTypes.StateDiscovered);
        var page = await h.ReadAsync(u => u.Events.ListAsync(events[2].Seq, 2, CancellationToken.None));
        Assert.Equal(events.Skip(3).Take(2).Select(e => e.EventId), page.Select(e => e.EventId));
    }

    [Fact]
    public void Workspace_lock_is_exclusive()
    {
        var directory = GraphHarness.NewDirectory();
        using (WorkspaceLock.Acquire(directory))
        {
            Assert.Throws<WorkspaceLockedException>(() => WorkspaceLock.Acquire(directory));
        }

        using var again = WorkspaceLock.Acquire(directory);
        Assert.True(File.Exists(again.Path));
    }

    private sealed class InjectedFaultException() : Exception("injected fault");

    /// <summary>Delegates to the real store and throws when an outbox event of the given type is appended (mid-transaction).</summary>
    private sealed class FaultyStore(IStateStore inner, string failOnEvent) : IStateStore
    {
        public Task<T> InTransactionAsync<T>(Func<IUnitOfWork, Task<T>> work, CancellationToken ct) =>
            inner.InTransactionAsync(uow => work(new FaultyUnitOfWork(uow, failOnEvent)), ct);
    }

    private sealed class FaultyUnitOfWork(IUnitOfWork inner, string failOnEvent) : IUnitOfWork
    {
        public IGraphRepository Graph => inner.Graph;

        public IFrontierRepository Frontier => inner.Frontier;

        public IEventOutbox Events { get; } = new FaultyOutbox(inner.Events, failOnEvent);

        public IAgentCallRepository AgentCalls => inner.AgentCalls;

        public IRunRepository Runs => inner.Runs;
    }

    private sealed class FaultyOutbox(IEventOutbox inner, string failOnEvent) : IEventOutbox
    {
        public async Task<long> AppendAsync(string eventId, string type, DateTimeOffset occurredAt, string? contextId, string? subjectId, string payloadJson, CancellationToken ct)
        {
            var seq = await inner.AppendAsync(eventId, type, occurredAt, contextId, subjectId, payloadJson, ct);
            return type == failOnEvent ? throw new InjectedFaultException() : seq;
        }

        public Task<IReadOnlyList<OutboxEvent>> ListAsync(long afterSeq, int limit, CancellationToken ct) => inner.ListAsync(afterSeq, limit, ct);

        public Task<long> LastSeqAsync(CancellationToken ct) => inner.LastSeqAsync(ct);
    }
}

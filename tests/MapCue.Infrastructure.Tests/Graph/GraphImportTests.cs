using MapCue.Application.Graph;
using MapCue.Domain;

namespace MapCue.Infrastructure.Tests.Graph;

public class GraphImportTests
{
    private static GraphImportPlan Plan(MapCue.Domain.Graph.ExplorationContext context, bool investor = false) => new(
        context,
        [
            new("home", GraphHarness.Home(investor)),
            new("accounts", GraphHarness.Accounts()),
            new("accounts-long", GraphHarness.Accounts(rows: 9)),
            new("settings", GraphHarness.Settings()),
        ],
        [
            new("home", GraphHarness.Tap("app:id/tab_accounts"), ActionOutcome.Succeeded, "accounts", TimeSpan.FromMilliseconds(300)),
            new("accounts", GraphHarness.Back, ActionOutcome.Succeeded, "home", TimeSpan.FromMilliseconds(200)),
            new("home", GraphHarness.Tap("app:id/tab_accounts"), ActionOutcome.Succeeded, "accounts-long", TimeSpan.FromMilliseconds(310)),
            new("home", GraphHarness.Tap("app:id/tab_settings"), ActionOutcome.Succeeded, "settings", TimeSpan.FromMilliseconds(280)),
            new("settings", GraphHarness.Back, ActionOutcome.Succeeded, "home", TimeSpan.FromMilliseconds(190)),
        ]);

    [Fact]
    public async Task Importing_the_same_plan_twice_gives_the_same_graph()
    {
        await using var h = GraphHarness.Create();
        var first = await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        var graph = GraphJson.Serialize(await h.CanonicalAsync());
        var summary = await h.SummaryAsync();

        h.Clock.Advance(TimeSpan.FromHours(1));
        var second = await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);

        Assert.Equal(3, first.NewStates);
        Assert.Equal(0, first.AlreadyCommittedTransitions);
        Assert.Equal(0, second.NewStates);
        Assert.Equal(0, second.NewVariants);
        Assert.Equal(0, second.NewTasks);
        Assert.Equal(5, second.AlreadyCommittedTransitions);
        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(first.StateOfObservation, second.StateOfObservation);
        Assert.Equal(graph, GraphJson.Serialize(await h.CanonicalAsync()));
        Assert.Equal(summary, await h.SummaryAsync());
        Assert.Equal(first.StateOfObservation["accounts"], first.StateOfObservation["accounts-long"]);
    }

    [Fact]
    public async Task Import_into_two_fresh_workspaces_gives_identical_graphs()
    {
        await using var a = GraphHarness.Create();
        await using var b = GraphHarness.Create();
        await a.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        b.Clock.Advance(TimeSpan.FromDays(3));
        await b.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);

        Assert.Equal(GraphJson.Serialize(await a.CanonicalAsync()), GraphJson.Serialize(await b.CanonicalAsync()));
    }

    [Fact]
    public async Task Cycle_in_the_plan_creates_edges_but_no_extra_explore_tasks()
    {
        await using var h = GraphHarness.Create();
        var result = await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        var summary = await h.SummaryAsync();

        // 4 distinct (state, action) pairs: home→accounts (twice), accounts→back, home→settings, settings→back.
        Assert.Equal(4, summary.Edges);
        Assert.Equal(5, summary.Observations);
        var tasks = await h.ReadAsync(u => u.Frontier.ListTasksAsync(null, CancellationToken.None));
        Assert.Equal(4, tasks.Count(t => t.Type == MapCue.Domain.Graph.TaskType.ExploreAction));
        Assert.Equal(3, tasks.Count(t => t.Type == MapCue.Domain.Graph.TaskType.AnalyzeState));
        Assert.Equal(result.NewTasks, tasks.Count);
    }

    [Fact]
    public async Task Second_profile_import_adds_variants_and_edges_without_touching_the_first()
    {
        await using var h = GraphHarness.Create();
        await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        var basicEdges = (await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None))).ToList();

        var investor = await h.Importer.ImportAsync(Plan(GraphHarness.Investor, investor: true), CancellationToken.None);

        Assert.Equal(0, investor.NewStates);
        Assert.Equal(3, investor.NewVariants);
        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Equal(8, edges.Count);
        Assert.Equal(basicEdges, edges.Where(e => e.ContextId == GraphHarness.Basic.ContextId).ToList());
        var summary = await h.SummaryAsync();
        Assert.Equal((2, 3, 6), (summary.Contexts, summary.States, summary.Variants));
    }

    [Fact]
    public async Task Import_writes_a_checkpoint_only_when_something_changed()
    {
        await using var h = GraphHarness.Create();
        var first = await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        var checkpoint = await h.ReadAsync(u => u.Frontier.GetLatestCheckpointAsync(first.RunId, CancellationToken.None));
        await h.Importer.ImportAsync(Plan(GraphHarness.Basic), CancellationToken.None);
        var again = await h.ReadAsync(u => u.Frontier.GetLatestCheckpointAsync(first.RunId, CancellationToken.None));

        Assert.NotNull(checkpoint);
        Assert.Equal(checkpoint, again);
        Assert.Equal((await h.SummaryAsync()).Events, checkpoint!.EventSeq);
        Assert.Equal(7, checkpoint.Frontier.Total);
        var rows = await h.Sqlite.QueryAsync("SELECT COUNT(*) FROM checkpoints", CancellationToken.None);
        Assert.Equal(1L, rows[0][0]);
    }

    [Fact]
    public async Task Invalid_plans_are_rejected_before_anything_is_written()
    {
        await using var h = GraphHarness.Create();
        var duplicate = new GraphImportPlan(GraphHarness.Basic, [new("a", GraphHarness.Home()), new("a", GraphHarness.Settings())], []);
        var dangling = new GraphImportPlan(GraphHarness.Basic, [new("a", GraphHarness.Home())], [new("a", GraphHarness.Back, ActionOutcome.Succeeded, "missing", TimeSpan.Zero)]);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Importer.ImportAsync(duplicate, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Importer.ImportAsync(dangling, CancellationToken.None));
        Assert.Equal(0, (await h.SummaryAsync()).States);
        Assert.False(Directory.Exists(h.Artifacts.Root));
    }
}

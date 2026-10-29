using System.Text.Json;
using MapCue.Application.Configuration;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// STEP-06 on <see cref="FakeDevice"/>: termination on a cycle and on an endless list, list rows represented by other rows, budgets that
/// stop at a predictable point and a resumed run that loses nothing and commits nothing twice.
/// </summary>
public class FrontierSchedulerEndToEndTests
{
    private const int PerRunActions = 8;

    private static string Id(string name) => FakeDemoApp.Id(name);

    private static FakeElement Title(string name, string text) => new(Id("screen." + name), text) { Clickable = false, ClassName = "android.widget.TextView" };

    /// <summary>a → b → c → a by "next" (tabs swapping content); back on a root screen stays (no launcher).</summary>
    private static FakeAppModel Ring() =>
        new(
            FakeDemoApp.AppId,
            "a",
            [
                new("a", [Title("a", "A"), new(Id("next"), "Next", "b") { Navigation = FakeNavigation.Replace }]),
                new("b", [Title("b", "B"), new(Id("next"), "Next", "c") { Navigation = FakeNavigation.Replace }]),
                new("c", [Title("c", "C"), new(Id("next"), "Next", "a") { Navigation = FakeNavigation.Replace }]),
            ])
        {
            SetupScreenId = "a",
        };

    private static string Word(int i) => i < 26 ? ((char)('a' + i)).ToString() : Word((i / 26) - 1) + (char)('a' + (i % 26));

    /// <summary>
    /// An endless feed: every page has a scrollable list of <paramref name="rows"/> rows (no ids, distinct texts) leading to one item
    /// screen, and "More" replaces the page with the next one, with other rows, for <paramref name="pages"/> pages.
    /// </summary>
    private static FakeAppModel Feed(int pages = 60, int rows = 12) =>
        new(
            FakeDemoApp.AppId,
            "feed0",
            [
                .. Enumerable.Range(0, pages).Select(p => new FakeScreen(
                    $"feed{p}",
                    [
                        Title("feed", "Feed"),
                        new(Id("feed.list"), string.Empty) { Clickable = false, Scrollable = true, ClassName = "androidx.recyclerview.widget.RecyclerView" },
                        .. Enumerable.Range(0, rows).Select(r => new FakeElement(string.Empty, $"Story {Word((p * rows) + r)}", "item") { Depth = 1, ClassName = "android.widget.TextView" }),
                        new(Id("feed.more"), "More", $"feed{Math.Min(p + 1, pages - 1)}") { Navigation = FakeNavigation.Replace },
                    ])),
                new("item", [Title("item", "Story"), new(Id("item.close"), "Close") { Navigation = FakeNavigation.Pop }]),
            ])
        {
            SetupScreenId = "feed0",
        };

    /// <summary>Every page is a new state ("Next" has another label on each page): only a budget ends the exploration.</summary>
    private static FakeAppModel Chain(int pages = 100) =>
        new(
            FakeDemoApp.AppId,
            "p0",
            [.. Enumerable.Range(0, pages).Select(p => new FakeScreen($"p{p}", [new(string.Empty, $"Next {Word(p)}", $"p{Math.Min(p + 1, pages - 1)}") { Navigation = FakeNavigation.Replace }]))])
        {
            SetupScreenId = "p0",
        };

    [Fact]
    public async Task A_cycle_terminates_with_every_edge_committed_once()
    {
        await using var h = ExplorationHarness.Create(Ring());

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);
        var states = await h.ReadAsync(u => u.Graph.ListStatesAsync(CancellationToken.None));
        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        var observations = await h.ReadAsync(u => u.Graph.ListObservationsAsync(CancellationToken.None));
        Assert.Equal(3, states.Count);

        // next and back on each of the three states: six attempts, one observation per edge, no task left.
        Assert.Equal(6, result.Counters.Attempts);
        Assert.Equal(6, edges.Count);
        Assert.All(edges, e => Assert.Equal((1, EdgeStatus.Observed), (e.ObservationCount, e.Status)));
        Assert.Equal(observations.Count, observations.Select(o => o.AttemptId).Distinct().Count());
        var stats = await h.ReadAsync(u => u.Frontier.GetStatsAsync(CancellationToken.None));
        Assert.Equal((0, 0), (stats.Pending, stats.Leased));

        // Locality: the ring is walked without a reset after the preflight.
        Assert.Equal(1, result.Counters.Resets);
        var attempts = await h.ReadAsync(u => u.Frontier.ListAttemptsAsync(null, CancellationToken.None));
        Assert.All(attempts, a => Assert.Equal((AttemptPhase.Committed, result.RunId), (a.Phase, a.RunId)));
    }

    [Fact]
    public async Task An_endless_list_terminates_and_extra_rows_are_represented()
    {
        await using var h = ExplorationHarness.Create(Feed());

        var result = await h.RunAsync(new AllTapsReasoner());

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);

        // Pages differ only in their rows: one list state (count bucket "many"), so "More" is a self loop and paging never ends the run.
        var states = await h.ReadAsync(u => u.Graph.ListStatesAsync(CancellationToken.None));
        Assert.Equal(2, states.Count);
        var rowTaps = h.Device.ExecutedActions.Count(a => a.TargetText?.StartsWith("Story ", StringComparison.Ordinal) == true);
        Assert.Equal(h.Settings.Budgets.MaxListRowsPerTemplate, rowTaps);

        var tasks = await h.ReadAsync(u => u.Frontier.ListTasksAsync(null, CancellationToken.None));
        var represented = tasks.Where(t => t.RepresentedBy is not null).ToList();
        Assert.Equal(12 - h.Settings.Budgets.MaxListRowsPerTemplate, represented.Count);
        Assert.Equal(represented.Count, result.Counters.RepresentedTasks);
        Assert.All(represented, t => Assert.Equal(FrontierTaskStatus.Cancelled, t.Status));
        var representative = Assert.Single(represented.Select(t => t.RepresentedBy).Distinct());
        Assert.Equal(FrontierTaskStatus.Completed, tasks.Single(t => t.TaskId == representative).Status);
    }

    [Fact]
    public async Task A_chain_of_new_states_stops_at_the_state_budget_exactly()
    {
        var settings = ExplorationHarness.DemoSettings(Budgets.Default with { MaxStates = 5 });
        await using var first = ExplorationHarness.Create(Chain(), settings);
        await using var second = ExplorationHarness.Create(Chain(), settings);

        var a = await first.RunAsync();
        var b = await second.RunAsync();

        Assert.Equal(RunFinishReason.BudgetExhausted, a.Reason);
        Assert.Contains("maxStates", a.Detail, StringComparison.Ordinal);
        Assert.Equal(5, a.Counters.NewStates);
        Assert.Equal(5, (await first.ReadAsync(u => u.Graph.ListStatesAsync(CancellationToken.None))).Count);
        Assert.Equal((a.Reason, a.Detail, a.Counters), (b.Reason, b.Detail, b.Counters));
        Assert.Equal(JsonSerializer.Serialize(await first.CanonicalAsync()), JsonSerializer.Serialize(await second.CanonicalAsync()));
    }

    [Fact]
    public async Task The_action_budget_stops_before_a_task_it_cannot_pay_for_and_a_resumed_run_loses_nothing()
    {
        var limited = ExplorationHarness.DemoSettings(Budgets.Default with { MaxActions = 12 });
        await using var h = ExplorationHarness.Create(settings: limited);
        await using var twin = ExplorationHarness.Create(settings: limited);
        await using var reference = ExplorationHarness.Create();

        var stopped = await h.RunAsync(seed: "a");
        var stoppedTwin = await twin.RunAsync(seed: "a");

        // Predictable point: the same counters and frontier for the same input, never more than the budget, and the next task would not fit.
        Assert.Equal(RunFinishReason.BudgetExhausted, stopped.Reason);
        Assert.Contains("needs", stopped.Detail, StringComparison.Ordinal);
        Assert.Equal((stopped.Detail, stopped.Counters), (stoppedTwin.Detail, stoppedTwin.Counters));
        Assert.True(stopped.Counters.ChargedActions <= 12, $"charged {stopped.Counters.ChargedActions}");
        Assert.Equal(JsonSerializer.Serialize((await h.CanonicalAsync()).Tasks), JsonSerializer.Serialize((await twin.CanonicalAsync()).Tasks));
        var afterStop = await h.ReadAsync(u => u.Frontier.GetStatsAsync(CancellationToken.None));
        Assert.True(afterStop.Pending > 0);
        Assert.Equal(0, afterStop.Leased);

        // The next run continues the frontier: same states and edges as one uninterrupted run, every edge observed once.
        var resumed = await h.RunAsync(seed: "b", settings: ExplorationHarness.DemoSettings());
        await reference.RunAsync(seed: "a");

        Assert.Equal(RunFinishReason.Exhausted, resumed.Reason);
        var graph = await h.CanonicalAsync();
        var expected = await reference.CanonicalAsync();
        Assert.Equal(expected.States, graph.States);
        Assert.Equal(expected.Edges, graph.Edges);
        Assert.Equal(expected.Tasks, graph.Tasks);
        var observations = await h.ReadAsync(u => u.Graph.ListObservationsAsync(CancellationToken.None));
        Assert.Equal(observations.Count, (await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None))).Sum(e => e.ObservationCount));
    }

    [Fact]
    public async Task Agent_budget_is_per_run()
    {
        var agent = AgentSettings.Default with { MaxCalls = 3 };
        await using var h = ExplorationHarness.Create(settings: ExplorationHarness.DemoSettings() with { Agent = agent });
        async Task<int> AcceptedCallsAsync() =>
            (await h.ReadAsync(u => u.AgentCalls.ListAsync(null, CancellationToken.None))).Count(c => c.Status == AgentCallStatus.Accepted);

        // The first run spends its three calls and stops on the action budget before the states it found are analysed.
        var first = await h.RunAsync(seed: "a", settings: ExplorationHarness.DemoSettings(Budgets.Default with { MaxActions = PerRunActions }) with { Agent = agent });
        var afterFirst = await AcceptedCallsAsync();
        await h.RunAsync(seed: "b");

        // The second run gets three calls of its own (with a workspace-wide budget it would have none left).
        Assert.Equal(RunFinishReason.BudgetExhausted, first.Reason);
        Assert.Equal(3, afterFirst);
        Assert.True(await AcceptedCallsAsync() > afterFirst);
    }
}

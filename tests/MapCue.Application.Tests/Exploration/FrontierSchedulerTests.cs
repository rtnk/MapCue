using MapCue.Application.Exploration;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Tests.Exploration;

public class FrontierSchedulerTests
{
    private static readonly DateTimeOffset _at = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    private static FrontierTask Task(string id, string from, int priority = 0, int depth = 0, long seq = 0, string? actionKey = null) =>
        new(id, "key_" + id, TaskType.ExploreAction, FrontierTaskStatus.Pending, priority, depth, "ctx", from, actionKey ?? "ak_" + id, null, null, null, null, seq, _at, _at);

    private static List<string> Order(IEnumerable<FrontierTask> tasks, Dictionary<string, int> distances, params string[] known) =>
        FrontierScheduler.Order(tasks, t => distances.GetValueOrDefault(t.FromStateId!, FrontierScheduler.Unreachable), t => known.Contains(t.TaskId)).Select(s => s.Task.TaskId).ToList();

    [Fact]
    public void Locality_comes_first_then_risk_novelty_depth_and_creation()
    {
        var tasks = new[]
        {
            Task("far", "B", seq: 1),
            Task("back", "A", priority: FrontierScheduler.BackPriority, seq: 2),
            Task("risky", "A", priority: FrontierScheduler.StateChangingPriority, seq: 3),
            Task("known", "A", seq: 4),
            Task("deep", "A", depth: 3, seq: 5),
            Task("novel", "A", depth: 3, seq: 6),
            Task("first", "A", depth: 1, seq: 7),
            Task("lost", "Z", seq: 0),
        };

        var order = Order(tasks, new() { ["A"] = 0, ["B"] = 2 }, "known");

        // On the current state A: safe before back before state-changing; novel before known; shallow before deep; then creation.
        Assert.Equal(["first", "deep", "novel", "known", "back", "risky", "far", "lost"], order);
    }

    [Fact]
    public void The_order_is_total_and_independent_of_the_input_order()
    {
        var tasks = Enumerable.Range(0, 20).Select(i => Task($"t{i:00}", i % 3 == 0 ? "A" : "B", priority: -(i % 2), depth: i % 4, seq: i)).ToList();
        var distances = new Dictionary<string, int> { ["A"] = 1, ["B"] = 0 };

        var forward = Order(tasks, distances);
        var backward = Order(Enumerable.Reverse(tasks), distances);

        Assert.Equal(forward, backward);
        Assert.Equal(tasks.Count, forward.Distinct().Count());
    }

    [Theory]
    [InlineData(ActionType.Tap, RiskClass.Safe, FrontierScheduler.DefaultPriority)]
    [InlineData(ActionType.Tap, RiskClass.Navigational, FrontierScheduler.DefaultPriority)]
    [InlineData(ActionType.Back, RiskClass.Safe, FrontierScheduler.BackPriority)]
    [InlineData(ActionType.Tap, RiskClass.StateChanging, FrontierScheduler.StateChangingPriority)]
    [InlineData(ActionType.Tap, RiskClass.Unknown, FrontierScheduler.StateChangingPriority)]
    [InlineData(ActionType.Tap, RiskClass.Destructive, FrontierScheduler.HighRiskPriority)]
    [InlineData(ActionType.Tap, RiskClass.Financial, FrontierScheduler.HighRiskPriority)]
    public void Priority_follows_the_risk_class(ActionType type, RiskClass risk, int expected)
    {
        var action = new ValidatedDeviceAction("a1", "com.example", type, risk, "com.example:id/x", null, null);

        Assert.Equal(expected, FrontierScheduler.PriorityOf(action));
    }

    [Fact]
    public void Estimated_cost_is_the_path_plus_the_action_and_a_reset_for_unreachable_sources()
    {
        var task = Task("t", "A");

        Assert.Equal(1, FrontierScheduler.EstimatedCost(new ScheduledTask(task, 0, false)));
        Assert.Equal(4, FrontierScheduler.EstimatedCost(new ScheduledTask(task, 3, false)));
        Assert.Equal(1, FrontierScheduler.EstimatedCost(new ScheduledTask(task, FrontierScheduler.Unreachable, false)));
    }
}

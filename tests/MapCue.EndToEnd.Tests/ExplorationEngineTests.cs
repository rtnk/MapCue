using System.Text.Json;
using MapCue.Application.Configuration;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// STEP-05 vertical slice on <see cref="FakeDevice"/> with the demo app model (<see cref="FakeDemoApp"/>) and the rule-based reasoner:
/// preflight → AnalyzeState/ExploreAction loop → finish, without device, TapCue or LLM.
/// </summary>
public class ExplorationEngineTests
{
    private static string Anchor(string automationId) => "a:id:" + FakeDemoApp.Id(automationId);

    private static async Task<Dictionary<string, ScreenState>> StatesByScreenAsync(ExplorationHarness h)
    {
        var states = await h.ReadAsync(u => u.Graph.ListStatesAsync(CancellationToken.None));
        var result = new Dictionary<string, ScreenState>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var marker = state.IdentityTokens.FirstOrDefault(t => t.StartsWith("a:id:" + FakeDemoApp.AppId + ":id/screen.", StringComparison.Ordinal))
                ?? (state.IdentityTokens.Contains("a:id:android:id/alertTitle") ? "dialog:" + state.StateId : "other:" + state.StateId);
            result[marker.Replace("a:id:" + FakeDemoApp.AppId + ":id/", string.Empty, StringComparison.Ordinal)] = state;
        }

        return result;
    }

    [Fact]
    public async Task Explores_the_demo_app_with_cycle_modal_and_policy()
    {
        await using var h = ExplorationHarness.Create();

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);
        var states = await StatesByScreenAsync(h);
        foreach (var screen in new[] { "screen.home", "screen.accounts", "screen.details", "screen.whatsnew", "screen.settings", "screen.verify", "screen.profile" })
        {
            Assert.True(states.ContainsKey(screen), $"state of {screen} discovered");
        }

        Assert.True(states.Count >= 5);
        var modal = Assert.Single(states.Values, s => s.StateKind == StateKind.Modal);
        Assert.Contains("a:id:android:id/alertTitle", modal.IdentityTokens);

        // Six logical screens at least: tabs, details, modal page, popup and profile picker are different screens (screen hints).
        var screens = await h.ReadAsync(u => u.Graph.ListScreensAsync(CancellationToken.None));
        Assert.True(screens.Count >= 7, $"screens: {screens.Count}");

        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        string Id(string screen) => states[screen].StateId;
        Assert.Contains(edges, e => e.FromStateId == Id("screen.home") && e.ToStateId == Id("screen.accounts") && e.Status == EdgeStatus.Observed);
        Assert.Contains(edges, e => e.FromStateId == Id("screen.accounts") && e.ToStateId == Id("screen.home") && e.Status == EdgeStatus.Observed);
        Assert.Contains(edges, e => e.FromStateId == Id("screen.details") && e.ToStateId == Id("screen.home"));
        Assert.Contains(edges, e => e.FromStateId == Id("screen.whatsnew") && e.ToStateId == Id("screen.home"));
        Assert.Contains(edges, e => e.FromStateId == Id("screen.settings") && e.ToStateId == Id("screen.profile"));
        Assert.Contains(edges, e => e.FromStateId == Id("screen.verify") && e.ToStateId == Id("screen.verify"));

        // Risky and state-changing actions from expected-graph.md are never executed.
        var executed = h.Device.ExecutedActions.Select(a => a.TargetResourceId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var risky in new[] { "details.delete", "verify.sendcode", "verify.signout", "profile.investor", "scenario.empty", "settings.notifications" })
        {
            Assert.DoesNotContain(FakeDemoApp.Id(risky), executed);
        }

        Assert.DoesNotContain(h.Device.ExecutedActions, a => a.EffectiveRisk is not (RiskClass.Safe or RiskClass.Navigational));
        Assert.DoesNotContain(states.Keys, k => k.StartsWith("dialog:", StringComparison.Ordinal) && states[k].IdentityTokens.Any(t => t.Contains("button2", StringComparison.Ordinal)));
        var candidates = await h.ReadAsync(u => u.Graph.ListCandidatesAsync(Id("screen.details"), CancellationToken.None));
        Assert.Contains(candidates, c => c.CanonicalSelector.Contains("details.delete", StringComparison.Ordinal) && c.Status == CandidateStatus.SkippedByPolicy && c.RiskClass == RiskClass.AccountChange);

        // Back on a root screen leaves the app: executed edge without a target, then a reset through the setup flow.
        Assert.True(result.Counters.AppLeft >= 3, $"app left {result.Counters.AppLeft}");
        Assert.True(h.Device.FlowsRun.Count >= 2);
        Assert.Contains(edges, e => e.FromStateId == Id("screen.home") && e.ToStateId is null && e.Status == EdgeStatus.Executed);
        Assert.DoesNotContain(states.Keys, k => k.StartsWith("other:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_edge_has_before_and_after_evidence()
    {
        await using var h = ExplorationHarness.Create();
        await h.RunAsync();

        var observations = await h.ReadAsync(u => u.Graph.ListObservationsAsync(CancellationToken.None));
        Assert.NotEmpty(observations);
        foreach (var observation in observations)
        {
            var evidence = await h.ReadAsync(u => u.Graph.ListEvidenceAsync(observation.ContextId, null, CancellationToken.None));
            Assert.Contains(evidence, e => e.SnapshotId == observation.BeforeSnapshotId && e.Kind == EvidenceKind.RawSnapshot);
            Assert.Contains(evidence, e => e.AttemptId == observation.AttemptId && e.Kind == EvidenceKind.ToolOutput);
            if (observation.AfterSnapshotId is not null)
            {
                Assert.Contains(evidence, e => e.SnapshotId == observation.AfterSnapshotId && e.Kind == EvidenceKind.RawSnapshot);
            }
            else
            {
                // Left the app: the launcher screen is kept as attempt evidence, not as a state.
                Assert.Contains(evidence, e => e.AttemptId == observation.AttemptId && e.Kind == EvidenceKind.RawSnapshot && e.SnapshotId is null);
            }

            foreach (var blob in evidence.Where(e => e.AttemptId == observation.AttemptId || e.SnapshotId == observation.BeforeSnapshotId))
            {
                Assert.True(h.Artifacts.Exists(blob.ArtifactSha256));
            }
        }

        var attemptFiles = Directory.GetFiles(h.RunsDirectory, "attempt.json", SearchOption.AllDirectories);
        Assert.Equal(observations.Count, attemptFiles.Length);
        using var document = JsonDocument.Parse(File.ReadAllText(attemptFiles[0]));
        Assert.Equal("mapcue.attempt/1", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.StartsWith("sha256:", document.RootElement.GetProperty("beforeRawSha256").GetString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(Directory.GetDirectories(h.RunsDirectory).Single(), "run.json")));
    }

    [Fact]
    public async Task Two_runs_with_the_same_seed_give_the_same_graph()
    {
        await using var first = ExplorationHarness.Create();
        await using var second = ExplorationHarness.Create();

        var a = await first.RunAsync(seed: "42");
        var b = await second.RunAsync(seed: "42");

        Assert.Equal(a.RunId, b.RunId);
        Assert.Equal(a.Counters, b.Counters);
        Assert.Equal(JsonSerializer.Serialize(await first.CanonicalAsync()), JsonSerializer.Serialize(await second.CanonicalAsync()));
        Assert.Equal(first.Device.ExecutedActions.Select(x => x.ActionId), second.Device.ExecutedActions.Select(x => x.ActionId));
        var eventsA = await first.ReadAsync(u => u.Events.ListAsync(0, 10_000, CancellationToken.None));
        var eventsB = await second.ReadAsync(u => u.Events.ListAsync(0, 10_000, CancellationToken.None));
        Assert.Equal(eventsA.Select(e => (e.EventId, e.Type, e.PayloadJson)), eventsB.Select(e => (e.EventId, e.Type, e.PayloadJson)));
    }

    [Fact]
    public async Task A_second_run_on_an_exhausted_workspace_executes_nothing_new()
    {
        await using var h = ExplorationHarness.Create();
        await h.RunAsync(seed: "a");
        var before = await h.CanonicalAsync();
        var actions = h.Device.ExecutedActions.Count(a => a.Type != ActionType.LaunchApp);

        var again = await h.RunAsync(seed: "b");

        Assert.Equal(RunFinishReason.Exhausted, again.Reason);
        Assert.Equal(0, again.Counters.Attempts);
        Assert.Equal(actions, h.Device.ExecutedActions.Count(a => a.Type != ActionType.LaunchApp));
        Assert.Equal(JsonSerializer.Serialize(before.States), JsonSerializer.Serialize((await h.CanonicalAsync()).States));
        Assert.Equal(JsonSerializer.Serialize(before.Edges), JsonSerializer.Serialize((await h.CanonicalAsync()).Edges));
    }

    [Fact]
    public async Task Action_budget_stops_the_run()
    {
        await using var h = ExplorationHarness.Create(settings: ExplorationHarness.DemoSettings(Budgets.Default with { MaxActions = 4 }));

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.BudgetExhausted, result.Reason);
        Assert.Contains("maxActions", result.Detail, StringComparison.Ordinal);
        Assert.Equal(4, result.Counters.DeviceActions + result.Counters.NavigationActions + result.Counters.Resets);
        var run = await h.ReadAsync(u => u.Runs.GetLatestAsync(CancellationToken.None));
        Assert.Equal(RunStatus.Finished, run!.Status);
        Assert.Equal(RunFinishReason.BudgetExhausted, run.FinishReason);
        var pending = await h.ReadAsync(u => u.Frontier.ListTasksAsync(FrontierTaskStatus.Pending, CancellationToken.None));
        Assert.NotEmpty(pending);
    }

    [Fact]
    public async Task Depth_budget_cancels_deeper_tasks()
    {
        await using var h = ExplorationHarness.Create(settings: ExplorationHarness.DemoSettings(Budgets.Default with { MaxDepth = 1 }));

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);
        var cancelled = await h.ReadAsync(u => u.Frontier.ListTasksAsync(FrontierTaskStatus.Cancelled, CancellationToken.None));
        Assert.NotEmpty(cancelled);
        Assert.All(cancelled, t => Assert.True(t.Depth >= 1));
        var states = await StatesByScreenAsync(h);
        Assert.True(states.ContainsKey("screen.settings"));
        Assert.False(states.ContainsKey("screen.verify"));
        Assert.False(states.ContainsKey("screen.details"));
    }

    [Fact]
    public async Task Outcome_unknown_is_not_retried()
    {
        await using var h = ExplorationHarness.Create();
        h.Device.Faults[FakeDemoApp.Id("home.whatsnew")] = ActionOutcome.OutcomeUnknown;

        var result = await h.RunAsync();

        Assert.Equal(1, result.Counters.OutcomeUnknown);
        Assert.Single(h.Device.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("home.whatsnew"));
        var unknown = Assert.Single(await h.ReadAsync(u => u.Frontier.ListTasksAsync(FrontierTaskStatus.OutcomeUnknown, CancellationToken.None)));
        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Contains(edges, e => e.ActionKey == unknown.ActionKey && e.Status == EdgeStatus.Unknown && e.ToStateId is null);
    }

    [Fact]
    public async Task Failed_action_fails_its_task_and_the_run_continues()
    {
        await using var h = ExplorationHarness.Create();
        h.Device.Faults[FakeDemoApp.Id("home.whatsnew")] = ActionOutcome.Failed;

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);
        Assert.Single(await h.ReadAsync(u => u.Frontier.ListTasksAsync(FrontierTaskStatus.Failed, CancellationToken.None)));
        Assert.True((await StatesByScreenAsync(h)).ContainsKey("screen.settings"));
    }

    [Fact]
    public async Task Missing_setup_flow_fails_the_preflight()
    {
        await using var h = ExplorationHarness.Create();
        File.Delete(h.SetupFlow);

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.PreflightFailed, result.Reason);
        Assert.Empty(h.Device.ExecutedActions);
        Assert.Empty(await h.ReadAsync(u => u.Graph.ListStatesAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task Preflight_refuses_a_start_screen_outside_the_app()
    {
        var app = FakeDemoApp.Basic() with { SetupScreenId = "launcher" };
        await using var h = ExplorationHarness.Create(app);

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.PreflightFailed, result.Reason);
        Assert.Contains("start state", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_device_fails_the_preflight()
    {
        await using var h = ExplorationHarness.Create();

        var result = await h.Engine().RunAsync(h.Options() with { DeviceId = "emulator-9999" }, CancellationToken.None);

        Assert.Equal(RunFinishReason.PreflightFailed, result.Reason);
        Assert.Empty(h.Device.ExecutedActions);
    }

    [Fact]
    public async Task Without_setup_flow_the_app_is_launched_and_loading_is_waited_out()
    {
        var settings = ExplorationHarness.DemoSettings() with { Profiles = [ExplorationHarness.Profile with { SetupFlow = null }] };
        await using var h = ExplorationHarness.Create(settings: settings);

        var result = await h.RunAsync();

        Assert.Equal(RunFinishReason.Exhausted, result.Reason);
        Assert.Empty(h.Device.FlowsRun);
        Assert.Contains(h.Device.ExecutedActions, a => a.Type == ActionType.LaunchApp);
        var states = await StatesByScreenAsync(h);
        Assert.True(states.ContainsKey("screen.profile"));
        Assert.True(states.ContainsKey("screen.home"));

        // profile.basic leads through the loading screen; stabilization waits until it is gone.
        Assert.False(states.ContainsKey("screen.loading"));
        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Contains(edges, e => e.FromStateId == states["screen.profile"].StateId && e.ToStateId == states["screen.home"].StateId);
    }

    [Fact]
    public async Task Cancellation_stops_the_run_and_records_it()
    {
        await using var h = ExplorationHarness.Create();
        using var cts = new CancellationTokenSource();
        var options = h.Options() with
        {
            Progress = line =>
            {
                if (line.StartsWith("explore", StringComparison.Ordinal))
                {
                    cts.Cancel();
                }
            },
        };

        var result = await h.Engine().RunAsync(options, cts.Token);

        Assert.Equal(RunFinishReason.StoppedByOperator, result.Reason);
        var run = await h.ReadAsync(u => u.Runs.GetAsync(result.RunId, CancellationToken.None));
        Assert.Equal(RunFinishReason.StoppedByOperator, run!.FinishReason);
        Assert.NotNull(await h.ReadAsync(u => u.Frontier.GetLatestCheckpointAsync(result.RunId, CancellationToken.None)));
    }
}

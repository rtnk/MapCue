using MapCue.Application.Configuration;
using MapCue.Application.Profiles;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// STEP-07: the two test profiles of the demo app (<c>basic</c>, <c>investor</c>) explored one after the other in one workspace
/// (<see cref="FakeDemoProfiles"/>): shared states, separate contexts, edges, tasks and evidence, the Investments guard, the profile switch
/// policy, recipes per context and the R-04 margin of the profile variants.
/// </summary>
public class ProfilesEndToEndTests
{
    private static string Investments => "id:" + FakeDemoApp.Id("tab.investments");

    private static ExplorationHarness Create(MapCueSettings? settings = null) => ExplorationHarness.Create(FakeDemoProfiles.Create(), settings ?? ExplorationHarness.ProfilesSettings());

    /// <summary>The screen marker (<c>screen.*</c>) of a state, or its first anchor for AlertDialogs.</summary>
    private static string Marker(ScreenState state) =>
        state.IdentityTokens.Where(t => t.StartsWith("a:id:", StringComparison.Ordinal)).Select(t => t[(t.LastIndexOf('/') + 1)..]).FirstOrDefault(t => t.StartsWith("screen.", StringComparison.Ordinal))
        ?? string.Join(',', state.IdentityTokens.Where(t => t.StartsWith("a:", StringComparison.Ordinal)));

    private static async Task<Dictionary<string, string>> MarkersAsync(ExplorationHarness h) =>
        (await h.ReadAsync(uow => uow.Graph.ListStatesAsync(CancellationToken.None))).ToDictionary(s => s.StateId, Marker, StringComparer.Ordinal);

    private static async Task<ExplorationContext> ContextOfAsync(ExplorationHarness h, string profileId) =>
        (await h.ReadAsync(uow => uow.Graph.ListContextsAsync(CancellationToken.None))).Single(c => c.ProfileId == profileId);

    [Fact]
    public async Task Two_profiles_share_states_but_never_edges_tasks_or_evidence()
    {
        await using var h = Create();

        var basic = await h.RunProfileAsync("basic");
        var basicEdges = await h.ReadAsync(uow => uow.Graph.ListEdgesAsync(CancellationToken.None));
        var investor = await h.RunProfileAsync("investor");

        Assert.Equal(RunFinishReason.Exhausted, basic.Reason);
        Assert.Equal(RunFinishReason.Exhausted, investor.Reason);
        var basicCtx = await ContextOfAsync(h, "basic");
        var investorCtx = await ContextOfAsync(h, "investor");
        Assert.NotEqual(basicCtx.ContextId, investorCtx.ContextId);
        Assert.Equal(["investments"], investorCtx.Capabilities);

        // One state per screen marker (no duplicate from a profile difference, R-04), shared by both contexts except Investments.
        var markers = await MarkersAsync(h);
        Assert.Equal(markers.Count, markers.Values.Distinct(StringComparer.Ordinal).Count());
        var variants = await h.ReadAsync(uow => uow.Graph.ListVariantsAsync(CancellationToken.None));
        string[] Seen(string ctx) => [.. variants.Where(v => v.ContextId == ctx).Select(v => markers[v.StateId]).Order(StringComparer.Ordinal)];
        string[] shared = ["screen.accounts", "screen.details", "screen.home", "screen.profile", "screen.settings", "screen.verify", "screen.whatsnew"];
        Assert.Equal(shared, Seen(basicCtx.ContextId));
        Assert.Equal([.. shared.Append("screen.investments").Order(StringComparer.Ordinal)], Seen(investorCtx.ContextId));
        Assert.Equal(1, investor.Counters.NewStates);
        Assert.Equal(1, investor.Counters.NewScreens);

        // The investor run added edges of its own context only; the basic edges are untouched.
        var edges = await h.ReadAsync(uow => uow.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Equal(basicEdges.OrderBy(e => e.EdgeId, StringComparer.Ordinal), edges.Where(e => e.ContextId == basicCtx.ContextId).OrderBy(e => e.EdgeId, StringComparer.Ordinal));
        Assert.All(edges, e => Assert.True(e.ContextId == basicCtx.ContextId || e.ContextId == investorCtx.ContextId));

        // Different actions: the Investments tab only in the investor context; shared actions as separate edges per context.
        var candidates = await h.ReadAsync(uow => uow.Graph.ListCandidatesAsync(null, CancellationToken.None));
        var tabInvestments = candidates.Where(c => c.CanonicalSelector.Contains("tab.investments", StringComparison.Ordinal)).Select(c => c.ActionKey).ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(tabInvestments);
        Assert.DoesNotContain(edges, e => e.ContextId == basicCtx.ContextId && tabInvestments.Contains(e.ActionKey));
        Assert.Contains(edges, e => e.ContextId == investorCtx.ContextId && tabInvestments.Contains(e.ActionKey) && markers[e.ToStateId!] == "screen.investments");
        var home = markers.Single(kv => kv.Value == "screen.home").Key;
        var homeEdges = edges.Where(e => e.FromStateId == home).GroupBy(e => e.ActionKey, StringComparer.Ordinal).ToList();
        Assert.Contains(homeEdges, g => g.Select(e => e.ContextId).Distinct(StringComparer.Ordinal).Count() == 2);
        Assert.All(edges, e => Assert.Equal(1, edges.Count(x => x.FromStateId == e.FromStateId && x.ActionKey == e.ActionKey && x.ContextId == e.ContextId)));

        // Evidence, snapshots, observations and tasks: each in exactly one context, and an observation's snapshots in its edge's context.
        var (snapshots, observations, tasks) = await h.ReadAsync(async uow => (
            await uow.Graph.ListSnapshotsAsync(CancellationToken.None),
            await uow.Graph.ListObservationsAsync(CancellationToken.None),
            await uow.Frontier.ListTasksAsync(null, CancellationToken.None)));
        var snapshotContext = snapshots.ToDictionary(s => s.SnapshotId, s => s.ContextId, StringComparer.Ordinal);
        foreach (var o in observations)
        {
            var edge = edges.Single(e => e.EdgeId == o.EdgeId);
            Assert.Equal(edge.ContextId, o.ContextId);
            Assert.Equal(edge.ContextId, snapshotContext[o.BeforeSnapshotId]);
            Assert.True(o.AfterSnapshotId is null || snapshotContext[o.AfterSnapshotId] == edge.ContextId);
        }

        foreach (var ctx in new[] { basicCtx, investorCtx })
        {
            var evidence = await h.ReadAsync(uow => uow.Graph.ListEvidenceAsync(ctx.ContextId, null, CancellationToken.None));
            Assert.NotEmpty(evidence);
            Assert.All(evidence, e => Assert.True(e.SnapshotId is null || snapshotContext[e.SnapshotId] == ctx.ContextId));
            var attempts = observations.Where(o => o.ContextId == ctx.ContextId).Select(o => o.AttemptId).ToHashSet(StringComparer.Ordinal);
            Assert.All(evidence.Where(e => e.AttemptId is not null), e => Assert.Contains(e.AttemptId!, attempts));
        }

        Assert.All(tasks.Where(t => t.Type == TaskType.ExploreAction && t.Status == FrontierTaskStatus.Completed), t => Assert.Contains(edges, e => e.FromStateId == t.FromStateId && e.ActionKey == t.ActionKey && e.ContextId == t.ContextId));
        Assert.Equal(tasks.Count, tasks.Select(t => t.TaskKey).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task The_investments_guard_is_inferred_after_one_profile_and_observed_only_after_both()
    {
        await using var h = Create();

        await h.RunProfileAsync("investor");
        var afterInvestor = await ProfileAnalysis.AnalyzeAsync(h.Store, CancellationToken.None);
        var inferred = Assert.Single(afterInvestor.Guards, g => g.ElementKey == Investments);
        Assert.Equal((GuardLevel.Inferred, "investments"), (inferred.Level, inferred.Capability));
        Assert.Empty(inferred.AbsentIn);

        await h.RunProfileAsync("basic");
        var report = await ProfileAnalysis.AnalyzeAsync(h.Store, CancellationToken.None);
        var basicCtx = await ContextOfAsync(h, "basic");
        var investorCtx = await ContextOfAsync(h, "investor");
        var observed = Assert.Single(report.Guards, g => g.ElementKey == Investments);
        Assert.Equal((GuardLevel.Observed, "investments"), (observed.Level, observed.Capability));
        Assert.Equal([investorCtx.ContextId], observed.PresentIn);
        Assert.Equal([basicCtx.ContextId], observed.AbsentIn);
        var markers = await MarkersAsync(h);
        Assert.Equal(["screen.accounts", "screen.home", "screen.settings"], observed.States.Where(s => markers[s] != "screen.investments").Select(s => markers[s]).Order(StringComparer.Ordinal));

        // Evidence of both sides, each from its own context.
        var evidence = new List<Evidence>();
        foreach (var ctx in new[] { basicCtx, investorCtx })
        {
            evidence.AddRange(await h.ReadAsync(uow => uow.Graph.ListEvidenceAsync(ctx.ContextId, null, CancellationToken.None)));
        }

        Assert.Contains(observed.EvidenceIds, id => evidence.Single(e => e.EvidenceId == id).ContextId == basicCtx.ContextId);
        Assert.Contains(observed.EvidenceIds, id => evidence.Single(e => e.EvidenceId == id).ContextId == investorCtx.ContextId);

        // Elements shared by both profiles are no hypotheses; Investments-only elements stay inferred (the screen is never seen in basic).
        Assert.DoesNotContain(report.Guards, g => g.ElementKey.Contains("tab.accounts", StringComparison.Ordinal));
        Assert.Contains(report.Guards, g => g.ElementKey.Contains("investments.buy", StringComparison.Ordinal) && g.Level == GuardLevel.Inferred);
        Assert.DoesNotContain(report.Guards, g => g.Level == GuardLevel.Refuted);
    }

    [Fact]
    public async Task Basic_first_gives_no_hypothesis_until_the_investor_profile_is_explored()
    {
        await using var h = Create();

        await h.RunProfileAsync("basic");
        Assert.DoesNotContain((await ProfileAnalysis.AnalyzeAsync(h.Store, CancellationToken.None)).Guards, g => g.ElementKey == Investments);

        await h.RunProfileAsync("investor");
        var guard = Assert.Single((await ProfileAnalysis.AnalyzeAsync(h.Store, CancellationToken.None)).Guards, g => g.ElementKey == Investments);
        Assert.Equal((GuardLevel.Observed, "investments"), (guard.Level, guard.Capability));
    }

    [Fact]
    public async Task Coverage_matrix_equivalence_classes_and_recipes_are_per_context()
    {
        await using var h = Create();
        await h.RunProfileAsync("basic");
        await h.RunProfileAsync("investor");
        var basicCtx = (await ContextOfAsync(h, "basic")).ContextId;
        var investorCtx = (await ContextOfAsync(h, "investor")).ContextId;
        var markers = await MarkersAsync(h);
        var report = await ProfileAnalysis.AnalyzeAsync(h.Store, CancellationToken.None);

        Assert.Equal(new[] { basicCtx, investorCtx }.Order(StringComparer.Ordinal), report.Contexts.Select(c => c.ContextId));
        CoverageCell Cell(string marker, string ctx) => report.Coverage.Single(r => markers[r.StateId] == marker).Cells.Single(c => c.ContextId == ctx);
        Assert.Equal(CoverageLevel.NotObserved, Cell("screen.investments", basicCtx).Level);
        Assert.Equal(CoverageLevel.Observed, Cell("screen.investments", investorCtx).Level);
        Assert.True(Cell("screen.investments", investorCtx).HasRecipe);
        Assert.All(report.Coverage.Where(r => markers[r.StateId] != "screen.investments"), r => Assert.All(r.Cells, c => Assert.Equal(CoverageLevel.Observed, c.Level)));
        Assert.True(Cell("screen.home", investorCtx).OutgoingEdges > Cell("screen.home", basicCtx).OutgoingEdges);

        // Equivalence classes: profiles differ on the tab screens, agree on the others.
        IReadOnlyList<IReadOnlyList<string>> Classes(string marker) => report.Equivalence.Single(e => markers[e.StateId] == marker).Classes;
        Assert.Equal(2, Classes("screen.home").Count);
        Assert.Equal(2, Classes("screen.accounts").Count);
        Assert.Single(Classes("screen.whatsnew"));
        Assert.Single(Classes("screen.details"));

        // Recipes: one per (context, reachable state), only over edges of that context, starting with the context's setup flow.
        var (recipes, edges) = await h.ReadAsync(async uow => (await uow.Graph.ListRecipesAsync(null, CancellationToken.None), await uow.Graph.ListEdgesAsync(CancellationToken.None)));
        var edgeContext = edges.ToDictionary(e => e.EdgeId, e => e.ContextId, StringComparer.Ordinal);
        Assert.All(recipes, r =>
        {
            Assert.Equal(RecipeStepKind.SetupFlow, r.Steps[0].Kind);
            Assert.All(r.BasedOnEdges, e => Assert.Equal(r.ContextId, edgeContext[e]));
            Assert.Equal(r.TargetStateId, r.Steps[^1].ExpectedStateId);
            Assert.Equal(ReachabilityRecipe.IdOf(r.ContextId, r.TargetStateId), r.RecipeId);
        });
        Assert.DoesNotContain(recipes, r => r.ContextId == basicCtx && markers[r.TargetStateId] == "screen.investments");
        var investments = Assert.Single(recipes, r => r.ContextId == investorCtx && markers[r.TargetStateId] == "screen.investments");
        Assert.Equal(2, investments.Steps.Count);
        Assert.Equal(7, recipes.Count(r => r.ContextId == basicCtx));
        Assert.Equal(8, recipes.Count(r => r.ContextId == investorCtx));
    }

    [Fact]
    public async Task A_run_refuses_to_switch_to_another_profile_by_default()
    {
        await using var h = Create();

        await h.RunProfileAsync("basic");

        Assert.Contains(h.Device.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("profile.basic"));
        Assert.DoesNotContain(h.Device.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("profile.investor"));
        var calls = await h.ReadAsync(uow => uow.AgentCalls.ListAsync(null, CancellationToken.None));
        Assert.NotEmpty(calls);
        var contexts = await h.ReadAsync(uow => uow.Graph.ListContextsAsync(CancellationToken.None));
        Assert.Equal(["basic"], contexts.Select(c => c.ProfileId));
        Assert.DoesNotContain(await MarkersAsync(h), kv => kv.Value == "screen.investments");
    }

    [Fact]
    public async Task A_shared_candidate_has_its_risk_and_status_per_context()
    {
        await using var h = Create();
        await h.RunProfileAsync("basic");
        await h.RunProfileAsync("investor");
        var basicCtx = (await ContextOfAsync(h, "basic")).ContextId;
        var investorCtx = (await ContextOfAsync(h, "investor")).ContextId;
        var (candidates, assessments) = await h.ReadAsync(async uow => (await uow.Graph.ListCandidatesAsync(null, CancellationToken.None), await uow.Graph.ListCandidateAssessmentsAsync(null, CancellationToken.None)));

        // The picker's profile.investor button: a profile switch in basic (never run), the own profile in investor (explored).
        var button = Assert.Single(candidates, c => c.CanonicalSelector.Contains("profile.investor", StringComparison.Ordinal));
        CandidateAssessment Of(string ctx) => assessments.Single(a => a.StateId == button.StateId && a.ActionKey == button.ActionKey && a.ContextId == ctx);
        Assert.Equal((MapCue.Domain.RiskClass.AccountChange, CandidateStatus.SkippedByPolicy), (Of(basicCtx).RiskClass, Of(basicCtx).Status));
        Assert.Equal((MapCue.Domain.RiskClass.Navigational, CandidateStatus.Explored), (Of(investorCtx).RiskClass, Of(investorCtx).Status));

        // Every explored edge has an explored assessment of its own context.
        var edges = await h.ReadAsync(uow => uow.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.All(edges, e => Assert.Equal(CandidateStatus.Explored, assessments.Single(a => a.StateId == e.FromStateId && a.ActionKey == e.ActionKey && a.ContextId == e.ContextId).Status));
    }

    [Fact]
    public async Task With_profile_switch_allowed_the_basic_context_reaches_the_investor_app()
    {
        await using var h = Create(ExplorationHarness.ProfilesSettings(policy: PolicySettings.Default with { ProfileSwitch = ProfileSwitchPolicy.Allow }));

        var result = await h.RunProfileAsync("basic");

        // What the default prevents: the investor profile is chosen inside the basic context, so the basic graph gets an edge into the
        // investor app and later navigation over it diverges (the investor Accounts tab is not a state of this context).
        Assert.Contains(h.Device.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("profile.investor"));
        var (candidates, edges) = await h.ReadAsync(async uow => (await uow.Graph.ListCandidatesAsync(null, CancellationToken.None), await uow.Graph.ListEdgesAsync(CancellationToken.None)));
        var switchKeys = candidates.Where(c => c.CanonicalSelector.Contains("profile.investor", StringComparison.Ordinal)).Select(c => c.ActionKey).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(edges, e => switchKeys.Contains(e.ActionKey) && e.Status == EdgeStatus.Observed);
        Assert.Equal(RunFinishReason.AllRemainingBlocked, result.Reason);
    }

    [Fact]
    public async Task Without_the_profile_variant_rule_the_investor_accounts_tab_is_a_duplicate_state()
    {
        // R-04: Home investor is a variant by similarity (6/7 ≈ 0.857 ≥ τ_state), Accounts investor is not (5/6 ≈ 0.83 < 0.85).
        await using var h = Create(ExplorationHarness.ProfilesSettings(profileVariantMaxDelta: 0));
        await h.RunProfileAsync("basic");
        await h.RunProfileAsync("investor");

        var markers = await MarkersAsync(h);
        Assert.Single(markers, kv => kv.Value == "screen.home");
        Assert.Equal(2, markers.Count(kv => kv.Value == "screen.accounts"));
    }

    [Fact]
    public async Task Two_workspaces_explored_in_the_same_order_give_the_same_graph()
    {
        await using var a = Create();
        await using var b = Create();
        foreach (var h in new[] { a, b })
        {
            await h.RunProfileAsync("basic", seed: "s1");
            await h.RunProfileAsync("investor", seed: "s2");
        }

        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(await a.CanonicalAsync()), System.Text.Json.JsonSerializer.Serialize(await b.CanonicalAsync()));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(await ProfileAnalysis.AnalyzeAsync(a.Store, CancellationToken.None)),
            System.Text.Json.JsonSerializer.Serialize(await ProfileAnalysis.AnalyzeAsync(b.Store, CancellationToken.None)));
    }
}

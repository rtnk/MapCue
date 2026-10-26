using MapCue.Application.Graph;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Infrastructure.Tests.Graph;

public class GraphCommitTests
{
    private static readonly string _accountsKey = GraphHarness.Tap("app:id/tab_accounts").ActionKey;

    [Fact]
    public async Task Cycle_home_accounts_home_records_edges_without_new_tasks_for_known_pairs()
    {
        await using var h = GraphHarness.Create();

        var first = await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var back = await h.ActAsync("att_2", GraphHarness.Accounts(), GraphHarness.Back, GraphHarness.Home());
        var tasksAfterCycle = (await h.SummaryAsync()).Tasks;
        var again = await h.ActAsync("att_3", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts(rows: 7));

        Assert.Equal(first.FromStateId, back.ToStateId);
        Assert.Equal(first.ToStateId, back.FromStateId);
        Assert.Equal(first.EdgeId, again.EdgeId);
        Assert.Equal(first.ToStateId, again.ToStateId);
        Assert.Empty(again.NewStateIds);
        Assert.Equal(0, again.NewTasks);

        var summary = await h.SummaryAsync();
        Assert.Equal(2, summary.States);
        Assert.Equal(2, summary.Edges);
        Assert.Equal(3, summary.Observations);
        Assert.Equal(tasksAfterCycle, summary.Tasks);

        var edge = await h.ReadAsync(u => u.Graph.GetEdgeAsync(first.FromStateId, _accountsKey, GraphHarness.Basic.ContextId, CancellationToken.None));
        Assert.Equal((EdgeStatus.Observed, 2, false), (edge!.Status, edge.ObservationCount, edge.Nondeterministic));

        var tasks = await h.ReadAsync(u => u.Frontier.ListTasksAsync(null, CancellationToken.None));
        Assert.Equal(2, tasks.Count(t => t.Type == TaskType.ExploreAction && t.Status == FrontierTaskStatus.Completed));
        Assert.Equal(2, tasks.Count(t => t.Type == TaskType.AnalyzeState && t.Status == FrontierTaskStatus.Pending));
        Assert.Equal(tasks.OrderByDescending(t => t.Priority).ThenBy(t => t.Depth).ThenBy(t => t.CreatedSeq).Select(t => t.TaskId), tasks.Select(t => t.TaskId));
    }

    [Fact]
    public async Task Committing_the_same_attempt_twice_changes_nothing()
    {
        await using var h = GraphHarness.Create();
        await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var before = await h.CanonicalAsync();
        var events = (await h.SummaryAsync()).Events;

        var repeated = await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());

        Assert.True(repeated.AlreadyCommitted);
        Assert.Equal(GraphJson.Serialize(before), GraphJson.Serialize(await h.CanonicalAsync()));
        Assert.Equal(events, (await h.SummaryAsync()).Events);
    }

    [Fact]
    public async Task Different_targets_mark_the_edge_nondeterministic()
    {
        await using var h = GraphHarness.Create();
        await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var other = await h.ActAsync("att_2", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Settings());

        var edge = await h.ReadAsync(u => u.Graph.GetEdgeAsync(other.FromStateId, _accountsKey, GraphHarness.Basic.ContextId, CancellationToken.None));
        Assert.True(edge!.Nondeterministic);
        Assert.Equal(2, edge.ObservationCount);
        var events = await h.ReadAsync(u => u.Events.ListAsync(0, 1000, CancellationToken.None));
        Assert.Contains(events, e => e.Type == GraphEventTypes.EdgeNondeterministic && e.SubjectId == edge.EdgeId);
    }

    [Fact]
    public async Task Outcome_unknown_never_lowers_an_observed_edge()
    {
        await using var h = GraphHarness.Create();
        var observed = await h.ActAsync("att_1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts());
        var unknown = await h.ActAsync("att_2", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), null, outcome: ActionOutcome.OutcomeUnknown);

        Assert.Equal(EdgeStatus.Observed, observed.EdgeStatus);
        Assert.Equal(EdgeStatus.Observed, unknown.EdgeStatus);
        Assert.Null(unknown.ToStateId);
        var observation = await h.ReadAsync(u => u.Graph.GetObservationAsync("att_2", CancellationToken.None));
        Assert.Equal(ActionOutcome.OutcomeUnknown, observation!.Outcome);
    }

    [Fact]
    public async Task Two_profiles_share_the_state_but_never_variants_edges_or_evidence()
    {
        await using var h = GraphHarness.Create();
        var basic = await h.ActAsync("att_b1", GraphHarness.Home(), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts(), GraphHarness.Basic);
        var investor = await h.ActAsync("att_i1", GraphHarness.Home(investor: true, greeting: "Witaj, inwestorze"), GraphHarness.Tap("app:id/tab_accounts"), GraphHarness.Accounts(rows: 5), GraphHarness.Investor);
        var investments = await h.ActAsync("att_i2", GraphHarness.Home(investor: true, greeting: "Witaj, inwestorze"), GraphHarness.Tap("app:id/investments"), GraphHarness.Settings(), GraphHarness.Investor);

        // Shared map: the same Home and Accounts states for both profiles (ADR-0006).
        Assert.Equal(basic.FromStateId, investor.FromStateId);
        Assert.Equal(basic.ToStateId, investor.ToStateId);
        Assert.Equal(basic.FromStateId, investments.FromStateId);

        var variants = await h.ReadAsync(u => u.Graph.ListVariantsAsync(CancellationToken.None));
        var homeVariants = variants.Where(v => v.StateId == basic.FromStateId).ToDictionary(v => v.ContextId);
        Assert.Equal(2, homeVariants.Count);
        Assert.NotEqual(homeVariants[GraphHarness.Basic.ContextId].ElementSetHash, homeVariants[GraphHarness.Investor.ContextId].ElementSetHash);
        Assert.Empty(homeVariants[GraphHarness.Basic.ContextId].AddedElementKeys);
        Assert.Contains("id:app:id/investments=investments", homeVariants[GraphHarness.Investor.ContextId].AddedElementKeys);

        // Edges are per context: the investments edge exists only for the investor.
        var edges = await h.ReadAsync(u => u.Graph.ListEdgesAsync(CancellationToken.None));
        Assert.Equal(2, edges.Count(e => e.ActionKey == _accountsKey));
        Assert.NotEqual(basic.EdgeId, investor.EdgeId);
        Assert.Equal(GraphHarness.Investor.ContextId, Assert.Single(edges, e => e.ActionKey == GraphHarness.Tap("app:id/investments").ActionKey).ContextId);
        Assert.All(edges.Where(e => e.EdgeId == basic.EdgeId), e => Assert.Equal(1, e.ObservationCount));

        // Evidence and snapshots never cross contexts.
        var basicEvidence = await h.ReadAsync(u => u.Graph.ListEvidenceAsync(GraphHarness.Basic.ContextId, basic.FromStateId, CancellationToken.None));
        var investorEvidence = await h.ReadAsync(u => u.Graph.ListEvidenceAsync(GraphHarness.Investor.ContextId, basic.FromStateId, CancellationToken.None));
        Assert.NotEmpty(basicEvidence);
        Assert.NotEmpty(investorEvidence);
        Assert.Empty(basicEvidence.Select(e => e.EvidenceId).Intersect(investorEvidence.Select(e => e.EvidenceId)));
        Assert.All(basicEvidence, e => Assert.Equal(GraphHarness.Basic.ContextId, e.ContextId));
        var snapshots = await h.ReadAsync(u => u.Graph.ListSnapshotsAsync(CancellationToken.None));
        foreach (var observation in await h.ReadAsync(u => u.Graph.ListObservationsAsync(CancellationToken.None)))
        {
            Assert.Equal(observation.ContextId, snapshots.Single(s => s.SnapshotId == observation.BeforeSnapshotId).ContextId);
        }

        // Tasks are per context too: each profile analyses Home and Accounts once.
        var tasks = await h.ReadAsync(u => u.Frontier.ListTasksAsync(null, CancellationToken.None));
        Assert.Equal(2, tasks.Count(t => t.Type == TaskType.AnalyzeState && t.ContextId == GraphHarness.Basic.ContextId));
        Assert.Equal(3, tasks.Count(t => t.Type == TaskType.AnalyzeState && t.ContextId == GraphHarness.Investor.ContextId));
    }

    [Fact]
    public async Task Fingerprint_can_be_recomputed_from_the_stored_redacted_snapshot()
    {
        await using var h = GraphHarness.Create();
        var recorded = await h.RecordAsync(GraphHarness.Home());
        await h.Committer.CommitObservationAsync(GraphHarness.Basic, recorded, CancellationToken.None);

        var stored = await h.Artifacts.ReadTextAsync(recorded.Snapshot.RawSha256, CancellationToken.None);
        var document = GraphJson.Deserialize<RawSnapshotDocument>(stored!);
        var recomputed = SnapshotNormalizer.Normalize(document.Elements, new NormalizationOptions(Platform.Android));
        Assert.Equal(recorded.Snapshot.IdentityHash, recomputed.IdentityHash);
        Assert.Equal(recorded.Snapshot.LayoutHash, recomputed.LayoutHash);
        Assert.Equal(RawSnapshotDocument.CurrentSchemaVersion, document.SchemaVersion);
    }

    [Fact]
    public async Task Sensitive_text_is_redacted_before_any_artifact_is_written()
    {
        await using var h = GraphHarness.Create();
        h.Redactor.RegisterSensitiveValue("S3cret-Pa55");
        var observation = GraphHarness.Observe(
            "login",
            MapCue.Testing.UiTrees.Text("anna.kowalska@example.com", "app:id/user"),
            MapCue.Testing.UiTrees.Node("android.widget.EditText", "app:id/password", "S3cret-Pa55", clickable: true));
        var recorded = await h.RecordAsync(observation);
        await h.Committer.CommitObservationAsync(GraphHarness.Basic, recorded, CancellationToken.None);

        foreach (var file in Directory.EnumerateFiles(h.Artifacts.Root, "*", SearchOption.AllDirectories))
        {
            var text = await File.ReadAllTextAsync(file);
            Assert.DoesNotContain("anna.kowalska@example.com", text, StringComparison.Ordinal);
            Assert.DoesNotContain("S3cret-Pa55", text, StringComparison.Ordinal);
        }

        Assert.Contains(recorded.Normalized.Elements, e => e.Label == "‹redacted›");
        var raw = Assert.Single(recorded.Evidence, e => e.Kind == EvidenceKind.RawSnapshot);
        Assert.Equal(2, raw.RedactionCount);
        Assert.Equal(["email", "secret"], raw.RedactionRules);
    }

    [Fact]
    public async Task Ambiguous_identity_creates_a_provisional_state_and_records_hypotheses()
    {
        await using var h = GraphHarness.Create();
        MapCue.Testing.UiNode Screen(params string[] ids) => MapCue.Testing.UiTrees.Container("android.widget.FrameLayout", [.. ids.Select(id => MapCue.Testing.UiTrees.Button("app:id/" + id))]);
        // a and b are different screens (similarity 0.5); c is equally close (0.6) to both.
        string[] shared = ["s1", "s2", "s3", "s4", "s5", "s6"];
        var a = await h.Committer.CommitObservationAsync(GraphHarness.Basic, await h.RecordAsync(GraphHarness.Observe("a", Screen([.. shared, "a1", "a2", "a3"]))), CancellationToken.None);
        var b = await h.Committer.CommitObservationAsync(GraphHarness.Basic, await h.RecordAsync(GraphHarness.Observe("b", Screen([.. shared, "b1", "b2", "b3"]))), CancellationToken.None);
        var c = await h.Committer.CommitObservationAsync(GraphHarness.Basic, await h.RecordAsync(GraphHarness.Observe("c", Screen([.. shared, "c1"]))), CancellationToken.None);

        Assert.NotEqual(a.ScreenId, b.ScreenId);
        Assert.Equal(ResolutionKind.Ambiguous, c.Resolution);
        Assert.True(c.NewState);
        var state = await h.ReadAsync(u => u.Graph.GetStateAsync(c.StateId, CancellationToken.None));
        Assert.Equal(IdentityStatus.Provisional, state!.IdentityStatus);
        var events = await h.ReadAsync(u => u.Events.ListAsync(0, 1000, CancellationToken.None));
        var hypothesis = Assert.Single(events, e => e.Type == GraphEventTypes.IdentityAmbiguous);
        Assert.Contains(a.StateId, hypothesis.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(b.StateId, hypothesis.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variant_resolution_is_remembered_as_an_alias()
    {
        await using var h = GraphHarness.Create();
        var basic = await h.Committer.CommitObservationAsync(GraphHarness.Basic, await h.RecordAsync(GraphHarness.Home()), CancellationToken.None);
        var investor = await h.Committer.CommitObservationAsync(GraphHarness.Investor, await h.RecordAsync(GraphHarness.Home(investor: true), GraphHarness.Investor), CancellationToken.None);

        Assert.Equal(ResolutionKind.KnownStateVariant, investor.Resolution);
        Assert.Equal(basic.StateId, investor.StateId);
        var aliases = await h.ReadAsync(u => u.Graph.GetIdentityAliasesAsync(SnapshotNormalizer.FingerprintVersion, CancellationToken.None));
        Assert.Equal(basic.StateId, Assert.Single(aliases).Value);
        var again = await h.Committer.CommitObservationAsync(GraphHarness.Investor, await h.RecordAsync(GraphHarness.Home(investor: true), GraphHarness.Investor), CancellationToken.None);
        Assert.Equal(ResolutionKind.KnownState, again.Resolution);
    }
}

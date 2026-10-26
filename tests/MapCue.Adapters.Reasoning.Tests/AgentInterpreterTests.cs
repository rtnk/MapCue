using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Contracts.Schemas;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.Adapters.Reasoning.Tests;

public sealed class AgentInterpreterTests : IAsyncLifetime
{
    private AgentHarness _h = null!;
    private string _stateId = null!;
    private Application.Graph.RecordedSnapshot _snapshot = null!;

    public async Task InitializeAsync()
    {
        _h = AgentHarness.Create();
        (_stateId, _snapshot) = await _h.CommitAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private DeterministicFakeReasoner Fake(params string[] fixtures) =>
        new DeterministicFakeReasoner().When(_snapshot.Snapshot.NormalizedSha256, (req, n) => AgentHarness.Golden(fixtures[Math.Min(n, fixtures.Length - 1)], req, _snapshot));

    private Task<IReadOnlyList<AgentCall>> CallsAsync() => _h.Store.InTransactionAsync(uow => uow.AgentCalls.ListAsync(_stateId, CancellationToken.None), CancellationToken.None);

    [Fact]
    public async Task Accepted_answer_records_the_call_artifacts_and_agent_candidates()
    {
        var reasoner = Fake("valid-navigation.json");
        var result = await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.Accepted, result.Status);
        Assert.Equal(4, result.AcceptedActions.Count());
        var call = Assert.Single(await CallsAsync());
        Assert.Equal((AgentCallStatus.Accepted, 1, "fake"), (call.Status, call.CallNumber, call.Reasoner));
        Assert.Equal(_snapshot.Snapshot.NormalizedSha256, call.SnapshotHash);
        Assert.Equal((100L, 50L, 0.001m), (call.InputTokens!.Value, call.OutputTokens!.Value, call.CostUsd!.Value));
        Assert.Equal(4, call.Decisions.Count);

        // Request and response are artifacts; the stored request is exactly what the reasoner received.
        var storedRequest = await _h.Artifacts.ReadTextAsync(call.RequestSha256, CancellationToken.None);
        Assert.Equal(AgentJson.Serialize(reasoner.Requests.Single()), storedRequest);
        Assert.NotNull(await _h.Artifacts.ReadTextAsync(call.ResponseSha256!, CancellationToken.None));

        var candidates = await _h.Store.InTransactionAsync(uow => uow.Graph.ListCandidatesAsync(_stateId, CancellationToken.None), CancellationToken.None);
        Assert.Equal(4, candidates.Count);
        Assert.All(candidates, c => Assert.Equal((CandidateSource.Agent, CandidateStatus.Proposed), (c.Source, c.Status)));
    }

    [Fact]
    public async Task Skipped_candidates_are_recorded_as_skipped_by_policy()
    {
        await _h.Interpreter(Fake("valid-risky-and-inputs.json")).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        var candidates = await _h.Store.InTransactionAsync(uow => uow.Graph.ListCandidatesAsync(_stateId, CancellationToken.None), CancellationToken.None);
        Assert.Equal(6, candidates.Count);
        Assert.All(candidates, c => Assert.Equal(CandidateStatus.SkippedByPolicy, c.Status));
        Assert.Contains(candidates, c => c.RiskClass == RiskClass.Financial && c.CanonicalSelector == "id:app:id/transfer");
    }

    [Fact]
    public async Task Malformed_answer_gets_exactly_one_repair_that_lists_the_errors()
    {
        var reasoner = Fake("invalid-extra-field.json", "valid-navigation.json");
        var result = await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.Accepted, result.Status);
        Assert.Equal(2, reasoner.Requests.Count);
        Assert.Null(reasoner.Requests[0].Repair);
        Assert.Contains(reasoner.Requests[1].Repair!.PreviousErrors, e => e.Contains("additionalProperties", StringComparison.Ordinal) || e.Contains("not allowed", StringComparison.Ordinal));
        Assert.Equal(reasoner.Requests[0].RequestId, reasoner.Requests[1].RequestId);
        Assert.Equal([AgentCallStatus.SchemaInvalid, AgentCallStatus.Accepted], (await CallsAsync()).Select(c => c.Status));
    }

    [Fact]
    public async Task Repair_is_attempted_at_most_once()
    {
        var reasoner = Fake("invalid-truncated.txt");
        var result = await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.Rejected, result.Status);
        Assert.Equal(2, reasoner.Requests.Count);
        Assert.Equal([1, 2], (await CallsAsync()).Select(c => c.CallNumber));
        Assert.Empty(result.AcceptedActions);
        Assert.Empty(await _h.Store.InTransactionAsync(uow => uow.Graph.ListCandidatesAsync(_stateId, CancellationToken.None), CancellationToken.None));
    }

    [Theory]
    [InlineData("stale-snapshot-hash.json", AgentCallStatus.Stale)]
    [InlineData("invalid-yaml-single-line.json", AgentCallStatus.Rejected)]
    public async Task Stale_or_refused_answers_are_not_repaired(string fixture, AgentCallStatus status)
    {
        var reasoner = Fake(fixture, "valid-navigation.json");
        var result = await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.Rejected, result.Status);
        Assert.Single(reasoner.Requests);
        Assert.Equal(status, Assert.Single(await CallsAsync()).Status);
    }

    [Fact]
    public async Task Unavailable_reasoner_blocks_the_task_without_retry()
    {
        var reasoner = new DeterministicFakeReasoner(); // no script: Unavailable
        var result = await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.ReasonerUnavailable, result.Status);
        Assert.Equal(BlockedReasonCode.ReasonerUnavailable, result.BlockedReason);
        Assert.Single(reasoner.Requests);
        Assert.Equal(AgentCallStatus.ReasonerUnavailable, Assert.Single(await CallsAsync()).Status);
    }

    [Fact]
    public async Task Budget_stops_calls_before_they_are_made()
    {
        var settings = AgentSettings.Default with { MaxCalls = 1 };
        var budget = new AgentBudget(settings);
        var reasoner = Fake("invalid-truncated.txt");
        var result = await _h.Interpreter(reasoner, budget).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.BudgetExhausted, result.Status);
        Assert.Equal(BlockedReasonCode.ReasonerBudgetExhausted, result.BlockedReason);
        Assert.Single(reasoner.Requests);
        Assert.Equal([AgentCallStatus.SchemaInvalid, AgentCallStatus.BudgetExhausted], (await CallsAsync()).Select(c => c.Status));
        var totals = await _h.Store.InTransactionAsync(uow => uow.AgentCalls.GetUsageTotalsAsync(CancellationToken.None), CancellationToken.None);
        Assert.Equal(new AgentUsageTotals(1, 150, 0.001m, 0), totals);
    }

    [Fact]
    public async Task Token_and_cost_limits_apply_and_unknown_tokens_are_estimated()
    {
        var tight = new AgentBudget(AgentSettings.Default with { MaxTokens = 10 });
        Assert.Equal(AgentInterpretationStatus.BudgetExhausted, (await _h.Interpreter(Fake("valid-navigation.json"), tight).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None)).Status);

        var cost = new AgentBudget(AgentSettings.Default with { MaxCostUsd = 0.001m });
        cost.Record(new ReasonerUsage(1, 1, 1, 0.001m), 0);
        Assert.Contains("maxCostUsd", cost.Check(0), StringComparison.Ordinal);

        var unknown = new AgentBudget(AgentSettings.Default);
        unknown.Record(new ReasonerUsage(1, null, null, null), 1234);
        Assert.Equal(new AgentUsageTotals(1, 1234, 0m, 1), unknown.Used);
    }

    [Fact]
    public async Task Request_is_schema_valid_minimal_and_carries_graph_hints()
    {
        // A second, similar state and a known candidate on this state feed the hints.
        var (otherState, _) = await _h.CommitAsync(AgentHarness.ProfileScreen(UiTrees.Button("app:id/extra", "Extra")));
        await _h.Interpreter(Fake("valid-navigation.json")).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);
        var reasoner = Fake("valid-navigation.json");
        await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(_stateId, _snapshot, taskId: "task_analyze_2"), CancellationToken.None);

        var request = reasoner.Requests.Single();
        using var document = System.Text.Json.JsonDocument.Parse(AgentJson.Serialize(request));
        Assert.Empty(ContractSchemas.AgentExplorationRequest.Validate(document.RootElement));
        Assert.Contains(request.GraphHints.CandidateStates, s => s.StateId == otherState);
        Assert.Contains(request.GraphHints.CandidateStates, s => s.StateId == _stateId && s.Similarity == 1.0);
        Assert.Equal(4, request.GraphHints.KnownActionKeys.Count);
        Assert.Null(request.Snapshot.Screenshot);
        Assert.Equal(["profile.inputs.username"], request.Policy.InputRefs);
        Assert.Equal(["navigational", "safe"], request.Policy.AllowedRiskClasses.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("longPress", request.Policy.AllowedActionTypes);

        // Masked labels only: the balance's amount never reaches the reasoner.
        var json = AgentJson.Serialize(request);
        Assert.DoesNotContain("1 234,00", json, StringComparison.Ordinal);
        Assert.DoesNotContain("MAPCUE_DEMO_USER", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Request_values_are_redacted_before_they_are_stored_or_sent()
    {
        var withEmail = AgentHarness.ProfileScreen(UiTrees.Button("app:id/contact", "Write to jan.kowalski@example.com"));
        var (state, snapshot) = await _h.CommitAsync(withEmail);
        var reasoner = new DeterministicFakeReasoner().When(snapshot.Snapshot.NormalizedSha256, (req, _) => AgentJson.Serialize(DeterministicFakeReasoner.Answer(req, "Profile")));
        await _h.Interpreter(reasoner).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        Assert.DoesNotContain("jan.kowalski@example.com", AgentJson.Serialize(reasoner.Requests.Single()), StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_h.Directory, "blobs"), "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("jan.kowalski@example.com", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Same_inputs_give_the_same_request_and_call_ids()
    {
        var first = Fake("valid-navigation.json");
        await _h.Interpreter(first).InterpretAsync(AgentHarness.Input(_stateId, _snapshot), CancellationToken.None);
        await using var other = AgentHarness.Create();
        var (state, snapshot) = await other.CommitAsync();
        var second = new DeterministicFakeReasoner().When(snapshot.Snapshot.NormalizedSha256, (req, _) => AgentHarness.Golden("valid-navigation.json", req, snapshot));
        await other.Interpreter(second).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        Assert.Equal(AgentJson.Serialize(first.Requests.Single()), AgentJson.Serialize(second.Requests.Single()));
        var a = Assert.Single(await CallsAsync());
        var b = Assert.Single(await other.Store.InTransactionAsync(uow => uow.AgentCalls.ListAsync(null, CancellationToken.None), CancellationToken.None));
        Assert.Equal(a, b, new AgentCallComparer());
    }

    private sealed class AgentCallComparer : IEqualityComparer<AgentCall>
    {
        public bool Equals(AgentCall? x, AgentCall? y) =>
            x is not null && y is not null
            && (x.CallId, x.RequestId, x.StateId, x.RequestSha256, x.ResponseSha256, x.Status) == (y.CallId, y.RequestId, y.StateId, y.RequestSha256, y.ResponseSha256, y.Status)
            && x.Decisions.Select(d => (d.CandidateId, d.Verdict, d.ActionKey)).SequenceEqual(y.Decisions.Select(d => (d.CandidateId, d.Verdict, d.ActionKey)));

        public int GetHashCode(AgentCall obj) => obj.CallId.GetHashCode(StringComparison.Ordinal);
    }
}

public sealed class PreExecutionGuardTests
{
    [Fact]
    public async Task Accepted_action_is_stale_when_the_state_or_element_changed()
    {
        await using var h = AgentHarness.Create();
        var (_, snapshot) = await h.CommitAsync();
        var request = AgentHarness.Request(snapshot);
        var validation = AgentHarness.Validator().Validate(AgentHarness.Golden("valid-navigation.json", request, snapshot), request, snapshot.Normalized, AgentHarness.Policy());
        var tapBack = validation.Accepted.Single(c => c.CandidateId == "c1");
        var expected = snapshot.Normalized.IdentityHash;

        Assert.Null(PreExecutionGuard.Check(tapBack, expected, snapshot.Normalized));

        var other = (await h.Recorder.RecordAsync(AgentHarness.ProfileScreen(UiTrees.Button("app:id/new", "New")), AgentHarness.Basic, null, CancellationToken.None)).Normalized;
        Assert.Equal(BlockedReasonCode.ElementStale, PreExecutionGuard.Check(tapBack, expected, other));

        var withoutBack = snapshot.Normalized with { Elements = [.. snapshot.Normalized.Elements.Where(e => e.ResourceId != "app:id/back")] };
        Assert.Equal(BlockedReasonCode.ElementStale, PreExecutionGuard.Check(tapBack, expected, withoutBack));

        var skipped = validation.Candidates.Single(c => c.CandidateId == "c1") with { Action = null };
        Assert.Equal(BlockedReasonCode.ElementStale, PreExecutionGuard.Check(skipped, expected, snapshot.Normalized));
    }

    [Fact]
    public async Task Crashing_reasoner_is_recorded_as_unavailable()
    {
        await using var h = AgentHarness.Create();
        var (state, snapshot) = await h.CommitAsync();
        var crashing = new DeterministicFakeReasoner().When(snapshot.Snapshot.NormalizedSha256, (_, _) => throw new InvalidOperationException("boom with jan@example.com"));

        var result = await h.Interpreter(crashing).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.ReasonerUnavailable, result.Status);
        var call = Assert.Single(result.Calls);
        Assert.DoesNotContain(call.Errors, e => e.Contains("jan@", StringComparison.Ordinal));
    }
}

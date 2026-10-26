using MapCue.Application.Agent;
using MapCue.Application.Graph;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Adapters.Reasoning.Tests;

/// <summary>Golden answers (tests/Fixtures/agent-responses) through the full validation order of architecture §9.3.</summary>
public sealed class GoldenResponseTests : IAsyncLifetime
{
    private AgentHarness _harness = null!;
    private RecordedSnapshot _profile = null!;

    public async Task InitializeAsync()
    {
        _harness = AgentHarness.Create();
        var (_, snapshot) = await _harness.CommitAsync();
        _profile = snapshot;
    }

    public async Task DisposeAsync() => await _harness.DisposeAsync();

    private AgentValidation Validate(string fixture, DataIsolation isolation = DataIsolation.SharedReadOnly, AgentPolicy? policy = null)
    {
        policy ??= AgentHarness.Policy(isolation);
        var request = AgentHarness.Request(_profile, policy);
        return AgentHarness.Validator().Validate(AgentHarness.Golden(fixture, request, _profile), request, _profile.Normalized, policy);
    }

    private static Dictionary<string, CandidateVerdict> Verdicts(AgentValidation validation) =>
        validation.Candidates.ToDictionary(c => c.CandidateId, c => c.Verdict, StringComparer.Ordinal);

    [Fact]
    public void Valid_navigation_answer_is_accepted_and_mapped_to_typed_actions()
    {
        var validation = Validate("valid-navigation.json");

        Assert.Equal(AgentCallStatus.Accepted, validation.Status);
        Assert.Empty(validation.Errors);
        Assert.All(validation.Candidates, c => Assert.Equal(CandidateVerdict.Accepted, c.Verdict));
        var actions = validation.Accepted.ToDictionary(c => c.CandidateId, c => c.Action!);
        Assert.Equal((ActionType.Tap, "app:id/back", (string?)null), (actions["c1"].Type, actions["c1"].TargetResourceId, actions["c1"].TargetText));
        Assert.Equal((ActionType.Tap, (string?)null, "Help"), (actions["c2"].Type, actions["c2"].TargetResourceId, actions["c2"].TargetText));
        Assert.Equal((ActionType.Scroll, ScrollDirection.Down), (actions["c3"].Type, actions["c3"].Direction!.Value));
        Assert.Equal(ActionType.Back, actions["c4"].Type);
        Assert.All(actions.Values, a => Assert.Equal(AgentHarness.AppId, a.AppId));
        Assert.All(validation.Accepted, c => Assert.Equal(CandidateSource.Agent, c.Spec!.Source));
        Assert.Single(validation.Facts);
        Assert.Equal("Profile", validation.Interpretation!.ProposedScreenName);
    }

    [Fact]
    public void Risky_actions_are_skipped_on_shared_data_whatever_the_agent_claims()
    {
        var validation = Validate("valid-risky-and-inputs.json");

        Assert.Equal(AgentCallStatus.Accepted, validation.Status);
        Assert.All(validation.Candidates, c => Assert.Equal(CandidateVerdict.SkippedByPolicy, c.Verdict));
        var byId = validation.Candidates.ToDictionary(c => c.CandidateId);
        Assert.Equal(RiskClass.Destructive, byId["c1"].AgentRisk);
        Assert.Equal(RiskClass.AccountChange, byId["c1"].EffectiveRisk);
        Assert.Equal(RiskClass.Navigational, byId["c2"].AgentRisk);
        Assert.Equal(RiskClass.Financial, byId["c2"].EffectiveRisk);
        Assert.Equal(RiskClass.Communication, byId["c6"].EffectiveRisk);
        Assert.Empty(validation.Accepted);
    }

    [Fact]
    public void Destructive_class_requires_approval_on_disposable_data_and_is_accepted_only_when_approved()
    {
        var disposable = Validate("valid-risky-and-inputs.json", DataIsolation.Disposable);
        Assert.Equal(
            new Dictionary<string, CandidateVerdict>
            {
                ["c1"] = CandidateVerdict.RequiresApproval,
                ["c2"] = CandidateVerdict.RequiresApproval,
                ["c3"] = CandidateVerdict.Accepted,
                ["c4"] = CandidateVerdict.Accepted,
                ["c5"] = CandidateVerdict.Accepted,
                ["c6"] = CandidateVerdict.RequiresApproval,
            },
            Verdicts(disposable));
        var input = disposable.Accepted.Single(c => c.CandidateId == "c3").Action!;
        Assert.Equal("profile.inputs.username", input.InputValueRef);
        Assert.Equal("generated:name", disposable.Accepted.Single(c => c.CandidateId == "c4").Action!.InputValueRef);

        var approved = AgentHarness.Policy(DataIsolation.Disposable) with { ApprovedRiskClasses = new HashSet<RiskClass> { RiskClass.AccountChange } };
        var verdicts = Verdicts(Validate("valid-risky-and-inputs.json", policy: approved));
        Assert.Equal(CandidateVerdict.Accepted, verdicts["c1"]);
        Assert.Equal(CandidateVerdict.RequiresApproval, verdicts["c2"]);
    }

    [Theory]
    [InlineData("invalid-extra-field.json")]
    [InlineData("invalid-extra-top-level.json")]
    [InlineData("invalid-missing-field.json")]
    [InlineData("invalid-malformed-elementref.json")]
    [InlineData("invalid-literal-input-value.json")]
    [InlineData("invalid-literal-input-ref.json")]
    [InlineData("invalid-yaml-multi-line.json")]
    [InlineData("invalid-action-outside-schema.json")]
    [InlineData("invalid-duplicate-keys.json")]
    [InlineData("invalid-fenced.txt")]
    [InlineData("invalid-truncated.txt")]
    public void Malformed_answers_fail_the_schema_step(string fixture)
    {
        var validation = Validate(fixture);

        Assert.Equal(AgentCallStatus.SchemaInvalid, validation.Status);
        Assert.True(validation.Repairable);
        Assert.NotEmpty(validation.Errors);
        Assert.Empty(validation.Candidates);
        Assert.Null(validation.Response);
    }

    [Theory]
    [InlineData("stale-snapshot-hash.json", "snapshotHash")]
    [InlineData("stale-request-id.json", "requestId")]
    public void Stale_answers_are_rejected_and_not_repairable(string fixture, string field)
    {
        var validation = Validate(fixture);

        Assert.Equal(AgentCallStatus.Stale, validation.Status);
        Assert.False(validation.Repairable);
        Assert.Contains(validation.Errors, e => e.StartsWith(field, StringComparison.Ordinal));
        Assert.Empty(validation.Candidates);
    }

    [Theory]
    [InlineData("invalid-yaml-single-line.json", "yaml")]
    [InlineData("invalid-command-in-warning.json", "command")]
    [InlineData("invalid-url-and-path.json", "url")]
    public void Executable_looking_text_rejects_the_whole_answer(string fixture, string rule)
    {
        var validation = Validate(fixture);

        Assert.Equal(AgentCallStatus.Rejected, validation.Status);
        Assert.False(validation.Repairable);
        Assert.Contains(validation.Errors, e => e.Contains($"({rule})", StringComparison.Ordinal));
        Assert.Empty(validation.Accepted);
    }

    [Fact]
    public void Unknown_or_non_interactive_element_refs_reject_the_candidate()
    {
        var validation = Validate("invalid-unknown-elementref.json");

        Assert.Equal(AgentCallStatus.Accepted, validation.Status);
        var byId = validation.Candidates.ToDictionary(c => c.CandidateId);
        Assert.Equal(CandidateVerdict.Rejected, byId["c1"].Verdict);
        Assert.Contains("does not exist", byId["c1"].Reasons.Single(), StringComparison.Ordinal);
        Assert.Equal(CandidateVerdict.Rejected, byId["c2"].Verdict);
        Assert.Contains("not interactive", byId["c2"].Reasons.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Inputs_must_reference_this_profile_and_only_inputText_takes_one()
    {
        var validation = Validate("invalid-unknown-input-ref.json", DataIsolation.Disposable);

        Assert.All(validation.Candidates, c => Assert.Equal(CandidateVerdict.Rejected, c.Verdict));
        Assert.Contains("input", validation.Candidates[0].Reasons.Single(), StringComparison.Ordinal);
        Assert.Contains("must not have an input", validation.Candidates[1].Reasons.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Action_type_outside_the_allowlist_is_rejected_even_when_safe_on_disposable_data_with_every_approval()
    {
        var everything = AgentHarness.Policy(DataIsolation.Disposable) with { ApprovedRiskClasses = new HashSet<RiskClass>(Enum.GetValues<RiskClass>()) };
        var validation = Validate("invalid-action-outside-allowlist.json", policy: everything);

        var candidate = Assert.Single(validation.Candidates);
        Assert.Equal(CandidateVerdict.Rejected, candidate.Verdict);
        Assert.Null(candidate.Action);
        Assert.Contains("not in the allowlist", candidate.Reasons.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_golden_fixture_has_a_test()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-responses"))
            .Select(Path.GetFileName)
            .Where(f => f != "README.md")
            .Order(StringComparer.Ordinal);
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "GoldenResponseTests.cs.txt"));
        Assert.All(files, f => Assert.Contains($"\"{f}\"", source, StringComparison.Ordinal));
    }
}

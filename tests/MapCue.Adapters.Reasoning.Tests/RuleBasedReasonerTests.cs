using MapCue.Application.Agent;
using MapCue.Contracts.Agent;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Testing;

namespace MapCue.Adapters.Reasoning.Tests;

public sealed class RuleBasedReasonerTests
{
    [Fact]
    public async Task Answers_pass_validation_and_only_safe_actions_are_accepted_on_shared_data()
    {
        await using var h = AgentHarness.Create();
        var (state, snapshot) = await h.CommitAsync();

        var result = await h.Interpreter(new RuleBasedReasoner()).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        Assert.Equal(AgentInterpretationStatus.Accepted, result.Status);
        var call = Assert.Single(result.Calls);
        Assert.Equal("rule-based", call.Reasoner);
        var accepted = result.AcceptedActions.Select(c => c.Spec!.CanonicalSelector).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([string.Empty, "dir:Down", "id:app:id/back", "text:Help", "text:Row 0", "text:Row 1"], accepted);
        var skipped = result.Validation!.Candidates.Where(c => c.Verdict == CandidateVerdict.SkippedByPolicy).Select(c => c.Spec!.CanonicalSelector).Order(StringComparer.Ordinal);
        Assert.Equal(["id:app:id/delete_account", "id:app:id/save", "id:app:id/share", "id:app:id/transfer", "id:app:id/username"], skipped);
        Assert.DoesNotContain(result.Validation.Candidates, c => c.Verdict == CandidateVerdict.Rejected);
    }

    [Fact]
    public async Task Is_deterministic_and_respects_limits_and_the_action_allowlist()
    {
        await using var h = AgentHarness.Create();
        var (_, snapshot) = await h.CommitAsync();
        var policy = AgentHarness.Policy() with { MaxCandidates = 3, AllowedActionTypes = new HashSet<ActionType> { ActionType.Tap } };
        var request = AgentHarness.Request(snapshot, policy);

        var first = await new RuleBasedReasoner().InterpretAsync(request, CancellationToken.None);
        var second = await new RuleBasedReasoner().InterpretAsync(request, CancellationToken.None);

        Assert.Equal(first.RawResponse, second.RawResponse);
        var answer = AgentJson.Deserialize<AgentExplorationResponse>(first.RawResponse!);
        Assert.Equal(3, answer.ActionCandidates.Count);
        Assert.All(answer.ActionCandidates, c => Assert.Equal("tap", c.ActionType));
    }

    [Fact]
    public async Task Proposes_input_only_into_fields_named_like_a_profile_input()
    {
        await using var h = AgentHarness.Create();
        var (state, snapshot) = await h.CommitAsync();
        var result = await h.Interpreter(new RuleBasedReasoner()).InterpretAsync(AgentHarness.Input(state, snapshot, AgentHarness.Policy(DataIsolation.Disposable)), CancellationToken.None);

        var inputs = result.Validation!.Candidates.Where(c => c.Spec?.Type == ActionType.InputText).ToList();
        var input = Assert.Single(inputs);
        Assert.Equal(CandidateVerdict.Accepted, input.Verdict);
        Assert.Equal("profile.inputs.username", input.Action!.InputValueRef);
    }

    [Fact]
    public async Task Polish_labels_are_classified_too()
    {
        await using var h = AgentHarness.Create();
        // Neutral resource ids: only the Polish labels can raise the risk.
        var screen = AgentHarness.ProfileScreen(UiTrees.Button("app:id/btn_a", "Usuń konto"), UiTrees.Button("app:id/btn_b", "Wykonaj przelew"));
        var (state, snapshot) = await h.CommitAsync(screen);

        var result = await h.Interpreter(new RuleBasedReasoner()).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        var byText = result.Validation!.Candidates.Where(c => c.Spec is not null).ToDictionary(c => c.Spec!.CanonicalSelector);
        Assert.Equal((CandidateVerdict.SkippedByPolicy, RiskClass.AccountChange), (byText["id:app:id/btn_a"].Verdict, byText["id:app:id/btn_a"].EffectiveRisk!.Value));
        Assert.Equal((CandidateVerdict.SkippedByPolicy, RiskClass.Financial), (byText["id:app:id/btn_b"].Verdict, byText["id:app:id/btn_b"].EffectiveRisk!.Value));
    }
}

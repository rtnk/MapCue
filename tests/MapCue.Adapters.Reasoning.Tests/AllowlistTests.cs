using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Contracts.Agent;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Adapters.Reasoning.Tests;

/// <summary>DoD STEP-04: the agent cannot execute an action outside the allowlist, whatever it claims.</summary>
public sealed class AllowlistTests : IAsyncLifetime
{
    private static readonly string[] _riskClasses = ["safe", "navigational", "stateChanging", "destructive", "financial", "communication", "accountChange", "external", "unknown"];
    private static readonly string[] _schemaActionTypes = ["tap", "back", "scroll", "swipe", "inputText", "hideKeyboard", "longPress"];

    private AgentHarness _h = null!;
    private Application.Graph.RecordedSnapshot _snapshot = null!;

    public async Task InitializeAsync()
    {
        _h = AgentHarness.Create();
        (_, _snapshot) = await _h.CommitAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static IEnumerable<AgentPolicy> Policies(AgentPolicy basis)
    {
        foreach (var isolation in Enum.GetValues<DataIsolation>())
        {
            yield return basis with { DataIsolation = isolation };
            yield return basis with { DataIsolation = isolation, ApprovedRiskClasses = new HashSet<RiskClass>(Enum.GetValues<RiskClass>()) };
        }
    }

    /// <summary>Every candidate shape the schema admits, on every element, with every risk claim.</summary>
    private List<AgentActionCandidate> AllCandidates()
    {
        var refs = _snapshot.Normalized.Elements.Select(e => (string?)e.ElementRef).Append(null).ToList();
        var candidates = new List<AgentActionCandidate>();
        foreach (var type in _schemaActionTypes)
        {
            foreach (var risk in _riskClasses)
            {
                foreach (var elementRef in refs)
                {
                    var direction = type is "scroll" or "swipe" ? "down" : null;
                    var input = type == "inputText" ? new AgentInput("profile.inputs.username") : null;
                    candidates.Add(new AgentActionCandidate("c" + (candidates.Count + 1), elementRef, type, direction, input, "candidate", risk, null, 1));
                }
            }
        }

        return candidates;
    }

    private AgentValidation Validate(AgentPolicy policy, IReadOnlyList<AgentActionCandidate> candidates, AllowlistSettings? allowlist = null)
    {
        var request = AgentHarness.Request(_snapshot, policy) with { Limits = new AgentLimits(50) };
        var answer = AgentJson.Serialize(DeterministicFakeReasoner.Answer(request, null, [.. candidates]));
        var validator = new AgentResponseValidator(new RiskClassifier(), new PolicyGate(allowlist ?? AgentHarness.Allowlist));
        return validator.Validate(answer, request, _snapshot.Normalized, policy);
    }

    [Fact]
    public void Action_types_outside_the_policy_are_never_accepted()
    {
        var all = AllCandidates();
        foreach (var allowedSet in new[] { new HashSet<ActionType> { ActionType.Tap }, new HashSet<ActionType> { ActionType.Back, ActionType.Scroll }, new HashSet<ActionType>() })
        {
            foreach (var policy in Policies(AgentHarness.Policy() with { AllowedActionTypes = allowedSet }))
            {
                foreach (var chunk in all.Chunk(50))
                {
                    var renumbered = chunk.Select((c, i) => c with { CandidateId = "c" + (i + 1) }).ToList();
                    var validation = Validate(policy, renumbered);
                    Assert.Equal(AgentCallStatus.Accepted, validation.Status);
                    Assert.All(validation.Accepted, c => Assert.Contains(c.Action!.Type, allowedSet));
                    Assert.All(validation.Candidates.Where(c => c.Action is not null), c => Assert.Equal(CandidateVerdict.Accepted, c.Verdict));
                }
            }
        }
    }

    [Fact]
    public void Default_policy_never_accepts_long_press_or_high_risk_on_shared_data()
    {
        foreach (var chunk in AllCandidates().Chunk(50))
        {
            var validation = Validate(AgentHarness.Policy(), [.. chunk.Select((c, i) => c with { CandidateId = "c" + (i + 1) })]);
            Assert.All(validation.Accepted, c =>
            {
                Assert.NotEqual(ActionType.LongPress, c.Action!.Type);
                Assert.Contains(c.EffectiveRisk!.Value, new[] { RiskClass.Safe, RiskClass.Navigational });
                Assert.Equal(AgentHarness.AppId, c.Action.AppId);
            });
        }
    }

    [Fact]
    public void App_outside_the_allowlist_rejects_everything()
    {
        var otherApp = new AllowlistSettings(["com.other.app"], ["test"], [], []);
        foreach (var chunk in AllCandidates().Chunk(50))
        {
            var validation = Validate(AgentHarness.Policy(DataIsolation.Disposable), [.. chunk.Select((c, i) => c with { CandidateId = "c" + (i + 1) })], otherApp);
            Assert.Empty(validation.Accepted);
        }
    }

    [Fact]
    public void Lifecycle_and_link_actions_are_not_even_in_the_schema()
    {
        foreach (var type in new[] { "launchApp", "openLink", "point", "setPermissions", "waitForAnimationToEnd", "runFlow", "TAP" })
        {
            var request = AgentHarness.Request(_snapshot);
            var answer = AgentJson.Serialize(DeterministicFakeReasoner.Answer(request, null, new AgentActionCandidate("c1", null, type, null, null, "x", "safe", null, 1)));
            Assert.Equal(AgentCallStatus.SchemaInvalid, AgentHarness.Validator().Validate(answer, request, _snapshot.Normalized, AgentHarness.Policy()).Status);
        }
    }
}

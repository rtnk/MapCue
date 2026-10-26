using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Tests.Agent;

public class PolicyGateTests
{
    private static readonly PolicyGate _gate = new(new AllowlistSettings(["com.example.demo"], ["test"], [], []));

    private static AgentPolicy Policy(DataIsolation isolation, params RiskClass[] approved) =>
        new("com.example.demo", isolation, new HashSet<ActionType>(ActionTypes.DefaultAllowed.Where(AgentPolicy.ProposableActionTypes.Contains)), new HashSet<RiskClass>(approved), [], 20);

    public static TheoryData<RiskClass, DataIsolation, PolicyVerdict> Matrix() => new()
    {
        { RiskClass.Safe, DataIsolation.SharedReadOnly, PolicyVerdict.Allowed },
        { RiskClass.Navigational, DataIsolation.SharedReadOnly, PolicyVerdict.Allowed },
        { RiskClass.StateChanging, DataIsolation.SharedReadOnly, PolicyVerdict.SkippedByPolicy },
        { RiskClass.Unknown, DataIsolation.SharedReadOnly, PolicyVerdict.SkippedByPolicy },
        { RiskClass.Destructive, DataIsolation.SharedReadOnly, PolicyVerdict.SkippedByPolicy },
        { RiskClass.StateChanging, DataIsolation.Disposable, PolicyVerdict.Allowed },
        { RiskClass.Unknown, DataIsolation.Disposable, PolicyVerdict.Allowed },
        { RiskClass.Destructive, DataIsolation.Disposable, PolicyVerdict.RequiresApproval },
        { RiskClass.Financial, DataIsolation.Disposable, PolicyVerdict.RequiresApproval },
        { RiskClass.Communication, DataIsolation.Disposable, PolicyVerdict.RequiresApproval },
        { RiskClass.AccountChange, DataIsolation.Disposable, PolicyVerdict.RequiresApproval },
        { RiskClass.External, DataIsolation.Disposable, PolicyVerdict.RequiresApproval },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Risk_is_judged_against_data_isolation(RiskClass risk, DataIsolation isolation, PolicyVerdict expected)
    {
        Assert.Equal(expected, _gate.Evaluate(ActionType.Tap, risk, Policy(isolation)).Verdict);
    }

    [Fact]
    public void Approvals_count_only_with_disposable_data()
    {
        Assert.Equal(PolicyVerdict.Allowed, _gate.Evaluate(ActionType.Tap, RiskClass.Destructive, Policy(DataIsolation.Disposable, RiskClass.Destructive)).Verdict);
        Assert.Equal(PolicyVerdict.SkippedByPolicy, _gate.Evaluate(ActionType.Tap, RiskClass.Destructive, Policy(DataIsolation.SharedReadOnly, RiskClass.Destructive)).Verdict);
        Assert.Equal(PolicyVerdict.RequiresApproval, _gate.Evaluate(ActionType.Tap, RiskClass.Financial, Policy(DataIsolation.Disposable, RiskClass.Destructive)).Verdict);
    }

    [Fact]
    public void Action_types_and_apps_outside_the_allowlists_are_always_rejected()
    {
        var everything = Policy(DataIsolation.Disposable, Enum.GetValues<RiskClass>());
        foreach (var type in Enum.GetValues<ActionType>().Where(t => !everything.AllowedActionTypes.Contains(t)))
        {
            foreach (var risk in Enum.GetValues<RiskClass>())
            {
                Assert.Equal(PolicyVerdict.Rejected, _gate.Evaluate(type, risk, everything).Verdict);
            }
        }

        Assert.Equal(PolicyVerdict.Rejected, _gate.Evaluate(ActionType.Tap, RiskClass.Safe, everything with { AppId = "com.other" }).Verdict);
        var withLaunch = everything with { AllowedActionTypes = new HashSet<ActionType>(Enum.GetValues<ActionType>()) };
        Assert.Equal(PolicyVerdict.Rejected, _gate.Evaluate(ActionType.LaunchApp, RiskClass.Safe, withLaunch).Verdict);
        Assert.Equal(PolicyVerdict.Rejected, _gate.Evaluate(ActionType.OpenLink, RiskClass.Safe, withLaunch).Verdict);
    }

    [Fact]
    public void Default_profile_policy_allows_default_types_and_only_safe_risks()
    {
        var profile = new ProfileSettings("basic", Platform.Android, "com.example.demo", "test", DataIsolation.SharedReadOnly, [new ProfileInput("username", null, "email")], []);
        var policy = AgentPolicy.For(profile);

        Assert.Equal([ActionType.Tap, ActionType.Back, ActionType.Scroll, ActionType.Swipe, ActionType.InputText, ActionType.HideKeyboard], policy.AllowedActionTypes.Order());
        Assert.Equal([RiskClass.Safe, RiskClass.Navigational], policy.AllowedRiskClasses);
        Assert.Equal(["username"], policy.InputNames);
    }
}

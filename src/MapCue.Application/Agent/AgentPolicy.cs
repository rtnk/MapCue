using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Agent;

/// <summary>
/// The policy one profile is explored under (architecture §10.1). Deny by default: only the listed action types and risk
/// classes are allowed; <c>stateChanging</c>/<c>unknown</c> only with <see cref="DataIsolation.Disposable"/> data; the high-risk
/// classes need an explicit per-category approval <em>and</em> disposable data (R-06). Approvals come from the operator
/// (resume with approval, STEP-09); profiles cannot pre-approve them in STEP-04.
/// </summary>
public sealed record AgentPolicy(
    string AppId,
    DataIsolation DataIsolation,
    IReadOnlySet<ActionType> AllowedActionTypes,
    IReadOnlySet<RiskClass> ApprovedRiskClasses,
    IReadOnlyList<string> InputNames,
    int MaxCandidates = AgentPolicy.DefaultMaxCandidates)
{
    public const int DefaultMaxCandidates = 20;

    /// <summary>Action types a reasoner may propose at all. Lifecycle actions (launch, wait) belong to the engine.</summary>
    public static IReadOnlySet<ActionType> ProposableActionTypes { get; } = new HashSet<ActionType>
    {
        ActionType.Tap,
        ActionType.Back,
        ActionType.Scroll,
        ActionType.Swipe,
        ActionType.InputText,
        ActionType.HideKeyboard,
        ActionType.LongPress,
    };

    /// <summary>Deterministic generators an <c>inputText</c> may use instead of a profile input.</summary>
    public static IReadOnlyList<string> Generators { get; } = ["email", "name", "text", "number"];

    /// <summary>High-risk classes: never allowed without an operator approval of that category.</summary>
    public static IReadOnlySet<RiskClass> HighRisk { get; } = new HashSet<RiskClass>
    {
        RiskClass.Destructive,
        RiskClass.Financial,
        RiskClass.Communication,
        RiskClass.AccountChange,
        RiskClass.External,
    };

    /// <summary>Default policy of a profile: default action types (architecture §10.1), no approvals.</summary>
    public static AgentPolicy For(ProfileSettings profile, int maxCandidates = DefaultMaxCandidates)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new AgentPolicy(
            profile.AppId,
            profile.DataIsolation,
            new HashSet<ActionType>(ActionTypes.DefaultAllowed.Where(ProposableActionTypes.Contains)),
            new HashSet<RiskClass>(),
            [.. profile.Inputs.Select(i => i.Name).Order(StringComparer.Ordinal)],
            maxCandidates);
    }

    /// <summary>Risk classes this policy allows without asking anyone.</summary>
    public IReadOnlyList<RiskClass> AllowedRiskClasses =>
        [.. Enum.GetValues<RiskClass>().Where(r => PolicyGate.RiskVerdict(r, this) == PolicyVerdict.Allowed)];
}

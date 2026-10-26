using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Agent;

public enum PolicyVerdict
{
    Allowed,

    /// <summary>Not executed because of the profile's policy (architecture §8: <c>skippedByPolicy</c>).</summary>
    SkippedByPolicy,

    /// <summary>High-risk action on disposable data: the operator may approve the category (<c>PolicyRequiresApproval</c>).</summary>
    RequiresApproval,

    /// <summary>Never allowed (app or action type outside the allowlist).</summary>
    Rejected,
}

public sealed record PolicyDecision(PolicyVerdict Verdict, string Reason);

/// <summary>
/// The last gate before an action becomes a <c>ValidatedDeviceAction</c> (architecture §10.1). Order: app allowlist, action type
/// allowlist (outside it: always rejected, whatever the risk), then the risk class against the profile's data isolation and
/// approvals. <see cref="RiskClass.Unknown"/> is treated as <see cref="RiskClass.StateChanging"/>.
/// </summary>
public sealed class PolicyGate(AllowlistSettings allowlist)
{
    public PolicyDecision Evaluate(ActionType type, RiskClass effectiveRisk, AgentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!allowlist.AppIds.Contains(policy.AppId, StringComparer.Ordinal))
        {
            return new(PolicyVerdict.Rejected, $"app '{policy.AppId}' is not in the allowlist");
        }

        if (!AgentPolicy.ProposableActionTypes.Contains(type) || !policy.AllowedActionTypes.Contains(type))
        {
            return new(PolicyVerdict.Rejected, $"action type {type} is not in the allowlist");
        }

        var verdict = RiskVerdict(effectiveRisk, policy);
        return new(verdict, verdict switch
        {
            PolicyVerdict.Allowed => $"risk {effectiveRisk} allowed",
            PolicyVerdict.RequiresApproval => $"risk {effectiveRisk} requires an operator approval of that category",
            _ => $"risk {effectiveRisk} not allowed with data isolation {policy.DataIsolation}",
        });
    }

    internal static PolicyVerdict RiskVerdict(RiskClass risk, AgentPolicy policy) => risk switch
    {
        RiskClass.Safe or RiskClass.Navigational => PolicyVerdict.Allowed,
        RiskClass.StateChanging or RiskClass.Unknown => policy.DataIsolation == DataIsolation.Disposable ? PolicyVerdict.Allowed : PolicyVerdict.SkippedByPolicy,
        _ when policy.DataIsolation != DataIsolation.Disposable => PolicyVerdict.SkippedByPolicy,
        _ when policy.ApprovedRiskClasses.Contains(risk) => PolicyVerdict.Allowed,
        _ => PolicyVerdict.RequiresApproval,
    };
}

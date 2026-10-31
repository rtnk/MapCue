using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Profiles;

/// <summary>
/// Profile switch policy (STEP-07): a run explores exactly one profile, so with <c>policy.profileSwitch: deny</c> (the default) the
/// <c>switchElements</c> of every other profile are account changes in its context. Under shared-readonly data they are skipped by policy,
/// under disposable data they need an operator approval; they are never executed silently. The rules only raise risk (like any operator
/// rule) and come before the operator's own rules, so the operator can only raise them further.
/// </summary>
public static class ProfileSwitchRules
{
    public const string RulePrefix = "profile-switch-";

    /// <summary>Risk rules of a run of <paramref name="profile"/>: the profile switch rules, then <c>policy.riskRules</c>.</summary>
    public static IReadOnlyList<OperatorRiskRule> For(MapCueSettings settings, ProfileSettings profile)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(profile);
        if (settings.Policy.ProfileSwitch == ProfileSwitchPolicy.Allow)
        {
            return settings.Policy.RiskRules;
        }

        return
        [
            .. settings.Profiles
                .Where(p => p.ProfileId != profile.ProfileId && p.AppId == profile.AppId)
                .OrderBy(p => p.ProfileId, StringComparer.Ordinal)
                .SelectMany(p => p.SwitchElements.Select(pattern => new OperatorRiskRule(RulePrefix + p.ProfileId, RiskClass.AccountChange, null, pattern, null))),
            .. settings.Policy.RiskRules,
        ];
    }
}

using MapCue.Application.Configuration;
using MapCue.Application.Profiles;
using MapCue.Domain;

namespace MapCue.Application.Tests.Profiles;

/// <summary>Profile settings of STEP-07: scenario, locale, switch elements, <c>policy.profileSwitch</c> and the profile variant delta.</summary>
public class ProfileConfigTests
{
    private static ProfileDocument Profile(string id, params string[] switchElements) => new()
    {
        ProfileId = id,
        Platform = "android",
        AppId = "com.example.demo",
        Environment = "test",
        SwitchElements = [.. switchElements],
    };

    private static MapCueConfigDocument Document(params ProfileDocument[] profiles) => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
        Profiles = [.. profiles],
    };

    [Fact]
    public void Profiles_default_to_the_default_scenario_and_locale_and_deny_profile_switches()
    {
        var settings = MapCueConfigValidator.Validate(Document(Profile("basic"))).Settings!;

        var basic = Assert.Single(settings.Profiles);
        Assert.Equal(("default", "und"), (basic.ScenarioId, basic.Locale));
        Assert.Empty(basic.SwitchElements);
        Assert.Equal(ProfileSwitchPolicy.Deny, settings.Policy.ProfileSwitch);
        Assert.Equal(2, settings.Identity.ProfileVariantMaxDelta);
    }

    [Fact]
    public void Scenario_locale_switch_elements_and_policy_are_read()
    {
        var investor = Profile("investor", ":id/profile\\.investor$");
        investor.Scenario = "empty-accounts";
        investor.Locale = "pl-PL";
        var document = Document(investor);
        document.Policy = new PolicyDocument { ProfileSwitch = "allow" };
        document.Identity = new IdentityDocument { ProfileVariantMaxDelta = 0 };

        var settings = MapCueConfigValidator.Validate(document).Settings!;

        var p = Assert.Single(settings.Profiles);
        Assert.Equal(("empty-accounts", "pl-PL"), (p.ScenarioId, p.Locale));
        Assert.Matches(Assert.Single(p.SwitchElements), "com.mapcue.demoapp:id/profile.investor");
        Assert.Equal(ProfileSwitchPolicy.Allow, settings.Policy.ProfileSwitch);
        Assert.Equal(0, settings.Identity.ProfileVariantMaxDelta);
    }

    [Theory]
    [InlineData("scenario", "has space", "profiles[0].scenario")]
    [InlineData("locale", "polish", "profiles[0].locale")]
    [InlineData("switch", "(unclosed", "profiles[0].switchElements[0]")]
    [InlineData("switch", "", "profiles[0].switchElements[0]")]
    [InlineData("policy", "maybe", "policy.profileSwitch")]
    [InlineData("delta", "9", "identity.profileVariantMaxDelta")]
    public void Invalid_profile_settings_are_rejected(string field, string value, string path)
    {
        var profile = Profile("basic");
        var document = Document(profile);
        switch (field)
        {
            case "scenario":
                profile.Scenario = value;
                break;
            case "locale":
                profile.Locale = value;
                break;
            case "switch":
                profile.SwitchElements = [value];
                break;
            case "policy":
                document.Policy = new PolicyDocument { ProfileSwitch = value };
                break;
            default:
                document.Identity = new IdentityDocument { ProfileVariantMaxDelta = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) };
                break;
        }

        var result = MapCueConfigValidator.Validate(document);

        Assert.False(result.IsValid);
        Assert.Equal(path, Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void Another_profiles_switch_elements_are_account_changes_by_default()
    {
        var settings = MapCueConfigValidator.Validate(Document(Profile("basic", ":id/profile\\.basic$"), Profile("investor", ":id/profile\\.investor$"))).Settings!;
        var basic = settings.Profiles[0];

        var rule = Assert.Single(ProfileSwitchRules.For(settings, basic));
        Assert.Equal(("profile-switch-investor", RiskClass.AccountChange), (rule.Name, rule.Risk));
        Assert.True(rule.Matches("com.example.demo:id/profile.investor", []));
        Assert.False(rule.Matches("com.example.demo:id/profile.basic", []));

        var allowed = settings with { Policy = settings.Policy with { ProfileSwitch = ProfileSwitchPolicy.Allow } };
        Assert.Empty(ProfileSwitchRules.For(allowed, basic));
    }

    [Fact]
    public void Operator_rules_follow_the_profile_switch_rules()
    {
        var document = Document(Profile("basic"), Profile("investor", ":id/profile\\.investor$"));
        document.Policy = new PolicyDocument { RiskRules = [new RiskRuleDocument { Name = "delete", RiskClass = "destructive", ResourceIdPattern = "delete" }] };
        var settings = MapCueConfigValidator.Validate(document).Settings!;

        Assert.Equal(["profile-switch-investor", "delete"], ProfileSwitchRules.For(settings, settings.Profiles[0]).Select(r => r.Name));
        Assert.Equal(["delete"], ProfileSwitchRules.For(settings, settings.Profiles[1]).Select(r => r.Name));
    }

    [Theory]
    [InlineData("id:com.mapcue.demoapp:id/tab.investments", "investments", true)]
    [InlineData("id:com.mapcue.demoapp:id/investments.buy", "Investments", true)]
    [InlineData("id:com.mapcue.demoapp:id/tab.investments", "invest", false)]
    [InlineData("id:com.mapcue.demoapp:id/tab.home", "demoapp", false)]
    [InlineData("rl:button|Investments", "investments", true)]
    public void Guard_names_match_whole_segments_of_the_element_name(string elementKey, string capability, bool expected) =>
        Assert.Equal(expected, ProfileAnalysis.NameMatches(elementKey, capability));
}

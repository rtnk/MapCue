using MapCue.Application.Configuration;

namespace MapCue.Application.Tests;

/// <summary>Configuration keys added for <c>mapcue explore</c> (STEP-05): reasoner choice, profile setup flow, screen hints.</summary>
public class ExploreConfigTests
{
    private static MapCueConfigDocument With(AgentDocument? agent = null, IdentityDocument? identity = null, string? setupFlow = null) => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
        Agent = agent,
        Identity = identity,
        Profiles = [new ProfileDocument { ProfileId = "basic", Platform = "android", AppId = "com.example.demo", Environment = "test", SetupFlow = setupFlow }],
    };

    [Fact]
    public void Defaults_are_the_rule_based_reasoner_no_setup_flow_and_no_hints()
    {
        var settings = MapCueConfigValidator.Validate(With()).Settings!;

        Assert.Equal(ReasonerKind.RuleBased, settings.Agent.Reasoner);
        Assert.Null(settings.Profiles[0].SetupFlow);
        Assert.Empty(settings.Identity.ScreenHints);
    }

    [Fact]
    public void Explore_keys_are_applied()
    {
        var result = MapCueConfigValidator.Validate(With(new AgentDocument { Reasoner = "cli" }, new IdentityDocument { ScreenHints = [@":id/screen\.[a-z]+$"] }, ".tapcue/setup-basic.yaml"));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var settings = result.Settings!;
        Assert.Equal(ReasonerKind.Cli, settings.Agent.Reasoner);
        Assert.Equal(".tapcue/setup-basic.yaml", settings.Profiles[0].SetupFlow);
        var hint = Assert.Single(settings.Identity.ScreenHints);
        Assert.Matches(hint, "com.example.demo:id/screen.home");
        Assert.Equal(TimeSpan.FromMilliseconds(100), hint.MatchTimeout);
    }

    [Theory]
    [InlineData("llm")]
    [InlineData("RuleBased")]
    public void Unknown_reasoner_is_rejected(string reasoner)
    {
        var error = Assert.Single(MapCueConfigValidator.Validate(With(new AgentDocument { Reasoner = reasoner })).Errors);
        Assert.Equal((ConfigErrorCodes.InvalidAgentSetting, "agent.reasoner"), (error.Code, error.Path));
    }

    [Theory]
    [InlineData("setup.json")]
    [InlineData("")]
    [InlineData("flow\n.yaml")]
    public void Setup_flow_must_be_a_yaml_file_path(string path)
    {
        var error = Assert.Single(MapCueConfigValidator.Validate(With(setupFlow: path)).Errors);
        Assert.Equal((ConfigErrorCodes.InvalidProfile, "profiles[0].setupFlow"), (error.Code, error.Path));
    }

    [Theory]
    [InlineData("(unclosed")]
    [InlineData("")]
    public void Invalid_screen_hint_is_rejected(string pattern)
    {
        var error = Assert.Single(MapCueConfigValidator.Validate(With(identity: new IdentityDocument { ScreenHints = [pattern] })).Errors);
        Assert.Equal((ConfigErrorCodes.InvalidScreenHint, "identity.screenHints[0]"), (error.Code, error.Path));
    }
}

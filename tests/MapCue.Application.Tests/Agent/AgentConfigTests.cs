using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Tests.Agent;

public class AgentConfigTests
{
    private static MapCueConfigDocument With(AgentDocument? agent = null, PolicyDocument? policy = null) => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
        Agent = agent,
        Policy = policy,
    };

    [Fact]
    public void Cli_agent_settings_are_validated_and_applied()
    {
        var result = MapCueConfigValidator.Validate(With(new AgentDocument
        {
            ContainerImage = "ghcr.io/example/mapcue-agent@sha256:" + new string('a', 64),
            ContainerEngine = "podman",
            ContainerNetwork = "llm-egress",
            Model = "claude-sonnet-5-5",
            Timeout = "90s",
            ApiKey = "secret://env/ANTHROPIC_API_KEY",
        }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var agent = result.Settings!.Agent;
        Assert.Equal((AgentRuntime.Container, "claude", "podman", "llm-egress"), (agent.Runtime, agent.Provider, agent.ContainerEngine, agent.ContainerNetwork));
        Assert.Equal(TimeSpan.FromSeconds(90), agent.Timeout);
        Assert.Equal("ANTHROPIC_API_KEY", agent.ApiKey!.Name);
    }

    [Theory]
    [InlineData("provider", "codex", ConfigErrorCodes.InvalidAgentSetting)]
    [InlineData("containerImage", "Bad Image", ConfigErrorCodes.InvalidAgentSetting)]
    [InlineData("containerEngine", "docker --privileged", ConfigErrorCodes.InvalidAgentSetting)]
    [InlineData("apiKey", "sk-ant-literal", ConfigErrorCodes.InvalidSecretRef)]
    [InlineData("timeout", "1s", ConfigErrorCodes.BudgetOutOfRange)]
    public void Invalid_cli_agent_settings_are_rejected(string field, string value, string code)
    {
        var doc = new AgentDocument();
        typeof(AgentDocument).GetProperty(char.ToUpperInvariant(field[0]) + field[1..])!.SetValue(doc, value);

        var result = MapCueConfigValidator.Validate(With(doc));

        var error = Assert.Single(result.Errors);
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Screenshots_cannot_be_sent_yet()
    {
        var result = MapCueConfigValidator.Validate(With(new AgentDocument { SendScreenshots = true }));

        Assert.Equal(ConfigErrorCodes.InvalidAgentSetting, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void Operator_risk_rules_are_parsed()
    {
        var result = MapCueConfigValidator.Validate(With(policy: new PolicyDocument
        {
            RiskRules =
            [
                new RiskRuleDocument { Name = "blik", RiskClass = "financial", ResourceId = "app:id/blik" },
                new RiskRuleDocument { Name = "admin", RiskClass = "destructive", ResourceIdPattern = "^app:id/admin_" },
            ],
        }));

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var rules = result.Settings!.Policy.RiskRules;
        Assert.Equal([("blik", RiskClass.Financial), ("admin", RiskClass.Destructive)], rules.Select(r => (r.Name, r.Risk)));
        Assert.True(rules[1].Matches("app:id/admin_reset", []));
    }

    [Theory]
    [InlineData(null, "financial", "app:id/x", null)]
    [InlineData("r", "safe", "app:id/x", null)]
    [InlineData("r", "bogus", "app:id/x", null)]
    [InlineData("r", "financial", null, null)]
    [InlineData("r", "financial", null, "([")]
    public void Invalid_risk_rules_are_rejected(string? name, string risk, string? resourceId, string? pattern)
    {
        var result = MapCueConfigValidator.Validate(With(policy: new PolicyDocument
        {
            RiskRules = [new RiskRuleDocument { Name = name, RiskClass = risk, ResourceId = resourceId, LabelPattern = pattern }],
        }));

        Assert.Equal(ConfigErrorCodes.InvalidRiskRule, Assert.Single(result.Errors).Code);
    }
}

using MapCue.Domain.Secrets;

namespace MapCue.Domain.Tests;

public class SecretRefTests
{
    [Theory]
    [InlineData("secret://env/MAPCUE_DEMO_PASSWORD", "MAPCUE_DEMO_PASSWORD")]
    [InlineData("secret://env/_X1", "_X1")]
    public void Parses_env_references(string uri, string name)
    {
        Assert.True(SecretRef.TryParse(uri, out var r));
        Assert.Equal(name, r.Name);
        Assert.Equal(uri, r.Uri);
        Assert.Equal(uri, r.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hunter2")]
    [InlineData("secret://env/")]
    [InlineData("secret://env/lower_case")]
    [InlineData("secret://env/1STARTS_WITH_DIGIT")]
    [InlineData("secret://env/HAS-DASH")]
    [InlineData("secret://vault/kv/demo")]
    [InlineData("SECRET://env/NAME")]
    [InlineData("secret://env/NAME/extra")]
    public void Rejects_everything_else(string? value)
    {
        Assert.False(SecretRef.TryParse(value, out _));
    }

    [Fact]
    public void Parse_error_does_not_echo_the_value()
    {
        var ex = Assert.Throws<FormatException>(() => SecretRef.Parse("p@ssw0rd-literal"));
        Assert.DoesNotContain("p@ssw0rd-literal", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SecretValue_never_renders_its_value()
    {
        var value = new SecretValue(SecretRef.Parse("secret://env/TOKEN"), "s3cr3t-value");
        Assert.Equal(SecretValue.Mask, value.ToString());
        Assert.Equal(SecretValue.Mask, $"{value}");
        Assert.Equal("s3cr3t-value", value.Reveal());
    }
}

public class PolicyDefaultsTests
{
    [Fact]
    public void Only_safe_and_navigational_risk_is_allowed_by_default()
    {
        Assert.Equal(new[] { RiskClass.Safe, RiskClass.Navigational }, RiskClasses.DefaultAllowed.Order());
    }

    [Theory]
    [InlineData(ActionType.LongPress)]
    [InlineData(ActionType.Point)]
    [InlineData(ActionType.OpenLink)]
    [InlineData(ActionType.SetPermissions)]
    public void Opt_in_action_types_are_not_allowed_by_default(ActionType type)
    {
        Assert.DoesNotContain(type, ActionTypes.DefaultAllowed);
    }

    [Fact]
    public void Unknown_risk_ranks_at_least_as_state_changing()
    {
        Assert.True(RiskClass.Unknown > RiskClass.StateChanging);
    }
}

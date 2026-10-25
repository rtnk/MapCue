using MapCue.Application.Configuration;
using MapCue.Domain;

namespace MapCue.Application.Tests;

public class ConfigValidatorTests
{
    private static MapCueConfigDocument Minimal() => new()
    {
        SchemaVersion = "mapcue.config/1",
        Allowlist = new AllowlistDocument { AppIds = ["com.example.demo"], Environments = ["test"] },
    };

    private static ConfigError SingleError(ConfigValidationResult result)
    {
        Assert.False(result.IsValid);
        Assert.Null(result.Settings);
        return Assert.Single(result.Errors);
    }

    [Fact]
    public void Minimal_config_is_valid_and_uses_safe_defaults()
    {
        var result = MapCueConfigValidator.Validate(Minimal());

        Assert.True(result.IsValid);
        var s = result.Settings!;
        Assert.Equal(".mapcue", s.Workspace);
        Assert.Equal(Budgets.Default, s.Budgets);
        Assert.Equal(AgentSettings.Default, s.Agent);
        Assert.Empty(s.Profiles);
    }

    [Fact]
    public void Default_budgets_match_architecture_section_10_2()
    {
        var b = Budgets.Default;
        Assert.Equal(8, b.MaxDepth);
        Assert.Equal(40, b.MaxScreens);
        Assert.Equal(120, b.MaxStates);
        Assert.Equal(300, b.MaxActions);
        Assert.Equal(TimeSpan.FromMinutes(60), b.MaxDuration);
        Assert.Equal(2, b.MaxRetries);
        Assert.Equal(2, b.MaxListRowsPerTemplate);
        Assert.Equal(TimeSpan.FromSeconds(5), b.UiSettleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(30), b.ActionTimeout);
        Assert.Equal(TimeSpan.FromSeconds(20), b.ObserveTimeout);

        var a = AgentSettings.Default;
        Assert.Equal(AgentRuntime.Container, a.Runtime);
        Assert.False(a.AllowLocalExecution);
        Assert.False(a.SendScreenshots);
        Assert.Equal(100, a.MaxCalls);
        Assert.Equal(1_000_000, a.MaxTokens);
        Assert.Equal(5m, a.MaxCostUsd);
    }

    [Fact]
    public void Empty_document_is_rejected()
    {
        Assert.False(MapCueConfigValidator.Validate(null).IsValid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mapcue.config/2")]
    [InlineData("")]
    public void Schema_version_must_match(string? version)
    {
        var doc = Minimal();
        doc.SchemaVersion = version;
        Assert.Equal(ConfigErrorCodes.SchemaVersion, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Fact]
    public void Missing_allowlist_section_reports_both_allowlists()
    {
        var doc = Minimal();
        doc.Allowlist = null;
        var codes = MapCueConfigValidator.Validate(doc).Errors.Select(e => e.Code).ToList();
        Assert.Contains(ConfigErrorCodes.MissingAppIds, codes);
        Assert.Contains(ConfigErrorCodes.MissingEnvironments, codes);
    }

    [Fact]
    public void Missing_app_allowlist_is_an_error()
    {
        var doc = Minimal();
        doc.Allowlist!.AppIds = [];
        var error = SingleError(MapCueConfigValidator.Validate(doc));
        Assert.Equal(ConfigErrorCodes.MissingAppIds, error.Code);
        Assert.Equal("allowlist.appIds", error.Path);
    }

    [Fact]
    public void Missing_environment_allowlist_is_an_error()
    {
        var doc = Minimal();
        doc.Allowlist!.Environments = null;
        var error = SingleError(MapCueConfigValidator.Validate(doc));
        Assert.Equal(ConfigErrorCodes.MissingEnvironments, error.Code);
        Assert.Equal("allowlist.environments", error.Path);
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("production")]
    [InlineData("staging")]
    [InlineData("TEST")]
    public void Non_test_environments_are_rejected(string environment)
    {
        var doc = Minimal();
        doc.Allowlist!.Environments = [environment];
        Assert.Equal(ConfigErrorCodes.UnsupportedEnvironment, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("com.example demo")]
    [InlineData("com.example/demo")]
    public void Malformed_allowlist_entries_are_rejected(string appId)
    {
        var doc = Minimal();
        doc.Allowlist!.AppIds = ["com.example.demo", appId];
        Assert.Equal(ConfigErrorCodes.InvalidAllowlistEntry, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65)]
    public void Out_of_range_depth_is_rejected(int depth)
    {
        var doc = Minimal();
        doc.Budgets = new BudgetsDocument { MaxDepth = depth };
        var error = SingleError(MapCueConfigValidator.Validate(doc));
        Assert.Equal(ConfigErrorCodes.BudgetOutOfRange, error.Code);
        Assert.Equal("budgets.maxDepth", error.Path);
    }

    [Theory]
    [InlineData("30", ConfigErrorCodes.InvalidDuration)]
    [InlineData("30 s", ConfigErrorCodes.InvalidDuration)]
    [InlineData("-5s", ConfigErrorCodes.InvalidDuration)]
    [InlineData("1d", ConfigErrorCodes.InvalidDuration)]
    [InlineData("0s", ConfigErrorCodes.BudgetOutOfRange)]
    [InlineData("11m", ConfigErrorCodes.BudgetOutOfRange)]
    public void Invalid_timeouts_are_rejected(string value, string code)
    {
        var doc = Minimal();
        doc.Budgets = new BudgetsDocument { ActionTimeout = value };
        Assert.Equal(code, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Fact]
    public void Overridden_budgets_are_applied()
    {
        var doc = Minimal();
        doc.Budgets = new BudgetsDocument { MaxActions = 50, MaxDuration = "15m", UiSettleTimeout = "500ms", MaxRetries = 0 };
        var b = MapCueConfigValidator.Validate(doc).Settings!.Budgets;
        Assert.Equal(50, b.MaxActions);
        Assert.Equal(TimeSpan.FromMinutes(15), b.MaxDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(500), b.UiSettleTimeout);
        Assert.Equal(0, b.MaxRetries);
        Assert.Equal(Budgets.Default.MaxDepth, b.MaxDepth);
    }

    [Fact]
    public void Local_agent_requires_explicit_opt_in()
    {
        var doc = Minimal();
        doc.Agent = new AgentDocument { Runtime = "local" };
        Assert.Equal(ConfigErrorCodes.LocalAgentNotAllowed, SingleError(MapCueConfigValidator.Validate(doc)).Code);

        doc.Agent.AllowLocalExecution = true;
        Assert.Equal(AgentRuntime.Local, MapCueConfigValidator.Validate(doc).Settings!.Agent.Runtime);
    }

    [Fact]
    public void Unknown_agent_runtime_is_rejected()
    {
        var doc = Minimal();
        doc.Agent = new AgentDocument { Runtime = "host" };
        Assert.Equal(ConfigErrorCodes.InvalidAgentRuntime, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    private static ProfileDocument Profile(Dictionary<string, string>? inputs = null) => new()
    {
        ProfileId = "basic",
        Platform = "android",
        AppId = "com.example.demo",
        Environment = "test",
        Inputs = inputs,
    };

    [Fact]
    public void Profile_with_secret_references_and_generators_is_valid()
    {
        var doc = Minimal();
        doc.Profiles = [Profile(new() { ["password"] = "secret://env/MAPCUE_DEMO_PASSWORD", ["email"] = "generated:email" })];

        var profile = Assert.Single(MapCueConfigValidator.Validate(doc).Settings!.Profiles);
        Assert.Equal(Platform.Android, profile.Platform);
        Assert.Equal(DataIsolation.SharedReadOnly, profile.DataIsolation);
        Assert.Collection(
            profile.Inputs,
            email => Assert.Equal("email", email.Generator),
            password => Assert.Equal("MAPCUE_DEMO_PASSWORD", password.Secret!.Name));
    }

    [Theory]
    [InlineData("hunter2-literal")]
    [InlineData("1234")]
    [InlineData("env:MAPCUE_PASSWORD")]
    [InlineData("generated:Bad Kind")]
    [InlineData("")]
    public void Literal_input_values_are_rejected_without_echoing_them(string literal)
    {
        var doc = Minimal();
        doc.Profiles = [Profile(new() { ["password"] = literal })];

        var error = SingleError(MapCueConfigValidator.Validate(doc));
        Assert.Equal(ConfigErrorCodes.LiteralInputValue, error.Code);
        Assert.Equal("profiles[0].inputs.password", error.Path);
        if (literal.Length > 0)
        {
            Assert.DoesNotContain(literal, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("secret://env/lowercase")]
    [InlineData("secret://vault/demo")]
    [InlineData("secret://env/")]
    public void Malformed_secret_references_are_rejected(string reference)
    {
        var doc = Minimal();
        doc.Profiles = [Profile(new() { ["password"] = reference })];
        Assert.Equal(ConfigErrorCodes.InvalidSecretRef, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Fact]
    public void Profile_app_must_be_allowlisted()
    {
        var doc = Minimal();
        var profile = Profile();
        profile.AppId = "com.other.app";
        doc.Profiles = [profile];
        Assert.Equal(ConfigErrorCodes.ProfileOutsideAllowlist, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("prod")]
    public void Profile_environment_must_be_test(string? environment)
    {
        var doc = Minimal();
        var profile = Profile();
        profile.Environment = environment;
        doc.Profiles = [profile];
        Assert.Equal(ConfigErrorCodes.UnsupportedEnvironment, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Fact]
    public void Profile_must_be_in_profile_allowlist_when_one_is_given()
    {
        var doc = Minimal();
        doc.Allowlist!.ProfileIds = ["investor"];
        doc.Profiles = [Profile()];
        Assert.Equal(ConfigErrorCodes.ProfileOutsideAllowlist, SingleError(MapCueConfigValidator.Validate(doc)).Code);
    }

    [Fact]
    public void Duplicate_profile_ids_are_rejected()
    {
        var doc = Minimal();
        doc.Profiles = [Profile(), Profile()];
        var error = SingleError(MapCueConfigValidator.Validate(doc));
        Assert.Equal("profiles[1].profileId", error.Path);
    }

    [Theory]
    [InlineData("platform", "windows")]
    [InlineData("dataIsolation", "wipe-everything")]
    public void Invalid_profile_enums_are_rejected(string field, string value)
    {
        var doc = Minimal();
        var profile = Profile();
        if (field == "platform")
        {
            profile.Platform = value;
        }
        else
        {
            profile.DataIsolation = value;
        }

        doc.Profiles = [profile];
        Assert.Equal($"profiles[0].{field}", SingleError(MapCueConfigValidator.Validate(doc)).Path);
    }
}

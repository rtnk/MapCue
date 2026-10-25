using MapCue.Application.Configuration;
using MapCue.Cli.Configuration;

namespace MapCue.Cli.Tests;

public class ConfigLoaderTests
{
    private const string Valid = """
        schemaVersion: mapcue.config/1
        allowlist:
          appIds: [com.example.demo]
          environments: [test]
        """;

    [Fact]
    public void Example_config_in_docs_is_valid()
    {
        var result = MapCueConfigLoader.LoadFile(CliTestHarness.RepoFile("docs/examples/mapcue.example.yaml"));

        Assert.True(result.IsValid, string.Join("\n", result.Errors));
        var profile = Assert.Single(result.Settings!.Profiles);
        Assert.Equal("basic", profile.ProfileId);
        Assert.Equal(Budgets.Default, result.Settings.Budgets);
    }

    [Fact]
    public void Minimal_yaml_uses_safe_defaults()
    {
        var result = MapCueConfigLoader.LoadText(Valid);
        Assert.True(result.IsValid);
        Assert.Equal(Budgets.Default, result.Settings!.Budgets);
        Assert.Equal(AgentSettings.Default, result.Settings.Agent);
    }

    [Fact]
    public void Empty_file_is_invalid()
    {
        Assert.False(MapCueConfigLoader.LoadText(string.Empty).IsValid);
    }

    [Fact]
    public void Unknown_keys_are_rejected()
    {
        var error = Assert.Single(MapCueConfigLoader.LoadText(Valid + "\nbudgetz:\n  maxDepth: 3\n").Errors);
        Assert.Equal(ConfigErrorCodes.SyntaxError, error.Code);
        Assert.Equal("Unknown key 'budgetz'.", error.Message);
    }

    [Fact]
    public void Duplicate_keys_are_rejected()
    {
        var error = Assert.Single(MapCueConfigLoader.LoadText(Valid + "\nschemaVersion: mapcue.config/1\n").Errors);
        Assert.Equal(ConfigErrorCodes.SyntaxError, error.Code);
    }

    [Fact]
    public void Type_errors_do_not_echo_the_value()
    {
        const string value = "Sup3rSecretToken";
        var error = Assert.Single(MapCueConfigLoader.LoadText(Valid + $"\nbudgets:\n  maxDepth: {value}\n").Errors);
        Assert.Equal(ConfigErrorCodes.SyntaxError, error.Code);
        Assert.DoesNotContain(value, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Literal_input_from_yaml_is_rejected_without_echo()
    {
        const string yaml = Valid + """

            profiles:
              - profileId: basic
                platform: ios
                appId: com.example.demo
                environment: test
                inputs:
                  pin: 4242
            """;
        var error = Assert.Single(MapCueConfigLoader.LoadText(yaml).Errors);
        Assert.Equal(ConfigErrorCodes.LiteralInputValue, error.Code);
        Assert.DoesNotContain("4242", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_file_is_rejected_before_parsing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mapcue-big-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, new string('#', (int)MapCueConfigLoader.MaxFileBytes + 1));
        try
        {
            Assert.Equal(ConfigErrorCodes.FileUnreadable, Assert.Single(MapCueConfigLoader.LoadFile(path).Errors).Code);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

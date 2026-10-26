namespace MapCue.Cli.Tests;

public class CommandTests
{
    [Fact]
    public async Task Help_matches_snapshot()
    {
        var (exitCode, output, _, _) = await CliTestHarness.RunAsync("--help");

        Assert.Equal(ExitCodes.Success, exitCode);
        var snapshotPath = Path.Combine(AppContext.BaseDirectory, "Snapshots", "help.txt");
        var expected = CliTestHarness.Normalize(await File.ReadAllTextAsync(snapshotPath));
        if (Environment.GetEnvironmentVariable("MAPCUE_UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(CliTestHarness.RepoFile("tests/MapCue.Cli.Tests/Snapshots/help.txt"), output);
            return;
        }

        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task Version_succeeds()
    {
        var (exitCode, output, _, _) = await CliTestHarness.RunAsync("--version");
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Equal(MapCueCli.Version + "\n", output);
        Assert.StartsWith("0.1.0", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("STEP-09", "blocked")]
    [InlineData("STEP-09", "resume", "task_0001", "--approve", "stateChanging")]
    [InlineData("STEP-10", "report")]
    [InlineData("STEP-10", "export")]
    [InlineData("STEP-10", "diff", "--from", "run_a", "--to", "run_b")]
    public async Task Placeholder_commands_report_not_implemented(string step, params string[] args)
    {
        var (exitCode, output, error, log) = await CliTestHarness.RunAsync(args);

        Assert.Equal(ExitCodes.NotImplemented, exitCode);
        Assert.Equal(string.Empty, output);
        Assert.Equal($"mapcue {args[0]}: not implemented (planned in {step}).\n", error);
        Assert.Contains("\"Command\":\"" + args[0] + "\"", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("status", "--help")]
    [InlineData("diff", "--help")]
    public async Task Subcommand_help_succeeds(params string[] args)
    {
        var (exitCode, output, _, _) = await CliTestHarness.RunAsync(args);
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown-command")]
    [InlineData("diff", "--from", "a")]
    [InlineData("resume")]
    public async Task Invalid_command_lines_are_usage_errors(params string[] args)
    {
        var (exitCode, _, error, _) = await CliTestHarness.RunAsync(args);
        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public async Task Explore_with_missing_config_fails_validation()
    {
        var (exitCode, _, error, _) = await CliTestHarness.RunAsync("explore", "--config", Path.Combine(Path.GetTempPath(), "mapcue-does-not-exist.yaml"));
        Assert.Equal(ExitCodes.ConfigInvalid, exitCode);
        Assert.Contains("CFG001", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explore_rejects_config_without_allowlists_and_never_prints_literal_secrets()
    {
        const string literal = "Hunter2-literal-password";
        var path = Path.Combine(Path.GetTempPath(), $"mapcue-test-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(path, $$"""
            schemaVersion: mapcue.config/1
            profiles:
              - profileId: basic
                platform: android
                appId: com.example.demo
                environment: test
                inputs:
                  password: {{literal}}
            """);
        try
        {
            var (exitCode, output, error, log) = await CliTestHarness.RunAsync("explore", "--config", path);

            Assert.Equal(ExitCodes.ConfigInvalid, exitCode);
            Assert.Contains("CFG010 allowlist.appIds", error, StringComparison.Ordinal);
            Assert.Contains("CFG011 allowlist.environments", error, StringComparison.Ordinal);
            Assert.Contains("CFG050 profiles[0].inputs.password", error, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, output + error + log, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

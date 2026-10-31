using MapCue.Domain;
using MapCue.Testing;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli.Tests;

/// <summary><c>mapcue explore</c> and <c>mapcue status</c> end to end on <see cref="FakeDevice"/> (the TapCue adapter is replaced).</summary>
public sealed class ExploreCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mapcue-cli-explore-" + Guid.NewGuid().ToString("N"));

    public ExploreCommandTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "setup-basic.yaml"), "appId: com.mapcue.demoapp\n---\n- launchApp\n");
    }

    private string Workspace => Path.Combine(_root, "ws");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Config(string extraProfile = "", string inputs = "")
    {
        var path = Path.Combine(_root, "mapcue.yaml");
        File.WriteAllText(path, $$"""
            schemaVersion: mapcue.config/1
            workspace: {{Workspace}}
            allowlist:
              appIds: [com.mapcue.demoapp]
              environments: [test]
            budgets:
              uiSettleTimeout: 200ms
            policy:
              riskRules:
                - name: other-profile
                  riskClass: accountChange
                  resourceId: com.mapcue.demoapp:id/profile.investor
            identity:
              screenHints:
                - ':id/screen\.[A-Za-z0-9_.-]+$'
            profiles:
              - profileId: basic
                platform: android
                appId: com.mapcue.demoapp
                environment: test
                setupFlow: setup-basic.yaml
            {{inputs}}
            {{extraProfile}}
            """);
        return path;
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(FakeDevice? device, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var log = new StringWriter();
        int exitCode;
        using (var host = new CliHost(log, new ManualClock(), LogLevel.Warning, new Dictionary<string, string>(), device is null ? null : (_, _) => device))
        {
            exitCode = await MapCueCli.RunAsync(args, output, error, host);
        }

        return (exitCode, CliTestHarness.Normalize(output.ToString()), CliTestHarness.Normalize(error.ToString()));
    }

    [Fact]
    public async Task Explore_runs_on_the_device_and_status_reports_the_run()
    {
        var config = Config();
        var device = new FakeDevice(FakeDemoApp.Basic(), new ManualClock());

        var (exitCode, output, error) = await RunAsync(device, "explore", "--config", config, "--seed", "7", "--app-build", "demo-1");

        Assert.True(exitCode == ExitCodes.Success, error + output);
        Assert.Contains("reset      start state st_", output, StringComparison.Ordinal);
        Assert.Contains("finished   Exhausted", output, StringComparison.Ordinal);
        Assert.Matches(@"graph      screens=\d+ states=(\d+)", output);
        Assert.True(File.Exists(Path.Combine(Workspace, "mapcue.db")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Workspace, "runs"), "attempt.json", SearchOption.AllDirectories));
        Assert.DoesNotContain(device.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("details.delete"));

        var (statusCode, status, _) = await RunAsync(null, "status", "--config", config);
        Assert.Equal(ExitCodes.Success, statusCode);
        Assert.Matches(@"run        run_[0-9a-f]+ status=Finished reason=Exhausted", status);
        Assert.Contains("profile    basic context=ctx_", status, StringComparison.Ordinal);
        Assert.Contains("device=fake-emulator-1 reasoner=rule-based", status, StringComparison.Ordinal);
        Assert.Matches(@"frontier   pending=0 leased=0 completed=\d+", status);

        var (byWorkspace, again, _) = await RunAsync(null, "status", "--workspace", Workspace);
        Assert.Equal(ExitCodes.Success, byWorkspace);
        Assert.Equal(status, again);
    }

    [Fact]
    public async Task Two_profiles_from_yaml_are_explored_separately_and_compared_by_status()
    {
        File.WriteAllText(Path.Combine(_root, "setup-investor.yaml"), "appId: com.mapcue.demoapp\n---\n- launchApp\n");
        var config = Path.Combine(_root, "profiles.yaml");
        File.WriteAllText(config, $$"""
            schemaVersion: mapcue.config/1
            workspace: {{Workspace}}
            allowlist:
              appIds: [com.mapcue.demoapp]
              environments: [test]
            budgets:
              uiSettleTimeout: 200ms
            policy:
              profileSwitch: deny
            identity:
              screenHints:
                - ':id/screen\.[A-Za-z0-9_.-]+$'
              profileVariantMaxDelta: 2
            profiles:
              - profileId: basic
                platform: android
                appId: com.mapcue.demoapp
                environment: test
                setupFlow: setup-basic.yaml
                switchElements: [':id/profile\.basic$']
              - profileId: investor
                platform: android
                appId: com.mapcue.demoapp
                environment: test
                setupFlow: setup-investor.yaml
                capabilities: [investments]
                scenario: default
                locale: und
                switchElements: [':id/profile\.investor$']
            """);

        var basicDevice = new FakeDevice(FakeDemoProfiles.Create(), new ManualClock());
        var (basic, basicOut, basicErr) = await RunAsync(basicDevice, "explore", "--config", config, "--profile", "basic", "--seed", "b", "--app-build", "demo-1");
        var (investor, investorOut, investorErr) = await RunAsync(new FakeDevice(FakeDemoProfiles.Create(), new ManualClock()), "explore", "--config", config, "--profile", "investor", "--seed", "i", "--app-build", "demo-1");

        Assert.True(basic == ExitCodes.Success, basicErr + basicOut);
        Assert.True(investor == ExitCodes.Success, investorErr + investorOut);
        Assert.DoesNotContain(basicDevice.ExecutedActions, a => a.TargetResourceId == FakeDemoApp.Id("profile.investor"));
        var (code, status, _) = await RunAsync(null, "status", "--config", config, "--profiles");
        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("profile    investor context=ctx_", status, StringComparison.Ordinal);
        Assert.Matches(@"context    ctx_\w+ profile=basic scenario=default build=demo-1 locale=und capabilities=\[\] states=7", status);
        Assert.Matches(@"context    ctx_\w+ profile=investor scenario=default build=demo-1 locale=und capabilities=\[investments\] states=8", status);
        Assert.Matches(@"guard      observed id:com\.mapcue\.demoapp:id/tab\.investments capability=investments present=\[ctx_\w+\] absent=\[ctx_\w+\] states=4", status);
        Assert.Contains("=notObserved", status, StringComparison.Ordinal);

        var (_, plain, _) = await RunAsync(null, "status", "--config", config);
        Assert.DoesNotContain("guard ", plain, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Same_seed_gives_the_same_run_id()
    {
        var config = Config();
        var (code, output, _) = await RunAsync(new FakeDevice(FakeDemoApp.Basic(), new ManualClock()), "explore", "--config", config, "--seed", "s");
        Assert.Equal(ExitCodes.Success, code);
        var first = output.Split('\n')[0];
        Directory.Delete(Workspace, recursive: true);

        var (_, second, _) = await RunAsync(new FakeDevice(FakeDemoApp.Basic(), new ManualClock()), "explore", "--config", config, "--seed", "s");

        Assert.Equal(first, second.Split('\n')[0]);
        Assert.StartsWith("run        run_", first, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_of_another_platform_fails_the_preflight()
    {
        var device = new FakeDevice(FakeDemoApp.Basic(), new ManualClock(), platform: Platform.Ios);

        var (exitCode, output, _) = await RunAsync(device, "explore", "--config", Config());

        Assert.Equal(ExitCodes.PreflightFailed, exitCode);
        Assert.Contains("finished   PreflightFailed: no connected, allowlisted device", output, StringComparison.Ordinal);
        Assert.Empty(device.ExecutedActions);
    }

    [Fact]
    public async Task Missing_secret_input_fails_before_the_run_without_echoing_values()
    {
        var device = new FakeDevice(FakeDemoApp.Basic(), new ManualClock());
        var config = Config(inputs: "    inputs:\n      password: secret://env/MAPCUE_TEST_PASSWORD_NOT_SET");

        var (exitCode, _, error) = await RunAsync(device, "explore", "--config", config);

        Assert.Equal(ExitCodes.PreflightFailed, exitCode);
        Assert.Contains("secret://env/MAPCUE_TEST_PASSWORD_NOT_SET", error, StringComparison.Ordinal);
        Assert.Empty(device.ExecutedActions);
        Assert.False(File.Exists(Path.Combine(Workspace, "mapcue.db")));
    }

    [Fact]
    public async Task Unknown_profile_is_a_usage_error()
    {
        var (exitCode, _, error) = await RunAsync(new FakeDevice(FakeDemoApp.Basic(), new ManualClock()), "explore", "--config", Config(), "--profile", "nope");

        Assert.Equal(ExitCodes.UsageError, exitCode);
        Assert.Contains("profile 'nope'", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_without_a_workspace_creates_nothing()
    {
        var (exitCode, output, _) = await RunAsync(null, "status", "--workspace", Workspace);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.StartsWith("no runs", output, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Workspace));
    }

    [Fact]
    public async Task Status_of_an_unknown_run_is_an_input_error()
    {
        await RunAsync(new FakeDevice(FakeDemoApp.Basic(), new ManualClock()), "explore", "--config", Config());

        var (exitCode, output, _) = await RunAsync(null, "status", "--workspace", Workspace, "--run", "run_nope");

        Assert.Equal(ExitCodes.InputInvalid, exitCode);
        Assert.Contains("run run_nope not found", output, StringComparison.Ordinal);
    }
}

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
    public async Task Contracts_from_yaml_are_captured_by_explore_built_and_versioned_and_a_manual_record_enters_the_next_version()
    {
        var config = Path.Combine(_root, "contracts.yaml");
        File.WriteAllText(config, $$"""
            schemaVersion: mapcue.config/1
            workspace: {{Workspace}}
            allowlist:
              appIds: [com.mapcue.demoapp]
              environments: [test]
            budgets:
              uiSettleTimeout: 200ms
            identity:
              screenHints:
                - ':id/screen\.[A-Za-z0-9_.-]+$'
            contracts:
              analyzeScreens: true
              keyboardFocus: true
              maxTabPresses: 10
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
                setupFlow: setup-basic.yaml
                capabilities: [investments]
                switchElements: [':id/profile\.investor$']
            """);

        var device = new FakeDevice(FakeDemoApp.Basic(withAboutDialog: false), new ManualClock());
        var (code, output, error) = await RunAsync(device, "explore", "--config", config, "--profile", "basic", "--seed", "c", "--app-build", "demo-1");
        Assert.True(code == ExitCodes.Success, error + output);
        Assert.Contains("screen     ", output, StringComparison.Ordinal);
        Assert.Matches(@"keyboard   st_\w+ presses=\d+ focused=\d+ reached=(\d+)/\1 stop=cycleClosed", output);
        Assert.True(device.KeyPresses > 0);

        var (built, contracts, buildErr) = await RunAsync(null, "contract", "build", "--config", config);
        Assert.True(built == ExitCodes.Success, buildErr + contracts);
        Assert.Matches(@"contract   screen-[\w-]+ v1 \(new\) sha256:[0-9a-f]{12} states=1 contexts=1 elements=\d+ findings=\d+ file=screens/screen-[\w-]+/grp_[0-9a-f]{12}/screen-contract\.v1\.json", contracts);
        Assert.Contains("a11y       ", contracts, StringComparison.Ordinal);
        Assert.Matches(@"TalkBack inspected=(present|issues) automated=unsupported assistiveVerified=notTested", contracts);
        Assert.Matches(@"Keyboard inspected=unsupported automated=pass assistiveVerified=notTested", contracts);
        Assert.DoesNotMatch(@"inspected=pass", contracts);
        Assert.Matches(@"contracts  total=7 new-versions=7 unchanged=0", contracts);

        var (_, again, _) = await RunAsync(null, "contract", "build", "--config", config);
        Assert.Contains("contracts  total=7 new-versions=0 unchanged=7", again, StringComparison.Ordinal);

        var settingsKey = System.Text.RegularExpressions.Regex.Matches(contracts, @"contract   (screen-[\w-]+) v1")
            .Select(m => m.Groups[1].Value)
            .Single(key => File.ReadAllText(Directory.GetFiles(Path.Combine(Workspace, "screens", key), "*.json", SearchOption.AllDirectories).Single()).Contains("settings.switchprofile", StringComparison.Ordinal));
        var transcript = Path.Combine(_root, "talkback.txt");
        File.WriteAllText(transcript, "Protocol mapcue.a11y-protocol/1, TalkBack, Settings: every element announced; the notifications switch is announced as 'Switch, on' without a name.\n");
        var (recorded, recordOut, recordErr) = await RunAsync(null, "a11y", "record", "--config", config, "--screen", settingsKey, "--profile", "basic", "--technology", "talkback", "--result", "fail", "--evidence", transcript, "--operator", "qa-1", "--device", "Pixel 8, Android 15, TalkBack 15.0");
        Assert.True(recorded == ExitCodes.Success, recordErr + recordOut);
        Assert.Matches(@"recorded   a11y_\w+ screen=scr_\w+ context=ctx_\w+ technology=TalkBack property=screen result=Fail evidence=ev_\w+", recordOut);

        var (_, third, _) = await RunAsync(null, "contract", "build", "--config", config);
        Assert.Contains($"contract   {settingsKey} v2 (new)", third, StringComparison.Ordinal);
        Assert.Contains($"a11y       {settingsKey} TalkBack inspected=issues automated=unsupported assistiveVerified=fail", third, StringComparison.Ordinal);
        Assert.Contains("contracts  total=7 new-versions=1 unchanged=6", third, StringComparison.Ordinal);

        // Records that cannot be true or have no evidence are refused; nothing is created without a database.
        var (wrong, _, wrongErr) = await RunAsync(null, "a11y", "record", "--config", config, "--screen", settingsKey, "--profile", "basic", "--technology", "voiceover", "--result", "pass", "--evidence", transcript, "--operator", "qa-1", "--device", "Pixel 8");
        Assert.Equal(ExitCodes.InputInvalid, wrong);
        Assert.Contains("VoiceOver cannot be recorded on the Android platform", wrongErr, StringComparison.Ordinal);
        var (missing, _, _) = await RunAsync(null, "a11y", "record", "--config", config, "--screen", settingsKey, "--technology", "talkback", "--result", "pass", "--evidence", Path.Combine(_root, "none.txt"), "--operator", "qa-1", "--device", "Pixel 8");
        Assert.Equal(ExitCodes.InputInvalid, missing);
        var (empty, _, emptyErr) = await RunAsync(null, "contract", "build", "--workspace", Path.Combine(_root, "empty"));
        Assert.Equal(ExitCodes.InputInvalid, empty);
        Assert.Contains("run mapcue explore first", emptyErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_root, "empty")));
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

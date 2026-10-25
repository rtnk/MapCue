using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.IntegrationTests;

/// <summary>CLI paths: version, check-syntax, full hierarchy, test with artifacts, flow policy for replays, exclusive mode.</summary>
public class CliRunnerTests
{
    private static readonly DeviceId _emulator = new("emulator-5554");

    [Fact]
    public async Task Version_and_check_syntax()
    {
        await using var h = AdapterHarness.Create();
        Assert.Equal(new ToolVersion("tapcue", "0.1.0"), await h.Adapter.GetVersionAsync(CancellationToken.None));

        var dir = Directory.CreateTempSubdirectory("mapcue-flows-");
        var good = Path.Combine(dir.FullName, "good.yaml");
        var bad = Path.Combine(dir.FullName, "bad.yaml");
        await File.WriteAllTextAsync(good, "appId: x\n---\n- back\n");
        await File.WriteAllTextAsync(bad, "appId: x\n---\n- tapOnn: \"x\"\n");
        Assert.Equal(new SyntaxCheckResult(true, []), await h.Adapter.CheckSyntaxAsync(new FlowFile(good), CancellationToken.None) with { Errors = [] });
        var invalid = await h.Adapter.CheckSyntaxAsync(new FlowFile(bad), CancellationToken.None);
        Assert.False(invalid.IsValid);
        Assert.Equal(["Invalid Command: tapOnn at /syntax-checker:3:9"], invalid.Errors);
        dir.Delete(true);
    }

    [Fact]
    public async Task Full_observation_uses_cli_hierarchy()
    {
        await using var h = AdapterHarness.Create();
        var observation = await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Full), CancellationToken.None);
        Assert.Equal(ObservationFidelity.Full, observation.Fidelity);
        Assert.Contains(observation.Elements, e => e.ResourceId == "com.android.settings:id/recycler_view" && e.Scrollable);
        Assert.StartsWith("{", observation.RawPayload, StringComparison.Ordinal);
        Assert.Empty(h.Fake.Calls());
    }

    [Fact]
    public async Task Exclusive_mode_closes_the_mcp_session_for_cli_calls()
    {
        await using var h = AdapterHarness.Create(configure: o => o with { TapCue = o.TapCue with { FullObservation = FullObservationMode.Exclusive } });
        await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None);
        await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Full), CancellationToken.None);
        await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None);
        Assert.Equal(2, h.Adapter.SessionStarts);
    }

    [Fact]
    public async Task Replay_writes_test_output_artifacts()
    {
        await using var h = AdapterHarness.Create();
        var flow = Path.Combine(h.Fake.Directory, "replay.yaml");
        await File.WriteAllTextAsync(flow, "appId: ${APP_ID}\nenv:\n  APP_ID: com.android.settings\n---\n- launchApp\n- takeScreenshot: launched\n");
        var outDir = Path.Combine(h.Fake.Directory, "out");

        var result = await h.Adapter.RunFlowAsync(_emulator, new FlowFile(flow), new FlowRunOptions(TimeSpan.FromSeconds(30), outDir), CancellationToken.None);

        Assert.Equal(ActionOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.GetFullPath(outDir), result.TestOutputDirectory);
        Assert.True(File.Exists(Path.Combine(outDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "commands.json")));
    }

    [Fact]
    public async Task Replay_closes_the_mcp_session_first_even_in_shared_mode()
    {
        // R-03: on the emulator a CLI `test` killed the open MCP session's driver connection.
        await using var h = AdapterHarness.Create();
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        var flow = Path.Combine(h.Fake.Directory, "replay.yaml");
        await File.WriteAllTextAsync(flow, "appId: com.android.settings\n---\n- launchApp\n");
        await h.Adapter.RunFlowAsync(_emulator, new FlowFile(flow), new FlowRunOptions(TimeSpan.FromSeconds(30), null), CancellationToken.None);
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, h.Adapter.SessionStarts);
    }

    [Fact]
    public async Task Failed_replay_is_classified()
    {
        await using var h = AdapterHarness.Create(scenarios: ["test-fail"]);
        var flow = Path.Combine(h.Fake.Directory, "replay.yaml");
        await File.WriteAllTextAsync(flow, "appId: com.android.settings\n---\n- tapOn: Nope\n");
        var result = await h.Adapter.RunFlowAsync(_emulator, new FlowFile(flow), new FlowRunOptions(TimeSpan.FromSeconds(30), null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Equal(DeviceErrorKind.ElementNotFound, result.ErrorKind);
        Assert.NotNull(result.TestOutputDirectory);
    }

    [Theory]
    [InlineData("appId: com.android.settings\n---\n- runScript: steal.js\n")]
    [InlineData("appId: com.other.app\n---\n- launchApp\n")]
    [InlineData("---\n- launchApp\n")]
    [InlineData("appId: com.android.settings\n---\n- inputText: ${output.token}\n")]
    public async Task Replays_violating_policy_are_rejected_before_tapcue_runs(string yaml)
    {
        await using var h = AdapterHarness.Create();
        var flow = Path.Combine(h.Fake.Directory, "replay.yaml");
        await File.WriteAllTextAsync(flow, yaml);
        var outDir = Path.Combine(h.Fake.Directory, "out");
        var result = await h.Adapter.RunFlowAsync(_emulator, new FlowFile(flow), new FlowRunOptions(TimeSpan.FromSeconds(30), outDir), CancellationToken.None);
        Assert.Equal(ActionOutcome.Rejected, result.Outcome);
        Assert.Equal(DeviceErrorKind.PolicyRejected, result.ErrorKind);
        Assert.False(Directory.Exists(outDir));
    }
}

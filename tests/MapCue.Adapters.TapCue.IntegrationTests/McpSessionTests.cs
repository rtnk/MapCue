using System.Diagnostics;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.IntegrationTests;

/// <summary>MCP over STDIO against a real child process: handshake, tool verification, replayed fixtures.</summary>
public class McpSessionTests
{
    private static readonly DeviceId _emulator = new("emulator-5554");

    [Fact]
    public async Task Recorded_no_device_session_lists_no_devices()
    {
        await using var h = AdapterHarness.Create("mcp-no-device.jsonl");
        Assert.Empty(await h.Adapter.ListDevicesAsync(CancellationToken.None));
        Assert.Equal("tapcue", h.Adapter.ServerInfo?.Name);
        Assert.Equal(1, h.Adapter.SessionStarts);
        Assert.Equal(TapCueVersionStatus.Supported, h.Adapter.VersionStatus);
    }

    [Fact]
    public async Task Recorded_device_errors_surface_with_their_kind()
    {
        await using var h = AdapterHarness.Create("mcp-no-device.jsonl");
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None));
        Assert.Equal(DeviceErrorKind.DeviceNotConnected, ex.Kind);

        var run = await h.Adapter.ExecuteAsync(_emulator, new ValidatedDeviceAction("a1", AdapterHarness.App, ActionType.Back, RiskClass.Navigational, null, null, null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, run.Outcome);
        Assert.Equal(DeviceErrorKind.DeviceNotConnected, run.ErrorKind);
        Assert.False(run.Dispatched);
    }

    [Fact]
    public async Task Launch_observe_back_observe_on_the_android_fixture()
    {
        await using var h = AdapterHarness.Create();
        var devices = await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Contains(devices, d => d.Id == _emulator && d.IsConnected);

        var launch = await h.Adapter.ExecuteAsync(_emulator, new ValidatedDeviceAction("launch", AdapterHarness.App, ActionType.LaunchApp, RiskClass.Navigational, null, null, null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Succeeded, launch.Outcome);
        Assert.True(launch.Dispatched);

        var before = await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None);
        Assert.Contains(before.Elements, e => e.Text == "Network & internet");
        Assert.Equal(ObservationFidelity.Fast, before.Fidelity);
        Assert.NotNull(before.RawPayload);

        var back = await h.Adapter.ExecuteAsync(_emulator, new ValidatedDeviceAction("back", AdapterHarness.App, ActionType.Back, RiskClass.Navigational, null, null, null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Succeeded, back.Outcome);

        var after = await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None);
        Assert.NotEqual(before.SnapshotHash, after.SnapshotHash);
        Assert.Equal(1, h.Adapter.SessionStarts);
        Assert.Equal(["list_devices", "run", "inspect_screen", "run", "inspect_screen"], h.Fake.Calls());
    }

    [Fact]
    public async Task Generated_flows_are_syntax_checked_once_per_shape()
    {
        await using var h = AdapterHarness.Create();
        var back = new ValidatedDeviceAction("b1", AdapterHarness.App, ActionType.Back, RiskClass.Navigational, null, null, null);
        Assert.Equal(ActionOutcome.Succeeded, (await h.Adapter.ExecuteAsync(_emulator, back, CancellationToken.None)).Outcome);
        Assert.Equal(ActionOutcome.Succeeded, (await h.Adapter.ExecuteAsync(_emulator, back with { ActionId = "b2" }, CancellationToken.None)).Outcome);
        // The check-syntax temp files are removed after the check.
        Assert.Empty(Directory.GetFiles(h.WorkDirectory, "check-*.yaml"));
    }

    [Fact]
    public async Task Missing_required_tool_is_an_unsupported_version()
    {
        await using var h = AdapterHarness.Create(scenarios: ["missing-tool:run"]);
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(DeviceErrorKind.UnsupportedVersion, ex.Kind);
        Assert.Contains("missing tool 'run'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_version_never_starts_a_session()
    {
        await using var h = AdapterHarness.Create(scenarios: ["version:0.2.0"]);
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(DeviceErrorKind.UnsupportedVersion, ex.Kind);
        await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(0, h.Adapter.SessionStarts);
        Assert.Empty(h.Fake.Calls());
    }

    [Fact]
    public async Task Untested_version_is_allowed_only_when_configured()
    {
        await using var h = AdapterHarness.Create(scenarios: ["version:0.2.0"], configure: o => o with { TapCue = o.TapCue with { AllowUntestedVersion = true } });
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(TapCueVersionStatus.Untested, h.Adapter.VersionStatus);
    }

    [Fact]
    public async Task Stray_stdout_lines_and_server_pings_do_not_break_the_session()
    {
        await using var h = AdapterHarness.Create(scenarios: ["noise", "ping-first"]);
        Assert.NotEmpty(await h.Adapter.ListDevicesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Allowlists_are_enforced_before_tapcue_is_called()
    {
        await using var h = AdapterHarness.Create(configure: o => o with { AllowedDeviceIds = ["emulator-*"] });
        var other = new DeviceId("R58M123");
        var exec = await h.Adapter.ExecuteAsync(other, new ValidatedDeviceAction("a", AdapterHarness.App, ActionType.Back, RiskClass.Safe, null, null, null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Rejected, exec.Outcome);
        Assert.Equal(DeviceErrorKind.PolicyRejected, exec.ErrorKind);

        var app = await h.Adapter.ExecuteAsync(_emulator, new ValidatedDeviceAction("a", "com.other.app", ActionType.Back, RiskClass.Safe, null, null, null), CancellationToken.None);
        Assert.Equal(ActionOutcome.Rejected, app.Outcome);

        var observe = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ObserveAsync(other, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None));
        Assert.Equal(DeviceErrorKind.PolicyRejected, observe.Kind);
        Assert.Empty(h.Fake.Calls());
    }

    [Fact]
    public async Task Missing_executable_is_reported_without_hardcoded_fallbacks()
    {
        await using var h = AdapterHarness.Create(configure: o => o with { TapCue = o.TapCue with { Executable = "tapcue-does-not-exist" } });
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.GetVersionAsync(CancellationToken.None));
        Assert.Equal(DeviceErrorKind.ToolNotFound, ex.Kind);
    }

    [Fact]
    public async Task Calls_are_serialized_per_adapter()
    {
        await using var h = AdapterHarness.Create();
        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 5).Select(_ => h.Adapter.ListDevicesAsync(CancellationToken.None)).ToList();
        await Task.WhenAll(tasks);
        Assert.All(tasks, t => Assert.NotEmpty(t.Result));
        Assert.Equal(1, h.Adapter.SessionStarts);
        Assert.Equal(5, h.Fake.Calls().Count);
    }
}

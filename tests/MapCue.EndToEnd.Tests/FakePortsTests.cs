using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Domain;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

public class FakePortsTests
{
    private static readonly FakeAppModel _app = new(
        "com.example.demo",
        "home",
        [
            new FakeScreen("home", [new FakeElement("AccountsTab", "Accounts", "accounts"), new FakeElement("SettingsTab", "Settings", "settings")]),
            new FakeScreen("accounts", [new FakeElement("HomeTab", "Home", "home")]),
            new FakeScreen("settings", []),
        ]);

    private static ValidatedDeviceAction Tap(string id) => new($"act-{id}", "com.example.demo", ActionType.Tap, RiskClass.Navigational, id, null, null);

    private static ValidatedDeviceAction Back() => new("act-back", "com.example.demo", ActionType.Back, RiskClass.Navigational, null, null, null);

    [Fact]
    public async Task Observe_reason_act_loop_runs_without_device_tapcue_or_llm()
    {
        var device = new FakeDevice(_app, new ManualClock());
        var id = device.Device.Id;
        var home = await device.ObserveAsync(id, new ObservationRequest(_app.AppId, ObservationFidelity.Fast), CancellationToken.None);

        var reasoner = new DeterministicFakeReasoner()
            .When(home.SnapshotHash, "Home", new AgentActionCandidate("c1", "e1", "tap", "navigational"));
        var request = new AgentExplorationRequest("req_0001", home.SnapshotHash, [new AgentSnapshotElement("e1", "button", "Accounts", "AccountsTab")]);
        var answer = await reasoner.InterpretAsync(request, CancellationToken.None);

        Assert.Equal(ReasonerStatus.Succeeded, answer.Status);
        Assert.Equal("req_0001", answer.Response!.RequestId);
        Assert.Equal(home.SnapshotHash, answer.Response.SnapshotHash);

        var executed = await device.ExecuteAsync(id, Tap("AccountsTab"), CancellationToken.None);
        Assert.Equal(ActionOutcome.Succeeded, executed.Outcome);
        Assert.Equal("accounts", device.CurrentScreenId);

        var accounts = await device.ObserveAsync(id, new ObservationRequest(_app.AppId, ObservationFidelity.Fast), CancellationToken.None);
        Assert.NotEqual(home.SnapshotHash, accounts.SnapshotHash);
        Assert.Equal(ReasonerStatus.Unavailable, (await reasoner.InterpretAsync(request with { SnapshotHash = accounts.SnapshotHash }, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Cycle_and_back_return_to_the_same_snapshot()
    {
        var device = new FakeDevice(_app, new ManualClock());
        var id = device.Device.Id;
        var request = new ObservationRequest(_app.AppId, ObservationFidelity.Fast);
        var start = (await device.ObserveAsync(id, request, CancellationToken.None)).SnapshotHash;

        await device.ExecuteAsync(id, Tap("AccountsTab"), CancellationToken.None);
        await device.ExecuteAsync(id, Tap("HomeTab"), CancellationToken.None);
        Assert.Equal(start, (await device.ObserveAsync(id, request, CancellationToken.None)).SnapshotHash);

        await device.ExecuteAsync(id, Back(), CancellationToken.None);
        Assert.Equal("accounts", device.CurrentScreenId);
    }

    [Fact]
    public async Task Missing_element_fails_and_unsupported_actions_are_rejected()
    {
        var device = new FakeDevice(_app, new ManualClock());
        var id = device.Device.Id;

        Assert.Equal(ActionOutcome.Failed, (await device.ExecuteAsync(id, Tap("Nope"), CancellationToken.None)).Outcome);
        var longPress = new ValidatedDeviceAction("act-lp", "com.example.demo", ActionType.LongPress, RiskClass.Safe, "AccountsTab", null, null);
        Assert.Equal(ActionOutcome.Rejected, (await device.ExecuteAsync(id, longPress, CancellationToken.None)).Outcome);
        Assert.Equal("home", device.CurrentScreenId);
    }

    [Fact]
    public async Task Two_identical_runs_produce_identical_observations()
    {
        static async Task<string[]> Run()
        {
            var device = new FakeDevice(_app, new ManualClock());
            var request = new ObservationRequest(_app.AppId, ObservationFidelity.Fast);
            var hashes = new List<string>();
            foreach (var target in new[] { "AccountsTab", "HomeTab", "SettingsTab" })
            {
                hashes.Add((await device.ObserveAsync(device.Device.Id, request, CancellationToken.None)).SnapshotHash);
                await device.ExecuteAsync(device.Device.Id, Tap(target), CancellationToken.None);
            }

            return [.. hashes];
        }

        Assert.Equal(await Run(), await Run());
    }

    [Fact]
    public async Task Wrong_device_is_refused()
    {
        var device = new FakeDevice(_app, new ManualClock());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.ObserveAsync(new DeviceId("other"), new ObservationRequest(_app.AppId, ObservationFidelity.Fast), CancellationToken.None));
    }
}

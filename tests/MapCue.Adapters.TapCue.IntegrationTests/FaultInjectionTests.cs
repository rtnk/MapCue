using System.Diagnostics;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.IntegrationTests;

/// <summary>Timeouts, hangs and broken STDIO (R-02, G-01, G-10): hard timeouts, process-tree kill, OutcomeUnknown, no action retry.</summary>
public class FaultInjectionTests
{
    private static readonly DeviceId _emulator = new("emulator-5554");

    private static ValidatedDeviceAction Tap(RiskClass risk = RiskClass.StateChanging) =>
        new("tap-1", AdapterHarness.App, ActionType.Tap, risk, "com.android.settings:id/search_action_bar", null, null);

    private static Func<TapCueAdapterOptions, TapCueAdapterOptions> Short(int retries = 0) => o => o with
    {
        TapCue = o.TapCue with { StartupTimeout = TimeSpan.FromSeconds(3) },
        Budgets = o.Budgets with { ActionTimeout = TimeSpan.FromSeconds(1), ObserveTimeout = TimeSpan.FromSeconds(1), MaxRetries = retries },
    };

    private static async Task AssertProcessGoneAsync(int? pid)
    {
        Assert.NotNull(pid);
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return; // No such process.
            }

            await Task.Delay(100);
        }

        Assert.Fail($"process {pid} is still running after the timeout kill");
    }

    [Fact]
    public async Task Hanging_action_times_out_is_killed_and_reported_as_outcome_unknown_without_retry()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-on:run"], configure: Short(retries: 2));
        await h.Adapter.ListDevicesAsync(CancellationToken.None); // Warm session; the device is still cold.
        var sw = Stopwatch.StartNew();
        var result = await h.Adapter.ExecuteAsync(_emulator, Tap(), CancellationToken.None);

        Assert.Equal(ActionOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(DeviceErrorKind.Timeout, result.ErrorKind);
        Assert.True(result.Dispatched);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        await AssertProcessGoneAsync(h.Fake.ChildPid());
        Assert.Equal(1, h.Fake.Calls().Count(c => c == "run"));

        // The next call transparently starts a new session.
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, h.Adapter.SessionStarts);
        Assert.Equal(1, h.Fake.Calls().Count(c => c == "run"));
    }

    [Fact]
    public async Task Broken_stdio_during_an_action_is_outcome_unknown()
    {
        await using var h = AdapterHarness.Create(scenarios: ["exit-on:run"], configure: Short(retries: 2));
        var result = await h.Adapter.ExecuteAsync(_emulator, Tap(RiskClass.Navigational), CancellationToken.None);

        Assert.Equal(ActionOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(DeviceErrorKind.TransportClosed, result.ErrorKind);
        Assert.True(result.Dispatched);
        Assert.Equal(["run"], h.Fake.Calls());
    }

    [Fact]
    public async Task Hanging_observation_is_killed_and_retried_within_the_retry_budget()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-on:inspect_screen"], configure: Short(retries: 1));
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None));

        Assert.Equal(DeviceErrorKind.Timeout, ex.Kind);
        Assert.Equal(["inspect_screen", "inspect_screen"], h.Fake.Calls());
        Assert.Equal(2, h.Adapter.SessionStarts);
        await AssertProcessGoneAsync(h.Fake.ChildPid());
    }

    [Fact]
    public async Task Broken_stdio_during_an_observation_is_retried_then_reported()
    {
        await using var h = AdapterHarness.Create(scenarios: ["exit-on:inspect_screen"], configure: Short(retries: 1));
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None));
        Assert.Equal(DeviceErrorKind.TransportClosed, ex.Kind);
        Assert.Equal(2, h.Fake.Calls().Count);
    }

    [Fact]
    public async Task Hanging_handshake_times_out_and_kills_the_server()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-handshake"], configure: o => o with { TapCue = o.TapCue with { StartupTimeout = TimeSpan.FromSeconds(2) } });
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ListDevicesAsync(CancellationToken.None));
        Assert.Equal(DeviceErrorKind.Timeout, ex.Kind);
        Assert.False(ex.Dispatched);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
        await AssertProcessGoneAsync(h.Fake.ChildPid());
    }

    [Fact]
    public async Task Action_with_a_hanging_handshake_is_not_dispatched()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-handshake"], configure: o => o with { TapCue = o.TapCue with { StartupTimeout = TimeSpan.FromSeconds(2), CheckGeneratedSyntax = false } });
        var result = await h.Adapter.ExecuteAsync(_emulator, Tap(), CancellationToken.None);
        Assert.Equal(ActionOutcome.TimedOut, result.Outcome);
        Assert.False(result.Dispatched);
        Assert.Empty(h.Fake.Calls());
    }

    [Fact]
    public async Task Hanging_cli_hierarchy_is_killed_with_its_process_tree()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-cli"], configure: o => o with
        {
            TapCue = o.TapCue with { StartupTimeout = TimeSpan.FromSeconds(5) },
            Budgets = o.Budgets with { ObserveTimeout = TimeSpan.FromSeconds(1) },
        });
        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TapCueException>(() => h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Full), CancellationToken.None));
        Assert.Equal(DeviceErrorKind.Timeout, ex.Kind);
        Assert.InRange(sw.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));
        await AssertProcessGoneAsync(h.Fake.ChildPid());
    }

    [Fact]
    public async Task Cancellation_kills_the_session_and_propagates()
    {
        await using var h = AdapterHarness.Create(scenarios: ["hang-on:run"], configure: o => o with { Budgets = o.Budgets with { ActionTimeout = TimeSpan.FromSeconds(60) } });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Adapter.ExecuteAsync(_emulator, Tap(), cts.Token));
        await AssertProcessGoneAsync(h.Fake.ChildPid());
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, h.Adapter.SessionStarts);
    }

    [Fact]
    public async Task Unknown_error_after_dispatch_is_outcome_unknown_only_for_state_changing_actions()
    {
        // The fixture has no reply for this tap, so the fake answers with an unrecognised isError text.
        await using var h = AdapterHarness.Create("mcp-no-device.jsonl", configure: o => o with { TapCue = o.TapCue with { CheckGeneratedSyntax = false } });
        var other = new DeviceId("emulator-5556");
        var risky = await h.Adapter.ExecuteAsync(other, Tap(RiskClass.StateChanging), CancellationToken.None);
        Assert.Equal(ActionOutcome.OutcomeUnknown, risky.Outcome);
        Assert.Equal(DeviceErrorKind.Unknown, risky.ErrorKind);

        var safe = await h.Adapter.ExecuteAsync(other, Tap(RiskClass.Navigational), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, safe.Outcome);
        Assert.Equal(2, h.Fake.Calls().Count);
    }

    [Fact]
    public async Task Lost_device_driver_restarts_the_session_and_retries_observations()
    {
        await using var h = AdapterHarness.Create(scenarios: ["driver-lost-once:inspect_screen"], configure: o => o with { Budgets = o.Budgets with { MaxRetries = 1 } });
        var observation = await h.Adapter.ObserveAsync(_emulator, new(AdapterHarness.App, ObservationFidelity.Fast), CancellationToken.None);
        Assert.NotEmpty(observation.Elements);
        Assert.Equal(2, h.Adapter.SessionStarts);
    }

    [Fact]
    public async Task Lost_device_driver_during_an_action_closes_the_session_without_retrying_it()
    {
        await using var h = AdapterHarness.Create(scenarios: ["driver-lost-once:run"], configure: Short(retries: 2));
        var result = await h.Adapter.ExecuteAsync(_emulator, Tap(RiskClass.Navigational), CancellationToken.None);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Equal(DeviceErrorKind.DriverLost, result.ErrorKind);
        Assert.Equal(["run"], h.Fake.Calls());
        await h.Adapter.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, h.Adapter.SessionStarts);
    }
}

using MapCue.Application.Exploration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.Time;
using MapCue.Domain;
using MapCue.Testing;

namespace MapCue.Application.Tests.Exploration;

public class UiStabilizerTests
{
    private static readonly NormalizationOptions _android = new(Platform.Android);

    private static IReadOnlyList<RawElement> Screen(string marker, bool loading = false) =>
        UiTrees.Flatten(UiTrees.Container(
            "android.widget.FrameLayout",
            [UiTrees.Text(marker, "app:id/screen." + marker), UiTrees.Button("app:id/go." + marker, "Go"), .. loading ? new[] { UiTrees.Node("android.widget.ProgressBar", "app:id/spinner") } : []]));

    /// <summary>Serves the scripted screens in order (the last one repeats); only observation is supported.</summary>
    private sealed class ScriptedDevice(params IReadOnlyList<RawElement>[] screens) : IDeviceAutomation
    {
        public int Observations { get; private set; }

        public Task<RawObservation> ObserveAsync(DeviceId device, ObservationRequest request, CancellationToken ct)
        {
            var screen = screens[Math.Min(Observations++, screens.Length - 1)];
            return Task.FromResult(new RawObservation(device, null, screen, "sha256:x", DateTimeOffset.UnixEpoch));
        }

        public Task<ToolVersion> GetVersionAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<ActionExecution> ExecuteAsync(DeviceId device, ValidatedDeviceAction action, CancellationToken ct) => throw new NotSupportedException();

        public Task<FlowRunResult> RunFlowAsync(DeviceId device, FlowFile flow, FlowRunOptions options, CancellationToken ct) => throw new NotSupportedException();

        public Task<SyntaxCheckResult> CheckSyntaxAsync(FlowFile flow, CancellationToken ct) => throw new NotSupportedException();
    }

    private static (UiStabilizer Stabilizer, List<TimeSpan> Waits) Create(IDeviceAutomation device, ManualClock clock, TimeSpan? settle = null)
    {
        var waits = new List<TimeSpan>();
        var stabilizer = new UiStabilizer(device, clock, settle ?? TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), (by, _) =>
        {
            waits.Add(by);
            clock.Advance(by);
            return Task.CompletedTask;
        });
        return (stabilizer, waits);
    }

    [Fact]
    public async Task Two_equal_fingerprints_are_stable()
    {
        var device = new ScriptedDevice(Screen("home"));
        var (stabilizer, waits) = Create(device, new ManualClock());

        var result = await stabilizer.ObserveAsync(new DeviceId("d"), "app", _android, CancellationToken.None);

        Assert.True(result.Stable);
        Assert.Equal(2, result.Observations);
        Assert.Equal([TimeSpan.FromMilliseconds(500)], waits);
    }

    [Fact]
    public async Task Loading_is_waited_out_and_the_final_screen_returned()
    {
        var device = new ScriptedDevice(Screen("loading", loading: true), Screen("loading", loading: true), Screen("home"), Screen("home"));
        var (stabilizer, _) = Create(device, new ManualClock());

        var result = await stabilizer.ObserveAsync(new DeviceId("d"), "app", _android, CancellationToken.None);

        Assert.True(result.Stable);
        Assert.Equal(4, result.Observations);
        Assert.Contains("a:id:app:id/screen.home", result.Normalized.IdentityTokens);
    }

    [Fact]
    public async Task A_screen_still_loading_at_the_timeout_is_returned_unstable()
    {
        var device = new ScriptedDevice(Screen("loading", loading: true));
        var clock = new ManualClock();
        var (stabilizer, _) = Create(device, clock);

        var result = await stabilizer.ObserveAsync(new DeviceId("d"), "app", _android, CancellationToken.None);

        Assert.False(result.Stable);
        Assert.Contains("loading", result.Normalized.StateDiscriminators);
        Assert.Equal(TimeSpan.FromSeconds(2), clock.UtcNow - ManualClock.DefaultStart);
        Assert.True(result.Observations <= stabilizer.MaxObservations);
    }

    [Fact]
    public async Task Changing_screens_are_bounded_even_when_the_clock_does_not_move()
    {
        var screens = Enumerable.Range(0, 50).Select(i => Screen("s" + i)).ToArray();
        var device = new ScriptedDevice(screens);
        var stabilizer = new UiStabilizer(device, new ManualClock(), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(500), (_, _) => Task.CompletedTask);

        var result = await stabilizer.ObserveAsync(new DeviceId("d"), "app", _android, CancellationToken.None);

        Assert.False(result.Stable);
        Assert.Equal(5, stabilizer.MaxObservations);
        Assert.Equal(5, device.Observations);
    }

    [Fact]
    public void Seeded_ids_repeat_for_the_same_seed_and_differ_otherwise()
    {
        string[] Ids(string seed)
        {
            var ids = new SeededIdGenerator(seed);
            return [ids.NewId("run"), ids.NewId("evt"), ids.NewId("evt")];
        }

        Assert.Equal(Ids("1"), Ids("1"));
        Assert.NotEqual(Ids("1"), Ids("2"));
        Assert.Equal(3, Ids("1").Distinct().Count());
        Assert.Matches("^run_[0-9a-f]{16}$", Ids("1")[0]);
    }
}

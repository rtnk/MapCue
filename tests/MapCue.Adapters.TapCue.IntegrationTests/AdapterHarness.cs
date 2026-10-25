using MapCue.Application.Configuration;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Testing;

namespace MapCue.Adapters.TapCue.IntegrationTests;

/// <summary>A <see cref="TapCueDeviceAutomation"/> wired to the fake TapCue process, with short budgets.</summary>
internal sealed class AdapterHarness : IAsyncDisposable
{
    public const string App = "com.android.settings";

    private readonly string _work;

    private AdapterHarness(FakeTapCue fake, TapCueDeviceAutomation adapter, PatternRedactor redactor, string work)
    {
        Fake = fake;
        Adapter = adapter;
        Redactor = redactor;
        _work = work;
    }

    public FakeTapCue Fake { get; }

    public TapCueDeviceAutomation Adapter { get; }

    public PatternRedactor Redactor { get; }

    public string WorkDirectory => _work;

    public static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tapcue-0.1.0", name);

    public static AdapterHarness Create(
        string? fixture = "mcp-android-settings.jsonl",
        string[]? scenarios = null,
        Func<TapCueAdapterOptions, TapCueAdapterOptions>? configure = null,
        IReadOnlyDictionary<string, string>? parentEnvironment = null,
        Func<string, string?>? resolveInput = null,
        Mcp.IMcpTranscriptSink? transcript = null)
    {
        var fake = FakeTapCue.Create(
            fixture is null ? null : Fixture(fixture),
            Fixture("hierarchy-android-launcher.txt"),
            scenarios ?? []);
        var work = Path.Combine(fake.Directory, "work");
        var options = new TapCueAdapterOptions(
            TapCueSettings.Default with { Executable = fake.Executable, StartupTimeout = TimeSpan.FromSeconds(15) },
            Budgets.Default with { ActionTimeout = TimeSpan.FromSeconds(10), ObserveTimeout = TimeSpan.FromSeconds(10), UiSettleTimeout = TimeSpan.FromSeconds(1), MaxRetries = 0 },
            [App],
            [],
            work,
            "test");
        options = configure?.Invoke(options) ?? options;
        var env = parentEnvironment ?? new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/tmp",
        };
        var redactor = new PatternRedactor();
        var adapter = new TapCueDeviceAutomation(options, redactor, new ManualClock(), resolveInput: resolveInput, transcript: transcript, parentEnvironment: env.GetValueOrDefault);
        return new AdapterHarness(fake, adapter, redactor, work);
    }

    public async ValueTask DisposeAsync()
    {
        await Adapter.DisposeAsync();
        Fake.Dispose();
    }
}

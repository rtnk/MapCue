using System.Text.RegularExpressions;
using MapCue.Adapters.Reasoning;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Exploration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.Time;
using MapCue.Domain;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.EndToEnd.Tests;

/// <summary>A workspace (SQLite + blobs + runs) with the exploration engine wired to a <see cref="FakeDevice"/>.</summary>
internal sealed class ExplorationHarness : IAsyncDisposable
{
    private ExplorationHarness(string root, FakeDevice device, ManualClock clock, SqliteStateStore store, FileSystemArtifactStore artifacts, PatternRedactor redactor, MapCueSettings settings)
    {
        Root = root;
        Device = device;
        Clock = clock;
        Store = store;
        Artifacts = artifacts;
        Redactor = redactor;
        Settings = settings;
    }

    public string Root { get; }

    public FakeDevice Device { get; }

    public ManualClock Clock { get; }

    public SqliteStateStore Store { get; }

    public FileSystemArtifactStore Artifacts { get; }

    public PatternRedactor Redactor { get; }

    public MapCueSettings Settings { get; }

    public string RunsDirectory => Path.Combine(Root, "runs");

    public string SetupFlow => Path.Combine(Root, "setup-basic.yaml");

    public List<string> Progress { get; } = [];

    /// <summary>
    /// Settings of the demo exploration: the demo app allowlisted, a shared-readonly basic profile, MAUI screen hints and the operator
    /// rule that keeps the run inside the basic profile (choosing the investor profile is an account change).
    /// </summary>
    public static MapCueSettings DemoSettings(Budgets? budgets = null) =>
        new(
            ".mapcue",
            new AllowlistSettings([FakeDemoApp.AppId], ["test"], [], []),
            budgets ?? Budgets.Default,
            AgentSettings.Default,
            [Profile],
            TapCueSettings.Default)
        {
            Policy = new PolicySettings([new OperatorRiskRule("other-profile", RiskClass.AccountChange, FakeDemoApp.Id("profile.investor"))]),
            Identity = new IdentitySettings([new Regex(@":id/screen\.[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))]),
        };

    public static ProfileSettings Profile { get; } =
        new("basic", Platform.Android, FakeDemoApp.AppId, "test", DataIsolation.SharedReadOnly, [], []) { SetupFlow = "setup-basic.yaml" };

    public static ExplorationHarness Create(FakeAppModel? app = null, MapCueSettings? settings = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "mapcue-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "setup-basic.yaml"), "appId: com.mapcue.demoapp\n---\n- launchApp\n");
        var clock = new ManualClock();
        var redactor = new PatternRedactor();
        return new ExplorationHarness(
            root,
            new FakeDevice(app ?? FakeDemoApp.Basic(), clock),
            clock,
            SqliteStateStore.Open(Path.Combine(root, "mapcue.db"), clock),
            new FileSystemArtifactStore(Path.Combine(root, "blobs"), redactor),
            redactor,
            settings ?? DemoSettings());
    }

    public ExplorationOptions Options(string? seed = "seed-1") => new(Settings.Profiles[0], RunsDirectory, "sha256:config")
    {
        SetupFlowPath = Settings.Profiles[0].SetupFlow is null ? null : SetupFlow,
        Seed = seed,
        AppBuildId = "demo-1",
        PollInterval = TimeSpan.FromMilliseconds(400),
        Delay = (by, _) =>
        {
            Clock.Advance(by);
            return Task.CompletedTask;
        },
        Progress = Progress.Add,
        MinFreeBytes = 0,
    };

    public Task<ExplorationResult> RunAsync(IExplorationReasoner? reasoner = null, string? seed = "seed-1", CancellationToken ct = default) =>
        Engine(reasoner, seed).RunAsync(Options(seed), ct);

    public ExplorationEngine Engine(IExplorationReasoner? reasoner = null, string? seed = "seed-1") =>
        new(
            Device,
            Store,
            Artifacts,
            Redactor,
            reasoner ?? new RuleBasedReasoner(new RiskClassifier(Settings.Policy.RiskRules)),
            Settings,
            Clock,
            seed is null ? new SequentialIdGenerator() : new SeededIdGenerator(seed));

    public Task<T> ReadAsync<T>(Func<IUnitOfWork, Task<T>> read) => Store.InTransactionAsync(read, CancellationToken.None);

    public Task<CanonicalGraph> CanonicalAsync() => GraphQueries.CanonicalAsync(Store, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

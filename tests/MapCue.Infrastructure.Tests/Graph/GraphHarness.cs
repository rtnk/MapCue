using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;
using static MapCue.Testing.UiTrees;

namespace MapCue.Infrastructure.Tests.Graph;

/// <summary>A temporary workspace with a real SQLite store and artifact store, plus a small simulated banking app.</summary>
internal sealed class GraphHarness : IAsyncDisposable
{
    public static readonly ExplorationContext Basic = ExplorationContext.Create("basic", Platform.Android, "com.mapcue.demo", "1.0.0", "pl-PL");
    public static readonly ExplorationContext Investor = ExplorationContext.Create("investor", Platform.Android, "com.mapcue.demo", "1.0.0", "pl-PL", capabilities: ["investments"]);

    private GraphHarness(string directory, IStateStore? wrap)
    {
        Directory = directory;
        Clock = new ManualClock();
        Redactor = new PatternRedactor();
        Sqlite = SqliteStateStore.Open(Path.Combine(directory, "mapcue.db"), Clock);
        Store = wrap ?? Sqlite;
        Artifacts = new FileSystemArtifactStore(Path.Combine(directory, "blobs"), Redactor);
        Recorder = new SnapshotRecorder(Artifacts, Redactor, Clock);
        Committer = new GraphCommitter(Store, Clock, Ids);
        Importer = new GraphImporter(Recorder, Committer, Store, Clock);
    }

    public string Directory { get; }

    public ManualClock Clock { get; }

    public SequentialIdGenerator Ids { get; } = new();

    public PatternRedactor Redactor { get; }

    public SqliteStateStore Sqlite { get; }

    public IStateStore Store { get; private set; }

    public FileSystemArtifactStore Artifacts { get; }

    public SnapshotRecorder Recorder { get; }

    public GraphCommitter Committer { get; private set; }

    public GraphImporter Importer { get; }

    public static GraphHarness Create(string? directory = null) =>
        new(directory ?? NewDirectory(), null);

    public static string NewDirectory() => Path.Combine(Path.GetTempPath(), "mapcue-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Uses <paramref name="store"/> (e.g. a fault-injecting wrapper around <see cref="Sqlite"/>) for commits.</summary>
    public void UseStore(IStateStore store)
    {
        Store = store;
        Committer = new GraphCommitter(store, Clock, Ids);
    }

    public static RawObservation Observe(string name, params UiNode[] roots) =>
        new(new DeviceId("emulator-5554"), null, Flatten([StatusBar(), .. roots]), ContentHash.Sha256(name), ManualClock.DefaultStart);

    // Simulated app: Home ↔ Accounts (cycle), Accounts → Details, Home → Settings; investor sees "Investments" on Home.
    public static RawObservation Home(bool investor = false, string greeting = "Dzień dobry") =>
        Observe(
            "home" + (investor ? "-investor" : string.Empty),
            Container(
                "android.widget.FrameLayout",
                [
                    Text(greeting, "app:id/greeting"),
                    Text("Home", "app:id/toolbar_title"),
                    Button("app:id/tab_home", "Home"),
                    Button("app:id/tab_accounts", "Accounts"),
                    Button("app:id/tab_settings", "Settings"),
                    Button("app:id/transfer", "Transfer"),
                    Button("app:id/blik", "BLIK"),
                    Button("app:id/notifications", "Notifications"),
                    Button("app:id/help", "Help"),
                    Button("app:id/profile", "Profile"),
                    .. investor ? new[] { Button("app:id/investments", "Investments") } : [],
                ]));

    public static RawObservation Accounts(int rows = 3) =>
        Observe("accounts-" + rows, Container("android.widget.FrameLayout", Text("Accounts", "app:id/toolbar_title"), Button("app:id/back", "Back"), List("app:id/accounts", rows, i => $"Konto {i} · {1000 + i},00 zł")));

    public static RawObservation Settings() =>
        Observe("settings", Container("android.widget.FrameLayout", Text("Settings", "app:id/toolbar_title"), Button("app:id/back", "Back"), Node("android.widget.Switch", "app:id/dark_mode", "Dark mode", clickable: true)));

    public static ActionSpec Tap(string resourceId) => new(ActionType.Tap, "id:" + resourceId, null, RiskClass.Navigational, CandidateSource.Rule);

    public static ActionSpec Back { get; } = new(ActionType.Back, string.Empty, null, RiskClass.Navigational, CandidateSource.Rule);

    public async Task<RecordedSnapshot> RecordAsync(RawObservation observation, ExplorationContext? context = null) =>
        await Recorder.RecordAsync(observation, context ?? Basic, null, CancellationToken.None);

    public async Task<AttemptCommitResult> ActAsync(string attemptId, RawObservation before, ActionSpec action, RawObservation? after, ExplorationContext? context = null, ActionOutcome outcome = ActionOutcome.Succeeded)
    {
        context ??= Basic;
        var b = await RecordAsync(before, context);
        var a = after is null ? null : await RecordAsync(after, context);
        return await Committer.CommitAttemptAsync(new AttemptCommit(context, attemptId, b, action, outcome, TimeSpan.FromMilliseconds(250), a), CancellationToken.None);
    }

    public Task<GraphSummary> SummaryAsync() => GraphQueries.SummarizeAsync(Sqlite, CancellationToken.None);

    public Task<CanonicalGraph> CanonicalAsync() => GraphQueries.CanonicalAsync(Sqlite, CancellationToken.None);

    public Task<T> ReadAsync<T>(Func<IUnitOfWork, Task<T>> read) => Sqlite.InTransactionAsync(read, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await Sqlite.DisposeAsync();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the temporary workspace.
        }
    }
}

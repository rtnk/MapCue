using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Domain.Secrets;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Artifacts.Redaction;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;
using static MapCue.Testing.UiTrees;

namespace MapCue.Adapters.Reasoning.Tests;

/// <summary>A temporary workspace (SQLite, artifact store, redactor) with a committed "Profile" screen to interpret.</summary>
internal sealed class AgentHarness : IAsyncDisposable
{
    public const string AppId = "com.mapcue.demo";

    public static readonly ExplorationContext Basic = ExplorationContext.Create("basic", Platform.Android, AppId, "1.0.0", "pl-PL");

    public static readonly AllowlistSettings Allowlist = new([AppId], ["test"], [], []);

    private AgentHarness(string directory)
    {
        Directory = directory;
        Redactor = new PatternRedactor();
        Store = SqliteStateStore.Open(Path.Combine(directory, "mapcue.db"), Clock);
        Artifacts = new FileSystemArtifactStore(Path.Combine(directory, "blobs"), Redactor);
        Recorder = new SnapshotRecorder(Artifacts, Redactor, Clock);
        Committer = new GraphCommitter(Store, Clock, new SequentialIdGenerator());
    }

    public string Directory { get; }

    public ManualClock Clock { get; } = new();

    public PatternRedactor Redactor { get; }

    public SqliteStateStore Store { get; }

    public FileSystemArtifactStore Artifacts { get; }

    public SnapshotRecorder Recorder { get; }

    public GraphCommitter Committer { get; }

    public static AgentHarness Create() =>
        new(Path.Combine(Path.GetTempPath(), "mapcue-agent-tests", Guid.NewGuid().ToString("N")));

    public static ProfileSettings Profile(DataIsolation isolation = DataIsolation.SharedReadOnly) =>
        new("basic", Platform.Android, AppId, "test", isolation, [new ProfileInput("username", SecretRef.Parse("secret://env/MAPCUE_DEMO_USER"), null)], []);

    public static AgentPolicy Policy(DataIsolation isolation = DataIsolation.SharedReadOnly) => AgentPolicy.For(Profile(isolation));

    /// <summary>A form-like screen with navigation, a list, text fields and risky buttons (EN labels; PL in other tests).</summary>
    public static RawObservation ProfileScreen(params UiNode[] extra) =>
        new(
            new DeviceId("emulator-5554"),
            null,
            Flatten(
                StatusBar(),
                Container(
                    "android.widget.FrameLayout",
                    [
                        Text("Profile", "app:id/toolbar_title"),
                        Button("app:id/back", "Back"),
                        Node("android.widget.EditText", "app:id/username", null, clickable: true),
                        Node("android.widget.EditText", "app:id/nickname", null, clickable: true),
                        Button("app:id/save", "Save"),
                        Button("app:id/transfer", "Transfer"),
                        Button("app:id/delete_account", "Delete account"),
                        Button("app:id/share", "Share"),
                        Node("android.widget.Button", null, "Help", clickable: true),
                        Text("Saldo 1 234,00 zł"),
                        List("app:id/items", 3),
                        .. extra,
                    ])),
            ContentHash.Sha256("profile-screen" + extra.Length),
            ManualClock.DefaultStart);

    /// <summary>Records and commits <paramref name="observation"/>; returns the state and its recorded snapshot.</summary>
    public async Task<(string StateId, RecordedSnapshot Snapshot)> CommitAsync(RawObservation? observation = null)
    {
        var recorded = await Recorder.RecordAsync(observation ?? ProfileScreen(), Basic, null, CancellationToken.None);
        var committed = await Committer.CommitObservationAsync(Basic, recorded, CancellationToken.None);
        return (committed.StateId, recorded);
    }

    public static AgentResponseValidator Validator(IReadOnlyList<OperatorRiskRule>? rules = null) => new(new RiskClassifier(rules), new PolicyGate(Allowlist));

    public AgentInterpreter Interpreter(IExplorationReasoner reasoner, AgentBudget? budget = null) =>
        new(reasoner, Validator(), budget ?? new AgentBudget(AgentSettings.Default), Artifacts, Store, Clock);

    public static AgentInterpretationInput Input(string stateId, RecordedSnapshot snapshot, AgentPolicy? policy = null, string taskId = "task_analyze_1") =>
        new(Basic, stateId, taskId, "att_1", snapshot, policy ?? Policy());

    /// <summary>A request as the interpreter would build it (no graph hints).</summary>
    public static AgentExplorationRequest Request(RecordedSnapshot snapshot, AgentPolicy? policy = null, AgentGraphHints? hints = null) =>
        AgentRequestBuilder.Build(new AgentRequestInput(
            AgentRequestBuilder.RequestIdOf("task_analyze_1", "att_1", snapshot.Snapshot.NormalizedSha256),
            "task_analyze_1",
            "att_1",
            Basic,
            snapshot,
            hints ?? new AgentGraphHints([], []),
            policy ?? Policy()));

    /// <summary>Ref of the element with this resource id (or, failing that, this text).</summary>
    public static string RefOf(RecordedSnapshot snapshot, string resourceIdOrText) =>
        snapshot.Normalized.Elements.First(e => e.ResourceId == resourceIdOrText || (e.ResourceId is null && e.Text == resourceIdOrText)).ElementRef;

    public static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "agent-responses", name));

    /// <summary>A golden answer with its placeholders resolved for <paramref name="request"/>.</summary>
    public static string Golden(string name, AgentExplorationRequest request, RecordedSnapshot snapshot)
    {
        var text = Fixture(name).Replace("{{requestId}}", request.RequestId, StringComparison.Ordinal).Replace("{{snapshotHash}}", request.SnapshotHash, StringComparison.Ordinal);
        return System.Text.RegularExpressions.Regex.Replace(text, "\\{\\{ref:([^}]+)\\}\\}", m => RefOf(snapshot, m.Groups[1].Value));
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
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

using System.CommandLine;
using MapCue.Application.Configuration;
using MapCue.Application.Graph;
using MapCue.Application.Time;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli.Graph;

/// <summary>
/// <c>mapcue graph</c>: screen graph maintenance. <c>graph import</c> is a test tool (STEP-03): it imports recorded snapshots and
/// transitions into the workspace without a device. Importing the same plan again changes nothing.
/// </summary>
internal static class GraphCommand
{
    public const string DatabaseFileName = "mapcue.db";
    public const string BlobsDirectoryName = "blobs";

    public static Command Build(CliHost host, ILogger logger)
    {
        var planArgument = new Argument<FileInfo>("plan") { Description = $"Import plan ({GraphImportFile.SchemaVersion}, JSON)." };
        var workspaceOption = new Option<DirectoryInfo>("--workspace", "-w")
        {
            Description = $"Workspace directory (database and artifacts). Default: {MapCueSettings.DefaultWorkspace}.",
            DefaultValueFactory = _ => new DirectoryInfo(MapCueSettings.DefaultWorkspace),
        };
        var import = new Command("import", "Import recorded snapshots and transitions into the graph (test tool, no device).");
        import.Arguments.Add(planArgument);
        import.Options.Add(workspaceOption);
        import.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            var planFile = parse.GetValue(planArgument)!;
            var workspace = parse.GetValue(workspaceOption)!.FullName;

            GraphImportPlan plan;
            try
            {
                plan = GraphImportFile.Load(planFile.FullName);
            }
            catch (Exception ex) when (ex is GraphImportFileException or FormatException or InvalidOperationException or ArgumentException)
            {
                error.WriteLine($"mapcue graph import: plan '{planFile.Name}' is invalid: {host.Redactor.Redact(ex.Message, new("console")).Text}");
                return ExitCodes.InputInvalid;
            }

            WorkspaceLock workspaceLock;
            try
            {
                workspaceLock = WorkspaceLock.Acquire(workspace);
            }
            catch (WorkspaceLockedException ex)
            {
                error.WriteLine($"mapcue graph import: {ex.Message}");
                return ExitCodes.WorkspaceUnavailable;
            }

            using (workspaceLock)
            {
                SqliteStateStore store;
                try
                {
                    store = SqliteStateStore.Open(Path.Combine(workspace, DatabaseFileName), host.Clock);
                }
                catch (SchemaMismatchException ex)
                {
                    error.WriteLine($"mapcue graph import: {ex.Message}");
                    return ExitCodes.WorkspaceUnavailable;
                }

                await using (store)
                {
                    var artifacts = new FileSystemArtifactStore(Path.Combine(workspace, BlobsDirectoryName), host.Redactor);
                    var ids = new Version7IdGenerator(host.Clock);
                    var importer = new GraphImporter(
                        new SnapshotRecorder(artifacts, host.Redactor, host.Clock),
                        new GraphCommitter(store, host.Clock, ids),
                        store,
                        host.Clock);
                    GraphImportResult result;
                    try
                    {
                        result = await importer.ImportAsync(plan, ct);
                    }
                    catch (ArgumentException ex)
                    {
                        error.WriteLine($"mapcue graph import: plan '{planFile.Name}' is invalid: {host.Redactor.Redact(ex.Message, new("console")).Text}");
                        return ExitCodes.InputInvalid;
                    }

                    var summary = await GraphQueries.SummarizeAsync(store, ct);

                    output.WriteLine($"imported   run={result.RunId} context={plan.Context.ContextId} profile={plan.Context.ProfileId}");
                    output.WriteLine($"changes    observations={result.Observations} transitions={result.Transitions} new-states={result.NewStates} new-variants={result.NewVariants} new-tasks={result.NewTasks} already-committed={result.AlreadyCommittedTransitions}");
                    output.WriteLine($"graph      contexts={summary.Contexts} screens={summary.Screens} states={summary.States} variants={summary.Variants} edges={summary.Edges} observations={summary.Observations} tasks={summary.Tasks} events={summary.Events}");
                    foreach (var (observation, state) in result.StateOfObservation)
                    {
                        output.WriteLine($"state      {observation} -> {state}");
                    }

                    logger.LogInformation("Graph import finished: {NewStates} new state(s), {Already} transition(s) already committed", result.NewStates, result.AlreadyCommittedTransitions);
                    return ExitCodes.Success;
                }
            }
        });

        var graph = new Command("graph", "Maintain the screen graph (test tooling: import recorded snapshots).");
        graph.Subcommands.Add(import);
        return graph;
    }
}

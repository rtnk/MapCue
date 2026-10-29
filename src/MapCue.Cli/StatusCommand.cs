using System.CommandLine;
using System.Globalization;
using MapCue.Application.Configuration;
using MapCue.Application.Graph;
using MapCue.Cli.Configuration;
using MapCue.Cli.Graph;
using MapCue.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>
/// <c>mapcue status</c> (STEP-05): the latest run (or <c>--run</c>) of a workspace with its counters, the graph and the frontier.
/// Read-only: it does not take the workspace lock, so it also works while <c>mapcue explore</c> runs (SQLite WAL readers).
/// The workspace comes from <c>--workspace</c>, else from the configuration (<c>--config</c>, default <c>mapcue.yaml</c> if it exists),
/// else <c>.mapcue</c>.
/// </summary>
internal static class StatusCommand
{
    public static Command Build(CliHost host, ILogger logger)
    {
        var configOption = new Option<FileInfo?>("--config", "-c") { Description = $"Configuration whose workspace to read. Default: {MapCueCli.DefaultConfigPath} if it exists." };
        var workspaceOption = new Option<DirectoryInfo?>("--workspace", "-w") { Description = "Workspace directory (overrides the configuration)." };
        var runOption = new Option<string?>("--run") { Description = "Run id (default: the latest run)." };
        var command = new Command("status", "Show the state of the current or last run.");
        command.Options.Add(configOption);
        command.Options.Add(workspaceOption);
        command.Options.Add(runOption);
        command.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            string workspace;
            if (parse.GetValue(workspaceOption) is { } dir)
            {
                workspace = dir.FullName;
            }
            else
            {
                var config = parse.GetValue(configOption) ?? new FileInfo(MapCueCli.DefaultConfigPath);
                if (parse.GetValue(configOption) is not null || config.Exists)
                {
                    var loaded = MapCueConfigLoader.LoadFile(config.FullName);
                    if (!loaded.IsValid)
                    {
                        error.WriteLine($"mapcue status: configuration '{config.Name}' is invalid:");
                        foreach (var e in loaded.Errors)
                        {
                            error.WriteLine($"  {e}");
                        }

                        return ExitCodes.ConfigInvalid;
                    }

                    workspace = Path.GetFullPath(loaded.Settings!.Workspace);
                }
                else
                {
                    workspace = Path.GetFullPath(MapCueSettings.DefaultWorkspace);
                }
            }

            var database = Path.Combine(workspace, GraphCommand.DatabaseFileName);
            if (!File.Exists(database))
            {
                // Never create a workspace just to report on it.
                output.WriteLine($"no runs: workspace has no database ({GraphCommand.DatabaseFileName})");
                return ExitCodes.Success;
            }

            SqliteStateStore store;
            try
            {
                store = SqliteStateStore.Open(database, host.Clock);
            }
            catch (SchemaMismatchException ex)
            {
                error.WriteLine($"mapcue status: {ex.Message}");
                return ExitCodes.WorkspaceUnavailable;
            }

            await using (store)
            {
                var runId = parse.GetValue(runOption);
                var (run, stats) = await store.InTransactionAsync(
                    async uow => (runId is null ? await uow.Runs.GetLatestAsync(ct) : await uow.Runs.GetAsync(runId, ct), await uow.Frontier.GetStatsAsync(ct)),
                    ct);
                if (run is null)
                {
                    output.WriteLine(runId is null ? "no runs" : $"run {runId} not found");
                    return runId is null ? ExitCodes.Success : ExitCodes.InputInvalid;
                }

                var summary = await GraphQueries.SummarizeAsync(store, ct);
                var c = run.Counters;
                var ended = run.EndedAt is { } at ? at.ToString("O", CultureInfo.InvariantCulture) : "-";
                output.WriteLine($"run        {run.RunId} status={run.Status} reason={run.FinishReason?.ToString() ?? "-"}");
                output.WriteLine($"profile    {run.ProfileId} context={run.ContextId ?? "-"} device={run.DeviceId ?? "-"} reasoner={run.Reasoner}");
                output.WriteLine($"time       started={run.StartedAt.ToString("O", CultureInfo.InvariantCulture)} ended={ended}");
                if (run.Detail is not null)
                {
                    output.WriteLine($"detail     {host.Redactor.Redact(run.Detail, new("console")).Text}");
                }

                output.WriteLine($"counters   analyses={c.Analyses} attempts={c.Attempts} actions={c.DeviceActions} navigation={c.NavigationActions} resets={c.Resets} new-states={c.NewStates} new-screens={c.NewScreens} app-left={c.AppLeft} blocked={c.Blocked} outcome-unknown={c.OutcomeUnknown} recovered={c.RecoveredTasks} represented={c.RepresentedTasks}");
                output.WriteLine($"graph      screens={summary.Screens} states={summary.States} variants={summary.Variants} edges={summary.Edges} observations={summary.Observations}");
                output.WriteLine($"frontier   pending={stats.Pending} leased={stats.Leased} completed={stats.Completed} blocked={stats.Blocked} failed={stats.Failed} outcome-unknown={stats.OutcomeUnknown} cancelled={stats.Cancelled}");
                logger.LogInformation("Status of run {RunId} shown", run.RunId);
                return ExitCodes.Success;
            }
        });
        return command;
    }
}

using System.CommandLine;
using MapCue.Adapters.Reasoning;
using MapCue.Adapters.Reasoning.Cli;
using MapCue.Adapters.TapCue;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Exploration;
using MapCue.Application.Ports;
using MapCue.Application.Time;
using MapCue.Cli.Configuration;
using MapCue.Cli.Graph;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Artifacts.Secrets;
using MapCue.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>
/// <c>mapcue explore</c> (STEP-05, STEP-06): validates the configuration, locks the workspace and runs <see cref="ExplorationEngine"/>
/// for one profile on one device through TapCue; holding the lock, the run first reconciles what an interrupted run left behind. Exit codes: 0 when the run finished normally (exhausted, budget or all remaining tasks
/// blocked), <see cref="ExitCodes.PreflightFailed"/>, <see cref="ExitCodes.RunFailed"/> (error or cancellation).
/// </summary>
internal static class ExploreCommand
{
    public const string RunsDirectoryName = "runs";

    /// <summary>Deterministic values of <c>generated:&lt;kind&gt;</c> inputs (synthetic test data, never real data).</summary>
    public static IReadOnlyDictionary<string, string> GeneratedValues { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["email"] = "mapcue.test@example.invalid",
        ["name"] = "MapCue Test",
        ["text"] = "mapcue",
        ["number"] = "12345",
    };

    public static Command Build(CliHost host, ILogger logger, Option<FileInfo> configOption)
    {
        var profileOption = new Option<string?>("--profile") { Description = "Profile to explore (default: the first profile of the configuration)." };
        var deviceOption = new Option<string?>("--device") { Description = "Device id (default: the only connected, allowlisted device of the profile's platform)." };
        var seedOption = new Option<string?>("--seed") { Description = "Seed of run, event and checkpoint ids (two runs with the same seed and app give the same graph)." };
        var buildOption = new Option<string>("--app-build")
        {
            Description = "Build label of the explored app (part of the exploration context).",
            DefaultValueFactory = _ => "unknown",
        };

        var explore = new Command("explore", "Run a budgeted exploration of an allowlisted app (validates the configuration first).");
        explore.Options.Add(configOption);
        explore.Options.Add(profileOption);
        explore.Options.Add(deviceOption);
        explore.Options.Add(seedOption);
        explore.Options.Add(buildOption);
        explore.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            var file = parse.GetValue(configOption)!;
            var loaded = MapCueConfigLoader.LoadFile(file.FullName);
            if (!loaded.IsValid)
            {
                error.WriteLine($"mapcue explore: configuration '{file.Name}' is invalid:");
                foreach (var e in loaded.Errors)
                {
                    error.WriteLine($"  {e}");
                }

                logger.LogWarning("Configuration rejected with {ErrorCount} error(s)", loaded.Errors.Count);
                return ExitCodes.ConfigInvalid;
            }

            var settings = loaded.Settings!;
            var profileId = parse.GetValue(profileOption);
            var profile = profileId is null ? (settings.Profiles.Count > 0 ? settings.Profiles[0] : null) : settings.Profiles.FirstOrDefault(p => p.ProfileId == profileId);
            if (profile is null)
            {
                error.WriteLine(profileId is null ? "mapcue explore: the configuration has no profile." : $"mapcue explore: profile '{profileId}' is not in the configuration.");
                return ExitCodes.UsageError;
            }

            // Profile inputs: secrets are resolved now (registered with the redactor), generators are synthetic constants.
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            var secrets = new EnvironmentSecretResolver(host.Redactor, host.Environment);
            foreach (var input in profile.Inputs)
            {
                if (input.Secret is not null)
                {
                    try
                    {
                        inputs[input.Name] = secrets.Resolve(input.Secret).Reveal();
                    }
                    catch (SecretUnavailableException ex)
                    {
                        error.WriteLine($"mapcue explore: preflight failed: {ex.Message}");
                        return ExitCodes.PreflightFailed;
                    }
                }
                else if (input.Generator is not null && GeneratedValues.TryGetValue(input.Generator, out var generated))
                {
                    inputs[input.Name] = generated;
                }
            }

            string? ResolveInput(string reference) =>
                reference.StartsWith("profile.inputs.", StringComparison.Ordinal) ? inputs.GetValueOrDefault(reference["profile.inputs.".Length..])
                : reference.StartsWith("generated:", StringComparison.Ordinal) ? GeneratedValues.GetValueOrDefault(reference["generated:".Length..])
                : null;

            var workspace = Path.GetFullPath(settings.Workspace);
            var setupFlow = profile.SetupFlow is null ? null : Path.GetFullPath(Path.Combine(file.DirectoryName ?? ".", profile.SetupFlow));
            WorkspaceLock workspaceLock;
            try
            {
                workspaceLock = WorkspaceLock.Acquire(workspace);
            }
            catch (WorkspaceLockedException ex)
            {
                error.WriteLine($"mapcue explore: {ex.Message}");
                return ExitCodes.WorkspaceUnavailable;
            }

            using (workspaceLock)
            {
                SqliteStateStore store;
                try
                {
                    store = SqliteStateStore.Open(Path.Combine(workspace, GraphCommand.DatabaseFileName), host.Clock);
                }
                catch (SchemaMismatchException ex)
                {
                    error.WriteLine($"mapcue explore: {ex.Message}");
                    return ExitCodes.WorkspaceUnavailable;
                }

                await using (store)
                {
                    var options = TapCueAdapterOptions.FromSettings(settings, MapCueCli.Version);
                    var device = host.DeviceFactory?.Invoke(options, ResolveInput)
                        ?? new TapCueDeviceAutomation(options, host.Redactor, host.Clock, logger, ResolveInput, parentEnvironment: host.Environment);
                    try
                    {
                        IExplorationReasoner reasoner = settings.Agent.Reasoner == ReasonerKind.Cli
                            ? new CliAgentReasoner(settings.Agent, secrets, hostVariable: host.Environment)
                            : new RuleBasedReasoner(new RiskClassifier(settings.Policy.RiskRules));
                        var seed = parse.GetValue(seedOption);
                        var engine = new ExplorationEngine(
                            device,
                            store,
                            new FileSystemArtifactStore(Path.Combine(workspace, GraphCommand.BlobsDirectoryName), host.Redactor),
                            host.Redactor,
                            reasoner,
                            settings,
                            host.Clock,
                            seed is null ? new Version7IdGenerator(host.Clock) : new SeededIdGenerator(seed));
                        var result = await engine.RunAsync(
                            new ExplorationOptions(profile, Path.Combine(workspace, RunsDirectoryName), ContentHash.Sha256(await File.ReadAllTextAsync(file.FullName, ct)))
                            {
                                DeviceId = parse.GetValue(deviceOption),
                                SetupFlowPath = setupFlow,
                                Seed = seed,
                                AppBuildId = parse.GetValue(buildOption)!,
                                InputValues = inputs.Values.ToList(),
                                Progress = output.WriteLine,

                                // The workspace lock is held: leases of other runs belong to dead processes and are reconciled at once.
                                WorkspaceLockHeld = true,
                            },
                            ct);

                        var summary = await Application.Graph.GraphQueries.SummarizeAsync(store, CancellationToken.None);
                        output.WriteLine($"graph      screens={summary.Screens} states={summary.States} edges={summary.Edges} observations={summary.Observations} tasks={summary.Tasks}");
                        logger.LogInformation("Run {RunId} finished: {Reason}", result.RunId, result.Reason);
                        return result.Reason switch
                        {
                            RunFinishReason.PreflightFailed => ExitCodes.PreflightFailed,
                            RunFinishReason.Failed or RunFinishReason.StoppedByOperator => ExitCodes.RunFailed,
                            _ => ExitCodes.Success,
                        };
                    }
                    finally
                    {
                        if (device is IAsyncDisposable disposable)
                        {
                            await disposable.DisposeAsync();
                        }
                    }
                }
            }
        });
        return explore;
    }
}

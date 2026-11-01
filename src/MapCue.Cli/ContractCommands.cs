using System.CommandLine;
using System.Text;
using MapCue.Application.Configuration;
using MapCue.Application.ScreenContracts;
using MapCue.Cli.Configuration;
using MapCue.Cli.Graph;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Artifacts;
using MapCue.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>
/// STEP-08: <c>mapcue contract build</c> builds and versions the screen contracts of a workspace (no device), <c>mapcue a11y record</c> stores a
/// manual accessibility check of the operator (the only way to <c>assistiveVerified</c> for screen readers). Both take the workspace lock.
/// The workspace comes from <c>--workspace</c>, else from the configuration (<c>--config</c>, default <c>mapcue.yaml</c> if it exists), else
/// <c>.mapcue</c>.
/// </summary>
internal static class ContractCommands
{
    public static Command BuildContract(CliHost host, ILogger logger)
    {
        var (configOption, workspaceOption) = WorkspaceOptions();
        var build = new Command("build", "Build and version the screen contracts of the workspace (mapcue.screen-contract/1) with their accessibility matrix.");
        build.Options.Add(configOption);
        build.Options.Add(workspaceOption);
        build.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            return await WithWorkspaceAsync(host, parse, configOption, workspaceOption, "contract build", error, async (workspace, store, artifacts) =>
            {
                var contracts = await ScreenContractBuilder.BuildAsync(store, artifacts, ct);
                var published = await ScreenContractPublisher.PublishAsync(store, artifacts, host.Clock, contracts, workspace, ct);
                foreach (var p in published)
                {
                    WriteContract(line => output.WriteLine(host.Redactor.Redact(line, new("console")).Text), p);
                }

                output.WriteLine($"contracts  total={published.Count} new-versions={published.Count(p => p.NewVersion)} unchanged={published.Count(p => !p.NewVersion)}");
                logger.LogInformation("Built {Count} screen contract(s), {New} new version(s)", published.Count, published.Count(p => p.NewVersion));
                return ExitCodes.Success;
            });
        });
        var contract = new Command("contract", "Screen contracts (STEP-08): build and version them from the workspace.");
        contract.Subcommands.Add(build);
        return contract;
    }

    public static Command A11y(CliHost host, ILogger logger)
    {
        var (configOption, workspaceOption) = WorkspaceOptions();
        var screenOption = new Option<string>("--screen") { Description = "Screen key or id (see mapcue contract build).", Required = true };
        var technologyOption = new Option<string>("--technology") { Description = "talkback, voiceover or keyboard.", Required = true };
        var resultOption = new Option<string>("--result") { Description = "pass or fail.", Required = true };
        var propertyOption = new Option<string>("--property") { Description = $"What was checked: {string.Join(", ", A11yProperties.Known.Order(StringComparer.Ordinal))} ('{A11yProperties.Screen}' = the whole screen by the protocol).", DefaultValueFactory = _ => A11yProperties.Screen };
        var evidenceOption = new Option<FileInfo>("--evidence") { Description = "UTF-8 text file with the protocol transcript (required; redacted before it is stored).", Required = true };
        var operatorOption = new Option<string>("--operator") { Description = "Who did the check (initials or a role, no personal data needed).", Required = true };
        var deviceOption = new Option<string>("--device") { Description = "Device, OS and assistive technology version, e.g. 'Pixel 8, Android 15, TalkBack 15.0'.", Required = true };
        var profileOption = new Option<string?>("--profile") { Description = "Profile whose context the check was done in." };
        var contextOption = new Option<string?>("--context") { Description = "Context id, when the profile was explored with several builds or locales." };
        var stateOption = new Option<string?>("--state") { Description = "State id, when only one state of the screen was checked." };
        var elementOption = new Option<string?>("--element") { Description = "Element key, for a check of one element." };
        var noteOption = new Option<string?>("--note") { Description = "Short note (redacted)." };
        var record = new Command("record", "Record a manual accessibility check (docs/a11y-manual-protocol.md): the only way to assistiveVerified for screen readers.");
        foreach (var option in new Option[] { configOption, workspaceOption, screenOption, technologyOption, resultOption, propertyOption, evidenceOption, operatorOption, deviceOption, profileOption, contextOption, stateOption, elementOption, noteOption })
        {
            record.Options.Add(option);
        }

        record.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            AccessibilityTechnology? technology = parse.GetValue(technologyOption)!.ToLowerInvariant() switch
            {
                "talkback" => AccessibilityTechnology.TalkBack,
                "voiceover" => AccessibilityTechnology.VoiceOver,
                "keyboard" => AccessibilityTechnology.Keyboard,
                _ => null,
            };
            AccessibilityRecordResult? result = parse.GetValue(resultOption)!.ToLowerInvariant() switch
            {
                "pass" => AccessibilityRecordResult.Pass,
                "fail" => AccessibilityRecordResult.Fail,
                _ => null,
            };
            if (technology is null || result is null)
            {
                error.WriteLine("mapcue a11y record: --technology must be talkback, voiceover or keyboard and --result pass or fail.");
                return ExitCodes.UsageError;
            }

            var evidenceFile = parse.GetValue(evidenceOption)!;
            string evidence;
            try
            {
                if (!evidenceFile.Exists || evidenceFile.Length > AccessibilityRecorder.MaxEvidenceBytes)
                {
                    error.WriteLine($"mapcue a11y record: evidence file '{evidenceFile.Name}' is missing or larger than {AccessibilityRecorder.MaxEvidenceBytes} bytes.");
                    return ExitCodes.InputInvalid;
                }

                evidence = await File.ReadAllTextAsync(evidenceFile.FullName, new UTF8Encoding(false, throwOnInvalidBytes: true), ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                error.WriteLine($"mapcue a11y record: evidence file '{evidenceFile.Name}' cannot be read as UTF-8 text.");
                return ExitCodes.InputInvalid;
            }

            return await WithWorkspaceAsync(host, parse, configOption, workspaceOption, "a11y record", error, async (_, store, artifacts) =>
            {
                try
                {
                    var stored = await AccessibilityRecorder.RecordAsync(
                        store,
                        artifacts,
                        host.Redactor,
                        host.Clock,
                        new AccessibilityRecordRequest(
                            parse.GetValue(screenOption)!,
                            parse.GetValue(profileOption),
                            parse.GetValue(contextOption),
                            parse.GetValue(stateOption),
                            technology.Value,
                            parse.GetValue(elementOption),
                            parse.GetValue(propertyOption)!,
                            result.Value,
                            parse.GetValue(operatorOption)!,
                            parse.GetValue(deviceOption)!,
                            evidence,
                            parse.GetValue(noteOption)),
                        ct);
                    output.WriteLine($"recorded   {stored.RecordId} screen={stored.ScreenId} context={stored.ContextId} technology={stored.Technology} property={stored.Property} result={stored.Result} evidence={stored.EvidenceId}");
                    output.WriteLine("next       mapcue contract build (the record enters the next contract version)");
                    logger.LogInformation("Manual accessibility record {RecordId} stored", stored.RecordId);
                    return ExitCodes.Success;
                }
                catch (AccessibilityRecordException ex)
                {
                    error.WriteLine($"mapcue a11y record: {host.Redactor.Redact(ex.Message, new("console")).Text}");
                    return ExitCodes.InputInvalid;
                }
            });
        });
        var a11y = new Command("a11y", "Accessibility (STEP-08): manual checks of the operator.");
        a11y.Subcommands.Add(record);
        return a11y;
    }

    /// <summary>One line per contract and a matrix line per technology: <c>technology inspected/automated/assistiveVerified</c>.</summary>
    private static void WriteContract(Action<string> writeLine, PublishedContract published)
    {
        var c = published.Contract;
        writeLine($"contract   {c.ScreenKey} v{c.Version}{(published.NewVersion ? " (new)" : string.Empty)} {c.ContentHash[..19]} states={c.States.Count} contexts={c.Contexts.Count} elements={c.Elements.Count} findings={c.Accessibility.Findings.Count} file={published.RelativePath}");
        foreach (var row in c.Accessibility.Matrix)
        {
            writeLine($"a11y       {c.ScreenKey} {row.Technology} inspected={row.Inspected.Status} automated={row.Automated.Status} assistiveVerified={row.AssistiveVerified.Status}");
        }
    }

    private static (Option<FileInfo?> Config, Option<DirectoryInfo?> Workspace) WorkspaceOptions() =>
        (new Option<FileInfo?>("--config", "-c") { Description = $"Configuration whose workspace to use. Default: {MapCueCli.DefaultConfigPath} if it exists." },
         new Option<DirectoryInfo?>("--workspace", "-w") { Description = "Workspace directory (overrides the configuration)." });

    private static async Task<int> WithWorkspaceAsync(
        CliHost host,
        ParseResult parse,
        Option<FileInfo?> configOption,
        Option<DirectoryInfo?> workspaceOption,
        string command,
        TextWriter error,
        Func<string, SqliteStateStore, FileSystemArtifactStore, Task<int>> work)
    {
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
                    error.WriteLine($"mapcue {command}: configuration '{config.Name}' is invalid:");
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
            // Never create a workspace: contracts and records belong to an explored graph.
            error.WriteLine($"mapcue {command}: workspace has no database ({GraphCommand.DatabaseFileName}); run mapcue explore first.");
            return ExitCodes.InputInvalid;
        }

        WorkspaceLock workspaceLock;
        try
        {
            workspaceLock = WorkspaceLock.Acquire(workspace);
        }
        catch (WorkspaceLockedException ex)
        {
            error.WriteLine($"mapcue {command}: {ex.Message}");
            return ExitCodes.WorkspaceUnavailable;
        }

        using (workspaceLock)
        {
            SqliteStateStore store;
            try
            {
                store = SqliteStateStore.Open(database, host.Clock);
            }
            catch (SchemaMismatchException ex)
            {
                error.WriteLine($"mapcue {command}: {ex.Message}");
                return ExitCodes.WorkspaceUnavailable;
            }

            await using (store)
            {
                var artifacts = new FileSystemArtifactStore(Path.Combine(workspace, GraphCommand.BlobsDirectoryName), host.Redactor);
                return await work(workspace, store, artifacts);
            }
        }
    }
}

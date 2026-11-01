using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.Reflection;
using MapCue.Cli.Graph;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>Command tree of <c>mapcue</c>. Commands of later steps are placeholders that exit with <see cref="ExitCodes.NotImplemented"/>.</summary>
public static class MapCueCli
{
    public const string CommandName = "mapcue";

    public const string DefaultConfigPath = "mapcue.yaml";

    public const int HelpWidth = 120;

    public static Command BuildRootCommand(CliHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var logger = host.CreateLogger("MapCue.Cli");

        // A named root (instead of RootCommand) keeps help stable regardless of the hosting process name,
        // and the version action reports MapCue itself rather than the entry assembly (tests, tools).
        var root = new Command(CommandName, "MapCue: deterministic exploration of mobile apps through TapCue.");
        // Fixed width: help output must not depend on the terminal (snapshot-tested).
        root.Options.Add(new HelpOption { Action = new HelpAction { MaxWidth = HelpWidth } });
        root.Options.Add(new VersionOption { Action = new PrintVersionAction() });

        var configOption = new Option<FileInfo>("--config", "-c")
        {
            Description = "Path to the MapCue configuration (YAML).",
            DefaultValueFactory = _ => new FileInfo(DefaultConfigPath),
        };
        root.Subcommands.Add(ExploreCommand.Build(host, logger, configOption));
        root.Subcommands.Add(StatusCommand.Build(host, logger));
        root.Subcommands.Add(Placeholder("blocked", "List tasks blocked and waiting for the operator.", "STEP-09", logger));

        var taskArgument = new Argument<string>("taskId") { Description = "Blocked task to resume." };
        var approveOption = new Option<string[]>("--approve") { Description = "Risk category approved by the operator for this task.", AllowMultipleArgumentsPerToken = false };
        var resume = new Command("resume", "Resume a blocked task after operator action.");
        resume.Arguments.Add(taskArgument);
        resume.Options.Add(approveOption);
        resume.SetAction(parse => NotImplemented(parse, logger, "resume", "STEP-09"));
        root.Subcommands.Add(resume);

        root.Subcommands.Add(Placeholder("report", "Generate coverage reports and the graph view.", "STEP-10", logger));
        root.Subcommands.Add(Placeholder("export", "Export verified paths as TapCue flows.", "STEP-10", logger));

        var fromOption = new Option<string>("--from") { Description = "Baseline run or build.", Required = true };
        var toOption = new Option<string>("--to") { Description = "Compared run or build.", Required = true };
        var diff = new Command("diff", "Compare screen maps of two runs or builds.");
        diff.Options.Add(fromOption);
        diff.Options.Add(toOption);
        diff.SetAction(parse => NotImplemented(parse, logger, "diff", "STEP-10"));
        root.Subcommands.Add(diff);

        root.Subcommands.Add(DoctorCommand.Build(host, logger));
        root.Subcommands.Add(GraphCommand.Build(host, logger));
        root.Subcommands.Add(ContractCommands.BuildContract(host, logger));
        root.Subcommands.Add(ContractCommands.A11y(host, logger));
        return root;
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CliHost host, CancellationToken ct = default)
    {
        var root = BuildRootCommand(host);
        var parse = root.Parse(args);
        return await parse.InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, ct);
    }

    /// <summary>Informational version of MapCue itself, e.g. <c>0.1.0-alpha.1+&lt;commit&gt;</c>.</summary>
    public static string Version { get; } =
        typeof(MapCueCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MapCueCli).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static Command Placeholder(string name, string description, string plannedStep, ILogger logger)
    {
        var command = new Command(name, description);
        command.SetAction(parse => NotImplemented(parse, logger, name, plannedStep));
        return command;
    }

    private static int NotImplemented(ParseResult parse, ILogger logger, string command, string plannedStep)
    {
        parse.InvocationConfiguration.Error.WriteLine($"mapcue {command}: not implemented (planned in {plannedStep}).");
        logger.LogInformation("Command {Command} is not implemented (planned in {PlannedStep})", command, plannedStep);
        return ExitCodes.NotImplemented;
    }

    private sealed class PrintVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(Version);
            return ExitCodes.Success;
        }
    }
}

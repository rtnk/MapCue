using System.CommandLine;
using MapCue.Adapters.TapCue;
using MapCue.Application.Configuration;
using MapCue.Cli.Configuration;
using MapCue.Domain;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli;

/// <summary>
/// <c>mapcue doctor</c>: checks TapCue (executable, version policy, clean environment, MCP handshake and required
/// tools), connected allowlisted devices and, with <c>--device</c>, reads the current screen. It never acts on a device.
/// Without a configuration file the default TapCue settings are used and every device is allowed.
/// </summary>
internal static class DoctorCommand
{
    public static Command Build(CliHost host, ILogger logger)
    {
        var configOption = new Option<FileInfo?>("--config", "-c")
        {
            Description = $"Path to the MapCue configuration (YAML). Default: {MapCueCli.DefaultConfigPath} if it exists.",
        };
        var deviceOption = new Option<string?>("--device")
        {
            Description = "Also read the current screen of this device (read-only).",
        };
        var command = new Command("doctor", "Check TapCue, devices and environment prerequisites.");
        command.Options.Add(configOption);
        command.Options.Add(deviceOption);
        command.SetAction(async (parse, ct) =>
        {
            var output = parse.InvocationConfiguration.Output;
            var error = parse.InvocationConfiguration.Error;
            var explicitConfig = parse.GetValue(configOption);
            var configFile = explicitConfig ?? new FileInfo(MapCueCli.DefaultConfigPath);
            MapCueSettings? settings = null;
            if (explicitConfig is not null || configFile.Exists)
            {
                var loaded = MapCueConfigLoader.LoadFile(configFile.FullName);
                if (!loaded.IsValid)
                {
                    error.WriteLine($"mapcue doctor: configuration '{configFile.Name}' is invalid:");
                    foreach (var e in loaded.Errors)
                    {
                        error.WriteLine($"  {e}");
                    }

                    return ExitCodes.ConfigInvalid;
                }

                settings = loaded.Settings;
            }

            var options = settings is null
                ? new TapCueAdapterOptions(TapCueSettings.Default, Budgets.Default, [], [], Path.Combine(MapCueSettings.DefaultWorkspace, "tmp", "tapcue"), MapCueCli.Version)
                : TapCueAdapterOptions.FromSettings(settings, MapCueCli.Version);
            var device = parse.GetValue(deviceOption) is { Length: > 0 } id ? new DeviceId(id) : (DeviceId?)null;

            var checks = new List<DoctorCheck>
            {
                settings is null
                    ? new("config", DoctorStatus.Skip, $"{MapCueCli.DefaultConfigPath} not found; using default TapCue settings, all devices allowed")
                    : new("config", DoctorStatus.Pass, configFile.Name),
            };
            await using (var adapter = new TapCueDeviceAutomation(options, host.Redactor, host.Clock, logger, parentEnvironment: host.Environment))
            {
                checks.AddRange(await TapCueDoctor.RunAsync(adapter, options, host.Environment, host.EnvironmentNames(), device, ct));
            }

            foreach (var check in checks)
            {
                // Details may contain tool output; they go through the redactor like logs do.
                var detail = host.Redactor.Redact(check.Detail, new("console")).Text;
                output.WriteLine($"{Label(check.Status),-5} {check.Name,-20} {detail}");
            }

            var failed = checks.Count(c => c.Status == DoctorStatus.Fail);
            logger.LogInformation("Doctor finished with {Failed} failing check(s)", failed);
            return failed == 0 ? ExitCodes.Success : ExitCodes.PrerequisitesFailed;
        });
        return command;
    }

    private static string Label(DoctorStatus status) => status switch
    {
        DoctorStatus.Pass => "PASS",
        DoctorStatus.Warn => "WARN",
        DoctorStatus.Fail => "FAIL",
        _ => "SKIP",
    };
}

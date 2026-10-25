using MapCue.Adapters.TapCue.Parsing;
using MapCue.Adapters.TapCue.Processes;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue;

public enum DoctorStatus
{
    Pass,
    Warn,
    Fail,
    Skip,
}

public sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail);

/// <summary>
/// Prerequisite checks behind <c>mapcue doctor</c>: executable, version policy, environment hygiene, MCP handshake
/// with required tools, connected devices and (optionally) a read-only screen probe. Never runs an action.
/// Later checks are skipped when an earlier one makes them meaningless.
/// </summary>
public static class TapCueDoctor
{
    public static async Task<IReadOnlyList<DoctorCheck>> RunAsync(
        TapCueDeviceAutomation adapter,
        TapCueAdapterOptions options,
        Func<string, string?> parentEnvironment,
        IEnumerable<string> parentVariableNames,
        DeviceId? probeDevice,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parentEnvironment);
        var checks = new List<DoctorCheck>();

        var path = ExecutableResolver.Resolve(options.TapCue.Executable, parentEnvironment("PATH"));
        if (path is null)
        {
            checks.Add(new("tapcue.executable", DoctorStatus.Fail, $"'{options.TapCue.Executable}' not found (set tapcue.executable or add it to PATH)"));
            return checks;
        }

        checks.Add(new("tapcue.executable", DoctorStatus.Pass, path));

        try
        {
            var (version, status) = await adapter.PreflightAsync(ct).ConfigureAwait(false);
            checks.Add(status == TapCueVersionStatus.Supported
                ? new("tapcue.version", DoctorStatus.Pass, $"{version.Version} (supported)")
                : new("tapcue.version", DoctorStatus.Warn, $"{version.Version} is untested; runs are flagged untestedAdapter"));
        }
        catch (Exception ex) when (ex is TapCueException or TapCueFormatException)
        {
            checks.Add(new("tapcue.version", DoctorStatus.Fail, ex.Message));
            return checks;
        }

        var stripped = parentVariableNames.Count(ProcessEnvironment.IsForbidden);
        checks.Add(stripped == 0
            ? new("tapcue.environment", DoctorStatus.Pass, $"clean environment with {adapter.ChildEnvironment.Count} allowlisted variable(s)")
            : new("tapcue.environment", DoctorStatus.Warn, $"{stripped} MAESTRO_* variable(s) in this shell are not passed to TapCue (G-06)"));

        try
        {
            var tools = await adapter.ProbeMcpAsync(ct).ConfigureAwait(false);
            var info = adapter.ServerInfo;
            checks.Add(new("tapcue.mcp", DoctorStatus.Pass, $"protocol {info?.ProtocolVersion}, tools: {string.Join(", ", tools.Select(t => t.Name).Order(StringComparer.Ordinal))}"));
        }
        catch (TapCueException ex)
        {
            checks.Add(new("tapcue.mcp", DoctorStatus.Fail, ex.Message));
            return checks;
        }

        try
        {
            var devices = await adapter.ListDevicesAsync(ct).ConfigureAwait(false);
            var usable = devices.Where(d => d.IsConnected && options.IsDeviceAllowed(d.Id.Value)).Select(d => $"{d.Id} ({d.Platform.ToString().ToLowerInvariant()})").ToList();
            checks.Add(usable.Count > 0
                ? new("devices", DoctorStatus.Pass, string.Join(", ", usable))
                : new("devices", DoctorStatus.Fail, $"no connected allowlisted device ({devices.Count} reported); boot an emulator or simulator"));
        }
        catch (Exception ex) when (ex is TapCueException or TapCueFormatException)
        {
            checks.Add(new("devices", DoctorStatus.Fail, ex.Message));
            return checks;
        }

        if (probeDevice is { } device)
        {
            try
            {
                var count = await adapter.ProbeScreenAsync(device, ct).ConfigureAwait(false);
                checks.Add(new("device.inspect", DoctorStatus.Pass, $"{device}: {count} element(s) on screen"));
            }
            catch (Exception ex) when (ex is TapCueException or TapCueFormatException)
            {
                checks.Add(new("device.inspect", DoctorStatus.Fail, $"{device}: {ex.Message}"));
            }
        }

        return checks;
    }
}

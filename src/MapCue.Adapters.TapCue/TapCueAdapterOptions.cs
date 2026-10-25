using System.Text.RegularExpressions;
using MapCue.Application.Configuration;

namespace MapCue.Adapters.TapCue;

/// <summary>
/// Everything the adapter needs from configuration. Timeouts come from the run budgets (architecture §10.2);
/// the first call on a device in a fresh MCP session additionally gets <see cref="TapCueSettings.StartupTimeout"/>
/// because TapCue installs its driver then.
/// </summary>
public sealed record TapCueAdapterOptions(
    TapCueSettings TapCue,
    Budgets Budgets,
    IReadOnlyList<string> AllowedAppIds,
    IReadOnlyList<string> AllowedDeviceIds,
    string WorkDirectory,
    string ClientVersion)
{
    public static TapCueAdapterOptions FromSettings(MapCueSettings settings, string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TapCueAdapterOptions(
            settings.TapCue,
            settings.Budgets,
            settings.Allowlist.AppIds,
            settings.Allowlist.DeviceIds,
            Path.Combine(settings.Workspace, "tmp", "tapcue"),
            clientVersion);
    }

    /// <summary>True when <paramref name="deviceId"/> matches the device allowlist (empty = any; <c>*</c> is a wildcard).</summary>
    public bool IsDeviceAllowed(string deviceId) =>
        AllowedDeviceIds.Count == 0 || AllowedDeviceIds.Any(p => Matches(p, deviceId));

    public bool IsAppAllowed(string appId) => AllowedAppIds.Contains(appId, StringComparer.Ordinal);

    private static bool Matches(string pattern, string value) =>
        Regex.IsMatch(value, "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

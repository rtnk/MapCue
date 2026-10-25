using MapCue.Application.Configuration;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue;

public enum TapCueVersionStatus
{
    Supported,

    /// <summary>Not in <c>supportedVersions</c> but allowed by <c>allowUntestedVersion</c>; the run is flagged <c>untestedAdapter</c>.</summary>
    Untested,
    Unsupported,
}

/// <summary>Version preflight (docs/tapcue-compatibility.md §8). The version comes from <c>tapcue --version</c>, never from MCP <c>serverInfo</c> (G-07).</summary>
public static class TapCueVersionPolicy
{
    public const string ToolName = "tapcue";

    public static TapCueVersionStatus Evaluate(ToolVersion version, TapCueSettings settings)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(settings);
        if (!string.Equals(version.Name, ToolName, StringComparison.Ordinal))
        {
            return TapCueVersionStatus.Unsupported;
        }

        if (settings.SupportedVersions.Contains(version.Version, StringComparer.Ordinal))
        {
            return TapCueVersionStatus.Supported;
        }

        return settings.AllowUntestedVersion ? TapCueVersionStatus.Untested : TapCueVersionStatus.Unsupported;
    }
}

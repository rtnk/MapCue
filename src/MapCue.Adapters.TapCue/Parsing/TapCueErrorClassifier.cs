using System.Text.RegularExpressions;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.Parsing;

/// <summary>
/// Classifies TapCue error text (MCP <c>isError</c> results and CLI stderr; G-08). The table is pinned by
/// contract tests on recorded fixtures for each supported TapCue version. Unrecognised text is
/// <see cref="DeviceErrorKind.Unknown"/>, never a guess.
/// </summary>
public static partial class TapCueErrorClassifier
{
    private static readonly (Regex Pattern, DeviceErrorKind Kind, bool ReachedDevice)[] _rules =
    [
        (DeviceNotConnected(), DeviceErrorKind.DeviceNotConnected, false),
        (DeviceNotSupported(), DeviceErrorKind.DeviceNotSupported, false),
        (InvalidArguments(), DeviceErrorKind.InvalidRequest, false),
        (ToolNotFound(), DeviceErrorKind.ToolNotFound, false),
        (Syntax(), DeviceErrorKind.FlowSyntax, false),
        (ElementNotFound(), DeviceErrorKind.ElementNotFound, true),
        (AssertionFailed(), DeviceErrorKind.AssertionFailed, true),
        (NotInstalled(), DeviceErrorKind.AppLaunchFailed, false),
        (LaunchFailed(), DeviceErrorKind.AppLaunchFailed, true),
        (AppCrashed(), DeviceErrorKind.AppCrashed, true),
        (DriverTimeout(), DeviceErrorKind.DriverTimeout, true),
    ];

    public static DeviceErrorKind Classify(string? text) => ClassifyDetailed(text).Kind;

    /// <summary>
    /// Returns the kind and whether the failure implies that commands already reached the device. Errors raised
    /// while parsing arguments or opening the device session prove that nothing was executed.
    /// </summary>
    public static (DeviceErrorKind Kind, bool ReachedDevice) ClassifyDetailed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (DeviceErrorKind.Unknown, true);
        }

        foreach (var (pattern, kind, reached) in _rules)
        {
            if (pattern.IsMatch(text))
            {
                return (kind, reached);
            }
        }

        return (DeviceErrorKind.Unknown, true);
    }

    [GeneratedRegex(@"Device with id .{1,256} is not connected", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNotConnected();

    [GeneratedRegex(@"is not supported by the MCP server|Web platform is not supported|Unsupported device type", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceNotSupported();

    [GeneratedRegex(@"^(device_id is required|Exactly one of `yaml`, `files`, or `dir` must be provided|`yaml`, `files`, and `dir` are mutually exclusive|`files` must contain at least one path|`include_tags` / `exclude_tags` are only valid with `dir`|Files not found:)", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidArguments();

    [GeneratedRegex(@"^Tool .{1,128} not found$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNotFound();

    [GeneratedRegex(@"Invalid Command|Invalid command|SyntaxError|Unknown key name|Unknown Property|Parsing Failed|at /syntax-checker:\d+:\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Syntax();

    [GeneratedRegex(@"Element not found", RegexOptions.CultureInvariant)]
    private static partial Regex ElementNotFound();

    [GeneratedRegex(@"Assertion is false", RegexOptions.CultureInvariant)]
    private static partial Regex AssertionFailed();

    [GeneratedRegex(@"Package .{1,256} is not installed", RegexOptions.CultureInvariant)]
    private static partial Regex NotInstalled();

    [GeneratedRegex(@"Unable to launch app", RegexOptions.CultureInvariant)]
    private static partial Regex LaunchFailed();

    [GeneratedRegex(@"App crashed|crashed or was killed", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AppCrashed();

    [GeneratedRegex(@"Maestro driver timed out|Maestro driver did not start|DriverTimeout", RegexOptions.CultureInvariant)]
    private static partial Regex DriverTimeout();
}

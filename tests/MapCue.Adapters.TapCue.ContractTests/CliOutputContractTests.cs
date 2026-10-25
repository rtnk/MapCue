using MapCue.Adapters.TapCue.Cli;
using MapCue.Adapters.TapCue.Parsing;
using MapCue.Adapters.TapCue.Processes;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;

namespace MapCue.Adapters.TapCue.ContractTests;

/// <summary>CLI outputs of TapCue 0.1.0 recorded on Linux x86_64 (docs/handovers/STEP-02.md, "Fixtures").</summary>
public class CliOutputContractTests
{
    // `tapcue --version` stdout, byte for byte (od -c of the real output).
    private const string RecordedVersion = "tapcue 0.1.0\nbased on Maestro (Apache-2.0), not affiliated with mobile.dev\n";

    [Fact]
    public void Version_output_parses()
    {
        var version = TapCueParsers.ParseVersion(RecordedVersion);
        Assert.Equal(new ToolVersion("tapcue", "0.1.0"), version);
        Assert.Equal(TapCueVersionStatus.Supported, TapCueVersionPolicy.Evaluate(version, TapCueSettings.Default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("maestro version 1.40.0\n")]
    [InlineData("based on Maestro (Apache-2.0)\n")]
    public void Unexpected_version_output_is_a_format_error(string stdout)
    {
        Assert.Throws<TapCueFormatException>(() => TapCueParsers.ParseVersion(stdout));
    }

    [Fact]
    public void Version_policy_rejects_other_versions_unless_untested_is_allowed()
    {
        var other = new ToolVersion("tapcue", "0.2.0");
        Assert.Equal(TapCueVersionStatus.Unsupported, TapCueVersionPolicy.Evaluate(other, TapCueSettings.Default));
        Assert.Equal(TapCueVersionStatus.Untested, TapCueVersionPolicy.Evaluate(other, TapCueSettings.Default with { AllowUntestedVersion = true }));
        Assert.Equal(TapCueVersionStatus.Unsupported, TapCueVersionPolicy.Evaluate(new ToolVersion("maestro", "0.1.0"), TapCueSettings.Default));
    }

    [Fact]
    public void Check_syntax_failure_is_read_from_stderr_without_jvm_banners()
    {
        // Recorded: exit 1, empty stdout line, stderr with the JVM banner and the position of the error.
        var result = new ProcessResult(1, "\n", "Picked up JAVA_TOOL_OPTIONS: -Dx=y\nInvalid Command: tapOnn at /syntax-checker:3:9\n", false, false, TimeSpan.Zero);
        var lines = TapCueCliRunner.ErrorLines(result);
        Assert.Equal(["Invalid Command: tapOnn at /syntax-checker:3:9"], lines);
        Assert.Equal(DeviceErrorKind.FlowSyntax, TapCueErrorClassifier.Classify(lines[0]));
    }

    [Fact]
    public void Missing_flow_file_is_an_invalid_request()
    {
        var result = new ProcessResult(1, string.Empty, "\nFile does not exist: /tmp/nope.yaml\n", false, false, TimeSpan.Zero);
        Assert.Equal(["File does not exist: /tmp/nope.yaml"], TapCueCliRunner.ErrorLines(result));
    }

    [Theory]
    [InlineData("Element not found: Text matching regex: Nope", DeviceErrorKind.ElementNotFound, true)]
    [InlineData("Assertion is false: \"Settings\" is visible", DeviceErrorKind.AssertionFailed, true)]
    [InlineData("Failed to run flow: Package com.example is not installed", DeviceErrorKind.AppLaunchFailed, false)]
    [InlineData("Maestro driver timed out during viewHierarchy call with: x", DeviceErrorKind.DriverTimeout, true)]
    [InlineData("Device emulator-5554 (ANDROID/REAL) is not supported by the MCP server", DeviceErrorKind.DeviceNotSupported, false)]
    [InlineData("Failed to inspect screen: Device server died during 'deviceInfo' on emulator-5554 (10708ms since last byte, connection age 31407ms): StatusRuntimeException: UNAVAILABLE", DeviceErrorKind.DriverLost, true)]
    [InlineData("Something completely new", DeviceErrorKind.Unknown, true)]
    [InlineData("", DeviceErrorKind.Unknown, true)]
    public void Error_table_from_tapcue_sources(string text, DeviceErrorKind kind, bool reached)
    {
        // Messages taken from TapCue 0.1.0 sources (Orchestra.kt, AndroidDriver.kt, IOSDriver.kt, McpMaestroSessionManager.kt);
        // the "Device server died" text was observed verbatim in the STEP-02 Android emulator smoke.
        Assert.Equal((kind, reached), TapCueErrorClassifier.ClassifyDetailed(text));
    }
}

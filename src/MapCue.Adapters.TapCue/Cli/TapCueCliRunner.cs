using MapCue.Adapters.TapCue.Processes;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.Cli;

/// <summary>
/// Runs the TapCue CLI commands MapCue uses (ADR-0002): <c>--version</c>, <c>check-syntax</c>,
/// <c>hierarchy --no-reinstall-driver</c> and <c>test --test-output-dir</c>. Every call is a separate JVM with a
/// clean environment and a hard timeout that kills the process tree (<c>hierarchy</c> hangs without a device, G-10).
/// Arguments are passed as an argument vector, never through a shell.
/// </summary>
public sealed class TapCueCliRunner(string executablePath, IReadOnlyDictionary<string, string> environment, string? workingDirectory = null)
{
    public string ExecutablePath { get; } = executablePath;

    public Task<ProcessResult> VersionAsync(TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["--version"], timeout, ct);

    public Task<ProcessResult> CheckSyntaxAsync(string flowPath, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["check-syntax", flowPath], timeout, ct);

    public Task<ProcessResult> HierarchyAsync(DeviceId device, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(["--device", device.Value, "hierarchy", "--no-reinstall-driver"], timeout, ct);

    /// <summary>Runs a flow with artifacts (<c>manifest.json</c>, <c>commands.json</c>, <c>logs/</c>) written into <paramref name="testOutputDirectory"/>.</summary>
    public Task<ProcessResult> TestAsync(DeviceId device, string flowPath, string testOutputDirectory, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(
            [
                "--device", device.Value, "test", "--no-reinstall-driver",
                "--format", "junit", "--output", Path.Combine(testOutputDirectory, "report.xml"),
                "--test-output-dir", testOutputDirectory, flowPath,
            ],
            timeout,
            ct);

    /// <summary>Error lines of a failed CLI call: stderr without JVM banners, falling back to stdout.</summary>
    public static IReadOnlyList<string> ErrorLines(ProcessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        static List<string> Lines(string text) => text.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("Picked up ", StringComparison.Ordinal))
            .ToList();
        var lines = Lines(result.StandardError);
        return lines.Count > 0 ? lines : Lines(result.StandardOutput);
    }

    private Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) =>
        ProcessRunner.RunAsync(new ProcessSpec(ExecutablePath, arguments, environment, workingDirectory), timeout, ct);
}

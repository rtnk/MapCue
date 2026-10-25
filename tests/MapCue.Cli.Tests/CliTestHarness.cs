using MapCue.Testing;
using Microsoft.Extensions.Logging;

namespace MapCue.Cli.Tests;

internal static class CliTestHarness
{
    public static async Task<(int ExitCode, string Output, string Error, string Log)> RunAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var log = new StringWriter();
        int exitCode;
        using (var host = new CliHost(log, new ManualClock(), LogLevel.Trace))
        {
            exitCode = await MapCueCli.RunAsync(args, output, error, host);
        }

        return (exitCode, Normalize(output.ToString()), Normalize(error.ToString()), log.ToString());
    }

    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MapCue.sln")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("MapCue.sln not found above test output."), relative);
    }
}

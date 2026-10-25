using System.Text;

namespace MapCue.Testing;

/// <summary>
/// Builds a <c>tapcue</c> launcher script around the fake server (<c>MapCue.Adapters.TapCue.FakeServer</c>) for
/// integration tests. The script is a real executable on disk, so the adapter starts it exactly like TapCue:
/// as a child process with a clean environment. Unix only (TapCue itself is macOS/Linux only).
/// </summary>
public sealed class FakeTapCue : IDisposable
{
    public const string ServerAssembly = "MapCue.Adapters.TapCue.FakeServer.dll";

    private FakeTapCue(string directory, string executable, string stateDirectory)
    {
        Directory = directory;
        Executable = executable;
        StateDirectory = stateDirectory;
    }

    public string Directory { get; }

    /// <summary>Absolute path of the launcher script (use as <c>tapcue.executable</c>).</summary>
    public string Executable { get; }

    /// <summary>Where the fake server writes <c>env.txt</c>, <c>calls.txt</c> and <c>child.pid</c>.</summary>
    public string StateDirectory { get; }

    public static FakeTapCue Create(string? fixture = null, string? hierarchy = null, params string[] scenarios)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mapcue-fake-tapcue-" + Guid.NewGuid().ToString("N"));
        var state = Path.Combine(dir, "state");
        System.IO.Directory.CreateDirectory(state);
        var dotnet = System.Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : System.Environment.ProcessPath ?? "dotnet";
        var server = Path.Combine(AppContext.BaseDirectory, ServerAssembly);
        if (!File.Exists(server))
        {
            throw new FileNotFoundException("Fake TapCue server is not next to the tests; reference the FakeServer project.", server);
        }

        var args = new StringBuilder();
        args.Append(" --state-dir ").Append(Quote(state));
        if (fixture is not null)
        {
            args.Append(" --fixture ").Append(Quote(fixture));
        }

        if (hierarchy is not null)
        {
            args.Append(" --hierarchy ").Append(Quote(hierarchy));
        }

        foreach (var scenario in scenarios)
        {
            args.Append(" --scenario ").Append(Quote(scenario));
        }

        var script = Path.Combine(dir, "tapcue");
        File.WriteAllText(script, $"#!/bin/sh\nexec {Quote(dotnet)} {Quote(server)}{args} -- \"$@\"\n");
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The fake TapCue launcher is a POSIX shell script.");
        }

        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FakeTapCue(dir, script, state);
    }

    /// <summary>Tool names of the MCP calls the fake server received, in order.</summary>
    public IReadOnlyList<string> Calls()
    {
        var path = Path.Combine(StateDirectory, "calls.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    /// <summary>The environment the most recent fake process saw (<c>NAME=value</c> lines).</summary>
    public IReadOnlyList<string> ObservedEnvironment() => File.ReadAllLines(Path.Combine(StateDirectory, "env.txt"));

    /// <summary>PID of the grandchild the fake started before hanging, if any.</summary>
    public int? ChildPid()
    {
        var path = Path.Combine(StateDirectory, "child.pid");
        return File.Exists(path) ? int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; temp files.
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

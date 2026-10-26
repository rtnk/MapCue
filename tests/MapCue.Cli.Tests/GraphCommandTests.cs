using MapCue.Infrastructure.Sqlite;

namespace MapCue.Cli.Tests;

public class GraphCommandTests
{
    private static readonly string _plan = CliTestHarness.RepoFile("tests/Fixtures/graph-import/android-settings-cycle.json");

    private static string NewWorkspace() => Path.Combine(Path.GetTempPath(), "mapcue-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Import_of_recorded_android_snapshots_is_idempotent()
    {
        var workspace = NewWorkspace();

        var first = await CliTestHarness.RunAsync("graph", "import", _plan, "--workspace", workspace);
        var second = await CliTestHarness.RunAsync("graph", "import", _plan, "-w", workspace);

        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.Equal(ExitCodes.Success, second.ExitCode);
        Assert.Contains("new-states=2 new-variants=2 new-tasks=4 already-committed=0", first.Output, StringComparison.Ordinal);
        Assert.Contains("new-states=0 new-variants=0 new-tasks=0 already-committed=2", second.Output, StringComparison.Ordinal);

        string Graph(string output) => output.Split('\n').Single(l => l.StartsWith("graph ", StringComparison.Ordinal));
        Assert.Equal("graph      contexts=1 screens=2 states=2 variants=2 edges=2 observations=2 tasks=4 events=18", Graph(first.Output));
        Assert.Equal(Graph(first.Output), Graph(second.Output));
        string States(string output) => string.Join('\n', output.Split('\n').Where(l => l.StartsWith("state ", StringComparison.Ordinal)));
        Assert.Equal(States(first.Output), States(second.Output));
        Assert.True(File.Exists(Path.Combine(workspace, "mapcue.db")));
        Assert.True(Directory.Exists(Path.Combine(workspace, "blobs", "sha256")));
    }

    [Fact]
    public async Task Invalid_plan_exits_with_input_invalid()
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(workspace);
        var plan = Path.Combine(workspace, "plan.json");
        await File.WriteAllTextAsync(plan, """{ "schemaVersion": "mapcue.graph-import/0" }""");

        var (exitCode, _, error, _) = await CliTestHarness.RunAsync("graph", "import", plan, "-w", workspace);

        Assert.Equal(ExitCodes.InputInvalid, exitCode);
        Assert.Contains("schemaVersion must be 'mapcue.graph-import/1'", error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(workspace, "mapcue.db")));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": "mapcue.graph-import/1", "context": { "profileId": "p", "platform": "symbian", "appId": "a", "appBuildId": "1" }, "observations": [] }""", "context.platform")]
    [InlineData("""{ "schemaVersion": "mapcue.graph-import/1", "context": { "profileId": "p", "platform": "android", "appId": "a", "appBuildId": "1" }, "observations": [ { "id": "x", "file": "missing.json", "format": "tapcue-inspect-screen" } ] }""", "cannot read 'missing.json'")]
    [InlineData("""{ "schemaVersion": "mapcue.graph-import/1", "context": { "profileId": "p", "platform": "android", "appId": "a", "appBuildId": "1" }, "observations": [], "transitions": [ { "from": "x", "action": { "type": "fly" } } ] }""", "action.type")]
    [InlineData("not json", "Cannot read plan")]
    public async Task Plan_errors_are_reported(string json, string message)
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(workspace);
        var plan = Path.Combine(workspace, "plan.json");
        await File.WriteAllTextAsync(plan, json);

        var (exitCode, _, error, _) = await CliTestHarness.RunAsync("graph", "import", plan, "-w", workspace);

        Assert.Equal(ExitCodes.InputInvalid, exitCode);
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transition_to_an_unknown_observation_is_input_invalid()
    {
        var workspace = NewWorkspace();
        Directory.CreateDirectory(workspace);
        var elements = Path.Combine(workspace, "home.json");
        await File.WriteAllTextAsync(elements, """[ { "resourceId": "app:id/ok", "className": "android.widget.Button", "clickable": true, "enabled": true } ]""");
        var plan = Path.Combine(workspace, "plan.json");
        await File.WriteAllTextAsync(plan, """
            { "schemaVersion": "mapcue.graph-import/1",
              "context": { "profileId": "p", "platform": "android", "appId": "a", "appBuildId": "1" },
              "observations": [ { "id": "home", "file": "home.json", "format": "mapcue-elements" } ],
              "transitions": [ { "from": "home", "action": { "type": "tap", "resourceId": "app:id/ok" }, "to": "nowhere" } ] }
            """);

        var (exitCode, _, error, _) = await CliTestHarness.RunAsync("graph", "import", plan, "-w", workspace);

        Assert.Equal(ExitCodes.InputInvalid, exitCode);
        Assert.Contains("unknown observation", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locked_workspace_exits_with_workspace_unavailable()
    {
        var workspace = NewWorkspace();
        using var held = WorkspaceLock.Acquire(workspace);

        var (exitCode, _, error, _) = await CliTestHarness.RunAsync("graph", "import", _plan, "-w", workspace);

        Assert.Equal(ExitCodes.WorkspaceUnavailable, exitCode);
        Assert.Contains("held by another MapCue process", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("graph", "--help")]
    [InlineData("graph", "import", "--help")]
    public async Task Graph_help_succeeds(params string[] args)
    {
        var (exitCode, output, _, _) = await CliTestHarness.RunAsync(args);
        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Contains("Usage:", output, StringComparison.Ordinal);
    }
}

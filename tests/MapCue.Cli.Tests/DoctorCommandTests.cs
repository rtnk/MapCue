using MapCue.Testing;

namespace MapCue.Cli.Tests;

/// <summary><c>mapcue doctor</c> against the fake TapCue process (no device, no real TapCue).</summary>
public sealed class DoctorCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("mapcue-doctor-");

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "tapcue-0.1.0", name);

    private static Dictionary<string, string> Env(params (string Key, string Value)[] extra)
    {
        var env = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Path.GetTempPath(),
        };
        foreach (var (k, v) in extra)
        {
            env[k] = v;
        }

        return env;
    }

    private string Config(string executable, string allowlistExtra = "")
    {
        var path = Path.Combine(_dir.FullName, "mapcue.yaml");
        File.WriteAllText(path, $"schemaVersion: mapcue.config/1\nworkspace: {Path.Combine(_dir.FullName, ".mapcue")}\nallowlist:\n  appIds: [com.android.settings]\n  environments: [test]\n{allowlistExtra}tapcue:\n  executable: {executable}\n");
        return path;
    }

    [Fact]
    public async Task All_checks_pass_with_a_connected_device()
    {
        using var fake = FakeTapCue.Create(Fixture("mcp-android-settings.synthetic.jsonl"));
        var (exit, output, error, _) = await CliTestHarness.RunWithEnvironmentAsync(Env(("MAESTRO_CLOUD_API_KEY", "mk-secret")), "doctor", "--config", Config(fake.Executable), "--device", "emulator-5554");

        Assert.Equal(string.Empty, error);
        Assert.Equal(ExitCodes.Success, exit);
        var lines = output.TrimEnd('\n').Split('\n');
        Assert.Equal(["config", "tapcue.executable", "tapcue.version", "tapcue.environment", "tapcue.mcp", "devices", "device.inspect"], lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]));
        Assert.StartsWith("PASS  tapcue.version       0.1.0 (supported)", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("WARN  tapcue.environment   1 MAESTRO_* variable(s)", lines[3], StringComparison.Ordinal);
        Assert.Contains("inspect_screen, list_devices, run", lines[4], StringComparison.Ordinal);
        Assert.Equal("PASS  devices              emulator-5554 (android)", lines[5]);
        Assert.Equal("PASS  device.inspect       emulator-5554: 16 element(s) on screen", lines[6]);
        Assert.DoesNotContain("mk-secret", output, StringComparison.Ordinal);
        Assert.DoesNotContain(fake.ObservedEnvironment(), l => l.StartsWith("MAESTRO_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_device_fails_with_prerequisites_exit_code()
    {
        using var fake = FakeTapCue.Create(Fixture("mcp-no-device.jsonl"));
        var (exit, output, _, _) = await CliTestHarness.RunWithEnvironmentAsync(Env(), "doctor", "-c", Config(fake.Executable));

        Assert.Equal(ExitCodes.PrerequisitesFailed, exit);
        Assert.Contains("FAIL  devices              no connected allowlisted device (0 reported)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Device_outside_the_allowlist_is_not_usable()
    {
        using var fake = FakeTapCue.Create(Fixture("mcp-android-settings.synthetic.jsonl"));
        var config = Config(fake.Executable, "  deviceIds: [R58*]\n");
        var (exit, output, _, _) = await CliTestHarness.RunWithEnvironmentAsync(Env(), "doctor", "-c", config);
        Assert.Equal(ExitCodes.PrerequisitesFailed, exit);
        Assert.Contains("FAIL  devices              no connected allowlisted device (2 reported)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_version_stops_further_checks()
    {
        using var fake = FakeTapCue.Create(Fixture("mcp-android-settings.synthetic.jsonl"), null, "version:9.9.9");
        var (exit, output, _, _) = await CliTestHarness.RunWithEnvironmentAsync(Env(), "doctor", "-c", Config(fake.Executable));
        Assert.Equal(ExitCodes.PrerequisitesFailed, exit);
        Assert.Contains("FAIL  tapcue.version       TapCue 9.9.9 is not in tapcue.supportedVersions (0.1.0)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("tapcue.mcp", output, StringComparison.Ordinal);
        Assert.Empty(fake.Calls());
    }

    [Fact]
    public async Task Missing_executable_without_config_uses_path_lookup_only()
    {
        var (exit, output, _, _) = await CliTestHarness.RunWithEnvironmentAsync(new Dictionary<string, string> { ["PATH"] = _dir.FullName }, "doctor");
        Assert.Equal(ExitCodes.PrerequisitesFailed, exit);
        Assert.Contains("SKIP  config               mapcue.yaml not found", output, StringComparison.Ordinal);
        Assert.Contains("FAIL  tapcue.executable    'tapcue' not found (set tapcue.executable or add it to PATH)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_config_is_a_config_error()
    {
        var path = Path.Combine(_dir.FullName, "bad.yaml");
        File.WriteAllText(path, "schemaVersion: mapcue.config/1\nallowlist:\n  appIds: [a]\n  environments: [test]\ntapcue:\n  passEnvironment: [MAESTRO_X]\n");
        var (exit, _, error, _) = await CliTestHarness.RunWithEnvironmentAsync(Env(), "doctor", "--config", path);
        Assert.Equal(ExitCodes.ConfigInvalid, exit);
        Assert.Contains("CFG061 tapcue.passEnvironment[0]", error, StringComparison.Ordinal);
    }

    public void Dispose() => _dir.Delete(recursive: true);
}

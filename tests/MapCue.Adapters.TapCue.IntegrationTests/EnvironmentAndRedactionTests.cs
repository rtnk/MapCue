using MapCue.Adapters.TapCue.Mcp;
using MapCue.Application.Ports;
using MapCue.Domain;

namespace MapCue.Adapters.TapCue.IntegrationTests;

/// <summary>G-06 / R-13: TapCue starts with a clean environment and echoed env values never leave the adapter unredacted.</summary>
public class EnvironmentAndRedactionTests
{
    private const string Secret = "Pa55w0rd-Zx81-unique";
    private static readonly DeviceId _emulator = new("emulator-5554");

    private sealed class MemoryTranscript : IMcpTranscriptSink
    {
        public List<string> Lines { get; } = [];

        public void Write(TranscriptDirection direction, string line) => Lines.Add(line);
    }

    [Fact]
    public async Task Child_environment_contains_no_maestro_variables_or_parent_secrets()
    {
        var parent = new Dictionary<string, string>
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["HOME"] = Path.GetTempPath(),
            ["MAESTRO_CLOUD_API_KEY"] = "mk-123",
            ["MAESTRO_DRIVER_STARTUP_TIMEOUT"] = "1",
            ["JAVA_TOOL_OPTIONS"] = "-javaagent:/tmp/x.jar",
            ["MAPCUE_DEMO_PASSWORD"] = Secret,
            ["EXTRA_ALLOWED"] = "yes",
        };
        await using var h = AdapterHarness.Create(parentEnvironment: parent, configure: o => o with { TapCue = o.TapCue with { PassEnvironment = ["EXTRA_ALLOWED", "MAESTRO_CLOUD_API_KEY"] } });
        await h.Adapter.ListDevicesAsync(CancellationToken.None);

        var env = h.Fake.ObservedEnvironment();
        Assert.DoesNotContain(env, l => l.StartsWith("MAESTRO_", StringComparison.Ordinal));
        Assert.DoesNotContain(env, l => l.StartsWith("JAVA_TOOL_OPTIONS=", StringComparison.Ordinal));
        Assert.DoesNotContain(env, l => l.Contains(Secret, StringComparison.Ordinal));
        Assert.Contains("EXTRA_ALLOWED=yes", env);
        Assert.Contains(env, l => l.StartsWith("PATH=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Echoed_input_values_are_removed_from_results_and_transcripts()
    {
        var transcript = new MemoryTranscript();
        await using var h = AdapterHarness.Create(
            scenarios: ["echo-env"],
            resolveInput: r => r == "profile.inputs.password" ? Secret : null,
            transcript: transcript);
        var action = new ValidatedDeviceAction("in-1", AdapterHarness.App, ActionType.InputText, RiskClass.Navigational, null, null, "profile.inputs.password");

        var result = await h.Adapter.ExecuteAsync(_emulator, action, CancellationToken.None);

        Assert.Equal(ActionOutcome.Succeeded, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.DoesNotContain(Secret, result.Detail, StringComparison.Ordinal);
        Assert.Contains("MAPCUE_IN_1", result.Detail, StringComparison.Ordinal);

        // The raw transcript does carry the value (it is the wire); a sink must redact before persisting,
        // and the adapter has registered the value with the redactor so it can.
        var redacted = transcript.Lines.Select(l => h.Redactor.Redact(l, new("fixture")).Text).ToList();
        Assert.Contains(transcript.Lines, l => l.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(redacted, l => l.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Generated_flow_files_never_contain_input_values()
    {
        await using var h = AdapterHarness.Create(scenarios: ["echo-env"], resolveInput: _ => Secret);
        var action = new ValidatedDeviceAction("in-2", AdapterHarness.App, ActionType.InputText, RiskClass.Navigational, null, null, "profile.inputs.password");
        await h.Adapter.ExecuteAsync(_emulator, action, CancellationToken.None);
        foreach (var file in Directory.Exists(h.WorkDirectory) ? Directory.GetFiles(h.WorkDirectory, "*", SearchOption.AllDirectories) : [])
        {
            Assert.DoesNotContain(Secret, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }
}

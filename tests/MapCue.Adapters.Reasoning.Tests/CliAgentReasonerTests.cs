using MapCue.Adapters.Reasoning.Cli;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Domain.Secrets;

namespace MapCue.Adapters.Reasoning.Tests;

public sealed class CliAgentReasonerTests : IAsyncLifetime
{
    private const string Image = "registry.example/mapcue-agent:1@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly AgentSettings _container = AgentSettings.Default with { ContainerImage = Image };

    private AgentHarness _h = null!;
    private AgentExplorationRequest _request = null!;

    public async Task InitializeAsync()
    {
        _h = AgentHarness.Create();
        var (_, snapshot) = await _h.CommitAsync();
        _request = AgentHarness.Request(snapshot);
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static string Host(string name) => name switch
    {
        "PATH" => "/usr/bin",
        "HOME" => "/home/op",
        "AWS_SECRET_ACCESS_KEY" => "leak",
        "MAPCUE_DEMO_PASSWORD" => "leak",
        _ => null!,
    };

    private sealed class RecordingLauncher(Func<AgentProcessSpec, AgentProcessResult> run) : IAgentProcessLauncher
    {
        public List<AgentProcessSpec> Started { get; } = [];

        public Task<AgentProcessResult> RunAsync(AgentProcessSpec spec, CancellationToken ct)
        {
            Started.Add(spec);
            return Task.FromResult(run(spec));
        }
    }

    private sealed class Secrets : ISecretResolver
    {
        public SecretValue Resolve(SecretRef reference) => new(reference, "sk-test-value");
    }

    private string Envelope(string? answer = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = answer ?? AgentJson.Serialize(DeterministicFakeReasoner.Answer(_request, "Profile")),
            ["total_cost_usd"] = 0.0123m,
            ["usage"] = new Dictionary<string, long> { ["input_tokens"] = 10, ["cache_read_input_tokens"] = 5, ["output_tokens"] = 7 },
        });

    [Fact]
    public async Task Container_runtime_runs_the_agent_isolated_with_the_request_on_stdin()
    {
        var launcher = new RecordingLauncher(_ => new(AgentProcessOutcome.Exited, 0, Envelope(), string.Empty));
        var reasoner = new CliAgentReasoner(_container with { ApiKey = SecretRef.Parse("secret://env/ANTHROPIC_API_KEY") }, new Secrets(), launcher, Host);

        var result = await reasoner.InterpretAsync(_request, CancellationToken.None);

        Assert.Equal(ReasonerStatus.Succeeded, result.Status);
        Assert.Equal(new ReasonerUsage(1, 15, 7, 0.0123m), result.Usage);
        Assert.Equal("cli:claude:container", reasoner.Name);
        var spec = Assert.Single(launcher.Started);
        Assert.Equal("docker", spec.FileName);
        var args = spec.Arguments.ToList();
        Assert.Equal(["run", "--rm", "-i"], args.Take(3));
        foreach (var flag in new[] { "--read-only", "--cap-drop", "--security-opt", "--pids-limit", "--memory" })
        {
            Assert.Contains(flag, args);
        }

        Assert.DoesNotContain(args, a => a is "-v" or "--volume" or "--mount" or "--privileged" or "--network=host");
        Assert.Equal(Image, args[args.IndexOf("--entrypoint") + 2]);
        Assert.Equal("claude", args[args.IndexOf("--entrypoint") + 1]);
        Assert.Contains("--bare", args);
        var schema = System.Text.Json.Nodes.JsonNode.Parse(args[args.IndexOf("--json-schema") + 1])!.AsObject();
        Assert.False(schema.ContainsKey("$schema")); // the CLI's validator does not know the 2020-12 meta-schema
        Assert.False((bool)schema["additionalProperties"]!);
        Assert.Equal(string.Empty, args[args.IndexOf("--tools") + 1]);
        Assert.Equal(AgentJson.Serialize(_request), spec.StandardInput);

        // The key travels by name only; the host environment is not inherited.
        Assert.Contains("ANTHROPIC_API_KEY", args);
        Assert.DoesNotContain(args, a => a.Contains("sk-test-value", StringComparison.Ordinal));
        Assert.Equal("sk-test-value", spec.Environment["ANTHROPIC_API_KEY"]);
        Assert.DoesNotContain("AWS_SECRET_ACCESS_KEY", spec.Environment.Keys);
        Assert.DoesNotContain("MAPCUE_DEMO_PASSWORD", spec.Environment.Keys);
    }

    [Fact]
    public async Task Container_failure_never_falls_back_to_the_host()
    {
        foreach (var failure in new AgentProcessResult[]
        {
            new(AgentProcessOutcome.FailedToStart, null, string.Empty, "no docker"),
            new(AgentProcessOutcome.Exited, 125, string.Empty, "Cannot connect to the Docker daemon"),
            new(AgentProcessOutcome.Exited, 127, string.Empty, "exec: claude: not found"),
        })
        {
            var launcher = new RecordingLauncher(_ => failure);
            var result = await new CliAgentReasoner(_container, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);

            Assert.Equal(ReasonerStatus.Unavailable, result.Status);
            Assert.Contains("no fallback", result.Detail, StringComparison.Ordinal);
            Assert.All(launcher.Started, s => Assert.Equal("docker", s.FileName));
            Assert.Single(launcher.Started);
        }
    }

    [Fact]
    public async Task Missing_image_or_local_without_opt_in_starts_nothing()
    {
        var launcher = new RecordingLauncher(_ => throw new InvalidOperationException("must not start"));

        var noImage = await new CliAgentReasoner(AgentSettings.Default, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);
        var local = await new CliAgentReasoner(AgentSettings.Default with { Runtime = AgentRuntime.Local }, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);

        Assert.Equal(ReasonerStatus.Unavailable, noImage.Status);
        Assert.Equal(ReasonerStatus.Unavailable, local.Status);
        Assert.Empty(launcher.Started);
    }

    [Fact]
    public async Task Local_runtime_runs_only_with_explicit_opt_in_in_an_empty_directory()
    {
        var launcher = new RecordingLauncher(_ => new(AgentProcessOutcome.Exited, 0, Envelope(), string.Empty));
        var settings = AgentSettings.Default with { Runtime = AgentRuntime.Local, AllowLocalExecution = true };

        var result = await new CliAgentReasoner(settings, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);

        Assert.Equal(ReasonerStatus.Succeeded, result.Status);
        var spec = Assert.Single(launcher.Started);
        Assert.Equal("claude", spec.FileName);
        Assert.Equal("-p", spec.Arguments[0]);
        Assert.NotNull(spec.WorkingDirectory);
        Assert.False(Directory.Exists(spec.WorkingDirectory)); // removed after the call
        Assert.Equal(["HOME", "PATH"], spec.Environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Timeout_kills_the_container_and_reports_timed_out()
    {
        var launcher = new RecordingLauncher(s => s.Arguments[0] == "run" ? new(AgentProcessOutcome.TimedOut, null, string.Empty, string.Empty) : new(AgentProcessOutcome.Exited, 0, string.Empty, string.Empty));

        var result = await new CliAgentReasoner(_container, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);

        Assert.Equal(ReasonerStatus.TimedOut, result.Status);
        Assert.Equal(2, launcher.Started.Count);
        var name = launcher.Started[0].Arguments[launcher.Started[0].Arguments.ToList().IndexOf("--name") + 1];
        Assert.Equal(["kill", name], launcher.Started[1].Arguments);
    }

    [Theory]
    [InlineData("""{"type":"result","is_error":false,"structured_output":{"a":1},"result":"ignored"}""", ReasonerStatus.Succeeded, """{"a":1}""")]
    [InlineData("""{"type":"result","is_error":false,"result":"```json\n{}\n```"}""", ReasonerStatus.Succeeded, "```json\n{}\n```")]
    [InlineData("""{"type":"result","is_error":true,"subtype":"error_max_budget"}""", ReasonerStatus.Unavailable, null)]
    [InlineData("""{"type":"result","is_error":false}""", ReasonerStatus.InvalidOutput, null)]
    [InlineData("Not logged in. Please run /login", ReasonerStatus.InvalidOutput, null)]
    public async Task Envelope_is_read_strictly(string stdout, ReasonerStatus status, string? answer)
    {
        var launcher = new RecordingLauncher(_ => new(AgentProcessOutcome.Exited, 0, stdout, string.Empty));

        var result = await new CliAgentReasoner(_container, new Secrets(), launcher, Host).InterpretAsync(_request, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(answer, result.RawResponse);
    }

    /// <summary>
    /// Real processes: a failing container engine and an agent CLI on PATH that would leave a marker if it ever ran on the host.
    /// The reasoner must report the container failure and never start the host CLI.
    /// </summary>
    [Fact]
    public async Task Isolation_with_real_processes_no_host_fallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var bin = Directory.CreateTempSubdirectory("mapcue-isolation-").FullName;
        try
        {
            var marker = Path.Combine(bin, "host-agent-ran");
            await WriteScriptAsync(Path.Combine(bin, "fake-docker"), "#!/bin/sh\necho 'Cannot connect to the Docker daemon' >&2\nexit 125\n");
            await WriteScriptAsync(Path.Combine(bin, "claude"), $"#!/bin/sh\ntouch '{marker}'\necho '{{}}'\n");
            var settings = _container with { ContainerEngine = Path.Combine(bin, "fake-docker"), Timeout = TimeSpan.FromSeconds(30) };
            string? HostWithFakeBin(string name) => name == "PATH" ? bin + ":/usr/bin:/bin" : Environment.GetEnvironmentVariable(name);

            var result = await new CliAgentReasoner(settings, new Secrets(), new AgentProcessLauncher(), HostWithFakeBin).InterpretAsync(_request, CancellationToken.None);

            Assert.Equal(ReasonerStatus.Unavailable, result.Status);
            Assert.Contains("125", result.Detail, StringComparison.Ordinal);
            Assert.False(File.Exists(marker), "the agent CLI ran on the host");

            // Through the interpreter the task blocks with ReasonerUnavailable and nothing is retried elsewhere.
            var (state, snapshot) = await _h.CommitAsync();
            var interpretation = await _h.Interpreter(new CliAgentReasoner(settings, new Secrets(), new AgentProcessLauncher(), HostWithFakeBin))
                .InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);
            Assert.Equal(AgentInterpretationStatus.ReasonerUnavailable, interpretation.Status);
            Assert.Equal(Domain.Graph.BlockedReasonCode.ReasonerUnavailable, interpretation.BlockedReason);
            Assert.False(File.Exists(marker), "the agent CLI ran on the host");
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    [Fact]
    public async Task Real_process_timeout_kills_the_tree()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var bin = Directory.CreateTempSubdirectory("mapcue-timeout-").FullName;
        try
        {
            await WriteScriptAsync(Path.Combine(bin, "slow"), "#!/bin/sh\nsleep 30\n");
            var started = DateTime.UtcNow;
            var run = await new AgentProcessLauncher().RunAsync(new AgentProcessSpec(Path.Combine(bin, "slow"), [], new Dictionary<string, string> { ["PATH"] = "/usr/bin:/bin" }, "{}", TimeSpan.FromMilliseconds(500)), CancellationToken.None);

            Assert.Equal(AgentProcessOutcome.TimedOut, run.Outcome);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    private static async Task WriteScriptAsync(string path, string content)
    {
        await File.WriteAllTextAsync(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

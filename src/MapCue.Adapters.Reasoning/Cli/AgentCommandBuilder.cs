using System.Text.Json.Nodes;
using MapCue.Application.Configuration;

namespace MapCue.Adapters.Reasoning.Cli;

/// <summary>
/// Builds the agent process (ADR-0004 §4, architecture §11). The request travels on stdin and the answer on stdout; nothing is
/// mounted. Container runtime (default): <c>docker|podman run --rm -i</c> with a read-only root file system, no capabilities,
/// no new privileges, process/memory limits, no volumes, only the API key variable passed by name. Local runtime (explicit
/// opt-in only): the CLI on the host with a clean environment in an empty working directory, all tools and MCP servers off.
/// </summary>
public static class AgentCommandBuilder
{
    public const string SystemPrompt =
        "You are the interpretation component of MapCue, a deterministic mobile app explorer. You receive one JSON request " +
        "(mapcue.agent-exploration-request/1) on stdin. You cannot act on any device and have no tools. Answer with exactly one JSON " +
        "object matching mapcue.agent-exploration-response/1 and the request's instructions. Your answer is validated and may be " +
        "rejected; it is never executed as text.";

    /// <summary>Variables a container engine client may need to reach its daemon; everything else is dropped.</summary>
    public static IReadOnlyList<string> EngineEnvironment { get; } = ["PATH", "HOME", "DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_CONFIG", "XDG_RUNTIME_DIR", "CONTAINER_HOST"];

    /// <summary>Variables the local CLI needs (its own login lives under HOME).</summary>
    public static IReadOnlyList<string> LocalEnvironment { get; } = ["PATH", "HOME", "USER", "LANG", "TMPDIR"];

    /// <summary>
    /// The process for one call. <paramref name="name"/> names the container so a timed-out one can be killed (the engine client
    /// dying does not stop it); <paramref name="apiKey"/> is the resolved key (variable name and value) or null;
    /// <paramref name="hostVariable"/> reads the host environment for the few variables that are passed on.
    /// </summary>
    public static AgentProcessSpec Build(AgentSettings settings, string responseSchema, string requestJson, string name, (string Name, string Value)? apiKey, Func<string, string?> hostVariable, string localWorkingDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(hostVariable);
        var executable = settings.Executable ?? settings.Provider;
        var agentArgs = AgentArguments(settings, responseSchema, bare: settings.Runtime == AgentRuntime.Container || apiKey is not null);

        if (settings.Runtime == AgentRuntime.Container)
        {
            var args = new List<string>
            {
                "run", "--rm", "-i", "--name", name,
                "--read-only", "--tmpfs", "/tmp:rw,nosuid,nodev,size=64m",
                "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                "--pids-limit", "256", "--memory", "1g", "--cpus", "1",
                "--label", "mapcue.agent=1",
                "--env", "HOME=/tmp", "--workdir", "/tmp",
            };
            if (settings.ContainerNetwork is not null)
            {
                args.AddRange(["--network", settings.ContainerNetwork]);
            }

            if (apiKey is { } key)
            {
                // By name only: the engine reads the value from its own environment, so it never appears in the arguments.
                args.AddRange(["--env", key.Name]);
            }

            args.AddRange(["--entrypoint", executable, settings.ContainerImage!]);
            args.AddRange(agentArgs);
            return new AgentProcessSpec(settings.ContainerEngine, args, Environment(EngineEnvironment, hostVariable, apiKey), requestJson, settings.Timeout);
        }

        return new AgentProcessSpec(executable, agentArgs, Environment(LocalEnvironment, hostVariable, apiKey), requestJson, settings.Timeout, localWorkingDirectory);
    }

    /// <summary>
    /// The response schema as handed to the CLI: without <c>$schema</c> and <c>$id</c>, because the CLI's validator does not know
    /// the 2020-12 meta-schema (measured with Claude Code 2.1.310). The keywords used are the same in draft-07; MapCue itself
    /// always validates against the original schema.
    /// </summary>
    public static string ProviderSchema(string schema)
    {
        var root = JsonNode.Parse(schema)!.AsObject();
        root.Remove("$schema");
        root.Remove("$id");
        return root.ToJsonString();
    }

    /// <summary><c>claude -p</c> in print mode with JSON output validated against the response schema [PoC STEP-04].</summary>
    private static List<string> AgentArguments(AgentSettings settings, string responseSchema, bool bare)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "json",
            "--json-schema", ProviderSchema(responseSchema),
            "--system-prompt", SystemPrompt,
            "--tools", string.Empty,
            "--strict-mcp-config",
            "--disable-slash-commands",
            "--no-session-persistence",
        };
        if (bare)
        {
            args.Add("--bare");
        }

        if (settings.Model is not null)
        {
            args.AddRange(["--model", settings.Model]);
        }

        return args;
    }

    private static Dictionary<string, string> Environment(IReadOnlyList<string> names, Func<string, string?> hostVariable, (string Name, string Value)? apiKey)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (hostVariable(name) is { } value)
            {
                environment[name] = value;
            }
        }

        if (apiKey is { } key)
        {
            environment[key.Name] = key.Value;
        }

        return environment;
    }
}

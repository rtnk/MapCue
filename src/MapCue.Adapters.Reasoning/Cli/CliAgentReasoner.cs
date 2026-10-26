using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Contracts.Schemas;
using MapCue.Domain.Graph;

namespace MapCue.Adapters.Reasoning.Cli;

/// <summary>
/// Real LLM through an agent CLI (<c>claude -p</c>; architecture §9.4, ADR-0004). Runs in a container by default; on the host
/// only with <c>agent.runtime: local</c> <em>and</em> <c>agent.allowLocalExecution: true</c>. Any failure of the configured runtime
/// (engine missing, image missing, non-zero exit, timeout) is reported as <see cref="ReasonerStatus.Unavailable"/> or
/// <see cref="ReasonerStatus.TimedOut"/>; there is never a fallback to another runtime or to the host.
/// The process gets the redacted request on stdin and nothing else: no repository, devices, TapCue tools or profile secrets.
/// </summary>
public sealed class CliAgentReasoner(AgentSettings settings, ISecretResolver secrets, IAgentProcessLauncher? launcher = null, Func<string, string?>? hostVariable = null) : IExplorationReasoner
{
    private readonly IAgentProcessLauncher _launcher = launcher ?? new AgentProcessLauncher();
    private readonly Func<string, string?> _hostVariable = hostVariable ?? Environment.GetEnvironmentVariable;

    public string Name => $"cli:{settings.Provider}:{(settings.Runtime == AgentRuntime.Container ? "container" : "local")}";

    public async Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (settings.Runtime == AgentRuntime.Local && !settings.AllowLocalExecution)
        {
            return Unavailable("local runtime requires agent.allowLocalExecution: true; no fallback to the host");
        }

        if (settings.Runtime == AgentRuntime.Container && string.IsNullOrWhiteSpace(settings.ContainerImage))
        {
            return Unavailable("agent.containerImage is not configured; no fallback to the host");
        }

        (string Name, string Value)? apiKey = null;
        if (settings.ApiKey is { } reference)
        {
            try
            {
                apiKey = (reference.Name, secrets.Resolve(reference).Reveal());
            }
#pragma warning disable CA1031 // Any resolver failure means the reasoner cannot run; the message may name the secret, so it is not echoed.
            catch (Exception)
#pragma warning restore CA1031
            {
                return Unavailable("agent.apiKey could not be resolved");
            }
        }

        var name = "mapcue-agent-" + ContentHash.Hex(ContentHash.Of("mapcue.agent-container/1", request.RequestId, request.Repair is null ? "1" : "2"))[..16];
        var workingDirectory = Directory.CreateTempSubdirectory("mapcue-agent-").FullName;
        try
        {
            var spec = AgentCommandBuilder.Build(
                settings,
                ContractSchemas.Text(ContractSchemas.AgentExplorationResponseFile),
                AgentJson.Serialize(request),
                name,
                apiKey,
                _hostVariable,
                workingDirectory);
            var run = await _launcher.RunAsync(spec, ct);
            switch (run.Outcome)
            {
                case AgentProcessOutcome.FailedToStart:
                    return Unavailable($"'{spec.FileName}' could not be started; no fallback to the host");
                case AgentProcessOutcome.TimedOut:
                    await KillContainerAsync(name);
                    return new ReasonerResult(ReasonerStatus.TimedOut, null, new ReasonerUsage(1, null, null, null), $"no answer within {settings.Timeout.TotalSeconds:0}s");
                case AgentProcessOutcome.OutputTooLarge:
                    await KillContainerAsync(name);
                    return new ReasonerResult(ReasonerStatus.InvalidOutput, null, new ReasonerUsage(1, null, null, null), "the CLI output is too large");
            }

            var envelope = ClaudeEnvelope.Parse(run.StandardOutput);
            if (run.ExitCode != 0 && envelope.Answer is null)
            {
                // Exit 125-127 come from the container engine itself (daemon, image or entrypoint missing).
                return new ReasonerResult(ReasonerStatus.Unavailable, null, envelope.Usage, $"'{spec.FileName}' exited with code {run.ExitCode}; no fallback to the host");
            }

            if (envelope.IsError)
            {
                return new ReasonerResult(ReasonerStatus.Unavailable, null, envelope.Usage, envelope.Problem);
            }

            return envelope.Answer is null
                ? new ReasonerResult(ReasonerStatus.InvalidOutput, null, envelope.Usage, envelope.Problem)
                : new ReasonerResult(ReasonerStatus.Succeeded, envelope.Answer, envelope.Usage, null);
        }
        finally
        {
            TryDelete(workingDirectory);
        }
    }

    private static ReasonerResult Unavailable(string detail) => new(ReasonerStatus.Unavailable, null, ReasonerUsage.None, detail);

    private async Task KillContainerAsync(string name)
    {
        if (settings.Runtime != AgentRuntime.Container)
        {
            return;
        }

        // Best effort: the engine client was killed, the container may still run.
        await _launcher.RunAsync(
            new AgentProcessSpec(settings.ContainerEngine, ["kill", name], new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = _hostVariable("PATH") ?? string.Empty }, null, TimeSpan.FromSeconds(15)),
            CancellationToken.None);
    }

    private static void TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

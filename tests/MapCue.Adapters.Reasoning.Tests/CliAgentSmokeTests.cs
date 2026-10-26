using MapCue.Adapters.Reasoning.Cli;
using MapCue.Application.Agent;
using MapCue.Application.Configuration;
using MapCue.Application.Ports;
using MapCue.Domain.Secrets;

namespace MapCue.Adapters.Reasoning.Tests;

/// <summary>
/// Smoke of <see cref="CliAgentReasoner"/> with the real agent CLI (STEP-04, PASS or NOT RUN). Skipped unless
/// <c>MAPCUE_AGENT_SMOKE</c> is set: <c>container</c> (needs <c>MAPCUE_AGENT_IMAGE</c> and a container engine; the key is read
/// from <c>ANTHROPIC_API_KEY</c> by reference) or <c>local</c> (the host CLI with its own login, explicit opt-in).
/// It sends only the redacted synthetic "Profile" screen of the test harness.
/// </summary>
public sealed class CliAgentSmokeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public const string Variable = "MAPCUE_AGENT_SMOKE";

    private sealed class EnvSecrets : ISecretResolver
    {
        public SecretValue Resolve(SecretRef reference) =>
            new(reference, Environment.GetEnvironmentVariable(reference.Name) ?? throw new InvalidOperationException("secret not set"));
    }

    [SmokeFact]
    public async Task Real_cli_answers_a_valid_interpretation()
    {
        var mode = Environment.GetEnvironmentVariable(Variable);
        var settings = mode == "local"
            ? AgentSettings.Default with { Runtime = AgentRuntime.Local, AllowLocalExecution = true, Timeout = TimeSpan.FromMinutes(5) }
            : AgentSettings.Default with
            {
                ContainerImage = Environment.GetEnvironmentVariable("MAPCUE_AGENT_IMAGE"),
                ContainerEngine = Environment.GetEnvironmentVariable("MAPCUE_AGENT_ENGINE") ?? "docker",
                ApiKey = SecretRef.Parse("secret://env/ANTHROPIC_API_KEY"),
                Timeout = TimeSpan.FromMinutes(5),
            };
        if (Environment.GetEnvironmentVariable("MAPCUE_AGENT_MODEL") is { Length: > 0 } model)
        {
            settings = settings with { Model = model };
        }

        await using var h = AgentHarness.Create();
        var (state, snapshot) = await h.CommitAsync();
        var reasoner = new CliAgentReasoner(settings, new EnvSecrets());

        var result = await h.Interpreter(reasoner, new AgentBudget(settings with { MaxCalls = 2, MaxCostUsd = 1m })).InterpretAsync(AgentHarness.Input(state, snapshot), CancellationToken.None);

        var summary = string.Join(" | ", result.Calls.Select(c => $"{c.Reasoner} call={c.CallNumber} {c.Status} in={c.InputTokens} out={c.OutputTokens} cost={c.CostUsd} ms={c.DurationMs} errors={string.Join(";", c.Errors)}"));
        output.WriteLine(summary);
        output.WriteLine("verdicts: " + string.Join(", ", result.Validation?.Candidates.Select(c => $"{c.CandidateId}:{c.Verdict}:{c.EffectiveRisk}") ?? []));
        Assert.True(result.Status == AgentInterpretationStatus.Accepted, summary);
        Assert.NotEmpty(result.Validation!.Candidates);
        Assert.DoesNotContain(result.AcceptedActions, c => c.EffectiveRisk is not (Domain.RiskClass.Safe or Domain.RiskClass.Navigational));
    }
}

/// <summary>A fact that runs only when <see cref="CliAgentSmokeTests.Variable"/> is set (NOT RUN otherwise).</summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(CliAgentSmokeTests.Variable) is not ("local" or "container"))
        {
            Skip = $"NOT RUN: set {CliAgentSmokeTests.Variable}=container (with MAPCUE_AGENT_IMAGE, ANTHROPIC_API_KEY) or =local to run the real agent CLI smoke.";
        }
    }
}

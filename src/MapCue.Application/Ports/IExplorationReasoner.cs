using MapCue.Contracts.Agent;

namespace MapCue.Application.Ports;

/// <summary>
/// Reasoner port (ADR-0004). Stateless: each call gets everything it needs in the request.
/// The reasoner never receives TapCue tools and its output is untrusted.
/// </summary>
public interface IExplorationReasoner
{
    Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct);
}

public enum ReasonerStatus
{
    Succeeded,
    InvalidOutput,
    Unavailable,
    TimedOut,
}

public sealed record ReasonerUsage(int Calls, long? InputTokens, long? OutputTokens, decimal? CostUsd);

/// <summary>Result of a reasoner call. <see cref="Response"/> is null unless <see cref="Status"/> is <see cref="ReasonerStatus.Succeeded"/>.</summary>
public sealed record ReasonerResult(
    ReasonerStatus Status,
    AgentExplorationResponse? Response,
    ReasonerUsage Usage,
    string? Detail);

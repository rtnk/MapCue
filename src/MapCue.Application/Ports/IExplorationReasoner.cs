using MapCue.Contracts.Agent;

namespace MapCue.Application.Ports;

/// <summary>
/// Reasoner port (ADR-0004). Stateless: each call gets everything it needs in the request.
/// The reasoner never receives TapCue tools and its output is untrusted: it returns the raw answer text, and MapCue alone
/// parses and validates it (schema, stale, semantics, risk; architecture §9.3).
/// </summary>
public interface IExplorationReasoner
{
    /// <summary>Short stable name recorded with every call (e.g. <c>rule-based</c>, <c>cli:claude</c>).</summary>
    string Name { get; }

    Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct);
}

public enum ReasonerStatus
{
    /// <summary>The reasoner produced an answer (<see cref="ReasonerResult.RawResponse"/>), not yet validated.</summary>
    Succeeded,

    /// <summary>The reasoner ran but its output could not be extracted (e.g. a broken CLI envelope); treated as a format error.</summary>
    InvalidOutput,

    /// <summary>The reasoner could not run (container engine missing, CLI failed to start, non-zero exit). No fallback.</summary>
    Unavailable,

    TimedOut,
}

/// <summary>Usage reported by the reasoner; null when unknown (an unknown cost still counts a call and its tokens).</summary>
public sealed record ReasonerUsage(int Calls, long? InputTokens, long? OutputTokens, decimal? CostUsd)
{
    public static ReasonerUsage None { get; } = new(0, null, null, null);

    public long Tokens => (InputTokens ?? 0) + (OutputTokens ?? 0);
}

/// <summary>Result of a reasoner call. <see cref="RawResponse"/> is the untrusted answer text (null unless it produced one).</summary>
public sealed record ReasonerResult(
    ReasonerStatus Status,
    string? RawResponse,
    ReasonerUsage Usage,
    string? Detail);

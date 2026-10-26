namespace MapCue.Domain.Graph;

/// <summary>Outcome of one reasoner call after validation (architecture §9.3; every failure is recorded).</summary>
public enum AgentCallStatus
{
    /// <summary>Schema, stale and content checks passed; candidates were judged one by one (see <see cref="AgentCall.Decisions"/>).</summary>
    Accepted,

    /// <summary>Not JSON, too large or not matching the response schema. Repairable once (CAP-0068).</summary>
    SchemaInvalid,

    /// <summary><c>requestId</c> or <c>snapshotHash</c> differs from the request.</summary>
    Stale,

    /// <summary>Well-formed but refused as a whole (e.g. YAML, commands, paths or URLs in a text field).</summary>
    Rejected,

    ReasonerUnavailable,

    ReasonerTimedOut,

    /// <summary>The call was not made: the budget would be exceeded.</summary>
    BudgetExhausted,
}

/// <summary>What MapCue decided about one candidate of an answer.</summary>
public enum CandidateVerdict
{
    Accepted,
    SkippedByPolicy,
    RequiresApproval,
    Rejected,
}

/// <summary>
/// Decision about one candidate. <see cref="EffectiveRisk"/> is <c>max(<see cref="AgentRisk"/>, <see cref="RuleRisk"/>)</c>;
/// <see cref="ActionKey"/> is set when the candidate mapped to a typed action.
/// </summary>
public sealed record AgentCandidateDecision(
    string CandidateId,
    CandidateVerdict Verdict,
    string? ActionKey,
    RiskClass? AgentRisk,
    RiskClass? RuleRisk,
    RiskClass? EffectiveRisk,
    IReadOnlyList<string> Reasons);

/// <summary>
/// One reasoner call (ADR-0004): request and response are stored, redacted, as artifacts; this record keeps their hashes,
/// the validation result and the usage. <see cref="CallNumber"/> is 1, or 2 for the single format repair.
/// </summary>
public sealed record AgentCall(
    string CallId,
    string RequestId,
    int CallNumber,
    string ContextId,
    string? StateId,
    string? TaskId,
    string? AttemptId,
    string Reasoner,
    string PromptVersion,
    string SnapshotHash,
    string RequestSha256,
    string? ResponseSha256,
    AgentCallStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<AgentCandidateDecision> Decisions,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd,
    long DurationMs,
    DateTimeOffset CreatedAt);

/// <summary>Totals of recorded reasoner usage (to resume budgets). Calls without a reported cost are counted in <see cref="UnknownCostCalls"/>.</summary>
public sealed record AgentUsageTotals(int Calls, long Tokens, decimal CostUsd, int UnknownCostCalls)
{
    public static AgentUsageTotals Zero { get; } = new(0, 0, 0m, 0);
}

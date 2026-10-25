namespace MapCue.Contracts.Agent;

/// <summary>
/// Skeleton of <c>mapcue.agent-exploration-response/1</c> (architecture §9.2). Untrusted input:
/// it is validated by MapCue (STEP-04) before anything is mapped to a device action.
/// </summary>
public sealed record AgentExplorationResponse(
    string RequestId,
    string SnapshotHash,
    string? ProposedScreenName,
    IReadOnlyList<AgentActionCandidate> ActionCandidates)
{
    public string SchemaVersion { get; init; } = SchemaVersions.AgentExplorationResponse;
}

/// <summary>Action proposed by the reasoner. Types are strings on purpose: mapping to typed actions happens only after validation.</summary>
public sealed record AgentActionCandidate(string CandidateId, string ElementRef, string ActionType, string RiskClass);

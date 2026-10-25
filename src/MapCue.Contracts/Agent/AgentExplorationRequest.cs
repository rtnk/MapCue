namespace MapCue.Contracts.Agent;

/// <summary>
/// Skeleton of <c>mapcue.agent-exploration-request/1</c> (architecture §9.1).
/// The full shape and JSON Schema are defined in STEP-04; only the correlation fields exist today.
/// </summary>
public sealed record AgentExplorationRequest(
    string RequestId,
    string SnapshotHash,
    IReadOnlyList<AgentSnapshotElement> Elements)
{
    public string SchemaVersion { get; init; } = SchemaVersions.AgentExplorationRequest;
}

/// <summary>Redacted UI element as sent to the reasoner. <see cref="Ref"/> is a snapshot-local reference.</summary>
public sealed record AgentSnapshotElement(string Ref, string? Role, string? Label, string? ResourceId);

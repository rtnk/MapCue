using System.Text.Json.Serialization;

namespace MapCue.Contracts.Agent;

/// <summary>
/// <c>mapcue.agent-exploration-request/1</c> (architecture §9.1, <c>schemas/agent-exploration-request.v1.schema.json</c>).
/// Built only by MapCue from a redacted, normalized snapshot; it never carries secrets, input values or screenshots.
/// </summary>
public sealed record AgentExplorationRequest(
    string RequestId,
    string PromptVersion,
    AgentTask Task,
    AgentSnapshot Snapshot,
    AgentContext Context,
    AgentGraphHints GraphHints,
    AgentPolicyView Policy,
    AgentLimits Limits,
    AgentRepair? Repair,
    string Instructions)
{
    [JsonPropertyOrder(-1)]
    public string SchemaVersion { get; init; } = SchemaVersions.AgentExplorationRequest;

    /// <summary>Correlation hash of the snapshot (<see cref="AgentSnapshot.SnapshotHash"/>).</summary>
    [JsonIgnore]
    public string SnapshotHash => Snapshot.SnapshotHash;
}

/// <summary>The task the request belongs to. <see cref="Type"/> is <c>AnalyzeState</c> in version 1.</summary>
public sealed record AgentTask(string TaskId, string? AttemptId, string Type);

/// <summary>
/// The snapshot as the reasoner sees it. <see cref="SnapshotHash"/> identifies the exact normalized document the element refs
/// belong to; an answer for another hash is stale. <see cref="Screenshot"/> is always null in version 1.
/// </summary>
public sealed record AgentSnapshot(
    string SnapshotHash,
    string Platform,
    string StateKind,
    IReadOnlyList<AgentSnapshotElement> Elements,
    int OmittedElements,
    object? Screenshot = null);

/// <summary>
/// A redacted UI element. <see cref="Ref"/> is valid within one snapshot only; <see cref="Label"/> is the masked label
/// (numbers, dates, amounts and input values replaced by tokens).
/// </summary>
public sealed record AgentSnapshotElement(
    string Ref,
    string Role,
    string? Label,
    string? ResourceId,
    IReadOnlyList<string> Flags,
    int Depth,
    bool Interactive,
    bool ListRow);

/// <summary>Context of the profile. Only the alias and capabilities, never input values.</summary>
public sealed record AgentContext(string ProfileAlias, string Locale, IReadOnlyList<string> Capabilities);

public sealed record AgentGraphHints(IReadOnlyList<AgentCandidateState> CandidateStates, IReadOnlyList<string> KnownActionKeys);

public sealed record AgentCandidateState(string StateId, string? DisplayName, double Similarity);

/// <summary>The policy the answer is validated against (informative for the reasoner; enforced by MapCue).</summary>
public sealed record AgentPolicyView(
    IReadOnlyList<string> AllowedActionTypes,
    IReadOnlyList<string> AllowedRiskClasses,
    IReadOnlyList<string> ForbiddenRiskClasses,
    IReadOnlyList<string> InputRefs,
    IReadOnlyList<string> Generators);

public sealed record AgentLimits(int MaxCandidates);

/// <summary>Present only on the single format repair (CAP-0068): what was wrong with the previous answer.</summary>
public sealed record AgentRepair(IReadOnlyList<string> PreviousErrors);

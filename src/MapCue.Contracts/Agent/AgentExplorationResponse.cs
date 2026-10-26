using System.Text.Json.Serialization;

namespace MapCue.Contracts.Agent;

/// <summary>
/// <c>mapcue.agent-exploration-response/1</c> (architecture §9.2, <c>schemas/agent-exploration-response.v1.schema.json</c>).
/// Untrusted input: MapCue deserializes it only after the JSON passed the schema, and maps nothing to a device action before
/// the stale, semantic and risk checks. Enumerations are strings on purpose: mapping to typed values happens in validation.
/// </summary>
public sealed record AgentExplorationResponse(
    string RequestId,
    string SnapshotHash,
    AgentInterpretation Interpretation,
    IReadOnlyList<AgentFact> Facts,
    IReadOnlyList<AgentHypothesis> Hypotheses,
    IReadOnlyList<AgentActionCandidate> ActionCandidates,
    IReadOnlyList<string> Warnings)
{
    [JsonPropertyOrder(-1)]
    public string SchemaVersion { get; init; } = SchemaVersions.AgentExplorationResponse;
}

public sealed record AgentInterpretation(string? ProposedScreenName, string? StateKind, string? SameAsStateId, double Confidence);

/// <summary>A fact about the screen, tied to element refs; always <c>observed</c>.</summary>
public sealed record AgentFact(string Claim, IReadOnlyList<string> ElementRefs, string Level);

/// <summary>A hypothesis (e.g. a capability guard); always <c>inferred</c> until observations on distinguishing profiles confirm it.</summary>
public sealed record AgentHypothesis(string Claim, string Kind, AgentGuard? Guard, string Level, double Confidence);

public sealed record AgentGuard(IReadOnlyList<AgentCapabilityCondition> All);

public sealed record AgentCapabilityCondition(string Capability);

/// <summary>Action proposed by the reasoner, addressed by element ref only. <see cref="ElementRef"/> is null for screen-level actions (back).</summary>
public sealed record AgentActionCandidate(
    string CandidateId,
    string? ElementRef,
    string ActionType,
    string? Direction,
    AgentInput? Input,
    string Intent,
    string RiskClass,
    string? ExpectedOutcome,
    double Confidence);

/// <summary>Input of an <c>inputText</c> candidate: exactly one of a profile input reference or a generator, never a value.</summary>
public sealed record AgentInput(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ValueRef = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Generator = null);

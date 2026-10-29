namespace MapCue.Domain.Graph;

/// <summary>A logical screen (architecture §6). <see cref="DisplayName"/> may come from the agent; the identity comes from the resolver.</summary>
public sealed record LogicalScreen(
    string ScreenId,
    string ScreenKey,
    string? DisplayName,
    IdentityStatus IdentityStatus,
    IdentitySource IdentitySource,
    DateTimeOffset CreatedAt);

/// <summary>
/// A state of a logical screen, shared by all contexts (ADR-0006). <see cref="IdentityTokens"/> is the sorted multiset the
/// <see cref="IdentityHash"/> is computed from (kept for similarity matching); <see cref="BaseElementKeys"/> is the element
/// set of the first observation, the base for variant differences.
/// </summary>
public sealed record ScreenState(
    string StateId,
    string ScreenId,
    StateKind StateKind,
    string IdentityHash,
    int FingerprintVersion,
    IReadOnlyList<string> IdentityTokens,
    IReadOnlyList<string> BaseElementKeys,
    IdentityStatus IdentityStatus,
    DateTimeOffset CreatedAt);

/// <summary>
/// A state as seen in one context (architecture §6). Element differences are relative to <see cref="ScreenState.BaseElementKeys"/>;
/// they describe the latest distinct snapshot. <see cref="SnapshotCount"/> counts distinct snapshots, so re-recording identical
/// content (e.g. a repeated import) changes nothing. Evidence of a variant is the evidence of its context and state (never shared).
/// </summary>
public sealed record ScreenVariant(
    string StateId,
    string ContextId,
    string ElementSetHash,
    IReadOnlyList<string> AddedElementKeys,
    IReadOnlyList<string> RemovedElementKeys,
    int SnapshotCount,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

/// <summary>
/// A recorded snapshot (architecture §6). <see cref="RawSha256"/> is the redacted raw observation blob, so the fingerprint can be
/// recomputed after a <see cref="FingerprintVersion"/> change; <see cref="PayloadHash"/> is the tool payload hash from the adapter.
/// <see cref="SnapshotKey"/> deduplicates identical content within a context.
/// </summary>
public sealed record UiSnapshot(
    string SnapshotId,
    string SnapshotKey,
    string ContextId,
    string? AttemptId,
    SnapshotFidelity Fidelity,
    string PayloadHash,
    string RawSha256,
    string NormalizedSha256,
    string IdentityHash,
    string LayoutHash,
    string ElementSetHash,
    int FingerprintVersion,
    string? StateId,
    string? ScreenshotSha256,
    int RedactionVersion,
    DateTimeOffset ObservedAt);

/// <summary>Flags of a normalized element.</summary>
[Flags]
public enum UiElementStates
{
    None = 0,
    Clickable = 1,
    Enabled = 2,
    Scrollable = 4,
    Checked = 8,
    Focused = 16,
    Selected = 32,
    Editable = 64,
}

/// <summary>
/// A normalized element (architecture §6, §7.1). <see cref="ElementRef"/> is valid within one snapshot (<c>e17</c>);
/// <see cref="ElementKey"/> is stable within a screen. <see cref="Label"/> is the masked label used for identity;
/// <see cref="Text"/> is the (redacted) original. <see cref="ListTemplate"/> is set for elements inside a detected list row.
/// </summary>
public sealed record UiElement(
    string ElementRef,
    string ElementKey,
    ElementKeyStrength KeyStrength,
    string Role,
    string? Label,
    string? Text,
    string? ResourceId,
    UiElementStates Flags,
    string? BoundsBucket,
    string? ListTemplate,
    int Depth,
    bool Interactive);

/// <summary>A detected list (architecture §7.1 step 3): <c>list(templateHash, countBucket ∈ {0, 1, many})</c>.</summary>
public sealed record UiList(string ContainerKey, string TemplateHash, int RowCount, string CountBucket);

/// <summary>
/// Result of snapshot normalization. Identity ignores order, bounds, pixels and dynamic content; layout keeps the order.
/// </summary>
public sealed record NormalizedSnapshot(
    int FingerprintVersion,
    Platform Platform,
    IReadOnlyList<UiElement> Elements,
    IReadOnlyList<UiList> Lists,
    IReadOnlyList<string> IdentityTokens,
    string IdentityHash,
    string LayoutHash,
    string ElementSetHash,
    IReadOnlyList<string> ElementSetKeys,
    StateKind StateKind,
    IReadOnlyList<string> StateDiscriminators,
    IReadOnlyList<string> ObservedPackages,
    int SystemUiElementsRemoved,
    int WeakKeyCount);

/// <summary>
/// An action candidate on a state (architecture §6). <c>actionKey = H(actionType, canonicalSelector, paramClass)</c>;
/// <see cref="ParamClass"/> is a class such as <c>input:profile.username</c>, never a value.
/// </summary>
public sealed record ActionCandidate(
    string StateId,
    string ActionKey,
    ActionType ActionType,
    string CanonicalSelector,
    string? ParamClass,
    CandidateSource Source,
    RiskClass RiskClass,
    CandidateStatus Status,
    DateTimeOffset CreatedAt)
{
    public static string KeyOf(ActionType type, string canonicalSelector, string? paramClass) =>
        ContentHash.Of("mapcue.action/1", type.ToString(), canonicalSelector, paramClass);
}

/// <summary>
/// An edge <c>(fromStateId, actionKey, contextId) → toStateId</c> (architecture §6). Observations of different targets set
/// <see cref="Nondeterministic"/>; <see cref="ToStateId"/> stays the first observed target.
/// </summary>
public sealed record ScreenEdge(
    string EdgeId,
    string FromStateId,
    string ActionKey,
    string ContextId,
    string? ToStateId,
    EdgeStatus Status,
    int ObservationCount,
    bool Nondeterministic,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    DateTimeOffset? LastVerifiedAt)
{
    public static string IdOf(string fromStateId, string actionKey, string contextId) =>
        ContentHash.ShortId("edge", ContentHash.Of("mapcue.edge/1", fromStateId, actionKey, contextId));
}

/// <summary>One observation per attempt (<see cref="AttemptId"/> is unique), which makes the attempt commit idempotent.</summary>
public sealed record ActionObservation(
    string AttemptId,
    string EdgeId,
    string ContextId,
    string BeforeSnapshotId,
    string? AfterSnapshotId,
    string? ToStateId,
    ActionOutcome Outcome,
    long DurationMs,
    DateTimeOffset ObservedAt);

/// <summary>A stored, redacted artifact used as evidence, always within one context.</summary>
public sealed record Evidence(
    string EvidenceId,
    EvidenceKind Kind,
    string ArtifactSha256,
    long Length,
    string ContextId,
    string? SnapshotId,
    string? AttemptId,
    int RedactionCount,
    IReadOnlyList<string> RedactionRules,
    DateTimeOffset CreatedAt)
{
    public static string IdOf(EvidenceKind kind, string artifactSha256, string contextId, string? attemptId) =>
        ContentHash.ShortId("ev", ContentHash.Of("mapcue.evidence/1", kind.ToString(), artifactSha256, contextId, attemptId));
}

/// <summary>Why a task is blocked, with what the operator needs to resume it (architecture §6, §13).</summary>
public sealed record BlockedReason(BlockedReasonCode Code, string Details, IReadOnlyList<string> EvidenceIds, string? ResumeDoc);

/// <summary>
/// A frontier task (architecture §6, §5.3). <see cref="TaskKey"/> is unique; <see cref="CreatedSeq"/> is assigned by the store.
/// <see cref="LeaseOwner"/> is the run that claimed the task (status <see cref="FrontierTaskStatus.Leased"/>) until
/// <see cref="LeaseExpiresAt"/>. <see cref="RepresentedBy"/> is the task of another row of the same list template that stands for
/// this one (budgets.maxListRowsPerTemplate); such a task is <see cref="FrontierTaskStatus.Cancelled"/> and never executed.
/// </summary>
public sealed record FrontierTask(
    string TaskId,
    string TaskKey,
    TaskType Type,
    FrontierTaskStatus Status,
    int Priority,
    int Depth,
    string ContextId,
    string? FromStateId,
    string? ActionKey,
    string? RecipeId,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    BlockedReason? BlockedReason,
    long CreatedSeq,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string? RepresentedBy { get; init; }
}

/// <summary>
/// An attempt of a task (architecture §6). <see cref="Phase"/> is journaled before its effect (§5.3), so after a crash the last
/// phase decides the recovery. <see cref="RunId"/> is the run that made the attempt (null for imported attempts).
/// </summary>
public sealed record TaskAttempt(
    string AttemptId,
    string TaskId,
    int N,
    AttemptPhase Phase,
    ActionOutcome? Outcome,
    string? Error,
    string? ArtifactsDir,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt)
{
    public string? RunId { get; init; }
}

/// <summary>Frontier statistics captured in a checkpoint.</summary>
public sealed record FrontierStats(int Pending, int Leased, int Completed, int Blocked, int Failed, int OutcomeUnknown, int ReadyToResume, int Cancelled)
{
    public int Total => Pending + Leased + Completed + Blocked + Failed + OutcomeUnknown + ReadyToResume + Cancelled;
}

/// <summary>Reference point for <c>resume</c> and reports (architecture §6).</summary>
public sealed record Checkpoint(
    string CheckpointId,
    string RunId,
    long EventSeq,
    FrontierStats Frontier,
    string ConfigHash,
    DateTimeOffset CreatedAt);

/// <summary>One step of a reachability recipe, each with the state expected after it.</summary>
public sealed record RecipeStep(RecipeStepKind Kind, string? ActionKey, string? EdgeId, string? ExpectedStateId, string? Postcondition);

/// <summary>How to reach a state from a clean start in one context (architecture §6, §13). Built from the graph, never from an LLM session.</summary>
public sealed record ReachabilityRecipe(
    string RecipeId,
    string ContextId,
    string TargetStateId,
    IReadOnlyList<RecipeStep> Steps,
    DateTimeOffset? VerifiedAt,
    IReadOnlyList<string> BasedOnEdges);

/// <summary>An event of the transactional outbox (architecture §12). <see cref="Seq"/> is assigned by the store.</summary>
public sealed record OutboxEvent(long Seq, string EventId, string Type, DateTimeOffset OccurredAt, string? ContextId, string? SubjectId, string PayloadJson);

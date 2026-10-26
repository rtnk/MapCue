namespace MapCue.Domain.Graph;

/// <summary>Whether a screen or state identity is settled (architecture §6, ADR-0006).</summary>
public enum IdentityStatus
{
    Provisional,
    Confirmed,
}

/// <summary>Who assigned a logical screen identity. The agent never does (ADR-0006: agent proposals are hypotheses).</summary>
public enum IdentitySource
{
    Hint,
    Structure,
    Operator,
}

/// <summary>Kind of a screen state (architecture §6).</summary>
public enum StateKind
{
    Default,
    Loading,
    Empty,
    Error,
    Modal,
    Menu,
    Form,
    Keyboard,
    Unknown,
}

/// <summary>
/// Status of an edge (architecture §8.1). The order of the first four values is the confidence order used by
/// <see cref="EdgeStatuses.Raise"/>; the remaining values are terminal annotations, not confidence levels.
/// </summary>
public enum EdgeStatus
{
    Discovered,
    Executed,
    Observed,
    Verified,
    Blocked,
    SkippedByPolicy,
    Unknown,
}

public static class EdgeStatuses
{
    /// <summary>
    /// Monotonic status update: confidence never goes down (an observed edge stays observed after a later
    /// <see cref="EdgeStatus.Unknown"/> attempt). <see cref="EdgeStatus.Blocked"/>, <see cref="EdgeStatus.SkippedByPolicy"/>
    /// and <see cref="EdgeStatus.Unknown"/> only replace <see cref="EdgeStatus.Discovered"/>.
    /// </summary>
    public static EdgeStatus Raise(EdgeStatus current, EdgeStatus observed)
    {
        static bool IsConfidence(EdgeStatus s) => s is EdgeStatus.Discovered or EdgeStatus.Executed or EdgeStatus.Observed or EdgeStatus.Verified;

        if (IsConfidence(current) && IsConfidence(observed))
        {
            return (EdgeStatus)Math.Max((int)current, (int)observed);
        }

        if (IsConfidence(observed))
        {
            // A real execution/observation beats an earlier blocked/unknown annotation.
            return observed == EdgeStatus.Discovered ? current : observed;
        }

        return current == EdgeStatus.Discovered ? observed : current;
    }
}

/// <summary>Origin of an action candidate.</summary>
public enum CandidateSource
{
    Agent,
    Rule,
}

/// <summary>Lifecycle of an action candidate on a state.</summary>
public enum CandidateStatus
{
    Proposed,
    Scheduled,
    Explored,
    SkippedByPolicy,
    Rejected,
}

/// <summary>Frontier task types (architecture §5.2).</summary>
public enum TaskType
{
    AnalyzeState,
    ExploreAction,
    VerifyEdge,
    AnalyzeScreen,
    AuditAccessibility,
}

/// <summary>Frontier task status (architecture §5.3).</summary>
public enum FrontierTaskStatus
{
    Pending,
    Leased,
    Completed,
    Blocked,
    Failed,
    OutcomeUnknown,
    ReadyToResume,
    Cancelled,
}

/// <summary>Phases of an attempt, journaled before their effect (architecture §5.3).</summary>
public enum AttemptPhase
{
    Created,
    RestoreStarted,
    RestoreVerified,
    PreObserved,
    PolicyChecked,
    ActionDispatched,
    ActionReturned,
    PostObserved,
    Classified,
    Committed,
    Abandoned,
}

/// <summary>Why a task is blocked (architecture §6).</summary>
public enum BlockedReasonCode
{
    ReplayDiverged,
    UnknownOverlay,
    PolicyRequiresApproval,
    ElementStale,
    AmbiguousIdentity,
    DeviceUnavailable,
    OutcomeUnknownUnsafe,
    AppLeft,
    Crash,
    BudgetForTask,

    /// <summary>The reasoner could not be reached (container down, CLI missing, timeout). Never falls back to the host (ADR-0004).</summary>
    ReasonerUnavailable,

    /// <summary>The reasoner budget (calls, tokens or cost) is spent (architecture §10.2).</summary>
    ReasonerBudgetExhausted,
}

/// <summary>What an evidence row points to.</summary>
public enum EvidenceKind
{
    RawSnapshot,
    NormalizedSnapshot,
    Screenshot,
    ToolOutput,
    AgentRequest,
    AgentResponse,
    Flow,
}

/// <summary>Strength of an <see cref="UiElement.ElementKey"/> (architecture §7.1 step 4). Weak keys are a map quality metric (R-04).</summary>
public enum ElementKeyStrength
{
    /// <summary>From the platform resource id.</summary>
    ResourceId,

    /// <summary>From role and normalized label.</summary>
    Label,

    /// <summary>From role and structural path only.</summary>
    Structural,
}

/// <summary>Fidelity of a snapshot (fast: MCP <c>inspect_screen</c>, full: CLI <c>hierarchy</c>).</summary>
public enum SnapshotFidelity
{
    Fast,
    Full,
}

/// <summary>Kind of a step in a reachability recipe (architecture §13).</summary>
public enum RecipeStepKind
{
    LaunchApp,
    SetupFlow,
    Action,
}

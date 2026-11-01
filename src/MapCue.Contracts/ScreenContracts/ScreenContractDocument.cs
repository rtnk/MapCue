using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapCue.Contracts.ScreenContracts;

/// <summary>
/// A versioned screen contract (<c>mapcue.screen-contract/1</c>, <c>schemas/screen-contract.v1.schema.json</c>, architecture §6, §14): one
/// logical screen in one comparison group (app, platform, build, locale). <see cref="ContentHash"/> is the SHA-256 of the contract with
/// <see cref="Version"/> 0 and an empty <see cref="ContentHash"/>, so equal content gives the same hash and a new version only when it changes.
/// The document has no timestamps: it is a pure function of the store.
/// </summary>
public sealed record ScreenContractDocument(
    string SchemaVersion,
    string ScreenId,
    string ScreenKey,
    string? DisplayName,
    int Version,
    string ContentHash,
    ContractGroup Group,
    int FingerprintVersion,
    IReadOnlyList<ContractState> States,
    IReadOnlyList<ContractContext> Contexts,
    IReadOnlyList<ContractElement> Elements,
    IReadOnlyList<ContractVariant> Variants,
    ContractAccessibility Accessibility,
    ContractKeyboard Keyboard,
    IReadOnlyList<string> EvidenceIds)
{
    public const string CurrentSchemaVersion = SchemaVersions.ScreenContract;
}

/// <summary>The comparison group of the contract (the same as STEP-07 profile comparison).</summary>
public sealed record ContractGroup(string GroupKey, string AppId, string Platform, string AppBuildId, string Locale);

public sealed record ContractState(string StateId, string StateKind, string IdentityHash, IReadOnlyList<string> ContextIds);

public sealed record ContractContext(string ContextId, string ProfileId, string ScenarioId, IReadOnlyList<string> Capabilities);

/// <summary>
/// An element of the screen. <see cref="RoleLevel"/> is <c>observed</c> when the platform reports the widget class and <c>inferred</c> on iOS
/// (G-03). <see cref="Presence"/> lists where it was seen; <see cref="VisibleWhen"/> is a guard hypothesis (STEP-07), never a fact unless
/// observed.
/// </summary>
public sealed record ContractElement(
    string ElementKey,
    string KeyStrength,
    string Role,
    string RoleLevel,
    string? Label,
    string? ResourceId,
    bool Interactive,
    bool KeyboardTarget,
    IReadOnlyList<ContractPresence> Presence,
    ContractVisibleWhen? VisibleWhen);

/// <summary>Where an element was seen: a state in a context, from a full or a fast snapshot.</summary>
public sealed record ContractPresence(string ContextId, string StateId, string Source);

/// <summary>A guard hypothesis on an element (<c>inferred</c>, <c>observed</c> or <c>refuted</c>), with the contexts it was seen and missed in.</summary>
public sealed record ContractVisibleWhen(string? Capability, string Level, IReadOnlyList<string> PresentIn, IReadOnlyList<string> AbsentIn, IReadOnlyList<string> EvidenceIds);

/// <summary>A state as seen in one context (<c>ScreenVariant</c>) with its identity tokens and the fidelity of the snapshot the contract used.</summary>
public sealed record ContractVariant(
    string StateId,
    string ContextId,
    string ElementSetHash,
    IReadOnlyList<string> IdentityTokens,
    IReadOnlyList<string> AddedElementKeys,
    IReadOnlyList<string> RemovedElementKeys,
    string Source,
    IReadOnlyList<string> EvidenceIds);

/// <summary>The accessibility matrix (architecture §14.2), findings of tree rules and manual records of the operator.</summary>
public sealed record ContractAccessibility(
    IReadOnlyList<AccessibilityMatrixRow> Matrix,
    IReadOnlyList<AccessibilityFinding> Findings,
    IReadOnlyList<AccessibilityRecordRef> ManualRecords);

/// <summary>
/// One technology of the matrix with one cell per evidence level. The cell statuses are constrained per level by the schema: a cell of
/// level <c>inspected</c> can never be <c>pass</c> (architecture §14.1).
/// </summary>
public sealed record AccessibilityMatrixRow(string Technology, InspectedCell Inspected, AutomatedCell Automated, AssistiveCell AssistiveVerified);

/// <summary>Level <c>inspected</c> (from the tree): <c>present</c>, <c>issues</c>, <c>notTested</c> or <c>unsupported</c>, never <c>pass</c>.</summary>
public sealed record InspectedCell(string Status, string Method, int Findings, IReadOnlyList<string> EvidenceIds)
{
    public string Level { get; init; } = "inspected";
}

/// <summary>Level <c>automated</c>: <c>pass</c> only for the scope its method states.</summary>
public sealed record AutomatedCell(string Status, string Method, int Findings, IReadOnlyList<string> EvidenceIds)
{
    public string Level { get; init; } = "automated";
}

/// <summary>Level <c>assistiveVerified</c>: only from manual records of the operator (<c>mapcue a11y record</c>).</summary>
public sealed record AssistiveCell(string Status, string Method, IReadOnlyList<string> RecordIds, IReadOnlyList<string> EvidenceIds)
{
    public string Level { get; init; } = "assistiveVerified";
}

/// <summary>A finding of a tree rule (level <c>inspected</c>): something to check, never a verdict.</summary>
public sealed record AccessibilityFinding(
    string RuleId,
    string Technology,
    string Level,
    string? ElementKey,
    string ContextId,
    string StateId,
    string Message,
    IReadOnlyList<string> EvidenceIds);

public sealed record AccessibilityRecordRef(
    string RecordId,
    string Technology,
    string? StateId,
    string? ElementKey,
    string Property,
    string Result,
    string ContextId,
    string ProtocolVersion,
    IReadOnlyList<string> EvidenceIds);

/// <summary>Keyboard focus: the order predicted from the tree (<c>predicted</c>/<c>unverified</c>) and the observed focus graph (<c>automated</c>).</summary>
public sealed record ContractKeyboard(IReadOnlyList<PredictedFocusOrder> Predicted, IReadOnlyList<ObservedFocusGraph> Observed);

/// <summary>Reading order of the keyboard targets from the tree (top to bottom, left to right). Never evidence of what a user gets.</summary>
public sealed record PredictedFocusOrder(string ContextId, string StateId, string Level, string Status, string Method, IReadOnlyList<string> Order);

/// <summary>
/// The focus graph observed with <c>Tab</c> (Android PoC): transitions between focused elements in press order. <see cref="Status"/> is the
/// automated verdict for this state (<c>pass</c>: the cycle closed and every keyboard target was reached).
/// </summary>
public sealed record ObservedFocusGraph(
    string ContextId,
    string StateId,
    string Level,
    string Status,
    string Method,
    IReadOnlyList<FocusTransition> Transitions,
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Unreached,
    bool CycleClosed,
    bool? MatchesPredicted,
    string? Detail,
    IReadOnlyList<string> EvidenceIds);

public sealed record FocusTransition(int Order, string? From, string To);

/// <summary>JSON of screen contracts: camelCase, nulls written, indented; the content hash is computed on the compact form.</summary>
public static class ScreenContractJson
{
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    private static readonly JsonSerializerOptions _compact = Create(indented: false);

    public static string Serialize(ScreenContractDocument contract) => JsonSerializer.Serialize(contract, Options);

    /// <summary><c>sha256:hex</c> of the compact JSON with version 0 and an empty content hash.</summary>
    public static string ContentHashOf(ScreenContractDocument contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var json = JsonSerializer.Serialize(contract with { Version = 0, ContentHash = string.Empty }, _compact);
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

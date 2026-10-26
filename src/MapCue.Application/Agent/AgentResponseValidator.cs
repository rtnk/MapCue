using System.Globalization;
using System.Text;
using System.Text.Json;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Contracts.Schemas;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Agent;

/// <summary>
/// Validation of one candidate. <see cref="Action"/> is set only for <see cref="CandidateVerdict.Accepted"/>; <see cref="Spec"/>
/// whenever the candidate mapped to a typed action (also when the policy skipped it). <see cref="ElementKey"/> lets the engine
/// re-check the element on a fresh snapshot right before execution (<see cref="PreExecutionGuard"/>).
/// </summary>
public sealed record CandidateValidation(
    string CandidateId,
    CandidateVerdict Verdict,
    IReadOnlyList<string> Reasons,
    RiskClass? AgentRisk,
    RiskClass? RuleRisk,
    RiskClass? EffectiveRisk,
    ActionSpec? Spec,
    ValidatedDeviceAction? Action,
    string? ElementKey)
{
    public AgentCandidateDecision ToDecision() => new(CandidateId, Verdict, Spec?.ActionKey, AgentRisk, RuleRisk, EffectiveRisk, Reasons);
}

/// <summary>
/// Result of validating an answer. <see cref="Response"/> is the parsed answer (null unless it passed the schema);
/// <see cref="Interpretation"/> is its interpretation with unverifiable parts removed (unknown <c>sameAsStateId</c>, facts
/// with unknown refs). Candidates are judged only when <see cref="Status"/> is <see cref="AgentCallStatus.Accepted"/>.
/// </summary>
public sealed record AgentValidation(
    AgentCallStatus Status,
    IReadOnlyList<string> Errors,
    AgentExplorationResponse? Response,
    AgentInterpretation? Interpretation,
    IReadOnlyList<AgentFact> Facts,
    IReadOnlyList<CandidateValidation> Candidates)
{
    public bool Repairable => Status == AgentCallStatus.SchemaInvalid;

    public IEnumerable<CandidateValidation> Accepted => Candidates.Where(c => c.Verdict == CandidateVerdict.Accepted);

    internal static AgentValidation Failed(AgentCallStatus status, IReadOnlyList<string> errors, AgentExplorationResponse? response = null) =>
        new(status, errors, response, null, [], []);
}

/// <summary>
/// Validates an untrusted answer in the order of architecture §9.3: (1) size, JSON and the response schema (with
/// <c>additionalProperties: false</c>, duplicate keys refused); (2) stale: <c>requestId</c> and <c>snapshotHash</c> must match the
/// request; (3) semantics: forbidden content in any text (YAML, commands, paths, URLs) rejects the whole answer, then per
/// candidate the element ref must exist, be interactive and enabled, the action type must be allowlisted, inputs must be
/// references, never literals; (4) risk: <c>max(agent, rules)</c> through <see cref="PolicyGate"/>; (5) only then a
/// <see cref="ValidatedDeviceAction"/> with a selector chosen by MapCue (resource id before text).
/// </summary>
public sealed class AgentResponseValidator(RiskClassifier classifier, PolicyGate gate)
{
    public const int MaxResponseBytes = 256 * 1024;

    private static readonly JsonDocumentOptions _parse = new() { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    public AgentValidation Validate(string? raw, AgentExplorationRequest request, NormalizedSnapshot snapshot, AgentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(policy);

        // 1. Format and schema.
        if (string.IsNullOrWhiteSpace(raw))
        {
            return AgentValidation.Failed(AgentCallStatus.SchemaInvalid, ["the answer is empty"]);
        }

        if (Encoding.UTF8.GetByteCount(raw) > MaxResponseBytes)
        {
            return AgentValidation.Failed(AgentCallStatus.SchemaInvalid, [$"the answer is larger than {MaxResponseBytes} bytes"]);
        }

        AgentExplorationResponse response;
        try
        {
            using var document = JsonDocument.Parse(raw, _parse);
            var schemaErrors = ContractSchemas.AgentExplorationResponse.Validate(document.RootElement);
            if (schemaErrors.Count > 0)
            {
                return AgentValidation.Failed(AgentCallStatus.SchemaInvalid, [.. schemaErrors.Select(e => "schema " + e)]);
            }

            response = AgentJson.Deserialize<AgentExplorationResponse>(document.RootElement);
        }
        catch (JsonException ex)
        {
            // The parser message may quote the input; only the position is reported.
            var where = ex.LineNumber is { } line ? $" (line {line + 1}, byte {ex.BytePositionInLine ?? 0})" : string.Empty;
            return AgentValidation.Failed(AgentCallStatus.SchemaInvalid, ["the answer is not a single valid JSON object" + where]);
        }

        // 2. Stale.
        var stale = new List<string>();
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
        {
            stale.Add("requestId does not match the request");
        }

        if (!string.Equals(response.SnapshotHash, request.SnapshotHash, StringComparison.Ordinal))
        {
            stale.Add("snapshotHash does not match the request");
        }

        if (stale.Count > 0)
        {
            return AgentValidation.Failed(AgentCallStatus.Stale, stale, response);
        }

        // 3a. Forbidden content anywhere rejects the answer as a whole.
        var forbidden = TextsOf(response).Select(t => (t.Field, Rule: ContentGuard.Find(t.Text))).Where(t => t.Rule is not null).ToList();
        if (forbidden.Count > 0)
        {
            return AgentValidation.Failed(AgentCallStatus.Rejected, [.. forbidden.Select(f => $"forbidden content ({f.Rule}) in {f.Field}")], response);
        }

        var errors = new List<string>();
        var elements = snapshot.Elements.ToDictionary(e => e.ElementRef, StringComparer.Ordinal);

        var interpretation = response.Interpretation;
        if (interpretation.SameAsStateId is { } same && !request.GraphHints.CandidateStates.Any(s => s.StateId == same))
        {
            errors.Add("interpretation.sameAsStateId is not one of graphHints.candidateStates; ignored");
            interpretation = interpretation with { SameAsStateId = null };
        }

        var facts = new List<AgentFact>();
        for (var i = 0; i < response.Facts.Count; i++)
        {
            if (response.Facts[i].ElementRefs.All(elements.ContainsKey))
            {
                facts.Add(response.Facts[i]);
            }
            else
            {
                errors.Add($"facts/{i.ToString(CultureInfo.InvariantCulture)} refers to an unknown element; ignored");
            }
        }

        // 3b-5. Candidates one by one.
        var candidates = new List<CandidateValidation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < response.ActionCandidates.Count; i++)
        {
            candidates.Add(ValidateCandidate(response.ActionCandidates[i], i, request, elements, policy, ids, keys));
        }

        return new AgentValidation(AgentCallStatus.Accepted, errors, response, interpretation, facts, candidates);
    }

    private CandidateValidation ValidateCandidate(
        AgentActionCandidate candidate,
        int index,
        AgentExplorationRequest request,
        Dictionary<string, UiElement> elements,
        AgentPolicy policy,
        HashSet<string> ids,
        HashSet<string> keys)
    {
        CandidateValidation Reject(string reason, RiskClass? agentRisk = null) =>
            new(candidate.CandidateId, CandidateVerdict.Rejected, [reason], agentRisk, null, null, null, null, null);

        var agentRisk = ParseRisk(candidate.RiskClass);
        if (index >= request.Limits.MaxCandidates)
        {
            return Reject("more candidates than limits.maxCandidates", agentRisk);
        }

        if (!ids.Add(candidate.CandidateId))
        {
            return Reject("duplicate candidateId", agentRisk);
        }

        var type = ParseActionType(candidate.ActionType);
        if (type is null || !AgentPolicy.ProposableActionTypes.Contains(type.Value) || !policy.AllowedActionTypes.Contains(type.Value))
        {
            return Reject($"action type '{candidate.ActionType}' is not in the allowlist", agentRisk);
        }

        UiElement? element = null;
        var needsElement = type is ActionType.Tap or ActionType.LongPress or ActionType.InputText;
        var screenLevel = type is ActionType.Back or ActionType.HideKeyboard;
        if (candidate.ElementRef is { } elementRef)
        {
            if (screenLevel)
            {
                return Reject($"{type} must not have an elementRef", agentRisk);
            }

            if (!elements.TryGetValue(elementRef, out element))
            {
                return Reject($"elementRef {elementRef} does not exist in the snapshot", agentRisk);
            }

            if (!element.Interactive)
            {
                return Reject($"elementRef {elementRef} is not interactive", agentRisk);
            }

            if (!element.Flags.HasFlag(UiElementStates.Enabled))
            {
                return Reject($"elementRef {elementRef} is disabled", agentRisk);
            }
        }
        else if (needsElement)
        {
            return Reject($"{type} needs an elementRef", agentRisk);
        }

        if (type == ActionType.InputText && !element!.Flags.HasFlag(UiElementStates.Editable))
        {
            return Reject($"elementRef {candidate.ElementRef} is not a text field", agentRisk);
        }

        if (type == ActionType.Scroll && element is not null && !element.Flags.HasFlag(UiElementStates.Scrollable))
        {
            return Reject($"elementRef {candidate.ElementRef} is not scrollable", agentRisk);
        }

        // Inputs: references only (profile inputs of this profile or deterministic generators), never literals.
        string? inputRef = null;
        string? paramClass = null;
        if (type == ActionType.InputText)
        {
            switch (candidate.Input)
            {
                case { ValueRef: { } valueRef, Generator: null } when valueRef.StartsWith("profile.inputs.", StringComparison.Ordinal)
                    && policy.InputNames.Contains(valueRef["profile.inputs.".Length..], StringComparer.Ordinal):
                    inputRef = valueRef;
                    paramClass = "input:" + valueRef;
                    break;
                case { ValueRef: null, Generator: { } generator } when AgentPolicy.Generators.Contains(generator, StringComparer.Ordinal):
                    inputRef = "generated:" + generator;
                    paramClass = inputRef;
                    break;
                default:
                    return Reject("inputText needs input.valueRef to an input of this profile or an allowed input.generator", agentRisk);
            }
        }
        else if (candidate.Input is not null)
        {
            return Reject($"{type} must not have an input", agentRisk);
        }

        var direction = ParseDirection(candidate.Direction);
        if (type is ActionType.Scroll or ActionType.Swipe)
        {
            if (direction is null)
            {
                return Reject($"{type} needs a direction", agentRisk);
            }
        }
        else if (candidate.Direction is not null)
        {
            return Reject($"{type} must not have a direction", agentRisk);
        }

        // Selector chosen by MapCue: resource id before text, each only if unique on the screen (list rows share ids);
        // elements without a unique stable selector are not actionable.
        string? targetId = null;
        string? targetText = null;
        if (element is not null && type is not ActionType.Scroll and not ActionType.Swipe)
        {
            if (Usable(element.ResourceId) && elements.Values.Count(e => e.ResourceId == element.ResourceId) == 1)
            {
                targetId = element.ResourceId;
            }
            else if (Usable(element.Text) && elements.Values.Count(e => e.Text == element.Text) == 1)
            {
                targetText = element.Text;
            }
            else
            {
                return Reject($"elementRef {candidate.ElementRef} has no unique stable selector (resource id or text)", agentRisk);
            }
        }

        var selector = string.Join(
            ';',
            new[] { targetId is null ? null : "id:" + targetId, targetText is null ? null : "text:" + targetText, direction is null ? null : "dir:" + direction }.OfType<string>());

        // 4. Risk: the agent can only raise it.
        var rule = classifier.Classify(type.Value, element);
        var effective = RiskClassifier.Max(agentRisk ?? RiskClass.Unknown, rule.Risk);
        var reasons = new List<string>(rule.Reasons);
        if (agentRisk is null)
        {
            reasons.Add($"agent risk '{candidate.RiskClass}' unknown");
        }

        var spec = new ActionSpec(type.Value, selector, paramClass, effective, CandidateSource.Agent);
        if (!keys.Add(spec.ActionKey))
        {
            return new(candidate.CandidateId, CandidateVerdict.Rejected, [.. reasons, "duplicate of another candidate's action"], agentRisk, rule.Risk, effective, null, null, element?.ElementKey);
        }

        var decision = gate.Evaluate(type.Value, effective, policy);
        reasons.Add("policy: " + decision.Reason);
        var verdict = decision.Verdict switch
        {
            PolicyVerdict.Allowed => CandidateVerdict.Accepted,
            PolicyVerdict.SkippedByPolicy => CandidateVerdict.SkippedByPolicy,
            PolicyVerdict.RequiresApproval => CandidateVerdict.RequiresApproval,
            _ => CandidateVerdict.Rejected,
        };

        // 5. Typed action, only for accepted candidates.
        var action = verdict == CandidateVerdict.Accepted
            ? new ValidatedDeviceAction(
                ContentHash.ShortId("act", ContentHash.Of("mapcue.agent-action/1", request.RequestId, candidate.CandidateId)),
                policy.AppId,
                type.Value,
                effective,
                targetId,
                targetText,
                inputRef,
                direction)
            : null;
        return new(candidate.CandidateId, verdict, reasons, agentRisk, rule.Risk, effective, spec, action, element?.ElementKey);
    }

    private static bool Usable(string? selector) =>
        !string.IsNullOrWhiteSpace(selector)
        && !selector.Contains("[REDACTED:", StringComparison.Ordinal)
        && !selector.Contains('‹', StringComparison.Ordinal)
        && !selector.Any(char.IsControl);

    private static IEnumerable<(string Field, string? Text)> TextsOf(AgentExplorationResponse response)
    {
        yield return ("interpretation.proposedScreenName", response.Interpretation.ProposedScreenName);
        for (var i = 0; i < response.Facts.Count; i++)
        {
            yield return ($"facts/{i}.claim", response.Facts[i].Claim);
        }

        for (var i = 0; i < response.Hypotheses.Count; i++)
        {
            yield return ($"hypotheses/{i}.claim", response.Hypotheses[i].Claim);
        }

        for (var i = 0; i < response.ActionCandidates.Count; i++)
        {
            yield return ($"actionCandidates/{i}.intent", response.ActionCandidates[i].Intent);
            yield return ($"actionCandidates/{i}.expectedOutcome", response.ActionCandidates[i].ExpectedOutcome);
        }

        for (var i = 0; i < response.Warnings.Count; i++)
        {
            yield return ($"warnings/{i}", response.Warnings[i]);
        }
    }

    internal static ActionType? ParseActionType(string value) => value switch
    {
        "tap" => ActionType.Tap,
        "back" => ActionType.Back,
        "scroll" => ActionType.Scroll,
        "swipe" => ActionType.Swipe,
        "inputText" => ActionType.InputText,
        "hideKeyboard" => ActionType.HideKeyboard,
        "longPress" => ActionType.LongPress,
        _ => null,
    };

    internal static RiskClass? ParseRisk(string value) =>
        Enum.GetValues<RiskClass>().Select(r => (RiskClass?)r).FirstOrDefault(r => AgentRequestBuilder.Camel(r!.Value.ToString()) == value);

    private static ScrollDirection? ParseDirection(string? value) => value switch
    {
        "down" => ScrollDirection.Down,
        "up" => ScrollDirection.Up,
        "left" => ScrollDirection.Left,
        "right" => ScrollDirection.Right,
        _ => null,
    };
}

/// <summary>
/// Last check right before execution (architecture §9.3 step 6): on a fresh snapshot the state must be the same
/// (<c>identityHash</c>) and the element must still exist and be interactive. Otherwise the task blocks with
/// <see cref="BlockedReasonCode.ElementStale"/>.
/// </summary>
public static class PreExecutionGuard
{
    public static BlockedReasonCode? Check(CandidateValidation candidate, string expectedIdentityHash, NormalizedSnapshot fresh)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(fresh);
        if (candidate.Action is null || !string.Equals(fresh.IdentityHash, expectedIdentityHash, StringComparison.Ordinal))
        {
            return BlockedReasonCode.ElementStale;
        }

        if (candidate.ElementKey is null)
        {
            return null;
        }

        return fresh.Elements.Any(e => e.ElementKey == candidate.ElementKey && e.Interactive && e.Flags.HasFlag(UiElementStates.Enabled))
            ? null
            : BlockedReasonCode.ElementStale;
    }
}

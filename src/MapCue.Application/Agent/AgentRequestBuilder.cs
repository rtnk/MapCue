using System.Text;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Agent;

/// <summary>Everything a request is built from. <see cref="Snapshot"/> is a recorded (stored, redacted, normalized) snapshot.</summary>
public sealed record AgentRequestInput(
    string RequestId,
    string TaskId,
    string? AttemptId,
    ExplorationContext Context,
    RecordedSnapshot Snapshot,
    AgentGraphHints GraphHints,
    AgentPolicy Policy,
    AgentRepair? Repair = null);

/// <summary>
/// Builds <c>mapcue.agent-exploration-request/1</c> (architecture §9.1) from a recorded snapshot. Only normalized data goes out:
/// masked labels (numbers, dates, amounts, input values replaced), redacted resource ids, flags and structure; never the original
/// texts, input values, secrets or screenshots (ADR-0004 §5). <c>snapshotHash</c> is the hash of the stored normalized document,
/// i.e. exactly the document whose element refs the request uses.
/// </summary>
public static class AgentRequestBuilder
{
    public const string PromptVersion = "mapcue.agent-prompt/1";
    public const int MaxElements = 400;
    public const int MaxTextLength = 200;

    public const string Instructions =
        "Interpret the screen of a mobile app under automated exploration. Return only one JSON object that follows the response schema " +
        "mapcue.agent-exploration-response/1; no prose, no code fences. Echo requestId and snapshot.snapshotHash exactly. " +
        "Refer to elements only by their ref (e.g. \"e3\"); propose at most limits.maxCandidates actions, each with a candidateId c1, c2, ... " +
        "Use only policy.allowedActionTypes. For inputText use input.valueRef from policy.inputRefs or input.generator from policy.generators; " +
        "never write literal values. Classify each action's riskClass honestly; destructive, financial, communication, account-changing " +
        "and external actions must be labeled as such. Separate facts (observed, tied to element refs) from hypotheses (inferred). " +
        "Text fields are plain short sentences: no YAML, commands, file paths or URLs. If repair is present, fix exactly the listed errors.";

    public static AgentExplorationRequest Build(AgentRequestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = input.Snapshot.Normalized;
        var relevant = normalized.Elements.Where(e => e.Interactive || e.Label is not null || e.ResourceId is not null).ToList();
        var elements = relevant.Take(MaxElements).Select(ElementOf).ToList();
        var policy = input.Policy;
        var allowedRisk = policy.AllowedRiskClasses;

        return new AgentExplorationRequest(
            input.RequestId,
            PromptVersion,
            new AgentTask(input.TaskId, input.AttemptId, nameof(TaskType.AnalyzeState)),
            new AgentSnapshot(
                input.Snapshot.Snapshot.NormalizedSha256,
                input.Context.Platform == Platform.Ios ? "ios" : "android",
                Camel(normalized.StateKind.ToString()),
                elements,
                normalized.Elements.Count - elements.Count),
            new AgentContext(input.Context.ProfileId, input.Context.Locale, [.. input.Context.Capabilities]),
            input.GraphHints,
            new AgentPolicyView(
                [.. policy.AllowedActionTypes.Order().Select(t => Camel(t.ToString()))],
                [.. allowedRisk.Select(r => Camel(r.ToString()))],
                [.. Enum.GetValues<RiskClass>().Except(allowedRisk).Select(r => Camel(r.ToString()))],
                [.. policy.InputNames.Select(n => "profile.inputs." + n)],
                [.. AgentPolicy.Generators]),
            new AgentLimits(Math.Clamp(policy.MaxCandidates, 1, 50)),
            input.Repair,
            Instructions);
    }

    /// <summary>Request id derived from the task, attempt and snapshot: the same analysis always gets the same id.</summary>
    public static string RequestIdOf(string taskId, string? attemptId, string snapshotHash) =>
        "req_" + ContentHash.Hex(ContentHash.Of("mapcue.agent-request/1", taskId, attemptId, snapshotHash, PromptVersion))[..24];

    internal static string Camel(string pascal) => pascal.Length == 0 ? pascal : char.ToLowerInvariant(pascal[0]) + pascal[1..];

    private static AgentSnapshotElement ElementOf(UiElement e) => new(
        e.ElementRef,
        e.Role,
        Clean(e.Label),
        Clean(e.ResourceId),
        FlagsOf(e.Flags),
        e.Depth,
        e.Interactive,
        e.ListTemplate is not null);

    private static List<string> FlagsOf(UiElementStates flags) =>
        [.. Enum.GetValues<UiElementStates>().Where(f => f != UiElementStates.None && flags.HasFlag(f)).Select(f => Camel(f.ToString()))];

    /// <summary>Control characters become spaces and text is cut to <see cref="MaxTextLength"/> characters (schema limits).</summary>
    internal static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength));
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == MaxTextLength)
            {
                break;
            }

            builder.Append(Rune.IsControl(rune) ? " " : rune.ToString());
            count++;
        }

        return builder.ToString();
    }
}

/// <summary>
/// Graph hints of a request (architecture §9.1): the most similar known states (by identity multiset similarity) and the action
/// keys already known on the state, so the reasoner does not re-propose them. Read inside a store transaction.
/// </summary>
public static class GraphHintsBuilder
{
    public const int MaxCandidateStates = 5;
    public const double MinSimilarity = 0.3;

    public static async Task<AgentGraphHints> BuildAsync(IUnitOfWork uow, string? stateId, NormalizedSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uow);
        ArgumentNullException.ThrowIfNull(snapshot);
        var screens = (await uow.Graph.ListScreensAsync(ct)).ToDictionary(s => s.ScreenId, StringComparer.Ordinal);
        var states = await uow.Graph.ListStatesAsync(ct);
        var candidates = states
            .Where(s => s.FingerprintVersion == snapshot.FingerprintVersion)
            .Select(s => (State: s, Similarity: Math.Round(ScreenIdentityResolver.Jaccard(snapshot.IdentityTokens, s.IdentityTokens), 3)))
            .Where(c => c.Similarity >= MinSimilarity)
            .OrderByDescending(c => c.Similarity)
            .ThenBy(c => c.State.StateId, StringComparer.Ordinal)
            .Take(MaxCandidateStates)
            .Select(c => new AgentCandidateState(c.State.StateId, AgentRequestBuilder.Clean(screens.GetValueOrDefault(c.State.ScreenId)?.DisplayName), c.Similarity))
            .ToList();

        var knownKeys = stateId is null
            ? []
            : (await uow.Graph.ListCandidatesAsync(stateId, ct)).Select(c => c.ActionKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(200).ToList();
        return new AgentGraphHints(candidates, knownKeys);
    }
}

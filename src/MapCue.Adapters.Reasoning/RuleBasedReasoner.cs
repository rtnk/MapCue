using MapCue.Application.Agent;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Domain;

namespace MapCue.Adapters.Reasoning;

/// <summary>
/// Offline baseline reasoner without a language model (architecture §9.4, ADR-0004, R-05). Deterministic heuristics over the
/// request: taps on enabled clickable elements outside lists first (document order), then at most
/// <see cref="MaxListRows"/> list rows, scrolls on scrollable containers, text input only into fields whose resource id or label
/// names a profile input, and finally back. Risk classes come from the same <see cref="RiskClassifier"/> MapCue validates with,
/// so the baseline never under-reports risk; the answer still goes through full validation. Text fields never echo labels.
/// </summary>
public sealed class RuleBasedReasoner(RiskClassifier? classifier = null) : IExplorationReasoner
{
    public const int MaxListRows = 2;

    private readonly RiskClassifier _classifier = classifier ?? new RiskClassifier();

    public string Name => "rule-based";

    public Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        var allowed = request.Policy.AllowedActionTypes.ToHashSet(StringComparer.Ordinal);
        var proposals = new List<(string? Ref, string Type, string? Direction, AgentInput? Input, RiskClass Risk)>();
        var enabled = request.Snapshot.Elements.Where(e => e.Interactive && e.Flags.Contains("enabled")).ToList();

        void Tap(AgentSnapshotElement e) => proposals.Add((e.Ref, "tap", null, null, Risk(ActionType.Tap, e)));

        var clickable = enabled.Where(e => e.Flags.Contains("clickable") && !e.Flags.Contains("editable")).ToList();
        clickable.Where(e => !e.ListRow).ToList().ForEach(Tap);
        clickable.Where(e => e.ListRow).Take(MaxListRows).ToList().ForEach(Tap);
        foreach (var e in enabled.Where(e => e.Flags.Contains("scrollable")))
        {
            proposals.Add((e.Ref, "scroll", "down", null, RiskClass.Safe));
        }

        foreach (var e in enabled.Where(e => e.Flags.Contains("editable")))
        {
            var words = TextFold.Words(string.Join(' ', e.ResourceId?.Replace('_', ' ') ?? string.Empty, e.Label ?? string.Empty));
            var input = request.Policy.InputRefs.FirstOrDefault(r => words.Contains(TextFold.Fold(r["profile.inputs.".Length..]), StringComparer.Ordinal));
            if (input is not null)
            {
                proposals.Add((e.Ref, "inputText", null, new AgentInput(input, null), Risk(ActionType.InputText, e)));
            }
        }

        var limit = request.Limits.MaxCandidates;
        var candidates = proposals
            .Where(p => allowed.Contains(p.Type))
            .Take(allowed.Contains("back") ? limit - 1 : limit)
            .Select((p, i) => new AgentActionCandidate($"c{i + 1}", p.Ref, p.Type, p.Direction, p.Input, $"{p.Type} element {p.Ref}", Camel(p.Risk), null, 0.5))
            .ToList();
        if (allowed.Contains("back"))
        {
            candidates.Add(new AgentActionCandidate($"c{candidates.Count + 1}", null, "back", null, null, "go back", "safe", null, 0.5));
        }

        var response = new AgentExplorationResponse(
            request.RequestId,
            request.SnapshotHash,
            new AgentInterpretation(null, request.Snapshot.StateKind, null, 0.5),
            [],
            [],
            candidates,
            ["rule-based reasoner: heuristics without a language model"]);
        return Task.FromResult(new ReasonerResult(ReasonerStatus.Succeeded, AgentJson.Serialize(response), new ReasonerUsage(1, 0, 0, 0m), null));
    }

    private RiskClass Risk(ActionType type, AgentSnapshotElement element) =>
        _classifier.Classify(type, element.Role, element.ResourceId, [element.Label]).Risk;

    private static string Camel(RiskClass risk) => char.ToLowerInvariant(risk.ToString()[0]) + risk.ToString()[1..];
}

using MapCue.Application.Ports;
using MapCue.Contracts.Agent;

namespace MapCue.EndToEnd.Tests;

/// <summary>
/// A reasoner that proposes a tap on every enabled clickable element (list rows included, unlike the rule-based baseline) and back,
/// like a language model that lists everything it sees. The answer still goes through full validation.
/// </summary>
internal sealed class AllTapsReasoner : IExplorationReasoner
{
    public string Name => "all-taps";

    public Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var taps = request.Snapshot.Elements
            .Where(e => e.Interactive && e.Flags.Contains("enabled") && e.Flags.Contains("clickable") && !e.Flags.Contains("editable"))
            .Take(request.Limits.MaxCandidates - 1)
            .Select((e, i) => new AgentActionCandidate($"c{i + 1}", e.Ref, "tap", null, null, $"tap element {e.Ref}", "safe", null, 0.5))
            .ToList();
        taps.Add(new AgentActionCandidate($"c{taps.Count + 1}", null, "back", null, null, "go back", "safe", null, 0.5));
        var response = new AgentExplorationResponse(
            request.RequestId,
            request.SnapshotHash,
            new AgentInterpretation(null, request.Snapshot.StateKind, null, 0.5),
            [],
            [],
            taps,
            ["test reasoner: every clickable element"]);
        return Task.FromResult(new ReasonerResult(ReasonerStatus.Succeeded, AgentJson.Serialize(response), new ReasonerUsage(1, 0, 0, 0m), null));
    }
}

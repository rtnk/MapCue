using MapCue.Application.Ports;
using MapCue.Contracts.Agent;

namespace MapCue.Testing;

/// <summary>
/// Scripted <see cref="IExplorationReasoner"/> (ADR-0004): answers are looked up by <c>snapshotHash</c>.
/// Unknown snapshots yield <see cref="ReasonerStatus.Unavailable"/>; nothing is random.
/// </summary>
public sealed class DeterministicFakeReasoner : IExplorationReasoner
{
    private readonly Dictionary<string, Func<AgentExplorationRequest, AgentExplorationResponse>> _script = new(StringComparer.Ordinal);
    private readonly List<AgentExplorationRequest> _requests = [];

    public IReadOnlyList<AgentExplorationRequest> Requests => _requests;

    /// <summary>Scripts an answer for a snapshot; the response always echoes the request's correlation fields.</summary>
    public DeterministicFakeReasoner When(string snapshotHash, string? screenName, params AgentActionCandidate[] candidates)
    {
        _script[snapshotHash] = req => new AgentExplorationResponse(req.RequestId, req.SnapshotHash, screenName, candidates);
        return this;
    }

    /// <summary>Scripts a raw answer (e.g. a deliberately stale or malformed one for validator tests).</summary>
    public DeterministicFakeReasoner When(string snapshotHash, Func<AgentExplorationRequest, AgentExplorationResponse> respond)
    {
        _script[snapshotHash] = respond;
        return this;
    }

    public Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        _requests.Add(request);
        var usage = new ReasonerUsage(Calls: 1, InputTokens: 0, OutputTokens: 0, CostUsd: 0m);
        return Task.FromResult(_script.TryGetValue(request.SnapshotHash, out var respond)
            ? new ReasonerResult(ReasonerStatus.Succeeded, respond(request), usage, null)
            : new ReasonerResult(ReasonerStatus.Unavailable, null, usage, "no scripted answer for snapshot"));
    }
}

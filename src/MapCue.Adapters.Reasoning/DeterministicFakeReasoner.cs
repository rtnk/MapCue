using MapCue.Application.Ports;
using MapCue.Contracts.Agent;

namespace MapCue.Adapters.Reasoning;

/// <summary>
/// Scripted <see cref="IExplorationReasoner"/> for tests (ADR-0004): answers are raw text looked up by <c>snapshotHash</c>, so
/// golden (also deliberately malformed or stale) answers go through the same validation as a real model's. A script with several
/// answers returns them in order (the last one repeats), e.g. a broken answer followed by its repair. Unknown snapshots yield
/// <see cref="ReasonerStatus.Unavailable"/>; nothing is random.
/// </summary>
public sealed class DeterministicFakeReasoner : IExplorationReasoner
{
    private readonly Dictionary<string, Func<AgentExplorationRequest, int, string>> _script = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly List<AgentExplorationRequest> _requests = [];

    public string Name => "fake";

    /// <summary>Usage reported for every call (tokens and cost known, so budgets are deterministic).</summary>
    public ReasonerUsage UsagePerCall { get; init; } = new(1, 100, 50, 0.001m);

    public IReadOnlyList<AgentExplorationRequest> Requests => _requests;

    /// <summary>Scripts raw answers for a snapshot, returned in order; the last one repeats.</summary>
    public DeterministicFakeReasoner When(string snapshotHash, params string[] rawAnswers)
    {
        ArgumentOutOfRangeException.ThrowIfZero(rawAnswers.Length);
        _script[snapshotHash] = (_, n) => rawAnswers[Math.Min(n, rawAnswers.Length - 1)];
        return this;
    }

    /// <summary>Scripts an answer computed from the request (<paramref name="respond"/> gets the 0-based call number for this snapshot).</summary>
    public DeterministicFakeReasoner When(string snapshotHash, Func<AgentExplorationRequest, int, string> respond)
    {
        _script[snapshotHash] = respond;
        return this;
    }

    /// <summary>Scripts a well-formed answer that echoes the request's correlation fields.</summary>
    public DeterministicFakeReasoner When(string snapshotHash, string? screenName, params AgentActionCandidate[] candidates)
    {
        _script[snapshotHash] = (req, _) => AgentJson.Serialize(Answer(req, screenName, candidates));
        return this;
    }

    /// <summary>A valid answer for <paramref name="request"/> with the given candidates and no facts or hypotheses.</summary>
    public static AgentExplorationResponse Answer(AgentExplorationRequest request, string? screenName, params AgentActionCandidate[] candidates)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new AgentExplorationResponse(
            request.RequestId,
            request.SnapshotHash,
            new AgentInterpretation(screenName, null, null, 0.5),
            [],
            [],
            candidates,
            []);
    }

    public Task<ReasonerResult> InterpretAsync(AgentExplorationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        _requests.Add(request);
        if (!_script.TryGetValue(request.SnapshotHash, out var respond))
        {
            return Task.FromResult(new ReasonerResult(ReasonerStatus.Unavailable, null, ReasonerUsage.None, "no scripted answer for snapshot"));
        }

        var n = _counts.GetValueOrDefault(request.SnapshotHash);
        _counts[request.SnapshotHash] = n + 1;
        return Task.FromResult(new ReasonerResult(ReasonerStatus.Succeeded, respond(request, n), UsagePerCall, null));
    }
}

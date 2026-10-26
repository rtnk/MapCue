using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>Counts of the stored graph.</summary>
public sealed record GraphSummary(
    int Contexts,
    int Screens,
    int States,
    int Variants,
    int Snapshots,
    int Evidence,
    int Candidates,
    int Edges,
    int Observations,
    int Tasks,
    long Events);

/// <summary>
/// Canonical view of the graph without timestamps and event ids, sorted by content-derived ids. Two stores with the same graph give
/// the same JSON, so it is used to show that repeated imports are stable.
/// </summary>
public sealed record CanonicalGraph(
    IReadOnlyList<string> Contexts,
    IReadOnlyList<string> Screens,
    IReadOnlyList<string> States,
    IReadOnlyList<string> Variants,
    IReadOnlyList<string> Snapshots,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<string> Candidates,
    IReadOnlyList<string> Edges,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> Tasks);

public static class GraphQueries
{
    public static Task<GraphSummary> SummarizeAsync(IStateStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.InTransactionAsync(
            async uow =>
            {
                var contexts = await uow.Graph.ListContextsAsync(ct);
                var evidence = 0;
                foreach (var context in contexts)
                {
                    evidence += (await uow.Graph.ListEvidenceAsync(context.ContextId, null, ct)).Count;
                }

                return new GraphSummary(
                    contexts.Count,
                    (await uow.Graph.ListScreensAsync(ct)).Count,
                    (await uow.Graph.ListStatesAsync(ct)).Count,
                    (await uow.Graph.ListVariantsAsync(ct)).Count,
                    (await uow.Graph.ListSnapshotsAsync(ct)).Count,
                    evidence,
                    (await uow.Graph.ListCandidatesAsync(null, ct)).Count,
                    (await uow.Graph.ListEdgesAsync(ct)).Count,
                    (await uow.Graph.ListObservationsAsync(ct)).Count,
                    (await uow.Frontier.ListTasksAsync(null, ct)).Count,
                    await uow.Events.LastSeqAsync(ct));
            },
            ct);
    }

    public static Task<CanonicalGraph> CanonicalAsync(IStateStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.InTransactionAsync(
            async uow =>
            {
                var contexts = await uow.Graph.ListContextsAsync(ct);
                var evidence = new List<Evidence>();
                foreach (var context in contexts)
                {
                    evidence.AddRange(await uow.Graph.ListEvidenceAsync(context.ContextId, null, ct));
                }

                static List<string> Sorted(IEnumerable<string> rows) => [.. rows.Order(StringComparer.Ordinal)];

                return new CanonicalGraph(
                    Sorted(contexts.Select(c => $"{c.ContextId} {c.ProfileId} {c.Platform} {c.AppId} {c.AppBuildId} {c.Signature}")),
                    Sorted((await uow.Graph.ListScreensAsync(ct)).Select(s => $"{s.ScreenId} {s.ScreenKey} {s.IdentityStatus} {s.IdentitySource}")),
                    Sorted((await uow.Graph.ListStatesAsync(ct)).Select(s => $"{s.StateId} {s.ScreenId} {s.StateKind} {s.IdentityHash} v{s.FingerprintVersion}")),
                    Sorted((await uow.Graph.ListVariantsAsync(ct)).Select(v => $"{v.StateId} {v.ContextId} {v.ElementSetHash} n={v.SnapshotCount} +{v.AddedElementKeys.Count} -{v.RemovedElementKeys.Count}")),
                    Sorted((await uow.Graph.ListSnapshotsAsync(ct)).Select(s => $"{s.SnapshotId} {s.ContextId} {s.StateId} {s.RawSha256} {s.IdentityHash} {s.LayoutHash}")),
                    Sorted(evidence.Select(e => $"{e.EvidenceId} {e.Kind} {e.ContextId} {e.SnapshotId} {e.ArtifactSha256}")),
                    Sorted((await uow.Graph.ListCandidatesAsync(null, ct)).Select(c => $"{c.StateId} {c.ActionKey} {c.ActionType} {c.CanonicalSelector} {c.Status}")),
                    Sorted((await uow.Graph.ListEdgesAsync(ct)).Select(e => $"{e.EdgeId} {e.FromStateId} -> {e.ToStateId} {e.ContextId} {e.Status} n={e.ObservationCount} nd={e.Nondeterministic}")),
                    Sorted((await uow.Graph.ListObservationsAsync(ct)).Select(o => $"{o.AttemptId} {o.EdgeId} {o.ContextId} {o.BeforeSnapshotId} {o.AfterSnapshotId} {o.Outcome}")),
                    Sorted((await uow.Frontier.ListTasksAsync(null, ct)).Select(t => $"{t.TaskId} {t.Type} {t.Status} {t.ContextId} {t.FromStateId} {t.ActionKey}")));
            },
            ct);
    }
}

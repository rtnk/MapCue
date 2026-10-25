using System.Globalization;
using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>A named observation of an import plan.</summary>
public sealed record ImportObservation(string Id, RawObservation Observation);

/// <summary>A recorded transition: <see cref="Action"/> on observation <see cref="From"/> led to observation <see cref="To"/> (null when nothing was observed).</summary>
public sealed record ImportTransition(string From, ActionSpec Action, ActionOutcome Outcome, string? To, TimeSpan Duration);

/// <summary>Snapshots and transitions to import into one context (test tool, <c>mapcue graph import</c>).</summary>
public sealed record GraphImportPlan(
    ExplorationContext Context,
    IReadOnlyList<ImportObservation> Observations,
    IReadOnlyList<ImportTransition> Transitions,
    IReadOnlyCollection<string>? InputValues = null);

/// <summary>What an import changed. A repeated import of the same plan reports zero new objects and only already committed transitions.</summary>
public sealed record GraphImportResult(
    string RunId,
    int Observations,
    int Transitions,
    int NewStates,
    int NewVariants,
    int NewTasks,
    int AlreadyCommittedTransitions,
    IReadOnlyDictionary<string, string> StateOfObservation);

/// <summary>
/// Imports recorded snapshots without a device (ROADMAP STEP-03): every snapshot is stored and normalized, every observation and
/// transition is committed through <see cref="GraphCommitter"/> like an exploration attempt. Attempt ids are derived from the plan
/// (context signature, stored snapshots, action, position), so importing the same plan twice yields the same graph.
/// </summary>
public sealed class GraphImporter(SnapshotRecorder recorder, GraphCommitter committer, IStateStore store, IClock clock)
{
    public async Task<GraphImportResult> ImportAsync(GraphImportPlan plan, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Validate(plan);
        var context = plan.Context;

        // Artifacts first (architecture §12), then one transaction per observation and per transition.
        var recorded = new Dictionary<string, RecordedSnapshot>(StringComparer.Ordinal);
        foreach (var observation in plan.Observations)
        {
            recorded[observation.Id] = await recorder.RecordAsync(observation.Observation, context, plan.InputValues, ct);
        }

        var newStates = new SortedSet<string>(StringComparer.Ordinal);
        var newVariants = 0;
        var newTasks = 0;
        var stateOf = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var observation in plan.Observations)
        {
            var result = await committer.CommitObservationAsync(context, recorded[observation.Id], ct);
            stateOf[observation.Id] = result.StateId;
            if (result.NewState)
            {
                newStates.Add(result.StateId);
            }

            newVariants += result.NewVariant ? 1 : 0;
            newTasks += result.NewTasks;
        }

        var already = 0;
        for (var i = 0; i < plan.Transitions.Count; i++)
        {
            var transition = plan.Transitions[i];
            var before = recorded[transition.From];
            var after = transition.To is null ? null : recorded[transition.To];
            var attemptId = ContentHash.ShortId(
                "att",
                ContentHash.Of("mapcue.import-attempt/1", context.Signature, before.Snapshot.RawSha256, transition.Action.ActionKey, after?.Snapshot.RawSha256, i.ToString(CultureInfo.InvariantCulture)));
            var result = await committer.CommitAttemptAsync(
                new AttemptCommit(context, attemptId, before, transition.Action, transition.Outcome, transition.Duration, after),
                ct);
            already += result.AlreadyCommitted ? 1 : 0;
            newStates.UnionWith(result.NewStateIds);
            newVariants += result.NewVariants;
            newTasks += result.NewTasks;
        }

        var runId = ContentHash.ShortId("import", PlanHash(plan, recorded));
        await store.InTransactionAsync(
            async uow =>
            {
                var seq = await uow.Events.LastSeqAsync(ct);
                var latest = await uow.Frontier.GetLatestCheckpointAsync(runId, ct);
                if (latest?.EventSeq != seq)
                {
                    var stats = await uow.Frontier.GetStatsAsync(ct);
                    var id = ContentHash.ShortId("chk", ContentHash.Of("mapcue.checkpoint/1", runId, seq.ToString(CultureInfo.InvariantCulture)));
                    await uow.Frontier.InsertCheckpointAsync(new Checkpoint(id, runId, seq, stats, PlanHash(plan, recorded), clock.UtcNow), ct);
                }

                return seq;
            },
            ct);

        return new GraphImportResult(runId, plan.Observations.Count, plan.Transitions.Count, newStates.Count, newVariants, newTasks, already, stateOf);
    }

    private static void Validate(GraphImportPlan plan)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in plan.Observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Id) || !ids.Add(observation.Id))
            {
                throw new ArgumentException($"Observation id '{observation.Id}' is empty or duplicated.", nameof(plan));
            }
        }

        foreach (var transition in plan.Transitions)
        {
            if (!ids.Contains(transition.From) || (transition.To is not null && !ids.Contains(transition.To)))
            {
                throw new ArgumentException($"Transition {transition.From} → {transition.To} refers to an unknown observation.", nameof(plan));
            }
        }
    }

    private static string PlanHash(GraphImportPlan plan, Dictionary<string, RecordedSnapshot> recorded) =>
        ContentHash.Of(
        [
            "mapcue.import-plan/1",
            plan.Context.Signature,
            .. plan.Observations.Select(o => o.Id + "=" + recorded[o.Id].Snapshot.RawSha256),
            .. plan.Transitions.Select(t => string.Join('|', t.From, t.Action.ActionKey, t.Outcome.ToString(), t.To ?? string.Empty)),
        ]);
}

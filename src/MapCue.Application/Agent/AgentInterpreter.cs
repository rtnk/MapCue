using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Contracts.Agent;
using MapCue.Contracts.Schemas;
using MapCue.Domain.Graph;

namespace MapCue.Application.Agent;

/// <summary>What to interpret: a committed state, its recorded snapshot and the policy of the context's profile.</summary>
public sealed record AgentInterpretationInput(
    ExplorationContext Context,
    string StateId,
    string TaskId,
    string? AttemptId,
    RecordedSnapshot Snapshot,
    AgentPolicy Policy);

public enum AgentInterpretationStatus
{
    /// <summary>The answer was valid; see the candidates for what may be executed.</summary>
    Accepted,

    /// <summary>The answer was malformed (also after the single repair), stale or refused.</summary>
    Rejected,

    /// <summary>The reasoner could not be reached or timed out; the task blocks, there is no fallback (ADR-0004).</summary>
    ReasonerUnavailable,

    BudgetExhausted,
}

/// <summary>Result of an interpretation. <see cref="Calls"/> are the recorded calls (one, or two with a repair).</summary>
public sealed record AgentInterpretationResult(
    AgentInterpretationStatus Status,
    IReadOnlyList<AgentCall> Calls,
    AgentValidation? Validation,
    BlockedReasonCode? BlockedReason)
{
    public IEnumerable<CandidateValidation> AcceptedActions => Validation?.Accepted ?? [];
}

public static class AgentEventTypes
{
    public const string AgentCall = "agent.call";
}

/// <summary>
/// Runs one <c>AnalyzeState</c> interpretation (architecture §9, ADR-0004): builds the request from the recorded snapshot and the
/// graph, checks the budget, stores the request <em>as redacted by the store</em> and sends exactly that, stores the raw answer,
/// validates it and records an <see cref="AgentCall"/> with the decisions plus the candidates (<see cref="CandidateSource.Agent"/>)
/// in one transaction. A malformed answer gets exactly one format repair (CAP-0068); stale or refused answers get none.
/// Reasoner failures are never retried here and never fall back to another runtime.
/// </summary>
public sealed class AgentInterpreter(
    IExplorationReasoner reasoner,
    AgentResponseValidator validator,
    AgentBudget budget,
    IArtifactStore artifacts,
    IStateStore store,
    IClock clock)
{
    public const int MaxCalls = 2;
    public const int MaxRepairErrors = 20;

    public async Task<AgentInterpretationResult> InterpretAsync(AgentInterpretationInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var hints = await store.InTransactionAsync(uow => GraphHintsBuilder.BuildAsync(uow, input.StateId, input.Snapshot.Normalized, ct), ct);
        var requestId = AgentRequestBuilder.RequestIdOf(input.TaskId, input.AttemptId, input.Snapshot.Snapshot.NormalizedSha256);
        var calls = new List<AgentCall>();
        AgentRepair? repair = null;

        for (var callNumber = 1; callNumber <= MaxCalls; callNumber++)
        {
            var started = clock.UtcNow;
            var built = AgentRequestBuilder.Build(new AgentRequestInput(requestId, input.TaskId, input.AttemptId, input.Context, input.Snapshot, hints, input.Policy, repair));
            var serialized = AgentJson.Serialize(built);
            var stored = await artifacts.PutTextAsync(serialized, ArtifactKind.AgentRequest, ct);
            var (request, requestErrors) = Reread(stored.RedactedText!);
            AgentCall Call(AgentCallStatus status, IReadOnlyList<string> errors, ReasonerUsage usage, string? responseSha, IReadOnlyList<AgentCandidateDecision>? decisions = null) => new(
                ContentHash.ShortId("call", ContentHash.Of("mapcue.agent-call/1", requestId, callNumber.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                requestId,
                callNumber,
                input.Context.ContextId,
                input.StateId,
                input.TaskId,
                input.AttemptId,
                reasoner.Name,
                AgentRequestBuilder.PromptVersion,
                built.SnapshotHash,
                stored.Ref.Sha256,
                responseSha,
                status,
                errors,
                decisions ?? [],
                usage.InputTokens,
                usage.OutputTokens,
                usage.CostUsd,
                (long)(clock.UtcNow - started).TotalMilliseconds,
                started);

            if (request is null)
            {
                // Fail closed: what would be sent is not what was built (e.g. redaction hit a hash). Nothing is sent.
                var refused = Call(AgentCallStatus.Rejected, ["request does not match its schema after redaction; not sent", .. requestErrors], ReasonerUsage.None, null);
                await RecordAsync(input, refused, null, ct);
                calls.Add(refused);
                return new(AgentInterpretationStatus.Rejected, calls, null, null);
            }

            var estimate = AgentBudget.EstimateTokens(stored.RedactedText) + 2_000;
            if (budget.Check(estimate) is { } exhausted)
            {
                var skipped = Call(AgentCallStatus.BudgetExhausted, [exhausted], ReasonerUsage.None, null);
                await RecordAsync(input, skipped, null, ct);
                calls.Add(skipped);
                return new(AgentInterpretationStatus.BudgetExhausted, calls, null, BlockedReasonCode.ReasonerBudgetExhausted);
            }

            ReasonerResult result;
            try
            {
                result = await reasoner.InterpretAsync(request, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // A crashing adapter is an unavailable reasoner (fail closed, recorded); the message is not echoed (it may quote input).
                result = new ReasonerResult(ReasonerStatus.Unavailable, null, ReasonerUsage.None, $"the reasoner failed ({ex.GetType().Name})");
            }

            budget.Record(result.Usage, AgentBudget.EstimateTokens(stored.RedactedText) + AgentBudget.EstimateTokens(result.RawResponse));

            StoredArtifact? answer = result.RawResponse is null ? null : await artifacts.PutTextAsync(result.RawResponse, ArtifactKind.AgentResponse, ct);
            if (result.Status is ReasonerStatus.Unavailable or ReasonerStatus.TimedOut)
            {
                var status = result.Status == ReasonerStatus.TimedOut ? AgentCallStatus.ReasonerTimedOut : AgentCallStatus.ReasonerUnavailable;
                var failed = Call(status, [result.Detail ?? status.ToString()], result.Usage, answer?.Ref.Sha256);
                await RecordAsync(input, failed, null, ct);
                calls.Add(failed);
                return new(AgentInterpretationStatus.ReasonerUnavailable, calls, null, BlockedReasonCode.ReasonerUnavailable);
            }

            // The answer is validated as stored (redacted), so what is recorded is exactly what was judged.
            var validation = result.Status == ReasonerStatus.Succeeded
                ? validator.Validate(answer?.RedactedText, request, input.Snapshot.Normalized, input.Policy)
                : AgentValidation.Failed(AgentCallStatus.SchemaInvalid, [result.Detail ?? "the reasoner output could not be read"]);
            var call = Call(validation.Status, validation.Errors, result.Usage, answer?.Ref.Sha256, [.. validation.Candidates.Select(c => c.ToDecision())]);
            await RecordAsync(input, call, validation, ct);
            calls.Add(call);

            if (validation.Status == AgentCallStatus.Accepted)
            {
                return new(AgentInterpretationStatus.Accepted, calls, validation, null);
            }

            if (!validation.Repairable || callNumber == MaxCalls)
            {
                return new(AgentInterpretationStatus.Rejected, calls, validation, null);
            }

            repair = new AgentRepair([.. validation.Errors.Take(MaxRepairErrors).Select(e => e.Length > 300 ? e[..300] : e)]);
        }

        throw new InvalidOperationException("unreachable");
    }

    private static (AgentExplorationRequest? Request, IReadOnlyList<string> Errors) Reread(string storedText)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(storedText);
            var errors = ContractSchemas.AgentExplorationRequest.Validate(document.RootElement);
            return errors.Count > 0
                ? (null, [.. errors.Select(e => e.ToString())])
                : (AgentJson.Deserialize<AgentExplorationRequest>(document.RootElement), []);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, ["stored request is not valid JSON"]);
        }
    }

    private Task<int> RecordAsync(AgentInterpretationInput input, AgentCall call, AgentValidation? validation, CancellationToken ct) =>
        store.InTransactionAsync(
            async uow =>
            {
                if (!await uow.AgentCalls.InsertIfAbsentAsync(call, ct))
                {
                    return 0;
                }

                var now = clock.UtcNow;
                foreach (var candidate in validation?.Candidates.Where(c => c.Spec is not null && c.Verdict != CandidateVerdict.Rejected) ?? [])
                {
                    var spec = candidate.Spec!;
                    var status = candidate.Verdict == CandidateVerdict.Accepted ? CandidateStatus.Proposed : CandidateStatus.SkippedByPolicy;
                    await uow.Graph.InsertCandidateIfAbsentAsync(
                        new ActionCandidate(input.StateId, spec.ActionKey, spec.Type, spec.CanonicalSelector, spec.ParamClass, CandidateSource.Agent, spec.Risk, status, now),
                        ct);
                    await uow.Graph.UpsertCandidateAssessmentAsync(new CandidateAssessment(input.StateId, spec.ActionKey, input.Context.ContextId, spec.Risk, status, now), ct);
                }

                await uow.Events.AppendAsync(
                    ContentHash.ShortId("evt", ContentHash.Of("mapcue.agent-call-event/1", call.CallId)),
                    AgentEventTypes.AgentCall,
                    now,
                    input.Context.ContextId,
                    call.CallId,
                    GraphJson.Serialize(new Dictionary<string, string?> { ["status"] = call.Status.ToString(), ["reasoner"] = call.Reasoner, ["requestId"] = call.RequestId }),
                    ct);
                return 1;
            },
            ct);
}

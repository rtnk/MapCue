using System.Globalization;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.ScreenContracts;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.Exploration;

/// <summary>Event types of screen captures (STEP-08).</summary>
public static class CaptureEventTypes
{
    public const string ScreenCaptured = "screen.captured";
}

/// <summary>
/// Screen captures of a run (STEP-08, architecture §14, <c>contracts</c> in the configuration). When the context has no explore task left,
/// every state seen in the context (except transient <c>loading</c> states) gets an <c>AnalyzeScreen</c> task and, on Android with
/// <c>contracts.keyboardFocus</c>, an <c>AuditAccessibility</c> task. Both are ordered by distance from the current state (the full
/// capture of a state before its keyboard probe) and never commit transitions, so the explored graph does not depend on them.
/// <list type="bullet">
/// <item><c>AnalyzeScreen</c>: navigate to the state, CLI <c>hierarchy</c> (fidelity full), then a fast observation that must resolve to the same
/// state; only then is the full snapshot stored as a snapshot of the state (never placed by the resolver: the full tree may differ from
/// the fast one, ADR-0002).</item>
/// <item><c>AuditAccessibility</c> (PoC, architecture §14.2): navigate, observe, then <c>pressKey Tab</c> up to <c>contracts.maxTabPresses</c>
/// times with a fast observation after each press; the elements reporting <c>focused</c> give the observed focus order (level
/// <c>automated</c>). It stops when focus returns to the first focused element, does not move, is never observed, or the app is left.</item>
/// </list>
/// A capture that cannot reach or keep its state fails its task (no retry, no block: the contract then says <c>notTested</c>).
/// </summary>
public sealed partial class ExplorationEngine
{
    private sealed partial class RunSession
    {
        private bool _captureTasksScheduled;

        private async Task<(FrontierTask Task, int Cost)?> NextCaptureTaskAsync(IReadOnlyList<FrontierTask> pending, CancellationToken ct)
        {
            var contracts = _e.Settings.Contracts;
            if (!contracts.Enabled)
            {
                return null;
            }

            if (!_captureTasksScheduled)
            {
                _captureTasksScheduled = true;
                if (await ScheduleCaptureTasksAsync(ct) > 0)
                {
                    pending = (await _e.Store.InTransactionAsync(uow => uow.Frontier.ListTasksAsync(FrontierTaskStatus.Pending, ct), ct))
                        .Where(t => t.ContextId == _context!.ContextId)
                        .ToList();
                }
            }

            var captures = pending.Where(t => t.Type is TaskType.AnalyzeScreen or TaskType.AuditAccessibility).ToList();
            if (captures.Count == 0)
            {
                return null;
            }

            var graph = await NavigationGraphAsync(ct);
            var fromCurrent = _current is null ? null : graph.Distances(_current);
            var fromStart = _start is null ? null : graph.Distances(_start);
            int Distance(FrontierTask t) =>
                fromCurrent is not null && fromCurrent.TryGetValue(t.FromStateId!, out var d) ? d
                : fromStart is not null && fromStart.TryGetValue(t.FromStateId!, out var s) ? s + 1
                : FrontierScheduler.Unreachable;

            var best = captures
                .Select(t => (Task: t, Distance: Distance(t)))
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Task.FromStateId, StringComparer.Ordinal)
                .ThenBy(x => x.Task.Type == TaskType.AnalyzeScreen ? 0 : 1)
                .ThenBy(x => x.Task.TaskId, StringComparer.Ordinal)
                .First();
            var way = best.Distance >= FrontierScheduler.Unreachable ? 1 : best.Distance;
            var cost = way + (best.Task.Type == TaskType.AuditAccessibility ? contracts.MaxTabPresses : 0);
            return (best.Task, cost);
        }

        /// <summary>Capture tasks for every non-transient state seen in this context (<c>ON CONFLICT DO NOTHING</c>). Returns the number created.</summary>
        private Task<int> ScheduleCaptureTasksAsync(CancellationToken ct)
        {
            var context = _context!;
            var contracts = _e.Settings.Contracts;
            var keyboard = contracts.KeyboardFocus && context.Platform == Platform.Android;
            return _e.Store.InTransactionAsync(
                async uow =>
                {
                    var now = _e.Clock.UtcNow;
                    var states = (await uow.Graph.ListStatesAsync(ct)).ToDictionary(s => s.StateId, StringComparer.Ordinal);
                    var created = 0;
                    foreach (var variant in (await uow.Graph.ListVariantsAsync(ct)).Where(v => v.ContextId == context.ContextId).OrderBy(v => v.StateId, StringComparer.Ordinal))
                    {
                        if (!states.TryGetValue(variant.StateId, out var state) || state.StateKind == StateKind.Loading)
                        {
                            continue;
                        }

                        var kinds = new List<(TaskType Type, string Key)>();
                        if (contracts.AnalyzeScreens)
                        {
                            kinds.Add((TaskType.AnalyzeScreen, TaskKeys.AnalyzeScreen(state.StateId, context)));
                        }

                        if (keyboard)
                        {
                            kinds.Add((TaskType.AuditAccessibility, TaskKeys.AuditAccessibility(state.StateId, context)));
                        }

                        foreach (var (type, key) in kinds)
                        {
                            var task = new FrontierTask(GraphCommitter.TaskIdOf(key), key, type, FrontierTaskStatus.Pending, 0, 0, context.ContextId, state.StateId, null, null, null, null, null, 0, now, now);
                            if (await uow.Frontier.InsertTaskIfAbsentAsync(task, ct))
                            {
                                created++;
                                await AppendAsync(uow, GraphEventTypes.TaskCreated, task.TaskId, new() { ["type"] = type.ToString(), ["status"] = task.Status.ToString() }, ct);
                            }
                        }
                    }

                    return created;
                },
                ct);
        }

        private async Task CaptureAsync(FrontierTask task, CancellationToken ct)
        {
            var kind = task.Type == TaskType.AnalyzeScreen ? ScreenCaptureKind.FullHierarchy : ScreenCaptureKind.KeyboardFocus;
            try
            {
                if (!await NavigateToAsync(task.FromStateId!, ct))
                {
                    await EndCaptureAsync(task, kind, ScreenCaptureStatus.Unreachable, null, null, [], "the state could not be reached over known edges after a reset", ct);
                    return;
                }

                if (kind == ScreenCaptureKind.FullHierarchy)
                {
                    await CaptureFullHierarchyAsync(task, ct);
                }
                else
                {
                    await ProbeKeyboardFocusAsync(task, ct);
                }

                _consecutiveDeviceFailures = 0;
            }
            catch (BudgetStopException)
            {
                // Nothing was captured: the next run takes the task again.
                await EndTaskAsync(task, FrontierTaskStatus.Pending, null, null, CancellationToken.None);
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await EndTaskAsync(task, FrontierTaskStatus.Pending, null, null, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                _consecutiveDeviceFailures++;
                _current = null;
                var error = $"{ex.GetType().Name}: {_e.Redact(ex.Message)}";
                await EndCaptureAsync(task, kind, ScreenCaptureStatus.Failed, null, null, [], error, CancellationToken.None);
                Progress($"failed     {task.TaskId} ({task.Type}): {ex.GetType().Name}");
            }
        }

        private async Task CaptureFullHierarchyAsync(FrontierTask task, CancellationToken ct)
        {
            var stateId = task.FromStateId!;
            var full = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Full), ct);

            // The full tree is attributed to the state only when the screen is still that state right after it (fast observation, resolver).
            var check = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Fast), ct);
            var after = await ResolveAsync(SnapshotNormalizer.Normalize(check, _normalization), ct);
            _current = after;
            if (after != stateId)
            {
                await EndCaptureAsync(task, ScreenCaptureKind.FullHierarchy, ScreenCaptureStatus.Diverged, null, null, [], $"the screen was {after ?? "unknown"} after the full observation", ct);
                Progress($"screen     {stateId} diverged (now {after ?? "unknown"})");
                return;
            }

            var recorded = await _recorder.RecordAsync(full, _context!, _o.InputValues, ct);
            var evidenceIds = await _e.Store.InTransactionAsync(
                async uow =>
                {
                    var (snapshot, _) = await uow.Graph.GetOrInsertSnapshotAsync(recorded.Snapshot with { StateId = stateId, AttemptId = null }, ct);
                    var ids = new List<string>();
                    foreach (var evidence in recorded.Evidence)
                    {
                        await uow.Graph.InsertEvidenceIfAbsentAsync(evidence with { SnapshotId = snapshot.SnapshotId }, ct);
                        ids.Add(evidence.EvidenceId);
                    }

                    await CompleteCaptureAsync(uow, task, ScreenCaptureKind.FullHierarchy, ScreenCaptureStatus.Captured, snapshot.SnapshotId, null, ids, null, ct);
                    return ids;
                },
                ct);
            _counters = _counters with { ScreenCaptures = _counters.ScreenCaptures + 1 };
            Progress($"screen     {stateId} full hierarchy: {recorded.Normalized.Elements.Count} elements, {KeyboardTargets.Of(recorded.Normalized).Count} targets, evidence={evidenceIds.Count}");
        }

        private async Task ProbeKeyboardFocusAsync(FrontierTask task, CancellationToken ct)
        {
            var stateId = task.FromStateId!;
            var max = _e.Settings.Contracts.MaxTabPresses;
            var initialObservation = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Fast), ct);
            var initial = await _recorder.RecordAsync(initialObservation, _context!, _o.InputValues, ct);
            var start = await ResolveAsync(initial.Normalized, ct);
            _current = start;
            if (start != stateId)
            {
                await EndCaptureAsync(task, ScreenCaptureKind.KeyboardFocus, ScreenCaptureStatus.Diverged, null, null, [], $"the screen was {start ?? "unknown"} before the first key press", ct);
                return;
            }

            static IReadOnlyList<string> Focused(NormalizedSnapshot s) =>
                [.. s.Elements.Where(e => e.Flags.HasFlag(UiElementStates.Focused)).Select(e => e.ElementKey).Distinct(StringComparer.Ordinal)];

            var steps = new List<FocusProbeStep>();
            var initialFocused = Focused(initial.Normalized);
            var targets = KeyboardTargets.Of(initial.Normalized);
            string? first = null;
            var stop = FocusProbeStops.MaxPresses;
            var cycle = false;
            var shortTask = task.TaskId.Length > 40 ? task.TaskId[..40] : task.TaskId;
            NormalizedSnapshot? last = initial.Normalized;
            for (var press = 1; press <= max; press++)
            {
                ChargeAction();
                var action = new ValidatedDeviceAction($"key-{shortTask}-{press.ToString(CultureInfo.InvariantCulture)}", _profile.AppId, ActionType.PressKey, RiskClass.Safe, null, null, null) { Key = DeviceKeys.Tab };
                _counters = _counters with { KeyPresses = _counters.KeyPresses + 1 };
                var execution = await _e.Device.ExecuteAsync(_device, action, ct);
                if (execution.Outcome != ActionOutcome.Succeeded)
                {
                    steps.Add(new FocusProbeStep(press, execution.Outcome, [], null, null));
                    stop = FocusProbeStops.ActionFailed;
                    last = null;
                    break;
                }

                var observation = await _e.Device.ObserveAsync(_device, new ObservationRequest(_profile.AppId, ObservationFidelity.Fast), ct);
                var recorded = await _recorder.RecordAsync(observation, _context!, _o.InputValues, ct);
                last = recorded.Normalized;
                if (AppPresenceDetector.Of(recorded.Normalized, _profile.AppId) == AppPresence.Left)
                {
                    steps.Add(new FocusProbeStep(press, execution.Outcome, [], recorded.Normalized.IdentityHash, recorded.Snapshot.RawSha256));
                    stop = FocusProbeStops.AppLeft;
                    last = null;
                    break;
                }

                var focused = Focused(recorded.Normalized);
                steps.Add(new FocusProbeStep(press, execution.Outcome, focused, recorded.Normalized.IdentityHash, recorded.Snapshot.RawSha256));
                if (focused.Count > 0)
                {
                    // The same element twice in a row: focus did not move. That is never a closed cycle (emulator, STEP-08: a false
                    // "cycle" after two presses on one element gave a false fail), except when the only keyboard target of the state holds
                    // the focus: a cycle of one element that covers every target.
                    var before = steps.Count >= 2 ? steps[^2].FocusedElementKeys : initialFocused;
                    var previous = before.Count > 0 ? before[0] : null;
                    if (previous == focused[0])
                    {
                        var visited = steps.SelectMany(s => s.FocusedElementKeys.Take(1)).Distinct(StringComparer.Ordinal).ToList();
                        cycle = visited.Count == 1 && targets.Count == 1 && targets[0] == focused[0];
                        stop = cycle ? FocusProbeStops.CycleClosed : FocusProbeStops.FocusStuck;
                        break;
                    }

                    if (first is null)
                    {
                        first = focused[0];
                    }
                    else if (focused[0] == first)
                    {
                        cycle = true;
                        stop = FocusProbeStops.CycleClosed;
                        break;
                    }
                }
                else if (first is null && press >= 3)
                {
                    // Three presses without any focused element: focus is not observable this way (the PoC question, architecture §14.2).
                    stop = FocusProbeStops.NoFocus;
                    break;
                }
            }

            _current = last is null ? null : await ResolveAsync(last, ct);
            var document = new FocusProbeDocument(
                FocusProbeDocument.CurrentSchemaVersion,
                stateId,
                _context!.ContextId,
                DeviceKeys.Tab,
                max,
                initial.Normalized.IdentityHash,
                initial.Snapshot.RawSha256,
                initialFocused,
                targets,
                steps,
                cycle,
                stop);
            var artifact = await _e.Artifacts.PutTextAsync(GraphJson.Serialize(document), ArtifactKind.FocusProbe, ct);
            var evidence = new Evidence(
                Evidence.IdOf(EvidenceKind.FocusProbe, artifact.Ref.Sha256, _context.ContextId, null),
                EvidenceKind.FocusProbe,
                artifact.Ref.Sha256,
                artifact.Ref.Length,
                _context.ContextId,
                null,
                null,
                artifact.Redaction.RedactionCount,
                artifact.Redaction.Rules,
                _e.Clock.UtcNow);
            var status = stop is FocusProbeStops.ActionFailed or FocusProbeStops.AppLeft ? ScreenCaptureStatus.Failed
                : first is null ? ScreenCaptureStatus.NoFocusObserved
                : ScreenCaptureStatus.Captured;
            await _e.Store.InTransactionAsync(
                async uow =>
                {
                    await uow.Graph.InsertEvidenceIfAbsentAsync(evidence, ct);
                    await CompleteCaptureAsync(uow, task, ScreenCaptureKind.KeyboardFocus, status, null, artifact.Ref.Sha256, [evidence.EvidenceId], status == ScreenCaptureStatus.Captured ? null : stop, ct);
                    return 0;
                },
                ct);
            _counters = _counters with { ScreenCaptures = _counters.ScreenCaptures + 1 };
            var reached = document.FocusOrder().Intersect(document.TargetElementKeys, StringComparer.Ordinal).Count();
            Progress($"keyboard   {stateId} presses={steps.Count} focused={document.FocusOrder().Count} reached={reached}/{document.TargetElementKeys.Count} stop={stop}");
        }

        /// <summary>Records the capture and ends its task in one transaction (<c>Completed</c> for a capture, <c>Failed</c> otherwise).</summary>
        private async Task CompleteCaptureAsync(IUnitOfWork uow, FrontierTask task, ScreenCaptureKind kind, ScreenCaptureStatus status, string? snapshotId, string? artifactSha256, IReadOnlyList<string> evidenceIds, string? detail, CancellationToken ct)
        {
            var now = _e.Clock.UtcNow;
            await uow.Contracts.UpsertCaptureAsync(
                new ScreenCapture(ScreenCapture.IdOf(kind, task.FromStateId!, task.ContextId), kind, task.FromStateId!, task.ContextId, task.TaskId, status, snapshotId, artifactSha256, evidenceIds, detail is null ? null : _e.Redact(detail), now),
                ct);
            var stored = await uow.Frontier.GetTaskAsync(task.TaskId, ct) ?? task;
            var taskStatus = status is ScreenCaptureStatus.Captured or ScreenCaptureStatus.NoFocusObserved ? FrontierTaskStatus.Completed : FrontierTaskStatus.Failed;
            await uow.Frontier.UpdateTaskAsync(stored with { Status = taskStatus, LeaseOwner = null, LeaseExpiresAt = null, UpdatedAt = now }, ct);
            await AppendAsync(uow, CaptureEventTypes.ScreenCaptured, task.TaskId, new() { ["kind"] = kind.ToString(), ["stateId"] = task.FromStateId, ["status"] = status.ToString() }, ct);
        }

        private Task<int> EndCaptureAsync(FrontierTask task, ScreenCaptureKind kind, ScreenCaptureStatus status, string? snapshotId, string? artifactSha256, IReadOnlyList<string> evidenceIds, string detail, CancellationToken ct) =>
            _e.Store.InTransactionAsync(
                async uow =>
                {
                    await CompleteCaptureAsync(uow, task, kind, status, snapshotId, artifactSha256, evidenceIds, detail, ct);
                    return 0;
                },
                ct);
    }
}

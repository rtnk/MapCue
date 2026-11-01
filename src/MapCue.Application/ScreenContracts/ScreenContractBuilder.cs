using System.Globalization;
using System.Text.RegularExpressions;
using MapCue.Application.Graph;
using MapCue.Application.Ports;
using MapCue.Application.Profiles;
using MapCue.Contracts.ScreenContracts;
using MapCue.Domain;
using MapCue.Domain.Graph;

namespace MapCue.Application.ScreenContracts;

/// <summary>Values of the accessibility matrix and of focus graphs (schema <c>screen-contract.v1</c>).</summary>
public static class A11yStatus
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Present = "present";
    public const string Issues = "issues";
    public const string Unverified = "unverified";
    public const string Blocked = "blocked";
    public const string NotTested = "notTested";
    public const string Unsupported = "unsupported";

    /// <summary>Statuses an <c>inspected</c> cell may have: never <see cref="Pass"/> (architecture §14.1).</summary>
    public static IReadOnlySet<string> InspectedAllowed { get; } = new HashSet<string>(StringComparer.Ordinal) { Present, Issues, NotTested, Unsupported };
}

/// <summary>
/// Builds screen contracts (<c>mapcue.screen-contract/1</c>, STEP-08, architecture §14) from the store and the artifact store only: no device,
/// no reasoner, no clock, so the same store always gives the same contracts (and content hashes).
/// <list type="bullet">
/// <item><b>Scope:</b> one contract per logical screen and comparison group (app, platform, build, locale; the STEP-07 group), over the states of
/// the screen seen in the group's contexts.</item>
/// <item><b>Snapshots:</b> per variant (state, context) the full hierarchy of <c>AnalyzeScreen</c> when captured, else the latest fast snapshot
/// (source <c>fast</c>; tree rules then say <c>notTested</c>, because the fast tree drops <c>important-for-accessibility</c>, G-02).</item>
/// <item><b>Elements:</b> union by element key with presence per context and state; <c>visibleWhen</c> is the STEP-07 guard hypothesis of the
/// element (inferred / observed / refuted), never more.</item>
/// <item><b>Tree rules</b> (level <c>inspected</c>, on full snapshots): unlabeled keyboard target, duplicate label, target hidden from
/// accessibility services, small target (px, density unknown). They are things to check; an <c>inspected</c> cell is <c>present</c> or
/// <c>issues</c>, never <c>pass</c>.</item>
/// <item><b>Keyboard:</b> the order predicted from the tree (top to bottom, left to right) is <c>predicted</c>/<c>unverified</c>; the focus
/// graph of <c>AuditAccessibility</c> is <c>automated</c>: <c>pass</c> only when focus cycled and reached every keyboard target of the state.</item>
/// <item><b>Screen readers:</b> TapCue 0.1.0 cannot drive TalkBack or VoiceOver (G-05), so their <c>automated</c> cells are <c>unsupported</c>
/// and <c>assistiveVerified</c> comes only from manual records (<c>mapcue a11y record</c>). iOS keyboard cells are <c>unsupported</c> (G-04).</item>
/// </list>
/// </summary>
public static partial class ScreenContractBuilder
{
    public const string MinTargetPx = "48";

    private const int MinTargetSize = 48;

    public static async Task<IReadOnlyList<ScreenContractDocument>> BuildAsync(IStateStore store, IArtifactStore artifacts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(artifacts);
        var data = await store.InTransactionAsync(
            async uow => new Data(
                (await uow.Graph.ListContextsAsync(ct)).ToDictionary(c => c.ContextId, StringComparer.Ordinal),
                [.. (await uow.Graph.ListScreensAsync(ct)).OrderBy(s => s.ScreenKey, StringComparer.Ordinal).ThenBy(s => s.ScreenId, StringComparer.Ordinal)],
                (await uow.Graph.ListStatesAsync(ct)).ToDictionary(s => s.StateId, StringComparer.Ordinal),
                await uow.Graph.ListVariantsAsync(ct),
                await uow.Graph.ListSnapshotsAsync(ct),
                await uow.Contracts.ListCapturesAsync(null, ct),
                await uow.Contracts.ListRecordsAsync(ct),
                await EvidenceBySnapshotAsync(uow, ct)),
            ct);
        var guards = (await ProfileAnalysis.AnalyzeAsync(store, ct)).Guards;
        var loader = new SnapshotLoader(artifacts);

        var result = new List<ScreenContractDocument>();
        foreach (var screen in data.Screens)
        {
            var screenStates = data.States.Values.Where(s => s.ScreenId == screen.ScreenId).Select(s => s.StateId).ToHashSet(StringComparer.Ordinal);
            var variants = data.Variants
                .Where(v => screenStates.Contains(v.StateId) && data.Contexts.ContainsKey(v.ContextId))
                .GroupBy(v => ProfileAnalysis.ComparisonGroupOf(data.Contexts[v.ContextId]), StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in variants)
            {
                var contract = await BuildOneAsync(screen, group.Key, [.. group.OrderBy(v => v.StateId, StringComparer.Ordinal).ThenBy(v => v.ContextId, StringComparer.Ordinal)], data, guards, loader, ct);
                result.Add(contract with { ContentHash = ScreenContractJson.ContentHashOf(contract) });
            }
        }

        return result;
    }

    /// <summary>The short id of a comparison group, used in contract paths (<c>screens/&lt;screenKey&gt;/&lt;groupId&gt;/</c>).</summary>
    public static string GroupIdOf(string groupKey) => ContentHash.ShortId("grp", ContentHash.Of("mapcue.contract-group/1", groupKey), 12);

    private static async Task<ScreenContractDocument> BuildOneAsync(
        LogicalScreen screen,
        string groupKey,
        List<ScreenVariant> variants,
        Data data,
        IReadOnlyList<GuardHypothesis> guards,
        SnapshotLoader loader,
        CancellationToken ct)
    {
        var first = data.Contexts[variants[0].ContextId];
        var platform = first.Platform;
        var contextIds = variants.Select(v => v.ContextId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var stateIds = variants.Select(v => v.StateId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var captures = data.Captures.Where(c => contextIds.Contains(c.ContextId, StringComparer.Ordinal) && stateIds.Contains(c.StateId, StringComparer.Ordinal))
            .ToDictionary(c => (c.Kind, c.StateId, c.ContextId));

        var views = new List<VariantView>();
        foreach (var variant in variants)
        {
            views.Add(await ViewOfAsync(variant, data, captures, loader, ct));
        }

        var elements = Elements(views, platform, groupKey, guards);
        var findings = new List<AccessibilityFinding>();
        var predicted = new List<PredictedFocusOrder>();
        var observed = new List<ObservedFocusGraph>();
        var reader = platform == Platform.Ios ? AccessibilityTechnology.VoiceOver : AccessibilityTechnology.TalkBack;
        foreach (var view in views)
        {
            if (view.Source == "full")
            {
                findings.AddRange(TreeRules(view, reader, platform));
            }

            if (view.Snapshot is not null)
            {
                predicted.Add(new PredictedFocusOrder(view.Variant.ContextId, view.Variant.StateId, "predicted", A11yStatus.Unverified, "Reading order of the keyboard targets in the tree: top to bottom, then left to right (bounds), tree order for ties. Not observed with any assistive technology (CAP-0115).", PredictedOrder(view.Snapshot)));
            }

            if (platform == Platform.Android && captures.TryGetValue((ScreenCaptureKind.KeyboardFocus, view.Variant.StateId, view.Variant.ContextId), out var probe))
            {
                var graph = await FocusGraphAsync(view, probe, loader, ct);
                observed.Add(graph);
                findings.AddRange(graph.Unreached.Where(_ => graph.Status == A11yStatus.Fail).Select(key => new AccessibilityFinding(
                    "keyboard.unreachable",
                    nameof(AccessibilityTechnology.Keyboard),
                    "automated",
                    key,
                    view.Variant.ContextId,
                    view.Variant.StateId,
                    "The focus cycle closed without reaching this keyboard target (pressKey Tab, focused flag).",
                    probe.EvidenceIds)));
            }
        }

        findings = [.. findings.OrderBy(f => f.Technology, StringComparer.Ordinal).ThenBy(f => f.RuleId, StringComparer.Ordinal).ThenBy(f => f.StateId, StringComparer.Ordinal).ThenBy(f => f.ContextId, StringComparer.Ordinal).ThenBy(f => f.ElementKey, StringComparer.Ordinal)];
        var records = data.Records
            .Where(r => r.ScreenId == screen.ScreenId && contextIds.Contains(r.ContextId, StringComparer.Ordinal))
            .OrderBy(r => r.RecordedAt)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .ToList();
        var matrix = Matrix(platform, views, findings, observed, captures.Values.ToList(), records);
        var evidence = views.SelectMany(v => v.EvidenceIds)
            .Concat(observed.SelectMany(o => o.EvidenceIds))
            .Concat(records.Select(r => r.EvidenceId))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return new ScreenContractDocument(
            ScreenContractDocument.CurrentSchemaVersion,
            screen.ScreenId,
            screen.ScreenKey,
            screen.DisplayName,
            0,
            string.Empty,
            new ContractGroup(groupKey, first.AppId, platform.ToString(), first.AppBuildId, first.Locale),
            stateIds.Select(id => data.States[id].FingerprintVersion).DefaultIfEmpty(SnapshotNormalizer.FingerprintVersion).Max(),
            [.. stateIds.Select(id => new ContractState(id, data.States[id].StateKind.ToString(), data.States[id].IdentityHash, [.. variants.Where(v => v.StateId == id).Select(v => v.ContextId).Order(StringComparer.Ordinal)]))],
            [.. contextIds.Select(id => data.Contexts[id]).Select(c => new ContractContext(c.ContextId, c.ProfileId, c.ScenarioId, [.. c.Capabilities.Order(StringComparer.Ordinal)]))],
            elements,
            [.. views.Select(v => new ContractVariant(v.Variant.StateId, v.Variant.ContextId, v.Variant.ElementSetHash, [.. v.Variant.IdentityTokens], [.. v.Variant.AddedElementKeys], [.. v.Variant.RemovedElementKeys], v.Source, v.EvidenceIds))],
            new ContractAccessibility(
                matrix,
                findings,
                [.. records.Select(r => new AccessibilityRecordRef(r.RecordId, r.Technology.ToString(), r.StateId, r.ElementKey, r.Property, r.Result.ToString(), r.ContextId, r.ProtocolVersion, [r.EvidenceId]))]),
            new ContractKeyboard(predicted, observed),
            evidence);
    }

    // ------------------------------------------------------------------ snapshots of variants

    private sealed record Data(
        Dictionary<string, ExplorationContext> Contexts,
        List<LogicalScreen> Screens,
        Dictionary<string, ScreenState> States,
        IReadOnlyList<ScreenVariant> Variants,
        IReadOnlyList<UiSnapshot> Snapshots,
        IReadOnlyList<ScreenCapture> Captures,
        IReadOnlyList<AccessibilityRecord> Records,
        Dictionary<string, List<string>> EvidenceBySnapshot);

    private sealed record VariantView(ScreenVariant Variant, NormalizedSnapshot? Snapshot, string Source, IReadOnlyList<string> EvidenceIds);

    private sealed class SnapshotLoader(IArtifactStore artifacts)
    {
        private readonly Dictionary<string, NormalizedSnapshot?> _snapshots = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FocusProbeDocument?> _probes = new(StringComparer.Ordinal);

        public async Task<NormalizedSnapshot?> NormalizedAsync(string sha256, CancellationToken ct)
        {
            if (!_snapshots.TryGetValue(sha256, out var snapshot))
            {
                var text = await artifacts.ReadTextAsync(sha256, ct);
                snapshot = text is null ? null : GraphJson.Deserialize<NormalizedSnapshotDocument>(text).Snapshot;
                _snapshots[sha256] = snapshot;
            }

            return snapshot;
        }

        public async Task<FocusProbeDocument?> ProbeAsync(string sha256, CancellationToken ct)
        {
            if (!_probes.TryGetValue(sha256, out var probe))
            {
                var text = await artifacts.ReadTextAsync(sha256, ct);
                probe = text is null ? null : GraphJson.Deserialize<FocusProbeDocument>(text);
                _probes[sha256] = probe;
            }

            return probe;
        }
    }

    private static async Task<Dictionary<string, List<string>>> EvidenceBySnapshotAsync(IUnitOfWork uow, CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var context in await uow.Graph.ListContextsAsync(ct))
        {
            foreach (var evidence in await uow.Graph.ListEvidenceAsync(context.ContextId, null, ct))
            {
                if (evidence.SnapshotId is { } snapshotId)
                {
                    if (!result.TryGetValue(snapshotId, out var list))
                    {
                        result[snapshotId] = list = [];
                    }

                    list.Add(evidence.EvidenceId);
                }
            }
        }

        foreach (var list in result.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        return result;
    }

    private static async Task<VariantView> ViewOfAsync(ScreenVariant variant, Data data, Dictionary<(ScreenCaptureKind, string, string), ScreenCapture> captures, SnapshotLoader loader, CancellationToken ct)
    {
        if (captures.TryGetValue((ScreenCaptureKind.FullHierarchy, variant.StateId, variant.ContextId), out var full)
            && full.Status == ScreenCaptureStatus.Captured
            && data.Snapshots.FirstOrDefault(s => s.SnapshotId == full.SnapshotId) is { } fullSnapshot
            && await loader.NormalizedAsync(fullSnapshot.NormalizedSha256, ct) is { } normalizedFull)
        {
            return new VariantView(variant, normalizedFull, "full", [.. full.EvidenceIds.Order(StringComparer.Ordinal)]);
        }

        // The latest fast snapshot of the variant (ties by id), as AnalyzeState reads it.
        var fast = data.Snapshots
            .Where(s => s.StateId == variant.StateId && s.ContextId == variant.ContextId && s.Fidelity == SnapshotFidelity.Fast)
            .OrderByDescending(s => s.ObservedAt)
            .ThenBy(s => s.SnapshotId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (fast is not null && await loader.NormalizedAsync(fast.NormalizedSha256, ct) is { } normalizedFast)
        {
            return new VariantView(variant, normalizedFast, "fast", data.EvidenceBySnapshot.TryGetValue(fast.SnapshotId, out var ids) ? ids : []);
        }

        return new VariantView(variant, null, "none", []);
    }

    // ------------------------------------------------------------------ elements

    private static List<ContractElement> Elements(List<VariantView> views, Platform platform, string groupKey, IReadOnlyList<GuardHypothesis> guards)
    {
        var byKey = new SortedDictionary<string, (UiElement First, bool Target, List<ContractPresence> Presence)>(StringComparer.Ordinal);

        // Full snapshots first, so an element's role and label come from the richest tree.
        foreach (var view in views.Where(v => v.Snapshot is not null).OrderBy(v => v.Source == "full" ? 0 : 1).ThenBy(v => v.Variant.StateId, StringComparer.Ordinal).ThenBy(v => v.Variant.ContextId, StringComparer.Ordinal))
        {
            foreach (var element in view.Snapshot!.Elements.Where(e => e.KeyStrength != ElementKeyStrength.Structural || e.Interactive))
            {
                if (!byKey.TryGetValue(element.ElementKey, out var entry))
                {
                    entry = (element, false, []);
                }

                entry.Target |= KeyboardTargets.IsTarget(element);
                if (!entry.Presence.Any(p => p.ContextId == view.Variant.ContextId && p.StateId == view.Variant.StateId))
                {
                    entry.Presence.Add(new ContractPresence(view.Variant.ContextId, view.Variant.StateId, view.Source));
                }

                byKey[element.ElementKey] = entry;
            }
        }

        var groupGuards = guards.Where(g => g.ComparisonGroup == groupKey).ToList();
        return [.. byKey.Select(kv =>
        {
            var (element, target, presence) = kv.Value;
            var states = presence.Select(p => p.StateId).ToHashSet(StringComparer.Ordinal);
            var guard = groupGuards
                .Where(g => g.ElementKey == kv.Key && g.States.Any(states.Contains))
                .OrderBy(g => g.Level)
                .ThenBy(g => g.Capability, StringComparer.Ordinal)
                .FirstOrDefault();
            return new ContractElement(
                kv.Key,
                element.KeyStrength.ToString(),
                element.Role,
                platform == Platform.Ios ? "inferred" : "observed",
                element.Label,
                element.ResourceId,
                element.Interactive,
                target,
                [.. presence.OrderBy(p => p.ContextId, StringComparer.Ordinal).ThenBy(p => p.StateId, StringComparer.Ordinal)],
                guard is null ? null : new ContractVisibleWhen(guard.Capability, guard.Level.ToString().ToLowerInvariant(), guard.PresentIn, guard.AbsentIn, guard.EvidenceIds));
        })];
    }

    // ------------------------------------------------------------------ tree rules (inspected)

    private static IEnumerable<AccessibilityFinding> TreeRules(VariantView view, AccessibilityTechnology reader, Platform platform)
    {
        var snapshot = view.Snapshot!;
        var elements = snapshot.Elements;
        var technology = reader.ToString();
        var contextId = view.Variant.ContextId;
        var stateId = view.Variant.StateId;
        AccessibilityFinding Finding(string rule, string? key, string message) => new(rule, technology, "inspected", key, contextId, stateId, message, view.EvidenceIds);

        var targets = elements.Select((e, i) => (Element: e, Index: i)).Where(x => KeyboardTargets.IsTarget(x.Element)).ToList();
        var reported = new HashSet<(string, string)>();
        foreach (var (element, index) in targets)
        {
            if (element.Label is null && !HasLabelledDescendant(elements, index) && reported.Add(("label.missing", element.ElementKey)))
            {
                yield return Finding("label.missing", element.ElementKey, $"A {element.Role} that can be activated has no text, content description or hint, nor a labelled child: a screen reader has nothing to announce. Check with {technology}.");
            }

            if (platform == Platform.Android && element.ImportantForAccessibility == false && reported.Add(("a11y.hidden", element.ElementKey)))
            {
                yield return Finding("a11y.hidden", element.ElementKey, "The element can be activated but is not important for accessibility (important-for-accessibility=false): TalkBack may skip it.");
            }

            if (SizeOf(element.Bounds) is { } size && (size.Width < MinTargetSize || size.Height < MinTargetSize) && reported.Add(("target.small", element.ElementKey)))
            {
                yield return Finding("target.small", element.ElementKey, $"Touch target {size.Width}x{size.Height} px is under {MinTargetPx}x{MinTargetPx}; the screen density is unknown, so the size in dp is not known (inferred).");
            }
        }

        foreach (var duplicate in targets
            .Where(x => x.Element.Label is not null)
            .GroupBy(x => x.Element.Label!, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Element.ElementKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var key in duplicate.Select(x => x.Element.ElementKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                yield return Finding("label.duplicate", key, $"{duplicate.Select(x => x.Element.ElementKey).Distinct(StringComparer.Ordinal).Count()} different targets share the label '{duplicate.Key}': a screen reader user cannot tell them apart.");
            }
        }
    }

    private static bool HasLabelledDescendant(IReadOnlyList<UiElement> elements, int index)
    {
        var depth = elements[index].Depth;
        for (var i = index + 1; i < elements.Count && elements[i].Depth > depth; i++)
        {
            if (elements[i].Label is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static (int Width, int Height)? SizeOf(string? bounds)
    {
        var box = BoxOf(bounds);
        return box is null ? null : (box.Value.Right - box.Value.Left, box.Value.Bottom - box.Value.Top);
    }

    private static (int Left, int Top, int Right, int Bottom)? BoxOf(string? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var match = BoundsPattern().Match(bounds);
        if (!match.Success)
        {
            return null;
        }

        int N(int g) => int.Parse(match.Groups[g].Value, CultureInfo.InvariantCulture);
        return (N(1), N(2), N(3), N(4));
    }

    [GeneratedRegex(@"^\[(-?\d{1,6}),(-?\d{1,6})\]\[(-?\d{1,6}),(-?\d{1,6})\]$", RegexOptions.CultureInvariant)]
    private static partial Regex BoundsPattern();

    // ------------------------------------------------------------------ keyboard

    /// <summary>Keyboard targets in reading order: top, then left (bounds); elements without bounds after them, in tree order.</summary>
    public static IReadOnlyList<string> PredictedOrder(NormalizedSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return [.. snapshot.Elements
            .Select((e, i) => (Element: e, Index: i, Box: BoxOf(e.Bounds)))
            .Where(x => KeyboardTargets.IsTarget(x.Element))
            .OrderBy(x => x.Box is null ? 1 : 0)
            .ThenBy(x => x.Box?.Top ?? 0)
            .ThenBy(x => x.Box?.Left ?? 0)
            .ThenBy(x => x.Index)
            .Select(x => x.Element.ElementKey)
            .Distinct(StringComparer.Ordinal)];
    }

    private const string ProbeMethod = "pressKey Tab through TapCue, then the element reporting focused on a fast observation (MCP inspect_screen), until focus returns to the first focused element (architecture §14.2, PoC). Scope: every keyboard target of the state receives focus; activation is not checked.";

    private static async Task<ObservedFocusGraph> FocusGraphAsync(VariantView view, ScreenCapture capture, SnapshotLoader loader, CancellationToken ct)
    {
        var contextId = view.Variant.ContextId;
        var stateId = view.Variant.StateId;
        var probe = capture.ArtifactSha256 is { } sha ? await loader.ProbeAsync(sha, ct) : null;
        if (capture.Status is ScreenCaptureStatus.Failed or ScreenCaptureStatus.Diverged or ScreenCaptureStatus.Unreachable || probe is null)
        {
            return new ObservedFocusGraph(contextId, stateId, "automated", A11yStatus.Blocked, ProbeMethod, [], [], [], false, null, $"capture {capture.Status}{(capture.Detail is null ? string.Empty : ": " + capture.Detail)}", [.. capture.EvidenceIds]);
        }

        var order = probe.FocusOrder();
        var transitions = new List<FocusTransition>();
        string? previous = probe.InitialFocusedElementKeys.Count > 0 ? probe.InitialFocusedElementKeys[0] : null;
        var n = 0;
        foreach (var step in probe.Steps.Where(s => s.FocusedElementKeys.Count > 0))
        {
            var to = step.FocusedElementKeys[0];
            if (to != previous)
            {
                transitions.Add(new FocusTransition(++n, previous, to));
                previous = to;
            }
        }

        var unreached = probe.TargetElementKeys.Except(order, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        string status;
        string? detail = null;
        if (capture.Status == ScreenCaptureStatus.NoFocusObserved || order.Count == 0)
        {
            status = A11yStatus.Unverified;
            detail = "no element reported focused after Tab: keyboard focus is not observable this way here (PoC result)";
        }
        else if (!probe.CycleClosed)
        {
            status = A11yStatus.Unverified;
            detail = $"the focus cycle did not close ({probe.StopReason} after {probe.Steps.Count} presses)";
        }
        else
        {
            status = unreached.Count == 0 ? A11yStatus.Pass : A11yStatus.Fail;
        }

        var predicted = view.Snapshot is null ? null : PredictedOrder(view.Snapshot);
        bool? matches = predicted is null || order.Count == 0 ? null : predicted.Where(order.Contains).SequenceEqual(order.Where(predicted.Contains), StringComparer.Ordinal);
        return new ObservedFocusGraph(contextId, stateId, "automated", status, ProbeMethod, transitions, order, unreached, probe.CycleClosed, matches, detail, [.. capture.EvidenceIds]);
    }

    // ------------------------------------------------------------------ matrix

    private static List<AccessibilityMatrixRow> Matrix(
        Platform platform,
        List<VariantView> views,
        List<AccessibilityFinding> findings,
        List<ObservedFocusGraph> observed,
        List<ScreenCapture> captures,
        List<AccessibilityRecord> records)
    {
        var fullViews = views.Where(v => v.Source == "full").ToList();
        var fullEvidence = fullViews.SelectMany(v => v.EvidenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var android = platform == Platform.Android;

        InspectedCell ReaderInspected(AccessibilityTechnology technology)
        {
            var own = findings.Count(f => f.Technology == technology.ToString() && f.Level == "inspected");
            var roles = technology == AccessibilityTechnology.VoiceOver ? " Roles on iOS are inferred (G-03)." : string.Empty;
            return fullViews.Count == 0
                ? new InspectedCell(A11yStatus.NotTested, "No full hierarchy (AnalyzeScreen) of this screen: tree rules need important-for-accessibility and bounds, which the fast snapshot drops (G-02).", 0, [])
                : new InspectedCell(
                    own > 0 ? A11yStatus.Issues : A11yStatus.Present,
                    $"Tree rules on the full hierarchy of {fullViews.Count} of {views.Count} variant(s): label.missing, label.duplicate, a11y.hidden, target.small. Present in the tree is not what a screen reader announces.{roles}",
                    own,
                    fullEvidence);
        }

        AssistiveCell Assistive(AccessibilityTechnology technology, string notTestedMethod)
        {
            var own = records.Where(r => r.Technology == technology).ToList();
            var screenLevel = own.Where(r => r.ElementKey is null && r.Property == A11yProperties.Screen).ToList();
            var status = own.Any(r => r.Result == AccessibilityRecordResult.Fail) ? A11yStatus.Fail
                : screenLevel.Any(r => r.Result == AccessibilityRecordResult.Pass) ? A11yStatus.Pass
                : A11yStatus.NotTested;
            var method = own.Count == 0
                ? notTestedMethod
                : $"Manual protocol mapcue.a11y-protocol/1 by the operator ({own.Count} record(s)); pass needs a whole-screen record ('{A11yProperties.Screen}') and no failing record.";
            return new AssistiveCell(status, method, [.. own.Select(r => r.RecordId)], [.. own.Select(r => r.EvidenceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }

        static InspectedCell InspectedUnsupported(string why) => new(A11yStatus.Unsupported, why, 0, []);
        static AutomatedCell AutomatedUnsupported(string why) => new(A11yStatus.Unsupported, why, 0, []);
        static AssistiveCell AssistiveUnsupported(string why) => new(A11yStatus.Unsupported, why, [], []);

        var talkBack = android
            ? new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.TalkBack),
                ReaderInspected(AccessibilityTechnology.TalkBack),
                AutomatedUnsupported("TapCue 0.1.0 cannot drive TalkBack or read its focus (G-05); tree rules are reported as inspected."),
                Assistive(AccessibilityTechnology.TalkBack, "Only a manual check with TalkBack by the operator (mapcue a11y record, docs/a11y-manual-protocol.md) can verify it."))
            : new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.TalkBack),
                InspectedUnsupported("TalkBack is an Android technology; this contract is for iOS."),
                AutomatedUnsupported("TalkBack is an Android technology; this contract is for iOS."),
                AssistiveUnsupported("TalkBack is an Android technology; this contract is for iOS."));

        var voiceOver = android
            ? new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.VoiceOver),
                InspectedUnsupported("VoiceOver is an iOS technology; this contract is for Android."),
                AutomatedUnsupported("VoiceOver is an iOS technology; this contract is for Android."),
                AssistiveUnsupported("VoiceOver is an iOS technology; this contract is for Android."))
            : new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.VoiceOver),
                ReaderInspected(AccessibilityTechnology.VoiceOver),
                AutomatedUnsupported("TapCue 0.1.0 has no accessibility audit and cannot drive VoiceOver (G-05)."),
                Assistive(AccessibilityTechnology.VoiceOver, "Only a manual check with VoiceOver on a physical device by the operator (mapcue a11y record); not possible on the simulator (CAP-0114)."));

        AutomatedCell KeyboardAutomated()
        {
            var probes = captures.Count(c => c.Kind == ScreenCaptureKind.KeyboardFocus);
            if (observed.Count == 0)
            {
                return new AutomatedCell(A11yStatus.NotTested, "No keyboard focus probe (AuditAccessibility, contracts.keyboardFocus) of this screen.", 0, []);
            }

            var statuses = observed.Select(o => o.Status).ToList();
            var missing = views.Count - observed.Count;
            var status = statuses.Contains(A11yStatus.Fail) ? A11yStatus.Fail
                : statuses.All(s => s == A11yStatus.Pass) && missing == 0 ? A11yStatus.Pass
                : statuses.All(s => s == A11yStatus.Blocked) ? A11yStatus.Blocked
                : A11yStatus.Unverified;
            var counts = string.Join(", ", statuses.GroupBy(s => s, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}"));
            return new AutomatedCell(
                status,
                $"{ProbeMethod} Variants probed: {observed.Count} of {views.Count} ({counts}); captures: {probes}.",
                findings.Count(f => f.Technology == nameof(AccessibilityTechnology.Keyboard)),
                [.. observed.SelectMany(o => o.EvidenceIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
        }

        var keyboard = android
            ? new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.Keyboard),
                InspectedUnsupported("No tree-only keyboard check: the order predicted from the tree is in keyboard.predicted (predicted, unverified)."),
                KeyboardAutomated(),
                Assistive(AccessibilityTechnology.Keyboard, "Only a manual keyboard check by the operator following the protocol (mapcue a11y record) gives assistiveVerified."))
            : new AccessibilityMatrixRow(
                nameof(AccessibilityTechnology.Keyboard),
                InspectedUnsupported("No tree-only keyboard check on iOS: the predicted order is unverified."),
                AutomatedUnsupported("TapCue 0.1.0 has no Tab key on iOS (G-04): no keyboard focus graph."),
                AssistiveUnsupported("Keyboard (Full Keyboard Access) checks on iOS are not supported in STEP-08 (G-04)."));

        return [talkBack, voiceOver, keyboard];
    }
}

/// <summary>Properties of manual accessibility records (<c>mapcue a11y record --property</c>).</summary>
public static class A11yProperties
{
    /// <summary>The whole screen was checked with the protocol (the only property that can make an <c>assistiveVerified</c> cell pass).</summary>
    public const string Screen = "screen";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(StringComparer.Ordinal) { Screen, "label", "role", "focusOrder", "reachable", "state", "announcement" };
}

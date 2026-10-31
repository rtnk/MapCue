using MapCue.Application.Ports;
using MapCue.Domain.Graph;

namespace MapCue.Application.Profiles;

/// <summary>Level of a guard hypothesis (architecture §8.2, STEP-07).</summary>
public enum GuardLevel
{
    /// <summary>A heuristic only: the element's name matches a capability of every context that shows it; its absence was never observed.</summary>
    Inferred,

    /// <summary>Seen: the element is on a state in some contexts and missing on the same state in others.</summary>
    Observed,

    /// <summary>The name suggested a capability, but a context with the capability lacks the element or one without it shows it.</summary>
    Refuted,
}

/// <summary>Whether a state was seen in a context (the profile coverage matrix).</summary>
public enum CoverageLevel
{
    Observed,
    NotObserved,
}

/// <summary>A context of the workspace with what its runs found.</summary>
public sealed record ContextCoverage(
    string ContextId,
    string ProfileId,
    string ScenarioId,
    string AppBuildId,
    string Locale,
    IReadOnlyList<string> Capabilities,
    int States,
    int Edges,
    int ObservedEdges,
    int Recipes);

/// <summary>One cell of the coverage matrix: a state in a context, with its snapshots and outgoing edges in that context.</summary>
public sealed record CoverageCell(string ContextId, CoverageLevel Level, int Snapshots, int OutgoingEdges, int ObservedOutgoingEdges, bool HasRecipe);

/// <summary>A row of the coverage matrix (a state shared by contexts, ADR-0006).</summary>
public sealed record CoverageRow(string StateId, string ScreenId, StateKind StateKind, IReadOnlyList<CoverageCell> Cells);

/// <summary>
/// Contexts of one comparison group that see a state with the same interactive elements. Each class is sorted; classes are ordered by
/// their first context id.
/// </summary>
public sealed record EquivalenceClasses(string StateId, string ComparisonGroup, IReadOnlyList<IReadOnlyList<string>> Classes);

/// <summary>
/// A guard hypothesis: an interactive element whose presence depends on the context (e.g. the Investments tab only for profiles with the
/// <c>investments</c> capability). <see cref="Capability"/> is the capability that explains it (null: an observed difference that no
/// capability explains). <see cref="States"/> are the states the element was compared on; <see cref="EvidenceIds"/> are the snapshot
/// evidence of those states in <see cref="PresentIn"/> and <see cref="AbsentIn"/>, each kept in its own context.
/// </summary>
public sealed record GuardHypothesis(
    string ComparisonGroup,
    string ElementKey,
    string? Capability,
    GuardLevel Level,
    IReadOnlyList<string> PresentIn,
    IReadOnlyList<string> AbsentIn,
    IReadOnlyList<string> States,
    IReadOnlyList<string> EvidenceIds);

/// <summary>Result of <see cref="ProfileAnalysis.AnalyzeAsync"/>.</summary>
public sealed record ProfileReport(
    IReadOnlyList<ContextCoverage> Contexts,
    IReadOnlyList<CoverageRow> Coverage,
    IReadOnlyList<EquivalenceClasses> Equivalence,
    IReadOnlyList<GuardHypothesis> Guards);

/// <summary>
/// Bounded comparison of profiles (STEP-07), deterministic and from the store only (no device, no reasoner):
/// <list type="bullet">
/// <item><b>Comparison groups:</b> only contexts of the same app, platform, build and locale are compared, so a build or language change
/// never reads as a profile difference.</item>
/// <item><b>Bounded to shared states and interactive elements:</b> a state is compared only in the contexts that have a variant of it, on
/// the interactive identity tokens of that variant (<see cref="ScreenVariant.IdentityTokens"/>), never on texts or data.</item>
/// <item><b>Coverage matrix:</b> every state × every context (<c>observed</c> / <c>notObserved</c>), with snapshots, outgoing edges and
/// recipes of that context.</item>
/// <item><b>Equivalence classes:</b> per state, the contexts that see the same interactive elements.</item>
/// <item><b>Guard hypotheses:</b> <c>inferred</c> when an element's name matches a capability of every context that shows it but no
/// context was seen without it; <c>observed</c> once the same state was seen without the element in a context without that capability
/// (with no capability match: observed when present and absent contexts differ by a capability, or as an unexplained difference);
/// <c>refuted</c> when the observation contradicts the name match.</item>
/// </list>
/// </summary>
public static class ProfileAnalysis
{
    private const string InteractivePrefix = "i:";

    public static Task<ProfileReport> AnalyzeAsync(IStateStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        return store.InTransactionAsync(
            async uow =>
            {
                var contexts = (await uow.Graph.ListContextsAsync(ct)).OrderBy(c => c.ContextId, StringComparer.Ordinal).ToList();
                var states = (await uow.Graph.ListStatesAsync(ct)).OrderBy(s => s.StateId, StringComparer.Ordinal).ToList();
                var variants = await uow.Graph.ListVariantsAsync(ct);
                var edges = await uow.Graph.ListEdgesAsync(ct);
                var recipes = await uow.Graph.ListRecipesAsync(null, ct);
                var snapshots = await uow.Graph.ListSnapshotsAsync(ct);
                var evidence = new Dictionary<(string Context, string State), IReadOnlyList<string>>();
                foreach (var v in variants)
                {
                    evidence[(v.ContextId, v.StateId)] = [.. (await uow.Graph.ListEvidenceAsync(v.ContextId, v.StateId, ct)).Where(e => e.SnapshotId is not null).Select(e => e.EvidenceId)];
                }

                return Analyze(contexts, states, variants, edges, recipes, snapshots, evidence);
            },
            ct);
    }

    /// <summary>The comparison group of a context: app, platform, build and locale (profiles, scenarios and capabilities may differ).</summary>
    public static string ComparisonGroupOf(ExplorationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return $"{context.AppId}|{context.Platform}|{context.AppBuildId}|{context.Locale}";
    }

    /// <summary>
    /// True when one of the element's name segments (split at non-alphanumerics) equals the capability, ignoring case. For a resource id key
    /// only the part after the last <c>/</c> is the name (<c>id:com.app:id/tab.investments</c> → <c>tab</c>, <c>investments</c>), so the
    /// package never matches.
    /// </summary>
    public static bool NameMatches(string elementKey, string capability)
    {
        ArgumentNullException.ThrowIfNull(elementKey);
        ArgumentNullException.ThrowIfNull(capability);
        var name = elementKey.StartsWith("id:", StringComparison.Ordinal) && elementKey.LastIndexOf('/') is var slash and >= 0 ? elementKey[(slash + 1)..] : elementKey;
        var segments = new string([.. name.Select(c => char.IsLetterOrDigit(c) ? c : ' ')]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new string([.. capability.Where(char.IsLetterOrDigit)]);
        return normalized.Length > 0 && segments.Any(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static ProfileReport Analyze(
        List<ExplorationContext> contexts,
        List<ScreenState> states,
        IReadOnlyList<ScreenVariant> variants,
        IReadOnlyList<ScreenEdge> edges,
        IReadOnlyList<ReachabilityRecipe> recipes,
        IReadOnlyList<UiSnapshot> snapshots,
        Dictionary<(string Context, string State), IReadOnlyList<string>> evidence)
    {
        var variantOf = variants.ToDictionary(v => (v.ContextId, v.StateId));
        var recipeOf = recipes.Select(r => (r.ContextId, r.TargetStateId)).ToHashSet();
        var contextCoverage = contexts.Select(c => new ContextCoverage(
            c.ContextId,
            c.ProfileId,
            c.ScenarioId,
            c.AppBuildId,
            c.Locale,
            c.Capabilities,
            variants.Count(v => v.ContextId == c.ContextId),
            edges.Count(e => e.ContextId == c.ContextId),
            edges.Count(e => e.ContextId == c.ContextId && e.Status is EdgeStatus.Observed or EdgeStatus.Verified),
            recipes.Count(r => r.ContextId == c.ContextId))).ToList();

        var coverage = states.Select(s => new CoverageRow(
            s.StateId,
            s.ScreenId,
            s.StateKind,
            [
                .. contexts.Select(c => variantOf.TryGetValue((c.ContextId, s.StateId), out var v)
                    ? new CoverageCell(
                        c.ContextId,
                        CoverageLevel.Observed,
                        snapshots.Count(x => x.ContextId == c.ContextId && x.StateId == s.StateId),
                        edges.Count(e => e.ContextId == c.ContextId && e.FromStateId == s.StateId),
                        edges.Count(e => e.ContextId == c.ContextId && e.FromStateId == s.StateId && e.Status is EdgeStatus.Observed or EdgeStatus.Verified),
                        recipeOf.Contains((c.ContextId, s.StateId)))
                    : new CoverageCell(c.ContextId, CoverageLevel.NotObserved, 0, 0, 0, false)),
            ])).ToList();

        var equivalence = new List<EquivalenceClasses>();
        var guards = new List<GuardHypothesis>();
        foreach (var group in contexts.GroupBy(ComparisonGroupOf, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.ToList();
            var capabilities = members.ToDictionary(c => c.ContextId, c => c.Capabilities.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);

            // Interactive elements of each state in each context of the group (bounded: only states seen in that context).
            var seen = new Dictionary<string, Dictionary<string, HashSet<string>>>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                var perContext = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
                foreach (var c in members)
                {
                    // Variants recorded before migration 0006 have no tokens of their own: not compared (never guessed from the base).
                    if (variantOf.TryGetValue((c.ContextId, state.StateId), out var v) && v.IdentityTokens.Count > 0)
                    {
                        perContext[c.ContextId] = v.IdentityTokens.Where(t => t.StartsWith(InteractivePrefix, StringComparison.Ordinal)).Select(t => t[InteractivePrefix.Length..]).ToHashSet(StringComparer.Ordinal);
                    }
                }

                if (perContext.Count == 0)
                {
                    continue;
                }

                seen[state.StateId] = perContext;
                if (perContext.Count > 1)
                {
                    equivalence.Add(new EquivalenceClasses(
                        state.StateId,
                        group.Key,
                        [
                            .. perContext
                                .GroupBy(kv => string.Join('\n', kv.Value.Order(StringComparer.Ordinal)), kv => kv.Key, StringComparer.Ordinal)
                                .Select(g => (IReadOnlyList<string>)[.. g.Order(StringComparer.Ordinal)])
                                .OrderBy(c => c[0], StringComparer.Ordinal),
                        ]));
                }
            }

            var elements = seen.Values.SelectMany(p => p.Values).SelectMany(e => e).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var element in elements)
            {
                var present = new SortedSet<string>(StringComparer.Ordinal);
                var absent = new SortedSet<string>(StringComparer.Ordinal);
                var compared = new SortedSet<string>(StringComparer.Ordinal);
                foreach (var (stateId, perContext) in seen)
                {
                    var with = perContext.Where(kv => kv.Value.Contains(element)).Select(kv => kv.Key).ToList();
                    if (with.Count == 0)
                    {
                        continue;
                    }

                    present.UnionWith(with);
                    var without = perContext.Where(kv => !kv.Value.Contains(element)).Select(kv => kv.Key).ToList();
                    absent.UnionWith(without);
                    compared.Add(stateId);
                }

                var guard = Classify(element, present, absent, capabilities);
                if (guard is null)
                {
                    continue;
                }

                var (capability, level) = guard.Value;
                var evidenceIds = compared
                    .SelectMany(s => present.Concat(absent).Select(c => evidence.TryGetValue((c, s), out var ids) ? ids : []))
                    .SelectMany(ids => ids)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                guards.Add(new GuardHypothesis(group.Key, element, capability, level, [.. present], [.. absent], [.. compared], evidenceIds));
            }
        }

        return new ProfileReport(contextCoverage, coverage, equivalence, guards);
    }

    private static (string? Capability, GuardLevel Level)? Classify(
        string element,
        SortedSet<string> present,
        SortedSet<string> absent,
        Dictionary<string, HashSet<string>> capabilities)
    {
        // A capability whose name is a segment of the element key, held by at least one context that shows the element.
        var named = present
            .SelectMany(c => capabilities[c])
            .Where(cap => NameMatches(element, cap))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        bool Consistent(string cap) => present.All(c => capabilities[c].Contains(cap)) && absent.All(c => !capabilities[c].Contains(cap));

        if (named.Count > 0)
        {
            var explained = named.FirstOrDefault(Consistent);
            if (explained is not null)
            {
                return (explained, absent.Count == 0 ? GuardLevel.Inferred : GuardLevel.Observed);
            }

            return (named[0], GuardLevel.Refuted);
        }

        if (absent.Count == 0)
        {
            // Present in every context that saw its states and no name hint: nothing to hypothesize.
            return null;
        }

        var consistent = present.SelectMany(c => capabilities[c]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).FirstOrDefault(Consistent);
        return (consistent, GuardLevel.Observed);
    }
}

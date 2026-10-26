using System.Text.RegularExpressions;
using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>
/// Thresholds of the resolver (ADR-0006), checked on the STEP-02B demo recordings in STEP-05 (R-04). <see cref="ScreenHints"/> are
/// operator patterns for the resource id of a screen marker (architecture §7.3 step 3, e.g. a MAUI <c>AutomationId</c> of the page
/// title); hints never change an identity hash, only which known states a snapshot may be matched with.
/// </summary>
public sealed record IdentityResolverOptions(double StateThreshold = 0.85, double ScreenThreshold = 0.6, double AmbiguityEpsilon = 0.02)
{
    public static IdentityResolverOptions Default { get; } = new();

    public IReadOnlyList<Regex> ScreenHints { get; init; } = [];
}

/// <summary>What the resolver knows about a stored state.</summary>
public sealed record KnownState(string StateId, string ScreenId, string IdentityHash, int FingerprintVersion, IReadOnlyList<string> IdentityTokens);

public enum ResolutionKind
{
    /// <summary>The identity hash (or a recorded alias of it) equals a known state.</summary>
    KnownState,

    /// <summary>Similar enough to a known state, with the same state-kind discriminators: same state, different element set.</summary>
    KnownStateVariant,

    /// <summary>Similar to a known screen (or same screen with another state kind): new state of that screen.</summary>
    NewStateOfScreen,

    /// <summary>Nothing similar: new provisional logical screen.</summary>
    NewScreen,

    /// <summary>Two candidates are equally close; recorded as hypotheses, never merged silently.</summary>
    Ambiguous,
}

/// <summary>A candidate considered by the resolver, with its similarity.</summary>
public sealed record IdentityCandidate(string StateId, string ScreenId, double Similarity);

/// <summary>Result of <see cref="ScreenIdentityResolver.Resolve"/>.</summary>
public sealed record IdentityResolution(ResolutionKind Kind, string? StateId, string? ScreenId, double Similarity, IReadOnlyList<IdentityCandidate> Candidates);

/// <summary>
/// Deterministic state recognition (architecture §7.3): exact identity hash or alias, then multiset Jaccard similarity of identity
/// tokens against <c>τ_state</c> (no discriminator difference) and <c>τ_screen</c>, then a new screen. Two different candidates
/// within <c>ε</c> of each other give <see cref="ResolutionKind.Ambiguous"/>. Only states of the same fingerprint version are compared.
/// Transient snapshots (stabilization) are decided by the engine before resolution (STEP-05).
/// With <see cref="IdentityResolverOptions.ScreenHints"/>, the hint of a snapshot is the set of resource ids of its identity elements
/// that match a hint pattern: states with another hint are never matched by similarity (another screen), and a state with the same
/// hint makes a dissimilar snapshot a new state of that screen instead of a new screen.
/// </summary>
public sealed class ScreenIdentityResolver(IdentityResolverOptions? options = null)
{
    private readonly IdentityResolverOptions _options = options ?? IdentityResolverOptions.Default;

    public IdentityResolution Resolve(NormalizedSnapshot snapshot, IReadOnlyList<KnownState> knownStates, IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(knownStates);

        var comparable = knownStates.Where(s => s.FingerprintVersion == snapshot.FingerprintVersion).ToList();
        var exact = comparable.FirstOrDefault(s => s.IdentityHash == snapshot.IdentityHash);
        if (exact is null && aliases is not null && aliases.TryGetValue(snapshot.IdentityHash, out var aliasStateId))
        {
            exact = comparable.FirstOrDefault(s => s.StateId == aliasStateId);
        }

        if (exact is not null)
        {
            return new IdentityResolution(ResolutionKind.KnownState, exact.StateId, exact.ScreenId, 1.0, [new IdentityCandidate(exact.StateId, exact.ScreenId, 1.0)]);
        }

        var discriminators = Discriminators(snapshot.IdentityTokens);
        var hint = Hint(snapshot.IdentityTokens);
        if (_options.ScreenHints.Count > 0)
        {
            comparable = [.. comparable.Where(s => Hint(s.IdentityTokens).SequenceEqual(hint, StringComparer.Ordinal))];
        }

        var scored = comparable
            .Select(s => new IdentityCandidate(s.StateId, s.ScreenId, Jaccard(snapshot.IdentityTokens, s.IdentityTokens)))
            .OrderByDescending(c => c.Similarity)
            .ThenBy(c => c.StateId, StringComparer.Ordinal)
            .ToList();
        var candidates = scored.Where(c => c.Similarity >= _options.ScreenThreshold).ToList();
        if (candidates.Count == 0)
        {
            // Same screen marker: another state of that screen, however different the rest is.
            return hint.Count > 0 && scored.Count > 0
                ? new IdentityResolution(ResolutionKind.NewStateOfScreen, null, scored[0].ScreenId, scored[0].Similarity, [scored[0]])
                : new IdentityResolution(ResolutionKind.NewScreen, null, null, 0, []);
        }

        var best = candidates[0];
        if (candidates.Count > 1)
        {
            var second = candidates[1];
            var close = best.Similarity - second.Similarity < _options.AmbiguityEpsilon;
            var conflicting = second.ScreenId != best.ScreenId || second.Similarity >= _options.StateThreshold;
            if (close && conflicting)
            {
                return new IdentityResolution(ResolutionKind.Ambiguous, null, null, best.Similarity, candidates.Take(2).ToList());
            }
        }

        var bestState = comparable.First(s => s.StateId == best.StateId);
        var sameKind = Discriminators(bestState.IdentityTokens).SequenceEqual(discriminators, StringComparer.Ordinal);
        return best.Similarity >= _options.StateThreshold && sameKind
            ? new IdentityResolution(ResolutionKind.KnownStateVariant, best.StateId, best.ScreenId, best.Similarity, candidates)
            : new IdentityResolution(ResolutionKind.NewStateOfScreen, null, best.ScreenId, best.Similarity, candidates);
    }

    /// <summary>Multiset Jaccard similarity: Σ min(count) / Σ max(count). Two empty sets are identical (1.0).</summary>
    public static double Jaccard(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 && b.Count == 0)
        {
            return 1.0;
        }

        var ca = a.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var cb = b.GroupBy(x => x, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var intersection = 0;
        var union = 0;
        foreach (var key in ca.Keys.Union(cb.Keys, StringComparer.Ordinal))
        {
            var x = ca.GetValueOrDefault(key);
            var y = cb.GetValueOrDefault(key);
            intersection += Math.Min(x, y);
            union += Math.Max(x, y);
        }

        return (double)intersection / union;
    }

    /// <summary>Resource ids of identity elements (anchors and interactive) that match a screen hint pattern, sorted.</summary>
    private List<string> Hint(IReadOnlyList<string> tokens)
    {
        if (_options.ScreenHints.Count == 0)
        {
            return [];
        }

        return
        [
            .. tokens
                .Where(t => t.StartsWith("a:id:", StringComparison.Ordinal) || t.StartsWith("i:id:", StringComparison.Ordinal))
                .Select(t => t[5..])
                .Where(id => _options.ScreenHints.Any(p => Matches(p, id)))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];
    }

    // A pattern that times out does not mark a hint (the snapshot is then compared like one without a marker).
    private static bool Matches(Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static List<string> Discriminators(IReadOnlyList<string> tokens) =>
        [.. tokens.Where(t => t.StartsWith("k:", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
}

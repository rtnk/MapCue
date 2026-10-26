using MapCue.Domain.Graph;

namespace MapCue.Application.Graph;

/// <summary>Thresholds of the resolver (ADR-0006). Defaults are uncalibrated until STEP-05/02B data exists (R-04).</summary>
public sealed record IdentityResolverOptions(double StateThreshold = 0.85, double ScreenThreshold = 0.6, double AmbiguityEpsilon = 0.02)
{
    public static IdentityResolverOptions Default { get; } = new();
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
        var candidates = comparable
            .Select(s => new IdentityCandidate(s.StateId, s.ScreenId, Jaccard(snapshot.IdentityTokens, s.IdentityTokens)))
            .Where(c => c.Similarity >= _options.ScreenThreshold)
            .OrderByDescending(c => c.Similarity)
            .ThenBy(c => c.StateId, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0)
        {
            return new IdentityResolution(ResolutionKind.NewScreen, null, null, 0, []);
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

    private static List<string> Discriminators(IReadOnlyList<string> tokens) =>
        [.. tokens.Where(t => t.StartsWith("k:", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
}

using MapCue.Application.Graph;
using MapCue.Domain.Graph;

namespace MapCue.Application.Tests.Graph;

public class ScreenIdentityResolverTests
{
    private static readonly ScreenIdentityResolver _resolver = new();

    private static NormalizedSnapshot Snapshot(params string[] tokens) => new(
        SnapshotNormalizer.FingerprintVersion,
        MapCue.Domain.Platform.Android,
        [],
        [],
        [.. tokens.Order(StringComparer.Ordinal)],
        ContentHash.Of(["test", .. tokens.Order(StringComparer.Ordinal)]),
        "sha256:layout",
        "sha256:elements",
        [],
        StateKind.Default,
        [],
        [],
        0,
        0);

    private static KnownState Known(string stateId, string screenId, NormalizedSnapshot snapshot) =>
        new(stateId, screenId, snapshot.IdentityHash, snapshot.FingerprintVersion, snapshot.IdentityTokens);

    private static string[] Tokens(int count, string prefix = "i:id:app:id/b") => [.. Enumerable.Range(0, count).Select(i => prefix + i)];

    [Fact]
    public void Exact_hash_is_a_known_state()
    {
        var home = Snapshot(Tokens(5));
        var result = _resolver.Resolve(home, [Known("st_home", "scr_home", home)]);
        Assert.Equal((ResolutionKind.KnownState, "st_home", 1.0), (result.Kind, result.StateId, result.Similarity));
    }

    [Fact]
    public void Alias_resolves_like_an_exact_hash()
    {
        var home = Snapshot(Tokens(5));
        var other = Snapshot(Tokens(3, "i:x"));
        var result = _resolver.Resolve(other, [Known("st_home", "scr_home", home)], new Dictionary<string, string> { [other.IdentityHash] = "st_home" });
        Assert.Equal(ResolutionKind.KnownState, result.Kind);
    }

    [Fact]
    public void Similar_snapshot_is_a_variant_of_the_known_state()
    {
        var home = Snapshot(Tokens(10));
        var withExtra = Snapshot([.. Tokens(10), "i:id:app:id/investments"]);
        var result = _resolver.Resolve(withExtra, [Known("st_home", "scr_home", home)]);
        Assert.Equal(ResolutionKind.KnownStateVariant, result.Kind);
        Assert.Equal("st_home", result.StateId);
        Assert.Equal(10.0 / 11, result.Similarity, 6);
    }

    [Fact]
    public void Discriminator_difference_is_a_new_state_of_the_same_screen()
    {
        var home = Snapshot(Tokens(10));
        var modal = Snapshot([.. Tokens(10), "k:modal"]);
        var result = _resolver.Resolve(modal, [Known("st_home", "scr_home", home)]);
        Assert.Equal((ResolutionKind.NewStateOfScreen, "scr_home"), (result.Kind, result.ScreenId));
        Assert.Null(result.StateId);
    }

    [Fact]
    public void Moderately_similar_snapshot_is_a_new_state_of_the_screen()
    {
        var home = Snapshot(Tokens(10));
        var tab = Snapshot([.. Tokens(8), .. Tokens(2, "i:tab")]);
        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(tab, [Known("st_home", "scr_home", home)]).Kind);
    }

    [Fact]
    public void Dissimilar_snapshot_is_a_new_screen()
    {
        var home = Snapshot(Tokens(10));
        var settings = Snapshot(Tokens(10, "i:settings"));
        Assert.Equal(ResolutionKind.NewScreen, _resolver.Resolve(settings, [Known("st_home", "scr_home", home)]).Kind);
        Assert.Equal(ResolutionKind.NewScreen, _resolver.Resolve(settings, []).Kind);
    }

    [Fact]
    public void Two_equally_close_screens_are_ambiguous_and_never_merged()
    {
        var shared = Tokens(8);
        var a = Snapshot([.. shared, "i:a1", "i:a2"]);
        var b = Snapshot([.. shared, "i:b1", "i:b2"]);
        var probe = Snapshot([.. shared, "i:c1"]);
        var result = _resolver.Resolve(probe, [Known("st_a", "scr_a", a), Known("st_b", "scr_b", b)]);
        Assert.Equal(ResolutionKind.Ambiguous, result.Kind);
        Assert.Null(result.StateId);
        Assert.Equal(["st_a", "st_b"], result.Candidates.Select(c => c.StateId));
    }

    [Fact]
    public void Other_fingerprint_versions_are_ignored()
    {
        var home = Snapshot(Tokens(5));
        var result = _resolver.Resolve(home, [Known("st_old", "scr_old", home) with { FingerprintVersion = 0 }]);
        Assert.Equal(ResolutionKind.NewScreen, result.Kind);
    }

    [Fact]
    public void Jaccard_is_a_multiset_similarity()
    {
        Assert.Equal(1.0, ScreenIdentityResolver.Jaccard([], []));
        Assert.Equal(0.5, ScreenIdentityResolver.Jaccard(["a", "a"], ["a"]));
        Assert.Equal(1.0 / 3, ScreenIdentityResolver.Jaccard(["a", "b"], ["b", "c"]), 6);
    }

    private static readonly ScreenIdentityResolver _hinted = new(IdentityResolverOptions.Default with
    {
        ScreenHints = [new System.Text.RegularExpressions.Regex(@":id/screen\.[a-z]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))],
    });

    [Fact]
    public void Screen_hints_keep_similar_screens_with_different_markers_apart()
    {
        // 6 shared tab tokens + one marker each: Jaccard 6/8 = 0.75 ≥ τ_screen, so without hints it would be the same screen.
        var home = Snapshot([.. Tokens(6), "a:id:app:id/screen.home"]);
        var accounts = Snapshot([.. Tokens(6), "a:id:app:id/screen.accounts"]);
        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(accounts, [Known("st_home", "scr_home", home)]).Kind);

        var result = _hinted.Resolve(accounts, [Known("st_home", "scr_home", home)]);

        Assert.Equal(ResolutionKind.NewScreen, result.Kind);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Same_screen_hint_makes_a_dissimilar_snapshot_a_state_of_that_screen()
    {
        var home = Snapshot([.. Tokens(6), "a:id:app:id/screen.home"]);
        var homeEmpty = Snapshot([.. Tokens(1, "i:id:app:id/other"), "a:id:app:id/screen.home", "k:empty"]);
        Assert.Equal(ResolutionKind.NewScreen, _resolver.Resolve(homeEmpty, [Known("st_home", "scr_home", home)]).Kind);

        var result = _hinted.Resolve(homeEmpty, [Known("st_home", "scr_home", home)]);

        Assert.Equal((ResolutionKind.NewStateOfScreen, "scr_home"), (result.Kind, result.ScreenId));
    }

    [Fact]
    public void Same_screen_hint_and_similar_tokens_is_a_variant()
    {
        var basic = Snapshot([.. Tokens(6), "a:id:app:id/screen.home"]);
        var investor = Snapshot([.. Tokens(7), "a:id:app:id/screen.home"]);

        var result = _hinted.Resolve(investor, [Known("st_home", "scr_home", basic)]);

        Assert.Equal((ResolutionKind.KnownStateVariant, "st_home"), (result.Kind, result.StateId));
    }

    [Fact]
    public void Hints_do_not_change_exact_matches_or_snapshots_without_markers()
    {
        var plain = Snapshot(Tokens(9));
        Assert.Equal(ResolutionKind.KnownState, _hinted.Resolve(plain, [Known("st_a", "scr_a", plain)]).Kind);
        var similar = Snapshot(Tokens(10));
        Assert.Equal(ResolutionKind.KnownStateVariant, _hinted.Resolve(similar, [Known("st_a", "scr_a", plain)]).Kind);
    }
}

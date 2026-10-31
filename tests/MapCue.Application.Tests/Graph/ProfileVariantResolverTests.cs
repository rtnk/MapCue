using System.Text.RegularExpressions;
using MapCue.Application.Graph;
using MapCue.Domain.Graph;

namespace MapCue.Application.Tests.Graph;

/// <summary>The profile variant rule of the resolver (STEP-07, R-04): a few elements more or less in another context is a variant.</summary>
public class ProfileVariantResolverTests
{
    private const string Basic = "ctx_basic";
    private const string Investor = "ctx_investor";

    private static readonly ScreenIdentityResolver _resolver = new(IdentityResolverOptions.Default with
    {
        ScreenHints = [new Regex(@":id/screen\.[a-z]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))],
    });

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

    private static KnownState Known(string stateId, NormalizedSnapshot snapshot, params string[] contexts) =>
        new(stateId, "scr_" + stateId, snapshot.IdentityHash, snapshot.FingerprintVersion, snapshot.IdentityTokens) { ContextIds = contexts };

    // The Accounts tab: title, three tabs and the accounts list (5 tokens; the investor tab makes 6, Jaccard 5/6 < τ_state).
    private static readonly string[] _accounts = ["a:id:app:id/screen.accounts", "i:id:app:id/tab.home", "i:id:app:id/tab.accounts", "i:id:app:id/tab.settings", "l:list|t|many"];

    [Fact]
    public void Another_context_with_one_element_more_is_a_variant_below_the_similarity_threshold()
    {
        var basic = Snapshot(_accounts);
        var investor = Snapshot([.. _accounts, "i:id:app:id/tab.investments"]);
        Assert.True(ScreenIdentityResolver.Jaccard(basic.IdentityTokens, investor.IdentityTokens) < IdentityResolverOptions.Default.StateThreshold);

        var result = _resolver.Resolve(investor, [Known("accounts", basic, Basic)], null, Investor);

        Assert.Equal((ResolutionKind.KnownStateVariant, "accounts", 1), (result.Kind, result.StateId, result.ProfileVariantDelta));
        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(investor, [Known("accounts", basic, Basic)]).Kind);
    }

    [Fact]
    public void In_the_same_context_another_element_set_stays_another_state()
    {
        var basic = Snapshot(_accounts);
        var more = Snapshot([.. _accounts, "i:id:app:id/accounts.add"]);

        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(more, [Known("accounts", basic, Basic)], null, Basic).Kind);
        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(more, [Known("accounts", basic, Basic, Investor)], null, Investor).Kind);
    }

    [Fact]
    public void The_rule_is_bounded_by_the_delta_the_screen_marker_and_the_discriminators()
    {
        var basic = Snapshot(_accounts);
        var three = Snapshot([.. _accounts, "i:id:app:id/x1", "i:id:app:id/x2", "i:id:app:id/x3"]);
        var otherScreen = Snapshot(["a:id:app:id/screen.settings", .. _accounts.Skip(1)]);
        var modal = Snapshot([.. _accounts, "k:modal"]);
        KnownState[] known = [Known("accounts", basic, Basic)];

        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(three, known, null, Investor).Kind);
        Assert.NotEqual(ResolutionKind.KnownStateVariant, _resolver.Resolve(otherScreen, known, null, Investor).Kind);
        Assert.Equal(ResolutionKind.NewStateOfScreen, _resolver.Resolve(modal, known, null, Investor).Kind);

        var disabled = new ScreenIdentityResolver(IdentityResolverOptions.Default with { ScreenHints = [new Regex(@":id/screen\.[a-z]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))], ProfileVariantMaxDelta = 0 });
        Assert.Equal(ResolutionKind.NewStateOfScreen, disabled.Resolve(Snapshot([.. _accounts, "i:id:app:id/tab.investments"]), known, null, Investor).Kind);
    }

    [Fact]
    public void Without_screen_hints_the_rule_never_applies()
    {
        var basic = Snapshot(_accounts);
        var investor = Snapshot([.. _accounts, "i:id:app:id/tab.investments"]);

        var result = new ScreenIdentityResolver().Resolve(investor, [Known("accounts", basic, Basic)], null, Investor);

        Assert.NotEqual(ResolutionKind.KnownStateVariant, result.Kind);
    }

    [Fact]
    public void Two_equally_close_states_of_the_screen_are_ambiguous()
    {
        var a = Snapshot([.. _accounts, "i:id:app:id/a"]);
        var b = Snapshot([.. _accounts, "i:id:app:id/b"]);
        var investor = Snapshot([.. _accounts, "i:id:app:id/tab.investments"]);

        var result = _resolver.Resolve(investor, [Known("a", a, Basic), Known("b", b, Basic)], null, Investor);

        Assert.Equal(ResolutionKind.Ambiguous, result.Kind);
        Assert.Equal(["a", "b"], result.Candidates.Select(c => c.StateId));
    }

    [Fact]
    public void Delta_counts_multiset_differences()
    {
        Assert.Equal(0, ScreenIdentityResolver.Delta(["a", "b"], ["b", "a"]));
        Assert.Equal(1, ScreenIdentityResolver.Delta(["a", "b"], ["a", "b", "c"]));
        Assert.Equal(2, ScreenIdentityResolver.Delta(["a", "b"], ["a", "c"]));
        Assert.Equal(1, ScreenIdentityResolver.Delta(["a", "a"], ["a"]));
    }
}

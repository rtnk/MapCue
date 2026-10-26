using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using MapCue.Domain.Graph;

namespace MapCue.Domain.Tests;

public class GraphDomainTests
{
    [Fact]
    public void Content_hash_is_prefixed_lowercase_sha256()
    {
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ContentHash.Sha256(string.Empty));
        Assert.True(ContentHash.IsValid(ContentHash.Sha256("x")));
        Assert.False(ContentHash.IsValid("sha256:../../etc/passwd"));
        Assert.False(ContentHash.IsValid("sha256:" + new string('A', 64)));
        Assert.False(ContentHash.IsValid(null));
    }

    [Fact]
    public void Tuple_hash_is_unambiguous()
    {
        Assert.NotEqual(ContentHash.Of("ab", "c"), ContentHash.Of("a", "bc"));
        Assert.NotEqual(ContentHash.Of("a", null), ContentHash.Of("a", string.Empty));
        Assert.Equal(ContentHash.Of("a", "b"), ContentHash.Of("a", "b"));
    }

    [Fact]
    public void Context_signature_ignores_capability_order_and_duplicates()
    {
        var a = ExplorationContext.Create("basic", Platform.Android, "com.demo", "1.0", "pl-PL", "default", ["investments", "cards"]);
        var b = ExplorationContext.Create("basic", Platform.Android, "com.demo", "1.0", "pl-PL", "default", ["cards", "investments", "cards", " "]);
        Assert.Equal(a.Signature, b.Signature);
        Assert.Equal(a.ContextId, b.ContextId);
        Assert.Equal(a, b);
        Assert.Equal(["cards", "investments"], b.Capabilities);
        Assert.StartsWith("ctx_", a.ContextId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("investor", "android", "com.demo", "1.0", "pl-PL", "default")]
    [InlineData("basic", "ios", "com.demo", "1.0", "pl-PL", "default")]
    [InlineData("basic", "android", "com.other", "1.0", "pl-PL", "default")]
    [InlineData("basic", "android", "com.demo", "1.1", "pl-PL", "default")]
    [InlineData("basic", "android", "com.demo", "1.0", "en-US", "default")]
    [InlineData("basic", "android", "com.demo", "1.0", "pl-PL", "empty-accounts")]
    public void Every_context_field_changes_the_signature(string profile, string platform, string app, string build, string locale, string scenario)
    {
        var baseline = ExplorationContext.Create("basic", Platform.Android, "com.demo", "1.0", "pl-PL", "default");
        var other = ExplorationContext.Create(profile, Enum.Parse<Platform>(platform, ignoreCase: true), app, build, locale, scenario);
        Assert.NotEqual(baseline.Signature, other.Signature);
    }

    [Fact]
    public void Task_keys_differ_by_context_but_not_by_call()
    {
        var basic = ExplorationContext.Create("basic", Platform.Android, "com.demo", "1.0");
        var investor = ExplorationContext.Create("investor", Platform.Android, "com.demo", "1.0");
        var action = ActionCandidate.KeyOf(ActionType.Tap, "id:app:id/accounts", null);
        Assert.Equal(TaskKeys.ExploreAction("st_1", action, basic), TaskKeys.ExploreAction("st_1", action, basic));
        Assert.NotEqual(TaskKeys.ExploreAction("st_1", action, basic), TaskKeys.ExploreAction("st_1", action, investor));
        Assert.NotEqual(TaskKeys.AnalyzeState("st_1", basic, "p1"), TaskKeys.AnalyzeState("st_1", basic, "p2"));
        Assert.NotEqual(TaskKeys.AnalyzeState("st_1", basic, "p1"), TaskKeys.ExploreAction("st_1", action, basic));
    }

    [Fact]
    public void Action_key_uses_the_param_class_never_a_value()
    {
        Assert.Equal(
            ActionCandidate.KeyOf(ActionType.InputText, "id:app:id/user", "input:profile.username"),
            ActionCandidate.KeyOf(ActionType.InputText, "id:app:id/user", "input:profile.username"));
        Assert.NotEqual(ActionCandidate.KeyOf(ActionType.Tap, "id:a", null), ActionCandidate.KeyOf(ActionType.LongPress, "id:a", null));
    }

    [Theory]
    [InlineData(EdgeStatus.Observed, EdgeStatus.Unknown, EdgeStatus.Observed)]
    [InlineData(EdgeStatus.Verified, EdgeStatus.Executed, EdgeStatus.Verified)]
    [InlineData(EdgeStatus.Discovered, EdgeStatus.Blocked, EdgeStatus.Blocked)]
    [InlineData(EdgeStatus.Blocked, EdgeStatus.Observed, EdgeStatus.Observed)]
    [InlineData(EdgeStatus.Unknown, EdgeStatus.Discovered, EdgeStatus.Unknown)]
    [InlineData(EdgeStatus.Executed, EdgeStatus.Observed, EdgeStatus.Observed)]
    public void Edge_status_raise_examples(EdgeStatus current, EdgeStatus observed, EdgeStatus expected) =>
        Assert.Equal(expected, EdgeStatuses.Raise(current, observed));

    [Property(MaxTest = 500)]
    public Property Edge_confidence_never_decreases() =>
        Prop.ForAll(
            Arb.From(Gen.Elements(Enum.GetValues<EdgeStatus>())),
            Arb.From(Gen.ListOf(Gen.Elements(Enum.GetValues<EdgeStatus>()))),
            (start, updates) =>
            {
                static int Confidence(EdgeStatus s) => s is EdgeStatus.Executed or EdgeStatus.Observed or EdgeStatus.Verified ? (int)s : -1;
                var current = start;
                foreach (var update in updates)
                {
                    var next = EdgeStatuses.Raise(current, update);
                    if (Confidence(next) < Confidence(current))
                    {
                        return false;
                    }

                    current = next;
                }

                return true;
            });
}

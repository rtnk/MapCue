using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

/// <summary>Migration 0006 (STEP-07): identity tokens of a variant per context and recipes listed per context.</summary>
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mapcue-profiles-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new();
    private readonly ExplorationContext _basic = ExplorationContext.Create("basic", Platform.Android, "com.example.demo", "1");
    private readonly ExplorationContext _investor = ExplorationContext.Create("investor", Platform.Android, "com.example.demo", "1", capabilities: ["investments"]);

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static Task<T> InTx<T>(SqliteStateStore store, Func<IUnitOfWork, Task<T>> work) => store.InTransactionAsync(work, CancellationToken.None);

    private async Task SeedStateAsync(SqliteStateStore store) =>
        await InTx(store, async uow =>
        {
            await uow.Graph.UpsertContextAsync(_basic, _clock.UtcNow, CancellationToken.None);
            await uow.Graph.UpsertContextAsync(_investor, _clock.UtcNow, CancellationToken.None);
            await uow.Graph.InsertScreenAsync(new LogicalScreen("scr_1", "screen-1", null, IdentityStatus.Provisional, IdentitySource.Structure, _clock.UtcNow), CancellationToken.None);
            await uow.Graph.InsertStateAsync(new ScreenState("st_1", "scr_1", StateKind.Default, "sha256:h", 2, ["a:x", "i:y"], ["k1"], IdentityStatus.Provisional, _clock.UtcNow), CancellationToken.None);
            return 0;
        });

    [Fact]
    public async Task Variant_identity_tokens_are_stored_per_context()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedStateAsync(store);
        var basic = new ScreenVariant("st_1", _basic.ContextId, "sha256:e1", [], [], 1, _clock.UtcNow, _clock.UtcNow) { IdentityTokens = ["a:x", "i:y"] };
        var investor = new ScreenVariant("st_1", _investor.ContextId, "sha256:e2", ["k2"], [], 1, _clock.UtcNow, _clock.UtcNow) { IdentityTokens = ["a:x", "i:y", "i:z"] };

        await InTx(store, async uow =>
        {
            await uow.Graph.UpsertVariantAsync(basic, CancellationToken.None);
            await uow.Graph.UpsertVariantAsync(investor, CancellationToken.None);
            await uow.Graph.UpsertVariantAsync(investor with { SnapshotCount = 2, IdentityTokens = ["a:x", "i:z"] }, CancellationToken.None);
            return 0;
        });

        var variants = await InTx(store, uow => uow.Graph.ListVariantsAsync(CancellationToken.None));
        Assert.Equal(["a:x", "i:y"], variants.Single(v => v.ContextId == _basic.ContextId).IdentityTokens);
        Assert.Equal(["a:x", "i:z"], variants.Single(v => v.ContextId == _investor.ContextId).IdentityTokens);
    }

    [Fact]
    public async Task Candidate_assessments_are_per_context_and_their_status_only_moves_forward()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedStateAsync(store);
        CandidateAssessment Assess(ExplorationContext c, RiskClass risk, CandidateStatus status) => new("st_1", "ak", c.ContextId, risk, status, _clock.UtcNow);

        await InTx(store, async uow =>
        {
            await uow.Graph.InsertCandidateIfAbsentAsync(new ActionCandidate("st_1", "ak", ActionType.Tap, "id:x", null, CandidateSource.Agent, RiskClass.Navigational, CandidateStatus.Proposed, _clock.UtcNow), CancellationToken.None);
            await uow.Graph.UpsertCandidateAssessmentAsync(Assess(_basic, RiskClass.AccountChange, CandidateStatus.SkippedByPolicy), CancellationToken.None);
            await uow.Graph.UpsertCandidateAssessmentAsync(Assess(_investor, RiskClass.Navigational, CandidateStatus.Proposed), CancellationToken.None);
            await uow.Graph.UpsertCandidateAssessmentAsync(Assess(_investor, RiskClass.Navigational, CandidateStatus.Explored), CancellationToken.None);
            await uow.Graph.UpsertCandidateAssessmentAsync(Assess(_investor, RiskClass.Navigational, CandidateStatus.Scheduled), CancellationToken.None);
            return 0;
        });

        var all = await InTx(store, uow => uow.Graph.ListCandidateAssessmentsAsync(null, CancellationToken.None));
        Assert.Equal(2, all.Count);
        Assert.Equal((RiskClass.AccountChange, CandidateStatus.SkippedByPolicy), (all.Single(a => a.ContextId == _basic.ContextId).RiskClass, all.Single(a => a.ContextId == _basic.ContextId).Status));
        var investor = Assert.Single(await InTx(store, uow => uow.Graph.ListCandidateAssessmentsAsync(_investor.ContextId, CancellationToken.None)));
        Assert.Equal(CandidateStatus.Explored, investor.Status);
        await Assert.ThrowsAnyAsync<Exception>(() => InTx(store, async uow =>
        {
            await uow.Graph.UpsertCandidateAssessmentAsync(new CandidateAssessment("st_1", "missing", _basic.ContextId, RiskClass.Safe, CandidateStatus.Proposed, _clock.UtcNow), CancellationToken.None);
            return 0;
        }));
    }

    [Fact]
    public async Task Recipes_are_listed_per_context()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedStateAsync(store);
        ReachabilityRecipe Recipe(ExplorationContext c) =>
            new(ReachabilityRecipe.IdOf(c.ContextId, "st_1"), c.ContextId, "st_1", [new(RecipeStepKind.SetupFlow, null, null, "st_1", null)], null, []);

        await InTx(store, async uow =>
        {
            await uow.Graph.UpsertRecipeAsync(Recipe(_basic), CancellationToken.None);
            await uow.Graph.UpsertRecipeAsync(Recipe(_investor), CancellationToken.None);
            await uow.Graph.UpsertRecipeAsync(Recipe(_investor), CancellationToken.None);
            return 0;
        });

        Assert.NotEqual(Recipe(_basic).RecipeId, Recipe(_investor).RecipeId);
        Assert.Equal(2, (await InTx(store, uow => uow.Graph.ListRecipesAsync(null, CancellationToken.None))).Count);
        var investor = Assert.Single(await InTx(store, uow => uow.Graph.ListRecipesAsync(_investor.ContextId, CancellationToken.None)));
        Assert.Equal(RecipeStepKind.SetupFlow, investor.Steps.Single().Kind);
    }

    [Fact]
    public async Task Migration_6_keeps_existing_variants_without_tokens()
    {
        using (var old = SqliteStateStore.Open(_path, _clock, targetVersion: 5))
        {
            await SeedStateAsync(old);
            await old.QueryAsync($"INSERT INTO screen_variants (state_id, context_id, element_set_hash, added_keys_json, removed_keys_json, snapshot_count, first_seen_at, last_seen_at) VALUES ('st_1', '{_basic.ContextId}', 'sha256:e', '[]', '[]', 1, '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00')", CancellationToken.None);
        }

        using var upgraded = SqliteStateStore.Open(_path, _clock);
        Assert.True(SqliteMigrator.LatestVersion >= 6);
        var variant = Assert.Single(await InTx(upgraded, uow => uow.Graph.ListVariantsAsync(CancellationToken.None)));
        Assert.Empty(variant.IdentityTokens);
    }
}

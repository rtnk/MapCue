using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using MapCue.Infrastructure.Sqlite;
using MapCue.Testing;

namespace MapCue.Infrastructure.Tests.Graph;

/// <summary>Migration 0007 (STEP-08): screen captures, contract versions and manual accessibility records.</summary>
public sealed class ScreenContractStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"mapcue-contracts-{Guid.NewGuid():N}.db");
    private readonly ManualClock _clock = new();
    private readonly ExplorationContext _basic = ExplorationContext.Create("basic", Platform.Android, "com.example.demo", "1");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    private static Task<T> InTx<T>(SqliteStateStore store, Func<IUnitOfWork, Task<T>> work) => store.InTransactionAsync(work, CancellationToken.None);

    private async Task SeedAsync(SqliteStateStore store) =>
        await InTx(store, async uow =>
        {
            await uow.Graph.UpsertContextAsync(_basic, _clock.UtcNow, CancellationToken.None);
            await uow.Graph.InsertScreenAsync(new LogicalScreen("scr_1", "screen-1", null, IdentityStatus.Provisional, IdentitySource.Structure, _clock.UtcNow), CancellationToken.None);
            await uow.Graph.InsertStateAsync(new ScreenState("st_1", "scr_1", StateKind.Default, "sha256:h", 3, ["a:x"], ["k1"], IdentityStatus.Provisional, _clock.UtcNow), CancellationToken.None);
            await uow.Graph.InsertEvidenceIfAbsentAsync(new Evidence("ev_1", EvidenceKind.OperatorRecord, "sha256:e", 1, _basic.ContextId, null, null, 0, [], _clock.UtcNow), CancellationToken.None);
            return 0;
        });

    [Fact]
    public async Task The_latest_capture_of_a_kind_replaces_the_earlier_one()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store);
        var id = ScreenCapture.IdOf(ScreenCaptureKind.KeyboardFocus, "st_1", _basic.ContextId);
        var failed = new ScreenCapture(id, ScreenCaptureKind.KeyboardFocus, "st_1", _basic.ContextId, "task_1", ScreenCaptureStatus.Failed, null, null, [], "pressKeyFailed", _clock.UtcNow);
        await InTx(store, async uow =>
        {
            await uow.Contracts.UpsertCaptureAsync(failed, CancellationToken.None);
            await uow.Contracts.UpsertCaptureAsync(failed with { Status = ScreenCaptureStatus.Captured, ArtifactSha256 = "sha256:p", EvidenceIds = ["ev_1"], Detail = null }, CancellationToken.None);
            await uow.Contracts.UpsertCaptureAsync(failed with { CaptureId = ScreenCapture.IdOf(ScreenCaptureKind.FullHierarchy, "st_1", _basic.ContextId), Kind = ScreenCaptureKind.FullHierarchy, Status = ScreenCaptureStatus.Diverged }, CancellationToken.None);
            return 0;
        });

        var captures = await InTx(store, uow => uow.Contracts.ListCapturesAsync(_basic.ContextId, CancellationToken.None));
        Assert.Equal([ScreenCaptureKind.FullHierarchy, ScreenCaptureKind.KeyboardFocus], captures.Select(c => c.Kind));
        var probe = captures.Single(c => c.Kind == ScreenCaptureKind.KeyboardFocus);
        Assert.Equal((ScreenCaptureStatus.Captured, "sha256:p", null), (probe.Status, probe.ArtifactSha256, probe.Detail));
        Assert.Equal(["ev_1"], probe.EvidenceIds);
    }

    [Fact]
    public async Task Contract_versions_and_records_round_trip()
    {
        using var store = SqliteStateStore.Open(_path, _clock);
        await SeedAsync(store);
        var v1 = new ScreenContractVersion(ScreenContractVersion.IdOf("scr_1", "g", 1), "scr_1", "g", 1, "sha256:c1", "sha256:a1", _clock.UtcNow);
        var record = new AccessibilityRecord("a11y_1", "scr_1", "st_1", _basic.ContextId, AccessibilityTechnology.TalkBack, null, "screen", AccessibilityRecordResult.Pass, "qa", "Pixel", "mapcue.a11y-protocol/1", null, "ev_1", _clock.UtcNow);
        await InTx(store, async uow =>
        {
            await uow.Contracts.InsertContractAsync(v1, CancellationToken.None);
            await uow.Contracts.InsertContractAsync(v1 with { ContractId = ScreenContractVersion.IdOf("scr_1", "g", 2), Version = 2, ContentHash = "sha256:c2" }, CancellationToken.None);
            Assert.True(await uow.Contracts.InsertRecordAsync(record, CancellationToken.None));
            Assert.False(await uow.Contracts.InsertRecordAsync(record, CancellationToken.None));
            return 0;
        });

        var latest = await InTx(store, uow => uow.Contracts.GetLatestContractAsync("scr_1", "g", CancellationToken.None));
        Assert.Equal((2, "sha256:c2"), (latest!.Version, latest.ContentHash));
        Assert.Null(await InTx(store, uow => uow.Contracts.GetLatestContractAsync("scr_1", "other", CancellationToken.None)));
        Assert.Equal(record, Assert.Single(await InTx(store, uow => uow.Contracts.ListRecordsAsync(CancellationToken.None))));

        // A version number is never reused for the same screen and group.
        await Assert.ThrowsAnyAsync<Exception>(() => InTx(store, async uow =>
        {
            await uow.Contracts.InsertContractAsync(v1 with { ContractId = "sc_other" }, CancellationToken.None);
            return 0;
        }));
    }

    [Fact]
    public async Task Migration_7_applies_on_a_version_6_workspace()
    {
        using (var old = SqliteStateStore.Open(_path, _clock, targetVersion: 6))
        {
            await SeedAsync(old);
        }

        using var upgraded = SqliteStateStore.Open(_path, _clock);
        Assert.True(SqliteMigrator.LatestVersion >= 7);
        Assert.Empty(await InTx(upgraded, uow => uow.Contracts.ListCapturesAsync(null, CancellationToken.None)));
        Assert.Empty(await InTx(upgraded, uow => uow.Contracts.ListContractsAsync(CancellationToken.None)));
        Assert.Single(await InTx(upgraded, uow => uow.Graph.ListStatesAsync(CancellationToken.None)));
    }
}

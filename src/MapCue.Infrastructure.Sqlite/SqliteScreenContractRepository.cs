using MapCue.Application.Ports;
using MapCue.Domain.Graph;
using Microsoft.Data.Sqlite;

namespace MapCue.Infrastructure.Sqlite;

/// <summary>Screen captures, contract versions and manual accessibility records (migration <c>0007</c>, STEP-08).</summary>
internal sealed class SqliteScreenContractRepository(SqliteConnection connection, SqliteTransaction transaction) : SqliteRepository(connection, transaction), IScreenContractRepository
{
    private const string CaptureColumns = "capture_id, kind, state_id, context_id, task_id, status, snapshot_id, artifact_sha256, evidence_ids_json, detail, created_at";
    private const string RecordColumns = "record_id, screen_id, state_id, context_id, technology, element_key, property, result, operator, device, protocol_version, note, evidence_id, recorded_at";
    private const string ContractColumns = "contract_id, screen_id, group_key, version, content_hash, artifact_sha256, created_at";

    public Task UpsertCaptureAsync(ScreenCapture capture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return ExecuteAsync(
            $"""
            INSERT INTO screen_captures ({CaptureColumns}) VALUES ($id, $kind, $state, $ctx, $task, $status, $snap, $artifact, $evidence, $detail, $at)
            ON CONFLICT(capture_id) DO UPDATE SET task_id = excluded.task_id, status = excluded.status, snapshot_id = excluded.snapshot_id,
                artifact_sha256 = excluded.artifact_sha256, evidence_ids_json = excluded.evidence_ids_json, detail = excluded.detail, created_at = excluded.created_at
            """,
            ct,
            ("$id", capture.CaptureId),
            ("$kind", capture.Kind.ToString()),
            ("$state", capture.StateId),
            ("$ctx", capture.ContextId),
            ("$task", capture.TaskId),
            ("$status", capture.Status.ToString()),
            ("$snap", capture.SnapshotId),
            ("$artifact", capture.ArtifactSha256),
            ("$evidence", Db.Json(capture.EvidenceIds)),
            ("$detail", capture.Detail),
            ("$at", Db.Time(capture.CreatedAt)));
    }

    public async Task<IReadOnlyList<ScreenCapture>> ListCapturesAsync(string? contextId, CancellationToken ct) =>
        contextId is null
            ? await QueryAsync($"SELECT {CaptureColumns} FROM screen_captures ORDER BY context_id, state_id, kind", MapCapture, ct)
            : await QueryAsync($"SELECT {CaptureColumns} FROM screen_captures WHERE context_id = $ctx ORDER BY state_id, kind", MapCapture, ct, ("$ctx", contextId));

    public async Task<bool> InsertRecordAsync(AccessibilityRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);
        return await ExecuteAsync(
            $"INSERT INTO a11y_records ({RecordColumns}) VALUES ($id, $screen, $state, $ctx, $tech, $element, $property, $result, $operator, $device, $protocol, $note, $evidence, $at) ON CONFLICT(record_id) DO NOTHING",
            ct,
            ("$id", record.RecordId),
            ("$screen", record.ScreenId),
            ("$state", record.StateId),
            ("$ctx", record.ContextId),
            ("$tech", record.Technology.ToString()),
            ("$element", record.ElementKey),
            ("$property", record.Property),
            ("$result", record.Result.ToString()),
            ("$operator", record.Operator),
            ("$device", record.Device),
            ("$protocol", record.ProtocolVersion),
            ("$note", record.Note),
            ("$evidence", record.EvidenceId),
            ("$at", Db.Time(record.RecordedAt))) == 1;
    }

    public async Task<IReadOnlyList<AccessibilityRecord>> ListRecordsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {RecordColumns} FROM a11y_records ORDER BY screen_id, context_id, recorded_at, record_id", MapRecord, ct);

    public Task<ScreenContractVersion?> GetLatestContractAsync(string screenId, string groupKey, CancellationToken ct) =>
        SingleOrDefaultAsync(
            $"SELECT {ContractColumns} FROM screen_contracts WHERE screen_id = $screen AND group_key = $group ORDER BY version DESC LIMIT 1",
            MapContract,
            ct,
            ("$screen", screenId),
            ("$group", groupKey));

    public Task InsertContractAsync(ScreenContractVersion contract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ExecuteAsync(
            $"INSERT INTO screen_contracts ({ContractColumns}) VALUES ($id, $screen, $group, $version, $hash, $artifact, $at)",
            ct,
            ("$id", contract.ContractId),
            ("$screen", contract.ScreenId),
            ("$group", contract.GroupKey),
            ("$version", contract.Version),
            ("$hash", contract.ContentHash),
            ("$artifact", contract.ArtifactSha256),
            ("$at", Db.Time(contract.CreatedAt)));
    }

    public async Task<IReadOnlyList<ScreenContractVersion>> ListContractsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {ContractColumns} FROM screen_contracts ORDER BY screen_id, group_key, version", MapContract, ct);

    private static ScreenCapture MapCapture(SqliteDataReader r) =>
        new(
            r.GetString(0),
            Db.ReadEnum<ScreenCaptureKind>(r, 1),
            r.GetString(2),
            r.GetString(3),
            Db.ReadStringOrNull(r, 4),
            Db.ReadEnum<ScreenCaptureStatus>(r, 5),
            Db.ReadStringOrNull(r, 6),
            Db.ReadStringOrNull(r, 7),
            Db.ReadJson<List<string>>(r, 8),
            Db.ReadStringOrNull(r, 9),
            Db.ReadTime(r, 10));

    private static AccessibilityRecord MapRecord(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            Db.ReadStringOrNull(r, 2),
            r.GetString(3),
            Db.ReadEnum<AccessibilityTechnology>(r, 4),
            Db.ReadStringOrNull(r, 5),
            r.GetString(6),
            Db.ReadEnum<AccessibilityRecordResult>(r, 7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10),
            Db.ReadStringOrNull(r, 11),
            r.GetString(12),
            Db.ReadTime(r, 13));

    private static ScreenContractVersion MapContract(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5), Db.ReadTime(r, 6));
}

using MapCue.Application.Ports;
using MapCue.Domain;
using MapCue.Domain.Graph;
using Microsoft.Data.Sqlite;

namespace MapCue.Infrastructure.Sqlite;

internal abstract class SqliteRepository(SqliteConnection connection, SqliteTransaction transaction)
{
    protected async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Db.Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    protected async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Db.Command(connection, transaction, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            result.Add(map(reader));
        }

        return result;
    }

    protected async Task<T?> SingleOrDefaultAsync<T>(string sql, Func<SqliteDataReader, T> map, CancellationToken ct, params (string Name, object? Value)[] parameters)
        where T : class =>
        (await QueryAsync(sql, map, ct, parameters)).SingleOrDefault();

    protected async Task<long> ScalarAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        using var command = Db.Command(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class SqliteGraphRepository(SqliteConnection connection, SqliteTransaction transaction) : SqliteRepository(connection, transaction), IGraphRepository
{
    private const string ContextColumns = "context_id, signature, profile_id, platform, app_id, app_build_id, locale, scenario_id, capabilities_json";
    private const string StateColumns = "state_id, screen_id, state_kind, identity_hash, fingerprint_version, identity_tokens_json, base_element_keys_json, identity_status, created_at";
    private const string VariantColumns = "state_id, context_id, element_set_hash, added_keys_json, removed_keys_json, snapshot_count, first_seen_at, last_seen_at";
    private const string SnapshotColumns = "snapshot_id, snapshot_key, context_id, attempt_id, fidelity, payload_hash, raw_sha256, normalized_sha256, identity_hash, layout_hash, element_set_hash, fingerprint_version, state_id, screenshot_sha256, redaction_version, observed_at";
    private const string EvidenceColumns = "evidence_id, kind, artifact_sha256, length, context_id, snapshot_id, attempt_id, redaction_count, redaction_rules_json, created_at";
    private const string CandidateColumns = "state_id, action_key, action_type, canonical_selector, param_class, source, risk_class, status, created_at";
    private const string EdgeColumns = "edge_id, from_state_id, action_key, context_id, to_state_id, status, observation_count, nondeterministic, first_observed_at, last_observed_at, last_verified_at";
    private const string ObservationColumns = "attempt_id, edge_id, context_id, before_snapshot_id, after_snapshot_id, to_state_id, outcome, duration_ms, observed_at";

    public async Task<bool> UpsertContextAsync(ExplorationContext context, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return await ExecuteAsync(
            $"INSERT INTO contexts ({ContextColumns}, created_at) VALUES ($id, $sig, $profile, $platform, $app, $build, $locale, $scenario, $caps, $now) ON CONFLICT(signature) DO NOTHING",
            ct,
            ("$id", context.ContextId),
            ("$sig", context.Signature),
            ("$profile", context.ProfileId),
            ("$platform", context.Platform.ToString()),
            ("$app", context.AppId),
            ("$build", context.AppBuildId),
            ("$locale", context.Locale),
            ("$scenario", context.ScenarioId),
            ("$caps", Db.Json(context.Capabilities)),
            ("$now", Db.Time(now))) == 1;
    }

    public Task<ExplorationContext?> GetContextAsync(string contextId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {ContextColumns} FROM contexts WHERE context_id = $id", MapContext, ct, ("$id", contextId));

    public async Task<IReadOnlyList<ExplorationContext>> ListContextsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {ContextColumns} FROM contexts ORDER BY context_id", MapContext, ct);

    public Task InsertScreenAsync(LogicalScreen screen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return ExecuteAsync(
            "INSERT INTO logical_screens (screen_id, screen_key, display_name, identity_status, identity_source, created_at) VALUES ($id, $key, $name, $status, $source, $at)",
            ct,
            ("$id", screen.ScreenId),
            ("$key", screen.ScreenKey),
            ("$name", screen.DisplayName),
            ("$status", screen.IdentityStatus.ToString()),
            ("$source", screen.IdentitySource.ToString()),
            ("$at", Db.Time(screen.CreatedAt)));
    }

    public Task<LogicalScreen?> GetScreenAsync(string screenId, CancellationToken ct) =>
        SingleOrDefaultAsync("SELECT screen_id, screen_key, display_name, identity_status, identity_source, created_at FROM logical_screens WHERE screen_id = $id", MapScreen, ct, ("$id", screenId));

    public async Task<IReadOnlyList<LogicalScreen>> ListScreensAsync(CancellationToken ct) =>
        await QueryAsync("SELECT screen_id, screen_key, display_name, identity_status, identity_source, created_at FROM logical_screens ORDER BY screen_id", MapScreen, ct);

    public Task InsertStateAsync(ScreenState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        return ExecuteAsync(
            $"INSERT INTO screen_states ({StateColumns}) VALUES ($id, $screen, $kind, $hash, $fp, $tokens, $base, $status, $at)",
            ct,
            ("$id", state.StateId),
            ("$screen", state.ScreenId),
            ("$kind", state.StateKind.ToString()),
            ("$hash", state.IdentityHash),
            ("$fp", state.FingerprintVersion),
            ("$tokens", Db.Json(state.IdentityTokens)),
            ("$base", Db.Json(state.BaseElementKeys)),
            ("$status", state.IdentityStatus.ToString()),
            ("$at", Db.Time(state.CreatedAt)));
    }

    public Task<ScreenState?> GetStateAsync(string stateId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {StateColumns} FROM screen_states WHERE state_id = $id", MapState, ct, ("$id", stateId));

    public async Task<IReadOnlyList<ScreenState>> ListStatesAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {StateColumns} FROM screen_states ORDER BY state_id", MapState, ct);

    public Task AddIdentityAliasAsync(string identityHash, int fingerprintVersion, string stateId, double similarity, DateTimeOffset now, CancellationToken ct) =>
        ExecuteAsync(
            "INSERT INTO state_identity_aliases (identity_hash, fingerprint_version, state_id, similarity, created_at) VALUES ($hash, $fp, $state, $sim, $at) ON CONFLICT DO NOTHING",
            ct,
            ("$hash", identityHash),
            ("$fp", fingerprintVersion),
            ("$state", stateId),
            ("$sim", similarity),
            ("$at", Db.Time(now)));

    public async Task<IReadOnlyDictionary<string, string>> GetIdentityAliasesAsync(int fingerprintVersion, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT identity_hash, state_id FROM state_identity_aliases WHERE fingerprint_version = $fp", r => (r.GetString(0), r.GetString(1)), ct, ("$fp", fingerprintVersion));
        return rows.ToDictionary(r => r.Item1, r => r.Item2, StringComparer.Ordinal);
    }

    public Task<ScreenVariant?> GetVariantAsync(string stateId, string contextId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {VariantColumns} FROM screen_variants WHERE state_id = $state AND context_id = $ctx", MapVariant, ct, ("$state", stateId), ("$ctx", contextId));

    public Task UpsertVariantAsync(ScreenVariant variant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return ExecuteAsync(
            $"""
            INSERT INTO screen_variants ({VariantColumns}) VALUES ($state, $ctx, $hash, $added, $removed, $count, $first, $last)
            ON CONFLICT(state_id, context_id) DO UPDATE SET element_set_hash = excluded.element_set_hash, added_keys_json = excluded.added_keys_json,
                removed_keys_json = excluded.removed_keys_json, snapshot_count = excluded.snapshot_count, last_seen_at = excluded.last_seen_at
            """,
            ct,
            ("$state", variant.StateId),
            ("$ctx", variant.ContextId),
            ("$hash", variant.ElementSetHash),
            ("$added", Db.Json(variant.AddedElementKeys)),
            ("$removed", Db.Json(variant.RemovedElementKeys)),
            ("$count", variant.SnapshotCount),
            ("$first", Db.Time(variant.FirstSeenAt)),
            ("$last", Db.Time(variant.LastSeenAt)));
    }

    public async Task<IReadOnlyList<ScreenVariant>> ListVariantsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {VariantColumns} FROM screen_variants ORDER BY state_id, context_id", MapVariant, ct);

    public async Task<(UiSnapshot Snapshot, bool Inserted)> GetOrInsertSnapshotAsync(UiSnapshot snapshot, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var inserted = await ExecuteAsync(
            $"INSERT INTO ui_snapshots ({SnapshotColumns}) VALUES ($id, $key, $ctx, $attempt, $fidelity, $payload, $raw, $norm, $identity, $layout, $elements, $fp, $state, $shot, $redaction, $at) ON CONFLICT(snapshot_key) DO NOTHING",
            ct,
            ("$id", snapshot.SnapshotId),
            ("$key", snapshot.SnapshotKey),
            ("$ctx", snapshot.ContextId),
            ("$attempt", snapshot.AttemptId),
            ("$fidelity", snapshot.Fidelity.ToString()),
            ("$payload", snapshot.PayloadHash),
            ("$raw", snapshot.RawSha256),
            ("$norm", snapshot.NormalizedSha256),
            ("$identity", snapshot.IdentityHash),
            ("$layout", snapshot.LayoutHash),
            ("$elements", snapshot.ElementSetHash),
            ("$fp", snapshot.FingerprintVersion),
            ("$state", snapshot.StateId),
            ("$shot", snapshot.ScreenshotSha256),
            ("$redaction", snapshot.RedactionVersion),
            ("$at", Db.Time(snapshot.ObservedAt))) == 1;
        var stored = await SingleOrDefaultAsync($"SELECT {SnapshotColumns} FROM ui_snapshots WHERE snapshot_key = $key", MapSnapshot, ct, ("$key", snapshot.SnapshotKey));
        return (stored!, inserted);
    }

    public Task<UiSnapshot?> GetSnapshotAsync(string snapshotId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {SnapshotColumns} FROM ui_snapshots WHERE snapshot_id = $id", MapSnapshot, ct, ("$id", snapshotId));

    public async Task<IReadOnlyList<UiSnapshot>> ListSnapshotsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {SnapshotColumns} FROM ui_snapshots ORDER BY snapshot_id", MapSnapshot, ct);

    public async Task<bool> InsertEvidenceIfAbsentAsync(Evidence evidence, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return await ExecuteAsync(
            $"INSERT INTO evidence ({EvidenceColumns}) VALUES ($id, $kind, $sha, $len, $ctx, $snap, $attempt, $count, $rules, $at) ON CONFLICT(evidence_id) DO NOTHING",
            ct,
            ("$id", evidence.EvidenceId),
            ("$kind", evidence.Kind.ToString()),
            ("$sha", evidence.ArtifactSha256),
            ("$len", evidence.Length),
            ("$ctx", evidence.ContextId),
            ("$snap", evidence.SnapshotId),
            ("$attempt", evidence.AttemptId),
            ("$count", evidence.RedactionCount),
            ("$rules", Db.Json(evidence.RedactionRules)),
            ("$at", Db.Time(evidence.CreatedAt))) == 1;
    }

    public async Task<IReadOnlyList<Evidence>> ListEvidenceAsync(string contextId, string? stateId, CancellationToken ct) =>
        stateId is null
            ? await QueryAsync($"SELECT {EvidenceColumns} FROM evidence WHERE context_id = $ctx ORDER BY evidence_id", MapEvidence, ct, ("$ctx", contextId))
            : await QueryAsync(
                $"SELECT {string.Join(", ", EvidenceColumns.Split(", ").Select(c => "e." + c))} FROM evidence e JOIN ui_snapshots s ON s.snapshot_id = e.snapshot_id WHERE e.context_id = $ctx AND s.state_id = $state ORDER BY e.evidence_id",
                MapEvidence,
                ct,
                ("$ctx", contextId),
                ("$state", stateId));

    public async Task<bool> InsertCandidateIfAbsentAsync(ActionCandidate candidate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return await ExecuteAsync(
            $"INSERT INTO action_candidates ({CandidateColumns}) VALUES ($state, $key, $type, $selector, $param, $source, $risk, $status, $at) ON CONFLICT(state_id, action_key) DO NOTHING",
            ct,
            ("$state", candidate.StateId),
            ("$key", candidate.ActionKey),
            ("$type", candidate.ActionType.ToString()),
            ("$selector", candidate.CanonicalSelector),
            ("$param", candidate.ParamClass),
            ("$source", candidate.Source.ToString()),
            ("$risk", candidate.RiskClass.ToString()),
            ("$status", candidate.Status.ToString()),
            ("$at", Db.Time(candidate.CreatedAt))) == 1;
    }

    public async Task<IReadOnlyList<ActionCandidate>> ListCandidatesAsync(string? stateId, CancellationToken ct) =>
        stateId is null
            ? await QueryAsync($"SELECT {CandidateColumns} FROM action_candidates ORDER BY state_id, action_key", MapCandidate, ct)
            : await QueryAsync($"SELECT {CandidateColumns} FROM action_candidates WHERE state_id = $state ORDER BY action_key", MapCandidate, ct, ("$state", stateId));

    public Task<ScreenEdge?> GetEdgeAsync(string fromStateId, string actionKey, string contextId, CancellationToken ct) =>
        SingleOrDefaultAsync(
            $"SELECT {EdgeColumns} FROM screen_edges WHERE from_state_id = $from AND action_key = $key AND context_id = $ctx",
            MapEdge,
            ct,
            ("$from", fromStateId),
            ("$key", actionKey),
            ("$ctx", contextId));

    public Task InsertEdgeAsync(ScreenEdge edge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return ExecuteAsync(
            $"INSERT INTO screen_edges ({EdgeColumns}) VALUES ($id, $from, $key, $ctx, $to, $status, $count, $nd, $first, $last, $verified)",
            ct,
            EdgeParameters(edge));
    }

    public async Task UpdateEdgeAsync(ScreenEdge edge, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edge);
        var rows = await ExecuteAsync(
            "UPDATE screen_edges SET to_state_id = $to, status = $status, observation_count = $count, nondeterministic = $nd, last_observed_at = $last, last_verified_at = $verified WHERE edge_id = $id",
            ct,
            EdgeParameters(edge));
        EnsureOne(rows, "edge", edge.EdgeId);
    }

    public async Task<IReadOnlyList<ScreenEdge>> ListEdgesAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {EdgeColumns} FROM screen_edges ORDER BY edge_id", MapEdge, ct);

    public async Task<bool> InsertObservationIfAbsentAsync(ActionObservation observation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return await ExecuteAsync(
            $"INSERT INTO action_observations ({ObservationColumns}) VALUES ($attempt, $edge, $ctx, $before, $after, $to, $outcome, $ms, $at) ON CONFLICT(attempt_id) DO NOTHING",
            ct,
            ("$attempt", observation.AttemptId),
            ("$edge", observation.EdgeId),
            ("$ctx", observation.ContextId),
            ("$before", observation.BeforeSnapshotId),
            ("$after", observation.AfterSnapshotId),
            ("$to", observation.ToStateId),
            ("$outcome", observation.Outcome.ToString()),
            ("$ms", observation.DurationMs),
            ("$at", Db.Time(observation.ObservedAt))) == 1;
    }

    public Task<ActionObservation?> GetObservationAsync(string attemptId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {ObservationColumns} FROM action_observations WHERE attempt_id = $id", MapObservation, ct, ("$id", attemptId));

    public async Task<IReadOnlyList<ActionObservation>> ListObservationsAsync(CancellationToken ct) =>
        await QueryAsync($"SELECT {ObservationColumns} FROM action_observations ORDER BY attempt_id", MapObservation, ct);

    public Task UpsertRecipeAsync(ReachabilityRecipe recipe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return ExecuteAsync(
            """
            INSERT INTO reachability_recipes (recipe_id, context_id, target_state_id, steps_json, verified_at, based_on_edges_json) VALUES ($id, $ctx, $target, $steps, $verified, $edges)
            ON CONFLICT(recipe_id) DO UPDATE SET steps_json = excluded.steps_json, verified_at = excluded.verified_at, based_on_edges_json = excluded.based_on_edges_json
            """,
            ct,
            ("$id", recipe.RecipeId),
            ("$ctx", recipe.ContextId),
            ("$target", recipe.TargetStateId),
            ("$steps", Db.Json(recipe.Steps)),
            ("$verified", Db.Time(recipe.VerifiedAt)),
            ("$edges", Db.Json(recipe.BasedOnEdges)));
    }

    public Task<ReachabilityRecipe?> GetRecipeAsync(string recipeId, CancellationToken ct) =>
        SingleOrDefaultAsync(
            "SELECT recipe_id, context_id, target_state_id, steps_json, verified_at, based_on_edges_json FROM reachability_recipes WHERE recipe_id = $id",
            r => new ReachabilityRecipe(r.GetString(0), r.GetString(1), r.GetString(2), Db.ReadJson<List<RecipeStep>>(r, 3), Db.ReadTimeOrNull(r, 4), Db.ReadJson<List<string>>(r, 5)),
            ct,
            ("$id", recipeId));

    internal static void EnsureOne(int rows, string what, string id)
    {
        if (rows != 1)
        {
            throw new InvalidOperationException($"Expected to update one {what} '{id}', updated {rows}.");
        }
    }

    private static (string, object?)[] EdgeParameters(ScreenEdge edge) =>
    [
        ("$id", edge.EdgeId),
        ("$from", edge.FromStateId),
        ("$key", edge.ActionKey),
        ("$ctx", edge.ContextId),
        ("$to", edge.ToStateId),
        ("$status", edge.Status.ToString()),
        ("$count", edge.ObservationCount),
        ("$nd", edge.Nondeterministic ? 1 : 0),
        ("$first", Db.Time(edge.FirstObservedAt)),
        ("$last", Db.Time(edge.LastObservedAt)),
        ("$verified", Db.Time(edge.LastVerifiedAt)),
    ];

    private static ExplorationContext MapContext(SqliteDataReader r)
    {
        var context = ExplorationContext.Create(r.GetString(2), Db.ReadEnum<Platform>(r, 3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), Db.ReadJson<List<string>>(r, 8));
        if (context.Signature != r.GetString(1) || context.ContextId != r.GetString(0))
        {
            throw new InvalidOperationException($"Stored context '{r.GetString(0)}' does not match its signature.");
        }

        return context;
    }

    private static LogicalScreen MapScreen(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Db.ReadStringOrNull(r, 2), Db.ReadEnum<IdentityStatus>(r, 3), Db.ReadEnum<IdentitySource>(r, 4), Db.ReadTime(r, 5));

    private static ScreenState MapState(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Db.ReadEnum<StateKind>(r, 2), r.GetString(3), r.GetInt32(4), Db.ReadJson<List<string>>(r, 5), Db.ReadJson<List<string>>(r, 6), Db.ReadEnum<IdentityStatus>(r, 7), Db.ReadTime(r, 8));

    private static ScreenVariant MapVariant(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), Db.ReadJson<List<string>>(r, 3), Db.ReadJson<List<string>>(r, 4), r.GetInt32(5), Db.ReadTime(r, 6), Db.ReadTime(r, 7));

    private static UiSnapshot MapSnapshot(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            Db.ReadStringOrNull(r, 3),
            Db.ReadEnum<SnapshotFidelity>(r, 4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10),
            r.GetInt32(11),
            Db.ReadStringOrNull(r, 12),
            Db.ReadStringOrNull(r, 13),
            r.GetInt32(14),
            Db.ReadTime(r, 15));

    private static Evidence MapEvidence(SqliteDataReader r) =>
        new(r.GetString(0), Db.ReadEnum<EvidenceKind>(r, 1), r.GetString(2), r.GetInt64(3), r.GetString(4), Db.ReadStringOrNull(r, 5), Db.ReadStringOrNull(r, 6), r.GetInt32(7), Db.ReadJson<List<string>>(r, 8), Db.ReadTime(r, 9));

    private static ActionCandidate MapCandidate(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), Db.ReadEnum<ActionType>(r, 2), r.GetString(3), Db.ReadStringOrNull(r, 4), Db.ReadEnum<CandidateSource>(r, 5), Db.ReadEnum<RiskClass>(r, 6), Db.ReadEnum<CandidateStatus>(r, 7), Db.ReadTime(r, 8));

    private static ScreenEdge MapEdge(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Db.ReadStringOrNull(r, 4), Db.ReadEnum<EdgeStatus>(r, 5), r.GetInt32(6), r.GetInt32(7) != 0, Db.ReadTime(r, 8), Db.ReadTime(r, 9), Db.ReadTimeOrNull(r, 10));

    private static ActionObservation MapObservation(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Db.ReadStringOrNull(r, 4), Db.ReadStringOrNull(r, 5), Db.ReadEnum<ActionOutcome>(r, 6), r.GetInt64(7), Db.ReadTime(r, 8));
}

internal sealed class SqliteFrontierRepository(SqliteConnection connection, SqliteTransaction transaction) : SqliteRepository(connection, transaction), IFrontierRepository
{
    private const string TaskColumns = "task_id, task_key, type, status, priority, depth, context_id, from_state_id, action_key, recipe_id, lease_owner, lease_expires_at, blocked_code, blocked_details, blocked_evidence_json, blocked_resume_doc, created_seq, created_at, updated_at";
    private const string AttemptColumns = "attempt_id, task_id, n, phase, outcome, error, artifacts_dir, started_at, ended_at";

    public async Task<bool> InsertTaskIfAbsentAsync(FrontierTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        return await ExecuteAsync(
            """
            INSERT INTO frontier_tasks (task_id, task_key, type, status, priority, depth, context_id, from_state_id, action_key, recipe_id, lease_owner, lease_expires_at,
                blocked_code, blocked_details, blocked_evidence_json, blocked_resume_doc, created_at, updated_at)
            VALUES ($id, $key, $type, $status, $priority, $depth, $ctx, $from, $action, $recipe, $owner, $expires, $bcode, $bdetails, $bevidence, $bresume, $created, $updated)
            ON CONFLICT(task_key) DO NOTHING
            """,
            ct,
            TaskParameters(task)) == 1;
    }

    public Task<FrontierTask?> GetTaskAsync(string taskId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {TaskColumns} FROM frontier_tasks WHERE task_id = $id", MapTask, ct, ("$id", taskId));

    public Task<FrontierTask?> GetTaskByKeyAsync(string taskKey, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {TaskColumns} FROM frontier_tasks WHERE task_key = $key", MapTask, ct, ("$key", taskKey));

    public async Task UpdateTaskAsync(FrontierTask task, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(task);
        var rows = await ExecuteAsync(
            """
            UPDATE frontier_tasks SET status = $status, priority = $priority, depth = $depth, recipe_id = $recipe, lease_owner = $owner, lease_expires_at = $expires,
                blocked_code = $bcode, blocked_details = $bdetails, blocked_evidence_json = $bevidence, blocked_resume_doc = $bresume, updated_at = $updated
            WHERE task_id = $id
            """,
            ct,
            TaskParameters(task));
        SqliteGraphRepository.EnsureOne(rows, "task", task.TaskId);
    }

    public async Task<IReadOnlyList<FrontierTask>> ListTasksAsync(FrontierTaskStatus? status, CancellationToken ct) =>
        status is null
            ? await QueryAsync($"SELECT {TaskColumns} FROM frontier_tasks ORDER BY priority DESC, depth, created_seq", MapTask, ct)
            : await QueryAsync($"SELECT {TaskColumns} FROM frontier_tasks WHERE status = $status ORDER BY priority DESC, depth, created_seq", MapTask, ct, ("$status", status.Value.ToString()));

    public async Task<FrontierStats> GetStatsAsync(CancellationToken ct)
    {
        var counts = (await QueryAsync("SELECT status, COUNT(*) FROM frontier_tasks GROUP BY status", r => (Db.ReadEnum<FrontierTaskStatus>(r, 0), r.GetInt32(1)), ct))
            .ToDictionary(x => x.Item1, x => x.Item2);
        int Of(FrontierTaskStatus s) => counts.GetValueOrDefault(s);
        return new FrontierStats(
            Of(FrontierTaskStatus.Pending),
            Of(FrontierTaskStatus.Leased),
            Of(FrontierTaskStatus.Completed),
            Of(FrontierTaskStatus.Blocked),
            Of(FrontierTaskStatus.Failed),
            Of(FrontierTaskStatus.OutcomeUnknown),
            Of(FrontierTaskStatus.ReadyToResume),
            Of(FrontierTaskStatus.Cancelled));
    }

    public async Task<bool> InsertAttemptIfAbsentAsync(TaskAttempt attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return await ExecuteAsync(
            $"INSERT INTO task_attempts ({AttemptColumns}) VALUES ($id, $task, $n, $phase, $outcome, $error, $dir, $started, $ended) ON CONFLICT(attempt_id) DO NOTHING",
            ct,
            AttemptParameters(attempt)) == 1;
    }

    public async Task UpdateAttemptAsync(TaskAttempt attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        var rows = await ExecuteAsync(
            "UPDATE task_attempts SET phase = $phase, outcome = $outcome, error = $error, artifacts_dir = $dir, ended_at = $ended WHERE attempt_id = $id",
            ct,
            AttemptParameters(attempt));
        SqliteGraphRepository.EnsureOne(rows, "attempt", attempt.AttemptId);
    }

    public Task<TaskAttempt?> GetAttemptAsync(string attemptId, CancellationToken ct) =>
        SingleOrDefaultAsync($"SELECT {AttemptColumns} FROM task_attempts WHERE attempt_id = $id", MapAttempt, ct, ("$id", attemptId));

    public async Task<IReadOnlyList<TaskAttempt>> ListAttemptsAsync(string? taskId, CancellationToken ct) =>
        taskId is null
            ? await QueryAsync($"SELECT {AttemptColumns} FROM task_attempts ORDER BY task_id, n", MapAttempt, ct)
            : await QueryAsync($"SELECT {AttemptColumns} FROM task_attempts WHERE task_id = $task ORDER BY n", MapAttempt, ct, ("$task", taskId));

    public Task InsertCheckpointAsync(Checkpoint checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return ExecuteAsync(
            "INSERT INTO checkpoints (checkpoint_id, run_id, event_seq, frontier_stats_json, config_hash, created_at) VALUES ($id, $run, $seq, $stats, $config, $at)",
            ct,
            ("$id", checkpoint.CheckpointId),
            ("$run", checkpoint.RunId),
            ("$seq", checkpoint.EventSeq),
            ("$stats", Db.Json(checkpoint.Frontier)),
            ("$config", checkpoint.ConfigHash),
            ("$at", Db.Time(checkpoint.CreatedAt)));
    }

    public Task<Checkpoint?> GetLatestCheckpointAsync(string runId, CancellationToken ct) =>
        SingleOrDefaultAsync(
            "SELECT checkpoint_id, run_id, event_seq, frontier_stats_json, config_hash, created_at FROM checkpoints WHERE run_id = $run ORDER BY event_seq DESC, checkpoint_id DESC LIMIT 1",
            r => new Checkpoint(r.GetString(0), r.GetString(1), r.GetInt64(2), Db.ReadJson<FrontierStats>(r, 3), r.GetString(4), Db.ReadTime(r, 5)),
            ct,
            ("$run", runId));

    private static (string, object?)[] TaskParameters(FrontierTask task) =>
    [
        ("$id", task.TaskId),
        ("$key", task.TaskKey),
        ("$type", task.Type.ToString()),
        ("$status", task.Status.ToString()),
        ("$priority", task.Priority),
        ("$depth", task.Depth),
        ("$ctx", task.ContextId),
        ("$from", task.FromStateId),
        ("$action", task.ActionKey),
        ("$recipe", task.RecipeId),
        ("$owner", task.LeaseOwner),
        ("$expires", Db.Time(task.LeaseExpiresAt)),
        ("$bcode", task.BlockedReason?.Code.ToString()),
        ("$bdetails", task.BlockedReason?.Details),
        ("$bevidence", task.BlockedReason is null ? null : Db.Json(task.BlockedReason.EvidenceIds)),
        ("$bresume", task.BlockedReason?.ResumeDoc),
        ("$created", Db.Time(task.CreatedAt)),
        ("$updated", Db.Time(task.UpdatedAt)),
    ];

    private static (string, object?)[] AttemptParameters(TaskAttempt attempt) =>
    [
        ("$id", attempt.AttemptId),
        ("$task", attempt.TaskId),
        ("$n", attempt.N),
        ("$phase", attempt.Phase.ToString()),
        ("$outcome", attempt.Outcome?.ToString()),
        ("$error", attempt.Error),
        ("$dir", attempt.ArtifactsDir),
        ("$started", Db.Time(attempt.StartedAt)),
        ("$ended", Db.Time(attempt.EndedAt)),
    ];

    private static FrontierTask MapTask(SqliteDataReader r)
    {
        var code = Db.ReadEnumOrNull<BlockedReasonCode>(r, 12);
        var blocked = code is null ? null : new BlockedReason(code.Value, Db.ReadStringOrNull(r, 13) ?? string.Empty, r.IsDBNull(14) ? [] : Db.ReadJson<List<string>>(r, 14), Db.ReadStringOrNull(r, 15));
        return new FrontierTask(
            r.GetString(0),
            r.GetString(1),
            Db.ReadEnum<TaskType>(r, 2),
            Db.ReadEnum<FrontierTaskStatus>(r, 3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetString(6),
            Db.ReadStringOrNull(r, 7),
            Db.ReadStringOrNull(r, 8),
            Db.ReadStringOrNull(r, 9),
            Db.ReadStringOrNull(r, 10),
            Db.ReadTimeOrNull(r, 11),
            blocked,
            r.GetInt64(16),
            Db.ReadTime(r, 17),
            Db.ReadTime(r, 18));
    }

    private static TaskAttempt MapAttempt(SqliteDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetInt32(2), Db.ReadEnum<AttemptPhase>(r, 3), Db.ReadEnumOrNull<ActionOutcome>(r, 4), Db.ReadStringOrNull(r, 5), Db.ReadStringOrNull(r, 6), Db.ReadTime(r, 7), Db.ReadTimeOrNull(r, 8));
}

internal sealed class SqliteEventOutbox(SqliteConnection connection, SqliteTransaction transaction) : SqliteRepository(connection, transaction), IEventOutbox
{
    public async Task<long> AppendAsync(string eventId, string type, DateTimeOffset occurredAt, string? contextId, string? subjectId, string payloadJson, CancellationToken ct)
    {
        await ExecuteAsync(
            "INSERT INTO events (event_id, type, occurred_at, context_id, subject_id, payload_json) VALUES ($id, $type, $at, $ctx, $subject, $payload)",
            ct,
            ("$id", eventId),
            ("$type", type),
            ("$at", Db.Time(occurredAt)),
            ("$ctx", contextId),
            ("$subject", subjectId),
            ("$payload", payloadJson));
        return await ScalarAsync("SELECT last_insert_rowid()", ct);
    }

    public async Task<IReadOnlyList<OutboxEvent>> ListAsync(long afterSeq, int limit, CancellationToken ct) =>
        await QueryAsync(
            "SELECT seq, event_id, type, occurred_at, context_id, subject_id, payload_json FROM events WHERE seq > $after ORDER BY seq LIMIT $limit",
            r => new OutboxEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), Db.ReadTime(r, 3), Db.ReadStringOrNull(r, 4), Db.ReadStringOrNull(r, 5), r.GetString(6)),
            ct,
            ("$after", afterSeq),
            ("$limit", limit));

    public Task<long> LastSeqAsync(CancellationToken ct) => ScalarAsync("SELECT COALESCE(MAX(seq), 0) FROM events", ct);
}

internal sealed class SqliteAgentCallRepository(SqliteConnection connection, SqliteTransaction transaction) : SqliteRepository(connection, transaction), IAgentCallRepository
{
    private const string Columns = "call_id, request_id, call_number, context_id, state_id, task_id, attempt_id, reasoner, prompt_version, snapshot_hash, request_sha256, response_sha256, status, errors_json, decisions_json, input_tokens, output_tokens, cost_usd, duration_ms, created_at";

    public async Task<bool> InsertIfAbsentAsync(AgentCall agentCall, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentCall);
        return await ExecuteAsync(
            $"INSERT INTO agent_calls ({Columns}) VALUES ($id, $req, $n, $ctx, $state, $task, $attempt, $reasoner, $prompt, $snap, $rsha, $asha, $status, $errors, $decisions, $in, $out, $cost, $ms, $at) ON CONFLICT DO NOTHING",
            ct,
            ("$id", agentCall.CallId),
            ("$req", agentCall.RequestId),
            ("$n", agentCall.CallNumber),
            ("$ctx", agentCall.ContextId),
            ("$state", agentCall.StateId),
            ("$task", agentCall.TaskId),
            ("$attempt", agentCall.AttemptId),
            ("$reasoner", agentCall.Reasoner),
            ("$prompt", agentCall.PromptVersion),
            ("$snap", agentCall.SnapshotHash),
            ("$rsha", agentCall.RequestSha256),
            ("$asha", agentCall.ResponseSha256),
            ("$status", agentCall.Status.ToString()),
            ("$errors", Db.Json(agentCall.Errors)),
            ("$decisions", Db.Json(agentCall.Decisions)),
            ("$in", agentCall.InputTokens),
            ("$out", agentCall.OutputTokens),
            ("$cost", agentCall.CostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("$ms", agentCall.DurationMs),
            ("$at", Db.Time(agentCall.CreatedAt))) > 0;
    }

    public async Task<IReadOnlyList<AgentCall>> ListAsync(string? stateId, CancellationToken ct) =>
        await QueryAsync(
            $"SELECT {Columns} FROM agent_calls WHERE $state IS NULL OR state_id = $state ORDER BY seq",
            Map,
            ct,
            ("$state", stateId));

    public async Task<AgentUsageTotals> GetUsageTotalsAsync(CancellationToken ct)
    {
        var rows = await QueryAsync(
            // Calls that were never sent: budget stops and requests refused before sending (Rejected without an answer).
            "SELECT input_tokens, output_tokens, cost_usd FROM agent_calls WHERE status <> 'BudgetExhausted' AND NOT (status = 'Rejected' AND response_sha256 IS NULL)",
            r => (Tokens: (r.IsDBNull(0) ? 0 : r.GetInt64(0)) + (r.IsDBNull(1) ? 0 : r.GetInt64(1)), Cost: Db.ReadStringOrNull(r, 2)),
            ct);
        return new AgentUsageTotals(
            rows.Count,
            rows.Sum(r => r.Tokens),
            rows.Where(r => r.Cost is not null).Sum(r => decimal.Parse(r.Cost!, System.Globalization.CultureInfo.InvariantCulture)),
            rows.Count(r => r.Cost is null));
    }

    private static AgentCall Map(SqliteDataReader r) =>
        new(
            r.GetString(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetString(3),
            Db.ReadStringOrNull(r, 4),
            Db.ReadStringOrNull(r, 5),
            Db.ReadStringOrNull(r, 6),
            r.GetString(7),
            r.GetString(8),
            r.GetString(9),
            r.GetString(10),
            Db.ReadStringOrNull(r, 11),
            Db.ReadEnum<AgentCallStatus>(r, 12),
            Db.ReadJson<List<string>>(r, 13),
            Db.ReadJson<List<AgentCandidateDecision>>(r, 14),
            r.IsDBNull(15) ? null : r.GetInt64(15),
            r.IsDBNull(16) ? null : r.GetInt64(16),
            Db.ReadStringOrNull(r, 17) is { } cost ? decimal.Parse(cost, System.Globalization.CultureInfo.InvariantCulture) : null,
            r.GetInt64(18),
            Db.ReadTime(r, 19));
}

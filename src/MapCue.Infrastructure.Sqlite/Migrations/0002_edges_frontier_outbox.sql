-- STEP-03: candidates, edges, observations, frontier, attempts, recipes, outbox and checkpoints (architecture §6, §7.4, §12).
CREATE TABLE action_candidates (
    state_id           TEXT NOT NULL REFERENCES screen_states (state_id),
    action_key         TEXT NOT NULL,
    action_type        TEXT NOT NULL,
    canonical_selector TEXT NOT NULL,
    param_class        TEXT,
    source             TEXT NOT NULL,
    risk_class         TEXT NOT NULL,
    status             TEXT NOT NULL,
    created_at         TEXT NOT NULL,
    PRIMARY KEY (state_id, action_key)
);

CREATE TABLE screen_edges (
    edge_id           TEXT NOT NULL PRIMARY KEY,
    from_state_id     TEXT NOT NULL,
    action_key        TEXT NOT NULL,
    context_id        TEXT NOT NULL REFERENCES contexts (context_id),
    to_state_id       TEXT REFERENCES screen_states (state_id),
    status            TEXT NOT NULL,
    observation_count INTEGER NOT NULL,
    nondeterministic  INTEGER NOT NULL,
    first_observed_at TEXT NOT NULL,
    last_observed_at  TEXT NOT NULL,
    last_verified_at  TEXT,
    UNIQUE (from_state_id, action_key, context_id),
    FOREIGN KEY (from_state_id, action_key) REFERENCES action_candidates (state_id, action_key)
);

CREATE TABLE frontier_tasks (
    created_seq          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id              TEXT NOT NULL UNIQUE,
    task_key             TEXT NOT NULL UNIQUE,
    type                 TEXT NOT NULL,
    status               TEXT NOT NULL,
    priority             INTEGER NOT NULL,
    depth                INTEGER NOT NULL,
    context_id           TEXT NOT NULL REFERENCES contexts (context_id),
    from_state_id        TEXT REFERENCES screen_states (state_id),
    action_key           TEXT,
    recipe_id            TEXT,
    lease_owner          TEXT,
    lease_expires_at     TEXT,
    blocked_code         TEXT,
    blocked_details      TEXT,
    blocked_evidence_json TEXT,
    blocked_resume_doc   TEXT,
    created_at           TEXT NOT NULL,
    updated_at           TEXT NOT NULL
);

CREATE INDEX ix_frontier_tasks_order ON frontier_tasks (status, priority DESC, depth, created_seq);

CREATE TABLE task_attempts (
    attempt_id    TEXT NOT NULL PRIMARY KEY,
    task_id       TEXT NOT NULL REFERENCES frontier_tasks (task_id),
    n             INTEGER NOT NULL,
    phase         TEXT NOT NULL,
    outcome       TEXT,
    error         TEXT,
    artifacts_dir TEXT,
    started_at    TEXT NOT NULL,
    ended_at      TEXT,
    UNIQUE (task_id, n)
);

CREATE TABLE action_observations (
    attempt_id         TEXT NOT NULL PRIMARY KEY REFERENCES task_attempts (attempt_id) DEFERRABLE INITIALLY DEFERRED,
    edge_id            TEXT NOT NULL REFERENCES screen_edges (edge_id),
    context_id         TEXT NOT NULL REFERENCES contexts (context_id),
    before_snapshot_id TEXT NOT NULL REFERENCES ui_snapshots (snapshot_id),
    after_snapshot_id  TEXT REFERENCES ui_snapshots (snapshot_id),
    to_state_id        TEXT REFERENCES screen_states (state_id),
    outcome            TEXT NOT NULL,
    duration_ms        INTEGER NOT NULL,
    observed_at        TEXT NOT NULL
);

CREATE INDEX ix_action_observations_edge ON action_observations (edge_id);

CREATE TABLE reachability_recipes (
    recipe_id            TEXT NOT NULL PRIMARY KEY,
    context_id           TEXT NOT NULL REFERENCES contexts (context_id),
    target_state_id      TEXT NOT NULL REFERENCES screen_states (state_id),
    steps_json           TEXT NOT NULL,
    verified_at          TEXT,
    based_on_edges_json  TEXT NOT NULL
);

CREATE TABLE events (
    seq          INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id     TEXT NOT NULL UNIQUE,
    type         TEXT NOT NULL,
    occurred_at  TEXT NOT NULL,
    context_id   TEXT,
    subject_id   TEXT,
    payload_json TEXT NOT NULL
);

CREATE TABLE checkpoints (
    checkpoint_id       TEXT NOT NULL PRIMARY KEY,
    run_id              TEXT NOT NULL,
    event_seq           INTEGER NOT NULL,
    frontier_stats_json TEXT NOT NULL,
    config_hash         TEXT NOT NULL,
    created_at          TEXT NOT NULL
);

CREATE INDEX ix_checkpoints_run ON checkpoints (run_id, event_seq);

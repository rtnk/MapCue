-- STEP-05: exploration runs (architecture §5.4) and the validated action of each ExploreAction task, so the frontier can be
-- executed (and replayed for navigation) from SQLite without the reasoner session that proposed it (CAP-0109).
CREATE TABLE runs (
    run_id        TEXT NOT NULL PRIMARY KEY,
    profile_id    TEXT NOT NULL,
    context_id    TEXT REFERENCES contexts (context_id),
    device_id     TEXT,
    reasoner      TEXT NOT NULL,
    config_hash   TEXT NOT NULL,
    seed          TEXT,
    status        TEXT NOT NULL,
    finish_reason TEXT,
    detail        TEXT,
    counters_json TEXT NOT NULL,
    started_at    TEXT NOT NULL,
    ended_at      TEXT
);

CREATE INDEX ix_runs_started ON runs (started_at, run_id);

CREATE TABLE task_actions (
    task_id                 TEXT NOT NULL PRIMARY KEY REFERENCES frontier_tasks (task_id),
    action_json             TEXT NOT NULL,
    element_key             TEXT,
    expected_identity_hash  TEXT NOT NULL,
    created_at              TEXT NOT NULL
);

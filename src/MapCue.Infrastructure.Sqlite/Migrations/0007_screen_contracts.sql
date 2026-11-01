-- STEP-08: screen contracts and accessibility. A capture is what AnalyzeScreen (full hierarchy) and AuditAccessibility (keyboard focus
-- probe) observed for one state in one context; the latest capture of a kind replaces the earlier one. Contract versions are content
-- addressed: a new version only when the content hash of the contract changes. Manual accessibility records of the operator are the only
-- source of the assistiveVerified level for screen readers.
CREATE TABLE screen_captures (
    capture_id        TEXT NOT NULL PRIMARY KEY,
    kind              TEXT NOT NULL,
    state_id          TEXT NOT NULL REFERENCES screen_states (state_id),
    context_id        TEXT NOT NULL REFERENCES contexts (context_id),
    task_id           TEXT,
    status            TEXT NOT NULL,
    snapshot_id       TEXT REFERENCES ui_snapshots (snapshot_id),
    artifact_sha256   TEXT,
    evidence_ids_json TEXT NOT NULL,
    detail            TEXT,
    created_at        TEXT NOT NULL,
    UNIQUE (kind, state_id, context_id)
);

CREATE TABLE screen_contracts (
    contract_id     TEXT NOT NULL PRIMARY KEY,
    screen_id       TEXT NOT NULL REFERENCES logical_screens (screen_id),
    group_key       TEXT NOT NULL,
    version         INTEGER NOT NULL,
    content_hash    TEXT NOT NULL,
    artifact_sha256 TEXT NOT NULL,
    created_at      TEXT NOT NULL,
    UNIQUE (screen_id, group_key, version)
);

CREATE TABLE a11y_records (
    record_id        TEXT NOT NULL PRIMARY KEY,
    screen_id        TEXT NOT NULL REFERENCES logical_screens (screen_id),
    state_id         TEXT REFERENCES screen_states (state_id),
    context_id       TEXT NOT NULL REFERENCES contexts (context_id),
    technology       TEXT NOT NULL,
    element_key      TEXT,
    property         TEXT NOT NULL,
    result           TEXT NOT NULL,
    operator         TEXT NOT NULL,
    device           TEXT NOT NULL,
    protocol_version TEXT NOT NULL,
    note             TEXT,
    evidence_id      TEXT NOT NULL REFERENCES evidence (evidence_id),
    recorded_at      TEXT NOT NULL
);

CREATE INDEX ix_a11y_records_screen ON a11y_records (screen_id, context_id);

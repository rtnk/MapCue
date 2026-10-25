-- STEP-03: graph core (architecture §6). Contexts never enter state identity; variants and evidence are per context.
CREATE TABLE contexts (
    context_id        TEXT NOT NULL PRIMARY KEY,
    signature         TEXT NOT NULL UNIQUE,
    profile_id        TEXT NOT NULL,
    platform          TEXT NOT NULL,
    app_id            TEXT NOT NULL,
    app_build_id      TEXT NOT NULL,
    locale            TEXT NOT NULL,
    scenario_id       TEXT NOT NULL,
    capabilities_json TEXT NOT NULL,
    created_at        TEXT NOT NULL
);

CREATE TABLE logical_screens (
    screen_id       TEXT NOT NULL PRIMARY KEY,
    screen_key      TEXT NOT NULL UNIQUE,
    display_name    TEXT,
    identity_status TEXT NOT NULL,
    identity_source TEXT NOT NULL,
    created_at      TEXT NOT NULL
);

CREATE TABLE screen_states (
    state_id               TEXT NOT NULL PRIMARY KEY,
    screen_id              TEXT NOT NULL REFERENCES logical_screens (screen_id),
    state_kind             TEXT NOT NULL,
    identity_hash          TEXT NOT NULL,
    fingerprint_version    INTEGER NOT NULL,
    identity_tokens_json   TEXT NOT NULL,
    base_element_keys_json TEXT NOT NULL,
    identity_status        TEXT NOT NULL,
    created_at             TEXT NOT NULL,
    UNIQUE (identity_hash, fingerprint_version)
);

CREATE INDEX ix_screen_states_screen ON screen_states (screen_id);

CREATE TABLE state_identity_aliases (
    identity_hash       TEXT NOT NULL,
    fingerprint_version INTEGER NOT NULL,
    state_id            TEXT NOT NULL REFERENCES screen_states (state_id),
    similarity          REAL NOT NULL,
    created_at          TEXT NOT NULL,
    PRIMARY KEY (identity_hash, fingerprint_version)
);

CREATE TABLE screen_variants (
    state_id            TEXT NOT NULL REFERENCES screen_states (state_id),
    context_id          TEXT NOT NULL REFERENCES contexts (context_id),
    element_set_hash    TEXT NOT NULL,
    added_keys_json     TEXT NOT NULL,
    removed_keys_json   TEXT NOT NULL,
    snapshot_count      INTEGER NOT NULL,
    first_seen_at       TEXT NOT NULL,
    last_seen_at        TEXT NOT NULL,
    PRIMARY KEY (state_id, context_id)
);

CREATE TABLE ui_snapshots (
    snapshot_id         TEXT NOT NULL PRIMARY KEY,
    snapshot_key        TEXT NOT NULL UNIQUE,
    context_id          TEXT NOT NULL REFERENCES contexts (context_id),
    attempt_id          TEXT,
    fidelity            TEXT NOT NULL,
    payload_hash        TEXT NOT NULL,
    raw_sha256          TEXT NOT NULL,
    normalized_sha256   TEXT NOT NULL,
    identity_hash       TEXT NOT NULL,
    layout_hash         TEXT NOT NULL,
    element_set_hash    TEXT NOT NULL,
    fingerprint_version INTEGER NOT NULL,
    state_id            TEXT REFERENCES screen_states (state_id),
    screenshot_sha256   TEXT,
    redaction_version   INTEGER NOT NULL,
    observed_at         TEXT NOT NULL
);

CREATE INDEX ix_ui_snapshots_state ON ui_snapshots (state_id, context_id);

CREATE TABLE evidence (
    evidence_id          TEXT NOT NULL PRIMARY KEY,
    kind                 TEXT NOT NULL,
    artifact_sha256      TEXT NOT NULL,
    length               INTEGER NOT NULL,
    context_id           TEXT NOT NULL REFERENCES contexts (context_id),
    snapshot_id          TEXT REFERENCES ui_snapshots (snapshot_id),
    attempt_id           TEXT,
    redaction_count      INTEGER NOT NULL,
    redaction_rules_json TEXT NOT NULL,
    created_at           TEXT NOT NULL
);

CREATE INDEX ix_evidence_context ON evidence (context_id, snapshot_id);

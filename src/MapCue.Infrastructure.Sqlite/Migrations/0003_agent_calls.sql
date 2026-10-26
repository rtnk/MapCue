-- STEP-04: reasoner calls with validation results (ADR-0004, architecture §9.3). Request and response are artifacts (blobs);
-- this table keeps their hashes, the decisions and the usage. Rejected answers are kept as evidence.
CREATE TABLE agent_calls (
    seq             INTEGER PRIMARY KEY AUTOINCREMENT,
    call_id         TEXT NOT NULL UNIQUE,
    request_id      TEXT NOT NULL,
    call_number     INTEGER NOT NULL,
    context_id      TEXT NOT NULL REFERENCES contexts (context_id),
    state_id        TEXT REFERENCES screen_states (state_id),
    task_id         TEXT,
    attempt_id      TEXT,
    reasoner        TEXT NOT NULL,
    prompt_version  TEXT NOT NULL,
    snapshot_hash   TEXT NOT NULL,
    request_sha256  TEXT NOT NULL,
    response_sha256 TEXT,
    status          TEXT NOT NULL,
    errors_json     TEXT NOT NULL,
    decisions_json  TEXT NOT NULL,
    input_tokens    INTEGER,
    output_tokens   INTEGER,
    cost_usd        TEXT,
    duration_ms     INTEGER NOT NULL,
    created_at      TEXT NOT NULL,
    UNIQUE (request_id, call_number)
);

CREATE INDEX ix_agent_calls_state ON agent_calls (state_id, seq);

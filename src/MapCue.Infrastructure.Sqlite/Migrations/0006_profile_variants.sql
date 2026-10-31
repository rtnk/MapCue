-- STEP-07: profiles and variants. A variant keeps the identity tokens of its latest distinct snapshot in its context, so profiles can be
-- compared (coverage matrix, equivalence classes, guard hypotheses) without re-reading blobs; recipes are listed per context.
ALTER TABLE screen_variants ADD COLUMN identity_tokens_json TEXT NOT NULL DEFAULT '[]';

CREATE INDEX ix_reachability_recipes_context ON reachability_recipes (context_id, target_state_id);

-- A candidate (state, action) is shared by contexts, but its risk and status are not: the profile switch policy and the profile's data
-- isolation decide them per context. One row per (state, action, context); the status only moves forward.
CREATE TABLE candidate_assessments (
    state_id    TEXT NOT NULL,
    action_key  TEXT NOT NULL,
    context_id  TEXT NOT NULL REFERENCES contexts (context_id),
    risk_class  TEXT NOT NULL,
    status      TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    PRIMARY KEY (state_id, action_key, context_id),
    FOREIGN KEY (state_id, action_key) REFERENCES action_candidates (state_id, action_key)
);

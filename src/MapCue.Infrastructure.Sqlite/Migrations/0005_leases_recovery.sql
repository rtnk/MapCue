-- STEP-06: frontier scheduler. Task leases already have columns (lease_owner, lease_expires_at, 0002); this adds the row a list row
-- task is represented by (budgets.maxListRowsPerTemplate), the run of each attempt (reconciliation after a restart, architecture §5.3)
-- and the exclusive lease of a device by one run (architecture §5.1: one UI executor per device).
ALTER TABLE frontier_tasks ADD COLUMN represented_by TEXT REFERENCES frontier_tasks (task_id);

ALTER TABLE task_attempts ADD COLUMN run_id TEXT REFERENCES runs (run_id);

CREATE INDEX ix_frontier_tasks_lease ON frontier_tasks (status, lease_owner);

CREATE TABLE device_leases (
    device_id    TEXT NOT NULL PRIMARY KEY,
    owner_run_id TEXT NOT NULL REFERENCES runs (run_id),
    acquired_at  TEXT NOT NULL,
    expires_at   TEXT NOT NULL
);

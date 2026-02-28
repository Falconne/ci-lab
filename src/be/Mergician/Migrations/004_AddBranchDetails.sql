-- Add activity detail columns to branch_in_project so the background sync thread can
-- persist MR and approval state alongside the branch record. These columns are nullable
-- because data is fetched asynchronously after a branch is first discovered.
ALTER TABLE branch_in_project
    ADD COLUMN has_merge_request  BOOLEAN,
    ADD COLUMN merge_request_title TEXT,
    ADD COLUMN merge_request_url   TEXT,
    ADD COLUMN project_url         TEXT,
    ADD COLUMN approvals_required  INTEGER,
    ADD COLUMN approvals_given     INTEGER;

-- Separate table for build jobs so that multiple jobs per branch can be stored and
-- replaced atomically each time the background thread refreshes a branch.
CREATE TABLE branch_build_jobs (
    id                   SERIAL PRIMARY KEY,
    branch_in_project_id INTEGER NOT NULL REFERENCES branch_in_project(id) ON DELETE CASCADE,
    name                 TEXT    NOT NULL,
    status               TEXT    NOT NULL,
    url                  TEXT,
    CONSTRAINT uq_branch_build_job UNIQUE (branch_in_project_id, name)
);

CREATE INDEX ix_branch_build_jobs_branch_id ON branch_build_jobs (branch_in_project_id);

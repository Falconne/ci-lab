CREATE TABLE branch_in_project
(
    id                          SERIAL PRIMARY KEY,
    branch_name                 TEXT        NOT NULL,
    project_id                  INTEGER     NOT NULL,
    project_name                TEXT        NOT NULL,
    project_name_with_namespace TEXT,
    has_merge_request           BOOLEAN,
    merge_request_title         TEXT,
    merge_request_url           TEXT,
    project_url                 TEXT,
    approvals_required          INTEGER,
    approvals_given             INTEGER,
    last_update_time            TIMESTAMPTZ,
    needs_rebase                BOOLEAN,
    mr_status                   INTEGER     NOT NULL DEFAULT 0,
    mr_status_reasons           TEXT,
    last_commit_message         TEXT,
    merge_error                 TEXT,
    CONSTRAINT uq_branch_in_project UNIQUE (branch_name, project_id)
);

CREATE TABLE branch_build_jobs
(
    id                   SERIAL PRIMARY KEY,
    branch_in_project_id INTEGER NOT NULL REFERENCES branch_in_project (id) ON DELETE CASCADE,
    name                 TEXT    NOT NULL,
    status               TEXT    NOT NULL,
    url                  TEXT,
    CONSTRAINT uq_branch_build_job UNIQUE (branch_in_project_id, name)
);

CREATE TABLE merge_group
(
    id                  SERIAL PRIMARY KEY,
    name                TEXT    NOT NULL,
    auto_merge          BOOLEAN NOT NULL DEFAULT FALSE,
    auto_merge_warning  TEXT,
    auto_merge_by_label BOOLEAN NOT NULL DEFAULT FALSE,
    CONSTRAINT uq_merge_group_name UNIQUE (name)
);

CREATE TABLE branches_in_merge_group
(
    id                   SERIAL PRIMARY KEY,
    merge_group_id       INTEGER NOT NULL REFERENCES merge_group (id) ON DELETE CASCADE,
    branch_in_project_id INTEGER NOT NULL REFERENCES branch_in_project (id) ON DELETE CASCADE,
    CONSTRAINT uq_branches_in_merge_group UNIQUE (merge_group_id, branch_in_project_id)
);

CREATE TABLE users_in_merge_groups
(
    id             SERIAL PRIMARY KEY,
    gitlab_user_id INTEGER NOT NULL,
    merge_group_id INTEGER NOT NULL REFERENCES merge_group (id) ON DELETE CASCADE,
    CONSTRAINT uq_users_in_merge_groups UNIQUE (gitlab_user_id, merge_group_id)
);

CREATE TABLE ignored_branches
(
    id          SERIAL PRIMARY KEY,
    user_id     INTEGER NOT NULL,
    branch_name TEXT    NOT NULL,
    CONSTRAINT uq_ignored_branches UNIQUE (user_id, branch_name)
);

CREATE TABLE merge_queue
(
    id         SERIAL PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE merge_queue_project
(
    queue_id   INTEGER NOT NULL REFERENCES merge_queue (id) ON DELETE CASCADE,
    project_id INTEGER NOT NULL,
    PRIMARY KEY (queue_id, project_id)
);

CREATE TABLE merge_queue_entry
(
    id             SERIAL PRIMARY KEY,
    queue_id       INTEGER NOT NULL REFERENCES merge_queue (id) ON DELETE CASCADE,
    merge_group_id INTEGER NOT NULL REFERENCES merge_group (id) ON DELETE CASCADE,
    position       INTEGER NOT NULL,
    CONSTRAINT uq_merge_queue_entry UNIQUE (queue_id, merge_group_id)
);

CREATE TABLE monitored_project
(
    id           SERIAL PRIMARY KEY,
    project_id   INTEGER NOT NULL,
    project_name TEXT,
    CONSTRAINT uq_monitored_project_project_id UNIQUE (project_id)
);

-- Indexes for frequent lookups
CREATE INDEX ix_branch_in_project_branch_name ON branch_in_project (branch_name);
CREATE INDEX ix_branch_in_project_project_id ON branch_in_project (project_id);
CREATE INDEX ix_branch_in_project_last_update_time ON branch_in_project (last_update_time);
CREATE INDEX ix_branch_build_jobs_branch_id ON branch_build_jobs (branch_in_project_id);
CREATE INDEX ix_branches_in_merge_group_merge_group_id ON branches_in_merge_group (merge_group_id);
CREATE INDEX ix_branches_in_merge_group_branch_in_project_id ON branches_in_merge_group (branch_in_project_id);
CREATE INDEX ix_users_in_merge_groups_gitlab_user_id ON users_in_merge_groups (gitlab_user_id);
CREATE INDEX ix_users_in_merge_groups_merge_group_id ON users_in_merge_groups (merge_group_id);
CREATE INDEX ix_merge_queue_entry_queue_id ON merge_queue_entry (queue_id);
CREATE INDEX ix_merge_queue_entry_merge_group_id ON merge_queue_entry (merge_group_id);
CREATE INDEX ix_merge_queue_project_queue_id ON merge_queue_project (queue_id);

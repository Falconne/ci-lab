CREATE TABLE user_activity (
    id SERIAL PRIMARY KEY,
    gitlab_user_id INTEGER NOT NULL,
    last_poll_timestamp TIMESTAMPTZ NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
    CONSTRAINT uq_user_activity_gitlab_user_id UNIQUE (gitlab_user_id)
);

CREATE TABLE branch_in_project (
    id SERIAL PRIMARY KEY,
    branch_name TEXT NOT NULL,
    project_id INTEGER NOT NULL,
    project_name TEXT NOT NULL,
    CONSTRAINT uq_branch_in_project UNIQUE (branch_name, project_id)
);

CREATE TABLE merge_group (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    last_update_time TIMESTAMPTZ NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC'),
    CONSTRAINT uq_merge_group_name UNIQUE (name)
);

CREATE TABLE branches_in_merge_group (
    id SERIAL PRIMARY KEY,
    merge_group_id INTEGER NOT NULL REFERENCES merge_group(id) ON DELETE CASCADE,
    branch_in_project_id INTEGER NOT NULL REFERENCES branch_in_project(id) ON DELETE CASCADE,
    CONSTRAINT uq_branches_in_merge_group UNIQUE (merge_group_id, branch_in_project_id)
);

CREATE TABLE users_in_merge_groups (
    id SERIAL PRIMARY KEY,
    gitlab_user_id INTEGER NOT NULL,
    merge_group_id INTEGER NOT NULL REFERENCES merge_group(id) ON DELETE CASCADE,
    CONSTRAINT uq_users_in_merge_groups UNIQUE (gitlab_user_id, merge_group_id)
);

-- Indexes for frequent lookups
CREATE INDEX ix_user_activity_gitlab_user_id ON user_activity (gitlab_user_id);
CREATE INDEX ix_branch_in_project_branch_name ON branch_in_project (branch_name);
CREATE INDEX ix_branch_in_project_project_id ON branch_in_project (project_id);
CREATE INDEX ix_branches_in_merge_group_merge_group_id ON branches_in_merge_group (merge_group_id);
CREATE INDEX ix_branches_in_merge_group_branch_in_project_id ON branches_in_merge_group (branch_in_project_id);
CREATE INDEX ix_users_in_merge_groups_gitlab_user_id ON users_in_merge_groups (gitlab_user_id);
CREATE INDEX ix_users_in_merge_groups_merge_group_id ON users_in_merge_groups (merge_group_id);
CREATE INDEX ix_merge_group_last_update_time ON merge_group (last_update_time);

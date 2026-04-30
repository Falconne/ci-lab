CREATE TABLE untracked_branches (
    id SERIAL PRIMARY KEY,
    user_id INTEGER NOT NULL,
    branch_name TEXT NOT NULL,
    CONSTRAINT uq_untracked_branches UNIQUE (user_id, branch_name)
);

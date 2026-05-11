CREATE TABLE merge_queue
(
    id         SERIAL PRIMARY KEY,
    created_at TIMESTAMPTZ NOT NULL DEFAULT (NOW() AT TIME ZONE 'UTC')
);

-- The set of GitLab project IDs that "key" a queue.
-- Any MG whose branches touch any of these projects belongs to this queue.
CREATE TABLE merge_queue_project
(
    queue_id   INTEGER NOT NULL REFERENCES merge_queue (id) ON DELETE CASCADE,
    project_id INTEGER NOT NULL,
    PRIMARY KEY (queue_id, project_id)
);

-- Ordered list of merge groups waiting in a queue.
-- position is 1-based; position 1 is next to be rebased/merged.
-- ON DELETE CASCADE ensures entries are cleaned up when a merge group is removed.
CREATE TABLE merge_queue_entry
(
    id             SERIAL PRIMARY KEY,
    queue_id       INTEGER NOT NULL REFERENCES merge_queue (id) ON DELETE CASCADE,
    merge_group_id INTEGER NOT NULL REFERENCES merge_group (id) ON DELETE CASCADE,
    position       INTEGER NOT NULL,
    CONSTRAINT uq_merge_queue_entry UNIQUE (queue_id, merge_group_id)
);

CREATE INDEX ix_merge_queue_entry_queue_id ON merge_queue_entry (queue_id);
CREATE INDEX ix_merge_queue_entry_merge_group_id ON merge_queue_entry (merge_group_id);
CREATE INDEX ix_merge_queue_project_queue_id ON merge_queue_project (queue_id);

-- Move last_update_time tracking from merge_group to branch_in_project.
-- Each branch row now stores the UTC timestamp of the most recent push event.
-- The merge group's effective last update time is the maximum across its branches.

ALTER TABLE branch_in_project
    ADD COLUMN last_update_time TIMESTAMPTZ;

DROP INDEX ix_merge_group_last_update_time;
ALTER TABLE merge_group DROP COLUMN last_update_time;

CREATE INDEX ix_branch_in_project_last_update_time ON branch_in_project (last_update_time);

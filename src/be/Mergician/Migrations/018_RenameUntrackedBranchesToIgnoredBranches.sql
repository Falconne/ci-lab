ALTER TABLE untracked_branches RENAME TO ignored_branches;
ALTER TABLE ignored_branches RENAME CONSTRAINT uq_untracked_branches TO uq_ignored_branches;

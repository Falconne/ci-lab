ALTER TABLE branch_in_project
    ADD COLUMN mr_status         INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN mr_status_reasons TEXT;

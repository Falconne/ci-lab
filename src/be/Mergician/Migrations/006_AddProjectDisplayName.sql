-- Add a column to store the short project display name separately from the
-- full name_with_namespace. Existing rows are backfilled by extracting the
-- last segment after the '/' separator.

ALTER TABLE branch_in_project
    ADD COLUMN project_display_name TEXT;

UPDATE branch_in_project
SET project_display_name = TRIM(
    CASE
        WHEN project_name LIKE '%/%'
            THEN SUBSTRING(project_name FROM '([^/]+)\s*$')
        ELSE project_name
    END
);

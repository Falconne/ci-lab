-- Rename project_display_name to project_name_with_namespace to accurately reflect
-- that this column stores the GitLab name_with_namespace value.

ALTER TABLE branch_in_project
    RENAME COLUMN project_display_name TO project_name_with_namespace;

# Database Schema

This document describes the Mergician database schema using an entity-relationship diagram.

```mermaid
erDiagram
    branch_in_project {
        int id PK
        text branch_name
        int project_id
        text project_name
        text project_name_with_namespace
        bool has_merge_request
        text merge_request_title
        text merge_request_url
        text project_url
        int approvals_required
        int approvals_given
        timestamptz last_update_time
        bool needs_rebase
        int mr_status
        text mr_status_reasons
        text last_commit_message
    }

    merge_group {
        int id PK
        text name
        timestamptz last_update_time
        bool auto_merge
        bool auto_rebase
        text auto_merge_warning
    }

    branches_in_merge_group {
        int id PK
        int merge_group_id FK
        int branch_in_project_id FK
    }

    users_in_merge_groups {
        int id PK
        int gitlab_user_id
        int merge_group_id FK
    }

    branch_build_jobs {
        int id PK
        int branch_in_project_id FK
        text name
        text status
        text url
    }

    merge_group ||--o{ branches_in_merge_group : "has"
    branch_in_project ||--o{ branches_in_merge_group : "belongs to"
    merge_group ||--o{ users_in_merge_groups : "has member"
    branch_in_project ||--o{ branch_build_jobs : "has"
```

## Table Descriptions

| Table | Description |
|---|---|
| `branch_in_project` | A tracked branch within a specific GitLab project, including its current MR status, approval state, and build details. |
| `merge_group` | A named group of related branches across projects that should be merged together. |
| `branches_in_merge_group` | Join table linking branches to merge groups. |
| `users_in_merge_groups` | Tracks which GitLab users are subscribed to which merge groups, used to scope activity polling per user. |
| `branch_build_jobs` | CI/CD pipeline job results for a branch (one row per job on the latest pipeline). |

## Key Constraints

- `branch_in_project`: unique on `(branch_name, project_id)`
- `merge_group`: unique on `name`
- `branches_in_merge_group`: unique on `(merge_group_id, branch_in_project_id)`
- `users_in_merge_groups`: unique on `(gitlab_user_id, merge_group_id)`

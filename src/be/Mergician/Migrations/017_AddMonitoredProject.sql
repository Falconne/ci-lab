CREATE TABLE monitored_project
(
    id           SERIAL PRIMARY KEY,
    project_id   INTEGER NOT NULL,
    project_name TEXT,
    CONSTRAINT uq_monitored_project_project_id UNIQUE (project_id)
);

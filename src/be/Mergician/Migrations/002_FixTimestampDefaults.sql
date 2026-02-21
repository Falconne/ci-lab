ALTER TABLE user_activity
    ALTER COLUMN last_poll_timestamp SET DEFAULT NOW();

ALTER TABLE merge_group
    ALTER COLUMN last_update_time SET DEFAULT NOW();

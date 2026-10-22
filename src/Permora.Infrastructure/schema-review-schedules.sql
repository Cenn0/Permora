CREATE TABLE IF NOT EXISTS review_schedules (
    application_id uuid PRIMARY KEY REFERENCES applications(id),
    enabled boolean NOT NULL,
    next_start_at timestamptz NOT NULL,
    revision bigint NOT NULL CHECK (revision > 0),
    document jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS review_schedules_due
    ON review_schedules(next_start_at, application_id) WHERE enabled;

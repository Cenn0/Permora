CREATE TABLE IF NOT EXISTS teams (
    id uuid PRIMARY KEY, revision bigint NOT NULL, document jsonb NOT NULL
);
CREATE TABLE IF NOT EXISTS applications (
    id uuid PRIMARY KEY, team_id uuid NOT NULL REFERENCES teams(id), realm_key text NOT NULL,
    slug text NOT NULL, environment text NOT NULL, revision bigint NOT NULL, document jsonb NOT NULL,
    UNIQUE (realm_key, slug, environment)
);
CREATE INDEX IF NOT EXISTS applications_team ON applications(team_id);
CREATE TABLE IF NOT EXISTS operations (
    id uuid PRIMARY KEY, application_id uuid NOT NULL REFERENCES applications(id),
    actor_key text NOT NULL, idempotency_key text NOT NULL, status text NOT NULL,
    created_at timestamptz NOT NULL, next_at timestamptz NOT NULL, document jsonb NOT NULL,
    UNIQUE (application_id, actor_key, idempotency_key)
);
CREATE INDEX IF NOT EXISTS operations_queue ON operations(next_at, application_id) WHERE status = 'Queued';
CREATE TABLE IF NOT EXISTS directory (
    id uuid PRIMARY KEY, realm_key text NOT NULL, name text NOT NULL, document jsonb NOT NULL
);
CREATE INDEX IF NOT EXISTS directory_realm ON directory(realm_key);
CREATE TABLE IF NOT EXISTS access_reviews (
    id uuid PRIMARY KEY, application_id uuid NOT NULL REFERENCES applications(id),
    closed boolean NOT NULL, due_at timestamptz NOT NULL, document jsonb NOT NULL
);
CREATE TABLE IF NOT EXISTS audit_events (
    id uuid PRIMARY KEY, application_id uuid NOT NULL REFERENCES applications(id),
    sequence bigint NOT NULL, document jsonb NOT NULL, UNIQUE(application_id, sequence)
);
CREATE TABLE IF NOT EXISTS admin_audit (
    id uuid PRIMARY KEY, at timestamptz NOT NULL DEFAULT now(), detail jsonb NOT NULL
);
CREATE TABLE IF NOT EXISTS sessions (
    id text PRIMARY KEY, ticket bytea NOT NULL, expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS sessions_expiry ON sessions(expires_at);
CREATE OR REPLACE FUNCTION reject_audit_mutation() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Audit records are append-only';
END; $$;
DROP TRIGGER IF EXISTS audit_append_only ON audit_events;
CREATE TRIGGER audit_append_only BEFORE UPDATE OR DELETE ON audit_events
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();
DROP TRIGGER IF EXISTS admin_audit_append_only ON admin_audit;
CREATE TRIGGER admin_audit_append_only BEFORE UPDATE OR DELETE ON admin_audit
FOR EACH ROW EXECUTE FUNCTION reject_audit_mutation();

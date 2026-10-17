#!/usr/bin/env bash
# CI-only PostgreSQL backup/restore smoke. NOT a multi-service DR certification.
set -euo pipefail
: "$PERMORA_CI_PGHOST" "$PERMORA_CI_PGPORT" "$PERMORA_CI_PGUSER" "$PERMORA_CI_PGPASSWORD" "$PERMORA_CI_PGDATABASE"
case "$PERMORA_CI_PGHOST" in 127.0.0.1|localhost) ;; *) echo "Only loopback allowed" >&2; exit 2 ;; esac
case "$PERMORA_CI_PGDATABASE" in permora_test_*) ;; *) echo "Only permora_test_* allowed" >&2; exit 2 ;; esac
[[ "$PERMORA_CI_PGPORT" =~ ^[0-9]{2,5}$ ]] || { echo "Invalid port" >&2; exit 2; }
[[ "$CI" == "true" ]] || { echo "Test runner only" >&2; exit 2; }
command -v docker >/dev/null
backup="$(mktemp -t permora-ci-backup.XXXXXXXX)"
restore="permora_test_restore_$RANDOM$RANDOM"
client() {
 docker run --rm -i --network host \
 -e PGHOST="$PERMORA_CI_PGHOST" -e PGPORT="$PERMORA_CI_PGPORT" \
 -e PGUSER="$PERMORA_CI_PGUSER" -e PGPASSWORD="$PERMORA_CI_PGPASSWORD" \
 -e PGCONNECT_TIMEOUT=5 postgres:18 "$@"
}
cleanup() {
 client dropdb --if-exists "$restore" >/dev/null 2>&1 || true
 rm -f "$backup"
}
trap cleanup EXIT
sql="SELECT
 (SELECT count(*)::text FROM teams) || ':' ||
 (SELECT count(*)::text FROM applications) || ':' ||
 (SELECT count(*)::text FROM operations) || ':' ||
 (SELECT count(*)::text FROM directory) || ':' ||
 (SELECT count(*)::text FROM audit_events) || ':' ||
 (SELECT count(*)::text FROM schema_versions) || ':' ||
 coalesce((SELECT md5(string_agg(md5(document::text), '' ORDER BY id)) FROM applications), '')"
before="$(client psql -X -A -t -v ON_ERROR_STOP=1 -d "$PERMORA_CI_PGDATABASE" -c "$sql")"
count="$(client psql -X -A -t -v ON_ERROR_STOP=1 -d "$PERMORA_CI_PGDATABASE" -c 'SELECT count(*) FROM applications')"
[[ -n "$before" && "$count" =~ ^[0-9]+$ && "$count" -gt 0 ]] || { echo "CI application state missing" >&2; exit 1; }
client pg_dump --format=custom --no-owner --no-acl -d "$PERMORA_CI_PGDATABASE" > "$backup"
test -s "$backup"
client createdb -T template0 "$restore"
cat "$backup" | client pg_restore --exit-on-error --no-owner --no-acl -d "$restore"
after="$(client psql -X -A -t -v ON_ERROR_STOP=1 -d "$restore" -c "$sql")"
[[ "$before" == "$after" ]] || { echo "Restored persistent state checksum differs" >&2; exit 1; }
triggers="$(client psql -X -A -t -v ON_ERROR_STOP=1 -d "$restore" \
 -c "SELECT count(*) FROM pg_trigger WHERE tgname IN ('audit_append_only','admin_audit_append_only') AND NOT tgisinternal")"
[[ "$triggers" == "2" ]] || { echo "Audit triggers missing after restore" >&2; exit 1; }
echo "PASS: PostgreSQL CI backup restored independently with matching persistent-state fingerprint and audit triggers"
echo "NOT PROVED: Keycloak reconciliation, secrets/key restore, RPO/RTO, PITR or production HA"

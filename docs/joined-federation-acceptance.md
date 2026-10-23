# Joined LDAP lifecycle and emergency revocation acceptance

This is the executable engineering contract for issues
[#58](https://github.com/Cenn0/Permora/issues/58) and the automated portion of
[#46](https://github.com/Cenn0/Permora/issues/46). A successful exact-source
[system acceptance run](https://github.com/Cenn0/Permora/actions/workflows/system-acceptance.yml)
is required as execution evidence; the existence of this document is not a pass.

## Actual processes and scope

[`ci-federation.py`](../scripts/ci-federation.py) starts its own TLS-validated
OpenLDAP and Keycloak **26.8.1**. For **each** imported/non-imported READ_ONLY
realm, [`ci_federation_worker.py`](../scripts/ci_federation_worker.py) starts
disposable PostgreSQL **18**, the built Release **Permora.Worker** and the
built **Permora.Api**. The workflow builds with the SDK in `global.json`.
No actual Microsoft AD, corporate accounts or productive credentials are used.

The API uses explicitly enabled, loopback-only Development authentication
and real CSRF cookies. It shares the actual database and has **no Keycloak
Admin REST credentials**. This tests HTTP admission, owner retry and worker
execution together, not OIDC/MFA; the separate genuine Keycloak BFF suite
remains responsible for authentication assurance.

Historical mappings are seeded by the isolated bootstrap administrator.
API and worker both keep **AllowGroupRoleGrants=false**, with no approved
group IDs. The worker uses the preflighted FGAP credential; the separate
directory identity has realm-wide `view-users` assigned and role-scoped.
Both are explicit test concessions, not a least-privilege production approval.
The worker configures a known federated `DirectoryProbeUserIds` entry. Before
accepting a refresh it requires a live point read and unique search presence
of that enabled normal user. This detects loss of the configured visibility
sentinel even when federated search returns a nominally successful filtered
list. It is an operator-configured guard, not proof of complete federation
visibility; see [configuration and limitations](keycloak.md#explizite-directory-sichtbarkeitsproben-worker).

## Required observations

| Scenario | Required state and independent readback |
| --- | --- |
| Complete baseline | Worker persists actual LDAP/Keycloak membership; no conflict and no queued write |
| Disabled new group grant | Actual API returns `403 group_mapping_disabled`; no queue entry or remote grant |
| LDAP visibility loss | Fixture-owned slapd is suspended; actual LDAPS times out; worker reports failed synchronization, preserves the complete persisted snapshot and creates no speculative revoke |
| Duplicated remote user page | Loopback fault boundary duplicates the first returned user; replacement rejects the invalid snapshot rather than overwriting the last complete view |
| Ambiguous persisted principal | Fixture injects two directory documents with the same local identity and different remote IDs, under distinct physical DB row IDs; an expired group grant becomes `Conflict/ambiguous_directory_identity`, with `change.blocked` audit and no remote DELETE |
| Explicit resolution/retry | Remove the corrupt fixture row; actual owner HTTP retry queues the same obligation and records `change.retry_requested` |
| Keycloak unavailable | Fault boundary returns real HTTP 503 to the worker; the emergency revoke remains queued with `connector_unavailable` and `change.retry` audit |
| Remote commit, lost reply, crash | Proxy forwards DELETE to real Keycloak, waits for 204, then withholds it; fixture kills the responsible worker before PostgreSQL success can commit; independent bootstrap read confirms remote removal while local state remains unapplied |
| Restart | Actual worker checks Keycloak postcondition and completes the existing operation; exactly one effective DELETE and no POST/regrant |
| Imported LDAP drift | External LDAP join causes user-reader/group-supervisor SoD conflict; exactly one group-supervisor revoke, surviving direct reader and unrelated observer mapping |
| Non-imported LDAP drift | Two genuine LDAP groups supply reader/supervisor; external join causes overlap, both conflicting group assignments are deterministically revoked; unrelated observer role survives; no unsupported individual-role write is attempted by the worker |
| Membership loss/rejoin and final restart | Revoked supervisor rights remain absent, unrelated observer mapping survives, no additional operation or remote grant |

Total applied revokes are **2 imported** (one emergency plus one SoD) and
**3 non-imported** (one emergency plus two SoD). The bootstrap contract
separately requires rejection of unsupported READ_ONLY non-imported direct
user-role writes. A two-group conflict deliberately revokes both implicated
assignments, rather than arbitrarily preserving one.

The fault proxy binds only to loopback, forwards only this disposable realm,
refuses redirects, preserves bounded Content-Length **and chunked JSON bodies**,
bounds request/response bodies and never logs tokens or
headers. It cannot fabricate successful writes. Its own tests use synthetic
HTTP servers, without Docker or IAM credentials. SIGCONT, process termination,
proxy shutdown and database/container cleanup run on the failure path too.
There are no uploaded credential files, raw logs or database backups.
The older multiworker proxy uses the same bounded body decoder. This is
necessary for faithful tests: dropping a chunked role-list DELETE body
would make Keycloak remove all client-role mappings. A regression sends
chunked JSON through the proxy and requires an unrelated remote role to
survive; the live emergency-revoke fixture checks unrelated roles too.

Each successful mode prints sanitized UTC observation time, actual checkout
SHA, versions, operation counts and isolated SoD revocation latency. On PRs
the checkout may be GitHub's synthetic merge commit; correlate it with the
workflow's PR head SHA. The latency is not a p95/p99 or approved SLO.

## Remaining release obligations

These tests establish behavior in a bounded single-runner fixture. They do
not narrow Keycloak `Groups.manage`, deploy an egress gateway, rotate real
operator secrets or provide independent approval. **#46 remains open** until
a narrower supported entitlement or explicit deployed central-IAM risk
acceptance and verified operational controls exist. Representative AD,
multi-host TLS/HA, coordinated DR/RPO/RTO, privacy/legal/license decisions,
manual accessibility and independent penetration testing remain in **#3**.

Technical functionality, official support, best practice and security
recommendation are separate claims. This fixture is a Permora regression
contract, not Keycloak/LDAP vendor certification or permission to enable
new group grants.

# Privacy inventory, directory retention and release acceptance

**Status: technical safeguards and a review template, not a GDPR compliance
certificate or an authorization to erase audit records.** Permora does not
determine an organization's lawful basis, retention schedule, data-controller
role, DPA or data-subject rights decisions.

## Data and trust boundaries

| Location | Personal-data categories | Current technical boundary | Required review |
| --- | --- | --- | --- |
| Keycloak | User IDs, credentials, groups, role grants, login history | External authoritative effective IAM state | Controller/processor responsibility, backup retention |
| Portal `directory` | Name, issuer/subject hash, Keycloak ID, membership and sync timestamp | Internal realm-scoped directory snapshot | Retention and complete federation visibility |
| `teams` / `applications` | Owner hashes, names, application grants and expiration | Scoped API and centrally managed mandates | Active rights and retention |
| `operations` / `access_reviews` | Requester/approver, reasons, targets and times | Audited request and revocation lifecycle | Restricted access and retention |
| `audit_events` / `admin_audit` | Actor identifiers, actions, hash-chained events | Database append-only, **not WORM** | Statutory retention, external protected export |
| `sessions` / key ring | Encrypted ticket, session identifier, protection keys | HttpOnly cookie, protected store, expiration | Key backups, logout and incident response |
| Logs | Error categories, correlation IDs, potential network metadata | Connector excludes upstream payloads in public errors | Redaction, access controls and SIEM policy |

A SHA-256 subject identifier is potentially **personal data** wherever it
can be mapped back to a person; it is not anonymous by default.

## Optional directory cleanup

Enable **only after an explicit organizational privacy-policy decision**
in the Worker-specific configuration:

```json
{
  "Privacy": {
    "UnreferencedDirectoryRetentionDays": 180
  }
}
```

The option is **off by default**. A value between 30 and 3650 days is
accepted. These are software safeguards, not legislated GDPR retention
durations.

After an explicitly successful realm directory refresh, the worker may
remove up to **250 old entries per synchronization**. A shared PostgreSQL
per-realm advisory lock prevents concurrent refresh and pruning.
Candidates must be older than the retention cutoff and must not have:

- A current direct application assignment.
- An unresolved access operation referencing requester, approver or target.
- A team-owner/administrator mandate.
- Effective membership in a group with an active assigned role.

Incompleteness of an LDAP/AD/FGAP listing is **not** proof of deletion.
This process never deletes upstream Keycloak users, pending revocations,
effective grants, immutable audit events or active sessions. Logs contain
aggregate cleanup counts only.

**Test coverage:** isolated PostgreSQL integration tests for stale unreferenced
principals, active and inherited grants, team owners, pending approvals,
recent records, cross-realm boundaries, repeat cleanup and invalid policy
settings. This does not prove external directory completeness or legal approval.

## Data-subject rights and erasure (human-governed process)

1. Verify requester identity and controller responsibility outside Permora.
   Do not accept a submitted hash as identity proof.
2. Resolve issuer and subject and enumerate Keycloak, directory, team grants,
   operations, audit events, backups and third-party logs.
3. Decide and document applicable retention and exceptions. Do not break an
   append-only audit hash chain to create a superficial erasure result.
4. Revoke or transfer live IAM privileges first and **verify Keycloak**;
   keep necessary identity evidence while revocation is pending.
5. Perform separately approved export/deletion in each authoritative system
   and apply retention rules to backups and SIEM. Record authorized evidence.
6. Confirm later synchronization cannot unlawfully reintroduce deleted data.

**Still open:** self-service subject export, automated audit archive/redaction,
upstream Keycloak deletion, a controller-specific legal retention register,
privacy notices, DPIA decisions and independent privacy sign-off.

## Production acceptance

Review data flow, legitimate access, group member visibility, API IDOR,
audit/log contents, backup scope, expiry and the legal basis for each
retention purpose. Document authorized reviewers and the deployment-specific
data protection impact assessment where applicable. A green CI job is not
a legal certification.

Related: [security](security.md), [operations](operations.md),
[release gates](release-checklist.md).
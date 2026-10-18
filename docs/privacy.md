# Privacy and data-retention acceptance — Permora

**Engineering controls and decisions to obtain; NOT a legal or GDPR certification.**

Permora processes account identifiers and access-management history. A SHA-256
subject key, operation identifier or hashed review explanation can still be
**pseudonymous personal data** when the organization can associate it with a
person. Treat all of these as personal data until assessed otherwise.

## Data inventory and trust boundaries

| Store | Data and purpose | Current safeguards | Required external decision |
| --- | --- | --- | --- |
| `directory` | Keycloak IDs, subject-key hashes, names, enabled/disabled state and effective group members; grant eligibility and SoD | Realm-scoped queries, stale-snapshot fail-closed controls, authenticated API | Owner, lawful basis, shortest adequate snapshot retention and upstream identity lifecycle |
| `teams` / `applications` | Team member IDs/names, management mandates, active grants and approvers | Scope checks and revision locks | Who owns each dataset, access review and offboarding procedure |
| `operations` / `access_reviews` | Requests, justifications, names, review outcomes and grant history | Approval separation, controlled API, PostgreSQL | Detailed operational retention, selective deletion/anonymization after expiry with integrity implications |
| `audit_events` / `admin_audit` | Pseudonymous actor references, timestamps and evidentiary metadata | Append-only triggers, hash chain for application audit, scope restrictions | Retention exception analysis, independent protected archive, access approval and legally defensible erasure/restriction handling |
| `sessions` / Data Protection keys | Encrypted server-side tickets and cryptographic session material | Session expiry, server-side logout, encrypted key ring in Production | Session retention, encryption-key custody, backup retention and incident response |

The **untrusted browser must never receive Keycloak administrative client secrets or
refresh tokens**. Logs and CI evidence must not contain user credentials or real
corporate account data. Test fixtures use disposable, isolated identities only.

## Implemented minimization change

Application audit events for `change.requested`, `change.approved`,
`change.rejected`, and central `iam.approved` no longer embed raw,
free-form explanations or the IAM evidence reference. The audit entry keeps
stable operation / action identifiers and deterministic SHA-256 digests to help
correlate a separately retained business record without persisting its entire
text into the **append-only audit chain**. Regression tests inject synthetic
email addresses and prove those literals are absent from resulting audit
entries while the chain still verifies.

Additional application events also use identifiers and SHA-256 fingerprints
rather than unfiltered business strings: `application.registered` records the
application UUID and hash of its slug; `binding.approved` records the hash of
the Keycloak client ID; and `policy.updated` records the revision and hash of
the complete policy rather than exposing separation-rule explanations.
This removes raw information from **new** immutable entries only. Existing
historical audit records are unchanged; their lawful retention and any
migration/archive decisions remain a separate controller responsibility.

These digests remain pseudonymous; common explanations may be guessed.
**This is data minimization, not anonymization, an erasure feature, a WORM system
or legal approval.** Free-form request reasons remain in the operation record
and IAM evidence references remain in application state. Those records require
their own approved retention and rights-handling mechanism.

## Subject requests and retention: release-blocking operator workflow

Before handling real staff data in production, the operating organization must:

1. Define controllers/processors, recorded purposes and lawful bases, a documented
   access/retention matrix, and responsible owners for every store.
2. Identify all related records in **Permora, Keycloak, federated LDAP/AD, SIEM,
   exports and backups** using verified subject-key and Keycloak-ID correlation
   restricted to authorized privacy administrators; names/email are not safe
   identity join keys. Do not expose the mapping through a team-facing API.
3. Provide an authorized data-access/export procedure with a verifiable subject
   match and redaction of other users' personal data. Audit the disclosure.
4. Define technically implemented erasure/restriction and archival workflows
   separately for live tables, append-only audit, log sinks and backups. Never
   delete pending revocations or unresolved operations merely to satisfy a
   retention timer; first resolve IAM consistency and legal duties.
5. Determine whether any records must be preserved for legal claims or security,
   for how long, and who approved exceptions. Hash chains and write-only
   triggers cannot themselves resolve this conflict.
6. Perform an approved restore/retention drill confirming that an old backup
   does not reintroduce unapproved grants or silently restore removed data.
7. Complete a documented risk assessment and, where required, DPIA, security
   review and independent legal/privacy sign-off before any 1.0 release.

No global `DELETE FROM audit_events` or unaudited mass deletion is provided.
Its hash chain would be broken and its purpose undermined; external privacy
counsel must define a lawful retention and irreversible disposal policy first.

## Technical regression scope

- Unit/integration tests: no raw operation or IAM-review reason in immutable
  audit details; audit chain integrity remains intact.
- Negative HTTP authorization: ordinary team members must not retrieve foreign
  people's data or realm-level telemetry.
- Live-system checks still outstanding: a complete subject export/erasure
  exercise, backups and external SIEM retention, real LDAP/AD identities,
  privileged administrator review and documented DPIA decision.

See [Security](security.md), [Release gates](release-checklist.md),
[Operations](operations.md), and [Threat model](threat-model.md).

**Primary legal basis for the organization's review:** Regulation (EU)
2016/679, particularly Articles 5, 15–18, 30, 32 and (if applicable) 35.
Compliance depends on the concrete deployment and organizational purposes,
not on passing this repository's tests.

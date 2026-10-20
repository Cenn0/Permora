# Permora 1.0 — verifiable engineering and production release gates

## 20 October 2026 — joined LDAP/worker integration and release boundary

[PR #60](https://github.com/Cenn0/Permora/pull/60) merged at
[`e3bfcf6ca6b2`](https://github.com/Cenn0/Permora/commit/e3bfcf6ca6b234d4f2082aaa633d95e95cfffbc4).
All **seven exact-PR-SHA workflows** passed on `dacae649479d`,
including [real system acceptance](https://github.com/Cenn0/Permora/actions/runs/39432701299),
[worker recovery](https://github.com/Cenn0/Permora/actions/runs/39432701343),
[bounded multiworker load](https://github.com/Cenn0/Permora/actions/runs/39432701364),
[core CI](https://github.com/Cenn0/Permora/actions/runs/39432701392)
and [candidate vulnerability/SBOM integrity](https://github.com/Cenn0/Permora/actions/runs/39432701640).

This closes an **engineering evidence gap**, not release gate R07:
the actual Release worker first persists a real imported-LDAPS
Keycloak directory snapshot, then handles an upstream LDAP membership
change that creates conflicting effective access. It queues and applies
one targeted group revocation, preserves the unrelated direct user
mapping, writes immutable audit evidence, and verifies remote Keycloak
postconditions after recovery of group membership. An isolated earlier
iteration measured 2.29 seconds from worker restart to verified revoke;
this is **not** a production p95/p99 or approved SLO.

Service-account `realm-management:view-users` must be both assigned to
the directory principal **and** in the client role-scope mappings when
`fullScopeAllowed=false`; this is a realm-wide read permission requiring
separate central IAM approval, not an application-only minimal entitlement.
The joined fixture intentionally reuses the known scoped group-mapping
principal for two roles; real deployments still need independently reviewed
separate credentials. See [Keycloak configuration](keycloak.md) and
[exact acceptance issue #58](https://github.com/Cenn0/Permora/issues/58).

**Still blocked:** joined non-imported LDAP worker and injected LDAP
visibility-loss cases, actual Microsoft Active Directory, FGAP
`Groups.manage` privilege acceptance (#46), operator TLS/HA/rotation,
coordinated DR/RPO/RTO, independent penetration/WCAG/GDPR/DPIA,
WORM/SIEM evidence, and accountable license/vulnerability signoffs.
[Issue #3](https://github.com/Cenn0/Permora/issues/3) remains open.
A signed draft candidate never means completed 1.0 approval.


## New verified engineering evidence — 20 October 2026

The merged [PR #57](https://github.com/Cenn0/Permora/pull/57) is present
on `main` commit
[`c83711c269204ada7f3dade33b68a036e373962c`](https://github.com/Cenn0/Permora/commit/c83711c269204ada7f3dade33b68a036e373962c).
All six workflows passed on the **same** merged commit:
[core xUnit/build/BFF](https://github.com/Cenn0/Permora/actions/runs/39416605015)
(221 xUnit passed, none skipped),
[real LDAPS federation](https://github.com/Cenn0/Permora/actions/runs/39416604931),
[two-process worker/recovery](https://github.com/Cenn0/Permora/actions/runs/39416605037),
[automated accessibility](https://github.com/Cenn0/Permora/actions/runs/39416604905),
[artifact integrity](https://github.com/Cenn0/Permora/actions/runs/39416604980)
and [documentation](https://github.com/Cenn0/Permora/actions/runs/39416605443).
Artifact signing creates an **unpublished draft candidate**, not a production
release or legal/security approval.

**R07/P4 improvement, still PARTIAL:** The genuine OpenLDAP/LDAPS/Keycloak
matrix now tests inherited effective client-role removal and restoration after
an external LDAP member switch (import enabled and disabled). Domain and
workflow regressions fail closed on stale/ambiguous directory evidence for
automatic SoD revocations and access-review retention. These independent
tests **do not** demonstrate that the running Permora worker observes and
reconciles that same upstream change end to end. Its dedicated
[acceptance issue #58](https://github.com/Cenn0/Permora/issues/58) remains
open. Representative Microsoft Active Directory, FGAP `Groups.manage`
blast-radius acceptance, real ingress TLS/secret rotation/multi-host HA,
coordinated disaster recovery, independent accessibility/security/privacy
and accountable vulnerability/license decisions remain outstanding
[release gates #3](https://github.com/Cenn0/Permora/issues/3) and
[security blocker #46](https://github.com/Cenn0/Permora/issues/46).

The historical 18 October baseline and `5017624d` checkpoint below are
retained solely for traceability, **not** as the current branch head.


**Assessment date:** 18 October 2026. **Verified baseline commit:**
`c0a5e0eed07006c20373ea08ff9b162ef831da52`.

**Latest CI checkpoint (18 October 2026):** `main` at
`2499f863521b1a3b42d2768a93849ed001e2941f` passed six completed
workflows: [core CI](https://github.com/Cenn0/Permora/actions/runs/39120692954),
[system](https://github.com/Cenn0/Permora/actions/runs/39120692850),
[worker/recovery](https://github.com/Cenn0/Permora/actions/runs/39120692981),
[accessibility](https://github.com/Cenn0/Permora/actions/runs/39120692887),
[artifact integrity](https://github.com/Cenn0/Permora/actions/runs/39120693032)
and [documentation](https://github.com/Cenn0/Permora/actions/runs/39120692930).
The earlier `c0a5e0ee` evidence table below is retained as a scoped
release-artifact baseline, **not** the latest source HEAD. The new
[18 October audit](evidence/roadmap-audit-2026-10-18.md) separates tested
engineering controls from missing external/operator acceptance. The unresolved
Groups.manage blast radius is documented in
[issue #46](https://github.com/Cenn0/Permora/issues/46).

## Updated verified source snapshot (20 October 2026)

`main` at
[`5017624d57aecb65758553a8d017a02d6636be4c`](https://github.com/Cenn0/Permora/commit/5017624d57aecb65758553a8d017a02d6636be4c)
passed six [exact-SHA
workflows](https://github.com/Cenn0/Permora/actions/runs/39398135314),
including the Keycloak/LDAP system test, worker/recovery, accessibility,
documentation and artifact integrity.
[Run #39398135494](https://github.com/Cenn0/Permora/actions/runs/39398135494)
created the signed **draft-only** tag
`candidate-5017624d57ae-run39398135494` (25 assets). It is not
a published community release or independent security validation.
Release-specific human/license/privacy decisions remain separate.

**Current P2 engineering work:** A new
[FGAP Group acceptance matrix](keycloak-fgap-acceptance.md)
adds same-delegated-token probes of foreign group edits/deletions and
membership changes, plus bootstrap-admin postcondition checks.
The new source itself requires passing live Keycloak CI at the
**new** commit before it becomes execution evidence.
`Groups.manage`'s non-mapping powers remain a documented open risk
even if all expected negative probes pass. R03 and R06 remain partial.

A green development CI, a signed development candidate, an automated WCAG scan
and an isolated successful restore do **not** constitute production approval,
Microsoft AD certification, GDPR compliance or a penetration test.

## Verified technical evidence at the exact baseline

| Workflow | Result and run |
| --- | --- |
| .NET 10 / Angular 22 / PostgreSQL / Keycloak | [Passed — #39117753038](https://github.com/Cenn0/Permora/actions/runs/39117753038) |
| Real LDAPS / FGAP, imported and non-imported LDAP identities | [Passed — #39117753117](https://github.com/Cenn0/Permora/actions/runs/39117753117) |
| Two real worker processes, Keycloak fault injection and database recovery | [Passed — #39117753047](https://github.com/Cenn0/Permora/actions/runs/39117753047) |
| Axe-core automated accessibility matrix, desktop/mobile/320px | [Passed — #39117753107](https://github.com/Cenn0/Permora/actions/runs/39117753107) |
| Actual image inventories, license evidence and signed **draft** candidate | [Passed — #39117753278](https://github.com/Cenn0/Permora/actions/runs/39117753278) |

The draft artifact tag is
`candidate-c0a5e0eed070-run39117753278` and contains CycloneDX/SPDX
inventories of three actual images, application archives, dependency/notice
evidence, checksums and a verified Sigstore provenance bundle. It is
**not a published production release**.

## Release gate matrix

Legend: **TESTED** = scoped automated evidence; **PARTIAL** = code or tests
exist but the complete release condition has not been demonstrated;
**BLOCKED** = critical acceptance missing. Do not convert a partial
result into a completed release just because its workflow succeeds.

| Gate | Evidence and remaining obligations | Status |
| --- | --- | --- |
| R01 — builds/reproducibility | Locked .NET/npm restores, PostgreSQL tests, Keycloak and Angular builds, container packages | **TESTED** in current scope |
| R02 — functional lifecycle | Team-scoped API, approvals, roles, queues, real PostgreSQL; complete operator journeys and long-running workloads still require acceptance | **PARTIAL** |
| R03 — FGAP least privilege | Real 26.8.1 client/user/group negative matrices; full deployment-specific rights and list-visibility matrix, including the documented broader `Groups.manage` privilege (no dedicated `Groups.map-role` in 26.8.1; [upstream request #45269](https://github.com/keycloak/keycloak/issues/45269)), unverified | **PARTIAL** |
| R04/R05 — real OIDC/MFA | Real Chromium/Keycloak LoA2-TOTP login and signed ACR/auth_time in BFF confirmed in [#39011230492](https://github.com/Cenn0/Permora/actions/runs/39011230492); corporate IdP/MFA policy and production clock/session constraints unverified | **TESTED** in isolated Keycloak; corporate setup **BLOCKED** |
| R06 — cross-team boundaries | Direct HTTP IDOR and foreign-resource denial exercised; exhaustive end-to-end combination of all roles with every production connector profile not proven | **PARTIAL** |
| R07 — directory lifecycle | Real certificate-validated OpenLDAP/LDAPS with imported/non-imported users, paging, nested groups, changes and FGAP; non-imported read-only direct user mapping correctly rejected. **Microsoft AD-specific semantics were not tested.** | **PARTIAL** |
| R08 — concurrency and revocations | Two real worker processes, 32 expirations, Keycloak 503 and remote-commit response loss, process termination, recovery and readback; no approved production load envelope or measured multi-host revocation SLO | **PARTIAL** |
| R09 — UI and accessibility | Automated axe-core WCAG A/AA checks across 54 UI state/viewport combinations; manual keyboard/screenreader, real-user validation, full English/German UI and complete live business workflow missing | **PARTIAL** |
| R10 — TLS/HA/recovery | Explicit proxy trust, encrypted shared Data Protection keys, wrapping-certificate rotation tests, shared PostgreSQL sessions, isolated Keycloak/Portal restore; real TLS ingress, secret rotation, multi-host HA, PITR/RPO/RTO and combined restore not independently accepted | **PARTIAL** |
| R11 — supply chain | Scanned built OS/application images, CycloneDX/SPDX, provenance and signed draft artifacts; third-party license judgments, vulnerability triage and production promotion approval not completed | **PARTIAL** |
| R12 — privacy and security signoff | Audit minimization, retention and data-subject workflows in development; production WORM/SIEM, GDPR review/DPIA decision, independent pentest and accountable security acceptance absent | **BLOCKED** |

## Four separate assessments

- **Technically functional:** only the cases and components specifically
  exercised by the linked runs.
- **Officially supported:** documented Keycloak features are vendor APIs;
  **Permora is not an officially supported Keycloak component** and no
  Microsoft Active Directory compatibility certification is implied.
- **Operational best practice:** isolated credentials, explicit FGAP boundaries,
  encrypted shared keys, signed artifacts and fail-closed retries. A configured
  production operator must still test those controls in its actual environment.
- **Security-side recommendation:** do not authorize Permora to administer
  real production grants until every applicable negative authorization,
  TLS/network/HA, recovery, accessibility, privacy and penetration gate has an
  immutable evidence reference and an accountable independent reviewer.

## Required human and environment-specific 1.0 approvals

- Central IAM: minimal Keycloak profile permissions, full relevant directory
  visibility, application token audience and downstream enforcement, production MFA.
- Operations: multi-host deployment, actual TLS/ingress failover, secret rotation,
  disaster recovery from coordinated Keycloak + Portal + key/certificate backups,
  load/RPO/RTO and measured revocation response.
- Supply chain: review every unknown/conditional license, package advisories and
  shipped artifact content before promotion beyond the signed **draft**.
- Accessibility: full page/state WCAG assessment, keyboard and independent
  screenreader/assistive-technology sessions including real user tasks.
- Privacy and security: approved retention/erasure strategy for pseudonymous
  identities and append-only audit, separate WORM/SIEM, DPIA decision where
  applicable, independent penetration report and tracked remediation.
- Release owner: record immutable source SHA, exact CI/workflow URLs, supported
  Keycloak and platform versions, risk owners, unresolved findings, rollback plan,
  signature verification and explicit release authorization.

No completion percentage is asserted without an explicit denominator and
independently verified acceptance records.

[Engineering roadmap](roadmap.md) · [Current security model](security.md) ·
[CI boundaries](ci.md) · [Keycloak integration](keycloak.md) ·
[Operational recovery](operations.md) · [System architecture](architecture.md).

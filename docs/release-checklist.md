# Permora 1.0 — verifiable engineering and production release gates

**Assessment date:** 18 October 2026. **Verified baseline commit:**
`c0a5e0eed07006c20373ea08ff9b162ef831da52`.

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
| R03 — FGAP least privilege | Real 26.8.1 client/user/group negative matrices; full deployment-specific rights and list-visibility matrix, including overly broad group `manage`, unverified | **PARTIAL** |
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

# Security threat model — Permora

**Baseline: 17 October 2026.** Engineering working document; not a penetration test or production signoff. Reassess when changing Keycloak version, delegated scopes, authentication, lifecycle or architecture.

## Assets, trust boundaries and data authority

| Asset | Trust owner | Principal concern |
| --- | --- | --- |
| Client and realm-level roles, user/group mappings | Central IAM and Keycloak | Cross-team escalation |
| Desired permissions, workflow decisions | Permora PostgreSQL | Unauthorized grants or incorrect revocations |
| Effective mappings and identity membership | Keycloak / potentially LDAP/AD | Incomplete visibility and out-of-band drift |
| Authenticated identity and MFA assurance | Keycloak signed ID token | Session hijack, auth downgrade, replay |
| BFF tickets and protection keys | Portal PostgreSQL and protected filesystem | Session compromise/forgery |
| Connector/Provisioner credentials | Isolated server processes | Broad service-account compromise |
| Audit evidence | PostgreSQL / future WORM | Privileged operator tampering |
| Release binaries, libraries and containers | CI and release signer | Malicious supply chain |

**Boundaries:** browser → BFF cookie and CSRF → typed application API / business policy → PostgreSQL desired state; worker → separately privileged Keycloak Admin REST; isolated client provisioner → dedicated creation profile; central IAM → identity binding/attestation. A business application's authorization endpoint validates JWTs itself. Portal grant success is *not* proof that this application enforces the permission.

## Threat matrix and existing evidence

| ID | Threat scenario | Security invariant | Status |
| --- | --- | --- | --- |
| T01 | Foreign app, operation, audit, group or review GUID is guessed | Resolve owner before validating resource payload; unknown/foreign appear as 404 | Foreign HTTP matrix PR #18, CI gate pending merge |
| T02 | Owner self-approves, reclassifies own rights or reuses old approval | Four-eyes, fresh requester and approver, current mandates, SoD, audit | Policy/workflow tests; positive IdP E2E open |
| T03 | Broad Keycloak group manage rights enable unrelated changes | Typed API, separate credentials, deny foreign targets, central IAM attestations | Representative Keycloak 26.8.1 FGAP tested; broad group-manage scope remains risk |
| T04 | Stale/password-only login attempts privilege mutation | Keycloak-signed ACR + auth_time, freshness, CSRF and server session | Password-only denial in real BFF/Chromium; positive MFA LoA open |
| T05 | Browser XSS/exfiltration/session theft | No admin/refresh token in browser; HttpOnly, CSP, HTTPS, SameSite | Code path partly tested; independent browser security review open |
| T06 | Crash between Keycloak remote commit and PostgreSQL commit | Queue revision, idempotency, readback, retry and conflict barriers | Unit tests, no full failure-injection release gate |
| T07 | Externally changed group creates new separation-of-duties breach | Poll effective group members, remediate group grants, fail closed if incomplete | Implemented polling, not instantaneous; LDAP/AD integration open |
| T08 | FGAP-filtered list or JSON null mistakenly treated as clean read | Complete visibility proof and explicit incomplete/error detection | Connector guards; full enterprise negative matrix open |
| T09 | Expiration/revocation delayed by failed worker or stale JWT | Priority to due revokes, persistent retries, alarms; independent token lifetime | Partial, no guaranteed revoke SLA |
| T10 | Historical Portal backup replay grants removed later in Keycloak | Restore with workers stopped, reconcile desired and actual before writes | Isolated PostgreSQL restore tested; full multi-system DR open |
| T11 | Public PR/dependency compromises secrets or release | Read-only CI, no production credentials, locked restores, SBOM/provenance | CI and partial lockfile SBOM; full image SCA/provenance open |
| T12 | Administrator modifies audit and data-protection keys | Separate custody, immutable external archive, retention, key rotation | Local append-only and hash chain only, no external WORM |

## Non-negotiable authorization semantics

1. Every mutating API call is authenticated, CSRF-protected and requires current privileged Keycloak assurance. Explicit development-only loopback auth does not count as MFA.
2. Every foreign app and nested resource is refused before business input validation. Neither errors nor effective state reveal another team's resources.
3. Keycloak client roles are app-scoped; no realm-management role or foreign-client role is imported into the delegated catalog.
4. An accepted HTTP 202 command has *not* yet changed effective Keycloak access. Applied requires a verified remote postcondition and committed local projection.
5. A lost Keycloak response, filtered directory result, missing identity or incomplete group snapshot is never proof of absence.
6. Privileged Keycloak credentials are isolated per process/profile. Group-level manage authority is not assumed to be an exact map-roles-only capability.
7. JWT claims may continue authorizing business APIs after upstream revocation until expiry or a separate app-specific revocation mechanism runs.
8. A stale database restore must not silently replay grants; the worker stays stopped until explicit reconciliation.

## Release security acceptance, in order

- Run positive and negative tests with two teams, two clients and independent minimally privileged service accounts against each explicitly supported Keycloak release.
- Prove **positive MFA/step-up** in a real browser and **negative** password-only, stale ACR/auth_time, CSRF, session fixation and issuer/audience cases.
- Verify actual LDAP/AD imported, non-imported, nested and disabled identities; partial pages and membership changes must be detected or handled fail-closed.
- Inject network loss before/after remote writes, multiple workers, locked DB resources, delayed revokes and backlog fairness.
- Restore Keycloak + Portal DB + Data Protection keys with reconciliation and no unapproved re-grants; measure RPO, RTO and operations SLO.
- Inspect full release SBOM including actual OS/container packages, complete licenses, advisories, signatures, data retention, immutable audit, and independent penetration testing.
- Record PR, exact commit, test URL, expected/actual negative results, Keycloak version, independent reviewer and accepted residual risks.

**Claiming success for a mocked or absent test is prohibited.** No independent penetration test, official Keycloak support for Permora or production security approval is implied by green Actions runs.

## Risk acceptance record

For every production-impacting change, record scope, risk owner, test matrix, immutable commit, rollback procedure, monitoring and security-review decision. Keep secrets, personal identifiers and token evidence out of public issues.

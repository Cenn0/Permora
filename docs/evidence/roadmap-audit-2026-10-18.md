# Permora roadmap / production gate audit — 2026-10-18

> **Historical audit:** This file records the 18 October assessment at
> `2499f863`; do not interpret it as the current branch head. A newer
> [20 October P4 engineering checkpoint](../release-checklist.md#new-verified-engineering-evidence--20-october-2026)
> covers [PR #57](https://github.com/Cenn0/Permora/pull/57)
> merged as `c83711c` with six passing exact-main-SHA workflows and
> [open end-to-end federation/worker gate #58](https://github.com/Cenn0/Permora/issues/58).
> The original six release conditions remain open.


**Repository:** `Cenn0/Permora` · **Inspected main SHA:** `2499f863521b1a3b42d2768a93849ed001e2941f`.
**Verdict:** Not 1.0 complete; production release blocked. This record is not an
independent audit or a substitute for signed engineering/operator acceptance.
**Sources:** [Release gates](../release-checklist.md),
[Roadmap](../roadmap.md),
[issue #3](https://github.com/Cenn0/Permora/issues/3),
[security issue #46](https://github.com/Cenn0/Permora/issues/46).

## Current machine-executed evidence

The following six completed workflows on the *same* main commit returned
`success` (not merely an earlier branch or a draft artifact):

| Workflow | GitHub Actions run | Scope limit |
| --- | --- | --- |
| Permora CI | [39120692954](https://github.com/Cenn0/Permora/actions/runs/39120692954) | Unit/build, API/Keycloak/OIDC and isolated integration tests; no production IdP |
| System acceptance | [39120692850](https://github.com/Cenn0/Permora/actions/runs/39120692850) | Actual OpenLDAP/LDAPS and Keycloak; **not** Microsoft AD |
| Worker and recovery | [39120692981](https://github.com/Cenn0/Permora/actions/runs/39120692981) | Isolated real processes, failures and restores; **not** multi-zone HA |
| Accessibility acceptance | [39120692887](https://github.com/Cenn0/Permora/actions/runs/39120692887) | Automated axe; **not** independent screenreader/manual WCAG acceptance |
| Artifact integrity | [39120693032](https://github.com/Cenn0/Permora/actions/runs/39120693032) | Source-bound SBOM/signing controls for development candidates; **not** legal release approval |
| Public documentation | [39120692930](https://github.com/Cenn0/Permora/actions/runs/39120692930) | Static documentation checks only |

## P0–P7 workstream classification

| Workstream | Engineering evidence | Release condition |
| --- | --- | --- |
| P0: baseline | CI and threat-model documents exist; recent FGAP regression in issue #46 | **PARTIAL** — update rights matrix and independent approval |
| P1: OIDC/BFF | Real isolated Keycloak browser MFA/step-up, cookie and CSRF tests exist | **PARTIAL** — actual enterprise IdP, real TLS/proxy and operational signoff |
| P2: FGAP/delegation | Real two-account FGAP negative probes and full read-back; broad `Groups.manage` behavior **confirmed** | **BLOCKED for group grants** — default-deny guard exists; no mapping-only entitlement proven |
| P3: workers | Two process/failure-injection and 96-revocation stress evidence | **PARTIAL** — production envelope, multi-host/zone latency and SLOs |
| P4: governance/directory | Real LDAPS imported/nonimported users, pagination and group membership; governance code | **PARTIAL** — representative Microsoft AD semantics and end-to-end organizational governance |
| P5: product/accessibility | Angular UI, automated accessibility matrix, E2E fixtures | **PARTIAL** — manual screenreaders, full EN/DE and real business-journey acceptance |
| P6: operations/supply chain | Pilot compose, backup/restore isolates, SBOM, signed draft and built-image Grype gate | **BLOCKED for production** — real HA/TLS/secrets/DR, licenses, legal and external security |
| P7: release gates | Workflow separation and documented checks | **PARTIAL** — immutable release-specific human signoffs and measured operational evidence |

## Six original production acceptance areas — remaining blockers

1. **Keycloak FGAP / directory:** Real Microsoft AD (AD-specific mappers,
   `userAccountControl`, Kerberos/forest cases as applicable); deployed
   service-identity reachability and broad `Groups.manage` blast radius.
   **Group role grants should remain disabled unless central IAM explicitly
   approves that exposure.**
2. **Multiworker / performance:** representative load/soak, multi-host and
   multi-zone failure drills, p95/p99 revocation measurements and approved SLO.
3. **TLS / secret rotation / HA:** actual ingress and certificate rotation,
   reverse-proxy spoofing controls, HA Keycloak/PostgreSQL and external
   secret-manager rotation in a representative environment.
4. **Recovery:** coordinated Keycloak + PostgreSQL + Data Protection key/
   secret restore, actual PITR and RPO/RTO measurements, no stale grant replay.
5. **Supply chain:** current candidate-specific vulnerability disposition and
   legally accountable license/notice decisions before production promotion.
6. **Accessibility, security, privacy:** independent assistive-technology
   testing, lawful data-retention and deletion/backup policy, WORM/SIEM,
   DPIA decision, authorized independent penetration test and remediation.

## New engineering work from this audit

A proposed security regression change on
`fix/group-mapping-fail-closed-audit-20261018` rejects unknown directory
principal enum values before any Keycloak role-mapping write, and adds tests for
unknown kinds and an ambiguous lost-response **group revocation** retry.
**Do not call this verified or merged unless GitHub checks pass and the PR is
actually merged.** This narrows a coding failure mode but does not solve the
upstream FGAP `Groups.manage` privilege problem, nor provide a real HTTP/Keycloak
test of the application's opt-out mode.

## Acceptance rules

Never close [issue #3](https://github.com/Cenn0/Permora/issues/3) merely because
the six CI workflows pass. Never close [issue #46](https://github.com/Cenn0/Permora/issues/46)
while delegated group-role mapping requires unaccepted broad group administration.
For each production environment obtain named reviewer, test scenario, date,
immutable evidence link, known exceptions and explicit risk ownership. A positive
integration test is not official vendor support or proof of security in a
different Keycloak/AD deployment.

# Production release gates and evidence

This file distinguishes development CI evidence from independent security and operational acceptance. No source file or CI badge by itself approves production usage.

> **Current verified technical evidence (17 Oct 2026):** [real Keycloak LoA2/TOTP, OIDC BFF/CSRF and scope, 8/8 required jobs](https://github.com/Cenn0/Permora/actions/runs/39011230492). This **is not** an acceptance of a corporate MFA flow or a production security release.

| Gate | Technical requirement | Current status (17 Oct 2026) |
| --- | --- | --- |
| R01 | Reproducible .NET 10, Angular 22, PostgreSQL/Keycloak CI and locked dependencies | **8/8 core CI gates verified**, including real browser LoA2, current PR #21 evidence |
| R02 | HTTP authorization, PostgreSQL integration, typed workflow and dashboard | **Partially verified**, more lifecycle paths open |
| R03 | Isolated Keycloak 26.8.1 FGAP clients/users/groups, role mapping, scope/audience | **Representative cases verified**, complete corporate matrix open |
| R04 | Real BFF OIDC callback, cookie/CSRF and denial of password-only privileged writes | **Verified in isolated CI**, see P1 |
| R05 | Successful actual MFA/LoA in a configured Keycloak browser flow | **Verified in disposable Keycloak 26.8.1**: real TOTP LoA2 browser flow and trusted server BFF cookie ([CI #39011230492](https://github.com/Cenn0/Permora/actions/runs/39011230492)); enterprise IdP/production assurance remains open |
| R06 | Cross-team IDOR/permission scope across all HTTP/Keycloak operations | **Partial**, tested subset and foreign-identifier matrix |
| R07 | Real external LDAP/AD and continuous effective-group SoD | **Partial group remediation**, external federation open |
| R08 | Remote-write failure injection, multi-worker fairness and revocation bounds | **Partial:** simulated post-commit network-loss retries, real PostgreSQL concurrency and rotating worker queue selection; actual Keycloak network faults, HA load and revocation SLO open |
| R09 | Full UI EN/DE, WCAG keyboard/screenreader and real BFF-based user journeys | **Partial:** desktop/mobile keyboard/ARIA navigation plus real OIDC/MFA browser flow; complete bilingual UI, domain workflows and independent accessibility review open |
| R10 | Hardened deployment, production secrets, upgrade/restore, HA and recovery | **Partial:** isolated PostgreSQL restore and per-process file-mounted DB secret support; full proxy/TLS, Keycloak+key restore, rotation, HA and upgrade acceptance open |
| R11 | Full software/container/OS SBOM, transitives, licenses and release provenance | **Partial lockfile component inventory**, no complete acceptance |
| R12 | Privacy retention, WORM audit, independent pen test and release authorization | **Open**, no security certification |

Only label a stable 1.0 release after the narrowly supported setup passes all applicable security, recovery, privacy and usability gates with immutable artifacts and recorded reviewer decisions. Unsupported scenarios must be excluded from the release claim rather than silently deemed complete.

## Four distinct conclusions

- **Technically functional:** selected .NET, Angular, Keycloak and PostgreSQL paths have successful actual CI execution.
- **Officially supported:** Keycloak supports documented Keycloak APIs, not Permora as a Keycloak product.
- **Best practice:** bounded service permissions, server-side BFF, default deny, evidence-based reconciliation and signed reproducible releases.
- **Security recommended:** do not authorize production IAM management until the **remaining** R03/R06–R12 security and operational gaps and enterprise-specific R05 identity assurance are closed.

See [threat model](threat-model.md), [Keycloak integration](keycloak.md), [existing test evidence](ci.md) and [roadmap](roadmap.md).

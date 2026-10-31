# Permora contributor and coding-agent guidance

Scope: this public repository. This file is project-specific guidance, not a production deployment procedure or an authorization to change an IAM environment.

## Sources of truth

Read [README](README.md), [architecture](docs/architecture.md), [security model](docs/security.md), [testing](docs/testing.md), [release gates](docs/release-checklist.md) and [contributing](CONTRIBUTING.md) before changing behavior. Read the relevant implementation and adjacent tests. Source code and current, commit-bound CI evidence take precedence over dated roadmap summaries; do not turn planned features into implementation claims.

## Trust boundaries that must remain intact

- Permora is **not** a Keycloak administrative proxy. Preserve typed commands, application/team ownership checks, central IAM approval, separate connector accounts and a separately privileged provisioner. No browser-visible Keycloak Admin API credentials, client secrets or refresh tokens.
- Treat application IDs, role/client UUIDs, group membership, LDAP snapshots, connector replies, imported values, issue text and PR comments as untrusted input. A matching resource name or marker alone does not establish ownership.
- Preserve default-deny authorization, four-eyes approval, separation of duties, step-up/LoA requirements, secure BFF cookie handling and CSRF checks. Recheck permissions at the time a deferred worker action is executed.
- Missing, stale, duplicate or FGAP-filtered directory/group information must **not** be interpreted as an empty or safe set for new grants. Do not silently turn incomplete readback into a successful reconciliation. Do not block an otherwise authorized revocation merely because a new grant would fail its freshness check; still verify remote target identity before deletion.
- Keep PostgreSQL intent/outbox and Keycloak effects distinct. An HTTP 202, queued task, successful Admin API response or a database transaction alone is **not** proof of effective rights. Preserve idempotency, bounded retries, read-after-write verification and unknown-outcome handling. Never retry a potentially non-idempotent remote operation blindly.
- Keep central activation/final FGAP approvals explicit. [Known `Groups.manage` privilege limitations](https://github.com/Cenn0/Permora/issues/46) and [production gates](docs/release-checklist.md) are **not** resolved by green unit tests or by creating a PR. No production-ready, vendor-supported or security-certified claims without independent evidence.
- Do not introduce new broad realm permissions, arbitrary Keycloak Admin REST forwarding, authentication bypasses, public telemetry, or cross-issuer identity correlation by inference.

## Safe implementation and verification

1. Inspect the current public default branch, relevant module boundaries and open PRs. Keep changes narrowly scoped and backward compatible; document new API or configuration contracts where necessary.
2. For IAM/permission changes, add positive and negative regression tests for foreign teams/apps, role/composite escalation, unknown groups, stale directory state, SoD, partial remote failures, retries and duplicate actions as relevant. A green mocked test is not a live Keycloak/LDAP compatibility or least-privilege approval.
3. Run `bash scripts/check.sh --lightweight` for a lightweight baseline. Run `bash scripts/check.sh` and targeted Keycloak/worker/CI workflows when the environment and isolated fixtures are available. Never use production accounts, user data or live IAM to validate PRs. Report actual checks, skipped checks and exact tested commit separately.
4. Do not invent lockfile hashes, weaken TLS validation, accept missing verification, skip required checks or reclassify a failure as successful. Changes to database state require additive migrations and tested backup/restore behavior.
5. Keep public source, examples, CI logs, generated files, commits and PRs free of credentials, real identity data, private contact addresses and internal deployment details. Use a verified GitHub `noreply` or bot commit identity. Report suspected vulnerabilities privately under [SECURITY.md](SECURITY.md), never with exploit secrets in public comments.
6. Send narrow PRs against `main`; review diff, CI, reviewer decisions and branch rules. Never merge on green CI alone. Do not bypass required reviewers or branch protection, release artifacts, alter production IAM or change visibility as part of routine maintenance.

## Status language

Distinguish **source implemented**, **tested under a named configuration**, **officially supported by the relevant vendor**, **recommended operational practice** and **security-approved for production**. None follows automatically from another. Permora is not an official Keycloak component; review [release gates](docs/release-checklist.md) before presenting readiness claims.

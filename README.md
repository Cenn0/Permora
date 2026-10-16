<p align="center"><img src="web/public/brand/permora.svg" width="78" alt="Permora logo: colorful modules forming a P"></p>

# Permora

**Language:** **English** · [Deutsch](README.de.md)

[![CI](https://github.com/Cenn0/Permora/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Cenn0/Permora/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache--2.0-blue.svg)](LICENSE)

**Delegate application and access management in Keycloak without handing over realm administration.**

Permora is an independent open-source **C# / Angular portal** designed to let application teams manage their own roles, fine-grained permissions, and access requests. Central IAM administrators retain control over the realm and cross-application security policies.

> **Status: CI-tested development version, not approved for production.** The documented technical acceptance run completed seven required GitHub Actions jobs, including .NET/Angular builds, 102 xUnit tests with PostgreSQL, 32 frontend-logic tests, 8 Playwright tests, selected Keycloak 26.8.1 FGAP permission tests, and a local Docker build. **Production OIDC/MFA, LDAP/AD, full authorization-policy coverage, recovery and penetration testing remain open.** See [CI scope and evidence](docs/ci.md) and the [roadmap](docs/roadmap.md).

## Scope

- **Applications and teams:** Application catalog, registration, and explicit Owner, Maintainer, AccessManager, Approver, and Auditor mandates.
- **Permissions:** Application-scoped `perm.*` permissions and composite `role.*` business roles; user/group grants.
- **Governance:** Four-eyes approval, separation of duties (SoD), expiring access, recertification, and an application-scoped change log.
- **Provisioning:** Persistent change requests, retries, desired/actual reconciliation, and verification of effective Keycloak assignments.
- **Dashboard:** Responsive Angular UI with application cards, task queues, and activity feeds.

The critical security boundary remains **centralized**: realm settings, cross-application privileges, IAM policy, and approval of newly prepared clients/delegation rights are outside team self-service. Prepared clients remain disabled until the documented IAM approval. An HTTP `202 Accepted` response **does not** prove a change has taken effect in Keycloak.

**Not yet implemented or accepted:** Automatic FGAP provisioning, complete cross-issuer identity correlation, ABAC, full HR/AD integration, externally immutable audit storage, and load, recovery, and penetration testing. The [roadmap](docs/roadmap.md) distinguishes implemented source code, executed tests, and outstanding acceptance criteria.

## Technology

| Component | Stack |
| --- | --- |
| Backend and workers | C# / .NET 10 (ASP.NET Core) |
| Frontend | Angular 22 / TypeScript |
| Database | PostgreSQL |
| Identity provider | Keycloak (validate the intended version and FGAP behavior) |
| Authentication | OIDC backend-for-frontend with server-side sessions and separated connector privileges |
| License | [Apache-2.0](LICENSE) |

Permora is **not an official Keycloak component**. An integration that works technically is not necessarily vendor-supported, an operational best practice, or security-certified.

## Local development

Requires the SDK selected in [global.json](global.json), a compatible Node.js version for Angular 22, Python 3, and Docker Compose. The following Bash commands start **local development dependencies only**:

```bash
python3 scripts/init-local.py
docker compose --env-file deploy/local.env -f deploy/compose.local.yml up -d --wait

export PERMORA_CONFIG="$PWD/config/local.json"
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development

dotnet run --project src/Permora.Worker -- --migrate
dotnet run --project src/Permora.Worker -- --seed-demo
```

From separate terminals at the repository root, use the same three environment variables and then start the API and worker:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/Permora.Api
# In another terminal:
dotnet run --project src/Permora.Worker
```

Start the frontend in a third terminal:

```bash
cd web
npm ci
npm start
```

Open **http://localhost:4200**. The local `demo-owner` login is enabled **for development only**; it is not production SSO. The Keycloak development container does not configure a production realm or fine-grained permissions on its own. Follow the separate [Keycloak integration guide](docs/keycloak.md) before testing actual remote changes.

## Tests and readiness

```bash
bash scripts/check.sh --lightweight
bash scripts/check.sh
```

`--lightweight` checks repository structure and isolated frontend logic. Dependency lockfiles are committed and validated in CI; the full local check additionally requires the correct SDKs and a **dedicated** PostgreSQL test database configured through `PERMORA_TEST_POSTGRES`. The [GitHub Actions CI](.github/workflows/ci.yml) runs locked restores, .NET/Angular tests, selected real Keycloak FGAP integration cases and a Docker image build **without publishing or deploying**. Passing these checks does not constitute production acceptance.

[Development guide](docs/development.md) · [CI coverage](docs/ci.md) · [Test requirements](docs/testing.md) · [Evidence and outstanding checks](docs/review.md)

## Documentation and contributions

[Architecture](docs/architecture.md) · [Keycloak delegation](docs/keycloak.md) · [Security model](docs/security.md) · [Operations](docs/operations.md) · [Roadmap](docs/roadmap.md) · [API](docs/api.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

Licensed under the [Apache License 2.0](LICENSE). Report vulnerabilities through the private procedure in [SECURITY.md](SECURITY.md), not in public issues containing credentials or internal deployment details.

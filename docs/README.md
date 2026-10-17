# Permora documentation

[← English README](../README.md) · [Deutsch](../README.de.md)

Permora is an independent, open-source C# / Angular portal for **delegated Keycloak application management**. It is **not** a Keycloak component and is **not approved for production deployment**. Detailed guides are currently partly in German.

| Goal | Document |
| --- | --- |
| Understand the main value and trust boundaries | [Project README](../README.md) · [Architecture](architecture.md) |
| Run a local development environment | [Development](development.md) |
| Integrate with Keycloak and understand delegated permissions | [Keycloak](keycloak.md) |
| Review tenant/application isolation and security requirements | [Security model](security.md) |
| Learn typed application API contracts | [API](api.md) |
| Understand deploy, operations and recovery limitations | [Operations](operations.md) |
| Check which tests were actually executed | [CI scope](ci.md) · [Testing](testing.md) · [Evidence](evidence/ci-2026-10-16-final.md) |
| Track outstanding work and acceptance criteria | [Roadmap](roadmap.md) |
| Contribute or report vulnerabilities | [Contributing](../CONTRIBUTING.md) · [Security policy](../SECURITY.md) |

## Support and security boundaries

- **Implemented:** portal API, worker, provisioner, Angular UI and selected client-scoped Keycloak integrations.
- **Verified:** specific CI, PostgreSQL and FGAP v2 cases as recorded in [CI evidence](ci.md).
- **Officially supported:** relevant Keycloak APIs are documented upstream, **not** Permora as a Keycloak product.
- **Security recommended:** do not deploy production permissions until IAM least-privilege verification, real SSO/MFA, lifecycle/SoD, recovery and independent security reviews are complete.

Changes are asynchronous. HTTP 202 acceptance is **not** confirmation of remote role enforcement. Central IAM approval is a distinct prerequisite for newly provisioned client activation.

No invented dashboard screenshot is published; the [tracked brand asset](../web/public/brand/permora.svg) and [design description](design.md) are available until a verified application capture exists.

**Dependency inventory:** [Lockfile CycloneDX generator and exclusions](supply-chain.md).

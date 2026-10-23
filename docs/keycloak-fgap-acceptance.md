# Keycloak FGAP v2 — Group role-mapping privilege boundary

**Applies to the isolated Keycloak 26.8.1 contract. This is an operator acceptance
runbook, not a claim of Keycloak vendor support, a production certification, or
permission to enable delegated group grants by default.**

## Why this gate exists

Keycloak 26.8.1 provides a `Groups.manage` permission that permits more than
adding/removing application-client roles on a group. Its documented `Groups`
scope set does not provide an independently verified mapping-only
`Groups.map-role` entitlement. A service credential permitted to update an
approved group's role mappings can also modify that group's metadata. **The
effective Keycloak token controls what an attacker can do after token theft;
Permora's typed API and `ApprovedGroupIds` only constrain normal application
requests.** The blast radius is not made safe by a successful HTTP 403 from
Permora's BFF.

Sources: [Keycloak 26.8.1 administration guide](https://www.keycloak.org/docs/26.8.1/server_admin/),
[upstream request #45269](https://github.com/keycloak/keycloak/issues/45269),
[security issue #46](https://github.com/Cenn0/Permora/issues/46).

## Executable same-identity test matrix

`python3 scripts/ci-keycloak-contract.py` creates a **disposable loopback**
Keycloak realm, two independent application clients (A and B), two delegated
service accounts, explicit client/user/group FGAP policies, and synthetic
principals. All probes use the **same A service-account access token** after
the positive group-role mapping, not the bootstrap token. A separate,
isolated bootstrap administrator reads the persisted postcondition.

| Operation | Expected on Keycloak 26.8.1 | Meaning |
| --- | --- | --- |
| Add/remove Client A role to permitted group | 201/204, then read-back | Authorized path works; group scope still wider than mapping |
| Change **permitted group's name** | 204, read-back of altered metadata | **Known privilege excess**; restore original metadata in a `finally` block |
| Rename or delete unrelated Group B | 401/403/404; unchanged/existing after admin read-back | Cross-group isolation, directly against Admin REST |
| Add/remove user as permitted Group A member | 401/403/404; no membership delta | Group role mapping does not imply separate membership management scopes |
| Map Client A role onto Group B | 401/403/404; mapping absent in read-back | Denied group target cannot receive role |
| Map Client B role onto Group A | 401/403/404; mapping absent in read-back | Approved group does not confer foreign client mapping |
| Modify target user's enabled status | 401/403/404 | User role mapping must not grant user administration |
| Change realm FGAP configuration; create arbitrary client; write Client B role | Denied and checked | No implicit global administrative scope |

Expected denial is not proof of complete least privilege. Passing the matrix
only demonstrates the explicitly tested Admin REST paths in one ephemeral
Keycloak version. Add an independent versioned run before any Keycloak
upgrade, with all deployed service-account profiles and **actual** FGAP
policies. **Do not turn on a group grant feature merely because these tests
pass.**

## Joined disabled-grant/emergency-revoke contract

The [joined federation acceptance](joined-federation-acceptance.md) adds the
actual Portal API and worker to the real Keycloak/LDAPS/PostgreSQL fixture.
Both have new group grants disabled. It requires HTTP 403/no queue for a new
grant, conflict/audit on ambiguous targets, explicit owner retry, HTTP 503,
actual committed remote DELETE with withheld response, killed worker and
postcondition-based restart without duplicate writes. Both LDAP import modes
are covered by assertions; successful exact-source CI is required as evidence.
Portal authentication here is explicit Development/loopback, not OIDC/MFA.
This narrows the automated gap in #46; the deployed controls below and the
actual upstream privilege excess remain separate blockers.

## Deployment-specific controls (not implemented by the CI fixture)

1. **Scope and identity.** Use separate service identities and independently
   rotated credentials for role editing, assignment mapping, directory reads,
   and disabled-client preparation. No `realm-admin`, broad
   `manage-users` or interactive login for those identities. Minimize
   target groups and clients in the Keycloak policy itself; use exact IDs.
   The current connector is scoped *per realm/profile*, not automatically per
   application, and Group `manage` is inherently broader than mapping.
2. **Application safeguards.** Leave `AllowGroupRoleGrants=false`. Any
   exception requires `AllowGroupRoleGrants=true` **and** the exact
   `ApprovedGroupIds` in both API and worker configurations. Revoke remains
   possible with the grant switch off. Missing/unrecognized principals must
   fail closed, without guessing remote IDs.
3. **Network confinement.** Restrict egress from the worker/provisioner to
   required PostgreSQL, Keycloak token, and Keycloak Admin API endpoints at
   the **host firewall or network policy**; block direct alternate routes
   and untrusted proxies. The Docker Compose network name
   `trusted-egress` is **not** a destination ACL. Destination/IP
   filtering alone does not restrict `PUT /groups/{id}` on the permitted
   Keycloak host. A dedicated, identity-aware method/path gateway could
   reduce Admin REST exposure only if bypass is impossible and every
   required GET/POST/DELETE path has been tested end-to-end. That gateway
   is **not supplied** by this repository.
4. **Secret rotation.** Use mounted secrets supplied by the operator's
   secret manager. The connector reads the assignment secret file at
   token-refresh time and caches access tokens until shortly before expiry.
   Coordinate credential overlap, deployment and restart/remount, then
   revoke the *old* credential and verify an old-secret token request fails.
   A bind-mounted single file may not observe inode replacement; test the
   real secret delivery mechanism. Rotate each profile independently,
   including after suspected compromise. The repository does not provide
   automatic secret-manager rotation or a tested HA cutover.
5. **Audit and containment.** Configure Keycloak admin-event recording and
   protect the transport into an external audit system (for example SIEM).
   Alert on group update/delete, unapproved membership changes, unexpected
   client/realm changes, and privileged operations attributed to a
   Permora assignment service identity. Verify the attribution fields
   emitted by the deployed Keycloak configuration before building a detector;
   portal audit records alone cannot reveal token theft or direct Admin REST.
   Keep unrelated administrators capable of disabling the compromised
   account, revoking its token sessions where supported, rotating secrets,
   and verifying real role mappings. Treat existing access tokens according
   to actual expiry/revocation behavior, not only the portal queue state.

## Operator acceptance record (required per deployed environment)

Record the Keycloak patch version and source/image digest, effective service
account/client identities, exact realm and group/client allowlists, positive
and negative same-identity test results, direct GitHub Actions/run or internal
test evidence, method/path egress restrictions and bypass test, secret
rotation/revocation results, external audit alerts, exceptions, risk owner
and date of decision. Re-run after any Keycloak upgrade or rights-policy
change. **Absent those records, group grants remain disabled.**

This is environment-specific IAM risk acceptance, not an official support
statement or a compulsory formal production-release approval for the public
open-source repository. No representative Microsoft Active Directory
integration, HA/DR, legal privacy review or independent penetration test is
proven by the isolated FGAP suite.

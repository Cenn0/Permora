#!/usr/bin/env python3
"""Real Keycloak 26.8 REST/OIDC contract checks against a disposable, isolated server.

This tests roles, composite roles, user/group mappings, scopes and access-token content.
It does NOT bootstrap or prove FGAP v2 least-privilege client-management policies.
Only ephemeral CI credentials are supported; NEVER point this at a production realm.
"""
import base64
import json
import os
import sys
import time
import uuid
import urllib.error
import urllib.parse
import urllib.request

BASE = os.environ.get("PERMORA_TEST_KC_URL", "").rstrip("/")
ADMIN = os.environ.get("PERMORA_TEST_KC_USERNAME", "")
PASSWORD = os.environ.get("PERMORA_TEST_KC_PASSWORD", "")
if not BASE.startswith("http://127.0.0.1:") or not ADMIN or not PASSWORD:
    raise SystemExit("Keycloak contract requires disposable loopback server and CI-only credentials")
REALM = "permora-ci-" + uuid.uuid4().hex[:12]


def request(method, path, body=None, *, token=None, form=False, expected=(200, 201, 204)):
    headers = {"Accept": "application/json"}
    if token:
        headers["Authorization"] = "Bearer " + token
    data = None
    if body is not None:
        if form:
            data = urllib.parse.urlencode(body).encode("utf-8")
            headers["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            data = json.dumps(body, separators=(",", ":")).encode("utf-8")
            headers["Content-Type"] = "application/json"
    uri = BASE + path
    req = urllib.request.Request(uri, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(req, timeout=15)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        status = response.status
        payload = response.read(8000)
        location = response.headers.get("Location", "")
    if status not in expected:
        # Limit error detail and never print submitted request bodies or passwords.
        raise AssertionError(f"{method} {path.split('?')[0]}: expected {expected}, received HTTP {status}: {payload[:600]!r}")
    if payload:
        try:
            return status, json.loads(payload), location
        except ValueError:
            return status, payload.decode("utf-8", "replace"), location
    return status, None, location


def admin_path(suffix=""):
    return "/admin/realms/" + urllib.parse.quote(REALM, safe="") + suffix


def kc_token(realm, data):
    return request("POST", "/realms/" + realm + "/protocol/openid-connect/token", data,
                   form=True)[1]["access_token"]


def bearer_json(token):
    piece = token.split(".")
    if len(piece) != 3:
        raise AssertionError("OIDC did not issue a JWT access token")
    return json.loads(base64.urlsafe_b64decode(piece[1] + "=" * (-len(piece[1]) % 4)))


def create_client(token, client_id, *, login=False, service=False, app_id=None):
    data = {
        "clientId": client_id,
        "protocol": "openid-connect",
        "enabled": True,
        "fullScopeAllowed": False,
        "standardFlowEnabled": False,
        "implicitFlowEnabled": False,
        "directAccessGrantsEnabled": login,
        "serviceAccountsEnabled": service,
        "publicClient": login,
        "bearerOnly": not login and not service,
    }
    if app_id is not None:
        data["attributes"] = {"permora.application": app_id}
    request("POST", admin_path("/clients"), data, token=token)
    found = request("GET", admin_path("/clients?clientId=" + urllib.parse.quote(client_id)), token=token)[1]
    exact = [c for c in found if c.get("clientId") == client_id]
    if len(exact) != 1:
        raise AssertionError("New test client is not uniquely available")
    return exact[0]["id"]


def create_role(token, client_uuid, role_name, app_id, entity):
    url = admin_path("/clients/" + client_uuid + "/roles")
    request("POST", url, {
        "name": role_name,
        "description": "Ephemeral Permora CI role",
        "attributes": {"permora.application": [app_id], "permora.entity": [entity]}
    }, token=token)
    role = request("GET", url + "/" + urllib.parse.quote(role_name), token=token)[1]
    if not role.get("id") or role["name"] != role_name or role.get("clientRole") is not True:
        raise AssertionError("Keycloak returned an invalid client role")
    if role.get("containerId") != client_uuid:
        raise AssertionError("Role unexpectedly belongs to another client")
    return role


def create_user(token, user_name, password):
    request("POST", admin_path("/users"), {
        "username": user_name, "enabled": True, "requiredActions": [],
        "firstName": "Permora", "lastName": "CI", "email": user_name + "@permora.invalid",
        "emailVerified": True,
        "credentials": [{"type": "password", "value": password, "temporary": False}]
    }, token=token)
    users = request("GET", admin_path("/users?username=" + urllib.parse.quote(user_name) + "&exact=true"), token=token)[1]
    matches = [u for u in users if u.get("username") == user_name]
    if len(matches) != 1:
        raise AssertionError("User creation was not confirmed")
    return matches[0]["id"]


def create_group(token, group_name):
    request("POST", admin_path("/groups"), {"name": group_name}, token=token)
    groups = request("GET", admin_path("/groups?search=" + urllib.parse.quote(group_name)), token=token)[1]
    matches = [g for g in groups if g.get("name") == group_name]
    if len(matches) != 1:
        raise AssertionError("Group creation was not confirmed")
    return matches[0]["id"]


def check(condition, label):
    if not condition:
        raise AssertionError(label)
    print("PASS:", label)


def wait_for_keycloak():
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            _, metadata, _ = request("GET", "/realms/master/.well-known/openid-configuration")
            if metadata.get("issuer"):
                return
        except (OSError, AssertionError, ValueError):
            pass
        time.sleep(3)
    raise TimeoutError("Isolated Keycloak did not become ready in 180 seconds")


def run():
    wait_for_keycloak()
    master_token = kc_token("master", {
        "grant_type": "password", "client_id": "admin-cli", "username": ADMIN, "password": PASSWORD
    })
    request("POST", "/admin/realms", {"realm": REALM, "enabled": True}, token=master_token)
    try:
        api_a = "ci-app-a-api"
        api_b = "ci-app-b-api"
        owner_a = str(uuid.uuid4())
        owner_b = str(uuid.uuid4())
        ca = create_client(master_token, api_a, app_id=owner_a)
        cb = create_client(master_token, api_b, app_id=owner_b)
        login = create_client(master_token, "ci-browser", login=True)
        unprivileged = create_client(master_token, "ci-unprivileged", service=True)
        leaf_a = create_role(master_token, ca, "perm.orders.read", owner_a, str(uuid.uuid4()))
        composite_a = create_role(master_token, ca, "role.operator", owner_a, str(uuid.uuid4()))
        leaf_b = create_role(master_token, cb, "perm.warehouse.read", owner_b, str(uuid.uuid4()))
        composite_b = create_role(master_token, cb, "role.warehouse", owner_b, str(uuid.uuid4()))

        path = admin_path("/clients/" + ca + "/roles/role.operator/composites")
        request("POST", path, [leaf_a], token=master_token)
        children = request("GET", path, token=master_token)[1]
        check(len(children) == 1 and children[0]["id"] == leaf_a["id"],
              "Client-A composite includes exactly the approved Client-A permission")

        path_b = admin_path("/clients/" + cb + "/roles/role.warehouse/composites")
        request("POST", path_b, [leaf_b], token=master_token)
        children_b = request("GET", path_b, token=master_token)[1]
        check(len(children_b) == 1 and children_b[0]["id"] == leaf_b["id"],
              "Client-B composite belongs to its own authorization client")

        test_password = "CI_only_complex_5678!"
        person = create_user(master_token, "permora-ci-person", test_password)
        group = create_group(master_token, "permora-ci-team")
        roles_a_path = admin_path("/users/" + person + "/role-mappings/clients/" + ca)
        roles_b_path = admin_path("/users/" + person + "/role-mappings/clients/" + cb)
        request("POST", roles_a_path, [composite_a], token=master_token)
        request("POST", roles_b_path, [composite_b], token=master_token)
        user_a_roles = request("GET", roles_a_path, token=master_token)[1]
        user_b_roles = request("GET", roles_b_path, token=master_token)[1]
        check(any(role["id"] == composite_a["id"] for role in user_a_roles)
              and any(role["id"] == composite_b["id"] for role in user_b_roles),
              "One user can hold distinct mappings of two independent applications")

        group_path = admin_path("/groups/" + group + "/role-mappings/clients/" + ca)
        request("POST", group_path, [composite_a], token=master_token)
        group_roles = request("GET", group_path, token=master_token)[1]
        check(any(role["id"] == composite_a["id"] for role in group_roles),
              "Group mapping is persisted and readable from Keycloak")

        # Explicit role scope, never Full Scope Allowed.
        scope_path = admin_path("/clients/" + login + "/scope-mappings/clients/" + ca)
        request("POST", scope_path, [leaf_a, composite_a], token=master_token)
        scope_roles = request("GET", scope_path, token=master_token)[1]
        check({leaf_a["id"], composite_a["id"]}.issubset({r["id"] for r in scope_roles}),
              "Login client uses explicit Client-A role scope mappings")

        # Verify a real token audience. No broad, realm-wide role mapping.
        request("POST", admin_path("/clients/" + login + "/protocol-mappers/models"), {
            "name": "permora-ci-app-a-audience", "protocol": "openid-connect",
            "protocolMapper": "oidc-audience-mapper",
            "consentRequired": False,
            "config": {
                "included.client.audience": api_a, "id.token.claim": "false",
                "access.token.claim": "true"
            }
        }, token=master_token)
        issued = kc_token(REALM, {
            "grant_type": "password", "scope": "openid", "client_id": "ci-browser",
            "username": "permora-ci-person", "password": test_password
        })
        _, claims, _ = request("GET", "/realms/" + REALM + "/protocol/openid-connect/userinfo", token=issued)
        check(bool(claims.get("sub")), "Issued test access token is accepted by Keycloak userinfo")
        payload = bearer_json(issued)
        audience = payload.get("aud", [])
        if isinstance(audience, str):
            audience = [audience]
        check(api_a in audience and api_b not in audience,
              "Explicit audience contains Client A but not unrelated Client B")
        mapping = payload.get("resource_access", {})
        in_a = mapping.get(api_a, {}).get("roles", [])
        in_b = mapping.get(api_b, {}).get("roles", [])
        check("role.operator" in in_a and "perm.orders.read" in in_a,
              "Expected composite role and atomic permission are present in Client-A token")
        check(not in_b, "Client-B roles are excluded from restricted Client-A token")

        # Negative check: a random service account is not an admin merely by being a client.
        secret_doc = request("GET", admin_path("/clients/" + unprivileged + "/client-secret"), token=master_token)[1]
        secret = secret_doc.get("value")
        if not secret:
            raise AssertionError("No credential for isolated negative-test service account")
        no_admin_token = kc_token(REALM, {
            "grant_type": "client_credentials", "client_id": "ci-unprivileged", "client_secret": secret
        })
        status, _, _ = request("GET", admin_path("/clients"), token=no_admin_token, expected=(401, 403))
        check(status in (401, 403), "Unprivileged service account cannot manage realm clients")

        # FGAP v2 security boundary, exercised with two genuinely delegated
        # service account users. The bootstrap token is NOT used for assertions.
        request("PUT", admin_path(), {"realm": REALM, "adminPermissionsEnabled": True},
                token=master_token)
        admin_clients = request("GET", admin_path("/clients?clientId=admin-permissions"),
                                token=master_token)[1]
        authz_clients = [c for c in admin_clients if c.get("clientId") == "admin-permissions"]
        check(len(authz_clients) == 1, "FGAP v2 admin-permissions resource server created")
        authz_path = admin_path("/clients/" + authz_clients[0]["id"] + "/authz/resource-server")

        def delegated_token(alias, owned_client):
            delegated_uuid = create_client(master_token, "ci-delegated-" + alias, service=True)
            service_user = request("GET", admin_path("/clients/" + delegated_uuid + "/service-account-user"),
                                   token=master_token)[1]
            if not service_user.get("id"):
                raise AssertionError("Delegated service account user missing")
            realm_management_clients = request("GET",
                admin_path("/clients?clientId=realm-management"), token=master_token)[1]
            management_uuid = [c for c in realm_management_clients
                               if c.get("clientId") == "realm-management"][0]["id"]
            current_admin_roles = request("GET", admin_path("/users/" + service_user["id"]
                + "/role-mappings/clients/" + management_uuid), token=master_token)[1]
            check(not any(role.get("name") in {"realm-admin", "manage-clients", "view-clients",
                      "manage-users"} for role in current_admin_roles),
                  "Delegated service account " + alias + " has no broad realm admin roles")

            policy_name = "ci-only-" + alias + "-delegate-policy"
            request("POST", authz_path + "/policy/user",
                    {"name": policy_name, "logic": "POSITIVE", "users": [service_user["id"]]},
                    token=master_token)
            request("POST", authz_path + "/permission/scope", {
                "name": "ci-only-" + alias + "-client-permission",
                "resourceType": "Clients",
                "scopes": ["view", "manage", "map-roles", "map-roles-composite"],
                "resources": [owned_client],
                "policies": [policy_name]
            }, token=master_token)
            doc = request("GET", admin_path("/clients/" + delegated_uuid + "/client-secret"),
                          token=master_token)[1]
            secret = doc.get("value")
            if not secret:
                raise AssertionError("Delegated client credential unavailable")
            return (kc_token(REALM, {"grant_type": "client_credentials",
                                   "client_id": "ci-delegated-" + alias,
                                   "client_secret": secret}), policy_name)

        delegated_a, policy_a = delegated_token("app-a", ca)
        delegated_b, policy_b = delegated_token("app-b", cb)
        new_roles = {}
        for alias, delegated, own, other in (
            ("A", delegated_a, ca, cb), ("B", delegated_b, cb, ca)
        ):
            status, own_details, _ = request("GET", admin_path("/clients/" + own),
                                             token=delegated, expected=(200,))
            check(status == 200 and own_details.get("id") == own,
                  "FGAP delegated team " + alias + " can view only its own client")
            status, _, _ = request("GET", admin_path("/clients/" + other),
                                   token=delegated, expected=(403, 404))
            check(status in (403, 404),
                  "FGAP delegated team " + alias + " cannot read the foreign client")
            status, _, _ = request("POST", admin_path("/clients/" + other + "/roles"), {
                "name": "ci-foreign-should-not-exist-" + alias.lower()
            }, token=delegated, expected=(401, 403, 404))
            check(status in (401, 403, 404),
                  "FGAP delegated team " + alias + " cannot create a role in the foreign client")
            status, _, _ = request("PUT", admin_path(),
                                  {"adminPermissionsEnabled": False}, token=delegated,
                                  expected=(401, 403))
            check(status in (401, 403),
                  "FGAP delegated team " + alias + " cannot disable realm security")
            status, _, _ = request("POST", admin_path("/clients"), {
                "clientId": "ci-unapproved-client-" + alias.lower()
            }, token=delegated, expected=(401, 403))
            check(status in (401, 403),
                  "FGAP delegated team " + alias + " cannot create arbitrary clients")
            fresh_role = "ci-delegated-created-" + alias.lower()
            status, _, _ = request("POST", admin_path("/clients/" + own + "/roles"), {
                "name": fresh_role,
                "description": "Scoped role created using FGAP, never realm-admin"
            }, token=delegated, expected=(201, 204))
            own_role = request("GET", admin_path("/clients/" + own + "/roles/" + fresh_role),
                               token=master_token)[1]
            check(status in (201, 204) and own_role.get("name") == fresh_role,
                  "FGAP delegated team " + alias + " can create an own-client role")
            new_roles[alias] = own_role

        # Delegated user/group mapping requires TWO independent permissions:
        # permission to map this client's roles AND permission on the target identity.
        # Never substitute realm-wide manage-users for a passing check.
        other_person = create_user(master_token, "permora-ci-forbidden-user", "CI_only_complex_4569!")
        other_group = create_group(master_token, "permora-ci-forbidden-team")
        forbidden_user_path = admin_path("/users/" + other_person + "/role-mappings/clients/" + ca)
        forbidden_group_path = admin_path("/groups/" + other_group + "/role-mappings/clients/" + ca)

        status, _, _ = request("POST", roles_a_path, [new_roles["A"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Client-level map-roles alone does not grant unrestricted user mappings")
        status, _, _ = request("POST", group_path, [new_roles["A"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Client-level map-roles alone does not grant unrestricted group mappings")

        request("POST", authz_path + "/permission/scope", {
            "name": "ci-only-a-target-user-role-mapping",
            "resourceType": "Users", "scopes": ["view", "map-roles"],
            "resources": [person], "policies": [policy_a]
        }, token=master_token)
        status, _, _ = request("POST", roles_a_path, [new_roles["A"]], token=delegated_a,
                               expected=(201, 204))
        actual = request("GET", roles_a_path, token=master_token)[1]
        check(any(role["id"] == new_roles["A"]["id"] for role in actual),
              "Team A can map its own client role onto an explicitly allowed user")

        status, _, _ = request("POST", roles_b_path, [new_roles["B"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Team A cannot map Client B roles even onto an allowed user")
        status, _, _ = request("POST", forbidden_user_path, [new_roles["A"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Team A cannot map own roles onto an unapproved target user")
        status, _, _ = request("PUT", admin_path("/users/" + person),
                               {"enabled": False}, token=delegated_a, expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "User map-roles permission cannot modify the target user's account")

        request("POST", authz_path + "/permission/scope", {
            "name": "ci-only-a-target-group-role-mapping",
            "resourceType": "Groups", "scopes": ["view", "manage"],
            "resources": [group], "policies": [policy_a]
        }, token=master_token)
        status, _, _ = request("POST", group_path, [new_roles["A"]], token=delegated_a,
                               expected=(201, 204))
        actual = request("GET", group_path, token=master_token)[1]
        check(any(role["id"] == new_roles["A"]["id"] for role in actual),
              "Team A can map its own client role onto its explicitly allowed group")

        # IMPORTANT SECURITY CONTRACT: in Keycloak 26.8.1 the FGAP group
        # 'manage' privilege is broader than role mapping alone. Probe an
        # unrelated group modification using the SAME delegated credential
        # to determine the actual extra authority, rather than calling this
        # service account 'map-roles-only' or least privilege.
        allowed_group_path = admin_path("/groups/" + group)
        group_before = request("GET", allowed_group_path, token=master_token)[1]
        group_name = group_before["name"]
        widened_name = "ci-group-manage-breadth-probe"
        edit_status, _, _ = request("PUT", allowed_group_path,
            {"id": group, "name": widened_name}, token=delegated_a,
            expected=(204, 403, 404))
        group_after = request("GET", allowed_group_path, token=master_token)[1]
        if edit_status == 204:
            check(group_after["name"] == widened_name,
                "FGAP Group.manage can also rename a mapped group (non-mapping authority)")
            request("PUT", allowed_group_path, {"id": group, "name": group_name}, token=master_token)
            check(request("GET", allowed_group_path, token=master_token)[1]["name"] == group_name,
                "Temporary group metadata restored after blast-radius test")
        else:
            check(group_after["name"] == group_name,
                "FGAP denied non-mapping group modification in this isolated setup")
        print("GROUP FGAP: map-roles on Groups currently needs manage; its wider operation surface must be individually audited.",
              flush=True)

        status, _, _ = request("POST", forbidden_group_path, [new_roles["A"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Team A cannot map own roles onto an unapproved group")
        status, _, _ = request("POST", group_path, [new_roles["B"]], token=delegated_a,
                               expected=(401, 403, 404))
        check(status in (401, 403, 404),
              "Group management alone does not allow foreign Client B roles")
        request("DELETE", roles_a_path, [new_roles["A"]], token=delegated_a)
        request("DELETE", group_path, [new_roles["A"]], token=delegated_a)
        after_user = request("GET", roles_a_path, token=master_token)[1]
        after_group = request("GET", group_path, token=master_token)[1]
        check(not any(role["id"] == new_roles["A"]["id"] for role in after_user + after_group),
              "Delegated user and group revocations are verified with real Keycloak read-back")

        request("DELETE", roles_a_path, [composite_a], token=master_token)
        after_revoke = request("GET", roles_a_path, token=master_token)[1]
        check(not any(role["id"] == composite_a["id"] for role in after_revoke),
              "Direct role revocation is confirmed by a Keycloak read-back")
        print("FGAP v2 delegated client administration: two-service-account positive/negative REST matrix passed.")
        print("Keycloak live contract completed; real RBAC/claims and negative unprivileged controls passed.")
    finally:
        request("DELETE", admin_path(), token=master_token)


if __name__ == "__main__":
    try:
        run()
    except Exception as error:
        print(f"CI Keycloak contract failed: {type(error).__name__}: {error}", file=sys.stderr)
        raise

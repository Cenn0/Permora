# Encrypted BFF sessions, TLS termination and key rotation

This is an implementation and runbook, not a claim that a production cluster has been deployed or accepted.

## Required production configuration

API startup outside Development now requires a current RSA key-encryption certificate with a private key (2048 bits or stronger), a password file and persistent shared Data Protection storage. The existing plaintext development key-ring fallback is not available in Production.

Add these sections to the process-specific configuration (paths only, never secret values):

```json
{
  "KeyProtection": {
    "CertificatePath": "/run/key-protection/current.pfx",
    "PasswordFile": "/run/key-protection/current.password",
    "PreviousCertificates": []
  },
  "Transport": {
    "TrustedProxies": ["192.0.2.10"]
  }
}
```

Replace the documentation-only proxy address with the exact observed, trusted ingress proxy address. No wildcard or implicit trust is enabled. Only one forwarded hop is processed; forwarded host is not used. Production requests outside `/health` are rejected unless HTTPS was established directly or reported by an explicitly trusted TLS terminator. Protect internal service access with NetworkPolicy/firewall rules as well; a header is not TLS encryption between proxy and API.

The certificate and password files must be separately mounted read-only and accessible only to the API identity. Never put them in the image or repository. The certificate password must contain at least 16 characters. API replicas share the same PostgreSQL session database, Data Protection application name (`Permora`), encrypted key directory and decryption certificates. No sticky-session dependency is intended. These prerequisites do not make PostgreSQL, the key-store volume or ingress highly available by themselves.

## Wrapping-certificate rotation

1. Generate and distribute a new private wrapping certificate and a distinct strong password via the organization’s secret manager. Do not replace the old private key yet.
2. Configure the new certificate as `CertificatePath`/`PasswordFile` on every API replica. Keep the old file pair in `PreviousCertificates` (at most four retained certificates). Mount all retained certificate files.
3. Roll replicas while keeping at least one ready API instance. Verify a pre-rotation session on a restarted replica and complete fresh authentication. New Data Protection keys are wrapped with the current certificate; existing keys retain their original wrapping certificate.
4. Keep old decryption keys until every dependent session, retained key-ring entry and recoverable backup has expired or been rewrapped using a reviewed procedure. Certificate expiration does not erase the need to decrypt historic backups, so old decryption certificates may be expired; the current encryption certificate may not.
5. A compromised certificate requires an explicit incident/revocation decision, not routine rotation. Invalidating sessions and backups may be necessary. This code does not provide a secret-manager controller or unattended rotation agent.

## Restore

Quiesce every API/worker/provisioner and Keycloak writer before a coordinated recovery point. Back up the portal database, Keycloak database, encrypted shared key ring and required wrapping certificates under separate access controls. Restore first into an isolated environment. Validate old and new protected payloads, database tickets and logout propagation between independent API service providers. Do not blindly replay grants from an old portal backup; reconcile against Keycloak before enabling expansions.

The automated tests verify encrypted XML key storage, two independent providers, old/new wrapping certificate decryption, a copied/restored key ring, missing-key/purpose-isolation failures, real PostgreSQL session sharing and cross-replica logout. They are not a full multi-host failover, coordinated Keycloak backup, RPO/RTO, HSM/KMS or production TLS deployment acceptance.

Primary reference: [Microsoft Data Protection configuration](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0). See also [operations](operations.md) and [release gates](release-checklist.md).

# Lockfile-based Software Bill of Materials (initial P6/P7 scope)

The repository generates a **deterministic, offline CycloneDX 1.6 package inventory** from its committed NuGet `packages.lock.json` and npm `package-lock.json`.

```bash
python3 -m unittest discover -s scripts -p 'test_sbom.py'
python3 scripts/generate_sbom.py --output ./permora-lockfile-sbom.cdx.json
```

The repository CI checks the generator and the output on its ephemeral runner. **No GitHub Actions artifact is uploaded or stored**. Produce a fresh file for each reviewed release and attach it to the release through a separately approved publishing process.

## Attested scope

- Includes exact NuGet and npm dependency names/versions from the committed locks, package URLs and supported lockfile hash values.
- Includes transitive entries already present in those lockfiles, with source-location properties. Development/test dependencies are included and marked where detectable.
- Normalization and stable ordering make regenerated outputs byte-for-byte comparable when locks are unchanged.
- A green CI run proves only that this lockfile inventory was generated and parsed.

## Explicit exclusions / required release follow-up

- **No container/base-image/OS-package inventory**, no proof of what binaries are shipped, no materialized dependency graph.
- No independent license review of all transitive dependencies; npm license strings are informational and NuGet licenses are not resolved.
- No vulnerability, CVE/OSV, malicious-package, secret, provenance or signature check.
- No authoritative VEX, risk acceptance, vulnerability SLA or release attestation.
- Review the release build and the full supply chain separately; use a trusted external SBOM/SCA scanner and verify it against actual published artifacts before any 1.0 security claim.

This feature **functions** as an offline lockfile inventory. It is not an officially supported Keycloak feature, nor a complete production SBOM, nor a security certification.

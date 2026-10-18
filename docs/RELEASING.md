# Versioned Permora GitHub releases

Permora already generates **draft, signed engineering candidates** in [`release-integrity.yml`](../.github/workflows/release-integrity.yml). Those candidates include actual backend archives, SBOMs, license evidence and signed provenance. They are **not public production releases**. The separate manual **Promote independently approved Permora release** workflow publishes finished downloadable packages only if all hard gates succeed.

## Promotion steps

1. Update version in `web/package.json` and the npm lockfile as needed, complete application and documentation review, and merge into protected `main`.
2. Wait for exact-source `main` CI: core, Keycloak/LDAPS acceptance, workers/recovery, accessibility and artifact integrity. Pick the **draft** candidate tag `candidate-<SHA12>-run<run-id>` produced from that exact SHA.
3. Obtain independent security, third-party license, privacy and operational production release authorizations (see [release-checklist.md](release-checklist.md)). Do **not** approve while documented critical gates remain blocked.
4. In repository Settings → Environments, create `release-approval`, configure required human reviewers and restrict deployment branches to `main`. After obtaining the external records, set the following **environment variables** (public-safe references, no personal data):
   - `PERMORA_PUBLIC_RELEASE_SHA`: exact accepted `main` SHA.
   - `PERMORA_RELEASE_ACCEPTANCE_REF`: operational release owner's approval record reference.
   - `PERMORA_SECURITY_ACCEPTANCE_REF`: accepted penetration/security review reference.
   - `PERMORA_LICENSE_ACCEPTANCE_REF`: third-party redistribution approval reference.
   - `PERMORA_PRIVACY_ACCEPTANCE_REF`: approved privacy/retention/DPIA decision reference.
5. Manually run the promotion workflow on `main`, supplying a previously unused SemVer `vX.Y.Z` matching `web/package.json`, and the exact candidate tag. The workflow blocks on any missing approval, tag reuse, version mismatch, stale candidate, failed exact SHA checks, failed checksum or invalid Sigstore attestation.
6. GitHub Releases receives the original checked and attested archives for `Permora.Api`, `Permora.Worker` and `Permora.Provisioner`, supporting SBOMs/notice inventories, original candidate attestation and `PUBLIC-RELEASE-ACCEPTANCE.txt`. Validate all published bytes with `sha256sum -c RELEASE-SHA256SUMS.txt`.

The signed candidate manifest retains `productionApproved: false` as historical pre-approval evidence. The **separate** release owner authorization is recorded explicitly; never rewrite previous cryptographically signed evidence to claim it was originally approved.

The workflow does **not** publish the private Angular npm package, invent Microsoft AD or official Keycloak compatibility certification, or assume CI means GDPR or production readiness. There is no public release until the independent requirements are actually met. Public container registry publication is out of scope for this promotion path and needs its own build, SBOM and verification process.

Enable release immutability and Git tag protection in GitHub Settings; those controls are not enforced merely by the workflow file.

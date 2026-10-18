# SBOM, license evidence and signed development candidates

**Status 18 October 2026:** [Artifact-integrity run #39117753278](https://github.com/Cenn0/Permora/actions/runs/39117753278)
succeeded for commit `c0a5e0eed07006c20373ea08ff9b162ef831da52`.
The build created a Sigstore-attested **draft prerelease**
`candidate-c0a5e0eed070-run39117753278`. This proves the builder,
source identity and attested bytes for the named candidate; it is **NOT**
a production/security or legal approval.

## Two levels of evidence

| Pipeline | Included | Not proved |
| --- | --- | --- |
| Offline lockfile inventory (`scripts/generate_sbom.py`) | NuGet/npm direct and transitive package entries in committed locks, deterministic CycloneDX | Shipped binary/OS packages, legal license compliance, vulnerabilities |
| Full candidate evidence (`scripts/build-release-evidence.py`) | Three actual images (API/Worker/Provisioner), Syft CycloneDX + SPDX, combined dependency references, OS/runtime/component inventory, production frontend package closure, framework-dependent application archives, per-file hashes, license declarations and notice files | Production-cluster deployment, complete legal review of every license, advisories/vulnerability remediation, independent SLSA/security certification |

The scanner installer `scripts/install-syft.py` requires a pinned
upstream checksum-manifest digest and verifies the downloaded Syft binary.
The candidate builder rejects duplicate/dangling SBOM references, archives
with unsafe source names and missing actual DLLs. Built image identifiers
and layers are recorded, but **the OCI images themselves are not uploaded
or published** as release tarballs. Framework-dependent archives require
a compatible runtime and are not a standalone secure deployment.

### Local reproduction in an isolated build environment

```bash
python3 -m unittest discover -s scripts -p test_release_evidence.py
python3 scripts/install-syft.py /tmp/permora-scanner
python3 scripts/build-release-evidence.py \
  --syft /tmp/permora-scanner/syft --output /tmp/permora-candidate
(cd /tmp/permora-candidate && sha256sum -c SHA256SUMS)
```

The output directory must be empty. Inspect all candidate files before
signing. PR CI uses read-only checkout, no production secrets, no release
permissions and **does not sign or upload**. Only a trusted `main` workflow
with exact-source required tests may use GitHub OIDC/Sigstore attestation.
It creates a **draft prerelease** with explicit non-production notes.

## Mandatory outstanding license/security review

`application-licenses.json` records declared licenses and a
`manualReviewRequired` set. A declaration or included license text is
not legal permission to distribute every asset, use a trademark, or satisfy
notice obligations automatically. All unknown, conditional, file-based or
otherwise atypical license terms require recorded manual review.

Before approving any public production release, verify:

1. Exactly which binaries, third-party assets, container/base OS layers and
   license notices are **shipped** versus build-only tooling.
2. Full dependency license compatibility, attribution/NOTICE requirements,
   modified-file obligations and any AGPL/GPL/MIT/Apache/MPL conditions.
   The isolated accessibility CI tool may have a distinct license from
   published application code; evaluate distribution boundaries explicitly.
3. Current dependency and container advisories, vulnerability exploitability,
   remediation, waiver expiry, incident/disclosure policy and signed artifacts.
4. The source SHA, workflow identity, SHA256SUMS, provenance bundle and every
   candidate archive hash before deployment; regenerate on any code change.
5. Security/operations/legal release owner approvals and recorded supported
   Keycloak/deployment versions.

A signature binds the **candidate bytes to a workflow**, not the quality
of its dependencies, completeness of legal notices, or the absence of
vulnerabilities. Do not equate a green `Permora artifact integrity` workflow
with all release requirements being satisfied.

[Release checklist](release-checklist.md) · [Security](security.md) ·
[Operations](operations.md) · [Architecture](architecture.md).

## Fresh vulnerability scanning before signed draft candidates

The `Permora artifact integrity` workflow additionally installs
[Anchore Grype](https://github.com/anchore/grype) version **0.120.1**
with immutable manifest and Linux-amd64 archive SHA-256 pins. Grype is
an Apache-2.0 open-source scanner and is used as an **ephemeral CI
tool**, not distributed with the application.

Immediately after generating actual application/image SBOMs, and
**before** OIDC/Sigstore signing, the scanner:

1. Authenticates the original candidate's checksums and installs a fresh
   Grype vulnerability database. DB download, staleness or scanner
   failures **fail closed**; no signed candidate is produced.
2. Scans the four scoped inventories:
   `api.image.cdx.json`, `worker.image.cdx.json`,
   `provisioner.image.cdx.json`, `frontend.cdx.json`.
3. Writes the complete scanner JSON and a
   `vulnerability-review.json` summary, including all High/Critical
   records requiring **manual** assessment, vulnerability/fix metadata
   and scanner source. Rehashes all evidence into `SHA256SUMS` before
   any signature is issued.
4. **Blocks candidate signing** when Grype detects known **fixable
   High or Critical** vulnerabilities. Items with no published fix,
   uncertainty about exploitability, affected/distributed scope and
   other severities are **not** silently approved: they remain listed
   for review before any production release.

A green scan is **not** a guarantee that there are no vulnerabilities.
Scanner coverage, advisory feeds, package inventories, false positives,
non-fixable findings, fix-state classifications and VEX applicability
need qualified human assessment. Freshness requires a functioning
network to the trusted Grype database. Existing signed development
candidates predating this gate do not acquire this evidence retroactively.

The concrete release owner must additionally review license notices,
other license obligations, threat-model impact, security advisories,
remaining unresolved findings, image provenance and deployment artifacts.

Local runner reproduction after a candidate is built:

```bash
python3 -m unittest discover -s scripts -p test_vulnerability_policy.py
python3 scripts/install-grype.py /tmp/permora-scanner
python3 scripts/scan-release-vulnerabilities.py \
  --grype /tmp/permora-scanner/grype \
  --candidate /tmp/permora-candidate
(cd /tmp/permora-candidate && sha256sum -c SHA256SUMS)
```

This check is intentionally independent of a production-release
authorization and may fail due to genuine actionable findings.

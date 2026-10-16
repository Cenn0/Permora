#!/usr/bin/env bash
# Manual verification only. Never creates GitHub Actions or other CI configuration.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 scripts/check-repository.py
(cd web && node --experimental-strip-types --test tests/*.test.mjs)
if [[ "${1:-}" == "--lightweight" ]]; then
  echo "PASS: structural and isolated frontend-logic checks only; backend, Angular and Keycloak NOT tested."
  exit 0
fi
command -v dotnet >/dev/null || { echo "BLOCKED: .NET 10 SDK missing." >&2; exit 2; }
command -v npm >/dev/null || { echo "BLOCKED: npm missing." >&2; exit 2; }
if [[ ! -f web/package-lock.json ]]; then
  echo "BLOCKED: npm lockfile missing. Run scripts/bootstrap-locks.sh in a network-enabled environment, review and commit the generated lockfiles." >&2
  exit 2
fi
while IFS= read -r project; do
  [[ -f "$(dirname "$project")/packages.lock.json" ]] || {
    echo "BLOCKED: NuGet lock missing for ${project}. Run scripts/bootstrap-locks.sh." >&2
    exit 2
  }
done < <(find src tests -name '*.csproj' | sort)
if [[ -z "${PERMORA_TEST_POSTGRES:-}" ]]; then
  echo "BLOCKED: PERMORA_TEST_POSTGRES must point to an isolated permora_test_* PostgreSQL database for the full suite." >&2
  exit 2
fi
dotnet restore Permora.slnx --locked-mode
dotnet build Permora.slnx --no-restore -c Release
dotnet test tests/Permora.Tests/Permora.Tests.csproj --no-build -c Release --logger 'trx;LogFileName=permora.trx'
(cd web && npm ci && npm run build && npx playwright install chromium && npm run test:e2e)
echo "Full build and tests finished. Live Keycloak FGAP, token isolation, and operational recovery STILL need explicit acceptance."

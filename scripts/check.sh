#!/usr/bin/env bash
# Manual local checks only. No CI configuration or remote execution.
set -euo pipefail
cd "$(dirname "$0")/.."
python3 scripts/check-repository.py
(cd web && node --experimental-strip-types --test tests/*.test.mjs)
if [[ "${1:-}" == "--lightweight" ]]; then
  echo "Lightweight checks only. C#, Angular templates and real integration were NOT tested."
  exit 0
fi
command -v dotnet >/dev/null || { echo "BLOCKED: .NET 10 SDK is required." >&2; exit 2; }
dotnet restore Permora.slnx --use-lock-file
dotnet build Permora.slnx --no-restore -c Release
dotnet test tests/Permora.Tests/Permora.Tests.csproj --no-build -c Release --logger 'trx;LogFileName=permora.trx'
(cd web && npm install && npm run build && npx playwright install chromium && npm run test:e2e)
echo "Review skipped PostgreSQL tests, dependency locks, audit results and Keycloak acceptance gates before release."

#!/usr/bin/env bash
# Explicit one-off dependency resolution. Requires network; CI may also generate diagnostic lock artifacts.
set -euo pipefail
cd "$(dirname "$0")/.."
command -v dotnet >/dev/null || { echo 'Install the supported .NET 10 SDK first.' >&2; exit 2; }
command -v npm >/dev/null || { echo 'Install a supported Node.js/npm version first.' >&2; exit 2; }
dotnet restore Permora.slnx --use-lock-file
(cd web && npm install --package-lock-only --ignore-scripts && npm ci)
echo 'Review generated packages.lock.json files, web/package-lock.json and the complete dependency license/security report before committing.'

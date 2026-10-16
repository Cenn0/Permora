# Permora CI: Tests, Belege und Grenzen

## Neuer Stand: echte Locks, ausschließlich lesende Standard-CI

Alle sieben C#-Projekte besitzen ein auf dem isolierten GitHub-Runner erzeugtes `packages.lock.json`; das Angular-Frontend besitzt `web/package-lock.json`. Der reguläre Workflow führt nun `dotnet restore --locked-mode` und `npm ci` aus. Es existiert **kein** dauerhafter CI-Job mit Schreibberechtigungen, Push/Deploy oder Zugriff auf produktive Secrets. Die temporäre One-shot-Lockfile-Übernahme wurde danach entfernt. Ein anschließender vollständiger PR-Run muss den grünen Status erneut nachweisen. Die in dieser Datei erwähnten roten Lockfile-Gates gehören zu den **historischen** Läufen vor der Lockfile-Übernahme.


## Auslöser und Sicherheit

Workflow: `.github/workflows/ci.yml`. Läuft auf Pull Requests gegen `main`, Pushes auf `main` und manuell über **Actions → Permora CI → Run workflow**. Kein Deployment, kein Merge-Bot, kein `pull_request_target`. Workflow-Token hat nur `contents: read`, Checkout persistiert keine Credentials. PR-Code kann Build- und Testprozesse ausführen; deshalb niemals Produktionssecrets an diese Jobs geben.

Docker-Dienste und Passwörter sind **ausschließlich** für die ephemeren Runner bestimmt. PostgreSQL und Keycloak laufen in isolierten Testjobs. Keycloak wird mit `start-dev` auf `127.0.0.1:18080` veröffentlicht. Niemals diese Konten, URLs oder Einstellungen im Unternehmen verwenden.

## Abnahmematrix

| Job | Echte Ausführung | Was nicht nachgewiesen wird |
|---|---|---|
| `structure` | Repository-/CI-Kontrakt, Dateiformate, Python/Shell-Syntax, isolierte TypeScript-Logik | C#-/Angular-Compile oder Deployment |
| `dotnet` | Restore, Release-Build sämtlicher C#-Projekte, xUnit und **echte PostgreSQL 18**-Integration; TRX-Nachprüfung verweigert Skips; zusätzlicher ASP.NET-HTTP-Test mit CSRF, Registrierung, fremden Anwendungs-IDs und zwei Development-Testidentitäten | Produktiver OIDC-/MFA-BFF-Login, Live-FGAP, Cluster-/Failover |
| `angular` | Node 24, Abhängigkeitsinstallation, Angular-Produktionsbuild, Node-Logiktests, Playwright Desktop und Mobile, npm Prod-Audit | Browser mit echtem Keycloak/OIDC-BFF; Playwright nutzt API-Fixtures |
| `keycloak` | Keycloak 26.8.1 REST mit zwei Clients, Rollen, Composite/User-/Group-Mappings, eingeschränkter Token-Audience sowie **echtem FGAP v2 mit zwei unabhängigen Servicekonten**: eigener Client erlaubt, fremde Clients und Realm-Konfiguration verweigert | Keine vollständige FGAP-v2-Zielbenutzer-/Gruppen-Mapping-Matrix oder produktiver Portal-End-to-End-Login; Admin wird nur für ephemeres Setup verwendet |
| `container` | Full locked multi-stage Docker build for Angular and ASP.NET API, verifies packaged files and non-root runtime user; **no registry upload or deployment** | A successful image build is not a deployment test or operational approval |
| `dependency-locks` | Überprüft, dass echtes `web/package-lock.json` und pro C#-Projekt echte `packages.lock.json` eingecheckt sind | SBOM- und Lizenzbewertung sämtlicher transitiver Abhängigkeiten |
| `scope-summary` | Sichtbare GitHub-Actions-Zusammenfassung der einzelnen Jobzustände und Grenzen | Kein eigener Freigabeprüfer |

Die ursprünglichen diagnostischen Läufe erzeugten echte Lockfiles als Artefakte. Die Dateien wurden danach auf einem isolierten GitHub-Runner aufgelöst und übernommen; nun ist ein **gesperrter** Restore mit `--locked-mode`/`npm ci` verpflichtend. Die CI schreibt keine Änderungen auf `main`.

## Erste Einrichtung

1. PR gegen `main` öffnen und die Jobzustände abwarten. Kein Token, Keycloak-Secret oder persönliches Produktionskonto für die CI nötig.
2. **Wenn Lockfiles fehlen:** `dotnet-test-and-dependencies` und `angular-browser-and-dependencies` unter dem Workflow-Run herunterladen, NuGet-/npm-Auflösung, Lizenz- und Security-Advisories prüfen, die benötigten Lockfiles ins Repository übernehmen.
3. Bei fehlgeschlagenen Builds die Logs und TRX/Playwright-Artefakte öffnen, Quellcode korrigieren und PR erneut testen.
4. Bei grünem `keycloak`: Rollen/Token-Vertrag und clientbezogene FGAP-v2-Isolation zweier minimal berechtigter Servicekonten bestätigt. Zusätzliche Prüfungen für vollständiges Zielbenutzer-/Gruppen-Mapping, LDAP-Sicht und produktive Anwendungs-SSO bleiben offen.
5. Erst bei vollständig bestandenen technischen Tests, minimalen Keycloak-Rechten, überprüftem BFF-OIDC/MFA, Betriebs- und Recovery-Nachweis eine Pilotfreigabe diskutieren.

## Lokale Ausführung

```bash
bash scripts/check.sh --lightweight
# Nach Bereitstellung von .NET 10, Node 24 und isolierter PostgreSQL-Testdatenbank:
bash scripts/bootstrap-locks.sh
# erzeugte Lockfiles prüfen und committen
export PERMORA_TEST_POSTGRES='Host=127.0.0.1;Database=permora_test_local;Username=...;Password=...'
bash scripts/check.sh
# separaten Keycloak-Testserver nur lokal starten, nie an Produktion richten:
PERMORA_TEST_KC_URL=http://127.0.0.1:18080 \
PERMORA_TEST_KC_USERNAME=ci-admin \
PERMORA_TEST_KC_PASSWORD='<only-ephemeral-value>' \
python3 scripts/ci-keycloak-contract.py
```

Bei der ersten Ausführung werden die tatsächlich erreichten Testzahlen und Compiler-/Integrationsfehler im GitHub-Run protokolliert. Ein erfolgreicher Workflow wurde beim Hinzufügen dieser Konfiguration **nicht vorausgesetzt**.

## Offene externe Nachweise

- Die FGAP-v2-Client-Isolation ist jetzt real getestet. **Weiter offen:** kombinierte Client-`map-roles`- und Zielbenutzer-/Gruppen-Rechte, LDAP-Sicht und vollständige negative Rollen-Zuweisungsmatrix.
- End-to-end Anmeldung am ASP.NET-BFF mit Keycloak-OIDC inklusive korrektem Issuer, JWT-Audience, CSRF, Sessionablauf und MFA-/Step-up-ACR-Prüfung.
- Dynamische Gruppen-/SoD-Änderungen aus LDAP/AD, fehlende Benutzer, Failover/Timeout und garantierter Widerruf bei bereits ausgestellten JWTs.
- Sicherheit, Daten-/Audit-Retention, Lizenz/SBOM, Last, HA, Disaster Recovery und externer Pentest.

**Grüne CI bedeutet keine Unternehmens- oder Produktionsfreigabe.**

## Nachweis des ersten erfolgreichen Funktionsdurchlaufs

[GitHub Actions #38912134224](https://github.com/Cenn0/Permora/actions/runs/38912134224): Struktur erfolgreich; **102/102 C#-Tests einschließlich PostgreSQL** sowie gesonderter gestarteter ASP.NET-HTTP-Smoke-Test erfolgreich; Angular-Produktionsbuild, 32 isolierte Frontend-Unit-Tests und 8 Playwright-Tests erfolgreich; Keycloak 26.8.1 Rollen-/OIDC-Vertrag sowie clientbezogene FGAP-v2-Isolation erfolgreich; npm-Produktion-Audit ohne gemeldete Schwachstellen. **`dependency-locks` fehlgeschlagen:** echte aufgelöste, geprüfte NuGet/npm-Lockfiles sind noch nicht eingecheckt. Alle anderen Jobs waren grün. [Prüfprotokoll mit Grenzen](evidence/ci-2026-10-16.md).

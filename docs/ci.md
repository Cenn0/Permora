# Permora CI: Tests, Belege und Grenzen

## 22.10.2026 — Governance-Berichte und Trigger-Konsolidierung

Die [Governance-Berichte](governance-reports.md) besitzen Unit-, echte
PostgreSQL-/HTTP- sowie Browser-/Accessibility-Regressionsfälle. Ihre Summen
werden vor der Paginierung ermittelt; der Datenbanktest verwendet 211 offene
Reviews. Die zentralen Workflows bleiben unverändert verpflichtend.

Der begrenzte Lastlauf wird jetzt auch bei Änderungen an Core, Infrastructure,
Worker, zentralen .NET-Paketdefinitionen und SDK-Version ausgelöst. Zuvor
führten nur Änderungen am Lasttest-Skript oder dessen Workflow diesen
zusätzlichen Lauf aus. Er ergänzt den stets laufenden kleineren Recovery-Test.
Es bleiben öffentliche Standard-Runner und lesende PR-Berechtigungen.

Die Core-CI-Zusammenfassung trennt inzwischen implementierte isolierte
MFA-/FGAP-/Datenbank-Restore-Prüfungen von separaten System- und Artefaktläufen
sowie weiterhin offenen Betriebsabnahmen. Frühere pauschale Aussagen, diese
Prüfbereiche seien vollständig ungetestet, sind historisch. Konkrete Teststände
und Produktionsgrenzen bleiben commitbezogen nachzuweisen.

## 20.10.2026 — gemeinsamer LDAPS-/Worker-Integrationstest

[PR #60](https://github.com/Cenn0/Permora/pull/60) auf exaktem Test-SHA
`dacae649479d0b23b7c18779e3a4cd3d73b3873e`:
**alle sieben ausgelösten Workflows erfolgreich**. Der
[System-Lauf](https://github.com/Cenn0/Permora/actions/runs/39432701299)
prüft erstmals einen echten OpenLDAP-Gruppenwechsel bis zum verifizierten
Entzug durch den tatsächlich gebauten .NET-Worker, mit PostgreSQL,
SoD-Entscheidung, Audit und Keycloak-Readback. Die
[Worker-/Recovery-CI](https://github.com/Cenn0/Permora/actions/runs/39432701343)
validiert außerdem die effektive Directory-Servicekonto-Sicht, statt sich
allein auf bereits im Test gespeicherte Directory-Zeilen zu verlassen.

Aufgrund explizit deaktiviertem `FullScopeAllowed` benötigen Keycloak-
Servicekonten neben `realm-management:view-users` eine **passende
Role-Scope-Zuweisung am Service-Client**. Der reale Test hat das vorher
fehlende Token-Recht als HTTP 403 reproduziert. Dieser Fix ist eine
Test-/Konfigurationskorrektur, keine Behauptung minimaler Sichtbarkeit:
`view-users` umfasst im Realm mehr als eine einzelne Permora-Anwendung.

**Nicht bewiesen:** nicht importierter LDAP-Worker-End-to-End-Fall,
Microsoft AD, reales Netz-/TLS-/Keycloak-/DB-Failover in mehreren Zonen,
individuelle Betriebs-RPO/RTO, unabhängige Prüfungen oder Produktionsabnahme.
Die nachfolgenden Zwischenstände von 16.–18.10. bleiben historisch.


## Verbindliche aktuelle Evidenz – 18.10.2026

**Prüfstand:** [Produktionsfreigabe- und Testmatrix](release-checklist.md),
Commit `c0a5e0eed07006c20373ea08ff9b162ef831da52`.

Die fünf **erfolgreichen** `main`-Workflows dieses Commits:

- [Basis-CI](https://github.com/Cenn0/Permora/actions/runs/39117753038): .NET/Angular, PostgreSQL, OIDC-BFF, Keycloak-FGAP.
- [System-/LDAPS-Abnahme](https://github.com/Cenn0/Permora/actions/runs/39117753117): OpenLDAP über echtes TLS, importierte/nicht importierte Identitäten, Gruppenänderungen und FGAP-Negativfälle.
- [Worker-/Recovery-Abnahme](https://github.com/Cenn0/Permora/actions/runs/39117753047): zwei reale Worker, Keycloak-Netzausfälle, verlorene Antwort nach Remote-Commit, Wiederanlauf, Keycloak-/PostgreSQL-Restore.
- [Accessibility-Automatik](https://github.com/Cenn0/Permora/actions/runs/39117753107): 54 Fälle in Desktop-, Mobil- und 320px-Ansichten; kein unabhängiger Screenreader-Nachweis.
- [Artifact Integrity](https://github.com/Cenn0/Permora/actions/runs/39117753278): tatsächliche Image-/OS- und Laufzeitinventare, NuGet-/npm-Lizenzbelege, verifizierte Sigstore-Herkunft, signierter **Draft**, keine Produktionsfreigabe.

Eine echte **positive** LoA2/TOTP-Browserprüfung einschließlich starker BFF-Sitzung
ist zusätzlich durch [Run #39011230492](https://github.com/Cenn0/Permora/actions/runs/39011230492) belegt.
Ältere Aussagen in diesem Dokument über fehlendes positives MFA, sieben statt acht
Pflichtjobs oder ausschließlich vorhandene Lockfile-SBOMs beschreiben
**historische Zwischenstände**. Eine grüne CI ersetzt nicht die externe AD-,
Datenschutz-, Lizenz-, Produktions-HA- oder unabhängige Security-Abnahme.


## Nachtrag: integrierte CI, 17. Oktober 2026

**Neu:** Die nachträgliche technische CI umfasst neben der bisherigen siebenstufigen Prüfung einen achten Pflichtjob für echten Chromium-/Keycloak-/ASP.NET-BFF-OIDC-Login. Der separate [erfolgreiche PR-#13-Lauf](https://github.com/Cenn0/Permora/actions/runs/38985065471) bestätigt den **Passwort-only-Negativtest** gegen das serverseitige ACR-/auth_time-Gate, echte BFF-Sitzung und CSRF-Verarbeitung. Die [erfolgreiche PR-#18-CI](https://github.com/Cenn0/Permora/actions/runs/38985300214) enthält weitere echte HTTP-Tests gegen Cross-Team-IDORs und die Korrektur, die Fremd-App-Sichtbarkeit vor fachlicher Payload-Validierung prüft.

Die aktuelle Standard-CI prüft außerdem ein offline erzeugtes CycloneDX-*Lockfile-Inventar* und eine isolierte PostgreSQL-Dump-/Restore-Probe. Dies ist **keine** vollständige Release-SBOM oder Mehrsystem-Disaster-Recovery. Ein tatsächlich bestandenes **positives MFA-Step-up**, LDAP-/AD-Federation, unabhängiger Pentest, HA-/Lastgrenzen und Hersteller-Support für Permora werden durch diese Prüfungen **nicht** behauptet. Die folgenden Abschnitte vom 16. Oktober bleiben als historische Lauf- und Review-Nachweise erhalten.

[Release-Freigabekriterien](release-checklist.md) · [Threat Model](threat-model.md) · [Security](security.md)


## Vollständiger grüner Prüflauf – 16.10.2026

[GitHub Actions #38946163282](https://github.com/Cenn0/Permora/actions/runs/38946163282) hat **alle sieben Pflichtjobs** erfolgreich ausgeführt: C#-Release-Build und 102 xUnit-Tests mit echter PostgreSQL-Integration ohne Skips, API-HTTP-Sicherheitstest, Angular-Produktionsbuild und 32 Frontend-Logiktests sowie 8 Playwright-Tests, Keycloak 26.8.1 mit zwei delegierten FGAP-Servicekonten inklusive Zielbenutzer-/Gruppen-Positiv-/Negativfällen, gesperrte npm-/NuGet-Dependency-Restores, lokaler Docker-Build des tatsächlichen Produktionsimages und fail-closed Zusammenfassungs-Gate.

GitHub-Action-Quellcode ist auf verifizierte Commit-SHAs gepinnt. npm verwendet das eingecheckte Lockfile und begrenzte Download-Wiederholungen, aber keinen persistenten Actions-Cache. **Die CI veröffentlicht weder Container noch Releases und deployt nichts.** Weitere Freigabegrenzen: produktiver OIDC-BFF-/MFA-Flow, AD-/LDAP-Federation und deren Gruppen-Lifecycle, vollständige SoD-/Audit-/SBOM-/Lizenz-/Betriebs-/Wiederherstellungs- und Pentest-Prüfung.

[Ausführlicher Nachweis](evidence/ci-2026-10-16-final.md) · [frühere Diagnosehistorie](evidence/ci-2026-10-16.md)


## Erweiterte FGAP-v2-Zielbenutzer- und Gruppentests

Ein echter Keycloak-26.8.1-Test bestätigt jetzt zusätzlich: Client-`map-roles` allein berechtigt nicht zur Zuweisung an beliebige Benutzer oder Gruppen. Bei expliziter FGAP-Freigabe für einen Zielbenutzer bzw. eine Zielgruppe lassen sich Rollen des eigenen Clients zuweisen und wieder entziehen. Fremde Clients und nicht freigegebene Zielidentitäten bleiben gesperrt, ebenso allgemeine Änderungen am Benutzerkonto. Alle Tests laufen mit isolierten Servicekonten ohne breite Realm-Adminrollen. Externe LDAP-/AD-Federation, vollständiges produktives OIDC-BFF/MFA und betriebliche Abnahme bleiben offen.

Vier Actions sind auf bei GitHub offiziell verifizierte Release-Commit-SHAs fixiert. Für Angular wird `web/package-lock.json` mit `npm ci` verwendet; der vorherige Actions-npm-Cache ist deaktiviert. Die begrenzten Download-Wiederholungen bei Registry-Netzwerkunterbrechungen bleiben aktiv. Ein vorheriger `ECONNRESET` beim Download war kein Lockfile-Konflikt.

## Neuer Stand: echte Locks, ausschließlich lesende Standard-CI

Alle sieben C#-Projekte besitzen ein auf dem isolierten GitHub-Runner erzeugtes `packages.lock.json`; das Angular-Frontend besitzt `web/package-lock.json`. Der reguläre Workflow führt nun `dotnet restore --locked-mode` und `npm ci` aus. Es existiert **kein** dauerhafter CI-Job mit Schreibberechtigungen, Push/Deploy oder Zugriff auf produktive Secrets. Die temporäre One-shot-Lockfile-Übernahme wurde danach entfernt. Der gesperrte PR-Run #38946163282 ist inzwischen vollständig grün. Die in dieser Datei erwähnten roten Lockfile-Gates gehören zu den **historischen** Läufen vor der Lockfile-Übernahme.


## Kontingentfreier Lauf (16.10.2026)

Der [geprüfte Pull-Request-Lauf #38963609610](https://github.com/Cenn0/Permora/actions/runs/38963609610) hat alle sieben Pflichtjobs erfolgreich abgeschlossen und **keine Actions-Artefakte gespeichert**. Die Workflow-Änderung wurde in PR #10 gemergt. npm-Cache und Uploads für TRX/Playwright wurden entfernt; die Prüfungen, PostgreSQL-/Keycloak-Integration, Docker-Builds und der npm-Security-Audit bleiben erhalten. Das senkt die Cache-/Artefaktnutzung, kann aber Downloads bei jedem Lauf etwas verlängern.

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
2. **Wenn Lockfiles fehlen:** Abhängigkeiten in einer isolierten Entwicklungsumgebung auflösen, Security-/Lizenzlage überprüfen, die passenden `packages.lock.json` beziehungsweise `web/package-lock.json` lokal erzeugen und als geprüften Code-Commit einreichen. Die CI erzeugt dafür keine herunterladbaren Actions-Artefakte.
3. Bei fehlgeschlagenen Builds die Job-Logs und `GITHUB_STEP_SUMMARY` prüfen. TRX-/Playwright-Dateien entstehen nur temporär auf dem Runner und werden nicht mehr hochgeladen; für eine vollständige lokale Analyse die Tests selbst ausführen. Danach Fehler korrigieren und PR erneut testen.
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

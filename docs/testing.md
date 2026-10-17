# Teststrategie und Ausführung

## Positiver Keycloak-LoA2-/TOTP-End-to-End-Nachweis (17.10.2026)

[GitHub Actions #39011230492](https://github.com/Cenn0/Permora/actions/runs/39011230492) bestätigt die **tatsächliche** Ausführung des Keycloak-26.8.1-Browserflows in einem isolierten Runner mit temporärer Testidentität: Passwort-only bleibt auf niedrigem ACR; privilegierte Mutationen scheitern mit `step_up_required`; ein neu registrierter TOTP-Faktor wird beim erneuten Login abgefragt; der signierte `acr=2` zusammen mit frischem `auth_time` bleibt nach ASP.NET-Core-Claim-Mapping in der serverseitigen Cookie-Session; ein fremder App-Zugriff wird auch nach MFA weiter verweigert; Logout invalidiert die Sitzung. Alle **acht Pflichtjobs** des PR-Laufs sind erfolgreich.

Die Ursache des ursprünglichen E2E-Fehlers war eine standardmäßige Claim-Action, die `acr` **nach** der ID-Tokenvalidierung entfernte. Sie ist für diesen einen validierten Claim aufgehoben, **nicht** die serverseitige Sicherheitsanforderung. Die temporäre Diagnose wurde entfernt.

Dies ist **kein** Test einer realen Firmen-AD-/LDAP-Föderation oder deren MFA-Konfiguration, kein HA-/Pentest und kein Nachweis der vollständigen Unternehmens-FGAP-Matrix.

## Technischer Testnachweis – 17. Oktober 2026

[Integrierter OIDC-/MFA-Negativlauf](https://github.com/Cenn0/Permora/actions/runs/38985065471): Echter Chromium-Browser mit Keycloak-26.8.1-Login, Cookies, ASP.NET-BFF-Sitzung und serverseitigem `403 step_up_required` bei Passwort-only; dazu Builds, xUnit/PostgreSQL, Angular/Playwright, FGAP-Contract, Docker und Lockfile-Gates. [Cross-Team-IDOR-Lauf](https://github.com/Cenn0/Permora/actions/runs/38985300214): direkter REST-Zugriff auf fremde Anwendungs-/Operation-/Audit-/Review-IDs führt in den getesteten Fällen zu HTTP 404, verbotene zentrale Administration zu 403. Das Validierungs-Reihenfolgeproblem wurde korrigiert. Die CI enthält außerdem den isolierten PostgreSQL-Restore- und den offline SBOM-Komponententest.

**Zum hier dokumentierten früheren Negativlauf zusätzlich nachgewiesen:** positiver TOTP-Faktor im isolierten Keycloak-26.8.1-Test (siehe oben). **Weiter unbewiesen:** MFA mit dem tatsächlichen Unternehmens-Identity-Provider, volle produktive FGAP-/LDAP-Matrix, verteilte Recovery und Last-/Sicherheitstests. Die älteren Testzahlen in den nachfolgenden Abschnitten beziehen sich auf konkrete historische Workflows.


**Aktueller Vollnachweis der vorhandenen CI:** [Run #38946163282](https://github.com/Cenn0/Permora/actions/runs/38946163282), 7/7 Pflichtjobs grün, 102/102 xUnit ohne Skips einschließlich PostgreSQL, 32 isolierte TypeScript-Tests, 8/8 Playwright, echte Keycloak 26.8.1 FGAP-Client- und User-/Group-Positiv-/Negativfälle, gesperrte Dependency-Restores und tatsächlicher Docker-Image-Build. [Evidenz und offene Grenzen](evidence/ci-2026-10-16-final.md).


**Container-Test:** Die CI baut das tatsächliche Multi-Stage-Image (`deploy/Dockerfile`) ausschließlich aus gesperrten NuGet-/npm-Abhängigkeiten und prüft, dass die API-DLL, die Angular-Startseite und ein Nicht-Root-Runtime-User vorhanden sind. Das ist keine Docker-/Kubernetes-Betriebsabnahme.

## FGAP-Negativtests mit echten Zielidentitäten

Die Keycloak-26.8.1-Integration prüft zusätzlich user- und group-spezifische FGAP-Rechte, positive und negative Zuweisungen, fremde Clients sowie den tatsächlichen Rollenentzug. Eine bewusst eingesetzte Zielgruppen-`manage`-Berechtigung ist weiter gefasst als reine Rollenzuweisung; sie darf nicht als unabhängig minimaler Gruppen-Mapping-Scope ausgegeben werden. Die Permora-API muss solche administrativen Operationen serverseitig weiter beschränken. Produktives AD-/LDAP, BFF-OIDC/MFA und Load/Recovery bleiben eigene Gates.

## Reproduzierbarkeit ab diesem Stand

Die tatsächlich aufgelösten NuGet- und npm-Lockfiles sind eingecheckt. In der Standard-CI laufen `dotnet restore Permora.slnx --locked-mode` und `npm ci`. Fehlende, inkonsistente oder nicht zur Manifestversion passende Locks brechen ab. Der zuvor rote Diagnoselauf bleibt als historischer Nachweis erhalten, und der neue Lockfile-Stand wurde in GitHub Actions #38946163282 bereits mit vollständigen Release-Builds überprüft.


## Was tatsächlich läuft und was nur vorbereitet ist

Die mitgelieferte Evidenz dokumentiert **32 erfolgreiche Node-Tests** der puren TypeScript-View-Model-Funktionen. Diese Tests benötigen keine Angular-Installation. Sie prüfen Labels, Fehlerbehandlung, sichere Zahlen-/Prozentdarstellung, Ablaufdatumsvalidierung und Idempotenzschlüssel. Sie beweisen weder gerenderte Angular-Templates noch .NET-Autorisierung.

Historisch lagen zunächst nur die C#-Testquellen vor. Inzwischen hat [CI-Lauf #38912134224](https://github.com/Cenn0/Permora/actions/runs/38912134224) **102/102 xUnit-Tests**, einschließlich echtem PostgreSQL-Test ohne Skips, erfolgreich ausgeführt. Darin enthalten: Policy-/Isolationsfälle, Rollen-/Gruppengrenzen, Selbsteskalation, SoD, Revision/Idempotenz, Freigaben, Remote-Retry, Reconciliation, Ablauf/Review, Audit und SDK sowie HTTP-Connector-Vertragssimulationen. Fake-Connector-Tests ersetzen kein echtes Keycloak.

PostgreSQL-Tests brauchen `PERMORA_TEST_POSTGRES`; der Datenbankname muss mit `permora_test_` beginnen. Jeder Test arbeitet in einem zufälligen eigenen Schema und entfernt dieses. Niemals produktive Credentials oder Datenbanken angeben. Ohne Variable melden Tests einen Skip, nicht einen bestandenen Integrationslauf.

Playwright besitzt Desktop-Chromium- und Mobile-Projekte. Die vorhandenen Browserfälle verwenden explizite API-Fixtures für Layout, fehlende Authentifizierung, Scope und Fehlermeldungen. Serverautorisierung muss zusätzlich direkt über echte HTTP-Requests geprüft werden. Die Playwright-Fälle wurden im GitHub-Runner mit Desktop- und Mobile-Projekt (**8/8**) ausgeführt. Sie verwenden ausdrücklich API-Fixtures; gesondert läuft ein echter ASP.NET-HTTP-Smoke-Test für ausgewählte Serverberechtigungen.

## Befehle

```bash
# Bereits in der Arbeitsumgebung ausführbare Teilmenge
bash scripts/check.sh --lightweight

# Erst auf einer Maschine mit .NET 10, kompatiblem Node und Netzwerk
dotnet restore Permora.slnx --use-lock-file
dotnet build Permora.slnx -c Release --no-restore
dotnet test tests/Permora.Tests/Permora.Tests.csproj -c Release --no-build
cd web
npm install
npm run build
npx playwright install chromium
npm run test:e2e
```

Für reale PostgreSQL-Prüfung eine separate Datenbank erzeugen und die Verbindungszeichenfolge lokal setzen; anschließend `dotnet test` wiederholen. Beispielname: `permora_test_local`. Die Connection String gehört nicht in Testberichte oder Commits.

## Noch zwingend nachzuweisen

- ASP.NET-HTTP-Integration: OIDC-Testissuer, Cookie-/CSRF-Verhalten, direkte Fremd-ID-Aufrufe, unbekannte JSON-Felder und Nebenläufigkeit.
- Echte Keycloak 26.8.1-Integration mit minimalen Rollenprofilen; vollständige FGAP-Negativmatrix aus [Keycloak](keycloak.md).
- Absturz vor/nach Remote-Write, verlorene Antwort, abgelaufene Freigabe, gleichzeitige Worker, echte Postgres-Sperren und Netzwerkpartition.
- Direkte versus geerbte Gruppenrollen; externe Mitgliedschaftsänderungen; vollständige Listensicht und Token-Audience.
- Last, Backlog-Fairness, Wiederherstellung, Datenbankschema-Upgrades, Accessibility und Security-Review.

Kein Coverage-Prozentsatz wird ausgewiesen, weil kein instrumentierter Gesamtlauf stattgefunden hat. Die Zahl von C#-Testmethoden ist keine Zahl bestandener Testfälle.


## Ergänzte Testquellen und manuelle Gates

`WorkflowTests` enthält Negativfälle zur Aktivierung ohne zentrale IAM-Attestierung bzw. mit falscher Client-UUID sowie einen positiven Simulationsfall. Ein Regressionstest fordert, dass ein fälliger automatischer Entzug einen älteren Entzug im Backoff überholen darf. `ConnectorTests` prüft eine Rollenlöschung bei unbekannter direkter Keycloak-Zuweisung. **Diese C#-Tests wurden im GitHub-Lauf erfolgreich ausgeführt; damit ist keine vollständige Keycloak-FGAP-Betriebsfreigabe verbunden.**

`scripts/bootstrap-locks.sh` erzeugt Lockfiles nur in einer netzwerkfähigen Umgebung; danach müssen die tatsächlichen Abhängigkeits-Lockfiles geprüft und eingecheckt werden. `scripts/check.sh` bricht ohne Lockfiles, .NET-SDK und isolierte `PERMORA_TEST_POSTGRES`-Datenbank ausdrücklich ab. `--lightweight` umfasst nur Struktur und isolierte Frontendlogik. Weder BFF-Login noch FGAP/Token-Audience können durch Mock-Tests abgenommen werden.

## CI-Evidenz (nachträglich, 16.10.2026)

[Ausgeführte GitHub-Prüfungen](evidence/ci-2026-10-16.md): .NET-Build, 102 xUnit-Tests (0 übersprungen, darunter echte PostgreSQL), ASP.NET-HTTP-Smoke mit zwei isolierten Development-Identitäten, Angular-Produktionsbuild, 32 Frontend-Unit-Tests, 8 Playwright und Keycloak 26.8.1 Rollen-/Token-Vertrag bestanden. Der Dependency-Lock-Gate scheitert mangels eingecheckter Lockfiles. Ein echter OIDC-BFF-Flow bleibt offen; clientbezogene FGAP-v2-Rechte sind mit zwei eingeschränkten Servicekonten getestet, die vollständige User-/Group-Zielmapping-Matrix jedoch nicht.

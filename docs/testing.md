# Teststrategie und Ausführung

## Reproduzierbarkeit ab diesem Stand

Die tatsächlich aufgelösten NuGet- und npm-Lockfiles sind eingecheckt. In der Standard-CI laufen `dotnet restore Permora.slnx --locked-mode` und `npm ci`. Fehlende, inkonsistente oder nicht zur Manifestversion passende Locks brechen ab. Der zuvor rote Diagnoselauf bleibt als historischer Nachweis erhalten, und der neue Lockfile-Stand muss noch gegen vollständige Release-Builds geprüft werden.


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

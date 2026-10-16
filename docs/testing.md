# Teststrategie und Ausführung

## Was tatsächlich läuft und was nur vorbereitet ist

Die mitgelieferte Evidenz dokumentiert **32 erfolgreiche Node-Tests** der puren TypeScript-View-Model-Funktionen. Diese Tests benötigen keine Angular-Installation. Sie prüfen Labels, Fehlerbehandlung, sichere Zahlen-/Prozentdarstellung, Ablaufdatumsvalidierung und Idempotenzschlüssel. Sie beweisen weder gerenderte Angular-Templates noch .NET-Autorisierung.

Die C#-Suite liegt als Testquellcode vor, wurde aber mangels SDK/Restore nicht ausgeführt. Darin enthalten: Policy-/Isolationsfälle, Rollen-/Gruppengrenzen, Selbsteskalation, SoD, Revision/Idempotenz, Freigaben, Remote-Retry, Reconciliation, Ablauf/Review, Audit und SDK sowie HTTP-Connector-Vertragssimulationen. Fake-Connector-Tests ersetzen kein echtes Keycloak.

PostgreSQL-Tests brauchen `PERMORA_TEST_POSTGRES`; der Datenbankname muss mit `permora_test_` beginnen. Jeder Test arbeitet in einem zufälligen eigenen Schema und entfernt dieses. Niemals produktive Credentials oder Datenbanken angeben. Ohne Variable melden Tests einen Skip, nicht einen bestandenen Integrationslauf.

Playwright besitzt Desktop-Chromium- und Mobile-Projekte. Die vorhandenen Browserfälle verwenden explizite API-Fixtures für Layout, fehlende Authentifizierung, Scope und Fehlermeldungen. Serverautorisierung muss zusätzlich direkt über echte HTTP-Requests geprüft werden. Auch die Playwright-Fälle wurden hier nicht ausgeführt.

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

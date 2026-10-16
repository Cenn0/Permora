<p align="center"><img src="web/public/brand/permora.svg" width="78" alt="Permora: fünf farbige Module als P"></p>
<h1 align="center">Permora</h1>
<p align="center">Anwendungen im Team verwalten. Rechte gezielt delegieren.</p>

**Permora ist ein eigenständiges C#-/Angular-Portal für delegierte Keycloak-Verwaltung.** Ein Anwendungsteam verwaltet seine eigenen Rollen, Einzelberechtigungen und Zugriffe. Realm-Konfiguration, fremde Anwendungen, Verwaltungsmandate und Sicherheitsrichtlinien bleiben außerhalb dieses Self-Service-Bereichs.

> **Entwicklungsstand: erste Implementierung, keine Produktionsfreigabe.** Der Quellcode umfasst die zentrale Verwaltungskette einschließlich Oberfläche, Worker und Connector. Ein vollständiger .NET-/Angular-Build und die Abnahme gegen echtes Keycloak/PostgreSQL stehen noch aus. 32 eigenständig ausführbare Frontend-Logiktests wurden tatsächlich erfolgreich ausgeführt; dies ersetzt keine Backend- oder Sicherheitstests. Details: [Prüfbericht](docs/review.md), [Roadmap mit Abnahmekriterien](docs/roadmap.md).

> **Nachgehärtet, noch nicht abgenommen:** Eine zentrale IAM-Attestierung mit Nachweis ist nun zwingende Voraussetzung vor der ersten Anwendung-Aktivierung. Dringende automatische Entzüge überholen verzögerte andere automatische Entzüge. Nicht vom Portal verwaltete Keycloak-Zuweisungen verhindern eine Rollenlöschung. Die dazugehörigen C#-Testquellen sind noch **nicht ausgeführt**.

**Manuelle Lockfile-Erstellung:** `bash scripts/bootstrap-locks.sh` in einer netzwerkfähigen .NET-10-/Node-Umgebung ausführen, erzeugte `packages.lock.json` und `web/package-lock.json` auflösen, prüfen und einchecken. Danach verlangt `bash scripts/check.sh` einen gesperrten Restore und eine isolierte `PERMORA_TEST_POSTGRES`-Datenbank. `--lightweight` bleibt ohne Backend-/Angular-/Keycloak-Abnahme nutzbar. **GitHub Actions CI ist nun konfiguriert; ein erfolgreicher Gesamtlauf steht noch aus.**

## Was enthalten ist

- Teambezogener Anwendungskatalog, kontrollierte Registrierung, explizite Owner/Maintainer/AccessManager/Approver/Auditor-Mandate.
- Eigene Berechtigungen als `perm.*`-Client-Rollen; fachliche `role.*`-Rollen enthalten ausschließlich Einzelberechtigungen derselben Anwendung.
- Benutzer- und Gruppenzuweisungen, Ablaufdaten, Vier-Augen-Freigaben, Selbsteskalationsschutz, statische Funktionstrennung (SoD).
- Persistente Änderungsaufträge mit Idempotenz, Versionsprüfung, kontrollierten Wiederholungen und Rückprüfung in Keycloak. `202 Accepted` ist **kein** Wirksamkeitsnachweis.
- Rezertifizierung, ablaufbedingter Entzug, Soll-/Ist-Abgleich, anwendungsbezogenes Änderungsprotokoll mit Hashkette.
- BFF-Anmeldung per OIDC Code + PKCE, serverseitige Sessions, CSRF-Schutz, begrenzte API, getrennte Connector-Profile.
- Isolierter Provisioner bereitet **deaktivierte** Clients vor. Die zentrale IAM-Freigabe für FGAP und Aktivierung bleibt absichtlich ein eigener Schritt.
- Helles, responsives Angular-Dashboard nach Designvariante 3: farbiges P, Anwendungskarten, Kennzahlen und rechte Aktivitäten-/Aufgabenspalte. Keine fiktiven Erfolgs- oder Wachstumszahlen.

## Was ausdrücklich noch fehlt

Die vollständige ursprüngliche Enterprise-Roadmap ist **nicht** erledigt. Offen sind unter anderem die echte End-to-End-Abnahme, automatisch provisionierte FGAP-Policies, fortlaufende SoD-Auswertung externer Gruppenänderungen, unabhängige Identitätskorrelation über mehrere Issuer, ABAC/Authorization Services, AD-/HR-Schreibkonnektoren, Benachrichtigungen, manipulationsresistenter externer Audit-Speicher sowie Last-, Wiederanlauf- und Penetrationstests. Siehe [Roadmap](docs/roadmap.md). **Die CI deckt Build, Test und Keycloak-Vertragstests ab; FGAP-Minimalrechte, BFF-SSO und Betriebsfreigabe bleiben eigenständige offene Prüfungen.**

## Technische Grundlage

| Bereich | Festlegung |
|---|---|
| Backend | C# / .NET 10; SDK 10.0.402, ASP.NET Core 10.0.13 |
| Frontend | Angular 22.2.3, TypeScript 6.0.x, RxJS |
| Datenhaltung | PostgreSQL, Npgsql 10.0.3; keine direkte Keycloak-Datenbankanbindung |
| IAM-Zielversion | Keycloak 26.8.1; FGAP-Konfiguration versionsbezogen abnehmen |
| Tests | xUnit v3, PostgreSQL-Integrationstests, Node-Test-Runner, Playwright |
| Lizenz | [Apache-2.0](LICENSE) |

Angular 22 benötigt eine passende Node-/TypeScript-Kombination. Verwende **Node 24.15.0 oder eine kompatible neuere 24.x-Version** und TypeScript `~6.0.0`; ein beliebiges „neuestes TypeScript“ ist nicht automatisch kompatibel. Versionsquellen und Wartungshinweise: [Entwicklung](docs/development.md).

## Lokal starten

Voraussetzungen: .NET SDK laut `global.json`, kompatibles Node.js, Python 3, Docker mit Compose, Netzwerkzugang für Restore/Install. Die folgenden Befehle sind für Bash; sie verändern keine GitHub-Einstellungen.

```bash
python3 scripts/init-local.py
docker compose --env-file deploy/local.env -f deploy/compose.local.yml up -d --wait
export PERMORA_CONFIG="$PWD/config/local.json"
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development

dotnet run --project src/Permora.Worker -- --migrate
dotnet run --project src/Permora.Worker -- --seed-demo
```

In getrennten Terminals dieselben drei Umgebungsvariablen setzen und anschließend starten:

```bash
# API: nur Loopback, nicht öffentlich exponieren
ASPNETCORE_URLS=http://127.0.0.1:5080 dotnet run --project src/Permora.Api
# Frontend: Terminal 2
cd web && npm install && npm start
# Worker: Terminal 3, aus dem Repository-Verzeichnis
dotnet run --project src/Permora.Worker
```

Öffne `http://localhost:4200`. Die lokalen Beispieldaten zeigen vier Anwendungen mit Status **IAM-Freigabe ausstehend**, nicht erfundene produktive Rechte. Die explizit aktivierte lokale Entwicklungsanmeldung verwendet `demo-owner`, ist kein echtes SSO und ist außerhalb der Development-Umgebung verboten. Für tatsächliche Keycloak-Änderungen zuerst [IAM-Anbindung](docs/keycloak.md) durchführen. Ein gestarteter Keycloak-Container allein konfiguriert noch keinen Realm, keine Rollen und keine Delegationsrechte.

Konfigurationen und Zufallspasswörter werden nur lokal erzeugt und sind ignoriert. Das Repository enthält keine echten Zugangsgeheimnisse. Das initiale private Repository wird durch diese Implementierung nicht automatisch öffentlich.

## Manuell prüfen

```bash
bash scripts/check.sh --lightweight  # Struktur + 32 reine Frontend-Logiktests
bash scripts/check.sh                # zusätzlich Restore, C#-Build/Tests, Angular-Build und Browser-Tests
```

Die PostgreSQL-Tests benötigen eine separate Testdatenbank über `PERMORA_TEST_POSTGRES`; ohne diese Variable werden sie ausdrücklich übersprungen. Die Browser-Tests verwenden API-Fixtures und beweisen keine serverseitige Autorisierung. [Testanleitung](docs/testing.md) und [tatsächliche Ergebnisse](docs/review.md) trennen diese Ebenen.

Echte `package-lock.json`- und NuGet-Lockdateien konnten ohne Restore nicht erzeugt werden. Der erste erfolgreiche Restore muss sie erzeugen, prüfen und einchecken. Bis dahin gibt es **keinen reproduzierbar freigegebenen Release-Build**.

## Dokumentation

[CI und Prüfumfang](docs/ci.md) · [Architektur](docs/architecture.md) · [Roadmap](docs/roadmap.md) · [API](docs/api.md) · [Keycloak](docs/keycloak.md) · [Betrieb](docs/operations.md) · [Sicherheitsmodell](docs/security.md) · [Design](docs/design.md) · [Tests](docs/testing.md) · [Review](docs/review.md) · [Beiträge](CONTRIBUTING.md)

Permora ist keine offizielle Keycloak-Komponente. Die Verwendung dokumentierter Keycloak-Schnittstellen ist nicht gleichbedeutend mit Hersteller-Support für dieses Portal. Projektname und Logo sind noch nicht markenrechtlich freigegeben.

## GitHub Actions

Der Workflow [Permora CI](.github/workflows/ci.yml) startet bei Pull Requests gegen `main`, nach Pushes auf `main` und manuell per **Run workflow**. Er umfasst .NET-Release-Build/xUnit mit einer temporären PostgreSQL-Datenbank, Angular-Build/Playwright, Keycloak 26.8.1 REST-/Token-Vertragstests und getrennte Sicherheits-/Reproduzierbarkeitsprüfungen. Fehlgeschlagene Jobs erhalten Diagnoseartefakte. Ein eigener Gate schlägt fehl, solange die tatsächlichen NuGet- und npm-Lockfiles noch fehlen; andere Jobs laufen für die Fehlerdiagnose weiter. **Grünes CI bedeutet keine automatische FGAP- oder Produktionsfreigabe.** Details: [docs/ci.md](docs/ci.md).


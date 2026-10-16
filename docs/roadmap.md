# Roadmap und Umsetzungsabgleich

Stand: 16. Oktober 2026. Ausgangspunkt war ein Repository mit ausschließlich Apache-2.0-Lizenz.

**Statuslegende:** `S` = Quellcode vorhanden, Laufzeit-Abnahme offen; `T` = tatsächlich ausgeführter, eng abgegrenzter Test; `O` = offen. Ein vorhandener Test ist noch kein bestandener Test. Kein Enterprise-Meilenstein wird allein aufgrund vorhandener Dateien als produktiv abgeschlossen markiert.

| ID | Ergebnis / Anforderung | Status | Implementierung / Nachweis | Abnahmekriterium |
|---|---|---|---|---|
| M0.1 | C#-/Angular-Struktur, getrennte Prozesse | S/T | `src/`, `web/`, `Permora.slnx`; CI-Lauf #38912134224 | Release-Builds ausgeführt; reproduzierbare Locks und Release-Abnahme noch offen |
| M0.2 | Logo und ausgewählte Designvariante 3 | S | `web/public/brand/`, Dashboard/CSS | Angular-Build, Desktop/Mobile-Vergleich, Accessibility |
| M0.3 | README, Architektur, Betrieb, Tests | S | `README.md`, `docs/` | Anleitung auf frischer Maschine erfolgreich |
| M0.4 | GitHub Actions für Builds, Tests und Integration | S/T | `.github/workflows/ci.yml`; CI-Lauf #38912134224 | Funktionsjobs bestanden; Lockfile-Gate rot, FGAP gesondert |
| M1.1 | Teams, eindeutige Ownership, Verwaltungsrollen | S | Policy, ApiEndpoints, Store | zwei Teams können gegenseitig weder lesen noch schreiben |
| M1.2 | Registrierung nur durch tatsächliche Team-Owner | S | API + Transaktionsprüfung | konkurrierender Mandatsentzug vor Erstellung blockiert |
| M1.3 | Serverautorisierung jeder App-Ressource | S | `Policy.RequireVisible`, App-Transaktionen | IDOR/API-Negativtests gegen echten Server |
| M1.4 | Zentrale Mandatsverwaltung mit Audit | S | `SaveTeamAsync`, Admin-Endpunkte | kein Owner kann selbst Verwaltungsbereich erweitern |
| M1.5 | Cross-Issuer / mehrere Identitätsdomänen | O | bewusst fail-closed | authoritative Personenkorrelation und Lifecycle-Konzept |
| M2.1 | Einzelrechte erstellen/ändern/löschen | S | Policy, Connector, Angular | reale Client-Rollen mit richtigen Markern |
| M2.2 | Eigene Rollen aus Einzelrechten bilden | S | flache Composite-Abbildung | fremde IDs, Realm-Rollen, Zyklen und Leaf-Composites abgewiesen |
| M2.3 | Benutzer-/Gruppenrollen vergeben/entziehen | S | Access-Commands, Directory, Connector | echte direkte und effektive Mappings geprüft |
| M2.4 | Neue Rechte standardmäßig High-Risk | S | Policy | niedrige Einstufung ausschließlich zentral |
| M2.5 | Vollständige Authorization Services / ABAC | O | nicht als RBAC umetikettiert | Resource/Scope/Policy-Modell mit App-Enforcement |
| M3.1 | OIDC-BFF, PKCE, Cookie/CSRF | S | API, SessionStore | echte Login-, CSRF-, Ablauf- und Cookie-Tests |
| M3.2 | Keine privilegierten Tokens im Browser | S | serverseitige Profile, `SaveTokens=false` | Browser-Netzwerk-/Storage-Test |
| M3.3 | Deaktivierte Clients kontrolliert vorbereiten | S | isolierter Provisioner | echte idempotente Erstellung und Crash-Wiederholung |
| M3.4 | Automatisches FGAP-Provisioning | O | zentrale IAM-Abnahme derzeit manuell | minimale Rechte matrixweise verifiziert |
| M3.5 | MFA/Step-up technisch zwingend | O | Keycloak-Flow-Betriebsvorgabe, kein Portal-ACR-Gate | ACR/AMR und maximale Authentifizierungsdauer geprüft |
| M4.1 | Persistente Aufträge, Idempotenz, Revision | S | ChangeService, Store | PostgreSQL-Concurrency-/Rollback-Tests |
| M4.2 | Vier Augen, aktuelle Mandate, Freigabealter | S | Policy/Processor | entfernte oder deaktivierte Entscheider blockiert |
| M4.3 | Backoff und unklare Remote-Ergebnisse | S | Processor, Reconciliation | Netzwerkabbrüche vor/nach Remote-Commit geprüft |
| M4.4 | Ablauf, explizit deaktivierte Identitäten | S | Worker/Sweep | Entzug bei Restart/DB-/Keycloak-Ausfall nachweisbar |
| M4.5 | Vollständige Job-Fairness und robuste SLA | O | Grenzen dokumentiert | Last-/Starvation-/Backlog-/Dauerlauftests |
| M5.1 | Rezertifizierung und kein eigenes Bestätigen | S | ChangeService, Angular | ausstehende/abgelehnte Rechte werden entzogen |
| M5.2 | Statische SoD bei Vergaben | S | direkte und effektive Rollen | Benutzer, Gruppen und leere Gruppen geprüft |
| M5.3 | Fortlaufende SoD bei externer Gruppenänderung | O | keine Garantie | Mitgliedschaftsänderung erzeugt kontrollierte Remediation |
| M5.4 | Hashketten-Audit / Driftanzeige | S | AuditChain, Store, Connector | Persistenz, Reihenfolge und Manipulationstests |
| M5.5 | WORM/SIEM, Aufbewahrung, Export | O | Hashkette ersetzt keine externe Integrität | regulatorisch definierte Aufbewahrung/Restore/Export |
| M6.1 | Scope-korrekte Dashboard-Kennzahlen | S/T | SQL-Quellcode; 32 Utility-Tests | PostgreSQL-Abfrage + Browser-Ansicht integriert |
| M6.2 | Antrags-, Rollen-, Rechte-, Zugriffsoberflächen | S | Angular ApplicationPage | erfolgreiche und negative reale End-to-End-Flows |
| M6.3 | Tastatur, responsive Layouts, Fehlermeldungen | S | CSS/HTML, Playwright-Testquellen | Browser-/Screenreader-Abnahme ohne Überläufe |
| M6.4 | Globale Such-/Reporting-/Benachrichtigungszentrale | O | kein Fake-Menü mit vorgetäuschter Funktion | Scope-sichere Suche, Filter, Zustellung, Zustandsmodell |
| M7.1 | C# Unit-/Connector-/Workflow-Testquellen | T | `tests/Permora.Tests`; 102/102 xUnit im CI-Lauf #38912134224 | Bestehende Suite grün, vollständige User-/Group-FGAP-Matrix noch offen |
| M7.2 | PostgreSQL-Integrationstestquellen | T | PostgreSQL 18 CI-Service, TRX-Prüfung | Echter Integrationstest bestanden, 0 übersprungen |
| M7.3 | Reine Frontend-Logiktests | T | 32/32 bestanden | TAP unter `docs/evidence/` |
| M7.4 | Angular-/Browser-Testquellen | S/T | Angular-Produktionsbuild + Playwright 8/8 | Reale UI-Build-/Fixture-E2E-Tests bestanden, echtes BFF/SSO offen |
| M7.5 | Real-Keycloak-Vertragstests | S/T | `scripts/ci-keycloak-contract.py` auf Keycloak 26.8.1 | Rollen/Token und clientbezogene FGAP-v2-Isolation zweier minimal privilegierter Konten bestanden; User-/Group-Mapping-Matrix offen |
| M7.6 | Dependency-Locks, SBOM, Lizenz-/Security-Audit | O | direkte Versionen gesetzt | echte Lockfiles und transitive Abhängigkeiten geprüft |
| M8.1 | Betriebs-/Recovery-Anleitung und lokale Hilfen | S | `deploy/`, `scripts/`, Operations | Neuinstallation, Upgrade und Restore ausgeführt |
| M8.2 | Performance, HA, unabhängiger Pentest | O | keine Behauptung vorhandener Abnahme | verbindliche SLOs und nachweisbare Sicherheitsprüfung |
| M8.3 | AD-/LDAP-Schreibkonnektoren und HR-Lifecycle | O | nur Keycloak-Directory-Lesen vorhanden | führende Systeme, Korrelation, Writeback und Audit |
| M8.4 | Kubernetes/Helm, produktive Secrets/KMS | O | Container-Buildvorlage, kein produktiver Cluster | SecurityContext, Netzregeln, Key-Rotation, Restore |

## Nächste verbindliche Reihenfolge

1. Netzwerkfähige Entwicklungsumgebung bereitstellen; echte NuGet/npm-Locks erzeugen; C# und Angular kompilieren. Fehler aus diesem ersten vollständigen Build vor jedem Release korrigieren.
2. C#-Suite und PostgreSQL-Integration ausführen; anschließend API-Authentifizierung/Autorisierung einschließlich fremder IDs direkt prüfen. Mock-Connector-Erfolge zählen nicht als Keycloak-Abnahme.
3. Keycloak 26.8.1 isoliert konfigurieren; FGAP-Matrix und Token-Vertrag prüfen. Unter privilegierten Standardrollen erfolgreiche Tests sind kein Minimalrechte-Nachweis.
4. Browser- und Designabnahme, operativer Crash-/Entzugstest, Wiederherstellung und Lastgrenzen. Danach frühestens eine ausdrücklich begrenzte Pilotfreigabe.
5. Weitere IGA-/ABAC-/Cross-Issuer-Funktionen separat implementieren und prüfen. Keine Checkbox rückwirkend grün setzen, nur um „vollständig“ zu melden.

## Release-Definition

Ein Release ist erst abgenommen, wenn Quelle, Build, Integration, Sicherheitsgrenzen, Abhängigkeiten und Betriebsanleitung zusammen nachgewiesen sind. **Diesen Zustand erreicht der vorliegende Stand noch nicht.** Die GitHub-Actions-CI wurde auf späteren ausdrücklichen Wunsch ergänzt. Ihre Ergebnisse ersetzen weder das FGAP-Minimalrechte-Audit noch die produktive Betriebsabnahme.


## Nachtrag: Integrations- und Sicherheitshärtung (16.10.2026)

| ID | Ergänzung | Quellcode | Abnahme |
|---|---|---|---|
| M3.6 | Explizite zentrale IAM-Attestierung vor Aktivierung, gebunden an Keycloak-Client-UUID und Client-ID | API, Modell, Worker, Angular | .NET-Testquellen vorhanden; echte Keycloak-/FGAP-Prüfung offen |
| M4.6 | Fällige automatische Entzüge überholen andere Entzüge im Backoff; normale Mutationen bleiben geordnet | PostgreSQL, Test-Store, Worker | echte PostgreSQL-Concurrency-Tests offen |
| M2.6 | Rollenlöschung trotz unbekannter Keycloak-Benutzer-/Gruppenzuweisungen verhindern | Connector | HTTP-Mock-Testquelle vorhanden; echter Keycloak-Test offen |
| M3.7 | OIDC-Callback-Routen im Angular-Entwicklungsproxy | Proxy-Konfiguration | echter Browser-/OIDC-Login offen |
| M7.7 | Manuelle Locked-Restore-/Test-Gates und separates Lockfile-Bootstrap | scripts | abhängig von .NET-SDK, Node-/Paketnetzwerk und PostgreSQL |

**Keine Freigabe:** .NET-Build, Angular-Build, echte PostgreSQL-/Keycloak-Tests und automatisches FGAP-Provisioning wurden mangels ausführbarer Umgebung nicht abgeschlossen. 32 Frontend-Logiktests und 251 Strukturprüfungen wurden lokal bestanden; diese beweisen keine Integrationsfähigkeit. CI war zu diesem historischen Zeitpunkt ausgeschaltet; wurde später ausdrücklich eingerichtet.

## CI-Erweiterung (16. Oktober 2026)

Die zuvor offene CI-Einrichtung ist nun als **Quellcode** vorhanden. Geplante Prüfpunkte: vollständiger .NET-Build/xUnit + echte PostgreSQL-Testinstanz, Angular-Release-Build/Node-Unit-Tests/Playwright auf Desktop und Mobil, Keycloak 26.8.1 Client-Rollen-/Composite-/Mapping-/OIDC-Token-Vertrag, verpflichtende Lockfile-Gates und getrennte Testartefakte. Siehe [CI-Handbuch](ci.md).

Die Tests sind nach dem ersten GitHub-Run auszuwerten; bereits vorhandene Evidenz aus einem früheren Teiltest darf nicht zu einem bestandenen vollständigen CI-Lauf umetikettiert werden. FGAP-v2-Client-Isolation ist inzwischen getestet; vollständige Zielbenutzer-/Gruppen-FGAP-Matrix, echtes Portal-SSO/MFA, externe Directory-Gruppenänderungen, Last/HA/Recovery und Penetrationstests bleiben **nicht automatisiert abgenommen**.

## Ausgeführte CI-Nachweise – 16. Oktober 2026

Der [GitHub-Actions-Lauf #38912134224](https://github.com/Cenn0/Permora/actions/runs/38912134224) bestätigt erfolgreiche .NET- und Angular-Release-Builds, 102/102 xUnit-Tests inkl. echter PostgreSQL-Integration ohne Skips, einen gestarteten ASP.NET-HTTP-Test mit zwei getrennten Verwaltungsidentitäten, 32/32 isolierte Frontend-Logiktests, 8/8 Playwright-Tests sowie reale Keycloak-26.8.1-Rollen-/Composite-/Gruppen-/Token-Vertragstests. Der **Lockfile-Gate schlägt weiterhin fehl**, daher kein vollständig grüner Workflow und keine reproduzierbare Releasefreigabe. FGAP-v2-Client-Isolation für zwei Accounts ist erfolgreich getestet; Zielbenutzer-/Gruppen-Mapping, produktiver OIDC-/MFA-BFF-Flow und Betriebs-/Security-Abnahme bleiben offen. [Prüfdetails](evidence/ci-2026-10-16.md).

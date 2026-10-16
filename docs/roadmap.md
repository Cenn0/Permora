# Roadmap und Umsetzungsabgleich

Stand: 16. Oktober 2026. Ausgangspunkt war ein Repository mit ausschließlich Apache-2.0-Lizenz.

**Statuslegende:** `S` = Quellcode vorhanden, Laufzeit-Abnahme offen; `T` = tatsächlich ausgeführter, eng abgegrenzter Test; `O` = offen. Ein vorhandener Test ist noch kein bestandener Test. Kein Enterprise-Meilenstein wird allein aufgrund vorhandener Dateien als produktiv abgeschlossen markiert.

| ID | Ergebnis / Anforderung | Status | Implementierung / Nachweis | Abnahmekriterium |
|---|---|---|---|---|
| M0.1 | C#-/Angular-Struktur, getrennte Prozesse | S | `src/`, `web/`, `Permora.slnx` | vollständiger Restore und Release-Build |
| M0.2 | Logo und ausgewählte Designvariante 3 | S | `web/public/brand/`, Dashboard/CSS | Angular-Build, Desktop/Mobile-Vergleich, Accessibility |
| M0.3 | README, Architektur, Betrieb, Tests | S | `README.md`, `docs/` | Anleitung auf frischer Maschine erfolgreich |
| M0.4 | Keine CI | T | strukturelle Repository-Prüfung | keine Workflow-/Pipeline-Dateien |
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
| M7.1 | C# Unit-/Connector-/Workflow-Testquellen | S | `tests/Permora.Tests` | ausführbare, vollständig grüne Tests |
| M7.2 | PostgreSQL-Integrationstestquellen | S | explizite Testdatenbank | tatsächlich ausgeführt, keine stillen Skips |
| M7.3 | Reine Frontend-Logiktests | T | 32/32 bestanden | TAP unter `docs/evidence/` |
| M7.4 | Angular-/Browser-Testquellen | S | `web/e2e`, Playwright | realer Build und beide Viewport-Projekte |
| M7.5 | Real-Keycloak-Vertragstests | O | manuelle Abnahmematrix dokumentiert | reproduzierbare isolierte KC-Instanz, positive/negative Tests |
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

Ein Release ist erst abgenommen, wenn Quelle, Build, Integration, Sicherheitsgrenzen, Abhängigkeiten und Betriebsanleitung zusammen nachgewiesen sind. **Diesen Zustand erreicht der vorliegende Stand noch nicht.** Eine CI-Pipeline ist dabei ausdrücklich nicht Teil dieses Arbeitsauftrags; alle vorbereiteten Prüfungen sind lokal/manual ausführbar.

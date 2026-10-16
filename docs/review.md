# Review und Prüfergebnis

Stand: 16. Oktober 2026. Dieser Bericht trennt Quellcode-Review, tatsächlich ausgeführte Prüfungen und ausstehende Laufzeitabnahme.

## Ergebnis

Aus dem initialen Lizenz-Repository wurde eine zusammenhängende erste Implementierung mit C#-Core/API/Worker/Connector/Provisioner/SDK, Angular-Oberfläche, Tests und Dokumentation erstellt. Das Dashboard folgt der ausgewählten Variante 3. **Die gesamte Enterprise-Roadmap ist nicht abgeschlossen und die Plattform ist nicht produktiv abgenommen.**

## Im Quellcode gefunden und korrigiert

| Befund | Korrektur | Absicherung |
|---|---|---|
| Dashboard-Gesamtzahlen aus begrenzter Vorschau ableitbar | SQL zählt den gesamten zugänglichen Bestand separat | PostgreSQL-Test mit 35 Anträgen/20 Vorschau; Test noch nicht ausgeführt |
| Fehlender Directory-Eintrag könnte als gelöscht interpretiert werden | fehlende Einträge behalten ihren alten Zeitstempel; nur explizit deaktivierte Identitäten lösen Entzug aus | PostgreSQL-Regressionstestquelle |
| Späterer Auftrag könnte ältere Remote-Unklarheit überholen | Queue-Reihenfolge, Backoff und Blockierung nach Failed/Conflict | Workflow-Testquellen |
| Sauberer Abgleich könnte fälschlich einen alten Grant als erfolgreich kennzeichnen | unklare, nicht übernommene Aufträge werden ausdrücklich verworfen | Workflow-Testquelle |
| Fremde Composite-Rollen oder privilegierte Leaf-Composites | tatsächliche Client-/Entity-/Composite-Prüfung vor Vergabe | Policy-/Connector-Testquellen |
| Leere Gruppe umgeht SoD durch fehlende aktuelle Mitglieder | zusätzlich direkte Gruppenrollen auf Konflikt prüfen | spezifischer Policy-Regressionstest |
| Veraltete Gruppenmitgliedschaft bei Rollenänderung | frische und vollständige Snapshots erforderlich | Policy-Testquelle |
| HTTP 200 mit JSON null könnte als Abwesenheit gelten | nur echtes 404 bedeutet fehlende Ressource; null-Erfolg wird Fehler | Connector-Quellcode-Review; echter Server-Test offen |
| Entfernte/deaktivierte Entscheider könnten alten Auftrag ausführen | aktuelle Mandate plus aktive frische Operator-Identität vor Remote-Schritt | Workflow-Regressionstestquelle |
| Import/Rebinding trotz bereits vorhandener Permissions | Rebinding auch bei Permissions gesperrt | API-Quellcode-Review |
| Team-Owner zwischen Prüfung und App-Erstellung entzogen | Teamzeile in Erstellungstransaktion sperren und Mandat erneut prüfen | PostgreSQL-Concurrency-Abnahme noch offen |
| Unterschiedliche Schreibweise kanonischer Subject-Hashes | normalisieren, case-insensitive Duplikate ablehnen | API-Quellcode-Review |
| OIDC-Sitzung lieferte andere Admin-Eigenschaft als das Frontend erwartet | Vertrag auf `administrator` vereinheitlicht | statischer Vertragsabgleich |
| Logout-XHR würde zu externer SSO-Seite umgeleitet | bewusster lokaler BFF-Logout | Browser-/SSO-Abnahme offen |
| Alte Suchantwort kann nach App-Wechsel erscheinen | versionsgebundene Such-/Ladeergebnisse und Reset beim Wechsel | Quellcode-Review, Browser-Regression noch auszuführen |
| Readiness prüfte nur Verbindungsaufbau | Datenpool wiederverwenden und Migration prüfen | echte DB-Abnahme offen |
| Teilweise Demo-Erstellung nicht wiederholbar | vorhandene Teams/Apps getrennt erkennen | Quellcode-Review |
| Fehlerhaft maskierte Quotes im Docker-Entrypoint | Exec-Form als gültiges JSON mit zitiertem Assembly-Pfad korrigiert | tatsächlich ausgeführte JSON-/Entrypoint-Strukturprüfung; Container-Build weiterhin offen |

## Tatsächlich ausgeführte Prüfungen

- **32 Node-Tests der puren TypeScript-Frontend-Logik: bestanden.** TAP unter [evidence/frontend-logic.tap](evidence/frontend-logic.tap).
- Strukturprüfung zum damaligen Stand: JSON/XML, Projektverweise, interne Dokumentlinks, Original-Lizenzhash und noch keine CI-Dateien. Ergebnis unter [evidence/repository-checks.json](evidence/repository-checks.json).
- TypeScript-Syntaxtranspilation und isolierte Typprüfung von Models/View-Model mit dem vorhandenen TypeScript 5.8.3. Das ist **kein Angular-22-/Template-Build**. Siehe [evidence/typescript-checks.txt](evidence/typescript-checks.txt).

## Nicht ausgeführt / nicht nachgewiesen

.NET-SDK, Docker und PostgreSQL-Server fehlten in der Arbeitsumgebung; Paketdownload war nicht möglich. Installiert waren Node 22.16.0 und TypeScript 5.8.3, nicht die für Angular 22 benötigte Buildkombination. Deshalb wurden C#-Build, xUnit, echte PostgreSQL-Tests, Angular-Build, Playwright, echte Keycloak-FGAP-/Token-Integration und Container-Build **nicht** ausgeführt. Es wird weder „0 Fehler im Backend“ noch ein erfundener Coverage-Wert behauptet.

Keine echten Lockfiles/SBOMs konnten aus einem Restore gewonnen werden. Die enthaltenen Tests sind umfangreiche Testquellen, aber nicht automatisch grüne Nachweise. Mögliche Compiler-, Analyzer-, Dependency- oder Integrationsfehler müssen beim ersten vollständigen Lauf noch korrigiert werden.

## Verbleibende Architektur-/Betriebsgrenzen

1. FGAP-Aktivierung und relevante vollständige Listensicht werden zentral manuell abgenommen; Filterung kann sonst falsche Driftfreiheit vortäuschen.
2. Rolle/Gruppe und Portal-DB bilden keine gemeinsame atomare Transaktion. Bestehende JWTs widerrufen sich nicht allein durch Entfernen eines Mappings.
3. Shared Realm-Profile sind keine unabhängige per-App-Containment-Garantie bei kompromittiertem Worker.
4. Cross-Issuer-Personenkorrelation, kontinuierliche externe Gruppen-SoD, Step-up-Gate, vollständige IGA/ABAC-Connectoren und externe unveränderliche Audit-Ablage sind offen.
5. Last-/HA-/Fairness-/Recovery-Abnahme, Datenschutz-Retention und unabhängiger Pentest fehlen.
6. Das ursprüngliche Repository bleibt privat; Veröffentlichung und History-Bereinigung wurden nicht durchgeführt; CI wurde erst in einem späteren Durchlauf hinzugefügt.

**Nächster fachlich notwendiger Schritt ist die vollständige Build-/Integrationsabnahme, nicht die Behauptung einer fertigen Enterprise-Plattform.** Die [Roadmap](roadmap.md) enthält jede verbleibende Anforderung und ihr Abnahmekriterium.


## Zweiter Härtungsdurchlauf (16.10.2026)

Im Quellcode zusätzlich korrigiert: Aktivierung vor zentraler, auditierter IAM-Freigabe; ein älterer Entzug im Retry-Backoff blockiert keine fälligen weiteren automatischen Entzüge; Rollen mit tatsächlich vorhandenen Keycloak-Zuweisungen können nicht blind gelöscht werden; fehlende Keycloak-Flow-Angaben gelten nicht mehr als sichere Konfiguration; OIDC-Callback-Routen werden im lokalen Angular-Proxy weitergeleitet. Passende C#-Regressionstestquellen sind ergänzt.

**Ausgeführte Tests:** 32/32 isolierte Node-Tests erfolgreich, 243/251 statische Strukturprüfungen erfolgreich, isolierte TypeScript-Typprüfung von Models/View-Model erfolgreich. **Nicht ausgeführt:** .NET-Build, xUnit, PostgreSQL, Angular-Template-Build, Playwright, echtes Keycloak/FGAP, Integration und Security-/Recovery-Abnahme. Keine erfundenen Lockdateien, Testergebnisse oder Sicherheitszusagen. Vor einem Pilot müssen die vorhandenen Testquellen erst mit echten SDKs und Diensten ausgeführt und Fehler korrigiert werden.

## CI-Konfiguration (nachträglicher Änderungsstand)

Die Workflows und Testhilfen sind neu im Repository. Siehe [CI-Testmatrix](ci.md). Ein GitHub-Workflow, der vorhandene Testquellen ausführt, ist **noch kein bestandenes Testergebnis**. Die historische Teiltestevidenz oben bleibt als damaliger Stand erhalten. Insbesondere FGAP-Minimalrechte und vollständige Produkt-/Betriebssicherheit werden nicht durch Admin-API-Smoke-Tests belegt.

## Tatsächliche GitHub-Actions-Ergebnisse – 16. Oktober 2026

Die historische Nicht-Ausführbarkeit in der vorherigen Arbeitsumgebung wurde durch GitHub-Runner teilweise aufgehoben. [Lauf #38912134224](https://github.com/Cenn0/Permora/actions/runs/38912134224) hat die .NET- und Angular-Produktionsbuilds erfolgreich abgeschlossen, 102/102 xUnit-Tests mit 0 Skips inkl. PostgreSQL bestätigt, 32 isolierte TS-Tests und 8 Playwright-Tests bestanden und einen realen Keycloak-26.8.1-Contract-Test sowie den ASP.NET-HTTP-Scope-/CSRF-Test erfolgreich ausgeführt. Ein früherer CI-Lauf deckte C#-Primary-Constructor-Warnungen, xUnit-CancellationToken-Analyzerprobleme und Keycloak-OIDC-Testspezifika auf; diese wurden in PR #4 korrigiert.

**Gesamtlauf bleibt rot:** NuGet-/npm-Lockfiles fehlen im Repository und sind noch zu prüfen/einzuchecken. Die funktionale Testabdeckung bestätigt eine begrenzte clientbezogene FGAP-v2-Delegation mit zwei minimal privilegierten Servicekonten; sie beweist aber **keine vollständige User-/Group-Mapping-FGAP-Matrix, keine produktive Browser-SSO-/MFA-Authentifizierung** und keine Belastungs-/Wiederherstellungs- oder unabhängige Sicherheitsabnahme. Siehe [Ausgeführte CI-Evidenz](evidence/ci-2026-10-16.md).

### FGAP-v2-Negativmatrix (GitHub Actions #38912134224)

Ein isolierter Keycloak 26.8.1 bestätigt bei **zwei eingeschränkten, eigenständigen Servicekonten**: eigener Client und Anlage eigener Rollen erlaubt; fremde Client-Zugriffe, fremde Rollenanlagen, beliebige Client-Erstellung und Änderung der Realm-Sicherheitskonfiguration verweigert. Die Testkonten haben keine breiten Realm-Adminrollen. Der zentrale Testadministrator wird ausschließlich zum Aufbau des Test-Realms und der Berechtigungspolicies verwendet. Benutzer-/Gruppen-Zielmappings und vollständige LDAP-Sicht bleiben offen.

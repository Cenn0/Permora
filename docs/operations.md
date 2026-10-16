# Betrieb und Wiederherstellung

**Keine Produktionsfreigabe.** Die Compose-Datei startet ausschließlich lokale Abhängigkeiten mit Loopback-Ports und Development-Keycloak. Sie ist weder HA-Deployment noch produktiver Secrets-/Netzwerkstandard. Das Dockerfile ist eine noch nicht ausgeführte Buildvorlage; Images und Lockfiles vor Release tatsächlich prüfen und auf freigegebene Digests festlegen.

## Prozesse und Berechtigungen

API: BFF-Client-Secret, Portal-DB und persistent geschütztes Data-Protection-Verzeichnis. Worker: Portal-DB und genau benötigte Keycloak-Profile. Provisioner: eigene Service-Identität und eigene Konfiguration; nicht in den öffentlichen HTTP-Pfad hängen. Geschäftsanwendungen verbinden sich direkt mit Keycloak, nicht zur Laufzeit für jeden Request mit dem Portal.

DB-Schemaänderungen mit gesonderter Migrationsidentität ausführen: `dotnet run --project src/Permora.Worker -- --migrate`. Der normale API-Start migriert nicht. Laufzeitrollen dürfen nicht DB-Superuser sein. Rechte pro Prozess auf erforderliche Tabellen/Sequenzen begrenzen; Audit-Tabellen niemals für normale UPDATE/DELETE freigeben. Schlüsselverzeichnis ist persistent, zwischen API-Replikaten konsistent, gegen unberechtigten Zugriff geschützt und separat gesichert. KMS-Integration ist noch nicht implementiert.

## Netzwerk und HTTPS

Öffentlich ausschließlich TLS-Reverse-Proxy/Ingress für Angular+BFF, gleicher Origin. Admin-API-Host ausschließlich intern aus Worker/Provisioner erreichbar. PostgreSQL nicht öffentlich. `PublicOrigin` fest konfigurieren, Callback-URIs exakt erlauben. Die Anwendung vertraut nicht pauschal beliebigen Forwarded-Headers. Vertrauenswürdige Proxy-Netze und TLS-/Header-Verhalten vor Deployment gezielt konfigurieren und testen; nicht blind `KnownNetworks` leeren.

Die .NET-Session ist absolut auf 30 Minuten begrenzt. Lokaler Logout beendet nicht die unternehmensweite SSO-Sitzung. MFA muss am Identity Provider eingerichtet werden; separate Portal-Step-up-Erzwingung ist noch offen.

## Health und Fehler

`/health/live`: Prozess erreichbar. `/health/ready`: DB erreichbar und Schema v1 vorhanden. Readiness prüft derzeit **nicht** die vollständige Keycloak-/FGAP-Funktion oder alle Worker-Profile. Ein grüner HTTP-Healthcheck ist kein IAM-Gesundheitsnachweis.

Aufträge und Fehlercodes im Portal kontrollieren. Fehlgeschlagene Erweiterungen nicht mit direkten Datenbankänderungen als erfolgreich markieren. Bei unklarem Remote-Zustand erst Keycloak-Istzustand und genehmigten Portalzustand vergleichen; danach kontrolliert wiederholen oder korrigieren. Queue-Retry bleibt auditierbar. Monitoringsysteme müssen Failed/Conflict, alte Queued-Aufträge, überfällige Entzüge, Directory-Alter und Drift alarmieren; fertige Prometheus-/OpenTelemetry-/SIEM-Integration ist noch nicht enthalten.

Directory-Sync ist periodisch; fehlende Einträge werden nicht als Löschung gedeutet. Große oder unvollständig sichtbare Verzeichnisse blockieren Erweiterungen statt Teilbestände als vollständig auszugeben. Group-/Page-/App-Sicherheitslimits und DB-Lockhaltezeit begrenzen derzeit die Skalierung. Worker-Fairness unter großem Backlog wurde noch nicht vermessen.

## Backup / Restore

Portal-PostgreSQL, Keycloak-Datenbank und BFF-Data-Protection-Schlüssel getrennt sichern; keine gemeinsame atomare Momentaufnahme behaupten. Secrets nicht in normale Log-/Audit-Backups kopieren. Vor Migration DB-Backup erzeugen und Restore praktisch testen.

Nach Restore: Worker und Provisioner zunächst anhalten, Verzeichnis frisch synchronisieren, Soll-/Ist-Abgleich jeder App, offene/mehrdeutige Aufträge prüfen, abgelaufene Rechte entziehen, erst danach wieder Erweiterungen zulassen. Ein älterer Portal-Backupstand darf nicht automatisch alle alten Rollen neu vergeben. Dafür existiert noch kein vollautomatisiertes Disaster-Recovery-Orchestrierungswerkzeug.

Hashketten und Append-only-Trigger schützen nicht gegen privilegierte DB-/Infrastruktur-Administratoren. Für belastbare unveränderliche Audit-Aufbewahrung externe Systeme und definierte Retention ergänzen. Protokolle können personenbezogene Kennungen und Begründungen enthalten; Zugriffe und Aufbewahrung organisatorisch begrenzen.


## IAM-Freigabe und Entzugspriorität

Ein neuer oder neu gebundener Client bleibt bis zur zentralen IAM-Prüfung gesperrt. FGAP, die Connector-Profile und die Token-Audience müssen außerhalb des Portals geprüft und mit einer Nachweisreferenz attestiert werden. Danach führt der Worker einen separaten Remote-Abgleich aus. Fehlgeschlagene Entzüge und nicht bestätigte Schreibvorgänge benötigen aktive Alarmierung und manuelle Klärung; automatische Entzugspriorisierung allein garantiert keine maximale Reaktionszeit.


## Zentrale Warteschlangen-Diagnostik (P6)

Zentrale IAM-Administratoren können `GET /api/v1/admin/operations-health` abfragen. Die Antwort enthält nur globale Summen für `Queued`, `AwaitingApproval`, `Failed`, `Conflict`, fällige systemseitige Entzüge und das Alter des ältesten fälligen Entzugs in Sekunden. Andere Teams haben keinen Zugriff. Werte sind **kein** Ersatz für vollständige Keycloak-Soll-/Ist-Verifikation, keine Aussage über noch gültige JWTs und ohne Lasttest keine zugesagte Entzugs-SLA.

Alarmierung auf die neuen Kennzahlen sollte insbesondere blockierte Aufträge und überfällige automatische Entzüge betrachten; Schwellenwerte müssen im Pilotbetrieb anhand gemessener Last bestätigt werden. Für Prometheus/OpenTelemetry ist noch kein Exporter konfiguriert.

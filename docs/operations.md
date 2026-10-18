# Betrieb und Wiederherstellung

**Keine Produktionsfreigabe.** `deploy/compose.local.yml` startet nur lokale Abhängigkeiten. `deploy/compose.pilot.yml` ist eine separate eingeschränkte Single-Host-Referenz, kein HA-Deployment. Die drei tatsächlichen Anwendungsimages wurden für den [signierten Draft-Kandidaten](https://github.com/Cenn0/Permora/actions/runs/39117753278) gebaut und gescannt; TLS- und Clusterbetrieb bleiben extern abnahmepflichtig.

## Prozesse und Berechtigungen

API: BFF-Client-Secret, Portal-DB und persistent geschütztes Data-Protection-Verzeichnis. Worker: Portal-DB und genau benötigte Keycloak-Profile. Provisioner: eigene Service-Identität und eigene Konfiguration; nicht in den öffentlichen HTTP-Pfad hängen. Geschäftsanwendungen verbinden sich direkt mit Keycloak, nicht zur Laufzeit für jeden Request mit dem Portal.

DB-Schemaänderungen mit gesonderter Migrationsidentität ausführen: `dotnet run --project src/Permora.Worker -- --migrate`. Der normale API-Start migriert nicht. Laufzeitrollen dürfen nicht DB-Superuser sein. Rechte pro Prozess auf erforderliche Tabellen/Sequenzen begrenzen; Audit-Tabellen niemals für normale UPDATE/DELETE freigeben. Schlüsselverzeichnis ist persistent, zwischen API-Replikaten konsistent, gegen unberechtigten Zugriff geschützt und separat gesichert. KMS-Integration ist noch nicht implementiert.

## Netzwerk und HTTPS

Öffentlich ausschließlich TLS-Reverse-Proxy/Ingress für Angular+BFF, gleicher Origin. Admin-API-Host ausschließlich intern aus Worker/Provisioner erreichbar. PostgreSQL nicht öffentlich. `PublicOrigin` fest konfigurieren, Callback-URIs exakt erlauben. Die Anwendung vertraut nicht pauschal beliebigen Forwarded-Headers. Vertrauenswürdige Proxy-Netze und TLS-/Header-Verhalten vor Deployment gezielt konfigurieren und testen; nicht blind `KnownNetworks` leeren.

Die .NET-Session ist absolut auf 30 Minuten begrenzt. Lokaler Logout beendet nicht die unternehmensweite SSO-Sitzung. Der Portalserver verlangt für mutierende API-Aufrufe `acr` und frisches `auth_time`; ein echter Keycloak-26.8.1-TOTP-/LoA2-Test ist [bestanden](https://github.com/Cenn0/Permora/actions/runs/39011230492). Der produktive Unternehmens-IdP muss MFA und Assurance-Level gesondert sicher konfigurieren und abnehmen.

## Health und Fehler

`/health/live`: Prozess erreichbar. `/health/ready`: DB erreichbar und Schema v1 vorhanden. Readiness prüft derzeit **nicht** die vollständige Keycloak-/FGAP-Funktion oder alle Worker-Profile. Ein grüner HTTP-Healthcheck ist kein IAM-Gesundheitsnachweis.

**Entzugspriorität:** Automatische Entzüge laufen zuerst; manuell
ausgelöste `RevokeAccess`-Aufträge dürfen ebenfalls ältere noch wartende
Vergaben überholen. Auch während ein Entzug wegen eines Remote-Timeouts im
Backoff liegt, führt die Queue **keine** anderen Erweiterungen der
betroffenen Anwendung aus. Ein manueller Entzug bleibt über zwischenzeitliche
Aggregate-Revisionen und die Sieben-Tage-Altersgrenze hinweg gültig,
aber Berechtigung des Anforderers, Vorhandensein der Zielzuweisung und
Remote-Readback werden beim Worker-Lauf erneut geprüft. Fehler/Conflicts
sind sichtbar zu eskalieren; diese Warteschlangenregel ist keine Garantie
für sofortigen Widerruf bereits ausgegebener JWTs.

Aufträge und Fehlercodes im Portal kontrollieren. Fehlgeschlagene Erweiterungen nicht mit direkten Datenbankänderungen als erfolgreich markieren. Bei unklarem Remote-Zustand erst Keycloak-Istzustand und genehmigten Portalzustand vergleichen; danach kontrolliert wiederholen oder korrigieren. Queue-Retry bleibt auditierbar. Monitoringsysteme müssen Failed/Conflict, alte Queued-Aufträge, überfällige Entzüge, Directory-Alter und Drift alarmieren; fertige Prometheus-/OpenTelemetry-/SIEM-Integration ist noch nicht enthalten.

Directory-Sync ist periodisch; fehlende Einträge werden nicht als Löschung gedeutet. Große oder unvollständig sichtbare Verzeichnisse blockieren Erweiterungen statt Teilbestände als vollständig auszugeben. Group-/Page-/App-Sicherheitslimits und DB-Lockhaltezeit begrenzen derzeit die Skalierung. UUID-rotierender Queue-Polling-Code wurde gegen PostgreSQL und das Worker-Failover mit vier Anwendungen und 32 realen Entzügen getestet. **Produktive** Backlog-Fairness, maximale Entzugszeit und Multi-Host-SLO wurden noch nicht vermessen.

## Backup / Restore

Portal-PostgreSQL, Keycloak-Datenbank und BFF-Data-Protection-Schlüssel getrennt sichern; keine gemeinsame atomare Momentaufnahme behaupten. Secrets nicht in normale Log-/Audit-Backups kopieren. Vor Migration DB-Backup erzeugen und Restore praktisch testen.

Nach Restore: Worker und Provisioner zunächst anhalten, Verzeichnis frisch synchronisieren, Soll-/Ist-Abgleich jeder App, offene/mehrdeutige Aufträge prüfen, abgelaufene Rechte entziehen, erst danach wieder Erweiterungen zulassen. Ein älterer Portal-Backupstand darf nicht automatisch alle alten Rollen neu vergeben. Dafür existiert noch kein vollautomatisiertes Disaster-Recovery-Orchestrierungswerkzeug.

Hashketten und Append-only-Trigger schützen nicht gegen privilegierte DB-/Infrastruktur-Administratoren. Für belastbare unveränderliche Audit-Aufbewahrung externe Systeme und definierte Retention ergänzen. Protokolle können personenbezogene Kennungen und Begründungen enthalten; Zugriffe und Aufbewahrung organisatorisch begrenzen.


## IAM-Freigabe und Entzugspriorität

Ein neuer oder neu gebundener Client bleibt bis zur zentralen IAM-Prüfung gesperrt. FGAP, die Connector-Profile und die Token-Audience müssen außerhalb des Portals geprüft und mit einer Nachweisreferenz attestiert werden. Danach führt der Worker einen separaten Remote-Abgleich aus. Fehlgeschlagene Entzüge und nicht bestätigte Schreibvorgänge benötigen aktive Alarmierung und manuelle Klärung; automatische Entzugspriorisierung allein garantiert keine maximale Reaktionszeit.


## Zentrale Warteschlangen-Diagnostik (P6)

Zentrale IAM-Administratoren können `GET /api/v1/admin/operations-health` abfragen. Die Antwort enthält nur globale Summen für `Queued`, `AwaitingApproval`, `Failed`, `Conflict`, fällige systemseitige Entzüge und das Alter des ältesten fälligen Entzugs in Sekunden. Andere Teams haben keinen Zugriff. Werte sind **kein** Ersatz für vollständige Keycloak-Soll-/Ist-Verifikation, keine Aussage über noch gültige JWTs und ohne Lasttest keine zugesagte Entzugs-SLA.

Alarmierung auf die neuen Kennzahlen sollte insbesondere blockierte Aufträge und überfällige automatische Entzüge betrachten; Schwellenwerte müssen im Pilotbetrieb anhand gemessener Last bestätigt werden. Für Prometheus/OpenTelemetry ist noch kein Exporter konfiguriert.


## Isolierte PostgreSQL-Restore-Prüfung in CI

`scripts/ci-database-recovery.sh` erzeugt **nach dem echten API-HTTP-Test** einen temporären `pg_dump` aus der isolierten CI-PostgreSQL-Datenbank, importiert ihn in eine getrennte temporäre Testdatenbank und vergleicht Tabellenbestände, Anwendungs-JSONB-Prüfsummen und die Existenz der Audit-Append-only-Trigger. Das Skript verweigert Remote-Hosts und nicht-testbezogene Datenbanknamen, löscht die Kopie und speichert kein Backup-Artefakt.

**Grenzen:** Kein vollständiges Disaster-Recovery, keine Keycloak-/BFF-Key-Wiederherstellung, kein PITR oder SLA-/RPO-/RTO-Nachweis. Nach jedem echten Restore bleiben Worker-Sperre, Remote-Keycloak-Abgleich, Secrets-/Key-Restore und Sicherheitsabnahme erforderlich.

### Störfälle: nicht mehr automatisch wiederholbare Entzüge

`GET /api/v1/admin/operations-health` unterscheidet zusätzlich
`failedRevocations`, `conflictedRevocations` und
`oldestOutstandingRevocationSeconds`. Letzterer Wert berücksichtigt
sämtliche noch in `Queued`, `Failed` oder `Conflict` befindlichen
systemischen Entzüge – nicht nur aktuell fällige Queued-Aufträge.

**Betriebsregel:** Jeder `Failed`-/`Conflict`-Entzug erfordert einen
gezielten Alarm und eine kontrollierte manuelle Soll-/Ist-Prüfung.
`oldestOutstandingRevocationSeconds` ist ein Alter des ältesten
Bearbeitungsbedarfs, **keine** Messung real noch gültiger JWT-Zugriffe.
Die API veröffentlicht keine personenbezogenen Identifikatoren oder
Realm-/Clientnamen. Schwellwerte und ein verbindlicher Entzugs-SLO
dürfen erst nach Last-/Recovery-Messung für die konkrete Installation
festgelegt werden.

### Warteschlangen-Fairness bei mehr als 100 fälligen Anwendungen

Der Worker verarbeitet pro Poll maximal 100 fällige Anwendungen. Eine lokale
UUID-Cursor-Rotation wählt in der nächsten Runde zunächst die Anwendungen
**nach** der zuletzt bearbeiteten ID und springt am Ende wieder zum Anfang.
Damit verdrängen die 100 niedrigsten UUIDs nicht dauerhaft andere
Anwendungen, deren Aufträge ebenfalls ununterbrochen fällig sind.

Die Regression verwendet sieben gleichzeitig fällige Anwendungen und ein
Testlimit von zwei Anwendungen je Runde gegen **echtes PostgreSQL**. Die
Rotation ist **keine** Garantie einer maximalen Entzugsdauer: Eine
langlebige Remote-Sperre, ein ausgefallener Worker, mehrere voneinander
unabhängige Worker-Instanzen oder Backlog oberhalb der tatsächlich gemessenen
Kapazität benötigen weiterhin Monitoring, Lasttests und Betriebsmaßnahmen.

## Directory-Privacy-Retention

Die optionale Einstellung `Privacy:UnreferencedDirectoryRetentionDays` (30–3650 Tage)
aktiviert eine begrenzte Bereinigung nicht mehr benötigter Directory-Snapshots
**nur nach erfolgreicher Synchronisation**. Ohne explizite Einstellung erfolgt
keine automatische Löschung. Eine PostgreSQL-Transaktion schützt alle noch
referenzierten Identitäten, insbesondere Teammitglieder, aktive Zuweisungen,
ausstehende Anträge und Mitglieder berechtigter Gruppen. Das ist keine
DSAR- oder gesetzliche Retention-Freigabe; siehe [Privacy](privacy-and-retention.md).

## Geheimnisdatei statt Konfigurationspasswort

Alle drei Prozesse (API, Worker und Provisioner) unterstützen neben der
bestehenden Entwicklungsoption `ConnectionStrings:Portal` jetzt
`ConnectionStrings:PortalFile` mit **absolutem Dateipfad**.
Die Datei enthält genau **eine vollständige Npgsql-Verbindungszeichenfolge**;
ein abschließender Zeilenumbruch wird entfernt. Die Anwendung meldet
fehlende/leere Dateien ohne Offenlegung des Kennworts. Beide Felder
gleichzeitig sind ausdrücklich verboten. Die veröffentlichte
Beispielkonfiguration verwendet `/run/secrets/portal-postgres`.

Für jeden Prozess eine separat berechtigte, schreibgeschützte Geheimnisdatei
bereitstellen und über einen geeigneten Secret-Manager/Orchestrator einhängen.
Secret-Mounts, minimale Datenbankberechtigungen und Rotation sind
**Betriebsanforderungen**, keine durch die Leseunterstützung bewiesene
Produktionshärtung. Die Alternative mit Inline-String bleibt für isolierte
lokale Entwicklung kompatibel. Niemals produktive Connection-Strings in
Repository, Actions-Logs oder öffentliche Issues übernehmen.

## Echte isolierte Mehrprozess-/Recovery-Evidenz

[Workflow #39117753047](https://github.com/Cenn0/Permora/actions/runs/39117753047)
hat **zwei echte Worker-Prozesse** mit PostgreSQL 18 und Keycloak 26.8.1
ausgeführt. Die Testdaten umfassen vier Anwendungen und 32 abgelaufene
Zuweisungen. Die Tests erzwingen eine vorübergehende Keycloak-503-Störung,
verlieren eine erfolgreiche Remote-Antwort nach dem Rechteentzug, beenden den
zuständigen Worker und verifizieren, dass der Überlebende alle Entzüge
abschließt, ohne ungewollte Rechte erneut zu vergeben. Anschließend werden
die isolierten PostgreSQL-/Keycloak-Datenbanken wiederhergestellt, und
Keycloak-Signaturschlüssel sowie effektive Rollenzuweisungen geprüft.

Die gesonderten [Data-Protection-Tests](session-ha-and-rotation.md) prüfen
zwei unabhängige Sitzungsanbieter, verschlüsselte Schlüsselablage,
Zertifikatswechsel mit alten Decryption-Zertifikaten und die gegenseitige
Logout-Wirksamkeit gegen dieselbe Datenbank.

**Nicht nachgewiesen:** Mehrstandort-HA, Live-TLS-Endpunkt- und
Proxy-Failover, Secret-Manager-Rotation, vollständiger Backup-PITR,
verbindliche RPO-/RTO-Werte oder funktionale Wiederanlauf-Garantien einer
konkreten produktiven IAM-/AD-Installation. Den Entwickler-Systemtest
nicht als operativen Abnahmebericht eines Rechenzentrums ausgeben.

## Bounded multiworker load and failure injection

The optional [load workflow](../.github/workflows/multiworker-load.yml)
runs **two actual Permora.Worker processes** against an entirely disposable
PostgreSQL 18 instance and Keycloak 26.8.1 with **eight applications and
12 principals each**. It creates 96 real remote role mappings and expired
assignments and verifies eventual revocation under deliberately injected
Keycloak 503 responses, a remote DELETE committed without returning to the
caller, and termination of the responsible worker. It then compares effective
Keycloak mappings and restores both isolated Keycloak and portal PostgreSQL
databases. The fixture outputs the actual elapsed time and aggregate
revocations-per-second observed on the ephemeral GitHub runner.

The workload is **bounded** (2–12 applications, 2–24 users, maximum 192
assignments) and refuses nonisolated execution. The extra load workflow runs
when its fixture/workflow changes or when manually dispatched; the smaller
default (4 apps × 8 principals) remains in the normal worker-acceptance
workflow. Neither configuration demonstrates a production workload,
multi-host topology, a specific percentile latency, or a guaranteed
revocation SLO. Real production baseline/load measurements, degraded-mode
capacity planning, independently reviewed chaos tests and a controlled
incident procedure remain hard acceptance gates.


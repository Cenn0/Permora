# Isolierter Pilotbetrieb – Compose-Referenz (keine Produktionsfreigabe)

Die Datei [`deploy/compose.pilot.yml`](../deploy/compose.pilot.yml) ist eine
**bewusst eingeschränkte Referenz** für einen einzelnen, administrativ
kontrollierten Host. Sie startet nur die drei Permora-Prozesse (API, Worker und
optional Provisioner), **keine** eigene Keycloak-/PostgreSQL-Produktivinstanz.
Sie ist kein HA-Deployment, kein Kubernetes-Manifest und kein Ersatz für
den vollständigen [1.0-Freigabeprozess](release-checklist.md).

## Sicherheitsmodell

- Nur die API erhält einen **Host-Loopback-Port** (`127.0.0.1:18088`);
  öffentlich darf ausschließlich ein separat gehärteter HTTPS-Reverse-Proxy
  erscheinen. Der Proxy muss den gleichen Origin für UI und BFF halten.
- Worker und Provisioner haben **keinen veröffentlichten Port** und
  getrennte Keycloak- und PostgreSQL-Credentials. Der Provisioner wird erst
  mit Profil `provisioning` gestartet und besitzt eine besonders
  privilegierte Identität.
- Container verwenden den im Dockerfile definierten **Nicht-Root-Benutzer**,
  ein schreibgeschütztes Root-Dateisystem, `cap_drop: ALL`,
  `no-new-privileges`, sowie ein temporäres `/tmp`. Die API benötigt
  zusätzlich ein dauerhaftes, **schreibbares** Verzeichnis für die
  ASP.NET-Data-Protection-Schlüssel.
- Alle Verbindungssätze und Client-Secrets sind **Datei-Mounts**; es gibt
  keine eingebetteten Passwörter in Compose, kein `privileged`, kein Docker
  Socket-Mount und keine automatischen Deployments aus CI.
- Das Docker-Bridge-Netz trägt nur einen Namen. Es implementiert **keine**
  Ausgangsfilterung; die produktiven PostgreSQL-, Keycloak-, DNS- und
  Aktualisierungsziele müssen am Host oder externem Gateway freigegeben und
  sonstige Ziele gesperrt werden.

## Vorbereitung durch die betreibende Organisation

Erforderlich sind ein bereitgestellter, getrennt gehärteter PostgreSQL-Server
und ein konkret kompatibilitätsgeprüfter Keycloak-Realm mit OIDC-/MFA-
Konfiguration, FGAP-v2-Rechten, vollständiger Directory-Sicht und zentraler
IAM-Freigabe. Der Pilotbetrieb darf diese Rechte nicht selbst erraten.

1. Unter dem Repository-Root `.local/permora-pilot/` die **nicht
   versionierten** Dateien `api.json`, `worker.json` und bei Bedarf
   `provisioner.json` anlegen. Die Beispiele unter `config/` als
   Ausgangspunkt **separat pro Prozess minimieren**:
   `config/portal.example.json` für die API,
   `config/worker.example.json` für den Worker und
   `config/provisioner.example.json` für den Provisioner.
   **Keine Worker-Admin-Credentials in die API übernehmen.** Die API
   liest lediglich den nicht geheimen
   `Keycloak:Realms:<realm>:AllowGroupRoleGrants`-Schalter (Standard:
   `false`). Nach zentraler IAM-Risikofreigabe ist der Schalter in API
   und Worker **getrennt** zu konfigurieren; er verengt die upstream
   `Groups.manage`-Berechtigung nicht.
2. `api-db.secret`, `worker-db.secret` und optional
   `provisioner-db.secret` als jeweils **eine vollständige** Npgsql-
   Verbindungszeichenfolge hinterlegen; pro Prozess eigene DB-Rolle mit
   ausschließlich notwendigen Rechten. JSON verwendet
   `ConnectionStrings:PortalFile=/run/secrets/portal-postgres`.
3. Dateien `oidc-client.secret` nur für die API und
   `role-editor.secret`, `role-mapper.secret`,
   `directory-reader.secret` nur für den Worker bereitstellen.
   Für den Provisioner zusätzlich `client-preparer.secret`; keine
   gemeinsame, globale `realm-admin`-Identität verwenden.
4. `.local/permora-pilot/keys` als dauerhaftes Verzeichnis anlegen,
   **vorher** für den Nicht-Root-UID des Images schreibbar machen und auf
   vertrauenswürdige Host-Administratoren beschränken. Schlüssel
   verschlüsselt sichern und einen separaten Restore durchführen.
5. Rückwärtige Proxy-Header, öffentliche Origin-URL und Callback-URIs
   ausschließlich aus vertrauenswürdigen Einstellungen konfigurieren;
   TLS-/CSP-/Session-Grenzen extern testen. Keine pauschalen
   `KnownNetworks`-Ausnahmen setzen.

**Alle Inhalte unter `.local/` sind vom Git ausgeschlossen.** Sie
dürfen weder in GitHub-Issues noch CI-Logs kopiert werden.

## Statische Prüfung und Pilotstart

Vom Repository-Root:

```bash
# Prüft nur die Compose-Konfiguration, startet keine Infrastruktur:
docker compose -f deploy/compose.pilot.yml --profile provisioning config --quiet

# Builds lokal, kein Image-Push:
docker compose -f deploy/compose.pilot.yml build

# Nach separat durchgeführter, auditierter DB-Migration und FGAP-Abnahme:
docker compose -f deploy/compose.pilot.yml up -d api worker

# Provisioner nur nach eigenständiger IAM-Freigabe aktivieren:
docker compose -f deploy/compose.pilot.yml --profile provisioning up -d provisioner
```

**Die Datenbankmigration muss vor dem Start mit einer separaten,
zeitlich befristeten Migrationsidentität durchgeführt werden**. Der
Worker-Standardaccount soll keine Schema-DDL-Rechte besitzen. Ein
erfolgreiches `docker compose config` oder ein Docker-Image-Build
beweist nicht, dass die bereitgestellten Secrets, die DB-Berechtigungen,
der echte Keycloak-FGAP-Vertrag oder die Reverse-Proxy-Regeln korrekt sind.

## Betriebs- und Freigabegrenzen

- TLS-Proxy, Host-Firewall, Secret-Verwaltung/Rotation,
  Schlüsselablage sowie Replica-/Failover-Topologie sind **außerhalb**
  dieser Compose-Datei zu konfigurieren.
- Ein einzelner Host ist ein Single Point of Failure. Vor jeder
  produktiven Zusage sind dokumentierte Last, RPO/RTO, vollständiger
  Keycloak-/Portal-/Schlüssel-Restore und ein sicherer Upgrade-Rollback
  nachzuweisen.
- Ein Backlog aus überfälligen Entzügen und fehlgeschlagenen
  Reconcile-Aufträgen ist ein **Security Incident**, kein
  kosmetischer Dashboardzustand.
- Die Versions-/Sicherheitsmatrix, FGAP-Grenzen, externer Pentest und
  Datenschutz-/Audit-Abnahme verbleiben als harte Release-Gates.

Referenzen: [Operations](operations.md) · [Security](security.md) ·
[Release-Checkliste](release-checklist.md).

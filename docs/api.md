# API-Vertrag

Alle fachlichen Endpunkte liegen unter `/api/v1`, verwenden BFF-Session und benötigen aktuelle Verwaltungsmandate. Mutationen benötigen den `X-XSRF-TOKEN`-Header aus der Sessioninitialisierung. Ohne Authentifizierung 401, bei fremden App-Ressourcen in der Regel 404, bei verbotenen Aktionen 403, Revision 412, fachlichem Konflikt 409.

## Leseschnittstellen

`GET /bff/session` initialisiert das CSRF-Token und liefert `authenticated`, `name`, `subjectKey`, `administrator`, `development`. Die letzte Eigenschaft macht Development-Login sichtbar. `GET /bff/login` beginnt das OIDC-Login; `POST /bff/logout` beendet ausschließlich die lokale Session.

`GET /api/v1/dashboard`, `/teams`, `/realms`, `/applications` liefern nur den zugänglichen Bereich. Dashboard-Kennzahlen sind vollständige aggregierte Werte; Aktivitäten und Anträge sind begrenzte Vorschauen. Eine Vorschau darf niemals als Gesamtzahl interpretiert werden.

`GET /api/v1/applications/{id}` liefert Anwendung, Mandat, relevante Identitäten und `reviewSchedule` (Objekt oder `null`, falls noch kein Plan besteht). Unter derselben App: `/operations`, `/operations/{operationId}`, `/reviews`, `/audit?after={sequence}`, `/identities?q={query}`. Directory-Suche mindestens drei Zeichen, maximal 25 Ergebnisse. Audit bis 200 Einträge, danach `nextSequence` folgen. Die Operationsliste ist derzeit auf 200 Einträge begrenzt; unbegrenzte Historiennavigation ist noch offen.

## Registrierung

`POST /api/v1/applications` mit `teamId`, `realmKey`, `slug`, `name`, `description`, `environment`. Environment ist `Development`, `Test` oder `Production`. Nur ein aktuell expliziter Team-Owner darf registrieren; zentrale Adminrechte allein ersetzen diese Mitgliedschaft bei der transaktionalen Erstellung nicht.

## Änderungen

```http
POST /api/v1/applications/{id}/changes
Idempotency-Key: a-client-generated-unique-key
X-XSRF-TOKEN: <session-token>
Content-Type: application/json
```

```json
{
  "expectedRevision": 7,
  "change": {
    "kind": "CreatePermission",
    "key": "orders.approve",
    "name": "Aufträge freigeben",
    "description": "Produktionsaufträge freigeben",
    "reason": "Neue Freigabefunktion der Anwendung"
  }
}
```

Antwort `202 Accepted` mit Operation. Die Ziel-ID für neue Ressourcen normalisiert der Server. Zulässige Commands: `CreatePermission`, `UpdatePermission`, `DeletePermission`, `CreateRole`, `UpdateRole`, `DeleteRole`, `GrantAccess`, `RevokeAccess`, `Reconcile`.

Rollen verwenden `permissionIds`; Zuweisung `roleId`, `principalId`, `expiresAt`; Update/Delete/Entzug `targetId`. Technische Keys werden nicht beliebig umbenannt. `reason` ist immer erforderlich. Ein Idempotenzschlüssel gilt für denselben Akteur, dieselbe Anwendung und dieselbe Nutzlast/Revision. Eine geänderte Nutzlast mit altem Schlüssel ist ein Konflikt, kein neuer Auftrag.

`POST /applications/{id}/operations/{operationId}/decision` mit `approve`, `reason`. Antragsteller kann nicht selbst freigeben. `POST .../retry` ist nur für Owner und tatsächlich versuchte fehlgeschlagene/unklare Aufträge vorgesehen; Revision und Autorisierung werden erneut geprüft.

`POST /applications/{id}/reviews` mit `name`, `dueAt`; `POST .../reviews/{reviewId}/items/{assignmentId}` mit `retain`. Ein Entzug aus der Rezertifizierung ist zunächst ein Auftrag, kein sofortiger JWT-Widerruf.

## Periodische Rezertifizierung

`PUT /api/v1/applications/{id}/review-schedule` konfiguriert den Plan. Beispiel:

```json
{
  "name": "Quartalsprüfung",
  "intervalDays": 90,
  "reviewWindowDays": 14,
  "nextStartAt": "2027-01-01T09:00:00Z",
  "enabled": true,
  "expectedRevision": 0
}
```

Nur Owner beziehungsweise zentrale Administratoren dürfen ändern; die übliche
BFF-, CSRF- und Step-up-Prüfung gilt auch hier. Approver und Auditor dürfen den
Plan über die sichtberechtigte Anwendungsabfrage lesen. Die Antwort enthält
`name`, `intervalDays`, `reviewWindowDays`, `nextStartAt`, `enabled`, `revision`.
`expectedRevision` ist bei der ersten Konfiguration `0`, danach die zuletzt
gelesene **Planrevision**, nicht die Anwendungsrevision. Auch ein Worker-Start
erhöht die Planrevision; veraltetes Speichern liefert `412 revision_conflict`.

Intervall: 1–365 Tage; Bearbeitungsfrist: 1–90 Tage und höchstens das Intervall.
Ein aktivierter Start liegt in der Zukunft und höchstens 365 Tage voraus.
Zum Pausieren dieselben Planfelder mit `enabled: false` senden; dabei ist ein
vergangener Start zulässig. Bereits gestartete Reviews werden nicht geändert.
Ein bereits fälliger aktiver Plan muss vor einer Änderung vom Worker verarbeitet
werden; bis dahin liefert die API `409 review_schedule_due`.
Automatische Reviews enthalten zusätzlich `scheduledFor`; bei alten oder
manuellen Reviews ist es `null`. Fristen nach Ausfällen, Zuweisungssnapshot und
feste UTC-Intervalle sind im [Lebenszyklusvertrag](access-reviews.md) beschrieben.

`retain: true` liefert `409 assignment_expired` für abgelaufene Zuweisungen und
`409 revocation_pending` bei vorhandenen Entzügen beziehungsweise einer bereits
unbestätigt verstrichenen Prüfpflicht. Ein abgelehnter Entzugsauftrag allein
blockiert keine Bestätigung. Die Prüfung auf Selbstzertifizierung und aktuelle
Directory-Identitäten bleibt erforderlich.

## Zentrale Verwaltung

Nur `PlatformAdmin`: `POST /admin/teams`, `PUT /admin/teams/{id}`, `PUT /admin/applications/{id}/binding`, `/policy`, `/state`. Team: `name`, `members[{subjectKey,name,role}]`, `expectedRevision`. Binding: interne `clientUuid`, `clientId`, optional `loginClientUuid`, `expectedRevision`. Policyänderungen an bereits vergebenen Rechten verlangen in dieser ersten Version vorherigen kontrollierten Entzug/Review. Suspendierung blockiert Portal-Erweiterungen; sie beendet nicht sämtliche Anwendungssitzungen.

Die Laufzeit-OpenAPI-Beschreibung unter `/openapi/v1.json` ist auf zentrale Administratoren begrenzt. Sie wurde noch nicht aus einem erfolgreich gestarteten Build exportiert.


## Zentrale IAM-Attestierung

`POST /api/v1/admin/applications/{id}/iam-approval` (nur `PlatformAdmin`, mit Session/CSRF) verlangt:

```json
{
  "expectedRevision": 12,
  "fgapVerified": true,
  "rolePermissionsVerified": true,
  "assignmentPermissionsVerified": true,
  "tokenAudienceVerified": true,
  "evidenceReference": "IAM-TEST-1234"
}
```

Alle vier Prüfungen und ein Nachweis mit mindestens sechs Zeichen sind Pflicht. Zulässig ist nur eine gebundene Anwendung im Status `AwaitingIam`. Der Backend-Auftrag erzeugt eine erneute Remote-Prüfung; eine positive Antwort allein ist kein erfolgreicher FGAP- oder Token-Test.


## Central IAM operations health

`GET /api/v1/admin/operations-health` returns aggregate queue counters (`queued`, `awaitingApproval`, `failed`, `conflict`, `dueRevocations`, `oldestDueRevocationSeconds`) from PostgreSQL. Only principals with the central `PlatformAdmin` mandate may call it; a foreign team receives `403`. Fields contain **no** user, group, role, application or realm identifiers. They are intended for alerting and incident response, not for making write/restore decisions without Keycloak readback.

The endpoint measures PostgreSQL queue state only. It does **not** prove that Keycloak has applied an operation, does not measure revoked JWT validity, and is not an SLO commitment. A safe operator alarm baseline is `failed > 0`, `conflict > 0` or `dueRevocations > 0` for a sustained period. Verify actual thresholds with load and recovery tests before production.

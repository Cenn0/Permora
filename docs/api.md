# API-Vertrag

Alle fachlichen Endpunkte liegen unter `/api/v1`, verwenden BFF-Session und benötigen aktuelle Verwaltungsmandate. Mutationen benötigen den `X-XSRF-TOKEN`-Header aus der Sessioninitialisierung. Ohne Authentifizierung 401, bei fremden App-Ressourcen in der Regel 404, bei verbotenen Aktionen 403, Revision 412, fachlichem Konflikt 409.

## Leseschnittstellen

`GET /bff/session` initialisiert das CSRF-Token und liefert `authenticated`, `name`, `subjectKey`, `administrator`, `development`. Die letzte Eigenschaft macht Development-Login sichtbar. `GET /bff/login` beginnt das OIDC-Login; `POST /bff/logout` beendet ausschließlich die lokale Session.

`GET /api/v1/dashboard`, `/teams`, `/realms`, `/applications` liefern nur den zugänglichen Bereich. Dashboard-Kennzahlen sind vollständige aggregierte Werte; Aktivitäten und Anträge sind begrenzte Vorschauen. Eine Vorschau darf niemals als Gesamtzahl interpretiert werden.

`GET /api/v1/applications/{id}` liefert Anwendung, Mandat und relevante Identitäten. Unter derselben App: `/operations`, `/operations/{operationId}`, `/reviews`, `/audit?after={sequence}`, `/identities?q={query}`. Directory-Suche mindestens drei Zeichen, maximal 25 Ergebnisse. Audit bis 200 Einträge, danach `nextSequence` folgen. Die Operationsliste ist derzeit auf 200 Einträge begrenzt; unbegrenzte Historiennavigation ist noch offen.

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

## Zentrale Verwaltung

Nur `PlatformAdmin`: `POST /admin/teams`, `PUT /admin/teams/{id}`, `PUT /admin/applications/{id}/binding`, `/policy`, `/state`. Team: `name`, `members[{subjectKey,name,role}]`, `expectedRevision`. Binding: interne `clientUuid`, `clientId`, optional `loginClientUuid`, `expectedRevision`. Policyänderungen an bereits vergebenen Rechten verlangen in dieser ersten Version vorherigen kontrollierten Entzug/Review. Suspendierung blockiert Portal-Erweiterungen; sie beendet nicht sämtliche Anwendungssitzungen.

Die Laufzeit-OpenAPI-Beschreibung unter `/openapi/v1.json` ist auf zentrale Administratoren begrenzt. Sie wurde noch nicht aus einem erfolgreich gestarteten Build exportiert.

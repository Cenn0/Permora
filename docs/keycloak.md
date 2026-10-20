# Keycloak anbinden und abnehmen

Zielversion: **26.8.1**. Das Portal ist eine Community-Erweiterung, keine offizielle Keycloak-Admin-Konsole. Rollen, Admin REST und FGAP sind dokumentierte Keycloak-Funktionen; die minimal nötige Kombination muss für diese konkrete Version praktisch geprüft werden.

## 1. Portal-Anmeldung

Im Mitarbeiter-Realm einen vertraulichen OIDC-Client `permora-portal` anlegen. Authorization Code / Standard Flow mit PKCE S256 verwenden; Implicit Flow und Direct Access Grants deaktivieren. Exakte Redirect-URI `https://access.example.org/signin-oidc` und gegebenenfalls lokale Logout-Callback-URI konfigurieren, keine Wildcards. Server-Secret ausschließlich über `Portal.ClientSecretFile` einlesen. MFA im zuständigen Browser-Flow einrichten. `Portal.Authority` muss dem tatsächlichen Token-Issuer entsprechen; Admin-Host und SSO-Host dürfen unterschiedlich sein.

`AdministratorSubjects` enthält wenige zentral verwaltete `sub`-Kennungen. Anwendungsteams dürfen diese Konfiguration nicht ändern. Die API liefert den kanonischen `subjectKey` einer angemeldeten Person über `/bff/session`; diesen für Teammitgliedschaften verwenden, nicht Namen oder E-Mail-Adressen.

## 2. Anwendung registrieren und vorbereiten

Zentrale IT legt zunächst ein Team an und gibt dessen ID unter `Portal.RealmTeams` frei. Ein tatsächlicher Team-Owner registriert die Anwendung. Status: `AwaitingIam`.

Optional läuft der getrennte Provisioner mit `config/provisioner.example.json`. Er erstellt einen deaktivierten Rechte-Client `app-{slug}-{environment}-api`, ohne Standard/Implicit/Direct-Grant-Flow und ohne Service Account. Ein gleichnamiger fremder oder bereits aktivierter Client wird nicht übernommen. Die Client-Erstellung führt **nicht** automatisch zu Zugriffen oder zu aktivierter Delegation.

Alternativ kann zentrale IAM einen eigenen existierenden Client importieren. Die Bindung erfolgt über interne Client-UUID, Client-ID und optional Login-Client-UUID. Der Rechte-Client muss `permora.application=<Portal-App-UUID>` tragen. Rebinding einer bereits mit Permissions/Rollen/Zuweisungen gefüllten Anwendung erfordert eine explizite Migration und wird abgewiesen.

## 3. Connector-Profile und FGAP

Drei Profile konfigurieren: Rollenbearbeitung, Rollenzuweisung, Directory-Lesen. Der Provisioner verwendet separate Credentials. Freigaben an Clients/Rollen und an Zielbenutzern beziehungsweise Gruppen getrennt betrachten.

| Profil | Benötigter Operationsbereich | Was nicht pauschal vergeben werden darf |
|---|---|---|
| Rollen | konkrete freigegebene Clients/Rollen, Composites, ggf. Login-Client-Scope-Mappings | Realm-Konfiguration oder fremde Clients |
| Zuweisung | Rollen der verwalteten Clients plus zulässige Zielbenutzer/-gruppen | globale beliebige Adminrollen |
| Directory | für die Abnahme vollständige relevante Benutzer-/Gruppen-/Mitgliedschaftssicht | Passwortverwaltung, Impersonation, Credential-Verwaltung |
| Provisioner | kontrollierte Client-Erstellung und Lesen der vorbereiteten Clients | Benutzer-/Rollen-Lifecycle über freie User-Payloads |

**Nicht ungeprüft `realm-admin` vergeben, um Tests „grün“ zu bekommen.** Client-`manage` ist weiter als reine Rollenverwaltung. Die Portal-Endpunktbegrenzung ist deshalb unverzichtbar. Vollständige relevante Listen müssen lesbar sein: Eine FGAP-gefilterte leere Antwort darf keine falsche „saubere“ Reconciliation vortäuschen.

### Kritischer FGAP-v2-Gruppen-Scope: Rolle zuweisen ≠ Gruppe verwalten

**Release-Grenze (Keycloak 26.8.1):** Die dokumentierten `Groups`-Scopes
enthalten derzeit `view`, `manage`, `view-members` und weitere
Mitgliedschaftsoperationen, aber **keinen dedizierten
`Groups.map-role`-Scope**. Die für Permora notwendige delegierte
Gruppenrollenzuweisung wird in den isolierten Vertragstests daher mit
`Groups.manage` an der konkreten Zielgruppe freigegeben. Dieser Scope
berechtigt nach Herstellerdokumentation zur Gruppenverwaltung, **nicht
ausschließlich** zur Rollenzuweisung. Ein berechtigter bzw. kompromittierter
Connector kann dadurch potenziell weitere Gruppenoperationen ausführen.

Der Keycloak-Issue [#45269](https://github.com/keycloak/keycloak/issues/45269)
fordert explizit `Groups.map-role` und wurde als Duplikat zu
[#38806](https://github.com/keycloak/keycloak/issues/38806) geschlossen.
Daraus folgt **nicht**, dass die Funktion in der getesteten Version
verfügbar wäre. Das neue echte
`scripts/ci-keycloak-contract.py`-Sicherheitsszenario prüft mit dem
delegierten Gruppen-Mapping-Konto eine von Rollenmapping unabhängige
Gruppenänderung und stellt die Testgruppe gegebenenfalls mit dem
isolierten Bootstrap-Admin wieder her. Das testet den tatsächlichen
Berechtigungsumfang statt ihn aus einer scheinbar minimalen Policy
abzuleiten.

**Sicherheitsbewertung:**

- *Technisch funktionsfähig:* Rollenmapping eigener Gruppen funktioniert
  in den nachgewiesenen isolierten Positivtests.
- *Offiziell unterstützt:* Keycloak dokumentiert den breiten
  `Groups.manage`-Scope, **nicht** einen separaten reinen
  Gruppen-Rollenmapping-Scope in 26.8.1.
- *Best Practice:* getrennte Worker-Serviceidentität mit möglichst
  gruppenbezogenem FGAP, kein direkter Browser-/Anwenderzugriff auf das
  Connector-Token; echte negative API- und Keycloak-Tests.
- *Sicherheitsseitig empfehlenswert:* Für Produktion das zusätzliche
  Gruppenverwaltungsrisiko bewerten und zentral freigeben, Alternativen
  mit feinerer Berechtigungsgrenze oder das Abwarten eines offiziell
  verfügbaren `map-role`-Scopes prüfen. Das Portal-API-Whitelisting
  begrenzt normale Befehle, **nicht** die Wirkung eines kompromittierten
  Servicekontos. Keine Produktionsfreigabe als `map-roles-only` ausgeben.

Primärquellen: [Keycloak 26.8.1 Server Administration – Groups Resource Type](https://www.keycloak.org/docs/26.8.1/server_admin/)
und [Keycloak upstream #45269](https://github.com/keycloak/keycloak/issues/45269).

**Erweiterte, direkt gegen die Keycloak-Admin-REST-API ausgeführte
Negativmatrix:** Der Vertragstest überprüft mit *derselben* delegierten
Serviceidentität neben der bekannten zulässigen Umbenennung einer eigenen
Gruppe auch die verbotene Änderung/Löschung fremder Gruppen, das unzulässige
Ändern der Gruppenmitgliedschaft und unerlaubte Fremd-Client-Rollen.
Ein zweites, isoliertes Admin-Konto bestätigt jeweils den tatsächlichen
Istzustand. Die Probe ist erst nach einem grünen CI-Lauf am konkreten
Commit als ausgeführt nachgewiesen. Vollständiger Ablauf und die
betrieblichen Gegenmaßnahmen: [FGAP-Gruppenrechte: Test- und
Abnahmematrix](keycloak-fgap-acceptance.md).

FGAP-Policy-Erstellung wird in diesem Stand nicht automatisch ausgerollt. Das ist ein offener Roadmap-Punkt, keine versteckte fertige Funktion.

## Delegierte Gruppen-Rollenzuweisungen: standardmäßig gesperrt

**Security gate:** In Keycloak 26.8.1 gewährt `Groups.manage` mehr Befugnisse als
das Hinzufügen von Client-Rollen. Der reale FGAP-Test in
[PR #44](https://github.com/Cenn0/Permora/pull/44) bestätigt, dass das
delegierte Konto auch Gruppenmetadaten ändern kann. Deshalb verweigert der
Keycloak-Connector **neue** Gruppenberechtigungen standardmäßig mit
`403 group_mapping_disabled`.

Eine zentrale IAM-Stelle kann nach dokumentierter Risikofreigabe pro
Realm `Keycloak:Realms:<realm>:AllowGroupRoleGrants=true` setzen.
**Zusätzlich ist `Keycloak:Realms:<realm>:ApprovedGroupIds` mit den exakt
zugelassenen Keycloak-Gruppen-IDs erforderlich** (kein Gruppenname, kein
Permora-interner Identifier). Fehlende/leere Listen blockieren alle neuen
Gruppengrants trotz aktiviertem Realm-Schalter. API und Worker müssen die
identische Liste über getrennte nicht geheime Konfigurationen erhalten.
Entzüge bleiben auch für Gruppen außerhalb der Allowlist möglich.
**Diese beiden Schalter verengen ausdrücklich nicht den tatsächlich von
Keycloak erteilten `Groups.manage`-Token-Scope.** Das ist nur eine
zusätzliche anwendungsseitige Kontrollschicht; #46 bleibt offen.
Er setzt ausdrücklich die Annahme voraus, dass das breitere Servicekonto
risikobewertet, auf zulässige Gruppen begrenzt und aktiv überwacht wird.
Für realen Produktivbetrieb sind alle Scope-/Token-/Admin-REST-Aktionen
dieses Kontos vollständig abzunehmen.

**Zwei getrennte Prüfgrenzen:** Bereits die API lehnt einen neuen
`GrantAccess`-Antrag für eine Gruppe synchron mit
`403 group_mapping_disabled` ab, bevor ein Approval- oder Worker-Auftrag
persistiert wird. Die API liest dafür ausschließlich das nicht geheime
`Keycloak:Realms:<realm>:AllowGroupRoleGrants`-Flag. Sie benötigt **keine**
`Roles`-, `Assignments`- oder `Directory`-Servicecredentials. Der Worker
prüft denselben Schalter im Keycloak-Connector **erneut unmittelbar vor dem
Remote-Write**. Ein IAM-geprüfter Opt-in muss deshalb in der API- und
Worker-Konfiguration explizit und konsistent stehen. Ein API-`202 Accepted`
beweist weiterhin ausschließlich die Annahme des Auftrags; nur ein
Keycloak-Readback nach dem Worker-Lauf belegt eine tatsächliche Zuweisung.

Sicherheitskritische **RevokeAccess**-Aufträge bleiben auch bei deaktivierten
Gruppengrants möglich; ein Incident darf nicht daran scheitern, dass die
Erweiterungssperre aktiv ist. Das Umschalten ändert keine bestehenden
Keycloak-Rollenzuweisungen rückwirkend. Aufräum-, Drift- und
Widerrufspflichten bleiben bestehen. Siehe
[Security-Blocker #46](https://github.com/Cenn0/Permora/issues/46).

## 4. Ressourcen und Token-Vertrag

Client-Rollen `perm.orders.read` und `role.operator` werden von Permora angelegt. Nur `role.operator` wird zugewiesen; seine eigenen `perm.*`-Kinder werden in Keycloak als Composite definiert. Andere Clients und Realm-Rollen sind ausgeschlossen. `Full Scope Allowed` bleibt deaktiviert.

Der optional gebundene Login-Client benötigt passende Role Scope Mappings und eine korrekte Audience für die Ziel-API. Der Connector kann Rollenscope-Zuordnungen ergänzen, provisioniert aber **keinen vollständigen Audience-/Mapper-Vertrag automatisch**. Mit einem echten Access Token prüfen, dass ausschließlich relevante `resource_access[clientId].roles` erscheinen und die erwartete `aud` enthalten ist.

Eine Anwendung muss JWT-Signatur, Issuer, Audience und Lebensdauer validieren und Berechtigungen serverseitig durchsetzen. Ein neuer Permission-Name erzeugt keine neue Geschäftslogik. Das SDK `Permora.Authorization` wertet nur die Berechtigungen des konfigurierten Clients aus; es ersetzt keine Tokenvalidierung.

## 5. Aktivierung

Nachdem Ownership, Flow-/Scope-Konfiguration, FGAP und Tokens geprüft sind, den Rechte-Client in Keycloak aktivieren. Im zentralen Portal die Bindung bestätigen; dadurch wird ein Reconcile-Auftrag erstellt. Nur eine technisch saubere Rückprüfung kann den Portalstatus auf `Active` setzen. Teamrechte selbst können den Aktivierungsschritt nicht umgehen.

## Verbindliche echte Abnahmematrix

- Zwei Teams mit je einer App: Fremd-App-IDs und fremde Rollen-/Permission-IDs per direktem HTTP-Aufruf testen; 404/403 und keinerlei Remote-Änderung nachweisen.
- Roles-Account darf Zielrollen bearbeiten, fremde Clients und Realm-Einstellungen müssen scheitern. Rollen zu `realm-management` oder fremden Clients einschleusen: abweisen.
- Zuweisungsaccount kann nur zugelassene Rollen und Ziele mappen. Insbesondere Berechtigungen am Zielbenutzer getrennt prüfen.
- Directory-Listen vollständig gegen einen zentralen Referenzbestand vergleichen; LDAP-Federation, nicht importierte Benutzer, Untergruppen und deaktivierte Konten berücksichtigen.
- Composite-Readback, User-/Group-Mapping, Scope-Mapping und tatsächliches Token prüfen.
- Fehlendes Secret, HTTP 403/429/500, Timeouts, JSON `null`, Verbindungsabbruch nach Remote-Commit und Prozessabbruch vor lokalem Commit testen.
- Portal-Logout, Sessionablauf, CSRF, MFA-Anforderung, Audience-Mismatch und entfernte Owner/Approver prüfen.

Diese Matrix wurde hier **nicht** gegen eine laufende Keycloak-Instanz ausgeführt.

## Primärquellen

- [Keycloak 26.8.1 Release und Security Fixes](https://www.keycloak.org/2026/10/keycloak-2681-released)
- [Server Administration / delegierte Administration](https://www.keycloak.org/docs/latest/server_admin/)
- [Admin REST API](https://www.keycloak.org/docs-api/latest/rest-api/index.html)
- [Authorization Services: separates erweitertes Modell](https://www.keycloak.org/docs/latest/authorization_services/)


## Auditierte zentrale IAM-Aktivierung (nachgehärtet)

Vor dem erstmaligen Wechsel von `AwaitingIam` nach `Active` muss ein `PlatformAdmin` **tatsächlich** folgende vier Punkte geprüft haben: FGAP-Isolation fremder Anwendungen, Rollen-Connector mit Minimalrechten, Assignment-/Directory-Zugriffe auf zulässige Zielkonten sowie ein Access Token mit korrekter Audience und Scope-Mappings.

Die zentrale Administration bestätigt dies über `POST /api/v1/admin/applications/{id}/iam-approval` mit `expectedRevision`, den vier Prüfflags und `evidenceReference`. Das Portal speichert Prüfer, Zeitpunkt und betroffene Keycloak-Client-UUID/ID, protokolliert ein Audit und erzeugt einen Reconcile-Auftrag. Ohne passenden Nachweis verweigert der Worker die Aktivierung mit `iam_approval_required`. Änderung der Bindung beziehungsweise erneutes Zurückstellen nach `AwaitingIam` entwertet die Bestätigung.

**Ein Häkchen ist kein automatischer FGAP-Nachweis.** Das Portal legt Keycloak-FGAP-Policies derzeit nicht selbst an. Die negative Zwei-Team-Testmatrix muss im echten Zielsystem ausgeführt werden. Der Angular-Dev-Proxy enthält jetzt auch `/signin-oidc` und `/signout-callback-oidc`; die Demo-Anmeldung ist weiterhin kein echter OIDC-Test.


## Step-up und privilegierte Änderungen (P1)

Für **alle mutierenden** Portal-API-Endpunkte unter `/api/v1` verlangt die API eine frische, vom vertrauenswürdigen OIDC-ID-Token übernommene `acr`- und `auth_time`-Kombination. Die produktive Standardkonfiguration nutzt `Portal.PrivilegedAcr = "2"` und `Portal.PrivilegedAuthenticationMaxAgeMinutes = 10`; das sind **Permora-Vorgaben**, keine Keycloak-Defaults. Der Keycloak-Browser-Flow muss tatsächlich ein höheres Assurance Level erzeugen, und der verwendete OIDC-Client muss den `acr`-Mapper/Client-Scope korrekt ausliefern.

`GET /bff/step-up` startet eine OIDC-Neuanmeldung mit **essentiellem** ID-Token-`acr`, `auth_time`, `max_age=0` und `prompt=login`. Der Server validiert beim folgenden API-Write unabhängig das **zurückgegebene**, signierte Assurance-Niveau sowie das Alter der Authentifizierung. Fehlen Claims oder ist der Authentifizierungszeitpunkt veraltet, liefert der Server `403 step_up_required` und nimmt **keinen Änderungsauftrag an**. Die UI verlinkt daraufhin zur erneuten MFA-Bestätigung.

**Betriebsgrenzen:** Eine OIDC-Anforderung allein erzwingt keine MFA in einem ungeeigneten Keycloak-Browser-Flow; der Flow muss unter Keycloak 26.8.1 als höheres Level konfiguriert und durch einen **positiven** echten MFA-End-to-End-Test abgenommen werden. Die Isolation `EnableDevelopmentLogin = true` ist ausschließlich für Development auf Loopback zugelassen und **simuliert keine MFA**. Auch MFA verhindert weder einen kompromittierten BFF noch die Weiterverwendung bereits ausgegebener JWTs.

**Historischer Teststand:** `SessionAssuranceTests` prüfen die lokale fail-closed Claim-Policy. Der zunächst fehlende positive LoA2-/OTP-Test ist seitdem für die isolierte Keycloak-26.8.1-Testumgebung [erfolgreich](https://github.com/Cenn0/Permora/actions/runs/39011230492). Das positive Produktions-LoA einer realen Unternehmensumgebung muss weiterhin gesondert nachgewiesen werden. Siehe [Teststrategie](testing.md) und [Security](security.md).

## Nachgewiesene LDAPS-Föderation und FGAP-Abgrenzung (18.10.2026)

Der [echte Systemtest](https://github.com/Cenn0/Permora/actions/runs/39117753117)
startet einen temporären OpenLDAP-Server mit geprüftem LDAPS-Zertifikat,
konfiguriert Keycloak 26.8.1 als LDAP-Föderation und prüft **beide**
Keycloak-Modi:

- Importierte LDAP-Benutzer: Paging, direkte delegierte Client-Rollen-Zuweisung
  und -Entzug, verschachtelte Gruppen und effektive Gruppenrollen.
- Nicht importierte Benutzer mit `READ_ONLY`: Fremd-ID-Ablehnung, dynamische
  Gruppenmitgliedschaft und Rollenvererbung. **Direktes Rollenmapping auf
  einen solchen Benutzer ist mit der geprüften Keycloak-Konfiguration nicht
  unterstützt; der Test verlangt die tatsächliche Ablehnung.**
- Delegierte Servicekonten: eigenes Client-Ziel positiv; fremde Benutzer,
  Clientrollen, Gruppen, Realmänderungen, Impersonation und Passwort-Reset
  negativ.
- Das Keycloak-Gruppenrecht `manage` ist **umfangreicher** als eine
  ausschließliche Rollen-Mapping-Berechtigung. Dies ist ein residualer
  Vertrauensbereich, keine nachgewiesene gruppenspezifische Minimalrechtelösung.

**Microsoft Active Directory wurde damit NICHT getestet.** AD-spezifische
Account-Flags, MSAD-Mapper, Kerberos/SPNEGO, Domänen-/Forest-Trusts,
Gruppenrichtlinien, gesperrte/abgelaufene Konten und reale Enterprise-Readbacks
müssen gegen eine dafür freigegebene Testdomäne verifiziert werden.

Ein unter OpenLDAP erfolgreiches Szenario darf nicht als offiziell bestätigte
Microsoft-AD-Kompatibilität, produktive Rechteisolation oder vollständige
FGAP-Matrix gekennzeichnet werden. Siehe
[Release-Gates](release-checklist.md).

## FGAP service-account token scope for complete directory reads

A **realm-wide read** account is a separate trust boundary from application role
creation and role mapping. Keycloak 26.8.1 requires a token able to list users,
groups, nested groups and effective group membership to build a complete
reconciliation snapshot. The `realm-management:view-users` role allows broad
read access to users and groups; it is **not** an application- or group-scoped
least-privilege entitlement. Central IAM must review and authorize this visibility
in each actual realm.

For confidential Keycloak service-account clients with
`fullScopeAllowed=false`, assigning `view-users` to the service-account user
**alone is insufficient**. Add the same `realm-management` client role to the
**service client's role-scope mappings** (directly or through an appropriate
linked client scope). The issued token's effective roles are the intersection
of account roles and client scope. The real acceptance fixture verifies all
four actual Admin REST reads with that token:
`GET /users`, `GET /groups`, `GET /groups/{id}/members`, and
`GET /groups/{id}/children`. The missing scope was independently reproduced
as HTTP 403 when `fullScopeAllowed=false`. Do **not** enable full scope as a
shortcut.

The worker's independent **Roles** and **Assignments** profiles remain
application-scoped permissions in the production model. The joined
OpenLDAP integration fixture deliberately reuses an existing, previously
validated limited mapping account for those two roles to avoid introducing
overlapping Keycloak group FGAP permissions in a disposable realm. This is a
test-fixture limitation, **not** a recommendation to combine production secrets.

The [joined worker acceptance](../scripts/ci_federation_worker.py) tests
external LDAP membership drift and actual Keycloak revocation against a
disposable imported-LDAP user. The separate [federation contract](../scripts/ci-federation.py)
still checks non-imported read-only identities. Microsoft AD, multi-host
outage scenarios, Keycloak group `manage` overbreadth, organizational rights
review and upstream token leakage are **not** certified by these tests.

Reference: [Keycloak Service Accounts and Token Role Mappings](https://www.keycloak.org/docs/latest/server_admin/).

# Sicherheitsmodell und Grenzen

> **Stand der Nachweise:** Für aktuelle ausgeführte Tests gilt [docs/ci.md](ci.md). Ältere, in diesem Dokument enthaltene Hinweise auf noch nicht ausgeführte Tests beschreiben historische Review-Zeitpunkte und dürfen nicht als aktueller CI-Status gelesen werden. Die produktive Sicherheitsfreigabe bleibt weiterhin ausstehend.

## Vier getrennte Aussagen

**Funktioniert grundsätzlich:** dokumentierte Keycloak-Admin-APIs können anwendungsspezifische Rollen und Mappings bearbeiten; der Connector implementiert diesen Weg. Die tatsächliche Laufzeitkompatibilität dieses Codes ist noch nicht vollständig geprüft.

**Offiziell unterstützt:** Keycloak dokumentiert Admin REST, Client Roles/Composites und FGAP. Das bedeutet keinen offiziellen Keycloak-Support für Permora oder für jede Kombination unserer drei Service-Account-Profile.

**Architektonische Empfehlung:** Benutzerverwaltung über ein begrenztes BFF, Default Deny, appbezogene Ressourcenauflösung, versionierte Aufträge und getrennte Provisioning-Geheimnisse statt freier Admin-API-Weiterleitung.

**Sicherheitsseitig empfehlenswert:** erst nach echtem Minimalrechte-Nachweis, MFA/Step-up-Konzept, Abhängigkeitenprüfung, Betriebsabnahme und unabhängiger Sicherheitsprüfung produktiv einsetzen. Diese Freigabe liegt nicht vor.

## Threat Model

| Angreifer / Fehler | Kontrolle | Restgrenze |
|---|---|---|
| Team manipuliert fremde App-/Rollen-ID | Owner-Auflösung im Backend, eigene Permission-IDs, Markerprüfung | API-Negativtests noch nicht ausgeführt |
| Browser injiziert Realm-/Client-Konfiguration | typisierte Commands, unbekannte JSON-Felder verboten, kein Admin-Proxy | Backend-Kompro­mittierung bleibt hochkritisch |
| Owner fügt eigene High-Risk-Rechte hinzu | aktuelle Rollen-/Gruppenprüfung, vier Augen | Gruppenänderungen nach Prüfung sind kein atomarer IAM-Vorgang |
| Leere Gruppe erhält zwei SoD-Rollen | direkte Gruppenrechte zusätzlich zu heutigen Mitgliedern prüfen | zukünftige externe Gruppenänderungen brauchen kontinuierliche SoD-Prüfung |
| Veraltete oder deaktivierte Entscheider | Mandate und frische, aktivierte Directory-Identitäten vor Verarbeitung prüfen | Synchronisationsintervall und bestehende Sessions begrenzen Reaktionszeit |
| Nach Remote-Write bricht Verbindung ab | Idempotenz, Read-after-write, Backoff, Blockierung unklarer Mutationen | keine atomare DB-/Keycloak-Transaktion |
| FGAP blendet Teile des Bestands aus | IAM-Abnahmematrix muss vollständige Sicht bestätigen | HTTP 200/leer beweist keine vollständige Sicht |
| Zugriff auf Browser-Storage | keine Keycloak-Service-Tokens oder Refresh Tokens im Angular-Code | XSS kann weiterhin Aktionen in einer aktiven Sitzung auslösen |
| Datenbank-Angreifer | append-only Audit-Trigger und Hashkette | DB-/Infrastruktur-Admins können beides umgehen; kein WORM-Nachweis |
| Ausfall des Portals | Anwendungs-Login bleibt bei Keycloak | Entzüge/Freigaben können verspätet sein; keine harte Widerrufs-SLA |

## Privilegienprofile

`roles`, `assignments`, `directory` haben getrennte Zugangsdaten. Der zusätzliche Provisioner verwendet eine vierte, separat bereitgestellte Identität. Die aktuelle Konfiguration ist **pro Realm/Vertrauensdomäne**, nicht automatisch ein Service Account pro Anwendung. Ein kompromittierter Rollen-Connector kann daher einen größeren Bereich als eine einzelne App treffen. Diese Grenze nicht als unabhängige Tenant-Isolation ausgeben.

Client-`manage` kann mehr als Rollen-CRUD erlauben; FGAP ersetzt die feste Backend-Operationsliste nicht. Keine pauschalen `realm-admin`-/`manage-users`-Zuweisungen an Anwendungsteams. Admin API nur aus internen Diensten erreichbar, TLS ohne Zertifikatsumgehung, keine automatischen Redirects des Connectors.

## Sitzungen und Browser

BFF-Cookie HttpOnly/Secure, OIDC Code + PKCE, CSRF für alle schreibenden API-/BFF-Aufrufe, 30 Minuten absolute lokale Sitzung. Logout beendet die **Permora-Sitzung**, nicht die gemeinsame Keycloak-SSO-Sitzung. Entwicklungsauthentifizierung ist explizit und nur auf Loopback in Development zulässig. Nicht hinter einem öffentlich erreichbaren Proxy weiterleiten.

CSP erlaubt für Angular derzeit Inline-Styles, aber kein `unsafe-inline` für Skripte. Nonce-basierte Styles, Trust-Proxy-Konfiguration und eine vollständige browsergestützte CSP-Prüfung stehen aus. Die HTTPS-Terminierung muss gesichert erfolgen; `PublicOrigin` ist eine feste vertrauenswürdige Deployment-Einstellung, keine User-Eingabe.

Der Portalserver prüft nun für **alle mutierenden `/api/v1`-Endpunkte** ein spezifisches signiertes ID-Token-`acr` sowie ein frisches `auth_time` und verweigert die Änderung mit `403 step_up_required`, wenn diese Nachweise fehlen. `/bff/step-up` fordert das erforderliche Assurance-Niveau per essential OIDC claims an. **Nicht bewiesen:** dass ein konkreter produktiver Keycloak-Browser-Flow tatsächlich MFA mit korrektem LoA erzeugt; eine echte positive MFA-E2E-Abnahme steht noch aus. Fehlkonfigurierte IdP-LoA-Zuordnungen wären eine kritische Sicherheitslücke.

## Identitäten, Gruppen und Entzug

Directory-Suche liefert minimale Felder, mindestens drei Zeichen und maximal 25 Ergebnisse. Ein fehlender Eintrag wird nicht als Löschsignal behandelt. Er behält seinen alten Synchronisationszeitpunkt und ist später nicht mehr frisch genug für Erweiterungen. Explizite Deaktivierung löst Entzüge aus. Vollständiger HR-Lifecycle und revisionssichere personenbezogene Suchprotokollierung sind offen.

Gruppenrechte können durch spätere externe Mitgliedschaftsänderung wirksam werden. Das Portal verwaltet keine AD-Gruppenmitgliedschaften. Referenzierte Gruppen müssen vollständig lesbar sein; verschachtelte oder sehr große Gruppen können an Sicherheitslimits scheitern. Es gibt noch keine laufende Garantie gegen neu entstehende gruppenübergreifende SoD-Konflikte.

Bereits ausgegebene JWTs können nach einem Rollenentzug weiter Rechte transportieren. Kurze Laufzeiten und appseitige Widerrufsstrategien festlegen; „Keycloak-Mapping entfernt“ ist nicht „alle verteilten Zugriffe sofort ungültig“.

## Produktfreigabe

Kein „Enterprise-ready“, keine Zertifizierung, kein Pentest-Erfolg und keine vollständige Roadmap-Abnahme behauptet. Offene Freigabepunkte stehen in [Roadmap](roadmap.md) und [Prüfbericht](review.md). Bei diesem neuen Projekt bestehen noch keine zugesagten Reaktionszeiten oder unterstützten Produktionsversionen.


## Ergänzende Sicherheitsbarrieren

Der Aktivierungs-Worker verlangt vor dem ersten `Active`-Status eine zentrale IAM-Attestierung mit konkreter Client-UUID/ID und anschließend einen sauberen Remote-Abgleich. Die Attestierung ist organisatorisch, keine kryptografische Prüfung von FGAP. Falsch konfigurierte weitreichende Keycloak-Service-Accounts bleiben ein wesentliches Risiko und dürfen vor einer echten Zwei-Team-Abnahme nicht als sicher gelten.

Eine Rollenlöschung liest vor dem DELETE in Keycloak die zugehörigen Benutzer/Gruppen aus und verweigert die Löschung bei verbliebenen Zuweisungen. Dies setzt eine **vollständige, nicht durch FGAP gefilterte** Sicht des Connector-Accounts voraus. Nicht sichtbare Zuordnungen sind kein Nachweis ihrer Abwesenheit. Keycloak-Client-Flow-Flags müssen explizit sicher gesetzt sein; ausgelassene/nullable Flags reichen nicht.


## Ergänzende Risiko- und Releasefreigabe

[Threat Model](threat-model.md) und [Release-Gate-Matrix](release-checklist.md) trennen getestete Technik von operativer und unabhängiger Sicherheitsfreigabe.

## Unvollständige Gruppenansicht bei bestehenden Freigaben

Reconciliation darf eine zugewiesene Keycloak-Gruppe nicht als **driftfrei** behandeln,
wenn die effektive Mitgliedschaft unvollständig oder älter als das für
Rechteänderungen erlaubte Freshness-Fenster ist. Permora meldet dann
`group_membership_unverified` als Konflikt. Ebenfalls abgewiesen wird ein
Directory-Prinzipal aus einem fremden Realm, selbst wenn dessen UUID mit der
gesuchten ID übereinstimmt.

Diese Sicherung ist **kein Beweis für einen vollständigen LDAP-/AD-Lifecycle**:
Ein unvollständiger Snapshot führt nicht allein zu einer vermuteten Massenlöschung,
weil unbekannte Mitgliedschaften nicht als belastbare Entzugsentscheidung gelten.
Die operative Gegenmaßnahme besteht aus Alarmierung, korrigierter vollständiger
Directory-Synchronisation, anschließender SoD-Prüfung und dokumentierter
Freigabe des Soll-/Ist-Abgleichs. Bereits ausgestellte JWTs bleiben gesondert zu
berücksichtigen.

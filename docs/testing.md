# Teststrategie und Ausführung

## P4: vorhandene, aber unsichtbare Zuweisungen (20.10.2026)

Neue `PolicyTests` adressieren einen bislang nicht ausreichend
abgesicherten Grant-/SoD-Pfad: Frühere Rollen-Zuweisungen können weiterhin
in Keycloak bestehen, obwohl eine Benutzer- oder Gruppenidentität aus dem
heutigen Directory-Snapshot fehlt. Ein solcher Eintrag darf weder
als leere Gruppe noch als sicher nicht vorhandene effektive Rolle gelten.

Vor **GrantAccess** und **UpdateRole** validiert der Domain-Plan sämtliche
bestehenden Zuweisungsziele anhand eines eindeutigen, frischen
In-Realm-Snapshots (einmalig nach lokaler ID indiziert):

- Fehlendes Ziel oder nur fremder Realm: `directory_incomplete` (409).
- Doppelte lokale Identität: `ambiguous_directory_identity` (409).
- Ungültige Principal-Art/Keycloak-ID: `invalid_directory_identity` (409).
- Veralteter/Zukunfts-Zeitstempel: `stale_identity` (409).
- Unvollständige zugewiesene Gruppe: `incomplete_group` (409).
- Deaktivierte/geschützte zugewiesene Identität:
  `ineligible_existing_identity` (409).

**RevokeAccess bleibt davon ausgenommen**: Ein notwendiger Entzug darf
durch den unvollständigen Snapshot nicht wegen dieser neuen Grant-Sperre
verhindert werden. Das tatsächliche Keycloak-Ziel muss der Connector
separat eindeutig verifizieren; bei unklaren Remote-IDs ist manuelle
Klärung nötig.

Die Tests enthalten außerdem einen zulässigen Grant bei vollständiger
Directory-Sicht. Erst ein erfolgreicher `dotnet test`- und
GitHub-CI-Lauf am konkreten Commit belegt die Ausführung. Die Regel
ersetzt weder echte LDAP-/AD-Fehlerszenarien noch sofortigen JWT-Widerruf.

## Erweiterte Keycloak-FGAP-Gruppenadministrationsmatrix (20.10.2026)

`scripts/ci-keycloak-contract.py` erweitert den isolierten **echten
Keycloak-26.8.1-Test**. Nach tatsächlicher Zuordnung einer eigenen
Anwendungsrolle zu Gruppe A prüft der Test mit **demselben delegierten
Bearer-Token** direkte Admin-REST-Aufrufe:

- Die ausdrücklich **zu weit gefasste** Berechtigung `Groups.manage`
  erlaubt auch die Umbenennung der berechtigten Gruppe; eine
  `finally`-Wiederherstellung verhindert zurückgelassene Änderungen.
- Umbenennen/Löschen einer fremden Gruppe ist abzuweisen und wird mit
  einem unabhängig authentifizierten Readback kontrolliert.
- Anlegen oder Aufheben einer Mitgliedschaft der eigenen Zielgruppe
  darf ohne zusätzliche Mitgliederberechtigungen nicht möglich sein.
- Ein verbotener Grant auf eine fremde Gruppe oder für Client B
  muss ohne Remote-Nebenwirkung bleiben (getrennte Readbacks).

Verifikationsstufen: **Quellcode vorhanden** genügt nicht; die realen
Keycloak-REST-Proben müssen im Keycloak-CI-Job des **exakten Commits**
erfolgreich sein. Diese Matrix ist **keine** Microsoft-AD-Abnahme und
keine vollständige Bewertung der effektiven Berechtigungen eines
späteren Betriebs-Servicekontos. Testreihenfolge und Betreiberpflichten:
[FGAP-Acceptance](keycloak-fgap-acceptance.md). Der Sicherheitsblocker
[Issue #46](https://github.com/Cenn0/Permora/issues/46) bleibt trotz
grünem Test bestehen, solange nur `Groups.manage` verfügbar ist.

## Directory-Reconciliation: Negativregressionen (20.10.2026)

Die neuen Tests in `PolicyTests` und `ConnectorTests` prüfen das Verhalten
bei unzuverlässigen Verzeichnisdaten. Ohne eindeutigen, aktuellen und
ausreichend validierten Snapshot darf ein Keycloak-Soll-/Ist-Abgleich **nicht**
als erfolgreich abgeschlossen oder als Grundlage einer Aktivierung verwendet
werden. Die Prüfungen verwenden einen aufgezeichneten HTTP-Testconnector;
sie sind **keine** Microsoft-AD-, LDAP- oder Keycloak-Server-End-to-End-Abnahme.

| Ursache | API/Policy beziehungsweise Worker | Reconciliation |
| --- | --- | --- |
| Zwei Einträge mit derselben lokalen Principal-ID im selben Realm | Neue Grants vor dem Queueing mit `ambiguous_directory_identity` blockieren; Worker unterbindet mutmaßliche Remote-Writes | Driftfinding `ambiguous_directory_identity`, kein erratener REST-Pfad |
| Unbekannter Principal-Typ oder leere Keycloak-ID | Bereits bestehende Typprüfung für Grants und Entzüge; keine implizite Umdeutung zu einer Gruppe | `invalid_directory_identity`, kein Zugriff auf erratene Remote-Ressource |
| Veralteter oder über eine Minute in der Zukunft liegender Directory-Zeitstempel | Grant wird über `Policy.Fresh` zurückgewiesen | `stale_directory_identity` anstelle einer vermeintlich sauberen Reconciliation |
| Deaktivierte oder geschützte zugewiesene Identität | Neue Grants verboten | `ineligible_assigned_identity`, unabhängig vom gelesenen Remote-Mapping |

Die Befunde führen beim normalen Reconcile-Worker zu `Conflict` und einem
korrelierten `drift.detected`-Audit statt zur automatischen Heilung. Auch
bereits ausgegebene Keycloak-Access-Tokens werden dadurch **nicht** sofort
widerrufen. Ein administrativ verifizierter Entzug bleibt erforderlich;
korrupten Identitätszuordnungen darf kein blindes Remote-Delete folgen.

Ausführen: `dotnet test tests/Permora.Tests/Permora.Tests.csproj -c Release`
oder über [Permora CI](../.github/workflows/ci.yml). Vor einer Einstufung
als **nachgewiesen** sind die Checks am **exakten Commit** zu prüfen.

## Neuester nachgewiesener Integrationsumfang (18.10.2026)

Maßgeblich ist der getestete Commit
[`c0a5e0eed070`](https://github.com/Cenn0/Permora/commit/c0a5e0eed07006c20373ea08ff9b162ef831da52)
und die [vollständige technische Evidenz](release-checklist.md). Reale
Keycloak-LoA2/TOTP-Positiv- und Passwort-Negativtests sind bestanden; die
OpenLDAP-Föderation deckt importierte und nicht importierte, verschachtelte
und wechselnde Gruppen ab. Zwei reale Worker wurden bei Keycloak-Ausfall,
Prozessabbruch nach Remote-Commit und Datenbankwiederherstellung getestet.
Ein separater, CI-grüner axe-core-Lauf prüft 54 Accessibility-Konstellationen.
Drei Image-SBOMs und signierte Draft-Artefakte wurden erstellt.

**Neue Tests (vor CI-Abnahme):** `GroupGrantAdmissionTests` prüfen die
synchronen Gruppen-Grant-Sperren ohne persistierten Auftrag, ausdrückliche
Realm-Freigabe, Fremdteam-Isolation, unbekannte Directory-Typen und den
Notfall-Entzug. `scripts/ci-api-smoke.py` ergänzt direkte ASP.NET-HTTP-
Negativtests in einer isolierten PostgreSQL-Fixture: `403` und keine Queue
bei Default-Deny, `202` für den Entzug, `404` für fremde Akteure
und `202 AwaitingApproval` nur bei **Realm- plus exakter
Keycloak-Gruppen-ID-Freigabe**. Realm-only-Opt-in wird ebenfalls
mit `403` abgewiesen. Der Worker prüft die Group-ID zusätzlich
unabhängig; falsche oder fehlende IDs sind regressionsgetestet.
Diese Fixture besitzt **keine
echten Keycloak-Grants**; die Connector- und FGAP-Tests sind unabhängige
Schichten. Erst der erfolgreiche CI-Lauf auf dem neuen Commit belegt diese
zusätzliche Testabdeckung.

**Nicht damit bewiesen:** Kompatibilität mit echtem Microsoft AD,
vollständige produktive FGAP-/Identity-Matrix, Multi-Host-Failover,
Security-/Privacy-Freigabe, manuelle Screenreader-Abnahme oder rechtlich
freigegebene Lizenz- und Release-Unterlagen. Nachfolgende ältere Testzahlen
und offenen Punkte sind historische Teilberichte; bei Widersprüchen gilt
diese aktuelle Abgrenzung.


## Ergänzende Revocation-Queue-Regressionen (vor CI-Abnahme)

`RevocationPriorityTests` ergänzen die bereits getesteten automatischen
Entzüge um **manuelle** Notfall-Entzüge: Vorrang auch vor älteren Grants im
Backoff, Stopp aller anderen Writes bei noch offener Revocation, Fortführung
trotz zwischenzeitlich geänderter Revisionsnummer und erneute Prüfung des
aktuellen Team-Mandats. Mindestens ein Test verwendet eine **echte isolierte
PostgreSQL-Transaktion** mit dem produktiven Queue-SQL; der Remote-Connector
ist in diesem Test simuliert. Ein echter Keycloak-Gegencheck und ein
produktives p95/p99-SLO werden dadurch nicht ersetzt.

Die getrennte Gruppen-Grant-HTTP-Regression aus PR #49 wurde im
[erfolgreichen CI-Lauf #39171097527](https://github.com/Cenn0/Permora/actions/runs/39171097527)
getestet und gemergt; sie ist keine produktive FGAP-Betriebsfreigabe.

## Technischer Testnachweis – 17. Oktober 2026

[Integrierter OIDC-/MFA-Negativlauf](https://github.com/Cenn0/Permora/actions/runs/38985065471): Echter Chromium-Browser mit Keycloak-26.8.1-Login, Cookies, ASP.NET-BFF-Sitzung und serverseitigem `403 step_up_required` bei Passwort-only; dazu Builds, xUnit/PostgreSQL, Angular/Playwright, FGAP-Contract, Docker und Lockfile-Gates. [Cross-Team-IDOR-Lauf](https://github.com/Cenn0/Permora/actions/runs/38985300214): direkter REST-Zugriff auf fremde Anwendungs-/Operation-/Audit-/Review-IDs führt in den getesteten Fällen zu HTTP 404, verbotene zentrale Administration zu 403. Das Validierungs-Reihenfolgeproblem wurde korrigiert. Die CI enthält außerdem den isolierten PostgreSQL-Restore- und den offline SBOM-Komponententest.

**Unbewiesen:** erfolgreicher zweiter MFA-Faktor mit dem tatsächlichen Unternehmens-LoA, volle produktive FGAP-/LDAP-Matrix, verteilte Recovery und Last-/Sicherheitstests. Die älteren Testzahlen in den nachfolgenden Abschnitten beziehen sich auf konkrete historische Workflows.


**Aktueller Vollnachweis der vorhandenen CI:** [Run #38946163282](https://github.com/Cenn0/Permora/actions/runs/38946163282), 7/7 Pflichtjobs grün, 102/102 xUnit ohne Skips einschließlich PostgreSQL, 32 isolierte TypeScript-Tests, 8/8 Playwright, echte Keycloak 26.8.1 FGAP-Client- und User-/Group-Positiv-/Negativfälle, gesperrte Dependency-Restores und tatsächlicher Docker-Image-Build. [Evidenz und offene Grenzen](evidence/ci-2026-10-16-final.md).


**Container-Test:** Die CI baut das tatsächliche Multi-Stage-Image (`deploy/Dockerfile`) ausschließlich aus gesperrten NuGet-/npm-Abhängigkeiten und prüft, dass die API-DLL, die Angular-Startseite und ein Nicht-Root-Runtime-User vorhanden sind. Das ist keine Docker-/Kubernetes-Betriebsabnahme.

## FGAP-Negativtests mit echten Zielidentitäten

Die Keycloak-26.8.1-Integration prüft zusätzlich user- und group-spezifische FGAP-Rechte, positive und negative Zuweisungen, fremde Clients sowie den tatsächlichen Rollenentzug. Eine bewusst eingesetzte Zielgruppen-`manage`-Berechtigung ist weiter gefasst als reine Rollenzuweisung; sie darf nicht als unabhängig minimaler Gruppen-Mapping-Scope ausgegeben werden. Die Permora-API muss solche administrativen Operationen serverseitig weiter beschränken. Produktives AD-/LDAP, BFF-OIDC/MFA und Load/Recovery bleiben eigene Gates.

## Reproduzierbarkeit ab diesem Stand

Die tatsächlich aufgelösten NuGet- und npm-Lockfiles sind eingecheckt. In der Standard-CI laufen `dotnet restore Permora.slnx --locked-mode` und `npm ci`. Fehlende, inkonsistente oder nicht zur Manifestversion passende Locks brechen ab. Der zuvor rote Diagnoselauf bleibt als historischer Nachweis erhalten, und der neue Lockfile-Stand wurde in GitHub Actions #38946163282 bereits mit vollständigen Release-Builds überprüft.


## Was tatsächlich läuft und was nur vorbereitet ist

Die mitgelieferte Evidenz dokumentiert **32 erfolgreiche Node-Tests** der puren TypeScript-View-Model-Funktionen. Diese Tests benötigen keine Angular-Installation. Sie prüfen Labels, Fehlerbehandlung, sichere Zahlen-/Prozentdarstellung, Ablaufdatumsvalidierung und Idempotenzschlüssel. Sie beweisen weder gerenderte Angular-Templates noch .NET-Autorisierung.

Historisch lagen zunächst nur die C#-Testquellen vor. Inzwischen hat [CI-Lauf #38912134224](https://github.com/Cenn0/Permora/actions/runs/38912134224) **102/102 xUnit-Tests**, einschließlich echtem PostgreSQL-Test ohne Skips, erfolgreich ausgeführt. Darin enthalten: Policy-/Isolationsfälle, Rollen-/Gruppengrenzen, Selbsteskalation, SoD, Revision/Idempotenz, Freigaben, Remote-Retry, Reconciliation, Ablauf/Review, Audit und SDK sowie HTTP-Connector-Vertragssimulationen. Fake-Connector-Tests ersetzen kein echtes Keycloak.

PostgreSQL-Tests brauchen `PERMORA_TEST_POSTGRES`; der Datenbankname muss mit `permora_test_` beginnen. Jeder Test arbeitet in einem zufälligen eigenen Schema und entfernt dieses. Niemals produktive Credentials oder Datenbanken angeben. Ohne Variable melden Tests einen Skip, nicht einen bestandenen Integrationslauf.

Playwright besitzt Desktop-Chromium- und Mobile-Projekte. Die vorhandenen Browserfälle verwenden explizite API-Fixtures für Layout, fehlende Authentifizierung, Scope und Fehlermeldungen. Serverautorisierung muss zusätzlich direkt über echte HTTP-Requests geprüft werden. Die Playwright-Fälle wurden im GitHub-Runner mit Desktop- und Mobile-Projekt (**8/8**) ausgeführt. Sie verwenden ausdrücklich API-Fixtures; gesondert läuft ein echter ASP.NET-HTTP-Smoke-Test für ausgewählte Serverberechtigungen.

## Befehle

```bash
# Bereits in der Arbeitsumgebung ausführbare Teilmenge
bash scripts/check.sh --lightweight

# Erst auf einer Maschine mit .NET 10, kompatiblem Node und Netzwerk
dotnet restore Permora.slnx --use-lock-file
dotnet build Permora.slnx -c Release --no-restore
dotnet test tests/Permora.Tests/Permora.Tests.csproj -c Release --no-build
cd web
npm install
npm run build
npx playwright install chromium
npm run test:e2e
```

Für reale PostgreSQL-Prüfung eine separate Datenbank erzeugen und die Verbindungszeichenfolge lokal setzen; anschließend `dotnet test` wiederholen. Beispielname: `permora_test_local`. Die Connection String gehört nicht in Testberichte oder Commits.

## Noch zwingend nachzuweisen

- ASP.NET-HTTP-Integration: OIDC-Testissuer, Cookie-/CSRF-Verhalten, direkte Fremd-ID-Aufrufe, unbekannte JSON-Felder und Nebenläufigkeit.
- Echte Keycloak 26.8.1-Integration mit minimalen Rollenprofilen; vollständige FGAP-Negativmatrix aus [Keycloak](keycloak.md).
- Absturz vor/nach Remote-Write, verlorene Antwort, abgelaufene Freigabe, gleichzeitige Worker, echte Postgres-Sperren und Netzwerkpartition.
- Direkte versus geerbte Gruppenrollen; externe Mitgliedschaftsänderungen; vollständige Listensicht und Token-Audience.
- Last, Backlog-Fairness, Wiederherstellung, Datenbankschema-Upgrades, Accessibility und Security-Review.

Kein Coverage-Prozentsatz wird ausgewiesen, weil kein instrumentierter Gesamtlauf stattgefunden hat. Die Zahl von C#-Testmethoden ist keine Zahl bestandener Testfälle.


## Ergänzte Testquellen und manuelle Gates

`WorkflowTests` enthält Negativfälle zur Aktivierung ohne zentrale IAM-Attestierung bzw. mit falscher Client-UUID sowie einen positiven Simulationsfall. Ein Regressionstest fordert, dass ein fälliger automatischer Entzug einen älteren Entzug im Backoff überholen darf. `ConnectorTests` prüft eine Rollenlöschung bei unbekannter direkter Keycloak-Zuweisung. **Diese C#-Tests wurden im GitHub-Lauf erfolgreich ausgeführt; damit ist keine vollständige Keycloak-FGAP-Betriebsfreigabe verbunden.**

`scripts/bootstrap-locks.sh` erzeugt Lockfiles nur in einer netzwerkfähigen Umgebung; danach müssen die tatsächlichen Abhängigkeits-Lockfiles geprüft und eingecheckt werden. `scripts/check.sh` bricht ohne Lockfiles, .NET-SDK und isolierte `PERMORA_TEST_POSTGRES`-Datenbank ausdrücklich ab. `--lightweight` umfasst nur Struktur und isolierte Frontendlogik. Weder BFF-Login noch FGAP/Token-Audience können durch Mock-Tests abgenommen werden.

## CI-Evidenz (nachträglich, 16.10.2026)

[Ausgeführte GitHub-Prüfungen](evidence/ci-2026-10-16.md): .NET-Build, 102 xUnit-Tests (0 übersprungen, darunter echte PostgreSQL), ASP.NET-HTTP-Smoke mit zwei isolierten Development-Identitäten, Angular-Produktionsbuild, 32 Frontend-Unit-Tests, 8 Playwright und Keycloak 26.8.1 Rollen-/Token-Vertrag bestanden. Der Dependency-Lock-Gate scheitert mangels eingecheckter Lockfiles. Ein echter OIDC-BFF-Flow bleibt offen; clientbezogene FGAP-v2-Rechte sind mit zwei eingeschränkten Servicekonten getestet, die vollständige User-/Group-Zielmapping-Matrix jedoch nicht.

## P4: External LDAP membership churn and governance snapshot safety (20.10.2026)

The isolated [system acceptance](../scripts/ci-federation.py) checks **actual OpenLDAP LDAPS and Keycloak 26.8.1** in both imported and non-imported read-only modes. After assigning an application role to an LDAP group, the fixture removes the original member and adds another account, clears the Keycloak user cache, and verifies the real effective role follows the new membership. The fixture then restores the original member, confirms access returns exclusively to that account and checks that no unexpected direct user mapping was created. All identities, groups and service credentials are disposable test fixtures.

The domain regression suite separately models the worker's SoD-remediation decision across group membership arrival/loss/restoration. Automatic group revocation now requires unique, valid, fresh, eligible and membership-complete directory identities on both sides of the conflicting role pair. Stale, duplicated and otherwise unreliable groups must produce findings rather than arbitrary automatic deletions. Access-review retention uses an equally unambiguous in-realm target check; tests cover missing, duplicate, unknown-kind, foreign, stale, disabled and incomplete identities plus successful independent review.

**Limits:** A successful federation fixture proves live Keycloak group-role semantics, **not** a running Permora worker connected end-to-end to that same LDAPS fixture. It is not a Microsoft Active Directory acceptance, production IAM approval, token revocation guarantee, or proof that a restored LDAP member's prior access is organizationally authorized. An externally restored group membership can restore inherited rights without a new Permora request; operators must use authoritative IAM lifecycle controls, reconciliation and timely group-grant revocation. See [FGAP group rights](keycloak-fgap-acceptance.md) and [release gates](release-checklist.md).

## Real end-to-end LDAPS directory-drift worker test (P4)

The `Permora system acceptance` GitHub workflow builds the actual Release
`Permora.Worker` and runs
[`ci-federation.py`](../scripts/ci-federation.py) with the
[`ci_federation_worker.py`](../scripts/ci_federation_worker.py) joined fixture.
All services and accounts are ephemeral: TLS-validated OpenLDAP, Keycloak
26.8.1, a PostgreSQL 18 container, and the compiled .NET worker.

The test seeds a real Keycloak client, a direct user role and a group role
under a separation-of-duties policy. It first requires the real worker to
synchronize a **nonconflicting** directory snapshot to PostgreSQL. It then
changes LDAP group membership externally, proves the effective Keycloak
role conflict, starts the worker to reconcile the new directory snapshot,
and verifies a **single** targeted group revocation: PostgreSQL domain
state, system operation, append-only audit, surviving unrelated individual
access, removed remote group role, and no regrant after external membership
restoration. A scoped same-token negative probe rejects realm mutation.

The group-role grant is seeded by the isolated administrator as historical
fixture state. Runtime `AllowGroupRoleGrants=false` remains enforced; only
emergency group revocation is exercised. The live joined worker case uses
**imported read-only** LDAP. The non-imported path separately proves inherited
access and direct-user-write rejection but is not yet a joined-worker case.
No real AD, LDAP outage/ambiguity fault matrix, key rotation, production RPO/RTO
or p95/p99/independent security acceptance is implied. The numerical CI
revocation duration is **not** an approved operational SLO.

A passing exact-commit [system acceptance workflow](../.github/workflows/system-acceptance.yml)
is required before marking this new engineering proof as verified.

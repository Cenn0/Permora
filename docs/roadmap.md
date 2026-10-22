# Permora – Product & Engineering Roadmap (V2)

**Verifizierter P4-Stand: 22.10.2026.** Der neue P4-Schritt ergänzt
[periodische Rezertifizierung](access-reviews.md): Owner-konfigurierte Intervalle,
verbindliche Fristen, automatische Kampagnen, nachvollziehbare Entzugsaufträge
und eine vollständige Bedienoberfläche. Plan- und Anwendungsrevisionen sind
getrennt; die additive Schema-v2-Migration erhält bestehende Daten.

Alle **sieben Workflows** für [PR #62](https://github.com/Cenn0/Permora/pull/62)
bestanden. Die Run-Metadaten referenzieren den PR-Head
[`2a260e0632106499ef10e14f64144a5c2aae17ef`](https://github.com/Cenn0/Permora/commit/2a260e0632106499ef10e14f64144a5c2aae17ef);
der tatsächlich ausgecheckte PR-Merge-Commit war
[`e635db8f25b73e8460d2d5ef7c8b5c6dfb32b7af`](https://github.com/Cenn0/Permora/commit/e635db8f25b73e8460d2d5ef7c8b5c6dfb32b7af)
gegen die damalige `main`-Basis
[`763cdcddde9a17336e51c58cbe993455c0308811`](https://github.com/Cenn0/Permora/commit/763cdcddde9a17336e51c58cbe993455c0308811).
Die Ergebnisse gelten für diesen Teststand; spätere Commits benötigen eigene CI.

| Prüfebene | Ergebnis dieses PR-Laufs | Direkter Nachweis |
| --- | --- | --- |
| Backend, HTTP und Datenbank | 272/272 Tests, 0 Skips, PostgreSQL 18.6; HTTP-Scope/CSRF und isolierter Backup-/Restore-Fingerprint bestanden | [Core CI](https://github.com/Cenn0/Permora/actions/runs/39718804538) |
| Frontend | 41 Logiktests und 40 Browserfälle bestanden; Browserfälle mit API-Fixtures | [Core CI](https://github.com/Cenn0/Permora/actions/runs/39718804538) |
| Accessibility | 63 geprüfte Zustände, 0 automatisch erkannte Verstöße und 0 Overflow-Fälle | [Axe-/Reflow-Lauf](https://github.com/Cenn0/Permora/actions/runs/39718804573) |
| Worker und Last | 32 + 2 beziehungsweise 96 + 2 Entzüge mit echten Workern/Keycloak; zusätzliche Rezertifizierungsentzüge und Recovery bestanden | [Worker](https://github.com/Cenn0/Permora/actions/runs/39718804613), [Lastlauf](https://github.com/Cenn0/Permora/actions/runs/39718804521) |
| Föderation | 68 LDAPS-/Keycloak-Prüfungen sowie realer importierter LDAP → PostgreSQL → Worker → Keycloak-Entzug bestanden | [Systemtest](https://github.com/Cenn0/Permora/actions/runs/39718804516) |
| OIDC/BFF | Echter Passwort-/OTP-Flow, Assurance-Sperre, gemeinsame Sitzung und Logout über zwei BFF-Prozesse bestanden | [OIDC-Job](https://github.com/Cenn0/Permora/actions/runs/39718804538/job/119627224622) |
| Artefakte | Artefakt-/SBOM-Prüfung und Vulnerability-Gate bestanden; keine behebbaren High/Critical im getesteten Scope | [Artefaktintegrität](https://github.com/Cenn0/Permora/actions/runs/39718804559) |
| Dokumentation | Öffentliche Dokumentationsprüfungen bestanden | [Public Docs](https://github.com/Cenn0/Permora/actions/runs/39718804575) |

Die PR-Signierung wurde erwartungsgemäß übersprungen; dieser Lauf erzeugt keine
Release-Freigabe. Automatische Accessibility-Checks sind keine vollständige
WCAG-Abnahme, und ein bestandenes Vulnerability-Gate bedeutet nicht, dass sämtliche
Schwachstellen ausgeschlossen sind. Details stehen in der
[Teststrategie](testing.md#periodische-rezertifizierung-22102026).
Microsoft-AD-spezifische Föderation, weitere Directory-Ausfallkombinationen,
repräsentative Mehrhost-/Recovery-Prüfungen und unabhängige Reviews bleiben
separate offene Arbeitspakete. Die bekannten Keycloak-Gruppenscope-Grenzen
werden in [Issue #46](https://github.com/Cenn0/Permora/issues/46) geführt.

**Vorheriger verifizierter Stand:** [PR #60](https://github.com/Cenn0/Permora/pull/60)
belegt den echten LDAPS → Keycloak → PostgreSQL → Worker-Entzug nach externer
Gruppenänderung; [PR #61](https://github.com/Cenn0/Permora/pull/61) synchronisierte
die zugehörige Dokumentation. Weitere offene Directory-Szenarien stehen in
[Issue #58](https://github.com/Cenn0/Permora/issues/58). Historische Checkpoints
sind in der Git-Historie und im [Audit vom 18.10.2026](evidence/roadmap-audit-2026-10-18.md)
nachvollziehbar. Die Bestandsmatrix in Abschnitt 2 ist ausdrücklich historisch;
der aktuelle P4-Umfang ist in Abschnitt 3 beschrieben.

Diese Roadmap unterscheidet implementierte Funktionen, tatsächlich ausgeführte
Tests und noch nicht geprüfte Umgebungen. Plantermine sind keine Lieferzusagen.

## 1. Produktziel und Nichtziele

Permora ermöglicht **delegierte, anwendungsspezifische Rechteverwaltung in Keycloak**. Teams verwalten ihre Anwendungen, `perm.*`-Einzelrechte, `role.*`-Rollen, Anträge, Freigaben und Rezertifizierungen selbst. Die zentrale IAM-Administration behält Realm-Konfiguration, die tatsächliche Vergabe privilegierter Keycloak-Berechtigungen und die Entscheidung über das erstmalige Onboarding.

**Für 1.0:**
- Mandantensichere Verwaltung mehrerer Anwendungen und Teams in einem klar abgegrenzten Keycloak-Trust-Modell.
- Echte Keycloak-Zuweisungen mit nachweisbarem Soll-/Ist-Abgleich, sicherem Entzug und verständlichen Zwischenzuständen.
- Ein vollständig geprüfter OIDC-BFF-Login mit MFA-/Step-up-Durchsetzung für privilegierte Aktionen.
- Vier-Augen-Freigaben, periodische Rezertifizierung, SoD-Prüfung und nachvollziehbares Audit.
- Reproduzierbare Installation, Upgrade, Backup/Restore, Observability, Sicherheits- und Release-Nachweise.

**Nicht Teil des 1.0-Versprechens:** Ersatz der Keycloak-Admin-Konsole; automatisches Management beliebiger Realm-Ressourcen; vollständige HR-IGA-Suite; universelles ABAC/UMA; garantierter sofortiger Widerruf bereits ausgegebener JWTs; beliebige Cross-Issuer-Personenidentifikation; ein bereits zertifizierter oder offiziell von Keycloak unterstützter Permora-Betrieb.

## 2. Historischer Ausgangspunkt (17.10.2026)

| Bereich | Zustand am Stichtag | Verifizierte Grenzen |
| --- | --- | --- |
| Backend | .NET 10, getrennte API/Worker/Provisioner-Prozesse, PostgreSQL, SDK und REST-API implementiert | Kein abgeschlossener HA-/Recovery-/Lastnachweis |
| Frontend | Angular 22, Dashboard, Application-/Role-/Access-/Approval-/Audit-Seiten | Playwright mit API-Fixtures ist kein echtes Browser-BFF-SSO |
| Geschäftslogik | Team-Scope, Eigentümerschaft, Revision/Idempotenz, Vier-Augen-Freigabe, Rezertifizierung, SoD, Entzugslogik vorhanden | Nicht alle realen Stör- und Directory-Änderungsszenarien abgenommen |
| Keycloak | Keycloak 26.8.1 in isolierten FGAP-v2-/Rollen-/Token-Tests, zwei begrenzt berechtigte Servicekonten | Vollständige Enterprise-Rechtematrix, LDAP/AD und echtes Portal-SSO fehlen |
| Build/Tests | 7/7 Pflichtjobs des CI-Workflows auf `main` erfolgreich, 102 xUnit, 32 Frontend-Logiktests, 8 Playwright, Docker-Image-Build, echte Lockfiles | Die Testzahlen gelten nur für den dokumentierten Scope; keine produktive Abnahme |
| Supply Chain | `dotnet restore --locked-mode`, `npm ci`, lesende CI ohne produktive Secrets, keine Veröffentlichung | SBOM, transitive Lizenzprüfung, Release-Provenance, vollständiges SCA fehlen |
| Dokumentation | README EN/DE, Architektur, Security, Keycloak, Operations, Testing und CI-Guides vorhanden | Einige historische Aussagen in `docs/ci.md`, `docs/testing.md` und Issue #3 müssen konsolidiert werden |

**Live-Nachweis:** [CI `main` vom 16.10.2026, 7/7 Pflichtjobs erfolgreich](https://github.com/Cenn0/Permora/actions/runs/38964688463); [dokumentierte Testabgrenzung](evidence/ci-2026-10-16-final.md). Ein grüner Workflow beweist weder sichere Mandantentrennung unter allen Bedingungen noch eine Produktionsfreigabe.

**Sicherheitsrelevantes Versionsrisiko:** Keycloak 26.8.1 erschien am 13.10.2026 mit mehreren Korrekturen für FGAP, delegierte Administration, MFA und realmübergreifende Zugriffe, unter anderem CVE-2026-94000/94001. Vor dem Pilotbetrieb muss deshalb die konkret ausgelieferte Keycloak-Version sowie die Rechteeskalationsmatrix geprüft werden. Referenz: [Keycloak 26.8.1 Release Notes](https://www.keycloak.org/2026/10/keycloak-2681-released). Der Bestand ist auf 26.8.1 getestet; ein beliebiger Versionswechsel ist **nicht** automatisch kompatibel oder sicher.

### Technische Aussagen getrennt bewerten

| Ebene | Zulässige Aussage |
| --- | --- |
| Funktioniert grundsätzlich | Einige API-, Rollen-, Token-, FGAP- und CI-Pfade funktionieren in isolierten Tests |
| Offiziell unterstützt | Keycloak dokumentiert Admin REST, FGAP v2 und OIDC; **Permora selbst ist kein offiziell unterstützter Keycloak-Bestandteil** |
| Best Practice | App-spezifische Delegation, getrennte Servicekonten und BFF sind Architekturentscheidungen mit klaren Grenzen, kein Ersatz für Threat Modeling |
| Sicherheitsseitig empfehlenswert | Produktive Freigabe erst nach negativen Mehrteam-Tests, echtem MFA-/SSO-Gate, Least-Privilege-Audit, Recovery- und Security-Abnahme |

## 3. Priorisierte Arbeitspakete

**Status:** `BELEGT` = im beschriebenen CI-Scope erfolgreich; `TEILWEISE` = Quellcode bzw. Teiltest vorhanden; `OFFEN` = nicht implementiert oder nicht ausreichend geprüft. Keine Prozentwerte ohne methodisch sauberen Funktionsabgleich.

### P0 — Bestandskonsolidierung und Security-Baseline
**Priorität: kritisch · Voraussetzung für alle folgenden Release-Gates · Schwerpunkt Q4/2026**

- [ ] Issue [#3](https://github.com/Cenn0/Permora/issues/3) und alte CI-/Testing-Texte auf den aktuellen `main`-Nachweis aktualisieren: bereits gelöste Lockfile-/Buildblocker nicht weiter als offen führen.
- [ ] Versionsmatrix für .NET, Angular/Node, PostgreSQL und Keycloak pflegen; Keycloak-Security-Fixes und Breaking Changes vor Upgrades prüfen.
- [ ] Threat Model mit Datenflüssen und Trust Boundaries (Browser, BFF, PostgreSQL, Worker, Keycloak-Rollenprofil, Provisioner, Geschäftsanwendung) erstellen.
- [ ] Vollständige Rechte- und Negativtestmatrix mit zwei Teams, zwei Apps, zwei Servicekonten, Zielbenutzern/-gruppen und fremden Ressourcen versionieren.
- [ ] Release-Gates und Testbelege maschinenlesbar verlinken; nach Änderung an Auth/Policy/Provisioning müssen passende Sicherheitsregressionen zwingend laufen.
- [ ] Repository- und CI-Konfiguration auf öffentliches Open Source ausrichten: keine sensitiven Metadaten/Secrets, getrennte minimal berechtigte Tokens; keine produktiven Berechtigungen im PR-Runner.

**Abnahme:** Jede als `BELEGT` markierte Aussage hat einen reproduzierbaren Test und eine prüfbare Quelle; keine widersprüchlichen Release-Angaben.

### P1 — OIDC-BFF, MFA und Sitzungsgrenzen
**Priorität: kritisch · P0 → P1 · Ziel: technisch abgenommene Portal-Identität**

**Ist:** OIDC-Code/PKCE, Cookie und CSRF sind im ASP.NET-Code; echte Browser-SSO- und Step-up-Tests fehlen (`TEILWEISE`).

- [ ] Testumgebung mit **echtem** Keycloak-Browser-Login über das ASP.NET-BFF aufbauen: Authorization Code, PKCE, Callback, Issuer, Audience, Nonce/State und Session-Cookie.
- [ ] Negative Tests: falscher Issuer/Audience, manipulierte/fehlende Cookies, CSRF, Session-Fixation, abgelaufene Session, fremde Subject-ID, Logout und Re-Login.
- [ ] Für risikoreiche Aktionen verpflichtendes **Step-up**: Keycloak `acr`/`amr`, Authentifizierungszeitpunkt und angemessene Maximaldauer serverseitig prüfen; bei fehlendem Niveau neu authentifizieren oder **fail closed**.
- [ ] Daten-/Secret-Schutz: Data-Protection-Key-Rotation, persistierte Schlüssel bei mehreren API-Instanzen, Secret-Zugriff, Keycloak-Ausfall, Forwarded Headers/TLS/CSP unter echtem Reverse-Proxy testen.
- [ ] Nachweis, dass weder Admin-Token noch Refresh-Token in Browser-Storage oder HTTP-Antworten gelangen.
- [ ] Dev-Authentifizierung unter produktiver Konfiguration nachweislich unerreichbar halten.

**Abnahme:** Reale Browser-E2E-Prüfung einschließlich privilegierter Aktion mit erforderlicher MFA; keine Rollenzuweisung über manipulierte BFF-Sitzung.

### P2 — Sichere Keycloak-Delegation, Onboarding und Rollen
**Priorität: kritisch · P0/P1 → P2 · Ziel: überprüfte End-to-End-Delegation**

**Ist:** Clientbindung, zentrale IAM-Attestierung, Rolle/Permission/Grant/Revocation und FGAP-Teiltests vorhanden (`TEILWEISE`).

- [ ] **Vollständige FGAP-v2-Matrix**: eigene und fremde Clients, Realm-Rollen, Benutzer, Gruppen, verschachtelte Gruppen, nicht importierte LDAP-Benutzer, Client-Scope-Mappings und alle betroffenen Admin-REST-Routen.
- [ ] Jede Operation mit getrennten Rollen-/Assignment-/Directory-Credentials ausführen: positive eigene Zugriffe und explizite **403-Negativtests** auf fremde IDs, Credentials, Gruppenmitgliedschaft, Impersonation und Realm-Konfiguration.
- [ ] Insbesondere breitere Keycloak-Scopes wie Gruppen-`manage` und `manage-users` prüfen; keine vermeintlich minimale Delegation behaupten, die eine Eskalationsmöglichkeit enthält. Sicherheitsregressionen zu den relevanten 26.8.1-Fixes aufnehmen.
- [ ] Neue Anwendung: Owner-Registrierung → deaktivierter Rechte-Client → zentrale IAM-Prüfung → geprüfte Binding-/Audience-/Scope-Konfiguration → Aktivierung → sichtbarer Status.
- [ ] Ein vollständiger fachlicher Ablauf: Permission/Composite Role anlegen → Antrag → Fremdfreigabe → tatsächliches User-/Group-Mapping → wirksames JWT → Ablauf/Entzug → Rückprüfung.
- [ ] Aus vollständigen Keycloak-Listen nachvollziehbar verifizieren, dass Rollenlöschung keine unbekannten oder unsichtbaren Mapping-Beziehungen hinterlässt.
- [ ] Automatische FGAP-Policy-Erstellung **erst nach** versionierter, deterministischer Rechte-Template- und Negative-Test-Abnahme entwickeln. Bis dahin bleibt zentrale manuelle IAM-Konfiguration eine ausdrücklich dokumentierte Betriebsgrenze.
- [ ] Rechteprüfung der **Geschäftsanwendung** separat testen: JWT-Signatur, Issuer, Audience, Ablauf und fachliche Enforcement-Policy. Rollen im Token ersetzen keine Autorisierung.

**Abnahme:** Team A kann unter keinen getesteten Umständen Rechte in Team B, an Realm-Rollen oder an fremden Zielidentitäten verändern; eine erfolgreiche Portalantwort wird erst nach Keycloak-Readback als wirksam dargestellt. **Pilotfähigkeit erfordert diesen Gate**, nicht lediglich einen grünen Connector-Mock.

### P3 — Konsistenz, Worker und verlässlicher Entzug
**Priorität: hoch · P2 → P3 · Ziel: kontrolliertes Verhalten bei Teilausfällen**

**Ist:** persistente Operationen, Idempotenz, Revisionen, Retry, Reconciliation, Priorisierung fälliger Entzüge vorhanden (`TEILWEISE`).

- [ ] Nachweise bei Crash/Timeout **vor Remote-Write**, **nach Remote-Write vor Antwort**, **nach Antwort vor DB-Commit** und beim Worker-Neustart.
- [ ] Parallelbetrieb mit mehreren API-/Worker-Instanzen: Datenbank-Locks, Fairness zwischen Apps, Queue-Starvation, Deadlocks, Ausfälle und Redispatch prüfen.
- [ ] Unklare Remote-Ergebnisse blockieren gefährliche Folge-Vergaben bis zur nachgewiesenen Soll-/Ist-Klärung; keine Annahme verteilter ACID-Transaktionen.
- [ ] Backoff, Rate Limits (`429`), Sperren (`403`), Eventual Consistency, defekte JSON-Antworten sowie fehlende/inkonsistente Keycloak-Daten abdecken.
- [ ] Überfällige Entzüge höher priorisieren als neue Vergaben; Metriken für Queue-Alter, Entzugszeit, Drift, Reconcile-Konflikte und Retry-Fälle hinzufügen.
- [ ] Verhalten bereits ausgegebener JWTs ausdrücklich dokumentieren und mit repräsentativen Anwendungen prüfen: Mapping-Entzug bedeutet **nicht** sofortigen Widerruf bestehender Tokens.
- [ ] Grenzwerte der JSONB-Aggregate (Rollen, Berechtigungen, Zuweisungen) unter realistischer Datenmenge vermessen; erst nach Messung über zusätzliche Tabellen/Normalisierung entscheiden.

**Abnahme:** Last-/Failure-Injection-Tests belegen keine Doppelvergabe, keine stille Erfolgsmeldung und einen klaren Incident-Prozess bei unvollständigem Entzug.

### P4 — Governance, Directory und kontinuierliche SoD
**Priorität: hoch · P2/P3 → P4 · Ziel: belastbarer Berechtigungslebenszyklus**

**Ist am 22.10.2026:** Vier-Augen-Policy, SoD, manuelle und periodische Rezertifizierung, Frist- und Entzugslogik sowie Planverwaltung im Portal sind implementiert. Die weitergehenden Directory- und Benachrichtigungsfunktionen bleiben einzeln ausgewiesen.

- [ ] Delegationsmandate, Entscheiderwechsel, Suspendierung, Owner-Entzug und konkurrierende Freigaben mit realen Identitäten testen.
- [x] Periodische Prüfpläne, unveränderte Deadlines nach Worker-Ausfällen, separate Revisionen, Owner-/Approver-/Auditor-Oberfläche und idempotente Entzüge implementiert; [Vertrag und Upgrade](access-reviews.md).
- [x] Periodischer Start bis tatsächlichem Keycloak-Entzug im isolierten erweiterten Worker-Systemtest erfolgreich nachgewiesen: [Worker-Lauf](https://github.com/Cenn0/Permora/actions/runs/39718804613) und [Lastlauf](https://github.com/Cenn0/Permora/actions/runs/39718804521), jeweils mit zwei zusätzlichen Rezertifizierungsentzügen; [Testumfang und Commit-Zuordnung](testing.md#periodische-rezertifizierung-22102026).
- [ ] Optionale Erinnerungs- und Eskalationsbenachrichtigungen auf Basis des nachgewiesenen Review-/Auftragszustands ergänzen.
- [ ] Externe Gruppenänderungen, direkte/effektive und verschachtelte Rollen sowie neu entstehende SoD-Konflikte erkennen; Drift markieren, Vergaben sperren und kontrollierte Remediation auslösen.
- [ ] AD-/LDAP-Federation **zunächst nur lesend** in repräsentativen Keycloak-Installationen prüfen, einschließlich unvollständiger Seiten, nicht importierter Konten, deaktivierter Benutzer und Gruppen mit Mitgliederlimits.
- [ ] Idempotente Reconciliation gegen externe Änderungen definieren: Fremdverwaltung nicht unautorisiert überschreiben; klarer Entscheid zwischen „beobachten“, „blockieren“ und „reparieren“.
- [ ] Audit-Events für Antrag, Genehmigung, Ablehnung, Änderung, Drift und Löschung mit Korrelation/Operator nachprüfbar machen; Datenschutz, Aufbewahrung und Export getrennt behandeln.
- [ ] SoD-/Rezertifizierungsberichte für Owner und IAM mit identischem Backend-Scope implementieren.

**Abnahme:** Eine externe Gruppenänderung kann nicht unbemerkt als dauerhaft konforme Berechtigung fortgeschrieben werden; manuelle Klärfälle werden sichtbar und prüfbar.

### P5 — Bedienbarkeit und Produktvollständigkeit
**Priorität: mittel bis hoch · parallel nach P1/P2 · Ziel: tatsächlich verwendbarer Self-Service**

**Ist:** Designvariante 3, Dashboard, Rollen- und Berechtigungsseiten vorhanden (`TEILWEISE`).

- [ ] Echte Browser-E2E-Prüfung der Rollen-/Antrags-/Audit-Seiten ohne Fixture-APIs; klare Anzeigen für `Queued`, `Applied`, `Conflict`, `Failed` und „noch nicht in Keycloak wirksam“.
- [ ] Dashboard-Karten, Aktivitäten und rechte Aufgabenleiste konsistent zum gewählten hellen Design ausbauen; Dunkelmodus optional, nicht vor Security-Gates.
- [ ] Such-, Filter-, Sortier- und Paginierungspfade für viele Apps/Rollen/Zuweisungen: serverseitig autorisiert, ohne fremde Mandantendaten.
- [ ] Responsive Oberflächen (Mobil/Desktop), Tastaturbedienung, Fokus-Reihenfolge, Screenreader-/ARIA-Prüfungen und nachvollziehbare Formularvalidierung.
- [ ] Nutzerfreundliches Onboarding mit Status der erforderlichen zentralen IAM-Freigabe, überprüfbarer Gegenüberstellung von Soll und Ist sowie Fehlerbehebungsvorschlägen ohne sensitive Daten.
- [ ] Vollständige deutsche und englische Lokalisierung mittels Angular-i18n-Strategie; README bleibt primär Englisch.
- [ ] Benachrichtigungen für fällige Genehmigungen, Entzüge, Rezertifizierungen und Störungen optional nach verlässlichem Zustandsmodell ergänzen.

**Abnahme:** Fachanwender können den gesamten getesteten Ablauf ohne Keycloak-Admin-Konsole (außer zentralem Erst-Onboarding) durchführen; kein UI-Erfolg ohne bestätigte Remote-Wirkung.

### P6 — Betrieb, Supply Chain und Deployment
**Priorität: kritisch vor öffentlicher 1.0 · P0–P4 → P6**

- [ ] Referenzdeployment mit getrennter API, Worker, Provisioner, PostgreSQL und intern erreichbarer Keycloak-Admin-API; HTTPS-Proxy, geschützte Secrets und geringste Prozessrechte.
- [ ] Upgrade-/Migration-/Rollback-Verfahren inkl. versionierten DB-Schemas und vorab getesteter Datenbank-Backups.
- [ ] Vollständiger Restore-Drill: PostgreSQL **und** ASP.NET-Data-Protection-Schlüssel; nach Restore zunächst schreibenden Worker kontrollieren und Soll/Ist-Abgleich durchführen, statt alte Grants blind erneut anzuwenden.
- [ ] OpenTelemetry/Prometheus-Metriken und strukturierte Logs, Alarmierung für veraltete Directory-Snapshots, Queue-Stau, Entzugsverzug, FGAP-403, Auth-Fehler und Audit-Drift.
- [ ] SLOs/RPO/RTO **messen und definieren**, nicht ohne Messwerte versprechen; Last, Langläufer, Parallelbetrieb, DB-/Keycloak-Ausfall und Rolling Upgrade abnehmen.
- [ ] SBOM für Backend, Frontend und Container, transitiver Lizenz-/Vulnerability-Scan, reproduzierbares Release, Security Advisories, Release-Signierung/Provenance prüfen.
- [ ] Standard-GitHub-hosted Runner im öffentlichen Repo und reine Read-only-PR-CI beibehalten; ohne kostenpflichtige Larger Runner, Produktiv-Secrets, unnötige Artefakt-/Cachespeicherung oder PR-Deployments.
- [ ] Datenretention, DSGVO-relevante Exporte/Löschkonzepte und **extern manipulationsgeschütztes** Audit (z.B. SIEM/WORM) festlegen; Hashkette allein ist kein WORM.
- [ ] Sicherheitsreview, Dependency-Audit und unabhängigen Penetrationstest mit klar dokumentierten Findings durchführen.

**Abnahme:** Frischer Aufbau, Upgrade, Backup/Restore und Störungshandbuch sind in einer isolierten Pilotumgebung reproduzierbar und dokumentiert.

### P7 — Release Engineering und Qualitätsschranken
**Priorität: hoch · quer zu P0–P6**

- [ ] Pflichtstatus der PR-CI und Branchschutz kontrollieren; keine Ausnahmen für unbekannten Code.
- [ ] 3 Prüfschichten pflegen: schnell (Unit/Policy/Build), real integriert (PostgreSQL/Keycloak/OIDC/Browser), Release (Last/Recovery/Security).
- [ ] Supportmatrix nach konkret getesteten Keycloak-Versionen und Deploymentvarianten ausweisen; jede neue Version als eigenen Vertragstest behandeln.
- [ ] Dokumentation: Quickstart ohne produktive Secrets, Konfiguration, Upgrade, Migration, Betrieb, Incident, Sicherheitsmodell, Beitrag und Threat Model.
- [ ] Release-Checklist automatisieren, soweit technisch beweisbar; menschliche Freigaben für Datenschutz, Security und Betriebsrisiken separat dokumentieren.
- [ ] Öffentliche Badges nur für tatsächlich gelaufene Gates; getrennte Kennzeichnung `CI erfolgreich` versus `für Produktion abgenommen`.

**Abnahme:** Jeder öffentliche Release hat einen unveränderlichen Commit/Tag, verifizierbare Build-/Testnachweise, Changelog, klaren Supportumfang und eine dokumentierte Risikofreigabe.

### Zuordnung zur bisherigen M0–M8-Matrix

Die historischen M-IDs bleiben in den früheren Git-Versionen nachvollziehbar: M0/M7 (Fundament, Tests, Lockfiles) → P0/P7; M1/M2 (Teams, Rechte) → P2/P5; M3 (BFF, Provisioning) → P1/P2; M4 (Aufträge) → P3; M5 (Governance) → P4; M6 (UI) → P5; M8 (Betrieb) → P6. Der frühere Punkt M7.6 (SBOM, transitive Lizenzen/Sicherheitsprüfung) wird in P6/P7 weitergeführt.

## 4. Release-Ziele und harte Gates

| Release / Planungskorridor | Umfang | Harte Freigabe |
| --- | --- | --- |
| **0.1 – CI-basierter Entwicklungsstand** (Bestand, kein offizieller Produktionsrelease) | Plattformgerüst, Team-/App-/Rollenfluss, Angular-Dashboard, Reconciliation, Lockfiles und technische Tests | Aktuelle CI nachgewiesen; Produktionsabnahme ausdrücklich ausstehend |
| **0.2 – Security Alpha** (Q4/2026, geplant) | P0, P1 und FGAP-Kern von P2 | Echter Browser-OIDC-/MFA-Test, Zwei-Team-Negativmatrix, keine Privilegieneskalation |
| **0.3 – Delegated Pilot** (Q1/2027, geplant) | P2-End-to-End plus P3-Crash-/Entzugstests | Rollen-/Antrags-/Grant-/Revoke-Flow live in Keycloak, kontrollierter Teilausfall; nur isolierter Pilot |
| **0.5 – Governance Beta** (Q1–Q2/2027, geplant) | P4-Rezertifizierung, Directory-Drift, wichtige P5-UX | Externe Gruppenänderung und SoD-Remediation in realer Testföderation nachgewiesen |
| **0.9 – Release Candidate** (Q2/2027, geplant) | P6-Härtung, P7-Releasepaket | Restore-, Last-, Upgrade-, SBOM-/Lizenz-/Sicherheitsnachweise vorhanden; kritische Findings behoben |
| **1.0 – Supported Community Scope** (nach bestandener Abnahme) | Delegierte Verwaltung in ausdrücklich getesteter Keycloak-/Deploymentmatrix | Security-, Betriebs-, Recovery- und Dokumentations-Gates bestanden; keinerlei impliziter Hersteller-Support |

**Die Zeitfenster sind Planungsannahmen, keine Lieferzusage.** Ein nicht bestandenes Gate verschiebt den Release. Die bloße Anzahl implementierter Funktionen ist kein Releasekriterium.

## 5. Verbindliche End-to-End-Testmatrix

| ID | Szenario | Erwarteter Nachweis |
| --- | --- | --- |
| A01 | Team A / Team B, Erraten fremder UUIDs, Direktaufruf aller relevanten API-Routen | Kein Fremdlesen/-schreiben, keine Keycloak-Nebenwirkung |
| A02 | Servicekonto für App A, Client-Rollen und zulässige Zielkonten | Eigene Operation erfolgreich; fremde Clients, Realm-Rechte, Konto- und Gruppenverwaltung abgewiesen |
| A03 | Anmeldung mit echtem Keycloak, Login/Logout/Callback, MFA und privilegierter Step-up | Abweisung ohne passendes Authentifizierungsniveau, keine Tokenexposition im Browser |
| A04 | Permission → Composite Role → Antrag → Vier-Augen-Freigabe → Keycloak-Mapping → JWT | Fachliche Enforcement-Entscheidung korrekt; Portal unterscheidet angenommen/ausgeführt |
| A05 | Ablauf/Entzug/Rezertifizierung und bereits existierende Access Tokens | Tatsächliches Mapping entfernt; Laufzeitrisiko bereits ausgegebener JWTs erfasst |
| A06 | Disconnect/Crash vor und nach Keycloak-Write, Retry nach Neustart | Idempotent, keine Doppelvergabe, keine falsche Erfolgsbestätigung |
| A07 | LDAP-/AD-Gruppen- und Benutzeränderung, unvollständige Sicht, neue SoD-Konflikte | Schutzentscheidungen fail-closed und aktive Drift-/Remediation-Meldung |
| A08 | Parallel-Worker und große Backlogs | Keine Queue-Starvation kritischer Entzüge; gemessene Grenzwerte |
| A09 | Restore älterer PostgreSQL-Daten und Data-Protection-Keys | Keine unkontrollierte Wiedervergabe historischer Rechte |
| A10 | Versionsupgrade Keycloak und negative FGAP-Regressionsfälle | Kompatibilitätsmatrix aktualisiert, keine Rechteausweitung |
| A11 | Unabhängiger Security-Test inklusive OIDC-/CSRF-/IDOR-/Secrets-/Privilege-Escalation | Findings bewertet, kritische/hohe Findings vor 1.0 behoben |

Diese Szenarien erhalten für jeden Release getrennte Links zu **Testcode**, **Ausführung**, **Ergebnis** und gegebenenfalls **menschlicher Abnahme**. Ein bloßer Testdateiname ist kein Nachweis.

## 6. Nachgelagertes Backlog nach 1.0

**1.1 – Self-Service-Ausbau (nach Tragfähigkeitsprüfung):** sichere automatisierte FGAP-Policy-Provisionierung und kontrolliertes Client-Onboarding; zentral definierte Templates, Simulation („dry run“), Freigabe, Rollback und Negativtests. Die zentrale IAM-Hoheit bleibt erhalten.

**1.2 – Multi-Realm und Integrationen:** mehrere Keycloak-Realms, per Realm getrennte Credentials und Konfiguration, robustes Identity-Mapping. Cross-Issuer-Korrelation nur mit autoritativer Personen-ID und geprüfter Lifecycle-Semantik.

**1.3 – Erweiterte IGA:** HR-/SCIM-/LDAP-Events und gegebenenfalls kontrollierter Writeback mit dedizierten Serviceidentitäten, Rezertifizierungskampagnen, Aufgaben-/Reporting-Exports.

**2.0 – Fachliche Policy Engine:** Keycloak Authorization Services/ABAC/UMA als **eigenständiges** Modell (Ressourcen, Scopes, Policies, Enforcement, Migration); nicht die bisherigen `perm.*`-Client-Rollen als fertiges ABAC ausgeben.

**Später, nach echten Anforderungen:** Helm/Kubernetes-Operator, Multi-Cluster-Betrieb, zusätzliche IAM-Provider, Plugin-SDK und globale Reporting-Plattform. Microservice-Aufspaltung nur bei empirisch belegten Skalierungs-/Teamgrenzen; bis dahin bleibt der modulare Monolith mit getrennten Sicherheitsprozessen bevorzugt.

## 7. Architekturentscheidungen, die bewusst bestehen bleiben

1. **Modularer Monolith** mit getrennten API-, Worker- und Provisioner-Prozessen. Keine zusätzliche Broker-/Microservice-Landschaft ohne Bedarf.
2. **PostgreSQL als fachlicher Sollzustand**, Keycloak als tatsächlicher Rechtezustand; Remote-Aufruf nie als lokale ACID-Transaktion darstellen.
3. **Application-scoped Client-Rollen** und bewusst flache Composites als v1-Rechtemodell; OIDC-Scopes und Authorization Services getrennt halten.
4. **Zentrale IAM-Aktivierung** plus technische Nachprüfung statt selbst vergebener globaler Administratorrechte.
5. **Fail-closed bei unvollständiger Verzeichnis- oder Keycloak-Sicht**, aber sicherer kontrollierter Rechteentzug und sichtbarer Incident-Pfad.
6. **Versionierter Vertragstest** statt Annahme, dass eine neuere Keycloak-Version automatisch die gleiche FGAP-Semantik hat.

## 8. Nächste priorisierte Umsetzungsschritte (aktualisiert 22.10.2026)

Die ursprünglichen PR-Vorschläge (OIDC-BFF, Keycloak-FGAP, Worker-Fehler,
Directory-SoD) wurden inzwischen **teilweise bereits in CI und Code
umgesetzt**. Aktueller Stand und offene Abnahmebedingungen stehen in
[Release-Gates](release-checklist.md) und [Issue #3](https://github.com/Cenn0/Permora/issues/3).
Eine erneute bloße Implementierung derselben Testnamen wäre kein Fortschritt.

1. **P4: Governance-Ausbau nach periodischer Rezertifizierung** —
   Erinnerungs-/Eskalationskanäle und begrenzte Reports ergänzen. Der bestehende
   Rechte- und Deadline-Vertrag bleibt dabei serverseitig maßgeblich. Die
   bereits implementierten Directory-Fail-Closed- und Remediation-Regeln
   werden mit weiteren realen Föderationsfehlern überprüft.
2. **P2: FGAP-Gruppen-Privilegien** — [Issue #46](https://github.com/Cenn0/Permora/issues/46):
   direkte Same-Token-Admin-REST-Negativmatrix, anschließende
   [Betriebsabnahme](keycloak-fgap-acceptance.md) mit nachweislich
   eingeschränktem Egress, getestetem Secret-Wechsel und externem Audit.
   **Automatisierte Tests ersetzen keine Freigabe des tatsächlichen
   Keycloak-Service-Token-Umfangs.** Neue Gruppengrants bleiben
   standardmäßig gesperrt.
3. **P4: Vollständige externe Directory-Drift** — in echten Keycloak-/LDAP-
   Integrationstests Deaktivierung, verschachtelte Mitgliedschaften und
   kollidierende Gruppenrollen verändern; verifizierte Soll-/Ist- und
   gesondert freigegebene Entzugsmaßnahmen nachweisen.
4. **P6: Repräsentativer Betriebsnachweis** — TLS-Reverse-Proxy, isolierte
   Service-Credentials, automatische Secret-Rotation sowie mindestens zwei
   API-/Worker-Hosts und echte PostgreSQL-/Keycloak-Failover-Drills.
   Nicht als erreicht markieren, solange reale Infrastruktur und Messwerte fehlen.
5. **P6/P7: Koordinierter Recovery- und Release-Gate-Lauf** — Keycloak,
   PostgreSQL, BFF-Data-Protection-Keys und Secrets gemeinsam restaurieren;
   RPO/RTO/Entzugslatenzen messen. SBOM/Vulnerability-/Lizenzbefunde und
   unabhängige WCAG-/Datenschutz-/Security-Abnahme separat erfassen.

Keine Produktionsberechtigung allein aus CI; Pull Requests benötigen
Quellcode, Tests, genaue Versionsbindung und nachvollziehbare offene Risiken.

## 9. Definition of Done

Ein Punkt ist erst `ABGESCHLOSSEN`, wenn:
- die Fachanforderung implementiert und mit eindeutigem Besitzer/Scope dokumentiert ist;
- positive und relevante negative Unit-/API-/Integrationsfälle nachweislich bestanden sind;
- bei Rechteänderungen der tatsächliche Keycloak-Istzustand **und** seine Verifikation geprüft sind;
- öffentliche Dokumentation, Threat Model/ADR und Migration bei Bedarf aktualisiert sind;
- CI im vorgesehenen Scope grün ist; bei Betriebs-/Security-Gates die zusätzliche **separate** Abnahme dokumentiert ist.

**Nicht ausreichend:** nur Quellcode, nur grüne Mock-Tests, nur statische Prüfung, nur vorhandenes Dockerfile oder ein erfolgreich angenommenes HTTP-`202`.

**Aktionspunkt:** Diese Roadmap nach Merge mit dem Release-Issue #3 und den verbliebenen historischen Aussagen in `docs/ci.md` / `docs/testing.md` synchronisieren. Die einzelnen Arbeitspakete sollen als nachvollziehbare GitHub-Issues mit Abhängigkeiten und Test-Checklisten gepflegt werden.

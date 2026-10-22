# Periodische Rezertifizierung

Owner können je Anwendung einen Prüfplan anlegen, ändern und pausieren. Die
Seite **Rezertifizierung** zeigt den nächsten geplanten Start, Bearbeitungsfrist,
offene Prüfpunkte und die Entscheidungen. Owner und Approver entscheiden;
Auditor sowie andere sichtberechtigte Teamrollen können den Stand lesen.
Zentrale Administratoren haben wie bei anderen Governance-Aktionen das
Owner-Mandat. Eine Prüfung kann weiterhin manuell gestartet werden.

## Plan und Fristen

| Feld | Vertrag |
| --- | --- |
| `name` | 3–120 Zeichen; Name der erzeugten Kampagnen |
| `intervalDays` | 1–365 Tage zwischen geplanten Starts |
| `reviewWindowDays` | 1–90 Tage; höchstens so lang wie das Intervall |
| `nextStartAt` | Bei Aktivierung zukünftig, höchstens 365 Tage voraus |
| `enabled` | `false` pausiert zukünftige Starts; bestehende Prüfungen bleiben bestehen |
| `revision` | Eigene serverseitige Version; ändert sich auch bei einem automatischen Start |

Das Intervall besteht aus festen **24-Stunden-Tagen in UTC**. Die Oberfläche
stellt Zeitpunkte in der lokalen Zeitzone dar; beim Wechsel zwischen Sommer-
und Winterzeit kann sich deshalb die lokale Uhrzeit verschieben. Es handelt
sich nicht um einen Kalenderplan wie „jeder erste Montag im Monat“.

Jeder automatische Start erstellt einen eigenen Review mit `scheduledFor` und
`dueAt = scheduledFor + reviewWindowDays`. Er erfasst die **heute noch
vorhandenen** Zuweisungen, deren `grantedAt` spätestens beim geplanten Start
lag. Nach diesem Start neu vergebene Rechte gehören in einen späteren Zyklus.
Leere Kampagnen werden unmittelbar abgeschlossen. Eine manuelle Kampagne hat
kein `scheduledFor` und enthält die beim tatsächlichen Start vorhandenen
Zuweisungen.

**Planänderungen verschieben keine bereits gespeicherten Fristen.** Das gilt
auch beim Pausieren. Mehrere manuelle oder geplante Kampagnen sind unabhängig:
Die Bestätigung in einer Kampagne hebt weder die Deadline einer anderen
Kampagne noch einen bereits ausgelösten Entzug auf.

Ist ein aktiver Plan bereits fällig, muss der Worker die Kampagne zunächst
anlegen, bevor der Plan geändert oder pausiert werden kann. Die API antwortet
bis dahin mit `409 review_schedule_due`. Eine bereits fällige Prüfpflicht
kann dadurch nicht während einer Worker-Unterbrechung verschwinden.

## Verhalten nach einem Worker-Ausfall

Pro Anwendung und Sweep werden höchstens **zwei** fällige Kampagnen angelegt:

1. Die neueste verpasste Kampagne, deren Frist bereits abgelaufen ist,
   erfasst alle noch vorhandenen Zuweisungen bis zu ihrem geplanten Start.
   Sie wird im selben Sweep geschlossen; ihre Prüfpunkte werden zum Entzug
   eingereiht. So bleiben auch Rechte erfasst, die erst während eines langen
   Worker-Ausfalls hinzugekommen und inzwischen prüffällig geworden sind.
2. Falls die zuletzt begonnene Kampagne noch eine offene Bearbeitungsfrist hat,
   wird diese zusätzlich angelegt. Sie enthält nur neuere Zuweisungen, die nicht
   schon in die überfällige Kampagne gehören. Überfällige Rechte bekommen dadurch
   keine neue Frist; neuere Rechte bekommen keine rückwirkend abgelaufene Frist.

Weil die Bearbeitungsfrist nie länger als das Intervall ist, kann höchstens
ein bereits begonnener Zyklus noch offen sein. Der nächste Start wird auf den
ersten ursprünglichen Intervallzeitpunkt **nach** der aktuellen Zeit
fortgeschrieben. Ältere verpasste Intervalle werden ohne unbeschränkte
Nachholschleife zusammengefasst; `earliestMissedStart` und `skippedCycles` im
Audit dokumentieren den ausgelassenen Zeitraum.

Beispiel: Bei Start `S`, sieben Tagen Intervall und einem Tag Bearbeitungsfrist
ist nach 16 Tagen Ausfall der zuletzt abgelaufene Zyklus `S + 14 Tage` mit
Deadline `S + 15 Tage` maßgeblich. Rechte, die bis `S + 14 Tage` vergeben wurden,
werden zur Prüfung und sofort zum Entzug eingereiht; danach erteilte Rechte
bleiben für den nächsten Zyklus `S + 21 Tage` bestehen. Auch ein erst an `S + 1 Tag`
vergebener Zugriff ist damit erfasst, obwohl er im allerersten Snapshot fehlte.
Bei einem Wiederanlauf an `S + 14 Tage + 12 Stunden` entstehen stattdessen eine
überfällige Kampagne für `S + 7 Tage` und eine noch offene für `S + 14 Tage`.
Jede gespeicherte Deadline bleibt auf ihrem ursprünglichen Kadenzpunkt.

Ein pausierter Plan erzeugt keine neuen Kampagnen. Fällige Entzüge aus
bestehenden Kampagnen und Ablaufdaten werden weiterhin verarbeitet.

## Entscheidungen und tatsächlicher Entzug

| Entscheidung | Bedeutung |
| --- | --- |
| `Pending` | Entscheidung steht aus |
| `Retained` | Unabhängiger berechtigter Prüfer hat diese Zuweisung bestätigt |
| `RevocationQueued` | Ein Entzugsauftrag wurde angelegt; Worker-Erfolg gesondert prüfen |
| `AlreadyRevoked` | Die Zuweisung ist im aktuellen Portalbestand nicht mehr vorhanden |

Ein fehlender Reviewentscheid bei Fristablauf erzeugt einen idempotenten
Systemauftrag. Ein vorhandener Entzug wird nicht nochmals angelegt. Wurde
ein früherer fehlgeschlagener Entzug nach einer Reconciliation verworfen,
kann eine neue Frist denselben Auftrag wieder ausführbar machen; das Audit
vermerkt `revocation.requeued`. Der
Worker entfernt das Keycloak-Mapping über den bestehenden Connector und
prüft dessen Nachbedingung, bevor er den Auftrag als `Applied` speichert.
Fehlgeschlagene oder widersprüchliche Entzüge bleiben als Auftrag sichtbar.
Ein abgeschlossener Review ist deshalb keine Aussage, dass jeder daraus
entstandene Entzug bereits ausgeführt wurde. Bereits ausgestellte JWTs
behalten ihre separat konfigurierte Gültigkeitsdauer.

Bestätigungen werden abgewiesen, wenn die Zuweisung bereits abgelaufen ist,
ein nicht abgelehnter Entzug für dieselbe Zuweisung existiert oder eine andere
Prüfpflicht für sie bereits unbestätigt abgelaufen ist. Letzteres gilt auch
vor dem nächsten Worker-Sweep. Die bisherigen Prüfungen auf Selbstzertifizierung,
eindeutige Identität, aktuellen Directory-Stand und vollständige
Gruppenmitgliedschaften bleiben bestehen.

## Persistenz und Upgrade

Schema-Version **2** ergänzt `review_schedules` und einen Index für fällige
aktive Pläne. Anwendungen werden nicht umgeschrieben. Alte Review-Dokumente
bleiben lesbar; `scheduledFor` ist optional. Die Migration ist wiederholbar
und wird unter dem vorhandenen PostgreSQL-Migrationslock ausgeführt.

Vor dem Start der neuen API und Worker den bisherigen
`Permora.Worker --migrate`-Ablauf mit der separaten Migrationsidentität
ausführen. Die API-Readiness verlangt Schema-Version 2. Laufzeitidentitäten
benötigen dieselben Lese-/Schreibrechte auf `review_schedules` wie auf den
bisherigen fachlichen Tabellen. Vor dem Upgrade ein wiederherstellbares Backup
erzeugen; Review- und Schedule-Dokumente gehören zum Portal-Backup.
Ein Rückwechsel auf ältere Binärdateien ist nach dem Schreiben neuer Review-
Metadaten kein geprüfter Rollback: Die ältere strikte JSON-Deserialisierung
kennt `scheduledFor` nicht. Wiederherstellung und Versionsrücknahme müssen
deshalb zusammen geplant werden.

Planänderung, automatischer Start, Reviewentscheid und Deadline-Verarbeitung
verwenden denselben Anwendungstransaktionslock. Zwei Worker dürfen deshalb
denselben fälligen Start nicht doppelt materialisieren. Die **eigene**
Planrevision verhindert veraltetes Überschreiben, ohne die Anwendungsrevision
oder bereits genehmigte fachliche Änderungen anzutasten.

Das Audit enthält `review.schedule_updated`, `review.scheduled` und
`review.overdue` zusätzlich zu den bestehenden Entscheidungs- und
Entzugsereignissen. Frei eingegebene Plannamen werden nicht in die unveränderlichen
Auditdetails kopiert. Automatische E-Mail-, Push- oder Eskalationsbenachrichtigungen
sind in diesem Paket nicht enthalten; der Bearbeitungsstand steht im Portal
und in der Auftrags-/Auditansicht.

API-Vertrag: [API](api.md#periodische-rezertifizierung).
Prüfungen: [Teststrategie](testing.md#periodische-rezertifizierung-22102026).

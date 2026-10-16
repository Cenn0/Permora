# Beiträge zu Permora

Vor Änderungen [Architektur](docs/architecture.md), [Sicherheitsmodell](docs/security.md) und [Roadmap](docs/roadmap.md) lesen. Kleine nachvollziehbare Änderungen mit Tests bevorzugen. Die primäre Programmiersprache ist C#, das Frontend bleibt Angular/TypeScript.

Ein PR beschreibt Anforderung, Änderung, Sicherheitswirkung, ausgeführte Prüfungen und verbleibende Grenzen. „Tests vorhanden“ und „Tests bestanden“ nicht gleichsetzen. IDs fremder Anwendungen, Scope-/Composite-Manipulation, Wiederholungen und Teilausfälle müssen negative Tests erhalten. Neue privilegierte Keycloak-Endpunkte nie als freien Proxy einführen.

Keine echten Benutzer, privaten E-Mail-Adressen, Tokens, Secrets, Produktionskonfigurationen oder personenbezogenen Dumpdaten committen. Lokale Testidentitäten verwenden. Änderungen an einer produktiven Umgebung sind keine implizite Folge eines Repository-PRs.

`bash scripts/check.sh` ist ein manueller Prüfablauf. Es gibt auf ausdrücklichen Projektwunsch noch keine CI. Neue Workflows nicht ohne separate Zustimmung hinzufügen. Lockfiles erst aus einer echten Paketauflösung übernehmen, niemals Prüfsummen erfinden.

Quellcode und das bereitgestellte einfache SVG stehen unter der bestehenden Apache-2.0-Lizenz, soweit keine Datei ausdrücklich eine andere Lizenz trägt. Die Lizenzen aller tatsächlichen transitiven Abhängigkeiten müssen vor einer Release-Distribution geprüft werden.

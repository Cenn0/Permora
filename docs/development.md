# Entwicklung

## Voraussetzungen und Wiederholbarkeit

Backend: SDK-Version aus `global.json` (10.0.402), C# 14, net10.0. Web: Angular 22.2.3; Node laut `web/package.json`; TypeScript 6.0.x. Das vorhandene Host-Node 22.16.0 reicht für die isolierten Node-Tests, aber **nicht** für den gewählten Angular-22-Build. Globales TypeScript 5.8.3 ist ebenso kein Ersatz für den deklarieren Angular-Compiler.

Direkte .NET-Versionen stehen in `Directory.Packages.props`, Web-Versionen in `web/package.json`. Es wurden keine erfundenen Lockfile-Integritäten eingetragen. In einer netzwerkfähigen Umgebung zunächst `dotnet restore Permora.slnx --use-lock-file` und `cd web && npm install` ausführen. Erzeugte Lockfiles überprüfen und einchecken; danach `dotnet restore --locked-mode` und `npm ci` verwenden. Sicherheits- und Lizenzbewertung der tatsächlichen transitiven Auflösung ist ein eigener Release-Gate.

Primärquellen, am 16.10.2026 geprüft: [Microsoft .NET 10 Downloads](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [Angular-Versionstabelle](https://angular.dev/reference/versions), [Angular-Pakete](https://www.npmjs.com/package/@angular/core), [Npgsql 10.0.3](https://www.nuget.org/packages/Npgsql/10.0.3), [xUnit v3](https://xunit.net/docs/getting-started/v3/whats-new).

## Konfiguration

Alle Prozesse lesen die normale .NET-Konfiguration. Optional lädt `PERMORA_CONFIG` eine explizit angegebene JSON-Datei; Umgebungsvariablen überschreiben deren Werte. `ConnectionStrings__Portal` oder `Portal__Authority` sind entsprechende Environment-Overrides. Keine vom Browser stammenden Pfade/URLs als Connector-Konfiguration verwenden.

`config/portal.example.json` enthält dokumentierte Platzhalter für API/Worker. Der API-Prozess benötigt keine administrativen Keycloak-Service-Geheimnisse; nur der Worker erhält diese gemountet. Für den Provisioner eine **separate** Datei aus `config/provisioner.example.json` und einen separaten Secret-Mount verwenden.

`python3 scripts/init-local.py` erzeugt ignorierte, lokale Zufallsgeheimnisse und einen Development-only-Katalogzugang. Vorhandene Dateien werden nicht überschrieben. Nicht in einer produktiven Umgebung ausführen. Demo-Anwendungen besitzen bewusst keine fingierten effektiven Rechte.

## Projektregeln

Domänenänderungen beginnen bei `Permora.Core` und ihren Tests. Infrastruktur implementiert Ports. Controller/Minimal-API-Endpunkte enthalten keine frei formulierbaren Keycloak-Requests. Zuweisungen werden nicht durch direkte Datenbankedits „freigeschaltet“.

Die API benutzt handgeschriebene typisierte Angular-Verträge; automatische OpenAPI-Clientgenerierung ist noch nicht implementiert. Änderungen an JSON-Namen sind deshalb beidseitig zu prüfen. Unbekannte JSON-Felder, Ganzzahl-Enums und nicht optionale Nullwerte werden serverseitig abgewiesen.

Das Frontend verwendet Standalone Components, Signals, Lazy Routes und native Formulare mit Angular Forms. Es wurde nicht fälschlich eine ungenutzte Material-/NgRx-Abhängigkeit ergänzt. Sicherheitsentscheidungen treffen immer API/Worker, nie ein Route Guard.

Die GitHub-Actions-CI befindet sich unter `.github/workflows/ci.yml`; der manuelle Ablauf bleibt unter `scripts/check.sh`. Die Builds wurden in GitHub Actions erfolgreich ausgeführt; für eine reproduzierbare Release-Abnahme fehlen weiterhin eingecheckte Lockfiles und die weitergehenden FGAP-Zielbenutzer-/Gruppen- und Betriebsnachweise.


## OIDC im lokalen Development-Proxy

Neben `/api` und `/bff` leitet `web/proxy.conf.json` die Callback-Routen `/signin-oidc` und `/signout-callback-oidc` an das ASP.NET-Backend weiter. Echte SSO-Tests setzen einen konfigurierten Keycloak-Realm, den vertraulichen Portal-Client, explizite Redirect-URIs und Client Secret voraus. Die Demoanmeldung ist dafür kein Ersatz.

Die neuen manuellen Prüfskripte trennen ein einmaliges Lockfile-Bootstrap von reproduzierbaren Testläufen mit `npm ci` und `dotnet restore --locked-mode`. Lockfile-Integritäten dürfen nicht erfunden werden.

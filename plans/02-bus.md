# Plan — KNXnet/IP, Download und Secure

[Gesamtplan](00-gesamtplan.md) · [Aktueller Funktionsumfang](../docs/status.md#network-and-commissioning)

## Steps

- [x] KNXnet/IP-Framecodec, Discovery, Tunnel und Routing implementieren.
- [x] cEMI-, Management- und DPT-Grundlagen mit Byte-Vektoren und Loopback prüfen.
- [x] Abbruch, Ressourcenfreigabe und erneuten Tunnelaufbau absichern.
- [x] Monitor-, Scan- und Programmier-Demos klar kennzeichnen.
- [x] Adressiertes MDT-M112-Management und expliziten Programmiermodus-Schreibtest mit Rücklesen und frischer Wiederherstellungsverbindung am freigegebenen Einzelgerät prüfen.
- [x] Lesende Koppler-/Geräteabfrage sowie enges M112-Wiederholungsladen und tatsächliche Parameteränderung mit unabhängig bestätigter Konfigurationswiederherstellung prüfen.
- [x] Explizite lesende Workbench-Verbindung, Gruppenmonitor und adressierten Scan mit gejointem Verbindungsabbau integrieren.
- [x] Authentisierte Secure-UDP-Sitzungen mit OS-X25519 und unabhängigen Windows-/Linux-Protokoll- und Loopbackprüfungen als Bibliothek ergänzen.
- [x] Extended-cEMI-Längen und gemeinsame Raw-Frames mit veröffentlichten Vektoren prüfen.
- [x] Geschützte Keyrings mit Signaturprüfung und exakter Interface-Auswahl importieren.
- [x] Data-Secure-Gruppenpfad mit expliziter Policy, dauerhaften Zählern und optionalem Tunnel-Hook als Bibliothek ergänzen; Sync und sichere Erstinbetriebnahme getrennt halten.
- [x] Explizite IP-Secure-Verbindungswahl in Workbench/MCP/CLI mit gemeinsamem Gateway-/Credentialbesitzer und geschützter Gruppenabgrenzung integrieren.
- [x] Explizit provisionierten individuellen Tool-/P2P-Pfad mit authentisiertem Client-Sync, dauerhafter Zählerzusammenführung und gejointem Tunnel-Initializer als Bibliothek ergänzen; Anwendungseinbindung und sichere Erstinbetriebnahme bleiben getrennt.
- [x] Explizite Tool-Access-Managementoperation an gemeinsame Factory binden: signierter ToolKey, vorhandener vertrauenswürdiger Zählerzustand, Sync vor Management und frische Recoveryverbindungen; als Bibliotheksprofil mit Windows-/Linux-Loopback prüfen.
- [x] Tool-Access-Konsolen-/M112-Pfad und separaten lesenden MCP-Identitätsabruf mit privaten Referenzen, expliziter Zählervertrauensentscheidung und vollständiger Wirebudgetprüfung vor Gerätezugriff integrieren.
- [x] Korrekte Adressbroadcasts, System-Priority, serielle Services und begrenzte lesende Discovery mit vollständiger Antwortprüfung und gejointem Abbruch ergänzen; [Adressierungsprofil](../docs/individual-addressing.md).
- [x] Expliziten Plain-Adressierungsablauf mit physischer Geräte-/Zielbestätigung, Kollisionsbeobachtung, dauerhaftem Auftrag und lesendem Verify/Kalt-Abgleich an Workbench/MCP anschließen; [Profilgrenzen und offene Hardwareabnahme](../docs/individual-addressing.md).
- [x] Vorhandene vertrauenswürdige Data-Secure-Gruppenzustände mit signierter Absenderpolicy an Factory, lesenden Monitor und ausdrücklich konfigurierten MCP-Host binden; Geräteprovisionierung getrennt halten.
- [x] Explizite Workbench-Gruppenauswahl mit Quellenbindung, erneuter Auftragsbestätigung, Monitor-Provenienz und gejointem Projekt-/Dialogabbau ergänzen; Scan und M112 behalten ihre Zielpolicy.
- [x] Gezielten adressierten Scan mit tatsächlichen Koppler-/Tasterantworten und lesende Workbench-Verbindung einschließlich Verbindungsabbau prüfen.
- [x] Enges M112-Herstellerprofil mit vollständigem Applikationsdownload, Verify und bestätigter Wiederherstellung am freigegebenen Taster abnehmen.
- [x] Separate echte Plain-M112-Warteschlange mit Quellen-/Identitätsbindung, Einzel-/Batchbestätigung, dauerhaften Belegen und gejointem Wiederherstellungs-/Schließablauf integrieren; [Profilgrenzen](../docs/real-programming-queue.md).
- [ ] Breitere Transport-Interoperabilität, Herstellerimages und weitere Management-/Ladeprofile abnehmen.
- [ ] Secure-Tunnel, Data Security und sichere Inbetriebnahme vollständig integrieren.

## Hardwareentscheidung

Seit 2026-10-29 hat der Nutzer Programmierung und Schreiben an einem bestimmten
Taster der vorhandenen Installation ausdrücklich freigegeben. Der Umfang und die
Prüfschritte stehen im [Einzelgeräteplan](06-commissioning.md). Die zuvor
zurückgestellte allgemeine Hardware-Abnahme bleibt offen. Simulation und Loopback
ersetzen die tatsächliche Geräteabnahme nicht.

## Spätere Abnahme

Protokollfelder, Framegrößen und APCI-Zuordnung mit einem freigegebenen Gerätetest
prüfen. Timeout, Sequenzfehler, Abbruch, Wiederverbindung und Verify einschließen.
Discovery oder ein erfolgreicher Verbindungsaufbau beweisen keinen Download.

Echte Bus-Schreibzugriffe, Downloads, Programmierung und Neustarts benötigen einen
ausdrücklich freigegebenen Testumfang und eine validierte gerätespezifische Umsetzung.

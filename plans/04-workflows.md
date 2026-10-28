# Plan — Arbeitsabläufe und lokale Historie

[Gesamtplan](00-gesamtplan.md) · [Oberfläche](03-ui.md)

- [x] Erfasste Telegramm-Metadaten wahrheitsgetreu speichern und exportieren.
- [x] Massenverknüpfungen tabellarisch prüfen, Ziele bewusst wählen und atomar übernehmen.
- [x] Bewusste TP-/Bestandsvorlagen im Projektassistenten anbieten.
- [x] Parameterseiten mit zugänglicher Kontexthilfe und begrenzten Ressourcen ergänzen.
- [ ] Dauerhafte Telegrammabfragen mit Zeitkontext und sichtbarer Datenabdeckung integrieren.
- [ ] Versionierte lokale Sitzungswiederherstellung und getrennten Änderungsnachweis ergänzen.

Vorhandene Entwürfe, native Archivspeicherung, Undo und vollständige
Sitzungsaufzeichnung bleiben erhalten. Fehlende Kompatibilitätsfakten,
Aufzeichnungslücken und Recovery-Grenzen müssen ausdrücklich sichtbar bleiben.
Geschützte Inhalte und Passwörter werden nicht still als Klartext abgelegt.

Aktueller Integrationsstand: Metadaten, Massenreview, Startvorlagen und begrenzte
Parameterlayouts/-ressourcen bestehen den integrierten Core-/Bus-/MCP- und
UI-Prüflauf mit acht Sprachen und 828 nativen Dialogprüfungen je Sprache (DE/EN).
Ein gemeinsames Bitmapbudget und erneute Ressourcenbindung sind nativ geprüft. Der
Telegrammstore ist mit 18 Fällen geprüft; UI und Aufnahmeadapter folgen im nächsten
vertikalen Schnitt. Wiederherstellung befindet sich weiterhin im Entwurf.
Weitere Schritte bleiben bis zu ihrem integrierten Prüflauf offen.

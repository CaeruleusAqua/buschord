# Plan — Arbeitsabläufe und lokale Historie

[Gesamtplan](00-gesamtplan.md) · [Oberfläche](03-ui.md)

- [x] Erfasste Telegramm-Metadaten wahrheitsgetreu speichern und exportieren.
- [x] Massenverknüpfungen tabellarisch prüfen, Ziele bewusst wählen und atomar übernehmen.
- [x] Bewusste TP-/Bestandsvorlagen im Projektassistenten anbieten.
- [x] Parameterseiten mit zugänglicher Kontexthilfe und begrenzten Ressourcen ergänzen.
- [x] Dauerhafte Telegrammabfragen mit Zeitkontext und sichtbarer Datenabdeckung integrieren.
- [x] Versionierte lokale Modell-/Undo-Wiederherstellung und getrennten Änderungsnachweis ergänzen.

Vorhandene Entwürfe, native Archivspeicherung, Undo und vollständige
Sitzungsaufzeichnung bleiben erhalten. Fehlende Kompatibilitätsfakten,
Aufzeichnungslücken und Recovery-Grenzen müssen ausdrücklich sichtbar bleiben.
Geschützte Inhalte und Passwörter werden nicht still als Klartext abgelegt.

Stand: 2026-10-29. Der integrierte Stand besteht 30 Suiten, den MCP-Selbsttest,
854 native Dialogprüfungen und 33 Recovery-Lifecycleprüfungen je Sprache (DE/EN).
Acht Sprachen, Aufnahmeadapter, Query-/Export-/Retentionsabläufe sowie
Quellenbindung und geschützte Inhalte sind geprüft. Native Linux-Dateirechte
werden separat geprüft; daraus folgt keine Linux-GUI-Abnahme.

[Historienablauf](../docs/workflows.md) und [Wiederherstellung](../docs/recovery.md)
beschreiben Opt-in, Datenabdeckung, Speichergrenzen und bewusste Übernahme.
Checkpoints enthalten übernommenes Modell und Undo/Redo. Private Parameter-/DEMO-
Entwürfe und Fensterlayout sind ausgenommen; unabhängiger Audit kann weiter reichen
als der letzte Checkpoint. Eine vollständige Herstellerumgebung oder
Stromausfallabnahme wird damit nicht behauptet.

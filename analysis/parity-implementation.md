# BusChord: REA-Abgleich und umgesetzte Arbeitsabläufe

Stand: 2026-10-18. Original: ETS6 6.4.8718.0, statische REA-Analyse,
keine Ausführung fremder Assemblies. **Kein Nachweis vollständiger ETS-Kompatibilität.**

Der [Workflow-Audit](parity-rea-audit.md) trennt belegte Originaltypen/Call-Edges,
BusChord-Quellbefunde und offene Fragen. Detailbelege und Tests:

- [Projektarchive](parity-archives.md): verlustvermeidendes Speichern und Undo.
- [Parameter](parity-parameters.md): reale Herstellertypen statt nur synthetischer XML.
- [Manuelle Verknüpfungen](parity-links.md): direkte Objekt-/Gruppenadressbearbeitung.
- [Katalogoberfläche](parity-catalog-ui.md): Suche, Sektionen und sichere Einfügung.
- [Smart Linking](parity-smartlinks.md): keine DPT-1-/DPT-10-Verwechslung.

## Priorität: gefährliche Teilimplementierungen schließen

Der bisherige Parameterdialog bearbeitete neu geparste Objekte ohne dauerhaften
Projektzustand. Projekt- und Undo-Serialisierung ließ zusätzliche Daten weg.
Die Eigenschaftenanzeige behielt nach Undo alte Modellinstanzen; außerdem fehlte
eine Namensinitialisierung bei Geräteauswahl. Diese Fehler sind wichtiger als
weitere Menüpunkte ohne funktionierenden Datenfluss.

Adressvalidierung prüft nun die echten Vier-Bit-Grenzen von Bereich/Linie.
`16.1.1` und `1.16.1` dürfen nicht still in andere Busadressen umgewandelt werden.
Der Bulk-Smart-Link-Dialog bietet höchstens einen Kandidaten je Objekt an;
manuelle zusätzliche Verknüpfungen bleiben bewusst möglich. Unbekannte DPTs sind
kein Kompatibilitätsnachweis; Namensvorschläge müssen fachlich geprüft werden.

## Weitere statische Kataloganalyse

REA `ev_f1eebdacefd7e32fa8f4893dace4ec59f60ac788efae3c9e207be717baae3360`
bestätigt in `Knx.Ets.OnlineCatalog.Download.dll` die Typen `IndexFile`,
`IndexFileEntry`, `IndexFileSection` mit `GetEntryById`, `AddChildSection` und
`FilterByMarket`. SHA-256:
`55f6675ca3c441da326bcfa0c5c47907dc94f81bb0583fe323e294f366abcdc1`.
Dies belegt strukturierte Katalogmodelle, **nicht** eine beobachtete Suchoberfläche.
Die lokale BusChord-Suche ist eine eigene UI-Entscheidung. Kein Zugriff auf
herstellerspezifische Online-Dienste und keine Übernahme proprietären Codes.

## Live-Prüfung

Der vorhandene Probe-Befehl erhielt heute erneut eine DESCRIPTION_RESPONSE vom
Router `192.0.2.10:3671`: MDT SCN-IP100.03, Adresse `1.0.0`, MAC
`02:00:00:00:00:01`. Nur SEARCH/DESCRIPTION, keine Tunnelverbindung, keine
Gruppenwerte gelesen/geschrieben, kein Download, Neustart oder Programmieren.
Siehe [Routerbeleg](router-discovery.md). Dies testet Discovery, nicht Inbetriebnahme.

## Weiter offen

Gebäude-/Raum-/Funktionsmodell samt Editor, vollständige dynamische Herstellerlogik,
Plug-ins/DCA, verschlüsselte echte Projektarchive und Interoperabilität von
Tunnel/Download/Secure bleiben gesonderte Aufgaben. Ein Produktwechsel mit
geänderter Applikation benötigt außerdem eine geprüfte Objekt-/Parameter-Migration.
Diese Runde behauptet weder vollständige Dateikompatibilität noch sichere
Geräteprogrammierung. UI-Smokechecks ersetzen keine interaktive Layoutprüfung.

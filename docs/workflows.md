# Geprüfte Zuordnungen und lokale Historie

## Massenverknüpfungen

„Intelligente Verknüpfung“ öffnet eine Ergebnisliste aller unverknüpften Objekte
im gewählten Umfang. Große Projekte lassen sich auf ein einzelnes Gerät begrenzen.
Die Liste unterscheidet bereit, mehrdeutig, inkompatibel, ohne Ziel und ungeprüft.
Fehlende DPT-, Bit- oder Sicherheitsfakten gelten nicht als bestätigte Kompatibilität.

Bei mehrdeutigen Vorschlägen ein Ziel ausdrücklich in der Zeile wählen. Auch
andere vorhandene Gruppenadressen sind wählbar; jede Wahl wird erneut geprüft.
„Nur Probleme“ filtert die Anzeige und erhält die markierten bereiten Zeilen.
Übernehmen verarbeitet die Auswahl gemeinsam: ein Konflikt verhindert sämtliche
Änderungen, ein Undo setzt den vollständigen Vorgang zurück. Nach Änderungen am
Projekt muss die Vorschau aktualisiert werden.

Die Vorschlagsliste ist begrenzt; die Zielauswahl enthält alle vorhandenen
Gruppenadressen innerhalb der angegebenen Prüfgrenzen. Eine neue Zuordnung ist
keine Geräteprogrammierung und schreibt nichts auf den Bus.

MCP verwendet `mass_link_review` und anschließend `mass_link_apply` mit dem
zurückgegebenen `reviewToken` als Argument `review_token`. Per `targets` lassen
sich Review-Zeilen auf vorhandene Gruppen-IDs setzen. Der Token gilt zehn Minuten
und bindet die genaue Projektsitzung; `row_ids` begrenzt die Übernahme. Ohne
`row_ids` werden alle bereiten Zeilen verarbeitet.

Die bestehenden manuellen Tools `group_link_add` und `smart_link_apply` verweigern
bekannte Konflikte ebenfalls atomar. Für bestehende Aufrufer bleiben unbekannte
Fakten ausdrücklich ungeprüft zulässig und erscheinen als Warnung; sie ergeben
keinen Kompatibilitätsnachweis. `group_link_add` verlangt mit
`require_compatibility=true` vollständige bekannte Kompatibilität. Der neue
Massenreview verwendet diese strenge Grenze immer.

## Telegramm-Metadaten

JSONL- und BusChord-XML-Aufzeichnungen erhalten einen bekannten Hop Count.
Fehlende Metadaten bleiben unbekannt. Der 13-spaltige Monitorexport schreibt keine
erfundenen Flags, Prioritäten oder Ack-Werte und übernimmt den erfassten Hop Count.
Die bisherige sechsspaltige CSV bleibt kompatibel; sie besitzt kein Hop-Feld und
lädt diesen Wert deshalb unbekannt. Nutzdaten werden nicht zu einem vollständigen
cEMI-Rahmen umgedeutet.

Weitere Schritte stehen im [Arbeitsablaufplan](../plans/04-workflows.md).
Die [lokale Wiederherstellung](recovery.md) beschreibt Checkpoints, Undo/Redo,
getrennten Änderungsnachweis, ausdrückliche Freigabe und deren Grenzen.
# Startvorlage

Der Projektassistent beginnt mit TP-Linie 1.1 und bietet die bisherige Struktur
ab 0.0 ausdrücklich als Alternative an. Die Vorschau nennt Anzahl und erste
freie Geräteadresse; Geräte und Koppler entstehen erst durch eine eigene Aktion.
Die Core-API und `project_new` behalten ohne Vorlagenauswahl die bisherige
Null-basierte Semantik. `topology_template: "tp"` wählt im MCP die TP-Vorlage;
deren Bereichs- und Linienanzahl liegt zwischen 1 und 15.

# Parameterseiten und Hilfe

Gültige aktive Tabellenblöcke kleiner Seiten erscheinen mit ihrer deklarierten
Zeilen-/Spaltenanordnung. „Tabellenlayout verwenden“ schaltet zur vollständigen
Feldliste zurück. Große Seiten (mehr als 128 sichtbare Felder), schmale Fenster,
überlappende oder unbekannte Platzierungen behalten die virtualisierte Feldliste.
Entwürfe, Abschnittsnavigation und die vorhandene Prüfung/Übernahme arbeiten mit
denselben Parametern. Der Review zählt vorhandene Links an deaktivierten Objekten;
die Links selbst bleiben erhalten.

F1 im fokussierten Feld oder „Kontexthilfe“ öffnet den deklarierten Herstellertext
und nennt dessen deklarierte Sprache. Das erste Ressourcenprofil liest PNG/JPEG
und UTF-8-Text aus einem deklarierten Hilfe-ZIP ausschließlich im Projektarchiv.
Es führt weder Markup noch Skripte aus und lädt keine externen Ressourcen.
Datei-, Bilddimensions- und Entpackgrenzen begrenzen die Anzeige; fehlende oder
nicht unterstützte Ressourcen erhalten einen sichtbaren Status.
Alle Ansichten eines Ressourcenlesers teilen zusätzlich höchstens 16 Millionen
dekodierte Bildpixel. Gleiche Ressourcen verwenden ein gemeinsames Bitmap;
Verbergen oder Schließen gibt dessen Speicher nach dem letzten Nutzer frei.

## Dauerhafte Telegrammhistorie

„Telegrammhistorie“ im Menü Werkzeuge öffnet vorhandene lokale Daten auch ohne
Projekt. Im Monitor führt derselbe Befehl zur Historie des aktuellen Umfangs.
Öffnen verbindet sich nicht mit dem Bus. Dauerhafte Aufnahme muss ausdrücklich
eingeschaltet werden; die Monitorbeschriftung zeigt sie auch bei geschlossenem
Historienfenster. Pause, Stopp und Verbindungsende begrenzen Aufnahmeintervalle.
Überlauf oder Schreibfehler beendet die Historienaufnahme sichtbar, während der
bisherige Monitor weiterarbeitet. Gepufferte Frames vor dem Aufnahmeanker werden
unverändert ausgelassen und gezählt.

Zeitangaben verlangen vollständige Datumswerte mit UTC oder explizitem Offset.
Der Beginn ist enthalten, das Ende ausgeschlossen. Mehrere durch Semikolon
getrennte Werte eines Filters werden mit ODER, verschiedene Filter mit UND
verknüpft. „Weitere laden“ ergänzt die feste Suchmomentaufnahme; eine neue Suche
erfasst später eingetroffene Daten. Kontextzeilen sind ausdrücklich gekennzeichnet
und können außerhalb der Filter oder des Zeitbereichs liegen. Die Oberfläche hält
höchstens 2000 geladene Zeilen und nennt erreichte Grenzen.

Geladener Zeitraum und bestätigte Aufnahmeabdeckung sind getrennte Angaben.
Abdeckung umfasst geschlossene Intervalle aller Schnittstellen und Projekte,
unabhängig von den Datenfiltern. Aktive oder nach einem Neustart unbekannte Enden
gelten nicht als bestätigte geschlossene Abdeckung. Eine Lücke sagt nichts darüber
aus, ob währenddessen Telegramme auf dem Bus vorhanden waren.

„Geladene Zeilen exportieren“ schreibt nur die sichtbare Suchmenge einschließlich
geladenem Kontext. Unbekannte Metadaten bleiben leer; APDU-Nutzdaten und ein
tatsächlich erfasster cEMI-Rahmen bleiben getrennte Spalten. Die Auswahl öffnet
eine heutige Gruppenadresse oder ein heutiges Quellgerät nur bei derselben
bekannten Projekt-GUID. Historische Geräteidentität wird damit nicht behauptet.

Der lokale Store liegt unter `BusChord.UserData/telegram-history` im
Benutzerdatenverzeichnis. Eine Sicherung benötigt einen neuen Dateinamen. Das
ausdrückliche Löschen älterer Daten verwendet die bestätigte UTC-Grenze und
betrifft alle Projekte und Schnittstellen im Store; vorher die dauerhafte Aufnahme
ausschalten. Abbruch vor der Bestätigung verändert keine Daten. Unter Unix werden
neue Speicherverzeichnisse mit 0700 und neue Datendateien mit 0600 angelegt.

Synthetische Beispieldaten: [Light-Ansicht](images/telegram-history-light.png)
und [Dark-Ansicht](images/telegram-history-dark.png). Die Bilder zeigen eine
eingespeiste Testhistorie und keinen realen Busbetrieb.

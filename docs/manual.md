# BusChord – Benutzerhandbuch

Stand: 2026-10-28.
Dieses Handbuch beschreibt die deutsche Oberfläche der nativen Workbench
(`WorkbenchWindow`). Es nennt nur Wege, die im aktuellen Quellcode existieren;
nicht belegte Punkte sind ausdrücklich als **nicht verifiziert** markiert.

Weiterführende Dokumente:

- [README](../README.md) – Funktionsübersicht, Build, Tests und Screenshot-Befehl
- [Workbench-Domainvertrag](workbench-domain.md) – Datenmodell, Links, Sitzungen, Grenzen
- [Lokale Projekt-Backups](project-backup.md) – Ablage, Retention und Wiederherstellung
- [MCP-Server](../mcp.md) – Agenten- und Skriptbetrieb
- [Netzwerkstatus](status.md) – was live am Bus geprüft wurde

## 1. Was BusChord ist

BusChord ist ein unabhängiges Engineering-Werkzeug für KNX-Gebäudeautomation. Es
liest und schreibt ETS-Projektarchive (`.knxproj`), durchsucht Hersteller-Kataloge
(`.knxprod`), verwaltet Topologie, Gruppenadressen, Kommunikationsobjekte und
Geräteparameter und spricht KNXnet/IP im Netzwerk.

- **Keine ETS-Lizenz nötig.** BusChord ist kein ETS-Bestandteil, nicht von der KNX
  Association freigegeben. *KNX* und *ETS* sind
  Marken der KNX Association und werden hier nur beschreibend verwendet.
- **Projektbearbeitung ist offline.** Adressen, Verknüpfungen und Parameter ändern
  nur das Projekt im Arbeitsspeicher. Eine Busverbindung wird ausdrücklich gewählt.
- **Echte und Demo-Sitzungen sind gekennzeichnet.** Monitor und lesender Scan
  können eine reale Verbindung nutzen. Die Programmierwarteschlange bleibt eine
  Demo-Sitzung; der separate [M112-Konsolenpfad](commissioning.md) hat eigene
  Anforderungen. Secure-Inbetriebnahme und allgemeines DCA-Hosting bleiben offen.

Was BusChord bewusst nicht ist: ein vollständiger ETS-Ersatz und kein Ersatz für
eine fachliche Inbetriebnahme. Offene Grenzen stehen in Abschnitt 11.

## 2. Erste Schritte

### 2.1 Startfläche

Ohne geöffneten Arbeitsgegenstand zeigt der Editor die Startfläche
**„Projekt öffnen oder anlegen“** mit dem Hinweis „Ein Projekt öffnen, neu
anlegen oder den Produktkatalog durchsuchen.“ und den Schaltflächen:

| Schaltfläche | Wirkung |
|---|---|
| **Projekt öffnen …** | Dateidialog für `*.knxproj` |
| **Neues Projekt …** | Dialog „Neues Projekt“, danach Speicherort wählen |
| **Gerät aus Katalog …** | öffnet den Produktkatalog als Tab, auch ohne Projekt |
| **Projektportfolio verwalten …** | öffnet die Liste der registrierten Projektdateien |

Darunter stehen die zuletzt geöffneten Projekte (höchstens zehn, das zuletzt
geöffnete zuerst). Nicht mehr vorhandene Dateien werden ausgeblendet. Ein Klick
öffnet das Projekt sofort. Der Produktkatalog ersetzt die Startfläche auch ohne
Projekt; die übrigen Ansichten bleiben ohne Projekt leer bzw. melden
„Projekt öffnen oder anlegen“.

**Projektportfolio.** **Projekt → Projektportfolio …** und die Startfläche öffnen
dieselbe Liste; sie wird beim ersten Gebrauch aus der Zuletzt-Liste gefüllt und
liegt in `%APPDATA%\BusChord\portfolio.json`. Jeder Eintrag zeigt Name, Pfad,
Dateirevision mit kurzem SHA-256, letzten Öffnungszeitpunkt und den Zustand
**Unverändert**, **Geändert** oder **Fehlt oder verschoben**. Aktionen:
**Öffnen**, **Aktuelles Projekt übernehmen**, **Aktualisieren**, **Alle prüfen**,
**Umbenennen** (Dialog „Anzeigename ändern“), **Datei suchen …** (bei fehlender
Datei) und **Aus Portfolio entfernen**. Entfernen und Umbenennen ändern nur den
Eintrag, nie die Projektdatei; ein ungespeichertes Projekt lässt sich nicht
übernehmen. Fehlt die Datei, bleibt **Öffnen** gesperrt, bis sie gefunden oder
der Eintrag entfernt wurde. Das Portfolio ist eine Liste, keine
Mehrfachbearbeitung: Es wird immer nur ein Projekt gleichzeitig geöffnet;
parallele Projekt-Tabs gibt es nicht. Die
Dateirevision im Portfolio ist ein Dateizähler, nicht der Projekt-Revisionsstand
aus Abschnitt 8.7.

### 2.2 Neues Projekt

„Neues Projekt“ fragt Name, Kommentar, Gruppenadress-Stil (dreistufig, zweistufig,
frei) sowie Anzahl Bereiche und Linien pro Bereich (je 1–15). Nach „Anlegen“ folgt
der Speicherdialog für die `.knxproj`. Wird er abgebrochen, wird **kein** Projekt
angelegt. BusChord schreibt das neue Projekt sofort als natives Archiv.

### 2.3 Projekt öffnen und speichern

- **Projekt → Öffnen …** (`Strg+O`) oder die Startfläche.
- Beim Laden wird das Projekt in die Zuletzt-Liste übernommen.
- **Projekt → Speichern** (`Strg+S`) speichert unter dem bekannten Pfad. Hat das
  Projekt noch keinen Pfad, öffnet der Menüpunkt den Speicherdialog.
- **Projekt → Speichern unter …** wählt einen neuen Pfad und trägt ihn ein.
- Vor dem Überschreiben einer vorhandenen Datei legt BusChord eine lokale
  Sicherung an (Abschnitt 8.1).

Ladefehler erscheinen als Meldungsfenster „Projekt nicht ladbar“ mit dem technischen
Grund; die Statuszeile zeigt denselben Text. Geschützte Archive verwenden das
enthaltene Standardprofil und benötigen das **Projektpasswort**. Unter
**Optionen → Archivschutz** lässt sich der Salt bei Bedarf überschreiben;
lokale Profile liegen in `%APPDATA%\BusChord\archive-protection.json`. Weitere Profilparameter sind
`iterations`, `hashAlgorithm`, `keyLength` und `passwordEncoding`.
Fehlt diese Datei, gilt das enthaltene Standardprofil. Eine vorhandene ungültige
Konfiguration wird gemeldet; ein explizit leerer Salt deaktiviert geschützte Vorgänge.
Bei falschem Passwort erscheint der Dialog erneut;
Abbrechen lässt das aktuelle Projekt unverändert.

### 2.4 Die Workbench

Die Produktoberfläche ist eine objektbezogene Workbench:

- **Kopfzeile** – Marke, die Menüs **Projekt**, **Bearbeiten**, **Ansicht** und
  **Werkzeuge**, die Befehlssuche (`Strg+K`), die Anzeige **OFFLINE** und die
  Schaltfläche **Programmieren** (öffnet das Werkzeug „Programmierung“).
- **Tab-Leiste** – Arbeitsgegenstands-Tabs und die Schaltfläche **Vergleich**, die
  die aktive Gruppenadresse im schreibgeschützten Nebenfenster verfolgt
  (Abschnitt 2.6).
- **Aktionsleiste links** – Perspektiven **Gebäude**, **Topologie** und **Gruppen**;
  darunter **Produktkatalog**, **Projektprüfung** (mit Befundzähler), **Monitor**,
  **Optionen** und unten die Hilfe-Schaltfläche (Fragezeichen, Tooltip
  „Bedienung und Grenzen“).
- **Navigator** – Projektname und -umfang, Perspektivumschalter, Suchfeld und
  Projektbaum. Jede Perspektive hat eigenen Suchtext, eigene Auswahl, eigenen
  Aufklappzustand und eigene Scrollposition.
- **Arbeitsgegenstands-Tabs** – Projekt, Raum, Linie, Gerät und Gruppenadresse;
  der Produktkatalog ist ebenfalls ein Tab. Jeder Tab behält während der Sitzung
  Darstellung, Filter, Auswahl, Aufklappzustand und Scrollposition. `+` öffnet die
  Befehlssuche, das Kreuz schließt den Tab, `Strg+Tab` wechselt zum nächsten Tab.
- **Editor** – Breadcrumb, Titel, **Beobachten**, **+ Gerät**, **Eigenschaften**
  und die Darstellungen **Kommunikationsobjekte**, **Parameter**, **Beziehungen**,
  **Prüfung** und **Busdiagramm**.
- **Inspektor** – rechts, standardmäßig ausgeblendet; Bereichswähler
  **Eigenschaften**, **Gruppen**, **Suche**, **Arbeitsbereiche**, **Vorgänge**.
  Die Fußaktionen bleiben beim Scrollen sichtbar.
- **Werkzeuge unten** – **Monitor**, **Programmierung**, **Scan**, **Suche**,
  **Änderungen** und **Prüfmeldungen**.
- **Statuszeile** – Projektname, letzte Meldung und der feste Hinweis
  „Lokal · keine Busverbindung“.

Die Workbench hat keine Oberfläche zum Verbinden, Senden, Lesen oder Downloaden.
Die Anzeigen **OFFLINE** und „Lokal · keine Busverbindung“ sind feste Hinweise,
keine umschaltbaren Zustände.

### 2.5 Navigation, Suche und Auswahl

**Busdiagramm.** Die Darstellung **Busdiagramm** zeigt die gesamte vorhandene
Projektstruktur: Installationen, Bereiche, Linien, Segmente und Geräte. Auch leere
Container sowie direkt am Bereich oder an der Linie abgelegte Geräte bleiben
sichtbar. Die Linien zeigen diese Zuordnung; sie beschreiben keine ermittelte
Verdrahtung und keinen Live-Zustand. **Koppler** erscheint nur bei belegten
Hersteller-Hardwaredaten. Segmentmedien erben gegebenenfalls das Linienmedium.

Klicken wählt einen Knoten. **Im Editor öffnen**, Enter oder Doppelklick öffnet
Geräte und Linien im vorhandenen Editor; Segmente öffnen ihre zugehörige Linie.
Der Inspektor zeigt die Diagrammauswahl lesend; Bearbeiten beginnt im geöffneten Editor.
Links/Rechts wechseln zum Eltern-/Kindknoten, Auf/Ab zum nächsten Knoten derselben
Spalte, Pos1/Ende zum ersten/letzten Knoten. **Einpassen**, **100 %**, +/− und
Strg+Mausrad steuern den Zoom; Ziehen am freien Hintergrund oder mit der mittleren
Maustaste verschiebt den Ausschnitt. Große Diagramme bleiben scrollbar, statt
beim Einpassen auf unlesbar kleine Punkte zu schrumpfen. Auswahl und Zoom bleiben
beim Tabwechsel erhalten; Projektänderungen und Undo/Redo aktualisieren die Ansicht.

- Ein Klick auf einen Knoten mit Arbeitsgegenstand (Projekt, Raum, Linie, Gerät,
  Gruppenadresse) öffnet den zugehörigen Tab. Der Pfeil links klappt Kinder auf
  oder zu; `→`/`←` machen dasselbe für den ausgewählten Knoten.
- Das Suchfeld im Navigator filtert den Baum der aktuellen Perspektive nach Name,
  Adresse oder Objekt.
- `Strg+K` bzw. die Kopfzeilenschaltfläche öffnet die **Befehlssuche**. Sie findet
  Projekt, Räume, Linien, Geräte, Gruppenadressen sowie Befehle (Katalog,
  Projektprüfung, Monitor, Programmierung, Layout zurücksetzen, Projekt öffnen,
  Hilfe). Pfeil ab, `Enter` ausführen, `Escape` schließen.
- **Werkzeuge → Suche** durchsucht das ganze Projekt (optional regulärer Ausdruck,
  Groß-/Kleinschreibung) und bietet unter **Ersetzen** eine Vorschau vor dem
  Übernehmen. Ersetzt werden nur Namen, Kommentare und Objekttexte, keine Adressen
  oder Referenzen.
- In der Objekttabelle wählt ein Klick eine Zeile, `Strg`/`Shift` erweitern die
  Auswahl. Die Checkbox-Spalte spiegelt dieselbe Auswahl; die Kopf-Checkbox ist
  dreiwertig (alles, nichts, teilweise). **Bearbeiten → Alles auswählen** und
  **Auswahl aufheben** wirken auf den aktiven Tab. Die Mehrfachauswahl gilt nur
  für diesen Arbeitsgegenstand.
- `→`/`←` klappen in der Tabelle Geräte und Kanäle auf oder zu.
- **Gerät verschieben:** Eine Gerätezeile lässt sich auf eine Linie oder ein
  Segment im Navigator oder auf die Tabelle eines Linien-Tabs ziehen. Der
  Einbauort und die Zuordnungen bleiben erhalten; ein unzulässiges Ziel ändert
  nichts. Tastaturweg: Kontextmenü der Gerätezeile (`Shift+F10` oder Menütaste) →
  **Verschieben …**, dann Ziellinie wählen.

### 2.6 Favoriten und Nebenfenster

- **Favorit** setzt bzw. entfernt eine Markierung an Navigatorknoten und in den
  Fußaktionen des Inspektors. Favoriten gelten pro Projekt, werden beim
  Projektwechsel geleert und im Layout gespeichert (höchstens 100).
- **Daneben öffnen** öffnet ein schreibgeschütztes Nebenfenster für ein Gerät oder
  eine Gruppenadresse. Es zeigt Adresse, DPT, Links und Verweise, folgt
  Projektänderungen und bietet **Im Hauptfenster zeigen**. Der Arbeitsgegenstand
  bleibt ein Tab im Hauptfenster; das Nebenfenster besitzt keine eigene Auswahl
  und kein eigenes Layout. Pro Ziel gibt es höchstens ein Fenster; es schließt mit
  dem Hauptfenster.
- **Vergleich** in der Tab-Leiste oder `Strg+Shift+G` öffnet dieselbe
  schreibgeschützte Nebenansicht für die aktive Gruppenadresse; ist keine
  Gruppenadresse aktiv, wird die erste Gruppenadresse numerisch gewählt. Das ist
  keine Projektdatei-Vergleichsansicht; diese steht unter **Projekt → Projekt
  vergleichen …** (Abschnitt 9.3).

### 2.7 Layout, Arbeitsbereiche und Sprache

- Größen und Sichtbarkeit von Navigator, Inspektor und Werkzeugbereich werden als
  Layout gespeichert, getrennt von den Projektdaten:
  `%LOCALAPPDATA%\BusChord\workbench-layout.json`; je Projekt zusätzlich
  `%LOCALAPPDATA%\BusChord\projects\<projekt-id>.json`.
- **Ansicht → Fokuslayout** blendet Navigator, Inspektor und Werkzeuge aus,
  **Layout zurücksetzen** stellt die Standardgrößen wieder her. Beides ändert
  keine Projektdaten.
- **Inspektor → Arbeitsbereiche** speichert benannte Layouts unter
  `%APPDATA%\BusChord\workspaces` und lädt sie wieder. Unlesbare oder veraltete
  Layoutdateien werden ignoriert, ohne den Start zu blockieren.
- **Ansicht → Optionen …** (oder die Aktionsleiste) öffnet die Einstellungen:
  Design (System/Hell/Dunkel), Sprache,
  "Vor dem Löschen nachfragen", Bus-Schnittstelle, eigene Tunneladresse und die
  Zuletzt-Liste. Design und Sprache werden beim Speichern sofort übernommen; bereits
  gezeichnete Menüs und Beschriftungen können bis zum Neustart in der alten Sprache
  bleiben. Die Einstellungen liegen in `%APPDATA%\BusChord\settings.json`.
  Das globale Archivschutzprofil wird separat gespeichert; sein Salt-Feld ist
  maskiert und gibt seinen Inhalt auch über Accessibility nicht aus.
  Verfügbar sind **Deutsch**, **English**, **Französisch**, **Italienisch**,
  **Spanisch**, **Niederländisch**, **Chinesisch (vereinfacht)** und **Polnisch**.
  Berichtsspezifische Tabellen und MCP-Exporte
  bleiben vorerst Deutsch/Englisch. Eine RTL-Darstellung wird nicht unterstützt.

**Optionen → Updates** öffnet die globalen Updateeinstellungen. Für Setup-/Portable-
und AppImage-Installationen stehen **Automatisch herunterladen und installieren**,
**Nur nach Updates suchen** und **Automatische Suche aus** zur Wahl. Der erste
Modus ist voreingestellt; **Vorabversionen** müssen bewusst als Kanal gewählt
werden. **Jetzt prüfen** führt auch bei ausgeschalteter Automatik eine reine
Prüfung aus. Die Einstellungen werden unabhängig vom Projekt gespeichert.

Automatische Prüfungen laufen beim Start und alle sechs Stunden. Verifizierte
Updates wechseln die Version vor dem nächsten Start oder nach normalem Beenden;
offene Instanzen, ungespeicherte Entwürfe und abwickelnde Sitzungen behalten ihre
bestehenden Schutzmechanismen. Zusätzliche oder veränderte Dateien im Windows-
Programmordner stellen den Austausch zurück. Ein fehlgeschlagenes Update lässt
sich über **Fehlgeschlagenes Update erneut versuchen** bewusst wieder freigeben.

Ein Linux-deb wird über den systemweiten Paketdienst aktualisiert; dessen
Richtlinie verwaltet die Administration. Ein AppImage benötigt einen vom Nutzer
beschreibbaren Speicherort. Entwicklungsstarts ohne eingerichtete Updatequelle
zeigen dies im Dialog an. Paketbau, Linux-Abhängigkeiten und die noch laufende
Distributionsabnahme stehen unter [Installation und Updates](distribution.md).

### 2.8 Hilfe und Dokumentation

Die Schaltfläche mit dem Fragezeichen unten in der Aktionsleiste (Tooltip
**„Bedienung und Grenzen“**), `F1`, **Werkzeuge → Bedienung und Grenzen** oder der
Befehl **Hilfe** in der Befehlssuche öffnet ein modales Fenster mit den Abschnitten
**Arbeitsweise**, **Tastenkürzel**, **Zuordnungen**, **Parameter**, **Sitzungen**
und **Grenzen**. **Schließen** oder `Escape` beendet es. Das ausführliche
Benutzerhandbuch ist diese Datei (`docs/manual.md`); einen eingebauten
Dokumentationsbrowser gibt es nicht.

### 2.9 Dialoge

Alle Dialoge sind modal zum Hauptfenster. `Escape` und **Abbrechen** schließen
ohne Änderung; die primäre Aktion ist die Standardtaste, destruktive Aktionen sind
nie vorausgewählt. Zu den Dialogen gehören **Neues Projekt**, **Optionen**,
**Projektportfolio**, **Neue Gruppenadresse**, **Zuordnen**, **Sicherungen**,
**Verschieben**, **Projekt vergleichen**, **Produkt ersetzen**, **Hilfe**, die
Passwortabfrage sowie die Bestätigungen für Löschen, Verwerfen, Wiederherstellen
und Demo-Reset. Eine
Änderung wird erst mit der ausdrücklichen Aktion im Dialog übernommen.

## 3. Katalog

Der Produktkatalog ist ein Tab des Arbeitsbereichs, kein eigenes Fenster. Er ist
über die Aktionsleiste **Produktkatalog**, **Projekt → Produktkatalog**,
**Werkzeuge → Produktkatalog**, die Startfläche oder **+ Gerät** erreichbar – mit
oder ohne geladenes Projekt.

Wird der Katalog aus einem Raum oder einer Linie geöffnet, übernimmt die
Einfügeleiste diesen Raum bzw. diese Linie als Ziel.

### 3.1 Bibliothek importieren

„Importieren …“ öffnet einen Mehrfach-Dateidialog für **`.knxprod`** und
**Hersteller-ZIPs** (`.zip`). Importierte Kataloge werden in die lokale Bibliothek
kopiert:

```
%APPDATA%\BusChord\catalogs
```

Die Bibliothek ist links sichtbar („Bibliothek · Anzahl“) und zeigt je Eintrag
Name, Hersteller und Produktzahl. „Neu laden“ liest die Bibliothek erneut ein;
„Katalog entfernen“ löscht den gewählten Bibliothekseintrag. Der Import läuft im
Hintergrund und lässt sich abbrechen; bereits übernommene Kataloge bleiben.

Dateien, die nicht gelesen werden können, blockieren den Import nicht: sie werden
im Fehlertext über der Bibliothek mit Dateiname und Grund aufgeführt. Über der
Liste steht eine Zusammenfassung des letzten Imports mit Produkten, Programmen und
Hinweisen auf defekte, DCA- oder fremdsoftwareabhängige Programme.

Beim ersten Einlesen einer Bibliothek legt BusChord einen Metadatenindex unter
`%LOCALAPPDATA%\BusChord\cache\catalog-index` an. Der Index wird über SHA-256 an
die Katalogdatei gebunden, bei neuen oder geänderten Katalogen neu aufgebaut und
beim Entfernen verworfen; Suche und späterer Start lesen den Index,
Anwendungsprogramme werden erst beim Öffnen eines Geräts geladen. Bei einer
großen Bibliothek kann der erste Aufbau spürbar dauern; danach startet die
Bibliothek aus dem Index. Der Index
enthält nur abgeleitete Metadaten und ersetzt weder `.knxprod` noch Herstellerdaten.

Bekannte Fehlerfälle:

| Fall | Verhalten |
|---|---|
| Hersteller-ZIP um eine `.knxprod` | wird entpackt und als Katalog gelesen |
| Datei ist kein KNX-Katalog | Meldung mit Grund, übrige Importe laufen weiter |
| Datei liegt nicht lokal (kein Pfad) | „Lokale Datei erforderlich“ |
| Katalog verschachtelt zu tief | Abbruch nach vier Archiv-Ebenen |
| Bibliothek leer | Hinweis „kein Katalog geladen“ mit Import-Schaltfläche |

### 3.2 Herstellerkataloge beziehen

Neben dem lokalen Import öffnet **Herstellerkataloge beziehen** die Quellenliste.
Herstellerverzeichnis und Dateien werden erst auf ausdrückliche Aktion geladen.
Nach quellspezifischer Freigabe lassen sich ausgewählte oder alle freigegebenen
Direktquellen herunterladen und automatisch importieren. Fortschritt, Abbruch,
Duplikate und Konflikte stehen im Ergebnisbericht. Bestehende Kataloge bleiben
bei Identitätskonflikten erhalten. Ablauf, Quellenbedingungen und lokale Ablage:
[Herstellerkataloge beziehen](catalog-downloads.md).

### 3.3 Suchen und filtern

- Das Suchfeld durchsucht **Name, Bestellnummer, ID und Beschreibung** des
  Produkts sowie Hersteller, Katalogsnamen, Applikationstext,
  Kommunikationsobjekt-Namen und Datapoint-Typen. Datapoint-Typen sind als
  `DPST-1-1`, `1.001` und `DPT-1` auffindbar.
- Das Auswahlfeld daneben filtert nach **Rubrik** („Alle Rubriken“ oder ein
  gespeicherter Katalogpfad); Suche und Rubrik wirken zusammen.
- „Keine Treffer – Filter ändern“ setzt Suche und Rubrik zurück.
- Gesucht wird immer nur im ausgewählten Bibliothekseintrag, nicht über alle
  Hersteller gleichzeitig.

Rechts zeigt die Detailspalte Hersteller, Name, Beschreibung, Bestellnummer,
Applikation, Status-Badges (z. B. **Defekt**, **Downloadinfo unvollständig**,
**DCA / Plug-in**, **Fremdsoftware**), Datapoint-Typen und eine Vorschau der
Kommunikationsobjekte.

### 3.4 Gerät einfügen

1. Produkt in der Produktliste wählen.
2. **Ziel-Linie** wählen; hat die Linie Segmente, zusätzlich das **Ziel-Segment**.
3. **Adresse** prüfen: BusChord schlägt die niedrigste freie Adresse der Linie vor.
4. Optional **Name** ändern (Vorgabe: Produktname).
5. **Einfügen**.

Die Adresse muss zur gewählten Linie passen (Bereich und Linie identisch,
Gerätenummer 1–255) und projektweit eindeutig sein. Ein bereits vergebener Name
wird automatisch zu „Name (2)“, „Name (3)“ usw. ergänzt; die Statuszeile nennt die
Anpassung. Das Einfügen ist **ein** Undo-Schritt und übernimmt Segment und
Raumzuordnung mit.

Mögliche Sperren:

- „kein Produkt ausgewählt“
- „Ziel-Linie fehlt“
- „Ungültige physikalische Adresse“
- „Adresse x.y.z ist bereits vergeben“
- „Ziel-Linie ist voll“
- „Applikation ist als defekt markiert“
- „Downloadinformationen sind unvollständig“

Ohne Projekt sind Ziel-Linie, Segment, Adresse und Name gesperrt; der Katalog
bleibt aber durchsuchbar.

### 3.5 DCA- und Plug-in-Geräte

Ein Programm mit DCA-/Plug-in-Erweiterung wird im Katalog als **DCA / Plug-in**
gekennzeichnet. BusChord führt den Plug-in-Code nicht aus:

- **Einfügen bleibt möglich** (das Gerät und seine deklarierten Objekte werden
  übernommen).
- **Parameter sind schreibgeschützt**; der Editor nennt als Grund, dass das
  DCA/Plug-in hier nicht läuft.
- **Download wird nicht unterstützt.**
- Opake Herstellerdaten bleiben im Projekt erhalten und werden nicht neu
  geschrieben.

Defekte oder unvollständig deklarierte Applikationen sperren Einfügen und
Parameterbearbeitung. „Fremdsoftware erforderlich“ ist nur ein Hinweis und keine
Sperre. Fehlt der Katalog, prüft BusChord die Applikation im Projektarchiv; auch
dann bleiben DCA-, defekte und downloadunvollständige Geräte schreibgeschützt.

## 4. Gruppenadressen

Die Perspektive **Gruppen** zeigt die Gruppenbereiche und Adressen des Projekts.
Ein Klick öffnet die Gruppenadresse als Tab.

### 4.1 Anlegen und bearbeiten

- **Projekt → Neue Gruppenadresse** öffnet einen Dialog mit Adresse (Vorgabe:
  nächste freie Adresse ab 1/1/1), Name, Datentyp (Vorgabe `DPST-1-1`) und
  optionalem Raumbezug. **Anlegen** prüft: Adresse ungleich null und nicht
  vergeben, Name vorhanden, Datentyp von BusChord unterstützt. Fehlende
  Haupt-/Mittelbereiche werden angelegt.
- Im Inspektor lassen sich **Name** und **Datentyp** ändern; **Übernehmen**
  schreibt beides als Undo-Schritt. Anders als beim Anlegen prüft diese
  Bearbeitung das DPT-Format nicht.
- Die **Adresse selbst ist in der Oberfläche nicht editierbar**.
- Das Kontextmenü bietet **Umbenennen**, **Kopieren**, **Löschen**,
  **Daneben öffnen** und **Favorit**.
- **Löschen** fragt immer nach. Die Option „Vor dem Löschen nachfragen“ wird
  gespeichert, aber nicht ausgewertet; es gibt keinen Weg, die Rückfrage
  abzuschalten.

### 4.2 Zuordnungen

Der Inspektor listet die verknüpften Kommunikationsobjekte mit Gerät, Nummer und
Text. Ein Klick auf ein Gerät öffnet dessen Tab in der Darstellung
**Beziehungen**. Die Sendeadresse wird im Objektinspektor ausdrücklich gewählt
(Abschnitt 5).

### 4.3 Kein Senden oder Lesen in der Workbench

Die Workbench hat keine Schaltflächen zum Senden oder Lesen von Gruppenwerten.
Gruppenwerte können ausschließlich über das MCP-Werkzeug `bus_read` gelesen werden;
`bus_write` ist in dieser Fassung gesperrt und wird mit einem Fehler abgewiesen.
Das Wire-Verhalten von `bus_read` ist nicht gegen echte Hardware abgenommen
(Abschnitt 7.5).

## 5. Kommunikationsobjekte und Zuordnungen

Die Darstellung **Kommunikationsobjekte** zeigt Geräte, Herstellerkanäle und
Kommunikationsobjekte in einer gemeinsamen Liste mit DPT, Länge, Flags und
Gruppenadressen.

- **Kanäle** blendet Herstellerkanäle ein, **Ohne GA** filtert auf Objekte ohne
  Zuordnung, das Suchfeld filtert den aktuellen Tab.
- **Alle auf-/zuklappen** wirkt auf Geräte und Kanäle.
- Ein Objekt ohne belegte Definition zeigt `—` statt erfundener Werte; die
  Fußzeile nennt die Zahl der sichtbaren Objekte.

### 5.1 Mehrfach verknüpfen

1. Objekte in der Tabelle auswählen (Klick, `Strg`/`Shift` oder Checkboxen).
2. **Verknüpfen** öffnet den Dialog **Zuordnen · N Objekt(e)**. Die Auswahl wird
   beim Öffnen eingefroren; spätere Tab- oder Auswahländerungen ändern den Auftrag
   nicht mehr.
3. Gruppenadresse wählen. Die Vorschau nennt je Objekt **bereits verknüpft**,
   **bereit** oder **unverträglich**.
4. **Zuordnen** übernimmt alle verträglichen Zuordnungen als **einen** Undo-Schritt.
   Ist auch nur ein Objekt unverträglich, wird nichts teilweise übernommen.

Geprüft werden DPT, Bitlänge und eine ausdrücklich belegte Secure-Fähigkeit.
Unbekannte native Fähigkeiten werden mit Begründung abgelehnt statt geraten.
Bestehende Zuordnungen bleiben erhalten; Duplikate werden übersprungen.

### 5.2 Sendeadresse und Entfernen

- Der Objektinspektor listet alle Zuordnungen. **Als Sendeadresse** markiert eine
  vorhandene Zuordnung ausdrücklich; die Sendeadresse ist unabhängig vom T-Flag.
- **Entfernen** löscht eine Zuordnung. War sie die Sendeadresse, wird keine
  Ersatzadresse still gewählt; die Statuszeile warnt, wenn weitere Zuordnungen
  bleiben.
- **Weitere Zuordnungen …** öffnet den Mehrfachdialog für das eine Objekt.

### 5.3 Beziehungen und Prüfung

- **Beziehungen** zeigt je Objekt des aktiven Tabs die Zuordnungen mit
  Sendekennzeichnung und **Zuordnungen bearbeiten …**.
- **Prüfung** startet die Projektprüfung und listet die Befunde im Editor.

### 5.4 Intelligente Verknüpfung

Die Heuristik „Intelligente Verknüpfung“ ist in der Workbench nicht verdrahtet.
Sie steht nur als MCP-Werkzeug `smart_link_suggest` / `smart_link_apply` zur
Verfügung; Vorschläge sind dort vor dem Übernehmen zu prüfen.

## 6. Parameter

Die Darstellung **Parameter** zeigt die Geräteparameter der Herstellerapplikation
(KSXML). Bei mehreren Geräten im Tab wählt eine Auswahlliste das Gerät. Der Editor
verwendet projektbezogene Entwürfe mit einer Strukturvorschau:

- Editierbar sind Zahl, Fließkomma, Text, Aufzählung und Boolean. Alle anderen
  Typen (Datum, Uhrzeit, IP, Farbe, Bild, Rohdaten) sowie nicht unterstützte
  Deklarationen erscheinen als schreibgeschützte Zeilen mit Begründung.
- Unterstützte Bedingungen, Zuweisungen und Strukturprofile aktualisieren die
  sichtbaren Parameter und die Vorschau der Kommunikationsobjekte. Die Vorschau
  zeigt hinzugefügte, entfernte sowie aktivierte oder deaktivierte Objekte.
  Nicht unterstützte Deklarationen und benötigte Laufzeitfähigkeiten werden
  ausdrücklich genannt; ein beliebiges Herstellerprogramm wird nicht ausgeführt.
- Änderungen bleiben zunächst **Entwürfe** und sind an das Gerät gebunden. Die
  Fußzeile nennt offene Entwürfe und Validierungsfehler; **Standardwerte** setzt
  sichtbare editierbare Felder auf den deklarierten Default zurück,
  **Verwerfen** verwirft die Entwürfe.
- **Änderungen prüfen** zeigt eine Vorschau mit Vorher/Nachher und den Grenzen;
  **Vorschau schließen** beendet die Prüfung ohne Änderung.
- **Übernehmen** validiert alle sichtbaren editierbaren Werte (Ganzzahl-/
  Fließkommabereiche, Bitbreite, Aufzählungswerte, konservative Textgröße) und
  schreibt in einem Undo-Schritt ins Projekt. Bei Fehlern wird nichts teilweise
  übernommen.
- **Projekt speichern** übernimmt keine offenen Entwürfe; die Statuszeile weist
  darauf hin. Erst **Übernehmen**, dann speichern.

### 6.1 BusChord-Overrides

Geänderte Werte speichert BusChord je Gerät unter der Referenz-ID des Parameters im
eigenen XML-Namensraum `urn:buschord:project:1`
(`ParameterInstanceRefs/ParameterInstanceRef`). Beim erneuten Öffnen werden sie auf
die frisch gelesenen Parameter angewendet.

**Nicht verifiziert:** dass ETS diese Overrides als Geräteparameter liest. Die
Ablage ist ein BusChord-Vertrag, keine belegte ETS-Parameterinteroperabilität;
native ETS-Parameterknoten bleiben unverändert opaque erhalten. Ein Parametertausch
mit geänderter Applikation braucht zusätzlich eine geprüfte Objekt- und
Parametermigration.

### 6.2 Demo-Applikationen

Das eingebaute Demo-Projekt verwendet eine synthetische Applikation. Deren
Parameterformular zeigt eine reine Wirkungsvorschau (Schalten/Dimmen) und ändert
nur Demo-Parameter; bestehende Zuordnungen bleiben erhalten. Das ist kein
Herstellerverhalten und keine Geräteprogrammierung.

### 6.3 DCA- und opake Applikationen

Läuft die Applikation über ein DCA/Plug-in oder enthält sie nicht darstellbare
Herstellerdaten, zeigt der Editor die Werte **schreibgeschützt** und nennt den
Grund. **Übernehmen** bleibt deaktiviert, Werte werden nicht verändert oder neu
geschrieben. Der Grund stammt aus dem geladenen Katalog oder, wenn dieser fehlt,
aus der Applikation im Projektarchiv; ohne beide Quellen kann die Ansicht leer
bleiben. Ein DCA-/Plug-in-Host wird nicht gestartet, auch nicht bei vorhandenem
Katalog.

## 7. Werkzeuge und Sitzungen

Die Werkzeugleiste unten enthält sechs Werkzeuge. Ausblenden beendet eine Sitzung
nicht; die Sitzungen gehören zum Projekt. Über die Symbole rechts lassen sich ein
Werkzeug in einem eigenen Fenster öffnen, maximieren oder ausblenden. Das
Auslagern erzeugt eine frische Ansicht; Schließen des Fensters dockt eine frische
Ansicht wieder an. `Strg+J` blendet den Werkzeugbereich ein oder aus.

### 7.1 Monitor

Der Monitor bietet die Quellen **Demo** und **Echter Bus**. Für echte Telegramme
zunächst **Werkzeuge → Busverbindung** oder **Verbindung wählen…** öffnen,
die Verbindungsart wählen. Für UDP Gateway-IPv4, UDP-Port und angeforderte
Tunneladresse eingeben. Für IP Secure UDP den geschützten Keyring lokal prüfen,
Gateway-KNX-Adresse, Benutzer und Tunneladresse daraus auswählen und das Kennwort
mit **Passwort eingeben und prüfen** im maskierten Dialog eingeben oder ausdrücklich
den passenden gespeicherten Eintrag verwenden. **Verbinden** stellt die gewählte Verbindung her.
Für bereits provisionierte Data-Secure-Gruppen zusätzlich **Data Secure Gruppen**
öffnen, eine private Gruppensicherheitsdatei und darin ein Profil auswählen.
Fingerabdruck, Installation, Schlüsselbereich, Zählerquelle, Gruppen und Sender
werden vor der Übernahme angezeigt; die Kandidaten sind zunächst ungeprüft.
Die vorhandene geprüfte Passwortquelle bleibt erhalten. Die Datei richtet keine
Zähler oder Geräteschlüssel ein. Ohne unabhängigen Anker muss der aktuelle
Zählerstand für jeden neuen Verbindungsauftrag ausdrücklich bestätigt werden.
Bei erneutem Verbinden, Scan oder M112 genügt die kurze erneute Bestätigung der
gleichen Auswahl; Dateiänderungen erfordern eine neue Auswahl. Unzulässige Ziele
bleiben gesperrt. **Gruppenprofil entfernen** ist eine ausdrückliche Entscheidung.
Die Monitorspalte **Herkunft / Datensicherheit** nennt die tatsächlich geprüfte
eingehende Nachricht; Gateway-Anmeldung, Demo und importierte Aufzeichnungen
erhalten dadurch keine Bestätigung. Details: [Gruppenprofile](group-secure-connections.md).
Die Anzeige nennt den Verbindungszustand und die tatsächlich zugewiesene
Adresse. Schließen des Dialogs erhält eine erfolgreiche Verbindung; **Trennen**
beendet sie. Abbrechen während des Aufbaus wartet dessen Beendigung ab.

Im Keyringdialog liest **Datei lesen** zunächst ungeprüfte Zuordnungskandidaten und
den vollständigen Dateihash. Erst die Kennwort-/Signaturprüfung gibt die Auswahl
frei. Bei einem unter vollständigem Pfad gespeicherten Projekt prüft **Ablage prüfen**
die OS-Verfügbarkeit, ohne einen Eintrag zu lesen. **Gespeichertes Passwort verwenden**
liest ausschließlich die genaue Projekt-/Datei-/Zuordnung und prüft den Export erneut.
**Neu speichern…** und **Passend ersetzen…** sind getrennte ausdrückliche Aktionen
mit eigenem maskiertem Bestätigungsdialog; vor einer Änderung müssen Signatur und
Dateipin passen. Speichern wählt die gespeicherte Quelle nicht automatisch aus.
**Gespeichertes Passwort löschen** betrifft nur diesen passenden OS-Eintrag.

Windows nutzt die benutzergebundene Anmeldeinformationsverwaltung; Linux benötigt
libsecret und einen konfigurierten Secret Service. Gesperrte oder fehlende Ablagen
werden angezeigt und nicht automatisch entsperrt. Die manuelle Kennwortabfrage
bleibt verfügbar. Nicht gespeicherte Projekte erhalten keine implizite Ablagekennung.
Geänderte Projektpfade, Exporte oder Zuordnungen benötigen eine neue bewusste Auswahl.
Bei unbekanntem Ausgang einer Ablageänderung wird nichts automatisch wiederholt oder
rückgängig gemacht. [Vertrag und Grenzen](credential-vault.md).

- **Starten**, **Stoppen**, **Pause**, **Fortsetzen**, **Anzeige leeren**.
- **Monitorart**: Gruppenmonitor oder Busmonitor; der Wechsel ist nur im
  gestoppten Zustand möglich. Die echten UDP-Verbindungen unterstützen hier den
  Gruppenmonitor; ein vollständiger Hardware-Busmonitor ist nicht enthalten.
- **Telegrammfilter**: Alle Telegramme, Ganzes Projekt, Aktueller
  Arbeitsgegenstand, Auswahl folgen.
- **Wert** bleibt ein Demo-Vorgang; echte Lese-/Schreibaktionen aus diesem Dialog
  sind gesperrt.
- **CSV Filter** / **CSV alle** exportieren die gefilterte Ansicht bzw. die
  vollständige Aufzeichnung.
- Die Anzeige hält die letzten 500 Zeilen; die vollständige Aufzeichnung liegt
  getrennt davon und wird beim Stoppen nicht gelöscht.
- **Beobachten** im Editor öffnet den Monitor und pinnt den Filter auf den
  aktuellen Raum, das Gerät oder die Gruppenadresse.

Ohne Projekt meldet der Monitor „Zuerst ein Projekt öffnen“.

**Telegrammhistorie** im Werkzeugmenü öffnet dauerhaft erfasste lokale Daten auch
ohne Projekt. Zeit-/Mehrwertfilter, Kontext, Aufnahmeabdeckung, Export und
Löschgrenzen sind im [Historienablauf](workflows.md#dauerhafte-telegrammhistorie)
beschrieben. Dauerhafte Aufnahme wird ausdrücklich eingeschaltet.

### 7.2 Programmierung

Die standardmäßig gewählte Ansicht **Simulation** meldet: „SIMULATION · kein realer
Download und keine Geräteprogrammierung“.

- Gerät wählen und **Auftrag einreihen**; dabei wird der Gerätestand eingefroren.
- **Ausführen** startet die Demo-Ausführung; die Option **Demo-Fehler** simuliert
  einen Fehlschlag, **Fehlgeschlagene wiederholen** reiht sie erneut ein.
- **Auftrag abbrechen**, **Wartende abbrechen** und **Verlauf leeren** helfen beim
  Aufräumen; die Anzeige nennt Geräte mit Programmierbedarf gegenüber dem zuletzt
  simulierten Erfolgsstand.
- Ein echter Download ist in dieser allgemeinen Warteschlange nicht verdrahtet; ein Auftrag mit Ursprung `Real` wird
  ohne Executor abgelehnt. Der separate M112-Dialog unten hat einen eigenen
  geprüften Ablauf und erzeugt keine allgemeinen Erfolgsflags für Demo-Aufträge.
- Die read-only **Download-Vorprüfung** ist kein Workbench-Befehl, sondern das
  MCP-Werkzeug `download_plan`. Es prüft Descriptor/Maske, Programmpaarung,
  Parameter-/Link-Vollständigkeit und Adress-/Linienbezug, berechnet die
  Image-Chunks und den druckbaren Dry-Run, sendet aber kein Programmiertelegramm.
  Da Programmiermodus und Vor-Ort-Bestätigungen nicht abfragbar sind, bleibt der
  Plan immer offen; `writesEnabled` ist `false` und ein echter Download ist
  gesperrt.

Für das Teilprofil MV-0701/M112 gibt es **Werkzeuge → M112-Änderung prüfen**:

1. Projektgerät wählen, private Ausgangsprojektkopie und Journalordner auswählen.
   **Offline prüfen** zeigt den gebundenen Parameter-/Tabellenplan und öffnet keine
   Verbindung. Applikation und Speichergeometrie müssen übereinstimmen.
2. **Gerät lesen** beendet die Monitorverbindung vollständig. Physische Identität
   vergleichen und ausdrücklich bestätigen, den unveränderlichen Plan prüfen und
   die Zieladresse eingeben. Der Fingerprint ist kopierbar und muss nicht abgetippt werden.
3. **Bestätigten Plan ausführen** lädt dieses Profil. Abbrechen und Schließen warten
   eine notwendige unabhängige Rücksicherung ab. Ergebnis und Recovery-Pfad bleiben sichtbar.
4. Nach einem Programmneustart lässt sich ohne Projekt oder Geräteauswahl dieselbe
   Funktion im Wiederherstellungsmodus öffnen. Eine gewählte Sicherung wird vorab
   geprüft; die physische Bestätigung benötigt keine laufende Geräteapplikation.
   Eine fehlgeschlagene Wiederherstellung muss vor dem Verlassen eigens bestätigt werden.

Umfang, Konsolenbefehle und Gerätebelege stehen in [Inbetriebnahme](commissioning.md).

Für mehrere unterstützte Geräte im Programmierbereich **Echte M112-Pläne** wählen:

1. Zielprojekt als private Kopie speichern und **M112-Warteschlange öffnen** wählen.
   Verbindung ausdrücklich auswählen, Ausgangsprojekt und Journalordner angeben,
   dann die Geräte einzeln vorbereiten. Die Vorbereitung sendet nichts.
2. Für jeden Auftrag **Gerät lesen**, physische Identität und Plan prüfen,
   Zieladresse eintippen und den einzelnen Auftrag bestätigen.
3. Ausstehende Geräte und Batch-Fingerabdruck prüfen und den gesamten Lauf
   ausdrücklich bestätigen. Die Geräte laufen nacheinander; Fehler oder Abbruch
   stoppen weitere Aufträge und warten eine notwendige Wiederherstellung ab.

Nur vollständig geprüfte Aufträge erhalten echte Prg-/Par-/Grp-Belege. Diese
beschreiben eine frühere Prüfung und keinen aktuellen Gerätezustand. Ungültige
Quellen oder Belege stellen keine alten Flags wieder her. Adressierung,
Erstinbetriebnahme und allgemeine Herstellerprofile sind damit nicht freigegeben.
[Ablauf, Wiederanlauf und Grenzen](real-programming-queue.md).

### 7.3 Scan

Der Linienscan bietet **Demo** und **Echter Bus**. Linie und Quelle wählen, dann
**Linie scannen**. Vor einem echten Scan muss die Monitorverbindung getrennt
werden; der Scan reserviert das gewählte Gateway bis zum vollständigen Abschluss.
Er fragt die Adressen der gewählten Linie einzeln lesend ab und zeigt bestätigte
Antworten mit Maske. **Unbekannt** bedeutet keine bestätigte Antwort,
**Nicht geprüft** eine noch nicht abgefragte Adresse; beides ist kein Beweis,
dass ein Gerät fehlt. **Abbrechen** erhält Teilergebnisse. Ein Demo-Scan zeigt
weiterhin ausschließlich simulierte Ergebnisse projektierter Geräte.

### 7.4 Änderungen und Prüfmeldungen

- **Änderungen** listet die Änderungen dieser Sitzung mit Zeit und Beschreibung
  (höchstens 500 Einträge). **Anzeige leeren** löscht die Liste, nicht die
  Undo-Historie.
- **Prüfmeldungen** startet die Projektprüfung und zeigt Fehler, Warnungen und
  Hinweise. Der Befundzähler an der Aktionsleiste zeigt die Zahl der Befunde.

### 7.5 Bus, Secure und was live belegt ist

- **Werkzeuge → Busverbindung** öffnet den ausdrücklich gewählten UDP- oder
  IP-Secure-Tunnel für den lesenden Monitor. Die Angaben gelten für die aktuelle
  Projektinstanz; ein Projektwechsel schließt die alte Verbindung vollständig.
  Die alten Felder unter **Optionen → Bus** öffnen keine Verbindung automatisch.
- IP Secure authentisiert das Gateway. Geschützte Gruppenwerte benötigen einen
  zusätzlich provisionierten Data-Secure-Kontext. Die Verbindungswahl sendet
  bekannte geschützte Gruppen nicht ersatzweise unverschlüsselt;
  [Umfang und Auswahl](secure-connections.md).
- **Werkzeuge → Secure-Übersicht** zählt Geräte und Gruppenadressen mit
  ausdrücklich belegter Secure-Fähigkeit. Die Übersicht verwaltet keine Schlüssel
  oder Zertifikate und behauptet keine sichere Inbetriebnahme.
- **Live verifiziert** sind Discovery, Routing und der Tunnel-Handshake der
  Busbibliothek gegen einen realen KNXnet/IP-Router (2026-10-18): gezieltes
  SEARCH/DESCRIPTION, ein Tunnel-CONNECT, genau ein Gruppenwert-Lesen auf `0/0/1`
  mit Tunnelling-ACK und `L_Data.con`, danach ein sauberes DISCONNECT. Es wurden
  keine Gruppenwerte geschrieben und keine Geräte programmiert. Details:
  [Router-Discovery](status.md).
- Ein gezielter M112-Geräteversuch hat Identifikation, lesende Koppler-/Geräte-
  Abfrage, vollständiges Wiederholungsladen und eine tatsächliche Parameteränderung
  mit anschließend unabhängig bestätigter Wiederherstellung belegt. Umfang und
  Grenzen stehen in [Inbetriebnahme](commissioning.md). Allgemeine Downloads,
  Secure-Tunnel und längere Workbench-Sitzungen gegen verschiedene Geräte bleiben
  offen. `bus_write` ist gesperrt; `download_plan` sendet kein Programmiertelegramm.
- **Werkzeuge → Aufzeichnung exportieren …** greift auf die alte
  Recorder-Aufzeichnung zurück. Die Workbench-Monitoraufzeichnung wird über
  **CSV Filter** / **CSV alle** exportiert.

## 8. Projektpflege

### 8.1 Speichern und Sicherungen

Speichern schreibt zuerst eine exklusive temporäre Datei neben das Ziel, prüft sie
durch erneutes Laden und ersetzt erst dann die Zieldatei (bei vorhandenem Ziel per
`File.Replace`, sonst per `File.Move`). Schlägt das Schreiben oder die Prüfung
fehl, bleibt die vorhandene Datei unverändert.

Vor jedem Überschreiben legt BusChord eine lokale Sicherung an.
**Projekt → Backup jetzt erstellen** sichert den aktuellen Stand zusätzlich;
**Projekt → Sicherungen …** listet die Sicherungen und schreibt eine gewählte
Sicherung in eine neu gewählte Datei. Ablage und Retention stehen in
[project-backup.md](project-backup.md).

### 8.2 Was erhalten bleibt

- Gelesene Archivteile und unbekannte XML-Elemente, Attribute, Texte und
  Kommentare bleiben erhalten (Drei-Wege-Zusammenführung aus Original, Modell und
  aktuellem Stand); bewusst gelöschte Modellobjekte nehmen ihren opaken Teil mit.
- IDs, Kommentare, Installationstyp, Kanalzustand und die MasterData-Signatur
  werden mitgeführt.
- Verknüpfungen, Geräteparameter-Overrides und BusChord-Zusatzattribute
  (`ComObjectId`, `Number`) liegen im Namensraum `urn:buschord:project:1`.

### 8.3 Signaturen

Signaturen im Projekt und in den MasterData werden **erhalten, aber nicht neu
berechnet**. Nach Änderungen an signierten Daten kann die Signaturprüfung in ETS
daher fehlschlagen. XML-Formatierung und ZIP-Komprimierung werden nicht garantiert
beibehalten.

### 8.4 Verschlüsselte, fremde und ältere Projekte

- Unterstützte `/20`-, `/21`-, `/22`- und `/23`-Projekte lassen sich lesen,
  bearbeiten und speichern. Das unterstützte Root-, ZIP- oder Projektordnerlayout
  bleibt erhalten; `/20` verwendet direkte Linien, `/21` und `/22` Segmente.
- **Geschützte Archive** benötigen das Projektpasswort und ein passendes
  Schutzprofil. Die Speicherung erhält die vorhandene AES-Verschlüsselung.
  Das eingebaute Standardprofil kann durch die globalen lokalen
  Archiveinstellungen überschrieben werden; Details stehen unter
  [Status](status.md#project-and-archive-support).
- Abgelehnt werden unter anderem ZIP64, Mehrteil-Archive, doppelte
  Eintragsnamen, mehrdeutige Wurzel- und verschachtelte Projekte sowie
  Übergrößen. Gesonderte Grenzen gelten für kanonische Applikationsdateien;
  die aktuellen Größen- und XML-Grenzen stehen im
  [Status](status.md#project-and-archive-support).

### 8.5 Undo/Redo und Zuletzt-Liste

- **Rückgängig** (`Strg+Z`) und **Wiederherstellen** (`Strg+Shift+Z` oder
  `Strg+Y`) arbeiten auf Projekt-Snapshots; die Oberfläche hält bis zu **60**
  Zustände, der MCP-Server 100. Ein neues oder neu geladenes Projekt setzt die
  Historie zurück.
- Speichern selbst erzeugt keinen zusätzlichen Undo-Schritt.
- Die Zuletzt-Liste führt maximal zehn Projekte; fehlende Dateien erscheinen nicht.

Optionale [lokale Checkpoints](recovery.md) können eine geprüfte Projektsitzung
einschließlich Undo/Redo nach einem Neustart wiederherstellen. Private Entwürfe
und Fensterlayout gehören nicht zu diesem Format.

### 8.6 Demo-Projekt als JSON

**Projekt → Demo-Projekt · JSON exportieren …** schreibt das separate
Austauschformat `knx-studio-demo`. Der Export verweigert native oder nicht
darstellbare Herstellerdaten, statt sie zu verwerfen. **Demo-Projekt · JSON
importieren …** validiert die Datei vollständig vor dem Laden.
Der Umschlag bleibt Version 4. Das bisherige Datenprofil `schemaVersion: 1`
bleibt für Projekte ohne GUID erhalten. Das neue Profil 2 erhält `project.guid`
einschließlich seiner Schreibweise; ältere Reader nur für Profil 1 lehnen es ab.
**Demo zurücksetzen …** verwirft den aktuellen Demo-Stand nach Bestätigung.

### 8.7 Projektstände und Revisionen

Ein Projekt kann im BusChord-Namensraum `urn:buschord:project:1` einen dauerhaften
Revisionsstand führen. Jeder Eintrag hat eine fortlaufende Nummer (`REV-0001`),
Zeitstempel, optionalen Quellpfad und -Hash, einen kanonischen SHA-256-Inhalt,
eine Beschreibung sowie den Signaturstatus `Absent` oder `Unverified`. Der
Inhalts-Hash ist unabhängig von ZIP-Verpackung und Dateireihenfolge; das
Revisionsprotokoll selbst fließt nicht in ihn ein. Signaturen werden erhalten,
aber weder neu berechnet noch geprüft.

Der Projektbericht zeigt den letzten Stand und, wenn Einträge vorhanden sind, die
vollständige Revisionstabelle. Das Übergabepaket schreibt die Liste als
`15_projektverlauf.csv`; seine README nennt fehlende Revisionen ausdrücklich.
Die Workbench erfasst vor dem normalen Speichern eine Revision im Projektarchiv.
Der MCP-Server ergänzt beim Speichern keine automatische Revision. Dateirevisionen,
der Dateizähler im Projektportfolio (Abschnitt 2.1) und die separate lokale
[Sitzungswiederherstellung](recovery.md) haben jeweils einen eigenen Umfang.

## 9. Prüfen, Exporte, Berichte, Vergleich

### 9.1 Projektprüfung

**Werkzeuge → Projektprüfung**, der Befundzähler in der Aktionsleiste oder die
Darstellung **Prüfung** starten die Prüfung. Der Bericht nennt Fehler, Warnungen
und Hinweise (Codes P001–P082, nicht lückenlos):

- fehlende Installation, Bereichs-/Linienadressen außerhalb 0–15, mehrere Segmente
  je Linie, doppelte Segmentadressen,
- Geräteadresse 0, fehlende Produktreferenz, fehlender Name, fehlende
  Kommunikationsobjekte, doppelte Geräteadressen,
- fehlender Datapoint-Typ, fehlender Name, doppelte Gruppenadressen, Adresse
  außerhalb des Bereichs,
- unverknüpfte Kommunikationsobjekte, Verknüpfungen auf nicht existierende
  Gruppenadressen, unverknüpfte Gruppenadressen,
- Gebäudeverweise auf fehlende Geräte, Arbeitsbereichs-Metadaten auf unbekannte
  Geräte/Objekte/Gruppenadressen,
- Applikationsprogramme, die im geladenen Katalog fehlen, als defekt markiert sind
  oder unvollständige Downloadinformationen haben,
- **Busstromplanung (P080):** Die Geräte einer Linie ziehen mehr Strom, als die
  deklarierte Spannungsversorgung liefert. Geprüft wird nur, wenn mindestens eine
  Versorgung und für alle Geräte ein `BusCurrent` im Projekt-Hardwaremodell
  vorliegen; fehlende Daten erzeugen keinen Befund und keine erfundene Grenze.
- **Segment-/Kopplerplanung (P081, P082):** Ein Untersegment ohne Linien- oder
  Segmentkoppler wird als Warnung gemeldet, ein Medienwechsel innerhalb der Linie
  ohne Medienkoppler als Hinweis. Das Hauptsegment ist das Segment mit der
  niedrigsten Nummer; ein Koppler wird über sein Hardware-Merkmal erkannt. P081
  schweigt, solange die Hardwaredaten des Segments unvollständig sind; P082 stützt
  sich nur auf die deklarierten Medien von Linie und Segment.
- Hinweise zu IP-Medien und Kopplern, deren Modellierung fehlt.

Die Prüfung ist strukturell; sie ersetzt keine Geräte- oder Applikationsprüfung.
Eine CSV-Ausgabe der Befunde gibt es in der Workbench nicht; der MCP-Befehl
`project_check` liefert sie als JSON.

### 9.2 Exporte

**Projekt** enthält die Exporte als einzelne Menüpunkte:

| Menüpunkt | Datei |
|---|---|
| Gruppenadressen · CSV … | `gruppenadressen.csv` |
| Geräte · CSV … | `geraete.csv` |
| Topologie · CSV … | `topologie.csv` |
| Projektbericht · HTML … | `projektbericht.html` |
| Objektzuordnungen · CSV … | `zuordnungen.csv` |
| Etiketten · HTML … | `etiketten.html` |
| Semantik · Turtle … | `semantik.ttl` |
| Semantik · JSON-LD … | `semantik.jsonld` |

Der Turtle-Export bettet die KNX-Ontologie ein, wenn
`C:\Program Files (x86)\ETS6\ontology.ttl` vorhanden ist; die Statuszeile weist
darauf hin. Der JSON-LD-Export ist davon unabhängig. Die Workbench exportiert
Etiketten nur als HTML; eine CSV-Ausgabe und eine kombinierte HTML-Seite aus
Gruppenadress- und Geräteetiketten bietet der MCP-Befehl `export_labels`.

### 9.3 Projektvergleich

**Projekt → Projekt vergleichen …** setzt ein geladenes Projekt voraus, lädt ein
zweites `.knxproj` und zeigt hinzugefügte, entfernte und geänderte Objekte mit
Pfad, Typ, Name und neuem Wert. Verglichen werden Projekt- und
Installationsdaten, Geräte einschließlich Parameterwerten, Gruppenadressen,
Verknüpfungen mit Sende-/Ack-/Prioritäts-/Slot-Merkmalen, BusChord-Metadaten und
der Gebäudebaum. Die Reihenfolge ist deterministisch (Pfad, Typ, Name, Änderung,
Wert). Die Statuszeile fasst „+ / − / ~“ zusammen; das Fenster kann die Liste als
CSV mit Pfad, Typ, Name, Änderungsart sowie altem und neuem Wert speichern. Der
Vergleich ändert kein Projekt und prüft keine Signaturen; geänderte Signaturen
werden als „erhalten, nicht neu berechnet oder geprüft“ ausgewiesen.

### 9.4 Produkt austauschen

**Projekt → Produkt ersetzen …** stellt alle Geräte einer Produkt-RefId auf eine
andere um, optional mit neuer Applikation (Hardware2Program-RefId).

1. Alte RefId (bei ausgewähltem Gerät vorbelegt) und neue RefId eingeben.
2. **Vorschau** – erst danach wird **Austauschen** aktiv.
3. Die Vorschau listet betroffene Geräte und die Zahl der erhaltenen
   Verknüpfungen; der Austausch ist ein Undo-Schritt.

Der Austausch ersetzt keine fehlende Objekt-/Parametermigration: Passen
Kommunikationsobjekte oder Parameter nicht zusammen, bleibt die fachliche Prüfung
beim Anwender.

### 9.5 Gruppenadressen importieren

Die Workbench hat keinen CSV-Import für Gruppenadressen. Der MCP-Befehl
`group_address_import_csv` liest `1/2/3;Name;DPT;Beschreibung` und meldet nicht
lesbare Zeilen; vorhandene Adressen werden übersprungen.

### 9.6 Abnahmeprotokoll

Das KNX-Abnahmeprotokoll wird ausschließlich über das MCP-Werkzeug
`acceptance_protocol_export` erzeugt; die Workbench hat dafür keinen Menüpunkt.
HTML enthält Projektangaben mit Dokumentversion, SHA-256-Dokumenthash und
MasterData-Signaturstatus, Topologie und Geräteliste, Gruppenadressen mit
Datentypen, Funktionsmatrix, Geräte-Testzeilen, offene Punkte mit leerer
Mängelliste und einen de/en-Unterschriftsblock. `format=csv` schreibt nur das
Geräte-Testblatt. Alle Prüfspalten starten als **nicht geprüft**, Messwert und
Bemerkung bleiben leere Eingabefelder. Die Download-Spalte zeigt nur einen
aufgezeichneten Gerätestand aus einer JSON-Datei im `DeviceProgrammingStates`-Format:
ohne Datei **kein Download aufgezeichnet**, Demo als **Simulation (Demo)** und
nur ein erfolgreicher Stand mit Ursprung `real` als **real geladen** bzw.
**real teilgeladen**, je nach Ladeflags. Die Workbench hat derzeit keinen
Menübefehl, um diese Zustandsdatei zu exportieren; sie entsteht über
`ProgrammingSession.ExportDownloadedJson`. Das Werkzeug ist read-only: kein
Buszugriff, keine Projektänderung, kein Download. Es setzt den aktuellen
Erzeugungszeitpunkt ein;
der Dokumenthash ändert sich daher zwischen Aufrufen. Nur ein fester Zeitstempel
über die Core-API erzeugt ein byte-identisches Dokument.

### 9.7 Übergabepaket

`handover_package_export` schreibt ein deterministisches ZIP für das geöffnete
Projekt oder eine nur gelesene `.knxproj`. Enthalten sind Projektbericht,
Gruppenadress-, Geräte- und Topologie-CSV, Funktionsmatrix, Raum- und
Verknüpfungsbuch, Etiketten, Turtle-Export, Abnahmeprotokoll, Parameternachweis,
Vorlagenseiten für Betrieb, Wartung/Kontakte und Datenschutz, die
Revisionsliste, eine README und `00_MANIFEST.sha256` mit einer SHA-256-Zeile je
anderer Datei. `language` (`de`/`en`) und ein festes `generated_at` machen die
Wiederholung byte-identisch; ohne Zeitstempel wird die aktuelle Zeit verwendet.
Ein State-File eines fremden Projekts wird abgewiesen. Das Werkzeug ersetzt kein
geöffnetes Projekt, schreibt die `.knxproj` nicht und legt nur das ZIP an. Das
Paket enthält keine Keyrings, Kataloge, Herstellerdateien oder absoluten
Quellpfade; fehlende Revisionen, Gebäudestruktur, Zustände und Demo-Ladungen
werden ausdrücklich benannt. Details:
[MCP-Server](../mcp.md).

## 10. Tastenkürzel

In der Workbench verdrahtet:

| Kürzel | Wirkung |
|---|---|
| `Strg+B` | Navigator ein-/ausblenden |
| `Strg+J` | Werkzeugbereich ein-/ausblenden |
| `Strg+K` | Befehlssuche |
| `Strg+Shift+G` | aktive Gruppenadresse im Vergleichs-Nebenfenster öffnen |
| `Strg+S` | Projekt speichern |
| `Strg+O` | Projekt öffnen |
| `Strg+N` | Neues Projekt |
| `Strg+Tab` | nächster Arbeitsgegenstands-Tab |
| `Strg+Z` / `Strg+Shift+Z`, `Strg+Y` | Projekt rückgängig / wiederherstellen |
| `F1` | Hilfe |
| `Shift+F10` / Menütaste im Navigator | Kontextmenü des fokussierten Eintrags öffnen; `Escape` gibt den Zeilenfokus zurück |
| `Escape` | Inspektor oder Dialog schließen |
| `→` / `←` | Knoten bzw. Tabellenzeile auf-/zuklappen |
| `Enter` / `Leertaste` | Tab-Darstellung oder Werkzeug aktivieren |

Textfelder behalten ihr lokales Undo; `Strg+Z` im Editor ändert nicht zusätzlich das
Projekt. `Strg+G` (ohne Shift), `F2` und `F5` sind in der Workbench **nicht**
belegt; belegt ist `Strg+Shift+G`.

## 11. Grenzen und Sicherheit

- **Projektänderungen senden nichts.** Echte Workbench-Verbindung, Monitor und
  Scan werden ausdrücklich gewählt und arbeiten lesend. Programmieraufträge
  bleiben in der allgemeinen Ansicht simuliert; die separate M112-Warteschlange und der M112-Dialog
  erfordert einen geprüften Plan und ausdrückliche Gerätebestätigung. `bus_write` ist gesperrt.
- **Allgemeine Downloads und Adressschreibungen bleiben gesperrt.** Der Schreibpfad ist
  deaktiviert (`WritesEnabled = false`), `bus_write` wird abgewiesen, ein realer
  Programmierauftrag hat keinen Executor, und `download_plan` sendet kein
  Programmiertelegramm. Der Plan ist immer offen, weil Programmiermodus und
  Vor-Ort-Bestätigungen nicht abfragbar sind. Der separate, explizit bestätigte
  [M112-Konsolenpfad](commissioning.md) besitzt ein enges Lade-/Änderungsprofil
  mit dauerhafter Konfigurationssicherung und Geräteprüfungen.
- **Keine Firewall- oder Netzwerkänderungen.** BusChord öffnet UDP-Sockets für
  Discovery/Tunnel; es ändert keine System- oder Routerkonfiguration.
- **Keine Passwortumgehung.** Verschlüsselte Projekte werden nur mit dem
  Projektpasswort gelesen und mit passendem Schutzprofil gespeichert.
- **Keine automatische Data-Secure-Provisionierung.** Die Bibliothek besitzt optionale
  Gruppen- und Individual-/Tool-Prozessoren mit expliziten Schlüssel-/Zählerverträgen.
  Die Workbench-Verbindungswahl provisioniert sie nicht automatisch; die
  Secure-Übersicht verwaltet keine Schlüssel. Die optionale OS-Ablage speichert nur
  das ausdrücklich gewählte Keyring-Passwort und ersetzt keine sichere Inbetriebnahme.
- **Kein DCA-Host.** DCA/Plug-in-Code wird nicht ausgeführt; Parameter bleiben
  lesbar, Download ist gesperrt. Die Sperre greift auch über die Applikation im
  Projektarchiv, wenn der Katalog fehlt.
- **Ein Projekt, keine parallelen Projekt-Tabs.** Das Portfolio listet mehrere
  Dateien, öffnet aber immer nur eine; Öffnen ersetzt das aktuelle Projekt.
- **Deutsch, English, Französisch, Italienisch, Spanisch, Niederländisch,
  Chinesisch (vereinfacht) und Polnisch.**
  Berichtsspezifische Tabellen und MCP-Exporte bleiben vorerst Deutsch/Englisch.
  RTL wird nicht unterstützt.
- **Feste Anzeigegrenzen.** Der Monitor zeigt die letzten 500 Zeilen; die
  vollständige Aufzeichnung liegt in einer temporären Datei und wird beim Beenden
  gelöscht, sofern sie nicht vorher als CSV exportiert wurde. Die Änderungsliste
  hält 500 Einträge, die Workbench 60 Undo-Schritte, der MCP-Server 100.
- **Kein Revisions-Menü.** Revisionen werden gelesen und in Bericht/Übergabe
  ausgegeben, aber weder die Workbench noch der MCP-Server legen neue an
  (Abschnitt 8.7).
- **Katalogindex ist abgeleitet.** Der erste Aufbau einer großen Bibliothek kann
  dauern; der Index unter `%LOCALAPPDATA%` ist kein Ersatz für die `.knxprod` und
  wird bei Änderungen neu geprüft.
- **Kein ETS-Roundtrip-Nachweis.** Das Schreiben wurde mit synthetischen Archiven
  und eigenen Prüfungen getestet, nicht durch Wiederöffnen in ETS abgenommen.
  Signaturprüfungen in ETS können nach Änderungen fehlschlagen.
- **Keine vollständige ETS-Parität.** Es fehlen unter anderem vollständige
  dynamische Herstellerlogik, Plug-ins/DCA-Ausführung, vollständige Geräteimages,
  Secure-Inbetriebnahme, USB/RDAC/Cloud-Anbindung und mehrere gleichzeitig
  geöffnete Projekte.
- **Busadressen sind 4-Bit-begrenzt.** Bereich und Linie 0–15, Gerätenummer
  1–255; ungültige Eingaben werden abgelehnt statt umgerechnet.
- **DPT-Prüfung ist keine Gerätekonformität.** Sie prüft Typ, Bitlänge und
  belegte Secure-Fähigkeit, nicht Produktverträglichkeit oder sichere
  Inbetriebnahme.
- **Die Projektprüfung ist strukturell**, keine Inbetriebnahme- oder
  Gerätediagnose.
- **„Vor dem Löschen nachfragen“ ist ohne Wirkung**: Die Einstellung wird
  gespeichert, aber beim Löschen nicht gelesen; BusChord fragt immer nach.

## 12. MCP und Agentenbetrieb

BusChord enthält einen headless MCP-Server (`src/Knx.Mcp`, stdio, JSON-RPC 2.0)
für Agenten und Skripte. Er nutzt dieselben Core-Funktionen, arbeitet ohne
Oberfläche und ändert die `.knxproj` erst bei `project_save`. Der Server bietet
51 Werkzeuge: Projekt, Suche/Ersetzen, Gruppenadressen, Geräte, manuelle
Verknüpfungen, Analyse (Prüfung, Smart Linking, Produkttausch), Exporte, Katalog,
Bus, Undo/Redo, das Abnahmeprotokoll, das Übergabepaket und die read-only
Download-Vorprüfung. `bus_write` ist vorhanden, aber gesperrt; Buszugriffe sind
lesend.

Einzelheiten, Startbefehle, Verbindungsbeispiel und Werkzeugliste stehen in
[mcp.md](../mcp.md). Für Agenten gilt dasselbe Sicherheitsmodell: Bearbeitung ist
offline, Buszugriffe sind ausdrückliche lesende Aufrufe, verschlüsselte Projekte
bleiben schreibgeschützt.

## 13. Was dieses Handbuch nicht belegt

Folgende Punkte sind bewusst als offen markiert und nicht durch dieses Handbuch
verifiziert:

- Breite Interoperabilität von Gruppenwert-Antworten, Descriptor-/Scan-Antworten
  und Secure-Tunneln mit verschiedenen Geräten. Die gezielten Gerätebelege aus
  7.5 und dem Inbetriebnahmeplan ersetzen keine allgemeine Hardware-Abnahme.
- ETS-Roundtrip und Signaturgültigkeit nach Änderungen.
- ETS-Interpretation der BusChord-Parameter-Overrides.
- Ausführung von DCA/Plug-in-Code, Secure-Inbetriebnahme und allgemeine
  Geräteprogrammierung über verschiedene Masken-/Herstellerprofile.
- Vollständige visuelle und interaktive Abnahme: Der Screenshot-Befehl und die
  Workbench-Selbstprüfung belegen gerenderte Ansichten und geroutete Abläufe,
  keine Screenreader-, 150-/200-%-DPI- oder vollständige Klicktest-Abnahme.
- Verhalten von ETS beim Öffnen der von BusChord geschriebenen Dateien insgesamt.

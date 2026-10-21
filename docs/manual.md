# BusChord – Benutzerhandbuch

Stand: 2026-10-18, geprüft gegen den Quellstand `77c78f9`.
Dieses Handbuch beschreibt die deutsche Oberfläche. Es nennt nur Wege, die im
aktuellen Quellcode existieren; nicht belegte Punkte sind ausdrücklich als
**nicht verifiziert** markiert.

Weiterführende Dokumente:

- [UX-/UI-Entwurf](../analysis/ux/design.md) – Designentscheidungen und ihr Umsetzungsstand
- [Router-Discovery-Beleg](../analysis/router-discovery.md) – was live am Bus geprüft wurde
- [MCP-Server](../mcp.md) – Agenten- und Skriptbetrieb
- [README](../README.md) – Funktionsübersicht, Build und Tests

## 1. Was BusChord ist

BusChord ist ein unabhängiges Engineering-Werkzeug für KNX-Gebäudeautomation. Es
liest und schreibt ETS-Projektarchive (`.knxproj`), durchsucht Hersteller-Kataloge
(`.knxprod`), verwaltet Topologie, Gruppenadressen, Kommunikationsobjekte und
Geräteparameter und spricht KNXnet/IP im Netzwerk.

- **Keine ETS-Lizenz nötig.** BusChord ist kein ETS-Bestandteil, nicht von der KNX
  Association freigegeben und nicht aus ETS-Code abgeleitet. *KNX* und *ETS* sind
  Marken der KNX Association und werden hier nur beschreibend verwendet.
- **Projektbearbeitung ist offline.** Adressen, Verknüpfungen und Parameter ändern
  nur das Projekt im Arbeitsspeicher. Auf den Bus geht erst eine ausdrückliche
  Aktion (Senden, Lesen, Download) – und auch die nur mit aktiver Verbindung.
- **Keine stille Geräteprogrammierung.** Ein Download startet ausschließlich über
  „Download (Image)…“ im Eigenschaften-Panel, mit ausdrücklich gewählter
  Image-Datei und vorhandener Verbindung. Es gibt keinen Auto-Download beim
  Speichern, Öffnen oder Prüfen.

Was BusChord bewusst nicht ist: ein vollständiger ETS-Ersatz. Offene Grenzen stehen
in Abschnitt 11, der Live-Status der Busfunktionen in Abschnitt 7.5.

## 2. Erste Schritte

### 2.1 Startfläche

Ohne geladenes Projekt zeigt das Hauptfenster die Startfläche mit drei Karten und
der Liste **„Zuletzt geöffnet“**:

| Karte | Wirkung |
|---|---|
| **Projekt öffnen** | Dateidialog für `*.knxproj` |
| **Neues Projekt** | Dialog „Neues Projekt“, danach Speicherort wählen |
| **Gerät aus Katalog** | öffnet den Katalog-Tab, auch ohne Projekt |

Ein Klick auf einen Eintrag der Zuletzt-Liste öffnet das Projekt sofort. Die Liste
enthält höchstens zehn Projekte, das zuletzt geöffnete zuerst; nicht mehr
vorhandene Dateien werden ausgeblendet. Der Katalog-Tab ersetzt die Startfläche
auch ohne Projekt; die übrigen Tabs bleiben ohne Projekt leer bzw. melden
„Projekt öffnen“.

### 2.2 Neues Projekt

„Neues Projekt“ fragt Name, Kommentar, Gruppenadress-Stil (dreistufig, zweistufig,
frei) sowie Anzahl Bereiche und Linien pro Bereich (je 1–15). Nach „Anlegen“ folgt
der Speicherdialog für die `.knxproj`. Wird er abgebrochen, existiert das Projekt
nur im Arbeitsspeicher; „Projekt speichern“ öffnet dann den Speicherdialog erneut.

### 2.3 Projekt öffnen und speichern

- **Datei → Projekt öffnen…** (`Strg+O`) oder die Karte auf der Startfläche.
- Beim Laden wird das Projekt in die Zuletzt-Liste übernommen.
- **Datei → Projekt speichern** (`Strg+S`) speichert unter dem bekannten Pfad.
  Hat das Projekt noch keinen Pfad, führt der Menüpunkt zu „Projekt speichern
  unter…“; die Tastenkombination `Strg+S` tut in diesem Fall nichts.
- **Datei → Projekt speichern unter…** wählt einen neuen Pfad und trägt ihn ein.

Ladefehler erscheinen als Meldungsfenster „Projekt nicht ladbar“ mit dem technischen
Grund; die Statuszeile zeigt denselben Text.

### 2.4 Hauptfenster

Die Produktoberfläche ist die objektbezogene V4-Workbench:

- **links** der Navigator mit den Perspektiven **Gebäude**, **Topologie** und
  **Gruppenadressen**, jeweils mit eigenem Suchfilter, eigener Auswahl und eigenem
  Aufklappzustand,
- **Mitte** schließbare Arbeitsgegenstands-Tabs für Projekt, Raum, Linie, Gerät
  und Gruppenadresse; jede Darstellung (Kommunikationsobjekte, Parameter,
  Beziehungen, Prüfung) bleibt an ihren Tab gebunden,
- **rechts** der bei Bedarf eingeblendete Inspektor,
- **unten** die Werkzeuge **Monitor**, **Programmierung** und **Prüfmeldungen**.

Kopfzeile: Projekt/Bearbeiten/Ansicht/Werkzeuge, Befehlssuche (`Ctrl+K`) und
„Programmieren“. Die Werkzeugleiste links blendet Navigator, Katalog, Prüfung,
Monitor und Optionen ein. `Ctrl+B` schaltet den Navigator, `Ctrl+J` das untere
Werkzeug. Größen und Sichtbarkeit werden als Layout getrennt vom Projekt
gespeichert; „Fokuslayout“ und „Layout zurücksetzen“ ändern keine Projektdaten.

Die Tabelle zeigt Geräte, Herstellerkanäle und Kommunikationsobjekte in einer
gemeinsamen virtualisierten Liste mit DPT, Länge, Flags und Gruppenadressen.
Mehrfachauswahl und „Verknüpfen“ prüfen alle Ziele vor der Änderung; eine
unverträgliche Auswahl wird vollständig abgelehnt. Die Sendeadresse wird
ausdrücklich im Inspektor gewählt und ist unabhängig vom T-Flag.

Das frühere Ribbon-/Dock-Hauptfenster existiert nur noch als
Kompatibilitätsprüfstand für ältere Tests; es ist nicht die Produktoberfläche.

### 2.5 Werkzeuge, Suche und Sicherungen

Die Werkzeugleiste unten enthält **Monitor**, **Programmierung**, **Scan**,
**Suche/Ersetzen**, **Änderungen** und **Prüfmeldungen**. Ausblenden beendet
Monitor- oder Programmier-Sitzungen nicht. Die Werkzeugübersicht im Menü
`Werkzeuge` öffnet dieselben Orte, Secure-Übersicht und Hilfe.

Der Inspektor hat einen Bereichswähler: Eigenschaften, Gruppen, Suche,
Arbeitsbereiche und Vorgänge. Arbeitsbereiche speichern nur das Layout unter
`%APPDATA%\BusChord\workspaces`; Projektdaten bleiben unberührt.

`Projekt → Projekt vergleichen …` öffnet einen nativen Vergleich mit einem zweiten
`.knxproj`. `Produkt ersetzen …` und die Label-/Semantik-Exporte sind ebenfalls im
Projektmenü. Vor jedem Speichern wird eine lokale Sicherung der vorhandenen Datei
angelegt; `Sicherungen …` listet sie und schreibt eine gewählte Sicherung atomar
in eine neue Datei. Der Download bleibt gesperrt, bis Descriptor-, Mask- und
Programming-Mode-Prüfungen implementiert sind; die Workbench-Programmierung ist
eine gekennzeichnete Simulation.

## 3. Katalog

Der Katalog ist ein Tab des Arbeitsbereichs, kein eigenes Fenster. Er ist über die
Startfläche, die Aktionsleiste **„Katalog“**, **Extras → Katalog**
(`Strg+Shift+K`) und das Kontextmenü im Topologie-Baum erreichbar – mit oder ohne
geladenes Projekt. Aus dem Baum wird die angeklickte Linie als Ziel vorbelegt.

### 3.1 Bibliothek importieren

„Importieren…“ öffnet einen Mehrfach-Dateidialog für **`.knxprod`** und
**Hersteller-ZIPs** (`.zip`). Importierte Kataloge werden in die lokale Bibliothek
kopiert:

```
%APPDATA%\BusChord\catalogs
```

Die Bibliothek ist links sichtbar („Bibliothek · Anzahl“). Sie zeigt je Eintrag
Herstellername, Katalogsnamen und Produktzahl. Der Herstellername stammt aus den
Katalogdaten bzw. einer eingebetteten `knx_master.xml`; fehlt er, erscheint die
Herstellerkennung. Derselbe Katalog als `.knxprod` und als Hersteller-ZIP landet
unter demselben Bibliothekseintrag.

Dateien, die nicht gelesen werden können, blockieren den Import nicht: sie werden
im Fehlertext über der Bibliothek mit Dateiname und Grund aufgeführt. „Neu laden“
liest die Bibliothek erneut ein.

Bekannte Fehlerfälle:

| Fall | Verhalten |
|---|---|
| Hersteller-ZIP um eine `.knxprod` | wird entpackt und als Katalog gelesen |
| Datei ist kein KNX-Katalog | Meldung „Katalog nicht lesbar: …“, übrige Importe laufen weiter |
| Datei liegt nicht lokal (kein Pfad) | „Lokale Datei erforderlich: …“ |
| Katalog verschachtelt zu tief | Abbruch nach vier Archiv-Ebenen |
| Bibliothek leer | Hinweis „kein Katalog geladen“ mit Import-Schaltfläche |

### 3.2 Suchen und filtern

- Das Suchfeld durchsucht **Name, Bestellnummer, ID und Beschreibung** des
  gewählten Bibliothekseintrags, ohne Rücksicht auf Groß-/Kleinschreibung.
- Das Auswahlfeld darunter filtert nach **Rubrik** („Alle Rubriken“ oder ein
  gespeicherter Katalogpfad); Suche und Rubrik wirken zusammen.
- „Keine Treffer – Filter ändern“ setzt Suche und Rubrik zurück.
- Gesucht wird immer nur im ausgewählten Bibliothekseintrag, nicht über alle
  Hersteller gleichzeitig.

Rechts zeigt die Detailspalte Hersteller, Name, Beschreibung, Bestellnummer,
Applikation, Anzahl Kommunikationsobjekte, Katalogpfad und Produkt-ID.

### 3.3 Gerät einfügen

1. Produkt in der Mittelliste wählen.
2. **Ziel-Linie** wählen (Installation, Bereich und Linie sind in der Auswahl
   enthalten).
3. **Adresse** prüfen: BusChord schlägt die niedrigste freie Adresse der Linie vor.
4. Optional **Name** ändern (Vorgabe: Produktname).
5. **Einfügen**.

Die Adresse muss zur gewählten Linie passen (Bereich und Linie identisch,
Gerätenummer 1–255) und projektweit eindeutig sein. Mögliche Sperren:

- „kein Produkt ausgewählt“
- „Ziel-Linie wählen“
- „Gültige Adresse der Ziel-Linie mit Gerätenummer 1–255 eingeben“
- „Adresse x.y.z ist bereits vergeben“
- „Ziel-Linie ist voll“

Das eingefügte Gerät übernimmt die Kommunikationsobjekte der Applikation (sofern
diese lesbar ist) und wird anschließend ausgewählt. Das Einfügen ist ein Undo-Schritt. Ohne Projekt sind
Ziel-Linie, Adresse und Name gesperrt; der Katalog bleibt aber durchsuchbar.

## 4. Gruppenadressen

Der Tab **Gruppenadressen** listet Adresse, Name, Datapoint-Typ und Verknüpfungs-ID.

### 4.1 Anlegen und bearbeiten

- **„+“** oder **Bearbeiten → Gruppenadresse anlegen** (`Strg+G`) legt sofort eine
  Adresse mit der nächsten freien Gruppenadresse (Beginn bei 1/1/1) und dem Namen
  „Neue Gruppenadresse“ an. Fehlende Haupt-/Mittelbereiche werden automatisch
  erzeugt.
- Auswahl rechts im Panel **Eigenschaften**: Name und Datapoint-Typ ändern, dann
  **Übernehmen** (oder `Enter` im Namensfeld). `F2` springt in das Namensfeld.
- Der Datapoint-Typ ist ein **freies Textfeld** (Platzhalter `DPST-1-1`). BusChord
  prüft das Format an dieser Stelle nicht; die Projektprüfung meldet nur fehlende,
  nicht ungültige Typen.
- Die **Adresse selbst ist in der Oberfläche nicht editierbar**; sie wird beim
  Anlegen automatisch vergeben.
- **„-“** oder `Entf` löscht die ausgewählte Adresse. Verknüpfungen
  auf diese Adresse werden aus allen Kommunikationsobjekten entfernt.
  **Achtung:** Die Option „Vor dem Löschen nachfragen“ in den Optionen wird im
  aktuellen Stand gespeichert, aber nicht ausgewertet – es gibt keine
  Rückfrage.

### 4.2 Datapoint-Typ und Werte

Der Datapoint-Typ steuert, wie **Senden** einen eingegebenen Wert kodiert. Kodiert
werden:

| Typ | Eingabe |
|---|---|
| `DPST-1-1`, `DPST-1-2`, `DPST-1-10`, `DPST-1-11` | `1`/`0`, `on`/`off`, `true`, `start` … |
| `DPST-5-1`, `DPST-5-3` | Prozentwert, z. B. `50` oder `50%` |
| `DPST-9-1`, `DPST-9-7` | Fließkommazahl, z. B. `21.5` (Komma wird akzeptiert) |
| alle anderen | Hex-Bytes, z. B. `0A FF` |

### 4.3 Lesen und Senden

Beides braucht eine aktive Busverbindung (Abschnitt 7):

- **Senden** schreibt den Wert aus dem Feld „Wert senden“ auf die ausgewählte
  Gruppenadresse. Ohne Verbindung oder ohne Auswahl bleibt der Klick wirkungslos;
  es gibt in diesem Fall keine Fehlermeldung.
- **Lesen** sendet eine Leseanforderung. Ohne Verbindung meldet die Statuszeile
  „Lesen benötigt eine Busverbindung“. Die Antwort erscheint als Telegramm im
  **Busmonitor**; BusChord zeigt sie nicht in einem eigenen Wertfeld an.

## 5. Kommunikationsobjekte verknüpfen

Der Tab **Kommunikationsobjekte** bearbeitet die Verknüpfungen eines Geräts. Er
öffnet sich über die Aktionsleiste, **Extras → Kommunikationsobjekte**, das
Kontextmenü oder das Eigenschaften-Panel – jeweils mit ausgewähltem Gerät.

- Links: Kommunikationsobjekte des Geräts (Nummer, Text, Datapoint-Typ), mit
  Suchfeld.
- Rechts: Gruppenadressen des Projekts (Adresse, Name, DPT), mit Suchfeld.
- Unten: bestehende Verknüpfungen des gewählten Objekts, mit **„Verknüpfung
  entfernen“**.
- **„Verknüpfen“** legt die markierte Gruppenadresse am markierten Objekt an.

Die Panes arbeiten **offline**: „Nur Projektbearbeitung, keine Bus-Telegramme.
Änderungen unterstützen Rückgängig/Wiederholen.“ Jede echte Änderung ist ein
Undo-Schritt; unveränderte Klicks melden „Keine Änderung“ und erzeugen keinen
Undo-Schritt. Nach Undo/Redo wird der Dialog neu aufgelöst; ein veralteter Bezug
meldet „Projekt geändert — Dialog erneut öffnen“.

Der angezeigte Datapoint-Typ eines Objekts stammt aus seinen verknüpften
Gruppenadressen. Hat ein Objekt Verknüpfungen mit unterschiedlichen Typen, bleibt
die Spalte leer. BusChord prüft **keine DPT-Kompatibilität** beim manuellen
Verknüpfen.

### 5.1 Intelligente Verknüpfung und ihre Grenzen

**Extras → Intelligente Verknüpfung…** (Aktionsleiste **„Verknüpfen“**) schlägt
für **noch unverknüpfte** Kommunikationsobjekte je einen Kandidaten vor:

- Bewertung aus Namensüberschneidung (Wörter ab drei Zeichen, ohne Füllwörter wie
  „der“, „Sensor“, „Kanal“) plus 40 Punkten bei passendem Datapoint-Typ.
- Bekannte, widersprüchliche DPT-Haupt- oder Subtypen werden ausgeschlossen, auch
  bei identischem Namen.
- Vorschläge unter 40 Punkten entfallen; je Objekt wird genau einer angezeigt.
- Der Bericht zeigt Punktzahl, Gerät, Gruppenadresse, Objekt und Begründung.
  **„Alle Verknüpfungen anlegen“** übernimmt alle Vorschläge als **einen**
  Undo-Schritt; von selbst ändert sich nichts.

Grenzen: Der DPT-Abgleich nutzt die Typen aus der geladenen Geräteapplikation.
Konnte BusChord die Applikation nicht lesen, bleibt nur der Namensvergleich.
DPT-Gleichheit ist kein Beweis für fachliche oder geräteseitige Kompatibilität –
Vorschläge sind zu prüfen. Mehrfachverknüpfungen von Hand bleiben möglich.

## 6. Parameter

Der Tab **Parameter** zeigt die Geräteparameter der Herstellerapplikation
(KSXML). Er öffnet sich über Doppelklick auf ein Gerät, das Eigenschaften-Panel,
**Extras → Geräteparameter…** oder die Aktionsleiste.

### 6.1 Statischer Editor

Ein Aufklapper **„Statischer Editor ?“** nennt die Grenze: „Statischer
Parametereditor. Dynamische Sichtbarkeit und Zuweisungen werden nicht ausgewertet.
Übernehmen speichert nur im Projekt, ohne Geräteprogrammierung.“

- Editierbar sind Zahl, Fließkomma, Text, Aufzählung und Boolean. Alle anderen
  Typen (Datum, Uhrzeit, IP, Farbe, Bild, Rohdaten) sowie nicht unterstützte
  Deklarationen erscheinen als schreibgeschützte Zeilen mit Begründung.
- **Dynamische Logik** (Choose/When/Assign, Modulinstanzen, Berechnungen) wird
  erkannt und als Warnung genannt, aber **nicht ausgewertet**. Die angezeigte
  Zeilenmenge ist daher nicht die zur Laufzeit aktive Parameterliste.
- Werte werden beim **Übernehmen** geprüft (Ganzzahl-/Fließkommabereiche,
  Bitbreite, Aufzählungswerte, konservative Textgröße). Fehler bleiben als Text im
  Panel; es wird nichts teilweise übernommen.
- **Übernehmen** schreibt in das Projekt und erzeugt einen Undo-Schritt.
  Dauerhaft gespeichert wird erst mit **Projekt speichern**. Das bloße Öffnen
  der Parameter erzeugt keine Overrides.

### 6.2 BusChord-Overrides

Geänderte Werte speichert BusChord je Gerät unter der Referenz-ID des Parameters im
eigenen XML-Namensraum `urn:buschord:project:1`
(`ParameterInstanceRefs/ParameterInstanceRef`). Beim erneuten Öffnen werden sie auf
die frisch gelesenen Parameter angewendet.

**Nicht verifiziert:** dass ETS diese Overrides als Geräteparameter liest. Die
Ablage ist ein BusChord-Vertrag, keine belegte ETS-Parameterinteroperabilität;
native ETS-Parameterknoten bleiben unverändert opaque erhalten. Ein Parametertausch
mit geänderter Applikation braucht zusätzlich eine geprüfte Objekt- und
Parametermigration.

## 7. Bus

### 7.1 Schnittstelle einrichten

**Extras → Optionen…** (oder Aktionsleiste **„Einstellungen“**) → Abschnitt
**Bus**:

- **Schnittstelle**: IP-Adresse oder Hostname des KNXnet/IP-Interfaces bzw.
  Routers, z. B. `192.168.1.10`. Ohne Eintrag melden Suchen/Verbinden
  „Schnittstelle einrichten“.
- **Eigene Adresse**: Tunnel-Adresse, Vorgabe `1.1.250`. Ist der Eintrag ungültig,
  verwendet BusChord `1.1.250`.

Weitere Optionen: Design (System/Hell/Dunkel), Sprache (Deutsch/English),
„Vor dem Löschen nachfragen“ (siehe 4.1: derzeit ohne Wirkung) und die Liste der
zuletzt geöffneten Projekte. Ein Sprachwechsel wirkt auf danach geöffnete
Fenster; bereits geladene Beschriftungen können bis zum Neustart in der alten
Sprache bleiben. Die Einstellungen liegen in `%APPDATA%\BusChord\settings.json`.

### 7.2 Suchen und Verbinden

1. Schnittstelle eintragen (7.1).
2. **Suchen** (Aktionsleiste) bzw. **Bus → Geräte suchen**.
3. Im Busmonitor das gefundene Gerät unter **„Gerät im Netzwerk:“** wählen.
4. **Verbinden** bzw. **Bus → Verbinden**.

Die Oberfläche verbindet **immer per Tunnel**; die Statuszeile zeigt
„Verbunden: <Gerät>“. **Trennen** löst die Verbindung. Routing (Multicast) ist nur
über den MCP-Server verfügbar, nicht in der Oberfläche. Verbinden ohne gewähltes
Gerät meldet „kein Gerät ausgewählt“.

### 7.3 Busmonitor

Der Monitor zeigt empfangene Telegramme, neueste zuerst, mit Zeitstempel, Quelle,
Ziel, APCI und Rohdaten. Er sammelt höchstens 2000 Zeilen; **„Monitor leeren“**
löscht die Anzeige. Ohne Verbindung bleibt er leer.

### 7.4 Aufzeichnung

Der Tab **Recorder** zeichnet nur während einer laufenden Aufzeichnung auf;
**Start** leert die vorherige Aufzeichnung, **Stop** beendet sie.

- **Speichern…** schreibt JSON Lines (`.jsonl`) oder CSV (`.csv`); die Dateiendung
  entscheidet das Format. Ohne Aufzeichnung meldet die Statuszeile „keine
  Aufzeichnung vorhanden“.
- **Laden…** liest `.jsonl` oder `.csv` und füllt die Recorder-Liste. Es werden
  dabei **keine Telegramme gesendet**. Die Statusmeldung nennt zwar eine
  „Wiedergabe über den Monitor“; tatsächlich wird nur die Liste gefüllt.

### 7.5 Was live verifiziert ist – und was nicht

**Live verifiziert** sind Discovery und der Tunnel-Handshake gegen einen MDT
SCN-IP100.03 unter `192.0.2.10:3671` (2026-10-18): Discovery per gezieltem
SEARCH/DESCRIPTION, danach ein Tunnel-CONNECT (Kanal 16, zugewiesene Adresse
`1.0.1`), genau ein Gruppenwert-Lesen auf `0/0/1` mit Tunnelling-ACK und
`L_Data.con`, anschließend ein sauberes DISCONNECT. Es wurden **keine**
Gruppenwerte geschrieben, keine Geräte programmiert, keine Neustarts oder
Adressänderungen ausgeführt und keine Firewall-Einstellungen geändert. Details:
[Router-Discovery](../analysis/router-discovery.md).

**Nicht verifiziert** sind Gruppenwert-Antworten (für `0/0/1` antwortete kein
Gerät), Schreibzugriffe, Download/Programmierung, Secure-Tunnel und längere
Sitzungen mit Heartbeat gegen echte Hardware. Synthetische Testläufe sind kein
Interoperabilitätsnachweis. Ein erfolgreicher Verbindungsaufbau in der Oberfläche
belegt daher noch keine Inbetriebnahme.

**Download:** „Download (Image)…“ im Eigenschaften-Panel ist nur aktiv mit
Verbindung, ausgewähltem Projektgerät und ausgewähltem Busgerät. Der Dialog
verlangt eine Image-Datei; der Fortschritt erscheint in der Statuszeile. Das
Verfahren überträgt laut Projektstatus eine Rohdatei als Image (kein
BCU2-Encoding) und ist nicht gegen echte Geräte abgenommen. Eine Programmierung
erfolgt nur durch diesen bewussten Schritt.

## 8. Projektpflege

### 8.1 Speichern

Speichern schreibt zuerst eine exklusive temporäre Datei neben dem Ziel, prüft sie
durch erneutes Laden und ersetzt erst dann die Zieldatei (bei vorhandenem Ziel per
`File.Replace`, sonst per `File.Move`). Schlägt das Schreiben oder die Prüfung fehl,
bleibt die vorhandene Datei unverändert.

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

### 8.4 Verschlüsselte und fremde Projekte

Unterstützt wird genau ein Format: unverschlüsseltes ZIP mit `0.xml` im Wurzel-
verzeichnis **oder** ein unverschlüsseltes `P-*.zip` mit `0.xml`, XML im
Namensraum `http://knx.org/xml/project/23`.

Abgelehnt werden unter anderem:

- verschlüsselte Archive (auch AES): „Encrypted project archives are not
  supported. …“
- andere Schemata oder mehrere Projekte: „Supported format: one KNX project in
  namespace /23; …“
- ZIP64, Mehrteil-Archive, doppelte Eintragsnamen, mehrdeutige Wurzel- und
  verschachtelte Projekte
- Übergrößen: 128 MiB Archiv, 4096 Einträge, 64 MiB je Eintrag, 256 MiB
  entpackt, 16 MiB Projekt-XML, XML-Tiefe 128

Passwörter werden nicht geraten und nicht umgangen. Für verschlüsselte
ETS-Exporte gibt es derzeit keinen Leseweg in BusChord.

### 8.5 Undo/Redo und Zuletzt-Liste

- **Rückgängig** (`Strg+Z`) und **Wiederherstellen** (`Strg+Y`) arbeiten auf
  Projekt-Snapshots; die Oberfläche hält bis zu **60** Schritte, der MCP-Server
  100. Ein neues Projekt oder ein neu geladenes Projekt setzt die Historie zurück.
- Speichern selbst erzeugt keinen zusätzlichen Undo-Schritt.
- Die Zuletzt-Liste führt maximal zehn Projekte; fehlende Dateien erscheinen nicht.

## 9. Prüfen, Exporte, Berichte, Vergleich

### 9.1 Projektprüfung

**Extras → Projektprüfung** (`F5`) bzw. der Tab **Projektprüfung** mit
**„Prüfung starten“**. Der Bericht nennt Fehler, Warnungen und Hinweise und lässt
sich als CSV speichern. Geprüft werden unter anderem (Codes P001–P042):

- fehlende Installation, Bereichs-/Linienadressen außerhalb 0–15, mehrere Segmente
  je Linie (Warnung),
- Geräteadresse 0, fehlende Produktreferenz, fehlender Name, fehlende
  Kommunikationsobjekte, doppelte Geräteadressen,
- fehlender Datapoint-Typ, fehlender Name, doppelte Gruppenadressen, Adresse
  außerhalb des Bereichs,
- unverknüpfte Kommunikationsobjekte, Verknüpfungen auf nicht existierende
  Gruppenadressen, unverknüpfte Gruppenadressen.

Die Prüfung ist strukturell; sie ersetzt keine Geräte- oder Applikationsprüfung.

### 9.2 Exporte

**Datei → Export** (auch als Schaltfläche mit Menü in der Aktionsleiste):

| Menüpunkt | Datei |
|---|---|
| Gruppenadressen (CSV)… | `gruppenadressen.csv` |
| Geräte (CSV)… | `geraete.csv` |
| Topologie (CSV)… | `topologie.csv` |
| Semantic Export (Turtle, .ttl)… | `semantik.ttl` |
| Semantic Export (JSON-LD, .jsonld)… | `semantik.jsonld` |
| Etiketten (HTML-Druckbogen)… | `etiketten.html` |
| Etiketten (CSV)… | `etiketten.csv` |
| Etiketten (Mail-Merge-Datenquelle)… | `etiketten-mailmerge.txt` |

Der Turtle-Export bettet die KNX-Ontologie ein, wenn
`C:\Program Files (x86)\ETS6\ontology.ttl` vorhanden ist; die Statuszeile weist
darauf hin. Der JSON-LD-Export ist davon unabhängig. Gebrauchsfertige
Word-Mail-Merge-Vorlagen liefert BusChord nicht – nur die Datenquelle.

### 9.3 Import von Gruppenadressen

**Datei → Gruppenadressen importieren (CSV)…** liest ein einfaches Format
`1/2/3;Name;DPT;Beschreibung`; ein ETS-CSV-Export funktioniert, solange diese
Spalten in dieser Reihenfolge stehen. Eine Kopfzeile ohne Adresse wird
übersprungen. Bereits vorhandene Adressen
werden übersprungen, fehlende Haupt-/Mittelbereiche angelegt, der DPT auf
`DPST-<Haupt>-<Sub>` normalisiert. Nicht lesbare Zeilen werden am Ende als
Bericht mit Zeilennummer gemeldet; der Rest wird importiert.

### 9.4 Projektvergleich

**Bearbeiten → Projekt vergleichen…** setzt ein geladenes Projekt voraus, lädt
ein zweites `.knxproj` und zeigt hinzugefügte, entfernte und geänderte Objekte
mit Pfad, Typ, Name und neuem Wert.
Die Statuszeile fasst „+ / − / ~“ zusammen; das Fenster kann die Liste als CSV
speichern. Der Vergleich ändert kein Projekt.

### 9.5 Produkt austauschen

**Extras → Produkt austauschen…** stellt alle Geräte einer Produkt-RefId auf eine
andere um, optional mit neuer Applikation (Hardware2Program-RefId).

1. Alte RefId (bei ausgewähltem Gerät vorbelegt) und neue RefId eingeben.
2. **Vorschau** – erst danach wird **Austauschen** aktiv.
3. Die Vorschau listet betroffene Geräte und die Zahl der erhaltenen
   Verknüpfungen; der Austausch ist ein Undo-Schritt.

Der Austausch ersetzt keine fehlende Objekt-/Parametermigration: Passen
Kommunikationsobjekte oder Parameter nicht zusammen, bleibt die fachliche Prüfung
beim Anwender.

## 10. Tastenkürzel

In der V4-Workbench verdrahtet (`WorkbenchWindow.axaml.cs`):

| Kürzel | Wirkung |
|---|---|
| `Strg+B` | Navigator ein-/ausblenden |
| `Strg+J` | unteres Werkzeug ein-/ausblenden |
| `Strg+K` | Befehlssuche |
| `Strg+Tab` | nächster Arbeitsgegenstands-Tab |
| `Strg+Z` / `Strg+Shift+Z`, `Strg+Y` | Projekt rückgängig / wiederherstellen |
| `Strg+S` | Projekt speichern (ohne Pfad: Speichern unter) |
| `Strg+N`, `Strg+O` | Neues Projekt, Projekt öffnen |
| `Escape` | temporäre Oberfläche/Dialog schließen |

Textfelder behalten ihr lokales Undo; `Strg+Z` im Editor ändert nicht zusätzlich das
Projekt. Das frühere Hauptfenster hat eigene Kürzel (`Strg+G`, `F5`, `F2`) und ist
nur noch Kompatibilitätsprüfstand.

## 11. Grenzen und Sicherheit

- **Keine Programmierung ohne bewussten Download.** Projektänderungen senden
  nichts. Senden/Lesen verlangen eine aktive Verbindung; der Download ist ein
  eigener, bewusster Schritt mit gewählter Image-Datei.
- **Keine Firewall- oder Netzwerkänderungen.** BusChord öffnet UDP-Sockets für
  Discovery/Tunnel; es ändert keine System- oder Routerkonfiguration.
- **Keine Passwortumgehung.** Verschlüsselte Projekte werden mit einer
  Formatmeldung abgelehnt; es gibt keinen Entschlüsselungs- oder Rateweg.
- **Keine KNX Data Security im Tunnel.** Keyring und Secure-Formate sind eine
  Grundlage, aber nicht in den Tunnel verdrahtet; das Wire-Format ist nicht gegen
  eine echte sichere Anlage abgenommen.
- **Kein ETS-Roundtrip-Nachweis.** Das Schreiben wurde mit synthetischen Archiven
  und eigenen Prüfungen getestet, nicht durch Wiederöffnen in ETS abgenommen.
  Signaturprüfungen in ETS können nach Änderungen fehlschlagen.
- **Keine vollständige ETS-Parität.** Es fehlen unter anderem Gebäude-/Raum-/
  Funktionsmodell und Editor, vollständige dynamische Herstellerlogik,
  Plug-ins/DCA, Hawk-/IoT-Geräteimages sowie breite Tunnel-/Download-
  Interoperabilität.
- **Busadressen sind 4-Bit-begrenzt.** Bereich und Linie 0–15, Gerätenummer
  1–255; ungültige Eingaben werden abgelehnt statt umgerechnet.
- **DPT-Abgleich ist keine Kompatibilitätsprüfung.** Weder manuelles Verknüpfen
  noch Smart Linking prüfen Bitgröße, Sicherheitsflags oder Produktverträglichkeit.
- **Die Projektprüfung ist strukturell**, keine Inbetriebnahme- oder
  Gerätediagnose.
- **„Vor dem Löschen nachfragen“ ist derzeit ohne Wirkung**: Die Einstellung wird
  gespeichert, aber beim Löschen nicht gelesen.

## 12. MCP und Agentenbetrieb

BusChord enthält einen headless MCP-Server (`src/Knx.Mcp`, stdio, JSON-RPC 2.0)
für Agenten und Skripte. Er nutzt dieselben Core-Funktionen, arbeitet ohne
Oberfläche und ändert die `.knxproj` erst bei `project_save`. Werkzeuggruppen:
Projekt, Gruppenadressen, Geräte, manuelle Verknüpfungen, Analyse, Export,
Katalog, Bus, Undo/Redo.

Einzelheiten, Startbefehle, Verbindungsbeispiel und Werkzeugliste stehen in
[mcp.md](../mcp.md). Für Agenten gilt dasselbe Sicherheitsmodell: Bearbeitung ist
offline, Buszugriffe sind ausdrückliche Aufrufe, verschlüsselte Projekte bleiben
gesperrt.

## 13. Was dieses Handbuch nicht belegt

Folgende Punkte sind bewusst als offen markiert und nicht durch dieses Handbuch
verifiziert:

- Interoperabilität von Gruppenwert-Antworten, Schreibzugriffen und Download mit
  echter Hardware (live belegt sind Discovery und Tunnel-Handshake, siehe 7.5).
- ETS-Roundtrip und Signaturgültigkeit nach Änderungen.
- ETS-Interpretation der BusChord-Parameter-Overrides.
- Visuelle/interaktive Abnahme der Katalogoberfläche (die Prüfungen sind
  Build- und Headless-Abläufe, keine Screenshot- oder Klicktests).
- Verhalten von ETS beim Öffnen der von BusChord geschriebenen Dateien insgesamt.

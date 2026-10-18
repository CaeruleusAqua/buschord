# Icon-System für BusChord

- Branch: `agents/ux/u3-icons`, Basis `dfc87fa576dd1d11a4bb7f4a37d098ec9a68c097`
- Datum: 18.10.2026
- Status: Vorschlag. Keine Codeänderung, keine Abhängigkeit, keine Assets im Repo — die Pfade stehen in dieser Datei.
- Umfang: Hauptfenster (`src/Knx.App/MainWindow.axaml`), `CatalogWindow`, `CompareWindow`, `NewProjectWindow`, `ParameterWindow`/`ParameterDialogView`, `ReplaceProductWindow`, `ReportWindow`, `SettingsWindow`, `GroupLinksWindow.cs`, `MainWindowViewModel.cs` (Baumknoten), Strings in `src/Knx.Core/Localization.cs`.

**Provenienz der Pfade.** Alle `d`-Werte sind am 18.10.2026 unverändert aus
`https://fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/<name>/default/24px.svg`
geholt (eine Datei je Icon, `viewBox="0 -960 960 960"`, reine Füllpfade, kein `stroke`).
Material Symbols stehen unter Apache-2.0. Kein Name in dieser Liste ist aus dem Gedächtnis ergänzt; jeder Name wurde erfolgreich abgerufen.

---

## 0. Ist-Aufnahme

### 0.1 Befehle und ihre heutige Darstellung

| Ort | Befehl | heute | Entscheidung |
|---|---|---|---|
| Menü Datei | Neu, Öffnen, Speichern, Speichern unter, Export (9 Varianten), Import, Beenden | Wort | **Wort** (Menü bleibt textlich) |
| Menü Bearbeiten | Rückgängig, Wiederherstellen, GA anlegen, GA löschen, Vergleich | Wort | Wort, zusätzlich Icons in der Leiste |
| Menü Bus | Geräte suchen, Verbinden, Trennen, Aufzeichnung ×4 | Wort | Wort im Menü, Icons in der Bus-Leiste |
| Menü Extras | Prüfung, Intelligente Verknüpfung, Produkttausch, Katalogbibliothek, Geräteparameter, Kommunikationsobjekte, Optionen | Wort | teils Icon (siehe §2) |
| Ribbon | 7 Gruppen × 21 Knöpfe (`Neu` … `Optionen…`) | Wort auf Wort | **Ribbon entfällt**, ersetzt durch 7 Ziele + Kontextleiste |
| Baum | `+ Gerät` | Plus-Glyphe + Wort | unverändert (das Plus ist schon ein Symbol) |
| Baum-Kontextmenü | Parameter…, Kommunikationsobjekte, Gerät löschen, Gerät zur Linie, GA anlegen | Wort | Wort + Icon für die ersten beiden |
| Gruppenadressen | `+`, `−`, Senden, Lesen | Glyphe bzw. Wort | `+`/`−` bleiben Glyphen, Senden/Lesen bleiben Wörter |
| Eigenschaften | Übernehmen, Parameter…, Kommunikationsobjekte, Download (Image)… | Wort | Parameter/Kommunikationsobjekte Icon + Label, Übernehmen und Download **nur Wort** |
| Reiter | Gruppenadressen, Dashboard, Projektprüfung, Busmonitor, Recorder | Wort | **Reiter entfallen**, Ziele der Navigation |
| Monitor | Gerät im Netzwerk, Monitor leeren | Wort | Wort |
| Recorder | Start, Stop, Speichern…, Laden… | Wort | Start/Stop Icon, Speichern/Laden Wort |
| Katalog | Importieren…, Neu laden, In Projekt einfügen, Schließen | Wort | Wort (Import ist richtungsmehrdeutig) |
| Vergleich | Als CSV exportieren…, Schließen | Wort | Wort |
| Dialoge | Anlegen/Abbrechen, Übernehmen/Schließen, Vorschau/Austauschen/Schließen, Speichern/Abbrechen | Wort | Wort (Standardverben) |

### 0.2 Wo heute Text Struktur trägt

| Ort | Text heute | Problem | Vorschlag |
|---|---|---|---|
| Ribbon | 7 Gruppenüberschriften + 21 Knopfwörter | 28 Wörter dauerhaft sichtbar, davon 12 Dubletten zum Menü | Ribbon löschen (§4) |
| Reiter | 5 Reitertitel | Orte als Reiter in einem von drei Panes versteckt | 7 Ziele in der Navigation |
| Baum | 5 typografische Glyphen `⌂ ▣ ≡ ▢ ◈` (Zeilen 586–626) | gemischt aus Geometrie und Sonderzeichen, `◈`/`▢` fehlen in manchen Fonts (Tofu), keine einheitliche Strichstärke | nur zwei echte Icons: Linie, Gerät |
| Panels | „Topologie", „Eigenschaften" | „Topologie" doppelt das Navigationsziel | Panel-Header „Topologie" löschen |
| Dashboard | 4 KPI-Beschriftungen, 2 Abschnittstitel | Text ist hier richtig (Zahlen ohne Wort sind nichts) | keine Icons |
| Tabellen | Spaltenköpfe | Text ist richtig | keine Icons |

**Wirkung in Zahlen.** Heute: 7 Gruppenüberschriften + 21 Knopfwörter + 5 Reitertitel = 33 dauerhaft sichtbare Strings im Hauptfenster.
Vorschlag: 7 Navigationslabels + 6 Kontextlabels + 2 icon-only = 13 Strings. Die Icons ersetzen also nicht Buchstaben, sondern **verschachtelte Ebenen**.

---

## 1. Prinzipien

**P1 Zweckbindung.** Ein Icon ist nur erlaubt, wenn es genau eines von drei Dingen markiert: einen **Ort** (Navigation), einen **Zustand** (Bus, Aufzeichnung), einen **Objekttyp** (Baumzeile). Alles andere bleibt Wort.

**P2 Obergrenze.** Maximal 20 Icons für die gesamte App. Erreicht: **18**. Ein neues Icon muss ein bestehendes verdrängen, nicht danebenstehen.

**P3 Größenraster.** 16 px in Baum und Listen, 20 px in Navigation und Werkzeugleiste. Nie kleiner als 16 px: bei weight 400 brechen die 2-px-Stämme sonst weg.

**P4 Eine Familie.** Nur Material Symbols **Outlined**, 24-px-Grid, weight 400, `filled=0`. Keine gefüllten Varianten, keine Mischung mit den heutigen Textglyphen `⌂▣≡▢◈`, keine zweifarbigen Icons.

**P5 Farbe.** Einfarbig, `Fill` = Vordergrundfarbe. Aktiv: `EtsAccent`, inaktiv: `EtsMuted`, deaktiviert: `EtsMuted` mit 40 % Deckkraft. `EtsError` (Rot) ausschließlich für „Aufzeichnung läuft".

**P6 Icon + Label.** Icon-Spalte fest 24 px, Abstand zum Label 6 px, Label nie unter 11 px. Dadurch stehen alle Labels einer Leiste bündig.

**P7 Icon-only.** Nur **Rückgängig** und **Wiederherstellen**: immer paarweise, immer mit Tooltip, immer mit Kürzel, immer zusätzlich als Wort im Bearbeiten-Menü erreichbar. Höchstens zwei icon-only-Ziele gleichzeitig sichtbar.

**P8 Tooltip.** Jedes icon-only-Ziel hat `ToolTip.Tip` = deutsches Verb + Objekt + Kürzel, z. B. „Rückgängig (Strg+Z)". Ein Tooltip ist **nie** der einzige Träger einer zerstörenden oder buswirksamen Aktion.

**P9 Kein Icon im Menü.** Die Menüleiste ist die textliche Rückfallebene. Icons dort verbreitern nur die Zeilen.

**P10 Kein dekoratives Icon.** Keine Icons an Panel-Headern, KPI-Kacheln, Spaltenköpfen oder Dialogüberschriften.

**P11 Sprache.** Beschriftungen bleiben deutsch; die englischen Material-Namen leben nur im Code (`Icon.Project`, `Icon.Topology`, …).

**P12 Namensdisziplin.** Nur abgerufene Namen. Kein Pfad aus dem Gedächtnis, kein „sieht ungefähr so aus".

---

## 2. Icon-Tabelle

Spalte „Pfad" ist der unveränderte `d`-Wert aus der jeweiligen 24-px-Datei (`viewBox="0 -960 960 960"`).

| # | Aktion (Ort) | Material | Pfad | Warum dieser | Verworfen | Entfallender Text |
|---|---|---|---|---|---|---|
| 1 | Projekt (Navigation) | `dashboard` | `d="M520-600v-240h320v240H520ZM120-440v-400h320v400H120Zm400 320v-400h320v400H520Zm-400 0v-240h320v240H120Zm80-400h160v-240H200v240Zm400 320h160v-240H600v240Zm0-480h160v-80H600v80ZM200-200h160v-80H200v80Zm160-320Zm240-160Zm0 240ZM360-280Z"` | vier Kacheln = Übersicht mit Kennzahlen, genau der Inhalt des Reiters | `home` (klingt nach App-Start), `insights` (klingt nach Bericht) | Reitertitel „Dashboard" |
| 2 | Topologie (Navigation) | `account_tree` | `d="M600-120v-120H440v-400h-80v120H80v-320h280v120h240v-120h280v320H600v-120h-80v320h80v-120h280v320H600ZM160-760v160-160Zm520 400v160-160Zm0-400v160-160Zm0 160h120v-160H680v160Zm0 400h120v-160H680v160ZM160-600h120v-160H160v160Z"` | Hierarchie mit drei Ebenen = Installation/Bereich/Linie/Gerät | `polyline` (Graph/Route), `lan` (Netzwerkschema) | Panel-Header „Topologie" |
| 3 | Gruppenadressen (Navigation) | `format_list_bulleted` | `d="M360-200v-80h480v80H360Zm0-240v-80h480v80H360Zm0-240v-80h480v80H360ZM200-160q-33 0-56.5-23.5T120-240q0-33 23.5-56.5T200-320q33 0 56.5 23.5T280-240q0 33-23.5 56.5T200-160Zm0-240q-33 0-56.5-23.5T120-480q0-33 23.5-56.5T200-560q33 0 56.5 23.5T280-480q0 33-23.5 56.5T200-400Zm-56.5-263.5Q120-687 120-720t23.5-56.5Q167-800 200-800t56.5 23.5Q280-753 280-720t-23.5 56.5Q233-640 200-640t-56.5-23.5Z"` | flache Liste mit Adresspunkten = GA-Liste | `list_alt` (Formular), `view_list` (Datentabelle) | Reitertitel „Gruppenadressen" |
| 4 | Katalog (Navigation) | `library_books` | `d="M400-400h160v-80H400v80Zm0-120h320v-80H400v80Zm0-120h320v-80H400v80Zm-80 400q-33 0-56.5-23.5T240-320v-480q0-33 23.5-56.5T320-880h480q33 0 56.5 23.5T880-800v480q0 33-23.5 56.5T800-240H320Zm0-80h480v-480H320v480ZM160-80q-33 0-56.5-23.5T80-160v-560h80v560h560v80H160Zm160-720v480-480Z"` | Bücherstapel = Bibliothek; die App nennt sie selbst „Bibliothek" | `inventory_2` (Lagerkiste), `store` (Laden) | „Katalogbibliothek…" → „Katalog" |
| 5 | Bus & Diagnose (Navigation) | `sensors` | `d="M197-197q-54-55-85.5-127.5T80-480q0-84 31.5-156.5T197-763l57 57q-44 44-69 102t-25 124q0 67 25 125t69 101l-57 57Zm113-113q-32-33-51-76.5T240-480q0-51 19-94.5t51-75.5l57 57q-22 22-34.5 51T320-480q0 33 12.5 62t34.5 51l-57 57Zm113.5-113.5Q400-447 400-480t23.5-56.5Q447-560 480-560t56.5 23.5Q560-513 560-480t-23.5 56.5Q513-400 480-400t-56.5-23.5ZM650-310l-57-57q22-22 34.5-51t12.5-62q0-33-12.5-62T593-593l57-57q32 32 51 75.5t19 94.5q0 50-19 93.5T650-310Zm113 113-57-57q44-44 69-102t25-124q0-67-25-125t-69-101l57-57q54 54 85.5 126.5T880-480q0 83-31.5 155.5T763-197Z"` | Sendewellen = Verkehr auf dem Bus | `monitor_heart` (EKG), `graphic_eq` (Pegel) | Reitertitel „Busmonitor" + „Recorder", Ribbon „Bus" + „Telegram Recorder" |
| 6 | Prüfen (Navigation) | `fact_check` | `d="M160-120q-33 0-56.5-23.5T80-200v-560q0-33 23.5-56.5T160-840h640q33 0 56.5 23.5T880-760v560q0 33-23.5 56.5T800-120H160Zm0-80h640v-560H160v560Zm40-80h200v-80H200v80Zm382-80 198-198-57-57-141 142-57-57-56 57 113 113Zm-382-80h200v-80H200v80Zm0-160h200v-80H200v80Zm-40 400v-560 560Z"` | Liste mit Haken = Prüflauf mit Befunden | `verified` (Siegel), `task_alt` (erledigt) | Reitertitel „Projektprüfung", Ribbon „Analyse" |
| 7 | Einstellungen (Navigation, unten) | `settings` | `d="m370-80-16-128q-13-5-24.5-12T307-235l-119 50L78-375l103-78q-1-7-1-13.5v-27q0-6.5 1-13.5L78-585l110-190 119 50q11-8 23-15t24-12l16-128h220l16 128q13 5 24.5 12t22.5 15l119-50 110 190-103 78q1 7 1 13.5v27q0 6.5-2 13.5l103 78-110 190-118-50q-11 8-23 15t-24 12L590-80H370Zm70-80h79l14-106q31-8 57.5-23.5T639-327l99 41 39-68-86-65q5-14 7-29.5t2-31.5q0-16-2-31.5t-7-29.5l86-65-39-68-99 42q-22-23-48.5-38.5T533-694l-13-106h-79l-14 106q-31 8-57.5 23.5T321-633l-99-41-39 68 86 64q-5 15-7 30t-2 32q0 16 2 31t7 30l-86 65 39 68 99-42q22 23 48.5 38.5T427-266l13 106Zm42-180q58 0 99-41t41-99q0-58-41-99t-99-41q-59 0-99.5 41T342-480q0 58 40.5 99t99.5 41Zm-2-140Z"` | Zahnrad = App-Einstellungen, weltweit erwartet | `tune` (trägt die Geräteparameter) | Ribbon-Knopf „Optionen…" |
| 8 | Rückgängig (Leiste) | `undo` | `d="M280-200v-80h284q63 0 109.5-40T720-420q0-60-46.5-100T564-560H312l104 104-56 56-200-200 200-200 56 56-104 104h252q97 0 166.5 63T800-420q0 94-69.5 157T564-200H280Z"` | Pfeil über den Verlauf zurück, einziges Standardbild dafür | `arrow_back` (Navigation) | Knopfwort „Rückgängig" (icon-only) |
| 9 | Wiederherstellen (Leiste) | `redo` | `d="M396-200q-97 0-166.5-63T160-420q0-94 69.5-157T396-640h252L544-744l56-56 200 200-200 200-56-56 104-104H396q-63 0-109.5 40T240-420q0 60 46.5 100T396-280h284v80H396Z"` | Spiegel von 8; das Paar ist der eigentliche Träger der Bedeutung | `arrow_forward` | Knopfwort „Wiederherstellen" (icon-only) |
| 10 | Verbinden/Trennen (Bus-Leiste) | `power` | `d="M460-200h40v-74l140-140v-186H320v186l140 140v74Zm-80 80v-120L240-380v-220q0-33 23.5-56.5T320-680h40l-40 40v-200h80v160h160v-160h80v200l-40-40h40q33 0 56.5 23.5T720-600v220L580-240v120H380Zm100-280Z"` | Stecker = Busverbindung herstellen; ein Knopf, zwei Beschriftungen | `link`/`link_off` (Kette heißt hier Verknüpfung), `power_off` als Zweiticon | Knopfwörter „Verbinden" und „Trennen" |
| 11 | Geräte suchen (Bus-Leiste) | `radar` | `d="M324-111.5Q251-143 197-197t-85.5-127Q80-397 80-480t31.5-156Q143-709 197-763t127-85.5Q397-880 480-880t156 31.5Q709-817 763-763t85.5 127Q880-563 880-480t-31.5 156Q817-251 763-197t-127 85.5Q563-80 480-80t-156-31.5ZM480-160q56 0 105.5-17.5T676-227l-57-57q-29 21-64.5 32.5T480-240q-100 0-170-70t-70-170q0-100 70-170t170-70q100 0 170 70t70 170q0 39-12 75t-33 65l57 57q32-41 50-91t18-106q0-134-93-227t-227-93q-134 0-227 93t-93 227q0 134 93 227t227 93Zm0-160q22 0 42.5-5.5T561-342l-61-61q-5 2-10 2.5t-10 .5q-33 0-56.5-23.5T400-480q0-33 23.5-56.5T480-560q33 0 56.5 23.5T560-480q0 6-.5 11.5T557-458l60 60q11-18 17-38.5t6-43.5q0-66-47-113t-113-47q-66 0-113 47t-47 113q0 66 47 113t113 47Z"` | Rundsuche im Bus = Discovery, kein Textfilter | `search` (Lupe gehört dem Katalogfilter) | Knopfwort „Suchen" |
| 12 | Aufzeichnung starten (Diagnose) | `fiber_manual_record` | `d="M480-480ZM282-282q-82-82-82-198t82-198q82-82 198-82t198 82q82 82 82 198t-82 198q-82 82-198 82t-198-82Zm339.5-56.5Q680-397 680-480t-58.5-141.5Q563-680 480-680t-141.5 58.5Q280-563 280-480t58.5 141.5Q397-280 480-280t141.5-58.5Z"` | Aufnahmering, das etablierte Bild für Mitschreiben | `play_arrow`/`play_circle` (klingt nach App-Start) | Knopfwort „Start" |
| 13 | Aufzeichnung beenden (Diagnose) | `stop` | `d="M320-640v320-320Zm-80 400v-480h480v480H240Zm80-80h320v-320H320v320Z"` | nacktes Quadrat, ruhiger Gegenpol zum Ring | `stop_circle` (Kreis im Kreis) | Knopfwort „Stop" |
| 14 | Verknüpfen (Gerätekontext, Objektliste) | `link` | `d="M440-280H280q-83 0-141.5-58.5T80-480q0-83 58.5-141.5T280-680h160v80H280q-50 0-85 35t-35 85q0 50 35 85t85 35h160v80ZM320-440v-80h320v80H320Zm200 160v-80h160q50 0 85-35t35-85q0-50-35-85t-85-35H520v-80h160q83 0 141.5 58.5T880-480q0 83-58.5 141.5T680-280H520Z"` | Kettenglied = Objekt ↔ Gruppenadresse | `device_hub` (trägt die Kommunikationsobjekte), `call_split` (Branch) | „Intelligente Verknüpfung…" → „Verknüpfen" |
| 15 | Parameter… (Gerätekontext) | `tune` | `d="M440-120v-240h80v80h320v80H520v80h-80Zm-320-80v-80h240v80H120Zm160-160v-80H120v-80h160v-80h80v240h-80Zm160-80v-80h400v80H440Zm160-160v-240h80v80h160v80H680v80h-80Zm-480-80v-80h400v80H120Z"` | Schieberegler = Geräteparameter einstellen | `settings` (Navigation), `equalizer` (Audio) | „Geräteparameter…" → „Parameter…" |
| 16 | Kommunikationsobjekte (Gerätekontext) | `device_hub` | `d="M120-120v-200h160l160-160v-128q-36-13-58-43.5T360-720q0-50 35-85t85-35q50 0 85 35t35 85q0 38-22 68.5T520-608v128l160 160h160v200H640v-122L480-402 320-242v122H120Z"` | ein Knoten, drei Strahlen = ein Objekt, viele Verknüpfungen | `cable` (physische Leitung), `call_split` | keiner (Fachbegriff bleibt Wort) |
| 17 | Linie (Baumzeile) | `lan` | `d="M120-80v-280h120v-160h200v-80H320v-280h320v280H520v80h200v160h120v280H520v-280h120v-80H320v80h120v280H120Zm280-600h160v-120H400v120ZM200-160h160v-120H200v120Zm400 0h160v-120H600v120ZM480-680ZM360-280Zm240 0Z"` | zentrale Linie mit drei Abzweigen = Linie mit Geräten | `linear_scale` (Schieberegler), `timeline` (Verlauf), `route` (Weg) | Glyphe `≡` |
| 18 | Gerät (Baumzeile) | `developer_board` | `d="M160-120q-33 0-56.5-23.5T80-200v-560q0-33 23.5-56.5T160-840h560q33 0 56.5 23.5T800-760v80h80v80h-80v80h80v80h-80v80h80v80h-80v80q0 33-23.5 56.5T720-120H160Zm0-80h560v-560H160v560Zm80-80h200v-160H240v160Zm240-280h160v-120H480v120Zm-240 80h200v-200H240v200Zm240 200h160v-240H480v240ZM160-760v560-560Z"` | Platine mit Anschlüssen = Gerät im Bus | `memory` (RAM), `router` (IP-Gerät), `perm_device_information` (Telefon) | Glyphe `◈` |

---

## 3. Teufelsadvokat, je Icon

Jedes Icon bekommt hier den stärksten Einwand, den ein erfahrener ETS-Nutzer vorbringen würde, plus die Milderung. Kein Einwand ist besiegt — jeder ist **eingepreist**.

### 3.1 `dashboard` (Projekt)
**Einwand:** Vier Kacheln sagen „Reporting-Startseite" oder „App-Menü", nicht „mein Projekt". Wer den Projektnamen sucht, klickt nicht auf Kacheln.
**Milderung:** Label „Projekt" steht direkt darunter, und der Bereich beginnt mit dem Projektnamen als H1. Das Icon ist Gliederung, nicht Bedeutungsträger.

### 3.2 `account_tree` (Topologie)
**Einwand:** Das Bild ist ein Organigramm. Es zeigt drei Ebenen, KNX hat vier (Installation, Bereich, Linie, Gerät/Segment) — und es sieht dem `device_hub` (16) ähnlich, das im selben Fenster vorkommt.
**Milderung:** `account_tree` ist ausschließlich Navigation, `device_hub` ausschließlich Gerätekontext; sie stehen nie in derselben Leiste. Das Label „Topologie" benennt den Ort unmissverständlich.

### 3.3 `format_list_bulleted` (Gruppenadressen)
**Einwand:** Drei Punkte mit Linien ist die allgemeinste Liste überhaupt — dieselbe Glyphe könnte auch Befunde, Objekte oder Einstellungen meinen. In diesem Fenster gibt es mindestens zwei weitere Listen.
**Milderung:** Feste Position in der Navigation, Label darunter. Regel: dieses Icon wird **nirgends sonst** verwendet; Befunde und Monitorlinien bekommen kein Icon (P10).

### 3.4 `library_books` (Katalog)
**Einwand:** Ein Buchstapel heißt „Handbuch/Hilfe/Dokumentation". Ein Nutzer, der die Anleitung sucht, landet im Produktkatalog — und ist enttäuscht.
**Milderung:** Es gibt in der App keine Hilfe und keine Doku-Fläche, also keinen Kollisionspartner. Das Label „Katalog" steht daneben. Alternative `inventory_2` (Kiste) wurde verworfen, weil eine Kiste „Archiv" sagt, nicht „Produktdatenbank".

### 3.5 `sensors` (Bus & Diagnose)
**Einwand:** Sendewellen sind das WLAN-Symbol. Ein Buskabel hat keine Antenne; wer Funkprobleme erwartet, sucht an der falschen Stelle. Zudem liegt es nah an `radar` (11).
**Milderung:** Beide Wellen-Icons erscheinen nie zusammen: `sensors` nur in der Navigation, `radar` nur in der Bus-Leiste. Label „Bus & Diagnose" macht den Ort eindeutig, der Zustand steht zusätzlich als Text in der Statusleiste.

### 3.6 `fact_check` (Prüfen)
**Einwand:** Eine Checkliste ist eine To-do-Liste — sie verspricht „noch zu erledigen", nicht „laufender Prüflauf". Und der Knopf „Prüfung starten" im selben Bereich braucht dasselbe Icon, was wie ein zweiter Ort wirkt.
**Milderung:** Wiederverwendung ist erlaubt (gleiche Bedeutung), Neuerfindung nicht. Der Knopf behält sein Wort „Prüfung starten"; das Icon zeigt nur, wohin er gehört.

### 3.7 `settings` (Einstellungen)
**Einwand:** Zahnrad und Schieberegler (`tune`, 15) bedeuten für die meisten dasselbe. Zwei Einstellungsbegriffe in einer App sind ein Klassiker für Fehlklicks.
**Milderung:** Harte Trennung nach Ebene: Zahnrad = App (Navigation, immer sichtbar), Regler = Gerät (nur im Gerätekontext). Nie in derselben Leiste, nie dieselbe Größe.

### 3.8 `undo` (Rückgängig)
**Einwand:** Ein Pfeil nach links heißt in neun von zehn Apps „zurück/zurücknavigieren". Ohne Beschriftung ist nicht klar, ob er den Bildschirm oder das Projekt zurückspult.
**Milderung:** Icon-only ist nur hier erlaubt (P7), immer mit Tooltip und Kürzel, immer paarweise mit 9, immer zusätzlich als Wort im Bearbeiten-Menü. Deaktiviert ist sichtbar ausgegraut — der häufigste Fehlklick („nichts passiert") wird so erklärt.

### 3.9 `redo` (Wiederherstellen)
**Einwand:** Spiegelbild von 8; im Eifer verwechselt man beide, besonders bei 16 px.
**Milderung:** Feste Reihenfolge (undo links, redo rechts), zwei verschiedene Tooltips, beide Kürzel (Strg+Z / Strg+Y). Kein weiteres Pfeil-Icon in der Leiste, damit die Paarigkeit auffällt.

### 3.10 `power` (Verbinden/Trennen)
**Einwand:** Der Stecker wird als „Gerät einschalten" oder „App beenden" gelesen. Schlimmer: ein Icon trägt zwei Bedeutungen, je nach Zustand — genau die Ambiguität, die Icons vermeiden sollen.
**Milderung:** Ein Zustand, ein Knopf: die Beschriftung wechselt zwischen „Verbinden" und „Trennen", der Statusbar-Text sagt „Verbunden: <Schnittstelle>" bzw. „Nicht verbunden". Kette (`link`) wurde bewusst freigehalten, weil „Verknüpfung" in dieser App bereits Gruppenadresse ↔ Objekt bedeutet.

### 3.11 `radar` (Geräte suchen)
**Einwand:** Radar = Überwachung/Ortung. Es liegt semantisch neben dem Busmonitor und optisch neben `sensors`; ein Nutzer könnte erwarten, dass es den Monitor startet.
**Milderung:** Nur sichtbar, solange keine Verbindung steht (Discovery ist der Schritt davor), nie neben dem Monitor. Tooltip und Menü sagen „Geräte suchen". Die Lupe bleibt dem Katalogfilter vorbehalten.

### 3.12 `fiber_manual_record` (Aufzeichnung starten)
**Einwand:** Der Outlined-Satz zeichnet einen **leeren Ring**, keinen gefüllten Punkt. Bei 16 px sieht das aus wie ein Optionsfeld (`radio_button_checked` ist fast identisch) — der Nutzer erwartet eine Auswahl, keine Aufnahme.
**Milderung:** Nur in der Diagnose-Leiste, immer direkt neben dem Stop-Quadrat, im Aufnahmezustand rot (`EtsError`) und mit Label/Tooltip „Aufzeichnung starten". Kein anderer Ring in der App (keine Radio-Optik in Leisten). Wenn das im Test trotzdem missverstanden wird: gefüllte Variante nur für dieses eine Icon, dann aber als bewusste, dokumentierte Ausnahme.

### 3.13 `stop` (Aufzeichnung beenden)
**Einwand:** Ein leeres Quadrat ist schwach — es könnte „Stoppuhr pausiert", „Auswahl aufheben" oder ein Platzhalter-Tofu sein. Es trägt die Bedeutung nur durch den Nachbarn.
**Milderung:** Untrennbar vom Ring (12), gleiche Leiste, gleiche Größe, sich gegenseitig ausschaltend (`IsEnabled` ist bereits so verdrahtet). Tooltip „Aufzeichnung beenden"; im Menü steht der volle Satz.

### 3.14 `link` (Verknüpfen)
**Einwand:** Kettenglied = Hyperlink/URL. In einem Engineering-Tool erwartet man darunter „öffne Dokument/Webseite", nicht „verbinde Datenpunkt mit Adresse".
**Milderung:** Nur im Gerätekontext (Objektliste, Kommunikationsobjekte-Dialog), nie global. Label „Verknüpfen". Es gibt in der App keine URLs und keine Dokumentlinks, also keinen echten Kollisionspartner.

### 3.15 `tune` (Parameter…)
**Einwand:** Schieberegler = Audio-Equalizer oder Ansichtsfilter. In Werkzeugleisten bedeutet dasselbe Symbol oft „Filter/Ansicht anpassen".
**Milderung:** Ausschließlich im Gerätekontext, nie in einer Filterzeile oder Liste. Label „Parameter…"; die Langform „Geräteparameter" bleibt im Menü und im Fenstertitel.

### 3.16 `device_hub` (Kommunikationsobjekte)
**Einwand:** Drei Knoten um ein Zentrum liest man als Netzwerk-Hub oder als halbe Topologie — die Verwechslung mit `account_tree` (2) ist naheliegend, und `cable` wäre physikalisch ehrlicher.
**Milderung:** Nie in der Navigation, nur gerätelokal (Eigenschaften-Panel, Kontextmenü, Katalogdetails). Tooltip und Label „Kommunikationsobjekte" — der Fachbegriff bleibt ausgeschrieben, weil er nicht abkürzbar ist. `cable` wurde verworfen, weil eine Leitung die Topologie meint, nicht die Objektliste.

### 3.17 `lan` (Linie)
**Einwand:** Das Bild ist ein IT-Netzwerkschema. Ein KNX-Nutzer könnte es für „IP-Schnittstelle" halten — und ausgerechnet die Schnittstelle steht in den Einstellungen.
**Milderung:** Es erscheint ausschließlich als Baumzeilenglyphe in `EtsMuted` neben dem Wort „Linie 1.1"; der Text bleibt der Bedeutungsträger, das Icon nur der Scanner. Kein Einsatz in Navigation oder Leisten.

### 3.18 `developer_board` (Gerät)
**Einwand:** Eine Platine heißt „Mikrocontroller/CPU", nicht „KNX-Gerät". Und eine Spalte mit Wiederholungsglyphen in einem dichten Baum ist Rauschen: Die Einrückung sagt die Ebene schon, die Adresse sagt den Typ.
**Milderung:** Nur auf Gerätezeilen, 16 px, gedämpfte Farbe; Bereich, Installation und Segment bekommen **kein** Icon (die Ebene ist an Einrückung und Adressformat ablesbar). Wenn der Baum dadurch unruhig wirkt, ist die richtige Antwort, die Glyphenspalte **ganz** zu löschen — nicht, sie auf vier Ebenen auszubauen.

---

## 4. Navigation

### 4.1 Vorschlag: sieben Ziele, Icon + Label, waagerechte Leiste

Die Ribbon-Zeile (7 Gruppen, 21 Knöpfe) wird durch eine 44 px hohe Zielleiste ersetzt; jedes Ziel ist Icon (20 px) + Label (11 px), aktives Ziel in `EtsAccent` mit Unterstrich.

| Ziel | Icon | Inhalt heute |
|---|---|---|
| Projekt | `dashboard` | Reiter „Dashboard": Kennzahlen, Geräte pro Linie, häufigste DPT |
| Topologie | `account_tree` | linker Baum + Eigenschaften |
| Gruppenadressen | `format_list_bulleted` | Reiter „Gruppenadressen" |
| Katalog | `library_books` | `CatalogWindow` (3 Spalten) |
| Bus & Diagnose | `sensors` | „Busmonitor" + „Recorder" + Verbindung + Discovery |
| Prüfen | `fact_check` | Reiter „Projektprüfung" + `ReportWindow` |
| Einstellungen | `settings` | `SettingsWindow` + zuletzt geöffnete Projekte |

**Nicht Ziele:** Geräteparameter, Kommunikationsobjekte (gerätelokal), Projektvergleich, Export, Produkttausch (Befehle), Gruppenadress-Import (Befehl). Ein Ziel ist etwas, das man länger als einen Dialog offen hat.

### 4.2 Warum der Katalog ein Hauptziel verdient

1. **Er ist der häufigste Bearbeitungsschritt.** Gerät hinzufügen heißt: Katalog → Hersteller → Produkt → Linie → einfügen. Heute liegt er drei Klicks tief unter *Extras → Katalogbibliothek…* — für die Tätigkeit, die man beim Projektausbau am öftesten macht.
2. **Er ist kein Dialog.** Das Fenster ist ein Dreispalter mit Suche, Rubrikfilter, Ergebnisliste und Detailspalte (1080 × 700). Das ist ein Arbeitsbereich, kein Bestätigungsfenster.
3. **Er ist das Datenfundament.** Ohne eingelesene `knxprod`-Kataloge gibt es keine Geräte, keine Parameter, keine Objekte. Die App lebt von der offline geparsten Produktdatenbank — das gehört nach vorn, nicht unter „Extras".
4. **Er ist der Einstieg in die Reihenfolge der Arbeit:** Projekt → Topologie → Katalog → Verknüpfen → Prüfen. Vier der sieben Ziele sind damit Schritte einer Linie, nicht eine Sammlung von Werkzeugen.

**Teufelsadvokat dazu:** Ein volles Ziel kostet einen von sieben Plätzen; ein „+ Gerät"-Knopf, der den Katalog als Dialog öffnet, wäre billiger und näher am heutigen Weg. Dagegen: Der Katalog wird **durchsucht und verglichen** (Hersteller, Rubrik, Bestellnummer, Programmanzahl), nicht nur „gepickt". Wer 40 Produkte eines Herstellers vergleicht, will kein modales Fenster über seinem Projekt.

### 4.3 Was aus dem heutigen Aufbau verschwindet

- der komplette Ribbon-Block (`MainWindow.axaml` Zeilen 64–148) samt 7 Gruppentiteln,
- die Reitertitel `Dashboard`, `Gruppenadressen`, `Projektprüfung` (wandern in die Navigation), `Busmonitor`/`Recorder` (werden zwei Panes unter *Bus & Diagnose*),
- der Panel-Header „Topologie" (doppelt das Navigationsziel),
- die Glyphen `⌂ ▣ ▢` im Baum (bleiben als leere Spalte für die Ausrichtung, gefüllt nur von `lan` und `developer_board`).

**Was Text bleibt und warum:** Menüleiste (Rückfallebene, P9), Exportformate (das Wort *ist* das Format), „Download (Image)…" (schreibt Gerätezustand), „Löschen" (zerstörend), „Senden"/„Lesen" (buswirksam und ohne Wort nicht unterscheidbar), „Produkttausch"/„Austauschen" (ändert viele Geräte), „Import" (Richtung am Icon nicht erkennbar), „Monitor leeren", „Prüfung starten", die drei Dateibefehle „Neu/Öffnen/Speichern" (drei kurze Wörter, null Mehrdeutigkeit — ein Disketten-Symbol neben einem Download-Pfeil wäre der schlechtere Tausch), sowie alle Dialogverben.

---

## 5. Endgültiges Minimum-Set (18)

| # | Icon | Label / Tooltip (deutsch) | Darstellung |
|---|---|---|---|
| 1 | `dashboard` | Projekt — Übersicht | Icon + Label |
| 2 | `account_tree` | Topologie | Icon + Label |
| 3 | `format_list_bulleted` | Gruppenadressen | Icon + Label |
| 4 | `library_books` | Katalog | Icon + Label |
| 5 | `sensors` | Bus & Diagnose | Icon + Label |
| 6 | `fact_check` | Prüfen | Icon + Label |
| 7 | `settings` | Einstellungen | Icon + Label |
| 8 | `undo` | Rückgängig (Strg+Z) | icon-only, Tooltip |
| 9 | `redo` | Wiederherstellen (Strg+Y) | icon-only, Tooltip |
| 10 | `power` | Verbinden / Trennen | Icon + wechselndes Label |
| 11 | `radar` | Geräte suchen | Icon + Tooltip |
| 12 | `fiber_manual_record` | Aufzeichnung starten | Icon + Tooltip |
| 13 | `stop` | Aufzeichnung beenden | Icon + Tooltip |
| 14 | `link` | Verknüpfen | Icon + Label |
| 15 | `tune` | Parameter… | Icon + Label |
| 16 | `device_hub` | Kommunikationsobjekte | Icon + Label |
| 17 | `lan` | Linie (Baumzeile 16 px) | Glyphe + Text |
| 18 | `developer_board` | Gerät (Baumzeile 16 px) | Glyphe + Text |

**Bewusst ohne Icon** (jedes mit Grund, damit es nicht als Vergessen gelesen wird):
Download (Image) · Löschen (Gerät, Gruppenadresse) · Senden · Lesen · Import · alle Exportformate · Produkttausch · Projektvergleich · Monitor leeren · Prüfung starten · Neu/Öffnen/Speichern · Anlegen/Abbrechen/Übernehmen/Schließen/Vorschau · `+`/`+ Gerät` (das Plus ist schon ein Symbol) · alle Menüeinträge · KPI-Kacheln und Panel-Header.

---

## 6. Umsetzung (Notizen, kein Code in diesem Schritt)

**Ablage.** Jeder Pfad wird eine `StreamGeometry`-Ressource in `App.axaml` (`Icon.Project`, `Icon.Topology`, …). Die Quellgeometrie liegt im 24-px-Grid mit `viewBox 0 -960 960 960`; da es reine Füllpfade sind, genügt `<Path Stretch="Uniform" Fill="{DynamicResource EtsText}" Data="{StaticResource Icon.Save}"/>` in einem 16- oder 20-px-Kasten. Avalonia normalisiert die Bounds selbst — kein `Viewbox`, kein Font, keine neue Abhängigkeit. 18 Pfade sind zusammen ca. 8 KB XAML.

**Stile.** Zwei Selektoren (`Path.icon` 20 px, `Path.icon.small` 16 px) plus Zusatzklassen `.nav`, `.active`, `.disabled`; Farbe ausschließlich über `Fill`, nie über `Stroke`.

**Barrierefreiheit.** Jedes icon-only-Ziel braucht `AutomationProperties.Name` mit demselben deutschen Text wie der Tooltip. Icon+Label-Ziele brauchen keins.

**Lizenz.** Material Symbols, Apache-2.0. In `NOTICE`/`README` eine Zeile mit Quelle und Abrufdatum; kein Font-File, kein NuGet-Paket.

**Reihenfolge der Umsetzung.** (1) Geometrie-Ressourcen + Stile, (2) Zielliste statt Ribbon, (3) Bus-Leiste, (4) Gerätekontext, (5) Baumglyphen ersetzen, (6) alte Glyphen-Strings `⌂▣≡▢◈` aus `MainWindowViewModel.cs` entfernen.

**Prüfregel.** Nach dem Umbau muss jeder der 18 Namen genau einmal als Ressource existieren, und jede der 18 Ressourcen muss mindestens einmal verwendet werden. Alles andere ist totes Gewicht.

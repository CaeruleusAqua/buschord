# Icon-Review (Teufelsadvokat) — BusChord

- Branch: `agents/ux/u4-icons-review`, Worktree `agent-wt/ux-u4`
- Basis: master `b9164c8` (bei Auftragsbeginn `ca509dc`; `b9164c8` kam während der Arbeit dazu)
- Eingabe: `agents/ux/u3-icons:analysis/ux/icon-map.md`
- Datum: 18.10.2026
- Ergebnis: **12 von 18 übernommen, 2 ersetzt, 4 gestrichen.** Endset: **14 Icons**, davon **1 bereits im Repo**.
- Prüfmethode: alle 18 `d`-Werte am 18.10.2026 erneut von
  `fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/<name>/default/24px.svg`
  geholt (alle byte-gleich zum Vorschlag), Silhouetten bei 18 und 24 px verglichen,
  Abgleich mit dem echten Repo-Stand (`Icons.axaml`, `Icon.cs`, `Localization.cs`,
  `MainWindowViewModel.cs`) und mit `analysis/ux/ets-help-ia.md`.

Dieses Dokument ist eine Ablehnung mit Begründung, keine Bestätigung. Wer die
Tabelle nur überfliegt, soll an den **fett gedruckten** Sätzen hängen bleiben.

---

## 0. Vier Sachfehler, bevor über Geschmack geredet wird

**0.1 „Keine Assets im Repo" ist seit `426f874` falsch.** Auf master liegen
`src/Knx.App/Assets/Icons.axaml` (5 `StreamGeometry`-Ressourcen `Icon.FolderOpen`,
`Icon.NoteAdd`, `Icon.Save`, `Icon.Search`, `Icon.Category`), `Views/Icon.cs`
(eigenes `Icon`-Control) und ein Test (`tests/Knx.Icon.Tests`). Der Vorschlag
plant 18 neue Pfade und „8 KB XAML" — richtig sind **13 neue** Ressourcen, weil
`Icon.FolderOpen` den Vorschlag Nr. 1 ersetzt (s. u.) und `Icon.Search` schon
existiert. `library_books` (Nr. 4) und `Icon.Category` wären zwei Katalog-Bilder
im selben Fenster — `Category` sollte dem Rubrikfilter gehören, nicht dem Ziel.

**0.2 Der Vorschlag bricht seine eigene Regel P7.** P7 erlaubt icon-only
„nur Rückgängig und Wiederherstellen … höchstens zwei gleichzeitig". Die
Endtabelle §5 führt aber **fünf** Ziele ohne Label: Nr. 8, 9 (P7-konform) und
Nr. 11, 12, 13 („Icon + Tooltip"). Ein Tooltip ist kein Label: er erscheint erst
nach 400–700 ms und nur mit Maus. Wer die Tastatur benutzt oder den Zeiger schon
bewegt hat, sieht nur das Bild.

**0.3 Zwei Milderungen sind durch den aktuellen Stand widerlegt.**
- Nr. 5/11: „beide Wellen-Icons erscheinen nie zusammen" — die Navigation ist
  global, die Bus-Leiste sitzt im selben Fenster, eine Leiste tiefer. Sie
  erscheinen sehr wohl zusammen.
- Nr. 4: „es gibt in der App keine Hilfe und keine Doku-Fläche" — `b9164c8`
  (Schwesteraufgabe u1) legt genau eine Hilfe-IA an (`analysis/ux/ets-help-ia.md`,
  Hilfe als Ort auf dem Dashboard). Der Kollisionspartner ist also nicht
  hypothetisch, er ist in Arbeit.

**0.4 Der Vorschlag beschreibt die Rendertechnik falsch.** `Icon.cs` zeichnet die
Geometrie im 960er-Kasten, Vorgabe **18 px**, Farbe über **`Foreground`**, nicht
über `Fill`; es gibt keinen `Path`-Stil mit 16/20 px. P3 („16 px Baum, 20 px
Leiste") und P5 („`Fill`") beschreiben einen Code, den es nicht gibt. Für
`Foreground`-Zustände (aktiv/inaktiv/deaktiviert) braucht es zusätzlich einen
`:disabled`-Style — der Vorschlag erwähnt ihn nicht.

Diese vier Punkte ändern keine einzige Icon-Entscheidung, aber sie zeigen, dass
die Karte nicht gegen den Code gelesen wurde. Wer sie umsetzt, merkt es beim
ersten Commit.

---

## 1. Entscheidungstabelle (alle 18)

„Wer leidet" ist die Rolle, die den Fehler bezahlt: **Inbetriebnehmer** (auf der
Baustelle, unter Zeitdruck), **Planer** (im Büro, projektiert), **Umsteiger**
(kommt von ETS6).

| # | Icon | Verdikt | Stärkster Einwand (wer leidet) | Milderung / Ersatz |
|---|---|---|---|---|
| 1 | `dashboard` | **ERSETZT** durch `Icon.FolderOpen` | In ETS6 heißt „Dashboard" der Startbildschirm **vor** dem Projekt (Projektliste, Hilfe, Einstellungen — `ets-help-ia.md` §2.1). Vier Kacheln über dem Label „Projekt" versprechen also genau den Ort, den ETS6 anders nennt. **Der Umsteiger sucht nach dem Öffnen die Projektliste und bekommt Kennzahlen.** | Ordner = Projektdatei (`.knxproj`), deckt sich mit ETS; Ressource ist schon da, 0 neue Assets. Label „Projekt". `dashboard` nur, wenn das Label „Übersicht" hieße — dann aber nie neben dem ETS-Dashboard. |
| 2 | `account_tree` | **KEEP** | Organigramm, nicht Anlage: es zeigt zwei Kinder, KNX hat unter dem Projekt drei Ebenen (Bereich → Linie → Gerät). Und es ist dasselbe Regal wie `lan` (Kästchen + rechtwinklige Linien): **der Inbetriebnehmer sieht zwei fast gleiche Baum-Bilder für zwei verschiedene Ebenen** und verwechselt Linie und Bereich in der Navigation. | `lan` entfällt (Nr. 17) — damit bleibt genau **ein** Baum-Bild in der App, und es ist ein Ort, keine Zeile. Nur 20 px, nur Navigation, Label „Topologie". |
| 3 | `format_list_bulleted` | **KEEP** | Die generischste Glyphe des Sets: in jedem Texteditor „Aufzählung formatieren", im selben Fenster konkurrieren drei weitere Listen (Befunde, Telegramme, Objekte). **Der Planer, der „meine Adressen" sucht, erhält keinen Hinweis, dass es hier um Gruppenadressen geht** — der Scannerwert ist nahe null, der Schaden auch. | Ausschließlich Navigation; nie in Liste, Filterzeile oder Dialog; kein zweites Listen-Icon in der App (Befunde/Telegramme bleiben Wort). Label „Gruppenadressen". |
| 4 | `library_books` | **KEEP** (Auflage) | Bücherstapel = Handbuch/Hilfe. Die Milderung („keine Hilfe-Fläche") ist seit `b9164c8` falsch. **Der Umsteiger, der die Anleitung sucht, landet im Produktkatalog** — Zeitverlust, kein Schaden. | Label „Katalog"; ein künftiger Hilfe-Ort bekommt `help`/`support`, **niemals ein Buch**; `Icon.Category` bleibt exklusiv der Rubrikfilter, sonst stehen im Katalogfenster zwei Katalog-Bilder. |
| 5 | `sensors` | **ERSETZT** durch `Icon.Cable` | Zwei Wellen links und rechts = **WLAN/Funk**. KNX RF ist ein reales, anderes Medium. **Der Inbetriebnehmer liest „Funkdiagnose" und sucht den Fehler am falschen Medium** — das ist der teuerste Einwand dieser Tabelle, weil er auf der Baustelle Stunden kostet. Dazu: bei 20 px ist es dem `radar` zum Verwechseln ähnlich, und die Milderung „nie zusammen sichtbar" ist falsch (0.3). | `cable` = Busleitung mit zwei Steckern (Pfad in §6 verifiziert), Label „Bus & Diagnose". Kein zweites Wellen-Icon mehr im Set. |
| 6 | `fact_check` | **KEEP** (Auflage) | Das Häkchen im Kasten sagt „erledigt/bestanden"; das Ziel heißt „Prüfung starten". **Der Planer hält die Projektprüfung für gelaufen und gibt ohne Befundbericht ab** — falsche Sicherheit, die erst auf der Baustelle auffällt. | Label „Prüfen"; Icon **nie** in Erfolgsfarbe, nur `EtsMuted`/`EtsText`; das Ergebnis („0 Fehler" / „3 Befunde") steht als Text daneben. Nie in derselben Leiste wie `Icon.NoteAdd` — beides beschriftete Dokumente, bei 18 px fast gleich. |
| 7 | `settings` | **KEEP** | Zahnrad und Regler (`tune`) sind zwei Einstellungsbilder in einer App. **Der Umsteiger sucht Geräteparameter hinter dem Zahnrad** und landet in den App-Optionen. | Harte Ebenentrennung: Zahnrad = App (nur Navigation, 20 px), Regler = Gerät (nur Gerätekontext, 18 px), Labels „Einstellungen" vs. „Parameter…". Nie in einer Leiste. |
| 8 | `undo` | **KEEP**, icon-only | Pfeil nach links heißt in fast jeder App „zurück". Hier gibt es keine Zurück-Navigation, aber einen Baum: **der Inbetriebnehmer erwartet, eine Ebene hochzugehen, und rollt stattdessen eine Parameteränderung zurück.** | Tooltip „Rückgängig (Strg+Z)", gleicher Text als `AutomationProperties.Name`, ohne Verlauf deaktiviert und sichtbar ausgegraut, 20 px, nie neben einem Navigationspfeil. |
| 9 | `redo` | **KEEP**, icon-only | Reines Spiegelbild; die Richtung ist bei 18 px das einzige Merkmal. **Ein Fehlklick stellt eine gerade verworfene Änderung wieder her, ohne dass der Nutzer sie sieht** — bei laufender Inbetriebnahme weiß er danach nicht mehr, welcher Stand im Projekt steht. | Feste Reihenfolge, ≥ 8 px Abstand, zwei Tooltips mit Kürzel, ohne Redo-Verlauf deaktiviert; beide nur 20 px, nie 16. |
| 10 | `power` | **DROP** | Der Pfad ist das **IEC-Netzsymbol** (Kreis mit Strich), nicht der im Vorschlag behauptete „Stecker" — die Form sagt „Ein/Aus", nicht „Verbinden". **Ein Glyph mit zwei Labels kippt seine Bedeutung mit dem Zustand**, genau die Ambiguität, die Icons vermeiden sollen; der Nutzer kann es als „Schnittstelle abschalten" lesen. | Wörter „Verbinden"/„Trennen" behalten (je ein Wort, null Mehrdeutigkeit), Statuszeile trägt den Zustand („Verbunden mit …"). Kein Ersatz-Icon. |
| 11 | `radar` | **KEEP** (mit Label) | Radar = Ortung/Überwachung; **der Inbetriebnehmer erwartet den Busmonitor** und bekommt eine Gerätesuche. Zusätzlich ist die Milderung „nur sichtbar, solange keine Verbindung steht" fachlich falsch: Discovery in `MainWindowViewModel.DiscoverAsync` läuft über `Settings.BusInterface` und wird **nach** jedem Geräteneustart erneut gebraucht. | Sichtbares Label „Geräte suchen" (2 Wörter), Knopf bleibt sichtbar, solange eine Schnittstelle konfiguriert ist — nie zustandsabhängig verstecken. Ergebnis als Text („3 Geräte gefunden"). |
| 12 | `fiber_manual_record` | **KEEP** (nur im Paar, mit Label) | Der Outlined-Pfad zeichnet einen **hohlen Ring** — die Form eines Optionsfelds (○), nicht eines Aufnahmepunkts (●). Bei 18 px ohne Rot liest der Nutzer „Auswahl". **Der Inbetriebnehmer startet die Aufnahme einer Störung nicht (Beweisverlust) oder beendet sie nicht (Datei wächst).** | Label „Start", immer direkt neben `stop`, 20 px, Rot nur im Aufnahmezustand, Laufzeit als Text („REC 00:12"). **Keine gefüllte Variante als Ausnahme** — das bricht P4 und die Familie. |
| 13 | `stop` | **KEEP** (nur im Paar, mit Label) | Leeres Quadrat = Platzhalter, Tofu oder Pause; es trägt Bedeutung nur durch den Nachbarn. **Der Nutzer lässt eine Aufnahme weiterlaufen, weil das Quadrat wie ein deaktivierter Knopf aussieht.** | Label „Stop", direkt neben dem Ring, gleiche Größe, sich gegenseitig ausschaltend; Tooltip „Aufzeichnung beenden". |
| 14 | `link` | **KEEP** | Kettenglied = Hyperlink/URL. Die Kollision ist schwach, weil die App keine URLs öffnet, aber **der Umsteiger erwartet „Dokument öffnen"** und bekommt einen Verknüpfungsdialog. | Nur Gerätekontext (Objektliste, Kommunikationsobjekte), Label „Verknüpfen"; nie neben dem Katalog. |
| 15 | `tune` | **KEEP** | Regler = Filter/Ansichtsanpassung. **Landet es je in einer Tabellenkopfzeile, liest der Planer es als Spaltenfilter und hält ausgeblendete Geräte für fehlend** — der gefährlichste Fall dieser Art, weil die Daten „weg" sind, ohne dass etwas kaputt ist. | Ausschließlich Gerätekontext/Properties, Label „Parameter…"; **nie** in Tabellen-, Listen- oder Filterzeile. |
| 16 | `device_hub` | **DROP** | Knoten mit drei Strahlen = Hub/Netzwerk; im selben Fenster wie `account_tree` liest man es als Topologie. Und „Kommunikationsobjekte" ist ein 22-Zeichen-Fachbegriff, den kein Glyph trägt: **das Icon ist Dekoration, das Label trägt alles.** Der Nutzer, der es für die Topologie hält, ist am falschen Ort; der, der den Fachbegriff sucht, erkennt ihn im Bild nicht wieder. | Wort bleibt (Fachbegriff, unübersetzbar kurz). Wenn die Eigenschaften-Leiste dadurch uneinheitlich wirkt: `tune` ebenfalls streichen, statt ein schlechtes Icon zu ergänzen. |
| 17 | `lan` | **DROP** | Das Bild ist ein **IP-Netzwerkschema**; „LAN" ist in dieser App die Schnittstelle (Einstellungen), nicht die KNX-Linie. **Der Inbetriebnehmer hält Linien für IP-Segmente und sucht die Schnittstelle an der falschen Stelle** — und in der Baumspalte steht ohnehin „Linie 1.1". | Keine Glyphe. Einrückung + Adresse tragen die Ebene; die heutigen Zeichen `⌂ ▣ ≡ ▢ ◈` werden ersatzlos gelöscht (Tofu-Risiko, gemischte Strichstärken). |
| 18 | `developer_board` | **DROP** | Platine = Mikrocontroller/Dev-Board; ein KNX-Aktor ist eine Reiheneinbaukomponente. Bei 18 px ist die Glyphe ein schraffierter Kasten: **in einem Baum mit 200 Zeilen reines Rauschen, das keine Information liefert, die die Adresse nicht schon liefert.** | Glyphenspalte ganz löschen (auch `TreeNode.Glyph` in `MainWindowViewModel.cs` 586–626). |

---

## 2. Icon-only-Budget: **zwei** von drei Plätzen

| Platz | Kontrolle | Warum genau diese | Bedingungen |
|---|---|---|---|
| 1 | Rückgängig | weltweit erwartetes Bild, paarweise, in jedem Editor an derselben Stelle; der Verlust (ein Klick) ist durch Redo gedeckt | Tooltip „Rückgängig (Strg+Z)", `AutomationProperties.Name`, ohne Verlauf deaktiviert, 20 px, nie allein |
| 2 | Wiederherstellen | Spiegel dazu; nur im Paar lesbar | wie 1, „Wiederherstellen (Strg+Y)", fester Platz rechts |
| 3 | **frei** | **bewusst nicht vergeben.** Ein Budget ist eine Obergrenze, keine Quote. | — |

**Der einzige Anwärter auf Platz 3 und warum er ihn nicht bekommt:** die
Aufzeichnung. Ring und Quadrat sind die stärksten Kandidaten (Media-Konvention),
aber der Fehlerfall ist teuer und unsichtbar — keine Aufnahme heißt kein Beweis
für die Störung, und ein leerer Ring wird als Optionsfeld gelesen. Dort bleibt
das Label. Wer Platz 3 später beansprucht, muss einen Fehlklick nennen, der
nichts kostet; „ist hübsch" zählt nicht.

---

## 3. Endgültiges Set (14)

Alle Labels sind sichtbar. Tooltips sind überall ≤ 3 deutsche Wörter; bei den
zwei icon-only-Zielen ist der Tooltip der einzige Träger und deshalb mit Kürzel.

| Ressource | Deutsches Label | Ort | Größe | Tooltip |
|---|---|---|---|---|
| `Icon.FolderOpen` *(vorhanden)* | Projekt | Navigation | 20 px | — (Label) |
| `Icon.AccountTree` | Topologie | Navigation | 20 px | — |
| `Icon.FormatListBulleted` | Gruppenadressen | Navigation | 20 px | — |
| `Icon.LibraryBooks` | Katalog | Navigation | 20 px | — |
| `Icon.Cable` | Bus & Diagnose | Navigation | 20 px | — |
| `Icon.FactCheck` | Prüfen | Navigation | 20 px | — |
| `Icon.Settings` | Einstellungen | Navigation, unten | 20 px | — |
| `Icon.Undo` | — (icon-only) | Werkzeugleiste | 20 px | Rückgängig (Strg+Z) |
| `Icon.Redo` | — (icon-only) | Werkzeugleiste | 20 px | Wiederherstellen (Strg+Y) |
| `Icon.Radar` | Geräte suchen | Bus-Leiste | 18 px | Geräte suchen |
| `Icon.FiberManualRecord` | Start | Bus-Leiste | 20 px | Aufzeichnung starten |
| `Icon.Stop` | Stop | Bus-Leiste | 20 px | Aufzeichnung beenden |
| `Icon.Link` | Verknüpfen | Gerätekontext | 18 px | Verknüpfen |
| `Icon.Tune` | Parameter… | Gerätekontext | 18 px | Geräteparameter |

Damit: **13 neue Ressourcen** in `Assets/Icons.axaml`, 1 Wiederverwendung,
0 neue Abhängigkeiten, 0 Assets. Die fünf vorhandenen Ressourcen bleiben:
`FolderOpen` (jetzt benutzt), `Search` (Katalogfilter), `Save`/`NoteAdd`/`Category`
unbenutzt, bis ein Ort sie wirklich braucht.

Zwei Umsetzungspflichten, die aus diesem Review folgen:

1. **18 px ist die Untergrenze für detailreiche Glyphen** (`Radar`, `Settings`,
   `FactCheck`, `Cable`): darunter laufen die 40/960-Stämme zu grauen Haarlinien
   aus. 16 px nur für einfache Silhouetten (Ring, Quadrat).
2. **Zustandsfarben über `Foreground`**, nicht `Fill` (siehe 0.4); deaktiviert
   braucht einen `:disabled`-Style mit 40 % Deckkraft, sonst sieht „nicht
   verfügbar" aus wie „verfügbar".
3. `AutomationProperties.Name` nur für die zwei icon-only-Ziele — bei
   Icon + Label wäre es eine Doppelansage für Screenreader.

---

## 4. Diese Icons werden **nicht** angelegt

| Icon | Grund in einem Satz |
|---|---|
| `dashboard` | ETS6 nennt so den Startbildschirm vor dem Projekt — der Name gehört nicht in ein Projekt hinein. |
| `sensors` | Wellen sagen Funk; KNX ist Draht, und KNX RF ist ein anderes Medium. |
| `power` | IEC-Ein/Aus, nicht Verbinden; zwei Labels auf einem Glyph. |
| `device_hub` | Hub-Bild für einen Fachbegriff, den kein Bild trägt. |
| `lan` | LAN ≠ KNX-Linie, und es ist das zweite Baum-Bild. |
| `developer_board` | Platine ≠ KNX-Gerät; in dichter Baumspalte nur Rauschen. |
| `fiber_manual_record` **allein** | ohne `stop` und ohne Label nicht lesbar. |
| `stop` **allein** | ohne Ring und ohne Label ein Platzhalter. |

Ebenfalls **nicht** ergänzen, obwohl sie im Repo liegen: `NoteAdd`, `Save`,
`Search`, `Category` — sie sind für spätere Orte reserviert, nicht für Icons, die
gerade niemand braucht.

**Nicht anfassen:** Menüleiste (textliche Rückfallebene), „Download (Image)…",
„Löschen", „Senden"/„Lesen", „Import", Exportformate, „Produkttausch",
„Austauschen", „Monitor leeren", „Prüfung starten", Dialogverben. Diese Wörter
tragen zerstörende oder buswirksame Bedeutung; ein Bild daneben macht sie nicht
kürzer, nur schneller falsch.

---

## 5. Regel für die Zukunft

> **Ein Icon ist gerechtfertigt, wenn alle vier Bedingungen halten. Fällt eine,
> bleibt das Wort.**
>
> 1. **Ort oder Zustand, kein Befehl.** Es markiert etwas, das der Nutzer in
>    einer Sitzung mehrfach ansteuert (Navigation, Status, Objekttyp) — nicht
>    einen einmaligen Befehl, ein Dialogverb oder ein Dateiformat.
> 2. **Erster Leseeindruck trifft das Label.** Wer die App nicht kennt, zeigt
>    auf die Glyphe ohne Label und sagt dasselbe deutsche Wort. „Sieht aus wie
>    X, heißt aber Y" ist eine Ablehnung, kein Kompromiss.
> 3. **Keine Doppelgänger bei 18 px.** Kein anderes Icon der App hat eine
>    ähnliche Silhouette (Kästchen-mit-Linien, Wellen, Dokument-mit-Symbol) und
>    keines bedeutet dasselbe. Geprüft wird die Silhouette, nicht der Name.
> 4. **Keine zerstörende oder buswirksame Aktion allein.** Download, Löschen,
>    Senden, Lesen, Produkttausch, Verbinden/Trennen behalten ihr Wort. Icons
>    dürfen schneller machen, nicht mutiger.

Zusatzregeln, die sich aus diesem Review ergeben:

- **Ein Name, ein Bild.** `Icon.X` wird an genau einer Bedeutung verwendet; wird
  dieselbe Bedeutung an zwei Orten gebraucht, ist Wiederverwendung erlaubt,
  Neuerfindung nicht.
- **Jedes Icon muss löschbar sein.** Nach dem Umbau: `grep` pro Ressource, genau
  eine Definition und ≥ 1 Verwendung. Ein Icon ohne Verwendung ist totes
  Gewicht und fliegt raus, nicht in die nächste Runde.
- **Kein Icon wird „für später" angelegt.** Die fünf vorhandenen Ressourcen sind
  der Beweis, dass das schiefgeht.

---

## 6. Verifizierter Pfad für den Ersatz

`cable`, abgerufen am 18.10.2026 von
`https://fonts.gstatic.com/s/i/short-term/release/materialsymbolsoutlined/cable/default/24px.svg`
(`viewBox="0 -960 960 960"`, reiner Füllpfad):

```text
M200-120q-17 0-28.5-11.5T160-160v-40h-40v-160q0-17 11.5-28.5T160-400h40v-280q0-66 47-113t113-47q66 0 113 47t47 113v400q0 33 23.5 56.5T600-200q33 0 56.5-23.5T680-280v-280h-40q-17 0-28.5-11.5T600-600v-160h40v-40q0-17 11.5-28.5T680-840h80q17 0 28.5 11.5T800-800v40h40v160q0 17-11.5 28.5T800-560h-40v280q0 66-47 113t-113 47q-66 0-113-47t-47-113v-400q0-33-23.5-56.5T360-760q-33 0-56.5 23.5T280-680v280h40q17 0 28.5 11.5T360-360v160h-40v40q0 17-11.5 28.5T280-120h-80Z
```

Form: eine senkrechte Leitung mit zwei Steckergehäusen (oben rechts, unten
links) — senkrecht, also keine Verwechslung mit dem waagerechten Kettenglied
`link`.

`Icon.FolderOpen` braucht **keinen** neuen Pfad: er steht bereits in
`src/Knx.App/Assets/Icons.axaml` (aus `426f874`) und ist byte-gleich mit der
Upstream-Datei.

---

## 7. Restrisiko (nicht weggeredet)

1. **Der Ring bleibt der schwächste Punkt.** `fiber_manual_record` (Outlined) ist
   ein hohler Ring. Mit Label und im Paar ist er tragbar; ohne beides nicht. Wenn
   ein Nutzertest ihn als Optionsfeld liest, ist die richtige Antwort **nicht**
   die gefüllte Variante (Familienbruch), sondern ein reiner Text-Knopf
   „Aufzeichnung starten".
2. **`account_tree` bleibt ein Organigramm.** Es ist der beste verfügbare
   Hierarchie-Glyph, aber es zeigt zwei Kinder, nicht drei. Das Label muss die
   Bedeutung tragen, nicht das Bild.
3. **Keine echte Nutzungsprüfung.** Diese Bewertung ist Text- und Formanalyse
   plus Domänenwissen, kein Test mit Inbetriebnehmern. Die zwei teuersten
   Annahmen (`fact_check` = „bestanden", `library_books` = „Hilfe") sollten in
   einem 5-Personen-Test mit genau einer Frage geprüft werden: „Was erwarten Sie
   hinter diesem Knopf?"
4. **Randbefund, außerhalb meines Auftrags:** `MainWindowViewModel` schreibt
   Statusmeldungen fest auf Deutsch („keine Geräte gefunden", „Verbunden mit …")
   statt über `Localization.cs`. Für Tooltip-Texte, die aus `Strings.Get`
   kommen sollen, ist das ein Bruch, den U4 nicht reparieren darf.

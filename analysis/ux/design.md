# BusChord: UX-/UI-Design

> Die zentrale flache Tab-Aufteilung unter D2/D3 ist eine BusChord-Entscheidung,
> kein ETS6-Nachweis. Der [ETS6-Gegencheck](ets6-panel-recheck.md) trennt
> Arbeitsfenster, Geräte-Unterreiter und Eigenschaften-Sidebar.

Stand: 2026-10-18. Grundlage: [ETS6-Hilfe und IA](ets-help-ia.md),
[Ist-Audit mit User Stories](current-ui-audit.md), [Icon-Vorschlag](icon-map.md)
und [Teufelsadvokat](icon-review.md). Kein Nachweis von ETS-Layoutgleichheit;
die ETS-Hilfe belegt Struktur und Begriffe, keine Pixel.

## 1. Was das Original tatsächlich macht

ETS6 hat **keinen Ribbon**. Belegt sind (Help-Center + de-DE-Ressourcen):

- ein **Dashboard** vor dem Projekt: Projektliste, Suche, Import/Export, Einstellungen, Hilfe;
- danach eine Projektansicht aus Menüleiste, **Panelauswahl**, Werkzeugleisten,
  Baum-/Listenansicht, **Sidebar** und Projekt-Tab;
- Arbeitsfenster (Panels): Gebäude, Gruppenadressen, Topologie, Ganzes Projekt,
  Geräte, **Kataloge**, Berichte, Monitore;
- Sidebar als Kontext, nicht als Navigation: Eigenschaften, Gruppen, Suchen,
  offene Aufgaben, Undo;
- Download und Diagnose sind **Menüaufgaben** (`Inbetriebnahme`, `Diagnose`),
  abhängig von der Auswahl — keine Dauerbuttons.

Daraus folgt für BusChord: Aufgabenorte müssen sichtbar sein, Kontext gehört in
eine rechte Leiste, und der Katalog ist ein **Arbeitsfenster**, kein Extras-Dialog.

## 2. Designentscheidungen

**D1 — Start ist ein Zustand, kein zweites Fenster.**
Ohne Projekt zeigt das Hauptfenster eine Startfläche: „Projekt öffnen" mit den
letzten Projekten, „Neues Projekt", „Gerät aus Katalog". Die leeren Panels
verschwinden. Kein Onboarding, keine Tour.

**D2 — Sieben Ziele statt sieben Ribbon-Gruppen.**
Navigation ist ein Ziel, keine Befehlsgruppe: Projekt, Topologie,
Gruppenadressen, Katalog, Bus & Diagnose, Prüfen, Einstellungen. Jedes Ziel hat
ein Icon **und** ein Wort. Befehle, die nur im Kontext gelten (Parameter,
Kommunikationsobjekte, Download), sitzen am ausgewählten Gerät. Das zentrale
Arbeitsfenster trägt die Tabs Übersicht, Gruppenadressen, Katalog, Parameter,
Kommunikationsobjekte und Prüfung; jedes Ziel wechselt den Tab statt ein Fenster
zu öffnen.

**D3 — Der Katalog ist primär.**
Er ist ein Tab des Arbeitsfensters und von der Startfläche, aus der
Werkzeugleiste, per `Strg+Shift+K` und aus dem Topologie-Kontext erreichbar —
auch ohne geladenes Projekt. „+ Gerät" legt kein produktloses Gerät mehr an,
sondern öffnet den Katalog mit vorbelegter Linie. Produktlose Geräte werden im
Baum als „ohne Produkt" markiert, bevor jemand Parameter öffnet.

**D4 — Drei Zonen, eine Sidebar.**
Links der Aufgabenbaum bzw. die Liste, in der Mitte der Arbeitsinhalt, rechts
Eigenschaften. Der Busmonitor bleibt unten und erscheint mit der Verbindung.
Damit sind zwei Sichten gleichzeitig sichtbar — die Voraussetzung für das
Verknüpfen von Objekten und Gruppenadressen.

**D5 — Icons nur für Ort, Zustand, Objekttyp.**
14 Icons, genehmigt nach Widerspruch (Details: [icon-review.md](icon-review.md)):
Projekt, Topologie, Gruppenadressen, Katalog, Bus, Prüfen, Einstellungen,
Rückgängig, Wiederherstellen, Suchen (Discovery), Aufnahme, Stop, Verknüpfen,
Parameter. **Nur Rückgängig und Wiederherstellen** stehen ohne Wort da, immer
paarweise, mit Tooltip und Kürzel. Wörter behalten: Neu, Öffnen, Speichern,
Download, Löschen, Senden/Lesen, Import, Export, alle Dialogverben. Ein Icon
rechtfertigt sich durch Wiedererkennung, nicht durch Platzersparnis; ein
zusätzliches Icon muss ein bestehendes verdrängen.

**D6 — Textbudget.**
Kein sichtbarer Satz über 80 Zeichen außer echten Fehlermeldungen. Keine
Implementierungsdetails (ZIP, Schema-URL, XML-Pfade) in der Oberfläche. Ein
Begriff pro Sache: „Katalog", „Parameter", „Kommunikationsobjekte",
„Projektprüfung". Lange Hinweise wandern in einen „?"-Aufklapper am Control.

**D7 — Rückmeldung am Ort des Klicks.**
Kein stiller No-Op. Ist eine Aktion nicht möglich, nennt das Control den Grund
und den nächsten Schritt: „Projekt öffnen", „Gerät wählen", „Schnittstelle
einrichten". Die Statuszeile bleibt für Hintergrundmeldungen, nicht als einziger
Fehlerkanal. Version/Build verschwinden daraus.

**D8 — Leere Zustände bieten die nächste Aktion an.**
„Noch keine Geräte" + Katalog, „Noch keine Gruppenadressen" + Anlegen,
„Keine Befunde" als Ergebnis statt als leere Liste.

## 3. Aufgabenwege nach dem Umbau

| Aufgabe | Weg | Tastatur |
|---|---|---|
| Letztes Projekt öffnen | Startfläche, ein Klick | `Strg+O` für Dateidialog |
| Gerät aus Katalog | Startfläche, Werkzeugleiste oder Topologie-Kontext | `Strg+Shift+K` |
| Gruppenadresse anlegen | „+" in der Liste, mit Namen | `Strg+G` |
| Objekt verknüpfen | Gerät wählen → Kommunikationsobjekte | — |
| Parameter | Gerät doppelklicken oder Eigenschaften → Parameter | — |
| Projekt prüfen | Ziel „Prüfen" | `F5` |
| Bus verbinden | Ziel „Bus & Diagnose" → Suchen → Verbinden | — |

## 4. User Stories und Abnahme

Die zwölf Stories mit prüfbaren Kriterien stehen im
[Audit](current-ui-audit.md#4-user-stories-sortiert-nach-häufigkeit-für-den-inbetriebnehmer).
Kurzfassung der Abnahme für diese Runde:

- Katalog ab Kaltstart in höchstens zwei Klicks und per Shortcut erreichbar.
- Ohne Projekt gibt es keinen einzigen stillen No-Op; jede gesperrte Aktion nennt
  den Grund.
- Zuletzt geöffnete Projekte sind auf der Startfläche, nicht nur in den Optionen.
- Gerätebezogene Aktionen (Parameter, Kommunikationsobjekte) sind ohne Auswahl
  deaktiviert und erklären warum.
- Kein sichtbarer Text über 80 Zeichen außer Fehlermeldungen.
- Icons: höchstens zwei ohne Label; jedes mit Tooltip und deutschem Begriff.

## 5. Bewusst nicht

Kein zweiter Katalog-Browser, keine zusätzlichen Dialogfenster für Arbeitsinhalte,
keine Onboarding-Tour, keine zusätzlichen Dashboard-Kacheln, keine
konfigurierbare Werkzeugleiste, keine Plugin-UI. Das Problem war Sichtbarkeit und
Textmenge, nicht fehlende Funktion.

## 6. Umsetzungsstand

Stand 2026-10-18.

- **Integriert:** Katalog (`CatalogPane`), Parameter (`ParameterPane`) und
  Kommunikationsobjekte (`GroupLinksPane`) sind Tabs des zentralen
  Arbeitsbereichs (`TabControl` „Workspaces"). Werkzeugleiste, Menü,
  Topologie-Kontext, Startfläche, `Strg+Shift+K` und Gerätedoppelklick wechseln
  nur den Tab.
- **Sichtbarkeit:** Das Arbeitsfenster erscheint bei geladenem Projekt oder wenn
  der Katalog ohne Projekt geöffnet wurde (`StartDismissed`); sonst bleibt die
  Startfläche.
- **Synchron:** Der Gerätewechsel lädt Parameter und Kommunikationsobjekte, ein
  Projektwechsel (Laden, Undo, Redo) lädt beide neu und aktualisiert die
  Katalog-Linien samt Adressvorschlag.
- **Entfernt:** `CatalogWindow`, `ParameterWindow`, `GroupLinksWindow`.
- **Bleiben Dialoge:** Einstellungen, Neues Projekt, Produkt ersetzen, Vergleich,
  Bericht — kurze modale Aufgaben ohne Arbeitskontext.
- **Startoptionen:** `--catalog` öffnet den Katalog-Tab im Hauptfenster;
  `--screen=catalog|parameters|links` fotografiert das Hauptfenster mit diesem
  Tab.

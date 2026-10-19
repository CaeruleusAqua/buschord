# ETS6: Tabs, Arbeitsfenster und Sidebar — unabhängiger Gegencheck

Stand: 2026-10-19. Investigation E1, keine Implementierung/Busaktionen.
Ausgangsstand geprüft: `f81c90e59a4b554eda07e360e12ef6df1c591687`.
Branch: `agents/etscheck/e1-evidence`; Worktree: `C:\projects\ai_work\agent-wt\etscheck-e1`.

## Ergebnis

**Ja, ETS6 hat Tabs. „Panels statt Tabs“ ist als grundsätzlicher Gegensatz falsch.**
ETS unterscheidet Projekt-/Ansichts-Tabs, darin Arbeitsfenster mit Panelauswahl,
kontextbezogene Reiter innerhalb deren Listenansicht und eine rechte Sidebar.
Auch ein einzelnes Arbeitsfenster kann in einem neuen Projekt-/Ansichts-Tab geöffnet
werden. Topologie ist ein Arbeitsfenster, Eigenschaften ist ein Sidebar-Container:
Die beiden sind also gerade nicht dieselbe Art von Seitenpanel.

## 1. Direkt dokumentiert: Quellen und genaue Belegstellen

Alle folgenden offiziellen KNX-Quellen wurden am 2026-10-19 erfolgreich gelesen.
Bei S4–S8 lieferte die Help-Center-API den Artikelkörper; wo die normale Webseite
403 lieferte, ist dies unten kenntlich gemacht. Zitate sind wörtliche Ausschnitte
aus dem Text/HTML-Artikelkörper, keine aus Binärnamen erschlossenen UI-Aussagen.

### S1 — Projekt-/Ansichts-Tabs

[Leisten & Ansichten – Übersicht](https://support.knx.org/hc/de/articles/360022026320),
Webseite erfolgreich; angezeigte Aktualisierung 2026-09-09.

> „Projekt-Registerkarte / Projekt-Tab“
>
> „Wenn mehrere Projekte gleichzeitig geöffnet sind, erscheint für jedes Projekt ein zusätzlicher Tab. Ein und dasselbe Projekt kann in mehreren, auch unterschiedlichen Ansichten dargestellt werden. Über die Reiter können die Ansichten umgeschaltet werden.“

Belegt sowohl mehrere Projekte als auch mehrere Ansichten desselben Projekts.

### S2 — Arbeitsfenster und Tab sind kombinierbar

[Panelauswahl](https://support.knx.org/hc/de/articles/360021517819), Webseite
erfolgreich; angezeigte Aktualisierung 2023-11-16.

> „Der Panel-Selektor (2) zeigt den Namen des Panels an und ermöglicht es, den Inhalt des Panels zu ändern.“
>
> „Die Aktion“ […] „öffnet das aktuell ausgewählte Panel in einer neuen Registerkarte.“
>
> „Da der Inhalt jedes Fensters individuell eingestellt werden kann, können mit dieser Funktion zwei oder mehr Fenster desselben Typs angezeigt werden.“

Die ausgelassene Stelle im zweiten Zitat ist ausschließlich das Aktionssymbol
`OpenAsNewTab.png`, kein Text. Zusätzlich dokumentiert: Öffnen als neues ETS-Fenster,
Verschieben und Schließen eines Panels. Daher kein Beleg für ein Tab-Verbot.

### S3 — Tab-Verwaltung

[Projekt-Reiter Verzeichnis](https://support.knx.org/hc/de/articles/9758911685906),
Webseite erfolgreich; angezeigte Aktualisierung 2026-10-15.

> „Jeder geöffnete Reiter wird im Projekt-Reiter Verzeichnis angezeigt.“
>
> „Wenn Sie einen Projekt-Reiter aus der Registerkarte herausziehen, wird eine neue ETS-Instanz (ein zweites ETS-Fenster) mit dem zuvor geöffneten Panel erstellt.“

Dokumentiert außerdem leere Reiter, Öffnen eines Projekt-/App-Panels, Zusammenführen
und Umordnen. Die Projekt-Tabs sind keine bloßen Geräteparameter-Reiter.

### S4 — Eindeutige Hierarchie: Panel → Tab → Fenster

[Listenansicht (Detailliert)](https://support.knx.org/hc/de/articles/12174382514834),
[erfolgreicher API-Abruf](https://support.knx.org/api/v2/help_center/de/articles/12174382514834.json),
`edited_at`: 2026-09-09.

Unter „Untermenü-Optionen öffnen“:

> „Öffnen in einem neuen Panel > Ergebnis: Öffnet ein neues Panel des gleichen Typs, in derselben Registerkarte, wobei das ausgewählte Element in der Baumansicht ausgewählt ist.“
>
> „Öffnen als neuen Tab > Ergebnis: Öffnet einen neuen Tab mit einem Panel desselben Typs im selben Fenster mit dem ausgewählten Element in der Baumansicht.“
>
> „Öffnen als neues Fenster > Ergebnis: Öffnet ein neues Fenster, enthält eine Registerkarte mit einem Panel desselben Typs, wobei das ausgewählte Element in der Baumansicht ausgewählt ist.“

Diese Gegenüberstellung löst die begriffliche Frage direkt: mehrere Panels in
einem Tab und ein Panel in einem neuen Tab sind beide vorgesehen.

### S5 — Untergeordnete Geräte-/Funktionsreiter

[Listenansicht](https://support.knx.org/hc/de/articles/360021517859), Webseite 403,
[API erfolgreich](https://support.knx.org/api/v2/help_center/de/articles/360021517859.json),
`edited_at`: 2026-09-07.

> „Die Arbeitsbereiche (Gebäude, Topologie, etc.) haben eine Listenansicht auf der rechten Seite.“
>
> „Die Listenansicht besitzt mehrere Ebenen (oder Reiter), die Sie über an der oberen linken Ecke auswählen können.“
>
> „Die erste Registerkartebene hat immer einen 'inhaltsbezogenen' Namen (z.B. Kommunikationsobjekte, wenn ein Gerät in der Baumansicht ausgewählt ist). Über die zusätzlichen Tabs 2...n können Sie zwischen Funktionen wechseln (siehe Bild oben). Dies ist z.B. notwendig, um die Geräteparameter auf der Registerkarte Parameter zu setzen.“

„Rechte Seite“ meint hier die Liste innerhalb des Arbeitsfensters, nicht die Sidebar.

### S6 — Parameter eingebettet, Plug-in als Ausnahme

[Geräte & Parameter](https://support.knx.org/hc/de/articles/4402400203794), Webseite
403, [API erfolgreich](https://support.knx.org/api/v2/help_center/de/articles/4402400203794.json),
`edited_at`: 2026-09-07.

> „Wenn in der Baumansicht oder der Listenansicht ein einzelnes (mehrere identische) Gerät ausgewählt ist, können Sie auf die Parametereinstellungen zugreifen, indem Sie auf den Reiter 'Parameter' im oberen Teil in der Listenansicht klicken.“
>
> „Standard Parameter bzw. eventuelle Zusatzfunktionalität […] werden eingebettet im ETS Hauptfenster (Listenansicht) angezeigt; diese Anzeige wird dabei vollständig von der ETS gesteuert.“
>
> „Die Parameter werden in einem separaten Plug-in-Fenster angezeigt.“

Die Auslassung ist der DCA-Link-Zusatz. Der letzte Satz gilt ausdrücklich für
„Plug-in Dialog“, nicht für sämtliche Parameter.

### S7 — Topologie ist vollständiges Arbeitsfenster, nicht nur Werkzeugbaum

[Arbeitsfenster Topologie](https://support.knx.org/hc/de/articles/360021517699),
[API erfolgreich](https://support.knx.org/api/v2/help_center/de/articles/360021517699.json),
`edited_at`: 2024-12-10.

> „Diese Ansicht kann gleichzeitig mit anderen Ansichten benutzt werden und zeigt das KNX-Projekt unter Bezug auf die Busstruktur.“
>
> „Die Baumansicht (linke Seite) zeigt die vorhandene Bustopologie des KNX-Projektes, und auf der rechten Seite befindet sich eine Listenansicht, der im linken Fenster markierten Elemente.“

### S8 — Eigenschaften gehört zur rechten Sidebar

[Sidebar (Seitenleiste) – Übersicht](https://support.knx.org/hc/de/articles/360021517919),
Webseite 403, [API erfolgreich](https://support.knx.org/api/v2/help_center/de/articles/360021517919.json),
`edited_at`: 2025-11-14.

> „Die Sidebar bündelt häufig benötigte Funktionen und Informationen während der Projektierung und Inbetriebnahme von Projekten.“
>
> „Der Sidebar wird immer am rechten Rand des Bildschirms angezeigt. Es ist nicht möglich, sie nach links zu wechseln.“
>
> „In der Navigationsleiste selber sind wiederum folgende (Ansichts-) Container untergebracht.“

Die anschließende Liste nennt wörtlich „Container Eigenschaften“, „Gruppen-Container“,
Suchen und Ersetzen, Arbeitsbereiche, Offene Aufgaben, Laufende Operationen und
Undo Historie. Unterschiedliche Darstellung von Eigenschaften und Arbeitsfenster
ist damit sachlich begründet; deren exakte Gestaltung wurde hier nicht beobachtet.

## 2. BusChord am geprüften Commit: direkt im Quelltext nachgewiesen

- `src/Knx.App/MainWindow.axaml:265–271`: ein DockControl und eine nur zur
  Instanziierung vorhandene, unsichtbare PanelStaging-Fläche.
- `src/Knx.App/MainWindow.axaml.cs:107–135`: Dashboard, Gruppenadressen, Katalog,
  Parameter, Kommunikationsobjekte, Projektprüfung, Monitor und Recorder sind
  `Document`s in **einem** `DocumentDock`; aktiv ist anfangs Dashboard.
- Ebenda `116–151`: Topologie und Eigenschaften sind dagegen je ein `Tool` in
  einem eigenen `ToolDock`, links/rechts (`Proportion` 0.18/0.21). Alle sind
  floatbar; die seitlichen Tools sind zusätzlich pinnbar. Beide Tools und alle
  Documents sind nicht schließbar (`236–243`).
- Ebenda `153–159`: feste anfängliche Reihenfolge Topologie | Dokumente |
  Eigenschaften. Das ist die konkrete Ursache der unterschiedlichen Dock-Chrome,
  nicht ein ETS6-Nachweis. Die genaue Laufzeitdarstellung hängt vom Dock-Theme ab.
- `src/Knx.App/App.axaml:140–150`: DockFluentTheme; eigene Styles getrennt für
  `DocumentTabStripItem` (Höhe/Padding) und `ToolTabStripItem` (nur Schriftgröße).
- `src/Knx.App/MainWindow.axaml:453–500`: Topologie enthält im Wesentlichen den
  Baum, **nicht** das in S7 dokumentierte komplette Baum-/Listen-Arbeitsfenster.
- `src/Knx.App/MainWindow.axaml.cs:796–826,865–871`: Parameter und
  Kommunikationsobjekte werden als eigenständige Dokumente aktiviert und mit
  der aktuellen Geräteauswahl synchronisiert; bei ETS sind sie nach S5/S6
  untergeordnete Listenreiter im Arbeitsfenster.
- `src/Knx.App/MainWindow.axaml:502–535`: Eigenschaften ist auswahlabhängiger
  Kontext. Die rechte Rolle entspricht grundsätzlich S8; freie Floatbarkeit ist
  eine BusChord-Abweichung vom dort beschriebenen festen rechten Rand.

## 3. Korrektur der bisherigen Analyse

`analysis/ux/ets-help-ia.md:234–235` markiert „Panel statt Tab“ selbst als
Designentscheidung, nicht als ETS-Aussage. Trotzdem erzeugen `195–196` und
`313–316` („Panels, nicht Tabs“) einen falschen Gegensatz. S2 und insbesondere
S4 widerlegen diesen Gegensatz direkt. Abschnitt 2.3 derselben Analyse hatte
Projekt-Tabs und „Panel in neuem Tab“ bereits richtig erfasst.

Auch der dortige BusChord-Vergleich (`183–206`: feste Splits, separate Katalog- und
Parameterfenster) ist am geprüften Commit historisch: inzwischen besteht bereits
ein Docklayout mit Arbeitsdokumenten. Er darf nicht als aktuelle Bestandsaufnahme
zitiert werden. In diesem Auftrag bleibt die alte Datei unverändert.

## 4. Empfehlung und minimaler Korrekturumfang — ausdrücklich Design-Inferenz

**Nicht pauschal alle Tabs entfernen und auch nicht Eigenschaften zum
gleichrangigen Arbeitsdokument machen.** Drei Ebenen erkennbar halten:

1. Arbeits-/Projektansichten dürfen Tabs haben; Arbeitsfenster sollten dieselbe
   Rolle und einen konsistenten Kopf/Selektor haben. Topologie gehört fachlich
   zu diesen Ansichten, nicht zur Kategorie Eigenschaften.
2. Eigenschaften bleibt rechts kontextbezogen; optische Gleichheit mit allen
   Arbeitsfenstern wäre keine ETS-Treue.
3. Parameter und Kommunikationsobjekte als geräteabhängige Unterreiter einer
   Arbeitsansicht kennzeichnen, statt sie mit Katalog/Monitor/Projektansicht
   ununterscheidbar in eine flache Navigation zu mischen.

Minimal für die **Erklärung**: Begriffe und bisherigen Analysebefund berichtigen;
kein Code nötig. Minimal für eine **spätere UI-Korrektur**: zunächst nur
`BuildLayout` und betroffene Aktivierungs-/Home-Dock-Zuordnungen konsistent machen
(Topologie als Arbeitsansicht; Eigenschaften als Kontext behalten), danach
Geräte-Unterreiter lokal gruppieren und die vorhandenen Pane-Controls weiterverwenden.
Eine reine Umwandlung von `Tool` in `Document` schafft aber weder den ETS-
Panel-Selektor noch mehrere Arbeitsfenster im selben Tab: das wäre nur eine
bewusst begrenzte BusChord-Vereinheitlichung, kein fertiger ETS-Nachbau.
Kein neues Dockframework, kein Multi-Projekt-System und keine vollständige
ETS-Fensterverwaltung nötig, um die konkrete Rollenvermischung zu korrigieren.
Styles allein beheben nur die Optik, nicht diese Informationsarchitektur.

## 5. Evidenzgrenzen und Checks

- **Dokumentiert:** S1–S8; alle zur Aussage benötigten Inhalte erfolgreich
  abgerufen, teilweise über API nach Webseiten-403. Keine inhaltliche
  Quellenmehrdeutigkeit bezüglich „hat ETS6 Tabs?“.
- **Direkt festgestellt:** oben angeführte BusChord-Quellstellen am angegebenen
  SHA. Kein BusChord-Laufzeittest; deshalb kein Anspruch auf beobachtete Pixel,
  Tabpositionen oder konkretes Dock-Verhalten.
- **Nicht beobachtet:** laufende ETS6-Oberfläche, installierte lokale Version,
  Screenshots/GIFs der Hilfe (nur deren Text/Verweise gelesen). Aussagen über
  konkrete aktuelle ETS-Pixel oder Versionsparität wären hier unzulässig.
- **Nicht als UI-Beweis verwendet:** historische REA-Ressourcen/Assemblynamen
  aus `ets-help-ia.md`. Sie ergänzen die Dokumentation nicht zu einer
  Laufzeitbeobachtung und sind für diese Frage auch nicht nötig.
- **Abgeleitet:** Empfehlung und minimaler Korrekturumfang in Abschnitt 4.
- Nur dieser Bericht wurde angelegt; kein Implementierungscode, keine
  Projekt-/Buszustände verändert. Kein Build nötig für einen Bericht.

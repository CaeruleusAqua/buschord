# ETS6 / BusChord: begrenzter Workflow-Abgleich

Stand: 2026-10-18; BusChord-Basis `98bb80a8ed26fafda71f97c9c38e11b9e7c60a8d`.
Read-only-Untersuchung; ausschließlich dieser Bericht geändert. **Kein Paritätsnachweis.**
Gelesen: `FEATURE.md`, alle drei `plans/*.md`, `analysis/ets-features.md`, `README.md` sowie die unten genannten Quellpfade.

## Methode und Grenzen

Frische REA-Aufrufe gegen `C:\Program Files (x86)\ETS6`; Metadaten, ausgewählte CIL-Call-Edges und retained views, keine Assembly-Ausführung, kein Bus-/Netzwerkzugriff. Alle vier Assemblies melden Version `6.4.8718.0`, .NET Framework 4.8. Ein initialer unselektierter Member-Aufruf für ViewModel/ObjectModel überschritt jeweils das 64-MiB-Repräsentationsbudget: **keine negative Funktionsaussage daraus ableiten**. Danach erfolgreiche exakte Typselektion (je sechs Typen, keine unmatched names, Coverage complete innerhalb dieser Auswahl). Die komplette Typ-/Methodenlandschaft ist nicht untersucht.

Zur Auswahl der großen Assemblies wurden TypeDef-Namen zusätzlich mit einem kleinen read-only PE-Metadatenleser inventarisiert; fachliche Aussagen unten stützen sich auf danach frisch mit REA bestätigte Typen/Methoden. Keine Decompilation und keine beobachteten UI-Klickfolgen. Namen/Call-Edges belegen APIs und Verbindungen, nicht alle Laufzeitbedingungen oder Dateiformate.

## Artefaktidentität (frisch)

| Kürzel | Assembly | SHA-256 | REA-Artefakt-Evidence (Summary) |
|---|---|---|---|
| VM | `Knx.Ets.ViewModel.dll` | `236048b0b3992f504488c2fb9890ab2fe73c3a7a7079c0f4d8671d30aefc5c75` | `ev_8e5d5386a5e0468631e2d39bc82bb8b20fe9ee5d904b30515bb9336e94e4de7e` |
| OM | `Knx.Ets.ObjectModel.dll` | `73ffaaa97640a4207819c182101d23d3da73d4c983056f5bca5881c0f02eda8a` | `ev_f2a1befdb5c100bc51a00161598f36789fe1e764b36034ec9a339d23f0cd180d` |
| PD | `Knx.Ets.UI.ParameterDialog.dll` | `54e28d224543e3c14d04dfd7359fc5552d7472959e695bb89aed1defbff440b0` | `ev_70b7ae33d8f8bef8cb590a6ffe8137abeb3870f1c78799e51254757c3ae9523a` |
| PT | `Knx.Ets.UI.ParameterDialogTypes.dll` | `bab684d46051c8dbeddadcdf32a51142c0cffb087993dece28137f3754552ee2` | `ev_1e1ee36d2d33fc900ad398846e2cf4795e7b85a5ec9ad0ca35b62183ad491b84` |

## Frisch beobachtete ETS-Schnittstellen

**Gebäude/Räume/Funktionen — teilweise beantwortet.** OM `Knx.Ets.ObjectModel.Project.IBuildingPart` hat `get_ParentBuildingPart`, `get_BuildingParts`, `get_Functions`, `get_Type`, `get_CanHoldDevices`, `get_DefaultLine`, `GetAllNestedBuildingParts`. `Knx.Ets.ObjectModel.Project.IFunction` bietet `get_ParentBuildingPart`, `get_GroupAddressRefs`, `get_DefaultGroupRange`, `get_Context` sowie Name/Typ/Nummer/CompletionStatus. `Knx.Ets.ObjectModel.Operations.BuildingParts.AddBuildingPartOperation` hat `SpecializedValidate`, `SpecializedExecute`, `ExecuteReturnCreated`, `SpecializedUndo`/`SpecializedRedo`; `MoveBuildingPartOperation` besitzt `GetValidators` und Undo-/Redo-Mover. Belegt: hierarchischer fachlicher Baum mit Funktionen, nicht bloß Topologie. Exakte Raum-Typenumwerte und vollständige Shell-CRUD-Kette bleiben offen.

Shell-Gegenstück: `Knx.Ets.ViewModel.Application.Workspace.DetailView.BuildingPartDetailViewModel`, `FunctionDetailViewModel.ComputeGroupsAndContext`, `DeviceActiveComObjectsDetailViewModel.ComputeGroupsAndContext`, `GroupAddressDetailViewModel.GetGroupAddress`/`UpdateCollection`/`get_GroupAddressStyle`. Belegt sind getrennte Detailansichten; Layout und Bedienreihenfolge sind unbekannt.

**Gruppenadress-/Objektverknüpfung — teilweise beantwortet.** OM `Knx.Ets.ObjectModel.Operations.GroupAddressRefs.LinkGroupAddressRefToComObjectInstanceOperation` bietet `SpecializedValidate`, `DetermineSecurityImpacts`, `SpecializedExecute`, `SpecializedUndo`/`SpecializedRedo`, `BuildComObjectInstanceRefList`. Die Execute-Methode (`0x06005fc0`) ruft `Operation.DetermineSecurityImpacts` und `Operation.Validate` auf. `UnlinkGroupAddressRefOperation` hat ebenfalls Validate/Execute/Undo/Redo. Das belegt explizites Verknüpfen/Trennen mit Validierungs-/Security-Hooks; nicht deren vollständige Regeln.

**Öffnen/Speichern/Sicherung — teilweise beantwortet.** VM `Knx.Ets.ViewModel.Service.ProjectService`: `OpenProject` (`0x06001820`), `OpenProjectSilently`, `ImportProject`, `ExportProjects`, `GetRestorePointInfos`, `CloseProject`, `CheckProjectAccessKey`, `GetProjectAccessKeyAsync`. Frische Call-Edges: `RenameProject` → `Knx.Ets.ObjectModel.Project.IProject.Save`; `bGRxsBvBfKV` → `IProject.ValidateProjectIntegrity`. VM `Knx.Ets.ViewModel.Application.Dashboard.Commands.CoreBackupToArchiveCommandHandler.ExecuteAsync` (`0x0600ac18`), `get_KeepLocal`, `get_LockForOthers`; Hilfsmethode `ta0Rd4opOwi` → `IDialogFactories.CreateBackupToArchiveOptionsDialog`. Belegt: Sicherung ist eigener Archivworkflow mit Optionen, nicht identisch mit Export/Save As. Atomizität, Backupformat, Retention und Autosave wurden nicht ermittelt.

**Parameterbearbeitung — teilweise beantwortet.** PD `Knx.Ets.UI.ParameterDialog.ViewModel.ParameterViewModel`: `UpdateFromSource`, `UpdateToSource` (`0x060005eb`), `SetNewValue`, `CoerceValue`, `get_VerifyAndPersistValueCommand`, `get_Error`. Frischer Call-Edge: `UpdateToSource` → PT `Knx.Ets.UI.ParameterDialogTypes.Interfaces.IUIParameter.SetValues`. PT bestätigt außerdem `get_Access`, `get_DefaultValue`, `get_ValidationErrorMessage`, `SetGrantAccess`. PD `ParameterDialogViewModel.UpdateParameterTrees`, `SelectPage`, `get_SelectedPages`, `get_ResetToDefaults`; Hilfsmethode `EMiXzetPuT2` → `IUIParameterRoot.ResetToDefaults`. Belegt: Quellmodell-Rückschreiben, Fehler-/Zugriffsmodell, Seitenbaum und Reset; die tatsächliche Projektserialisierung und dynamische Auswertung hinter dem Provider bleiben offen.

## Tatsächlicher BusChord-Stand und Priorität

Status bezeichnet die **gesamte jeweilige Kette**, nicht das Vorhandensein einer Klasse. Schweregrad: kritisch = Datenverlust; hoch = irreführende/unsichere Bearbeitung; mittel = fehlender Arbeitsablauf.

| Workflow | Status | Schwere | Quellbeobachtung an der Basis | Empfohlener nächster Schritt |
|---|---|---|---|---|
| Projekt neu/öffnen/speichern | teilweise | **kritisch** | App `MainWindowViewModel.LoadProject`/`SaveProject` und MCP `ProjectOpen`/`ProjectSave` laufen gemeinsam über `Knxproj`. `Knxproj.Save:18–25` löscht zuerst das Ziel und schreibt nur `0.xml`; `Load:28–34` liest nur Root-`0.xml`. Andere ZIP-Einträge/unbekannte XML-Inhalte gehen verloren; Fehler nach Delete gefährdet die ganze Datei. | Archiv-/XML-Erhaltung plus atomischer Austausch; realer Fremddaten-Roundtrip, Schreibfehler muss Original unverändert lassen. **Bereits separatem Worker zugeteilt.** |
| Undo/Redo | teilweise | **kritisch** | `Undo.cs:35,59–60` verwendet dieselbe unvollständige `ToXml`/`FromXml`-Abbildung. `DeviceXml` schreibt keine Device-Id/Channels, `ReadDevice` erzeugt Id aus Adresse; GA-Kommentar wird nicht geschrieben/gelesen. Schon Undo eines anderen Feldes kann nichtserialisierte Daten entfernen. | Archivfix nicht nur am ZIP testen: Undo/Redo müssen dieselben Modell-/unbekannten Daten erhalten. Snapshot-Test mit Channels, stabilen IDs und GA-Kommentar. |
| Parameter lesen/anzeigen/ändern | teilweise | **hoch** | `LoadParametersFor:368–387` parst jedes Mal neue Applikationsinstanzen; `ParameterItemViewModel.OnValueChanged:49–53` ändert diese Instanz. `MainWindow.axaml.cs:418–419` setzt lediglich Status. `Model.cs` enthält keinen Geräte-Parameterzustand, `Knxproj.DeviceXml` keine Parameterwerte. Keine MCP-Parameterwerkzeuge in `BuildTools`. | Geräteinstanzwerte persistent ans Projekt binden; ändern → schließen → öffnen → speichern → neu laden testen; Validierung vor Übernahme. Parserkorrektur allein löst diesen Lifecycle nicht. **Parserarbeit separat zugeteilt.** |
| Gruppenadressen anlegen/ändern/löschen; Links speichern | implementiert (begrenzter Umfang) | mittel | App Add/Rename/Delete, MCP `group_address_*`; Löschen entfernt Objektlinks. `ComObjectRefs`/`Links` existieren und werden serialisiert. Kein Beleg für vollständige ETS-Objekt-/Flag-Semantik. | Bestehende Funktion behalten; nicht mit vollständigem Objektmanagement gleichsetzen. |
| Explizite Objektliste, gezielt verbinden/trennen | teilweise | **hoch** | App zeigt nur Objekt-/Linkanzahlen (`UpdateDetails:548–549`), bietet Smart-Link statt Objekteditor. MCP-BuildTools hat Smart-Link, aber keine expliziten ComObject-List/Link/Unlink-Tools. | Auswahl eines konkreten Geräteobjekts + GA, Link/Unlink mit Undo und Roundtrip. **Direkte Links separat zugeteilt.** |
| Smart-Link als Ersatz für gezieltes Engineering | teilweise | **hoch** | App übernimmt alle bis zu drei Vorschläge je Objekt (`MainWindow.axaml.cs:369–385`); MCP nur den besten. `SmartLinking.TrimSubtype:89–92` reduziert alle DPT-Ziffern auf **das erste Zeichen** (z. B. 1 und 10 kollidieren). MCP liefert keine DPT-Auflösung; App-Cache wird erst beim Parameteröffnen gefüllt und nur nach Objekt-Id indiziert. `Apply:72–80` prüft nur Duplikate, keine fachliche Kompatibilität. | Vor neuer Komfortfunktion: korrekt normalisierte DPTs/Bitgrößen, gerätebezogene Metadaten, einzelne Bestätigung; Gleichstand nicht automatisch verdrahten. Regression: DPT 1 vs. 10; zwei Geräte mit gleichen Objekt-Ids. |
| Produkt austauschen | teilweise | **hoch** | `ReplaceProduct.cs:31–40` ersetzt Referenzen, behält alte ComObjects/Links ohne neues Programm zu lesen. `ProjectCheck.CheckLinks` prüft GA-Ziel-Ids, nicht die Existenz eines Objekts im neuen Produkt. Kommentar verspricht mehr als Prüfung leistet. | Bis zum kompatibilitätsgeprüften Objektmapping als Referenzwechsel kennzeichnen/warnen; inkompatible Programme blockieren oder explizit bestätigen lassen. |
| Gebäude/Räume/Funktionen pflegen | fehlt | mittel | `Model.cs` bietet ausschließlich Installation/Area/Line/Segment/Device/Channel und GA-Baum. App `Refresh:497–525` baut nur Topologie; MCP-Toolinventar keine Gebäudefunktionen. | Erst nach Datensicherheit: kleines persistentes Gebäudeteil-/Funktionsmodell + Geräte-/GA-Bezüge; echte CRUD-/Undo-Kette, nicht dekorativer Ribbon-Tab. |
| Archivbackup/Wiederherstellung | fehlt | mittel | App/MCP haben Save/Save As, aber keinen entsprechenden Backup-/Restore-Arbeitsablauf. | Zunächst sichere Dateioperationen; danach nutzerseitiger Snapshot + Wiederherstellung mit geprüftem Inhalt. Keine ETS-Archivkollaboration vortäuschen. |
| Änderungen schützen / Saved-State | teilweise | **hoch** | Titel nutzt `_undo.Steps > 0`, `SaveProject` pusht nur statt Saved-Marker zu setzen; `SaveProjectAs` hat ebenfalls keinen Saved-Marker. Open/New ersetzen Projekt; im gelesenen MainWindow kein Closing-/Dirty-Guard. | Eigenen Saved-Zustand und Nachfrage vor Ersetzen/Schließen; nicht Undo-Historienlänge als Dirty-Zustand verwenden. |

**Reihenfolge:** (1) verlustfreies/ausfallsicheres Save **und Undo**, (2) persistente, validierte Parameterinstanzen + explizite sichere Links, (3) Smart-Link-/Produktwechsel-Risiken und Dirty-Schutz, (4) Gebäude/Funktionen und Backup/Restore. Laufende Workerarbeiten oben nicht duplizieren; ihre Abnahme muss die komplette Kette prüfen. Ein neuer Menüpunkt senkt keines dieser Risiken.

## Evidence für Reproduktion / Prüfung

Die folgenden IDs stammen aus dieser Untersuchung; retained views bleiben an ihr jeweiliges Parent-Evidence gebunden. Tokens gelten nur für den oben angegebenen SHA/MVID-Build.

Prüfung: alle zwölf im Bericht zitierten IDs mit `get_evidence_bundle(detail: summary, filters.evidence_id)` nachgeschlagen: jeweils genau ein retained record, SHA passend. Alle vier Datei-SHA zusätzlich unabhängig mit `Get-FileHash -Algorithm SHA256` bestätigt.

| Referenz | Inhalt | Evidence-ID |
|---|---|---|
| VM-M | selektierte vollständige Memberbeobachtung (6 Typen) | `ev_77a91c169a2fd2dca6504d2ff89a97f60eb92bc50f3fc5351891537b74682f41` |
| VM-Methods | retained methods, 116/116 Zeilen | `ev_1e2ce7de21f7ceb07b1e7c35334a13756e4a0169419a1c7c723db9819b0ac09d` |
| VM-Edges | retained call_edges, 636/636 Zeilen | `ev_0d578af639b126fa7b1ca236663ad110600e4c144dd4c553a8572d21300fb70f` |
| OM-M | selektierte vollständige Memberbeobachtung (6 Typen) | `ev_56f910f7b381bf114f1e8e3a70ecd6e98b78d3d35272d7fa68f4840afef2cdbd` |
| OM-Methods | retained methods, 89/89 Zeilen | `ev_d869f7d7b7a95d3ee82f0c914175519b3b8b47ba0bc474374e68c76c940e6000` |
| OM-Edges | retained call_edges, 492/492 Zeilen | `ev_c5dac33fe2d2ee85777787f5bd223ca33d3ce99c3a7dffe48960a4cdd5c39048` |
| PD-M | ParameterDialogViewModel, ParameterViewModel, ParameterBuilder | `ev_77d3e40c3eb1986ad2aee5f7faebf4b6b895e030f32acfcf7ba6e7f67f7f6e63` |
| PT-M | IUIParameter, IParameterProvider | `ev_39f56ec94382f8566eeb1f5754024a7c289467c940545d3282156f460d4c9026` |

**Unbekannt / nicht abgenommen:** reales ETS-Projekt-Roundtrip einschließlich verschlüsselter/nested Archive; komplette Gebäude-/Funktionsenumwerte und Shellbefehle; dynamische Parameterabhängigkeiten und Pluginparameter; ETS-Backupformat/Atomizität/Autosave; reale Bus-/Download-Kompatibilität. Bestehende grüne README-/FEATURE-Häkchen sind hierfür keine neue Evidenz. Kein Build erforderlich oder ausgeführt; Prüfungen dieser Untersuchung: Branch/HEAD, Artefakt-SHA, gültige REA-IDs/retained references sowie Berichtsdiff.

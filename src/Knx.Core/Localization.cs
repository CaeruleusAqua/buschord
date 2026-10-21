using System.ComponentModel;

namespace Knx;

/// <summary>
/// Central UI strings. The clone ships the dictionaries in code instead of satellite
/// assemblies: no build-time resources, no dependency, still one place per key.
/// Add a language by adding a column to <see cref="Map"/> and registering the code in
/// <see cref="Languages"/>; XAML binds through {loc:Tr Key} and switches live.
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    public static Strings Instance { get; } = new();

    private static string _language = "de";

    /// <summary>Active language code ("de", "en"); every bound {loc:Tr} updates immediately.</summary>
    public static string Language
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = Available.Contains(value) ? value : "de";
            Instance.Refresh();
        }
    }

    public static IReadOnlyList<string> Available { get; } = ["de", "en"];

    public string this[string key] => Get(key);

    public static string Get(string key, params object[] args)
    {
        var text = Map.TryGetValue(key, out var entry)
            ? _language == "en" ? entry.En : entry.De
            : key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static readonly Dictionary<string, (string De, string En)> Map = new()
    {
        // ---- menu ----
        ["MenuFile"] = ("Datei", "File"),
        ["MenuFileNew"] = ("Neues Projekt…", "New project…"),
        ["MenuFileOpen"] = ("Projekt öffnen…", "Open project…"),
        ["MenuFileSave"] = ("Projekt speichern", "Save project"),
        ["MenuFileSaveAs"] = ("Projekt speichern unter…", "Save project as…"),
        ["MenuFileExport"] = ("Export", "Export"),
        ["MenuFileExportDevices"] = ("Geräte (CSV)…", "Devices (CSV)…"),
        ["MenuFileExportTopology"] = ("Topologie (CSV)…", "Topology (CSV)…"),
        ["MenuFileExportTurtle"] = ("Semantic Export (Turtle, .ttl)…", "Semantic export (Turtle, .ttl)…"),
        ["MenuFileExportJsonLd"] = ("Semantic Export (JSON-LD, .jsonld)…", "Semantic export (JSON-LD, .jsonld)…"),
        ["MenuFileExportLabels"] = ("Etiketten (HTML-Druckbogen)…", "Labels (printable HTML sheet)…"),
        ["MenuFileExportLabelsCsv"] = ("Etiketten (CSV)…", "Labels (CSV)…"),
        ["MenuFileExportMerge"] = ("Etiketten (Mail-Merge-Datenquelle)…", "Labels (mail merge data source)…"),
        ["MenuFileExportGroupAddresses"] = ("Gruppenadressen (CSV)…", "Group addresses (CSV)…"),
        ["MenuFileImport"] = ("Gruppenadressen importieren (CSV)…", "Import group addresses (CSV)…"),
        ["MenuFileExit"] = ("Beenden", "Exit"),
        ["MenuEdit"] = ("Bearbeiten", "Edit"),
        ["MenuEditUndo"] = ("Rückgängig", "Undo"),
        ["MenuEditRedo"] = ("Wiederherstellen", "Redo"),
        ["MenuEditAddGroupAddress"] = ("Gruppenadresse anlegen", "Add group address"),
        ["MenuEditDeleteGroupAddress"] = ("Gruppenadresse löschen", "Delete group address"),
        ["MenuEditCompare"] = ("Projekt vergleichen…", "Compare projects…"),
        ["MenuBus"] = ("Bus", "Bus"),
        ["MenuBusDiscover"] = ("Geräte suchen", "Discover devices"),
        ["MenuBusConnect"] = ("Verbinden", "Connect"),
        ["MenuBusDisconnect"] = ("Trennen", "Disconnect"),
        ["MenuBusRecordStart"] = ("Aufzeichnung starten", "Start recording"),
        ["MenuBusRecordStop"] = ("Aufzeichnung beenden", "Stop recording"),
        ["MenuBusRecordSave"] = ("Aufzeichnung speichern…", "Save recording…"),
        ["MenuBusRecordLoad"] = ("Aufzeichnung laden…", "Load recording…"),
        ["MenuExtras"] = ("Extras", "Tools"),
        ["MenuExtrasCheck"] = ("Projektprüfung", "Project check"),
        ["MenuExtrasLink"] = ("Intelligente Verknüpfung…", "Smart linking…"),
        ["MenuExtrasReplace"] = ("Produkt austauschen…", "Replace product…"),
        ["MenuExtrasParameters"] = ("Geräteparameter…", "Device parameters…"),
        ["MenuExtrasOptions"] = ("Optionen…", "Options…"),

        // ---- ribbon ----
        ["RibbonProject"] = ("Projekt", "Project"),
        ["RibbonEdit"] = ("Bearbeiten", "Edit"),
        ["RibbonBus"] = ("Bus", "Bus"),
        ["RibbonRecorder"] = ("Telegram Recorder", "Telegram recorder"),
        ["RibbonAnalysis"] = ("Analyse", "Analysis"),
        ["RibbonExport"] = ("Export", "Export"),
        ["RibbonExtras"] = ("Extras", "Tools"),
        ["BtnNew"] = ("Neu", "New"),
        ["BtnOpen"] = ("Öffnen", "Open"),
        ["BtnSave"] = ("Speichern", "Save"),
        ["BtnUndo"] = ("Rückgängig", "Undo"),
        ["BtnRedo"] = ("Wiederherstellen", "Redo"),
        ["BtnDiscover"] = ("Suchen", "Discover"),
        ["BtnConnect"] = ("Verbinden", "Connect"),
        ["BtnDisconnect"] = ("Trennen", "Disconnect"),
        ["BtnStart"] = ("Start", "Start"),
        ["BtnStop"] = ("Stop", "Stop"),
        ["BtnSaveDots"] = ("Speichern…", "Save…"),
        ["BtnLoadDots"] = ("Laden…", "Load…"),
        ["BtnCheck"] = ("Projektprüfung", "Project check"),
        ["BtnSmartLink"] = ("Verknüpfen", "Smart link"),
        ["BtnReplaceProduct"] = ("Produkttausch", "Replace product"),
        ["BtnLabels"] = ("Etiketten", "Labels"),
        ["BtnCompareDots"] = ("Vergleich…", "Compare…"),
        ["BtnOptionsDots"] = ("Optionen…", "Options…"),
        ["BtnAddDevice"] = ("+ Gerät", "+ Device"),
        ["CtxDeleteDevice"] = ("Gerät löschen", "Delete device"),
        ["CtxAddDeviceToLine"] = ("Linie: Gerät hinzufügen", "Line: add device"),

        // ---- panels / tabs ----
        ["TabGroupAddresses"] = ("Gruppenadressen", "Group addresses"),
        ["CommunicationObjects"] = ("Kommunikationsobjekte", "Communication objects"),
        ["ParameterStaticNotice"] = ("Statischer Parametereditor. Dynamische Sichtbarkeit und Zuweisungen werden nicht ausgewertet. Übernehmen speichert nur im Projekt, ohne Geräteprogrammierung.", "Static parameter editor. Dynamic visibility and assignments are not evaluated. Apply updates the project only; no device programming."),
        ["ParameterSaved"] = ("Parameter im Projekt übernommen. Projekt speichern zum dauerhaften Sichern.", "Parameters applied to project. Save the project to persist them."),
        ["LinkAdd"] = ("Verknüpfen", "Link"),
        ["LinkRemove"] = ("Verknüpfung entfernen", "Unlink"),
        ["LinkAdded"] = ("Verknüpfung angelegt", "Link added"),
        ["LinkRemoved"] = ("Verknüpfung entfernt", "Link removed"),
        ["LinkUnchanged"] = ("Keine Änderung", "No change"),
        ["LinkExisting"] = ("Bestehende Verknüpfungen", "Existing links"),
        ["LinkSelectDevice"] = ("Bitte ein Projektgerät auswählen", "Select a project device"),
        ["LinkSelectAddress"] = ("Bitte eine Gruppenadresse auswählen", "Select a group address"),
        ["LinkProjectChanged"] = ("Projekt geändert — Dialog erneut öffnen", "Project changed — reopen this dialog"),
        ["LinkOfflineNotice"] = ("Nur Projektbearbeitung, keine Bus-Telegramme. Änderungen unterstützen Rückgängig/Wiederholen.", "Project edits only; no bus telegrams. Changes support undo/redo."),
        ["TabDashboard"] = ("Dashboard", "Dashboard"),
        ["TabCheck"] = ("Projektprüfung", "Project check"),
        ["TabMonitor"] = ("Busmonitor", "Bus monitor"),
        ["TabRecorder"] = ("Recorder", "Recorder"),
        ["PanelTopology"] = ("Topologie", "Topology"),
        ["PanelProperties"] = ("Eigenschaften", "Properties"),
        ["ColAddress"] = ("Adresse", "Address"),
        ["ColName"] = ("Name", "Name"),
        ["ColDatapointType"] = ("Datapoint-Typ", "Datapoint type"),
        ["ColLinked"] = ("Verknüpft", "Linked"),
        ["ColSeverity"] = ("Stufe", "Severity"),
        ["ColCode"] = ("Code", "Code"),
        ["ColPath"] = ("Objekt", "Object"),
        ["ColMessage"] = ("Beschreibung", "Description"),
        ["ColDevice"] = ("Gerät", "Device"),

        // ---- group address panel ----
        ["BtnAdd"] = ("+", "+"),
        ["BtnDelete"] = ("-", "-"),
        ["TipAddGroupAddress"] = ("Gruppenadresse anlegen", "Add group address"),
        ["TipDeleteGroupAddress"] = ("Gruppenadresse löschen", "Delete group address"),
        ["WatermarkSendValue"] = ("Wert senden (z. B. 1, 21.5, 0/1)", "Value to send (e.g. 1, 21.5, 0/1)"),
        ["BtnSend"] = ("Senden", "Send"),
        ["BtnRead"] = ("Lesen", "Read"),
        ["LblSelection"] = ("Auswahl: {0}", "Selection: {0}"),

        // ---- details ----
        ["LblDatapointType"] = ("Datapoint-Typ", "Datapoint type"),
        ["BtnApply"] = ("Übernehmen", "Apply"),
        ["BtnParameters"] = ("Parameter…", "Parameters…"),
        ["BtnDownload"] = ("Download (Image)…", "Download (image)…"),

        // ---- dashboard ----
        ["DashDevices"] = ("Geräte", "Devices"),
        ["DashGroupAddresses"] = ("Gruppenadressen", "Group addresses"),
        ["DashLinkedObjects"] = ("verknüpfte Objekte", "linked objects"),
        ["DashDptTypes"] = ("Datapoint-Typen", "Datapoint types"),
        ["DashDevicesPerLine"] = ("Geräte pro Linie", "Devices per line"),
        ["DashTopDpt"] = ("Häufigste Datapoint-Typen", "Most frequent datapoint types"),
        ["DashProject"] = ("Projekt: {0}", "Project: {0}"),

        // ---- check / monitor / recorder ----
        ["BtnRunCheck"] = ("Prüfung starten", "Run check"),
        ["LblFindings"] = ("{0} Befunde", "{0} findings"),
        ["LblDeviceInNetwork"] = ("Gerät im Netzwerk:", "Device on the network:"),
        ["BtnClearMonitor"] = ("Monitor leeren", "Clear monitor"),
        ["LblTelegrams"] = ("{0} Telegramme", "{0} telegrams"),
        ["ColTime"] = ("Zeit", "Time"),
        ["ColSource"] = ("Quelle", "Source"),
        ["LblGroupOnly"] = ("Nur Gruppenadressen", "Group addresses only"),
        ["MonitorKindAll"] = ("Alle", "All"),
        ["MonitorKindWrite"] = ("Schreiben", "Write"),
        ["MonitorKindRead"] = ("Lesen", "Read"),
        ["MonitorKindResponse"] = ("Antwort", "Response"),
        ["LblRecorded"] = ("{0} aufgezeichnet", "{0} recorded"),

        // ---- status ----
        ["StatusReady"] = ("Bereit", "Ready"),
        ["StatusNoProject"] = ("kein Projekt geladen", "no project loaded"),
        ["StatusConnectedTo"] = ("Verbunden: {0}", "Connected: {0}"),
        ["StatusConnecting"] = ("Verbinde…", "Connecting…"),
        ["StatusDisconnecting"] = ("Trenne…", "Disconnecting…"),
        ["StatusNotConnected"] = ("Nicht verbunden", "Not connected"),

        // ---- report window ----
        ["BtnSaveCsvDots"] = ("Als CSV speichern…", "Save as CSV…"),
        ["BtnClose"] = ("Schließen", "Close"),

        // ---- settings ----
        ["OptionsTitle"] = ("Optionen", "Options"),
        ["OptionsAppearance"] = ("Darstellung", "Appearance"),
        ["OptionsTheme"] = ("Design", "Theme"),
        ["OptionsLanguage"] = ("Sprache", "Language"),
        ["OptionsConfirmDelete"] = ("Vor dem Löschen nachfragen", "Ask before deleting"),
        ["OptionsBus"] = ("Bus", "Bus"),
        ["OptionsInterface"] = ("Schnittstelle", "Interface"),
        ["OptionsOwnAddress"] = ("Eigene Adresse", "Own address"),
        ["OptionsRecent"] = ("Zuletzt geöffnete Projekte", "Recently opened projects"),
        ["OptionsNoRecent"] = ("(noch keine Projekte geöffnet)", "(no projects opened yet)"),
        ["BtnSaveSettings"] = ("Speichern", "Save"),
        ["BtnCancel"] = ("Abbrechen", "Cancel"),
        ["ThemeSystem"] = ("System", "System"),
        ["ThemeLight"] = ("Hell", "Light"),
        ["ThemeDark"] = ("Dunkel", "Dark"),
        ["OptionsSaved"] = ("Optionen gespeichert", "Options saved"),

        // ---- new project ----
        ["NewProjectTitle"] = ("Neues Projekt", "New project"),
        ["NewProjectHeader"] = ("Projekt", "Project"),
        ["NewProjectComment"] = ("Kommentar", "Comment"),
        ["NewProjectTopology"] = ("Topologie", "Topology"),
        ["NewProjectStyle"] = ("Gruppenadress-Stil", "Group address style"),
        ["NewProjectStyleThree"] = ("Dreistufig (1/2/3)", "Three-level (1/2/3)"),
        ["NewProjectStyleTwo"] = ("Zweistufig (1/2)", "Two-level (1/2)"),
        ["NewProjectStyleFree"] = ("Frei (1/2/3/4)", "Free (1/2/3/4)"),
        ["NewProjectAreas"] = ("Bereiche", "Areas"),
        ["NewProjectLines"] = ("Linien pro Bereich", "Lines per area"),
        ["BtnCreate"] = ("Anlegen", "Create"),

        // ---- replace product ----
        ["ReplaceProductTitle"] = ("Produkt austauschen", "Replace product"),
        ["ReplaceOldRef"] = ("Alte Produkt-RefId", "Old product ref id"),
        ["ReplaceNewRef"] = ("Neue Produkt-RefId", "New product ref id"),
        ["ReplaceNewProgram"] = ("Neue Applikation (optional)", "New application (optional)"),
        ["BtnPreview"] = ("Vorschau", "Preview"),
        ["BtnReplace"] = ("Austauschen", "Replace"),
        ["ReplaceHint"] = ("Alle Geräte eines Produkts auf ein anderes Produkt umstellen. Gruppenadress-Verknüpfungen bleiben erhalten.",
                           "Point all devices of one product at another product. Group address links are kept."),

        // ---- messages ----
        ["MsgNoSelection"] = ("keine Auswahl", "nothing selected"),
        ["MsgNeedConnection"] = ("Lesen benötigt eine Busverbindung", "Reading requires a bus connection"),

        // ---- catalog (knxprod) ----
        ["CatalogSearch"] = ("Name, Bestellnummer, ID, Beschreibung suchen", "Search name, order number, ID, description"),
        ["CatalogAllSections"] = ("Alle Rubriken", "All sections"),
        ["CatalogResultCount"] = ("{0} von {1} Produkten", "{0} of {1} products"),
        ["CatalogNoResults"] = ("Keine Treffer – Filter ändern", "No matches – change the filters"),
        ["CatalogInvalidAddress"] = ("Gültige Adresse der Ziel-Linie mit Gerätenummer 1–255 eingeben", "Enter a valid address on the target line with device number 1–255"),
        ["CatalogDuplicateAddress"] = ("Adresse {0} ist bereits vergeben", "Address {0} is already in use"),
        ["CatalogLineFull"] = ("Ziel-Linie ist voll", "Target line is full"),
        ["CatalogFileTypes"] = ("KNX-Produktdatenbank / Hersteller-ZIP", "KNX product database / vendor ZIP"),
        ["CatalogLocalFileRequired"] = ("Lokale Datei erforderlich: {0}", "Local file required: {0}"),
        ["CatalogTitle"] = ("Produktkatalog", "Product catalog"),
        ["MenuCatalog"] = ("Katalog", "Catalog"),
        ["MenuCatalogOpen"] = ("Katalog öffnen…", "Open catalog…"),
        ["MenuCatalogLibrary"] = ("Katalogbibliothek…", "Catalog library…"),
        ["CatalogLibrary"] = ("Bibliothek", "Library"),
        ["CatalogImport"] = ("Importieren…", "Import…"),
        ["CatalogReload"] = ("Neu laden", "Reload"),
        ["CatalogProducts"] = ("Produkte", "Products"),
        ["CatalogManufacturer"] = ("Hersteller", "Manufacturer"),
        ["CatalogOrderNumber"] = ("Bestellnummer", "Order number"),
        ["CatalogProgram"] = ("Applikation", "Application"),
        ["CatalogProgramCount"] = ("Progr.", "Prog."),
        ["CatalogComObjects"] = ("Kommunikationsobjekte", "Communication objects"),
        ["CatalogInsert"] = ("In Projekt einfügen", "Insert into project"),
        ["CatalogTargetLine"] = ("Ziel-Linie", "Target line"),
        ["CatalogNoCatalog"] = ("kein Katalog geladen", "no catalog loaded"),
        ["CatalogNoProducts"] = ("keine Produkte im Katalog", "no products in catalog"),
        ["CatalogPaths"] = ("Katalogpfad", "Catalogue path"),
        ["CatalogImported"] = ("{0} Katalog(e) importiert", "{0} catalog(s) imported"),
        ["CatalogInserted"] = ("{0} als {1} eingefügt", "{0} inserted as {1}"),
        ["CatalogNeedLine"] = ("Ziel-Linie wählen", "Select a target line"),
        ["CatalogNeedProduct"] = ("kein Produkt ausgewählt", "no product selected"),
        ["CatalogLoadError"] = ("Katalog nicht lesbar: {0}", "Catalog not readable: {0}"),

        // ---- UX: start screen, empty states and next-step hints ----
        ["UxStart"] = ("Start", "Start"),
        ["UxOpenProject"] = ("Projekt öffnen", "Open project"),
        ["UxNewProject"] = ("Neues Projekt", "New project"),
        ["UxRecent"] = ("Zuletzt geöffnet", "Recently opened"),
        ["UxNoRecent"] = ("Noch kein Projekt geöffnet", "No project opened yet"),
        ["UxCatalogCard"] = ("Gerät aus Katalog", "Device from catalogue"),
        ["UxCatalogHint"] = ("Herstellerprodukte durchsuchen und einfügen", "Browse manufacturer products and insert"),
        ["UxEmptyDevices"] = ("Noch keine Geräte", "No devices yet"),
        ["UxEmptyGroupAddresses"] = ("Noch keine Gruppenadressen", "No group addresses yet"),
        ["UxEmptyFindings"] = ("Keine Befunde", "No findings"),
        ["UxNeedsProject"] = ("Projekt öffnen", "Open a project"),
        ["UxNeedsDevice"] = ("Gerät wählen", "Select a device"),
        ["UxNeedsInterface"] = ("Schnittstelle einrichten", "Set up interface"),
        ["UxNoProduct"] = ("ohne Produkt", "no product"),
        ["UxDeviceWithoutProduct"] = ("Dieses Gerät hat kein Produkt.", "This device has no product."),
        ["UxInsert"] = ("Einfügen", "Insert"),
        ["UxMore"] = ("Mehr", "More"),
        ["UxSettings"] = ("Einstellungen", "Settings"),
        ["TabParameters"] = ("Parameter", "Parameters"),
        ["ColChange"] = ("Änderung", "Change"),
        ["ColValue"] = ("Wert", "Value"),
        ["ColType"] = ("Typ", "Type"),
        ["TitleCompare"] = ("Projektvergleich", "Project compare"),
        ["ParameterTitle"] = ("Geräteparameter", "Device parameters"),
        ["ParameterNoticeShort"] = ("Statischer Editor", "Static editor"),
        ["ParameterNoticeMore"] = ("Dynamische Logik wird nicht ausgewertet.", "Dynamic logic is not evaluated."),

        // ---- search / replace pane ----
        ["SearchWatermark"] = ("Suchen…", "Search…"),
        ["SearchScope"] = ("Bereich", "Scope"),
        ["SearchScopeAll"] = ("Alle", "All"),
        ["SearchScopeDevices"] = ("Geräte", "Devices"),
        ["SearchScopeGroupAddresses"] = ("Gruppenadressen", "Group addresses"),
        ["SearchScopeTopology"] = ("Topologie", "Topology"),
        ["SearchScopeCommunicationObjects"] = ("Kommunikationsobjekte", "Communication objects"),
        ["SearchScopeBuildings"] = ("Gebäude", "Buildings"),
        ["SearchRegex"] = ("Regex", "Regex"),
        ["SearchCase"] = ("Aa", "Aa"),
        ["SearchCaseTip"] = ("Groß-/Kleinschreibung beachten", "Match case"),
        ["BtnSearch"] = ("Suchen", "Search"),
        ["SearchResults"] = ("Treffer", "Results"),
        ["SearchNoResults"] = ("Keine Treffer", "No matches"),
        ["SearchCount"] = ("{0} Treffer", "{0} matches"),
        ["SearchTruncated"] = ("{0} Treffer (erste {1} angezeigt)", "{0} matches (first {1} shown)"),
        ["SearchEmptyQuery"] = ("Suchbegriff eingeben", "Enter a search term"),
        ["SearchInvalidRegex"] = ("Ungültiger regulärer Ausdruck: {0}", "Invalid regular expression: {0}"),
        ["SearchReplace"] = ("Ersetzen", "Replace"),
        ["SearchReplaceWatermark"] = ("Ersetzen durch…", "Replace with…"),
        ["SearchPreviewNothing"] = ("Noch keine Vorschau", "Nothing previewed yet"),
        ["SearchPreviewCounts"] = ("{0} Felder in {1} Objekten", "{0} fields in {1} items"),
        ["SearchPreviewNoMatch"] = ("Kein Treffer zum Ersetzen", "No matches to replace"),
        ["SearchPreviewStale"] = ("Vorschau veraltet – erneut Vorschau berechnen",
                                   "Preview is stale – run the preview again"),
        ["SearchApplied"] = ("{0} Felder in {1} Objekten ersetzt – Rückgängig möglich",
                              "{0} fields in {1} items replaced – undo available"),
        ["SearchKindDevice"] = ("Gerät", "Device"),
        ["SearchKindGroupAddress"] = ("Gruppenadresse", "Group address"),
        ["SearchKindTopology"] = ("Topologie", "Topology"),
        ["SearchKindCommunicationObject"] = ("Kommunikationsobjekt", "Communication object"),
        ["SearchKindBuilding"] = ("Gebäude", "Building"),
        ["SearchKindOther"] = ("Objekt", "Object"),

        // ---- building pane (B2); appended block, no existing key changed ----
        ["BuildingTitle"] = ("Gebäude", "Buildings"),
        ["BuildingSpaces"] = ("Bereiche", "Spaces"),
        ["BuildingDevicesInSpace"] = ("Geräte im Bereich", "Devices in space"),
        ["BuildingUnassigned"] = ("Nicht zugewiesene Geräte", "Unassigned devices"),
        ["BuildingAdd"] = ("Bereich hinzufügen", "Add space"),
        ["BuildingKind"] = ("Art", "Kind"),
        ["BuildingNewSpace"] = ("Neuer Bereich", "New space"),
        ["BuildingRename"] = ("Umbenennen", "Rename"),
        ["BuildingDelete"] = ("Löschen", "Delete"),
        ["BuildingAssign"] = ("Zuweisen", "Assign"),
        ["BuildingUnassign"] = ("Entfernen", "Remove"),
        ["BuildingSearch"] = ("Bereiche und Geräte filtern", "Filter spaces and devices"),
        ["BuildingNoSpaces"] = ("Noch keine Bereiche. Legen Sie ein Gebäude an.", "No spaces yet. Create a building."),
        ["BuildingNoDevicesInSpace"] = ("Keine Geräte in diesem Bereich", "No devices in this space"),
        ["BuildingNoUnassigned"] = ("Alle Geräte sind zugewiesen", "All devices are assigned"),
        ["BuildingSelectSpace"] = ("Bitte einen Bereich auswählen", "Select a space"),
        ["BuildingNameEmpty"] = ("Name darf nicht leer sein", "Name must not be empty"),
        ["BuildingKindBuilding"] = ("Gebäude", "Building"),
        ["BuildingKindBuildingPart"] = ("Gebäudeteil", "Building part"),
        ["BuildingKindFloor"] = ("Etage", "Floor"),
        ["BuildingKindRoom"] = ("Raum", "Room"),
        ["BuildingKindDistributionBoard"] = ("Verteiler", "Distribution board"),
        ["BuildingKindStairway"] = ("Treppenhaus", "Stairway"),
        ["BuildingKindCorridor"] = ("Flur", "Corridor"),
        ["BuildingKindArea"] = ("Bereich", "Area"),
        ["BuildingKindGround"] = ("Gelände", "Ground"),
        ["BuildingKindSegment"] = ("Abschnitt", "Segment"),
        ["BuildingKindFunction"] = ("Funktion", "Function"),
        ["BuildingKindUnknown"] = ("Unbekannt", "Unknown"),
        ["CatalogLoading"] = ("Kataloge werden geladen…", "Loading catalogues…"),
        ["CatalogLoadingCount"] = ("Kataloge werden geladen… {0}/{1}", "Loading catalogues… {0}/{1}"),

        // ---- shared shell status, dialogs and panes (appended, no existing key changed) ----
        ["TitleNoProject"] = ("BusChord – kein Projekt", "BusChord – no project"),
        ["StatusProjectSaved"] = ("Projekt gespeichert", "Project saved"),
        ["StatusGroupAddressAdded"] = ("Gruppenadresse angelegt", "Group address added"),
        ["StatusGroupAddressDeleted"] = ("Gruppenadresse {0} gelöscht", "Group address {0} deleted"),
        ["StatusGroupAddressChanged"] = ("Gruppenadresse geändert", "Group address changed"),
        ["StatusDeviceAdded"] = ("Gerät angelegt", "Device added"),
        ["StatusDeviceEdited"] = ("Gerät {0} bearbeitet", "Device {0} edited"),
        ["StatusDeviceDeleted"] = ("Gerät {0} gelöscht", "Device {0} deleted"),
        ["StatusDeviceMoved"] = ("Gerät {0} verschoben", "Device {0} moved"),
        ["StatusDeviceRenamed"] = ("Gerät {0} umbenannt", "Device {0} renamed"),
        ["StatusLinksAdded"] = ("{0} Verknüpfung(en) angelegt", "{0} link(s) added"),
        ["StatusSmartLinkSuggestions"] = ("Intelligente Verknüpfung: {0} Vorschläge", "Smart linking: {0} suggestions"),
        ["StatusParameterUnreadable"] = ("Parameter nicht lesbar: {0}", "Parameters not readable: {0}"),
        ["StatusDevicesFound"] = ("{0} Geräte gefunden", "{0} devices found"),
        ["StatusNoDevicesFound"] = ("keine Geräte gefunden", "no devices found"),
        ["StatusNoDeviceSelected"] = ("kein Gerät ausgewählt", "no device selected"),
        ["StatusConnectedToDevice"] = ("Verbunden mit {0}", "Connected to {0}"),
        ["StatusDisconnected"] = ("Getrennt", "Disconnected"),
        ["StatusSentValue"] = ("gesendet {0} = {1}", "sent {0} = {1}"),
        ["StatusDownloadFailed"] = ("Download fehlgeschlagen: {0}", "Download failed: {0}"),
        ["StatusRecordingStarted"] = ("Aufzeichnung gestartet", "Recording started"),
        ["StatusRecordingStopped"] = ("Aufzeichnung beendet: {0} Telegramme", "Recording stopped: {0} telegrams"),
        ["StatusTelegramsLoaded"] = ("{0} Telegramme geladen", "{0} telegrams loaded"),
        ["MsgFileNotFound"] = ("Die Datei '{0}' wurde nicht gefunden.", "The file '{0}' was not found."),
        ["PropertyProduct"] = ("Produkt", "Product"),
        ["PropertyLinks"] = ("Verknüpfungen", "Links"),
        ["PropertyLinkedObjects"] = ("Verknüpfte Objekte", "Linked objects"),
        ["TreeNodeInstallation"] = ("Installation {0}", "Installation {0}"),
        ["TreeNodeArea"] = ("Bereich {0}", "Area {0}"),
        ["TreeNodeLine"] = ("Linie {0}", "Line {0}"),
        ["TreeNodeSegment"] = ("Segment {0}", "Segment {0}"),
        ["CompareSummary"] = ("{0} hinzugefügt · {1} entfernt · {2} geändert",
                               "{0} added · {1} removed · {2} changed"),
        ["CsvSaveFailed"] = ("CSV nicht speicherbar", "CSV could not be saved"),
        ["ReplaceRequiredFields"] = ("Alte und neue Produkt-RefId sind Pflichtfelder.",
                                      "Old and new product ref ids are required."),
        ["ReplaceNoDevices"] = ("Keine Geräte mit dieser Produkt-RefId gefunden.",
                                 "No devices found with this product ref id."),
        ["ReplacePreviewSummary"] = ("{0} Gerät(e) betroffen, {1} Verknüpfung(en) bleiben erhalten.",
                                      "{0} device(s) affected, {1} link(s) retained."),
        ["ReplaceApplied"] = ("{0} Gerät(e) umgestellt", "{0} device(s) changed"),
        ["SettingsInterfaceExample"] = ("z. B. 192.168.1.10", "e.g. 192.168.1.10"),
        ["UxReport"] = ("Bericht", "Report"),
        ["ReportEmpty"] = ("Keine Einträge", "No entries"),
        ["ReportCsvHeader"] = ("Stufe;Objekt;Beschreibung", "Severity;Object;Description"),
        ["ReportCsvFileName"] = ("befunde", "findings"),
        ["CompareCsvHeader"] = ("Pfad;Typ;Objekt;Änderung;Alt;Neu", "Path;Type;Object;Change;Old;New"),
        ["CompareCsvFileName"] = ("projektvergleich", "project-compare"),
        ["CompareTypeProject"] = ("Projekt", "Project"),
        ["CompareTypeInstallation"] = ("Anlage", "Installation"),
        ["CompareTypeGroupAddress"] = ("Gruppenadresse", "Group address"),
        ["CompareTypeDevice"] = ("Gerät", "Device"),
        ["ModificationAdded"] = ("Hinzugefügt", "Added"),
        ["ModificationRemoved"] = ("Entfernt", "Removed"),
        ["ModificationChanged"] = ("Geändert", "Changed"),
        ["ModificationUnchanged"] = ("Unverändert", "Unchanged"),
        ["SplashTagline"] = ("Open-Source-Gebäudeautomation", "Open-source building automation engineering"),
    };
}

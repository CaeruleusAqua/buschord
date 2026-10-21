using Knx.Catalog;

namespace Knx.App.Services;

public sealed record LineChoice(string ProjectId, string? ProjectGuid, string InstallationId,
    string AreaId, string LineId, string Text);

/// <summary>Segment of the selected line; the insert target below the line.</summary>
public sealed record SegmentChoice(string Id, string Text);

/// <summary>What a catalogue application is used for; every use has its own blocking rules.</summary>
public enum CatalogUse
{
    /// <summary>Insert the product as a device into the project.</summary>
    Insert,

    /// <summary>Open and edit the static parameter values of the application.</summary>
    EditParameters,

    /// <summary>Program the device from the catalogue application.</summary>
    Download,
}

/// <summary>Why a catalogue application cannot serve a <see cref="CatalogUse"/>.</summary>
public enum CatalogBlockReason
{
    /// <summary>The use is allowed.</summary>
    None,

    /// <summary>ETS marked the application broken.</summary>
    Broken,

    /// <summary>ETS marked the download information incomplete.</summary>
    DownloadInfoIncomplete,

    /// <summary>A DCA/plug-in provides the editor or download; neither is executed here.</summary>
    DcaUnsupported,
}

/// <summary>
/// Catalogue-only vocabulary (status badges, gating reasons, import report). Kept apart from
/// <see cref="Strings"/> because the pane owns these texts; language follows
/// <see cref="Strings.Language"/>.
/// </summary>
public static class CatalogUiText
{
    public static string Get(string key, params object[] args)
    {
        var text = Map.TryGetValue(key, out var entry)
            ? Strings.Language == "en" ? entry.En : entry.De
            : key;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    /// <summary>Readable reason for a blocked use; empty when the use is allowed.</summary>
    public static string Reason(CatalogBlockReason reason) => reason switch
    {
        CatalogBlockReason.Broken => Get("ReasonBroken"),
        CatalogBlockReason.DownloadInfoIncomplete => Get("ReasonIncomplete"),
        CatalogBlockReason.DcaUnsupported => Get("ReasonDca"),
        _ => "",
    };

    private static readonly Dictionary<string, (string De, string En)> Map = new()
    {
        ["ApplicationStatus"] = ("Applikationsstatus", "Application status"),
        ["NoApplication"] = ("Keine Applikation deklariert", "No application declared"),
        ["BadgeBroken"] = ("Defekt", "Broken"),
        ["BadgeIncomplete"] = ("Downloadinfo unvollständig", "Download info incomplete"),
        ["BadgeDca"] = ("DCA / Plug-in", "DCA / plug-in"),
        ["BadgeExternal"] = ("Fremdsoftware", "External software"),
        ["ReasonBroken"] = ("Applikation ist als defekt markiert: Einfügen, Parameter bearbeiten und Download sind gesperrt.",
                            "The application is marked broken: insert, parameter editing and download are blocked."),
        ["ReasonIncomplete"] = ("Downloadinformationen sind unvollständig: Einfügen, Parameter bearbeiten und Download sind gesperrt.",
                                "Download information is incomplete: insert, parameter editing and download are blocked."),
        ["ReasonDca"] = ("DCA/Plug-in läuft hier nicht: Parameter sind nur lesbar, Download wird nicht unterstützt. Einfügen bleibt möglich.",
                         "The DCA/plug-in does not run here: parameters are read-only and download is unsupported. Insert remains possible."),
        ["ExternalSoftware"] = ("Zusätzlich Hersteller-Software erforderlich (Hinweis, keine Sperre).",
                                "Vendor software is also required (information only, not a block)."),
        ["StaticReady"] = ("Einfügen und statische Parameterbearbeitung sind lokal möglich.",
                           "Insert and static parameter editing are available locally."),
        ["RegistrationStatus"] = ("Registrierungsstatus", "Registration status"),
        ["MinEtsVersion"] = ("Mindest-ETS", "Minimum ETS"),
        ["SchemaVersion"] = ("Schema-Version", "Schema version"),
        ["MaskVersion"] = ("Maskenversion", "Mask version"),
        ["Programs"] = ("Programme", "Programs"),
        ["LastImport"] = ("Letzter Import: {0}", "Last import: {0}"),
        ["ImportSummary"] = ("{0} Produkte · {1} Programme", "{0} products · {1} programs"),
        ["RemoveCatalog"] = ("Katalog entfernen", "Remove catalogue"),
        ["RemoveCatalogTip"] = ("Katalogdatei aus der Bibliothek löschen", "Delete the catalogue file from the library"),
        ["Removed"] = ("Katalog entfernt: {0}", "Catalogue removed: {0}"),
        ["NeedRoom"] = ("Der gewählte Raum existiert nicht mehr.", "The selected room no longer exists."),
        ["RemoveFailed"] = ("Katalog nicht entfernt (außerhalb der Bibliothek oder gesperrt): {0}",
                            "Catalogue not removed (outside the library or locked): {0}"),
        ["TargetSegment"] = ("Ziel-Segment", "Target segment"),
        ["SegmentLabel"] = ("Segment {0}", "Segment {0}"),
        ["SegmentMissing"] = ("Das gewählte Segment existiert nicht mehr.", "The selected segment no longer exists."),
        ["NameAdjusted"] = ("Name bereits vergeben, eingefügt als {0}", "Name already in use, inserted as {0}"),
        ["RoomContext"] = ("Raum: {0}", "Room: {0}"),
        ["NoRoomContext"] = ("Ohne Raumzuordnung", "No room assignment"),
        ["DatapointTypes"] = ("Datapoint-Typen", "Datapoint types"),
        ["ComObjectsPreview"] = ("Kommunikationsobjekte", "Communication objects"),
        ["NoComObjects"] = ("Keine Kommunikationsobjekte deklariert", "No communication objects declared"),
        ["MoreComObjects"] = ("… und {0} weitere", "… and {0} more"),
        ["UnknownManufacturer"] = ("Hersteller unbekannt", "Manufacturer unknown"),
        ["UnknownVendorData"] = ("Herstellerdaten nicht auflösbar: Die Applikation dieses Produkts ist in der Katalogdatei nicht deklariert oder verweist auf nicht unterstützte Herstellerdaten. Es werden keine Parameter oder Kommunikationsobjekte erfunden; das Einfügen bleibt möglich, die Parametrierung bleibt leer.",
                                 "Vendor data cannot be resolved: this product's application is not declared in the catalogue file, or it references unsupported vendor data. No parameters or communication objects are invented; insert remains possible with an empty parameter set."),
        ["Bits"] = ("Bit", "bit"),
        ["Importing"] = ("Importiere {0}/{1}: {2}", "Importing {0}/{1}: {2}"),
        ["LoadingFile"] = ("Lade {0}/{1}: {2}", "Loading {0}/{1}: {2}"),
        ["ImportCancelled"] = ("Import abgebrochen: {0} von {1} Katalogen übernommen",
                               "Import cancelled: {0} of {1} catalogues kept"),
        ["LoadCancelled"] = ("Laden abgebrochen; es läuft im Hintergrund weiter und erscheint automatisch.",
                             "Loading cancelled; it continues in the background and appears automatically."),
        ["LoadContinued"] = ("Laden im Hintergrund abgeschlossen.", "Background loading finished."),
        ["InsertUndoHint"] = ("Einfügen mit Strg+Z rückgängig machen.", "Undo the insert with Ctrl+Z."),
    };
}

/// <summary>Local browsing and validated insertion, also usable without an Avalonia window.</summary>
public static class CatalogBrowser
{
    /// <summary>
    /// The first reason the application blocks a use, or <see cref="CatalogBlockReason.None"/>.
    /// Broken and incomplete applications block every use. A DCA application stays insertable,
    /// but no plug-in is hosted here, so parameter editing and download are unsupported.
    /// <see cref="CatalogApplicationProgram.RequiresExternalSoftware"/> never blocks.
    /// </summary>
    public static CatalogBlockReason Blocked(CatalogApplicationProgram? program, CatalogUse use)
    {
        if (program is null) return CatalogBlockReason.None;
        if (program.Broken) return CatalogBlockReason.Broken;
        if (program.DownloadInfoIncomplete) return CatalogBlockReason.DownloadInfoIncomplete;
        if (program.RequiresDca && use is CatalogUse.EditParameters or CatalogUse.Download)
            return CatalogBlockReason.DcaUnsupported;
        return CatalogBlockReason.None;
    }

    /// <summary>Filter without catalogue context: product fields only.</summary>
    public static List<CatalogProduct> Filter(IEnumerable<CatalogProduct> products, string? search, string? section) =>
        Filter(products, search, section, null);

    /// <summary>
    /// Filter with catalogue context, so the search also covers manufacturer, catalogue name,
    /// application text and communication-object datapoint types (e.g. "1.001" or "DPST-1-1").
    /// Scans the precomputed search text only: never parses a program part or builds a parameter model.
    /// </summary>
    public static List<CatalogProduct> Filter(KnxProductCatalog? catalog, string? search, string? section) =>
        Filter(catalog?.Products ?? [], search, section, catalog);

    private static List<CatalogProduct> Filter(IEnumerable<CatalogProduct> products, string? search,
        string? section, KnxProductCatalog? catalog)
    {
        var query = search?.Trim() ?? "";
        var normalized = CatalogSearchText.Normalize(query);
        var datapoint = normalized.Length == 0 ? null : CatalogSearchText.TryNormalizeDatapoint(normalized);
        var matches = new List<CatalogProduct>();
        foreach (var product in products)
        {
            if (!string.IsNullOrEmpty(section) && !product.CatalogPaths.Contains(section)) continue;
            if (normalized.Length == 0 || Matches(product, normalized, datapoint, catalog))
                matches.Add(product);
        }
        return matches;
    }

    private static bool Matches(CatalogProduct product, string query, string? datapoint, KnxProductCatalog? catalog)
    {
        var productText = product.SearchText.Length > 0
            ? product.SearchText
            : CatalogSearchText.Product(product, catalog);
        if (productText.Contains(query, StringComparison.Ordinal)) return true;
        if (catalog is null) return false;

        // Metadata only: a split archive's program part is never pulled in by a keystroke.
        var program = catalog.FindProgramSummaryFor(product);
        if (program is null) return false;
        var programText = program.SearchText.Length > 0
            ? program.SearchText
            : CatalogSearchText.Program(program);
        if (programText.Contains(query, StringComparison.Ordinal)) return true;
        return datapoint is { Length: > 0 } && program.NormalizedDatapointTypes.Length > 0
            && program.NormalizedDatapointTypes.Contains(";" + datapoint + ";", StringComparison.Ordinal);
    }

    /// <summary>Distinct datapoint types declared by the product's application program (metadata only).</summary>
    public static List<string> DatapointTypes(KnxProductCatalog? catalog, CatalogProduct? product)
    {
        if (catalog is null || product is null) return [];
        try
        {
            return catalog.FindProgramSummaryFor(product)?.ComObjects
                .Select(comObject => comObject.DatapointType)
                .Where(type => !string.IsNullOrWhiteSpace(type))
                .Select(type => type!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? [];
        }
        catch (Exception) { return []; }
    }

    public static List<string> Sections(IEnumerable<CatalogProduct> products) => products
        .SelectMany(p => p.CatalogPaths).Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();

    public static List<LineChoice> Lines(Project? project) => project is null ? [] :
        (from installation in project.Installations
         from area in installation.Areas
         from line in area.Lines
         select new LineChoice(project.Id, project.Guid, installation.Id, area.Id, line.Id,
             $"{installation.Name ?? installation.Id} · {area.Address}.{line.Address}  {line.Name}".Trim())).ToList();

    /// <summary>Segments of the chosen line; empty when the line has none or the choice is stale.</summary>
    public static List<SegmentChoice> Segments(Project? project, LineChoice? choice)
    {
        if (project is null || Resolve(project, choice) is not { } target) return [];
        return target.Line.Segments
            .Select(segment => new SegmentChoice(segment.Id, segment.Name is { Length: > 0 } name
                ? CatalogUiText.Get("SegmentLabel", segment.Address) + " · " + name
                : CatalogUiText.Get("SegmentLabel", segment.Address)))
            .ToList();
    }

    /// <summary>Lowest free device address on the chosen line; null when the line is full or the choice is stale.</summary>
    public static string? SuggestAddress(Project? project, LineChoice? choice)
    {
        if (project is null || Resolve(project, choice) is not { } target) return null;
        try { return ProjectQuery.NextFreeDeviceAddress(project, target.Area.Address, target.Line.Address).ToString(); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Free device name on the chosen line. Existing names are never reused; a conflict gets
    /// the next " (2)", " (3)" suffix. Returns the requested name when no target is known.
    /// </summary>
    public static string UniqueName(Project? project, LineChoice? choice, string? requested)
    {
        var name = string.IsNullOrWhiteSpace(requested) ? "Device" : requested.Trim();
        if (project is null || Resolve(project, choice) is not { } target) return name;
        var used = target.Line.Devices.Concat(target.Line.Segments.SelectMany(segment => segment.Devices))
            .Select(device => device.Name?.Trim())
            .Where(existing => !string.IsNullOrEmpty(existing))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(name)) return name;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static (Area Area, Line Line)? Resolve(Project? project, LineChoice? choice)
    {
        if (project is null || choice is null
            || choice.ProjectId != project.Id || choice.ProjectGuid != project.Guid) return null;
        var targets = (from installation in project.Installations
                       where installation.Id == choice.InstallationId
                       from area in installation.Areas where area.Id == choice.AreaId
                       from line in area.Lines where line.Id == choice.LineId
                       select (Area: area, Line: line)).ToList();
        return targets.Count == 1 ? targets[0] : null;
    }

    public static bool Insert(MainWindowViewModel vm, CatalogProduct? product, CatalogApplicationProgram? program,
        LineChoice? choice, string? requestedAddress, string? name, out string message, string? roomId = null,
        string? segmentId = null)
    {
        message = Strings.Get("StatusNoProject");
        if (vm.Project is not { } project) return false;
        message = Strings.Get("CatalogNeedProduct");
        if (product is null) return false;
        var blocked = Blocked(program, CatalogUse.Insert);
        if (blocked != CatalogBlockReason.None)
        {
            message = CatalogUiText.Reason(blocked);
            return false;
        }
        message = Strings.Get("CatalogNeedLine");
        if (Resolve(project, choice) is not { } target) return false;
        var (targetArea, targetLine) = target;
        Segment? segment = null;
        if (segmentId is not null)
        {
            segment = targetLine.Segments.FirstOrDefault(candidate => candidate.Id == segmentId);
            if (segment is null) { message = CatalogUiText.Get("SegmentMissing"); return false; }
        }
        else if (targetLine.Segments.Count > 0)
        {
            // Compatibility: without a segment choice the first segment receives the device.
            segment = targetLine.Segments[0];
        }
        SpaceNode? room = null;
        if (roomId is not null)
        {
            room = project.Installations.SelectMany(i => i.Locations.AllNodes)
                .FirstOrDefault(n => n.Id == roomId && n.Kind == SpaceKind.Room);
            if (room is null) { message = CatalogUiText.Get("NeedRoom"); return false; }
        }
        message = Strings.Get("CatalogInvalidAddress");
        if (targetArea.Address > 15 || targetLine.Address > 15) return false;
        IndividualAddress address;
        if (string.IsNullOrWhiteSpace(requestedAddress))
        {
            try { address = ProjectQuery.NextFreeDeviceAddress(project, targetArea.Address, targetLine.Address); }
            catch (InvalidOperationException) { message = Strings.Get("CatalogLineFull"); return false; }
        }
        else
        {
            // Validate before packing: the current Core address constructor masks overflowing nibbles.
            var parts = requestedAddress.Trim().Split('.');
            if (parts.Length != 3 || !byte.TryParse(parts[0], out var a) || a > 15
                || !byte.TryParse(parts[1], out var l) || l > 15
                || !IndividualAddress.TryParse(requestedAddress.Trim(), out address)) return false;
        }
        if (address.Area != targetArea.Address || address.Line != targetLine.Address || address.Device == 0) return false;
        message = Strings.Get("CatalogDuplicateAddress", address);
        if (ProjectQuery.FindDevice(project, address) is not null) return false;

        var requestedName = string.IsNullOrWhiteSpace(name) ? product.Name : name.Trim();
        var finalName = UniqueName(project, choice, requestedName);
        var device = CatalogInstaller.CreateDevice(product, program, address, finalName);
        // Segment placement and room assignment belong to the same undo step as the device.
        vm.Edit($"{product.Name} → {device.Name}", p =>
        {
            if (segment is not null) segment.Devices.Add(device);
            else targetLine.Devices.Add(device);
            if (room is not null)
                p.Installations.SelectMany(i => i.Locations.AllNodes)
                    .FirstOrDefault(n => n.Id == room.Id)?.AssignDevice(device);
        });
        vm.SelectedDevice = vm.Devices.FirstOrDefault(row => ReferenceEquals(row.Device, device));
        message = Strings.Get("CatalogInserted", product.Name, address);
        if (!string.Equals(finalName, requestedName, StringComparison.Ordinal))
            message += " · " + CatalogUiText.Get("NameAdjusted", finalName);
        return true;
    }
}

/// <summary>Result of one background catalogue search; <see cref="Version"/> rejects stale results.</summary>
public sealed record CatalogSearchResult(int Version, IReadOnlyList<CatalogProduct> Products);

/// <summary>
/// Debounced, versioned search for the catalogue pane. Filtering and the debounce run off the
/// caller's thread (the UI thread), and a superseded request returns null instead of results.
/// </summary>
public static class CatalogSearch
{
    private static int _nextVersion;

    public static async Task<CatalogSearchResult?> RunAsync(KnxProductCatalog? catalog, string? query,
        string? section, int debounceMilliseconds, CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _nextVersion);
        try
        {
            if (debounceMilliseconds > 0)
                await Task.Delay(debounceMilliseconds, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return null;
            var products = await Task.Run(() => CatalogBrowser.Filter(catalog, query, section),
                cancellationToken).ConfigureAwait(false);
            return cancellationToken.IsCancellationRequested
                ? null
                : new CatalogSearchResult(version, products);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}

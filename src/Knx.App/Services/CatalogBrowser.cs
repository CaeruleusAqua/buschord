using Knx.Catalog;

namespace Knx.App.Services;

public sealed record LineChoice(string ProjectId, string? ProjectGuid, string InstallationId,
    string AreaId, string LineId, string Text);

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
        ["RemoveFailed"] = ("Katalog nicht entfernt (außerhalb der Bibliothek oder gesperrt): {0}",
                            "Catalogue not removed (outside the library or locked): {0}"),
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

    public static List<CatalogProduct> Filter(IEnumerable<CatalogProduct> products, string? search, string? section)
    {
        var query = search?.Trim() ?? "";
        return products.Where(p => (string.IsNullOrEmpty(section) || p.CatalogPaths.Contains(section)) &&
            (query.Length == 0 || new[] { p.Name, p.OrderNumber, p.Id, p.Description }
                .Any(text => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))).ToList();
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

    /// <summary>Lowest free device address on the chosen line; null when the line is full or the choice is stale.</summary>
    public static string? SuggestAddress(Project? project, LineChoice? choice)
    {
        if (project is null || choice is null
            || choice.ProjectId != project.Id || choice.ProjectGuid != project.Guid) return null;
        var targets = (from installation in project.Installations
                       where installation.Id == choice.InstallationId
                       from area in installation.Areas where area.Id == choice.AreaId
                       from line in area.Lines where line.Id == choice.LineId
                       select (Area: area, Line: line)).ToList();
        if (targets.Count != 1) return null;
        try { return ProjectQuery.NextFreeDeviceAddress(project, targets[0].Area.Address, targets[0].Line.Address).ToString(); }
        catch (InvalidOperationException) { return null; }
    }

    public static bool Insert(MainWindowViewModel vm, CatalogProduct? product, CatalogApplicationProgram? program,
        LineChoice? choice, string? requestedAddress, string? name, out string message)
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
        if (choice is null || choice.ProjectId != project.Id || choice.ProjectGuid != project.Guid) return false;
        var targets = (from installation in project.Installations
                       where installation.Id == choice.InstallationId
                       from area in installation.Areas where area.Id == choice.AreaId
                       from line in area.Lines where line.Id == choice.LineId
                       select (Area: area, Line: line)).ToList();
        if (targets.Count != 1) return false;
        var (targetArea, target) = targets[0];
        message = Strings.Get("CatalogInvalidAddress");
        if (targetArea.Address > 15 || target.Address > 15) return false;
        IndividualAddress address;
        if (string.IsNullOrWhiteSpace(requestedAddress))
        {
            try { address = ProjectQuery.NextFreeDeviceAddress(project, targetArea.Address, target.Address); }
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
        if (address.Area != targetArea.Address || address.Line != target.Address || address.Device == 0) return false;
        message = Strings.Get("CatalogDuplicateAddress", address);
        if (ProjectQuery.FindDevice(project, address) is not null) return false;

        var device = CatalogInstaller.CreateDevice(product, program, address,
            string.IsNullOrWhiteSpace(name) ? product.Name : name.Trim());
        vm.Edit($"{product.Name} → {device.Name}", _ => ProjectQuery.AddToLine(target, device));
        vm.SelectedDevice = vm.Devices.FirstOrDefault(row => ReferenceEquals(row.Device, device));
        message = Strings.Get("CatalogInserted", product.Name, address);
        return true;
    }
}

using Knx.Catalog;

namespace Knx.App.Services;

public sealed record LineChoice(string ProjectId, string? ProjectGuid, string InstallationId,
    string AreaId, string LineId, string Text);

/// <summary>Local browsing and validated insertion, also usable without an Avalonia window.</summary>
public static class CatalogBrowser
{
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

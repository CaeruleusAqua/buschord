namespace Knx.Catalog;

/// <summary>
/// Builds the lowercase search text that <see cref="CatalogBrowser"/> scans. The text is computed
/// once when a catalogue is read or loaded from the index, so a keystroke never touches XML,
/// application programs or communication objects.
/// </summary>
public static class CatalogSearchText
{
    public static string Normalize(string? text) => text?.ToLowerInvariant() ?? "";

    /// <summary>Product fields plus the catalogue/manufacturer identity, as one lowercase text.</summary>
    public static string Product(CatalogProduct product, KnxProductCatalog? catalog) =>
        Normalize(string.Join('\n',
            product.Name, product.OrderNumber, product.Id, product.Description,
            catalog?.Name, catalog?.Manufacturer?.Id, catalog?.Manufacturer?.Name,
            string.Join(' ', product.ProgramRefIds)));

    /// <summary>Program metadata, communication-object names/ids and raw datapoint types.</summary>
    public static string Program(CatalogApplicationProgram program) =>
        Normalize(string.Join('\n',
            program.Name, program.Id, program.ApplicationNumber, program.ApplicationVersion,
            program.RegistrationStatus, program.MaskVersion,
            string.Join('\n', program.ComObjects.Select(comObject =>
                string.Join('\n', comObject.Name, comObject.Id, comObject.DatapointType)))));

    /// <summary>Normalised datapoint ids in the form ";dpst-1-1;dpst-1-2;" (lowercase, sorted).</summary>
    public static string DatapointTypes(IEnumerable<ComObject> comObjects)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var comObject in comObjects)
            if (TryNormalizeDatapoint(comObject.DatapointType) is { } type) types.Add(type);
        return types.Count == 0 ? "" : ";" + string.Join(';', types) + ";";
    }

    /// <summary>Normalises "DPST-1-1", "DPT-1", "1.001" or "1.1" for the datapoint set lookup.</summary>
    public static string? TryNormalizeDatapoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return ProjectWizard.NormaliseDpt(value).ToLowerInvariant(); }
        catch (Exception) { return null; }
    }
}

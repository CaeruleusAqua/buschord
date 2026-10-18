namespace Knx;

public sealed record ProductReplaceResult(DeviceInstance Device, string OldRefId, string NewRefId, int KeptLinks);

/// <summary>
/// "Produkt austauschen": points devices of one product at another product/application
/// program. Group address links survive because com-object references keep their ids;
/// when the replacement device numbers its objects differently the links must be
/// re-checked afterwards (project check reports dangling links).
/// </summary>
public static class ProductReplacer
{
    public static IReadOnlyList<ProductReplaceResult> Replace(
        Project project,
        string oldProductRefId,
        string newProductRefId,
        string? newHardware2ProgramRefId = null,
        bool dryRun = false)
    {
        var results = new List<ProductReplaceResult>();

        foreach (var device in ProjectQuery.DeviceList(project))
        {
            var matchesProduct = device.ProductRefId == oldProductRefId;
            var matchesProgram = device.Hardware2ProgramRefId == oldProductRefId;
            if (!matchesProduct && !matchesProgram) continue;

            var keptLinks = device.ComObjectRefs.Sum(r => r.Links.Count);
            var old = matchesProduct ? device.ProductRefId : device.Hardware2ProgramRefId!;

            if (!dryRun)
            {
                if (matchesProduct) device.ProductRefId = newProductRefId;
                if (newHardware2ProgramRefId is { Length: > 0 })
                    device.Hardware2ProgramRefId = newHardware2ProgramRefId;
                else if (matchesProgram)
                    device.Hardware2ProgramRefId = newProductRefId;
            }

            results.Add(new ProductReplaceResult(device, old, newProductRefId, keptLinks));
        }

        return results;
    }

    /// <summary>Devices that still use the given product reference.</summary>
    public static IReadOnlyList<DeviceInstance> Find(Project project, string productRefId)
        => ProjectQuery.DeviceList(project)
            .Where(d => d.ProductRefId == productRefId || d.Hardware2ProgramRefId == productRefId)
            .ToList();
}

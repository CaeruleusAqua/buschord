namespace Knx.Catalog;

/// <summary>
/// Turns a catalogue product into a device instance for the topology. Group address links
/// are added later by the user; the created refs only carry the catalogue identity.
/// </summary>
public static class CatalogInstaller
{
    public static DeviceInstance CreateDevice(
        CatalogProduct product,
        CatalogApplicationProgram? program,
        IndividualAddress address,
        string name)
    {
        var device = new DeviceInstance
        {
            Id = address.ToString(),
            Address = address,
            Name = name,
            ProductRefId = product.Id,
            Hardware2ProgramRefId = product.EntryRefIds.FirstOrDefault()
                ?? product.Hardware2ProgramIds.FirstOrDefault()
                ?? program?.Id,
        };

        if (program is null) return device;
        foreach (var comObject in program.ComObjects)
        {
            var (text, functionText) = program.ComObjectDisplay.TryGetValue(comObject.Id, out var display)
                ? display
                : (comObject.Name, comObject.Name);
            device.ComObjectRefs.Add(new ComObjectInstanceRef
            {
                Id = comObject.Id,
                ComObjectId = comObject.Id,
                Number = comObject.Number,
                Text = text,
                FunctionText = functionText,
            });
        }
        return device;
    }
}

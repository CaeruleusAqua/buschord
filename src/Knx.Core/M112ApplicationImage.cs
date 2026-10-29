using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>A declared absolute load segment. Unknown bits must be filled from the same device before loading.</summary>
public sealed record M112LoadSegment(byte ObjectIndex, byte SegmentType, ushort Address, ushort Size,
    byte Access, byte MemoryType, byte Flags, ImmutablePatchBytes Data, ImmutablePatchBytes Validity);
public sealed record M112ObjectStorage(ushort Number, bool Active, uint SizeInBit, bool ReadOnInitEnabled);

/// <summary>Detached, bounded M112 full-load recipe; it requires identity and before-image checks on the bus.</summary>
public sealed class M112ApplicationImage
{
    private readonly NativeImageBuildBinding _binding;
    private readonly ArchivedImageSourceBinding _sources;
    private readonly string _inputHash;
    public string Fingerprint { get; }
    public string LayoutFingerprint { get; }
    public string Address { get; }
    public ushort Manufacturer { get; }
    public ushort ApplicationNumber { get; }
    public byte ApplicationVersion { get; }
    public byte PeiType { get; }
    public ImmutablePatchBytes HardwareType { get; }
    public ushort LoadControlAddress { get; }
    public IReadOnlyList<ushort> LoadStatusAddresses { get; }
    public IReadOnlyList<M112LoadSegment> Segments { get; }
    public IReadOnlyList<LoadProcedureControl> Controls { get; }
    public int UnionCount { get; }
    public ushort CommunicationTableAddress { get; }
    public IReadOnlyList<M112ObjectStorage> Objects { get; }
    public bool IsCurrent(Project project, DeviceInstance device) => _binding.IsCurrent(project, device);
    public bool AreInputsCurrent(Project project, DeviceInstance device)
    {
        try { return _sources.IsCurrent(project, device) && NativeCommunicationInputs.Capture(project, device).Fingerprint == _inputHash; }
        catch (Exception ex) when (ex is NativeCommunicationRejected or InvalidOperationException or ArgumentException or IOException or FormatException or OverflowException) { return false; }
    }

    internal M112ApplicationImage(NativeImageBuildPlan image, DeviceInstance device, XElement application, XDocument master,
        IReadOnlyList<NativeCommunicationSlot> slots, bool preserveUnlinkedTypes, ArchivedImageSourceBinding sources, string inputHash)
    {
        _sources = sources; _inputHash = inputHash;
        if (!image.AllRequestedAssumptionsAvailable || image.MaskVersionId != "MV-0701"
            || image.Policy?.ByteOrder != ParameterPatchByteOrder.BigEndian || image.Binding is null)
            throw new InvalidOperationException("A complete supported M112 working image is required.");
        var variant = image.Variants.Single();
        if (variant.AssumedOptions != 255 || variant.Tables?.Format != GeneratedTableFormat.M112
            || variant.Tables.ProcedureAdjustments.Count != 0 || variant.Procedure?.State != NativeImageBuildProcedureState.Compiled
            || variant.Chunks.Any(c => c.Target.Space != NativeImageBuildSpace.StandardMemory))
            throw new InvalidOperationException("Unsupported M112 image or table geometry.");
        var objectTable = variant.Tables.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
        if (slots.Count != objectTable.Data[0]) throw new InvalidOperationException("Communication storage facts do not match the compiled table.");
        CommunicationTableAddress = checked((ushort)objectTable.ByteOffset);
        Objects = Array.AsReadOnly(slots.Select(s => new M112ObjectStorage(s.Number, s.Active && !s.IsPlaceholder, s.SizeInBit, s.ReadOnInitEnabled)).ToArray());
        var ns = application.Name.Namespace;
        var sourceMask = master.Descendants().Single(e => e.Name.LocalName == "MaskVersion" && (string?)e.Attribute("Id") == "MV-0701");
        if (sourceMask.Descendants().Single(e => e.Name.LocalName == "Feature" && (string?)e.Attribute("Name") == "PropertyMappedLsms").Attribute("Value")?.Value != "0")
            throw new InvalidOperationException("This M112 loader requires the declared memory-mapped load controls.");
        var controlNames = new[] { "GroupAddressTable", "GroupAssociationTable", "Application" };
        var statuses = new List<ushort>();
        foreach (var name in controlNames)
        {
            var control = Resource(name + "LoadControl"); var status = Resource(name + "LoadStatus");
            var type = control.Elements().Single(e => e.Name.LocalName == "ResourceType");
            if (Location(control) != 0x104 || (string?)type.Attribute("Flavour") != "LoadControl_M112" || (string?)type.Attribute("Length") != "12"
                || (string?)status.Elements().Single(e => e.Name.LocalName == "ResourceType").Attribute("Length") != "1")
                throw new InvalidOperationException("Unsupported M112 control resource.");
            statuses.Add(Location(status));
        }
        if (!statuses.SequenceEqual(new ushort[] { 0xB6EA, 0xB6EB, 0xB6EC })) throw new InvalidOperationException("Unsupported M112 status mapping.");
        LoadControlAddress = 0x104; LoadStatusAddresses = statuses.AsReadOnly();
        XElement Resource(string name) => sourceMask.Descendants().Single(e => e.Name.LocalName == "Resource" && (string?)e.Attribute("Name") == name && (string?)e.Attribute("Access") == "remote");
        static ushort Location(XElement resource)
        {
            var location = resource.Elements().Single(e => e.Name.LocalName == "Location");
            if ((string?)location.Attribute("AddressSpace") != "StandardMemory") throw new InvalidOperationException("A standard-memory resource is required.");
            return U16(location, "StartAddress");
        }
        var stat = application.Element(ns + "Static") ?? throw new InvalidOperationException("Missing static application.");
        var admitted = new HashSet<string>(["Code", "ParameterTypes", "Parameters", "ParameterRefs", "ComObjectTable", "ComObjectRefs",
            "AddressTable", "AssociationTable", "LoadProcedures", "Options"], StringComparer.Ordinal);
        if (stat.Elements().Any(e => e.Name.Namespace != ns || !admitted.Contains(e.Name.LocalName))
            || application.Descendants().Any(e => e.Name.LocalName is "Script" or "ModuleDef" or "Module" or "Fixup"
                or "Extension" or "BinaryData" or "Security" or "RfChannel")
            || (string?)application.Attribute("DynamicTableManagement") == "true")
            throw new InvalidOperationException("M112 finalization cannot account for extra application writers.");
        Manufacturer = ushort.Parse(image.ApplicationId!.AsSpan(2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        ApplicationNumber = U16(application, "ApplicationNumber");
        ApplicationVersion = U8(application, "ApplicationVersion"); PeiType = U8(application, "PeiType");
        Address = device.Address.ToString(); _binding = image.Binding;
        UnionCount = stat.Element(ns + "Parameters")?.Elements(ns + "Union").Count() ?? 0;
        Controls = variant.Procedure.EffectiveControls;
        var controls = Controls.ToArray();
        if (controls.Length is < 15 or > 64 || controls[0].Kind != LoadControlKind.Connect
            || controls[1].Kind != LoadControlKind.CompareProperty || controls[^2].Kind != LoadControlKind.Restart
            || controls[^1].Kind != LoadControlKind.Disconnect || controls.Any(c => c.OnError.Count != 0))
            throw new InvalidOperationException("Unsupported M112 full-load control sequence.");
        var compare = controls[1];
        if (Arg(compare, "ObjIdx") != 0 || Arg(compare, "PropId") != 78 || Arg(compare, "Occurrence") != 0
            || Arg(compare, "StartElement") != 1 || Arg(compare, "Count") != 1
            || compare.Arguments.Any(a => a.Kind == LoadControlArgumentKind.Boolean && a.BooleanValue != false)
            || compare.InlineData is not { Length: 10 } hardware || compare.InlineMask is not null
            || Enumerable.Range(6, 4).Any(i => hardware[i] != 0))
            throw new InvalidOperationException("Only the six-byte hardware-type comparison with zero control padding is supported.");
        HardwareType = new(hardware.ToArray()[..6]);
        var position = 2;
        for (byte oi = 1; oi <= 3; oi++) Expect(LoadControlKind.Unload, oi);
        var segments = new List<M112LoadSegment>();
        for (byte oi = 1; oi <= 3; oi++)
        {
            Expect(LoadControlKind.Load, oi);
            var count = 0;
            while (position < controls.Length && controls[position].Kind == LoadControlKind.AbsoluteSegment)
            {
                var control = controls[position++];
                if (Arg(control, "LsmIdx") != oi || Arg(control, "Occurrence") != 0)
                    throw new InvalidOperationException("Unexpected segment owner.");
                var address = checked((ushort)Arg(control, "Address")); var size = checked((ushort)Arg(control, "Size"));
                var kind = checked((byte)Arg(control, "SegType")); var memory = checked((byte)Arg(control, "MemType"));
                var access = checked((byte)Arg(control, "Access")); var flags = checked((byte)Arg(control, "SegFlags"));
                if (size == 0 || address < 0x100 || address + size > 0x10000 || kind > 1 || memory is not (2 or 3)
                    || memory == 3 && (kind != 0 || access != 255 || flags != 128)
                    || memory == 2 && (access != 0 || flags != 0)
                    || segments.Any(s => address < s.Address + s.Size && s.Address < address + size))
                    throw new InvalidOperationException("Unsupported or overlapping M112 load segment.");
                var data = new byte[size]; var mask = new byte[size];
                foreach (var chunk in variant.Chunks.Where(c => c.ByteOffset < address + size && address < c.ByteOffset + c.Length))
                {
                    var start = Math.Max((uint)address, chunk.ByteOffset); var end = Math.Min((uint)(address + size), chunk.ByteOffset + (uint)chunk.Length);
                    for (var a = start; a < end; a++)
                    {
                        var source = checked((int)(a - chunk.ByteOffset)); var target = checked((int)(a - address));
                        data[target] = chunk.WorkingData[source]; mask[target] = chunk.DesiredValidity?[source] ?? 255;
                    }
                }
                if (oi == 1)
                {
                    if (size < 3) throw new InvalidOperationException("The address table has no individual-address entry.");
                    mask[1] = mask[2] = 0;
                }
                // Unlinked descriptor size bits are not part of this same-configuration reload. Retain them from the device.
                var objects = variant.Tables.Payloads.Single(p => p.Role == GeneratedTableRole.CommunicationObject);
                var associations = variant.Tables.Payloads.Single(p => p.Role == GeneratedTableRole.Association).Data;
                var linked = Enumerable.Range(0, associations[0]).Select(i => (int)associations[2 + 2 * i]).ToHashSet();
                if (memory == 3 && objects.ByteOffset >= address && objects.ByteOffset + objects.Length <= address + size)
                    for (var entry = 0; entry < objects.Data[0]; entry++)
                    {
                        var descriptor = checked((int)(objects.ByteOffset - address) + 3 + 4 * entry);
                        if (!linked.Contains(entry) && (preserveUnlinkedTypes || !Objects[entry].Active)) mask[descriptor + 3] &= 0xC0;
                    }
                segments.Add(new(oi, kind, address, size, access, memory, flags, new(data), new(mask))); count++;
            }
            if (count == 0 || oi < 3 && (count != 1 || segments[^1].MemoryType != 3))
                throw new InvalidOperationException("Missing table or application load segments.");
            var task = Expect(LoadControlKind.TaskSegment, oi);
            if (Arg(task, "Address") != segments.First(s => s.ObjectIndex == oi && s.MemoryType == 3).Address)
                throw new InvalidOperationException("Task entry is outside its declared persistent segment.");
            Expect(LoadControlKind.LoadCompleted, oi);
        }
        if (position != controls.Length - 2) throw new InvalidOperationException("Unaccounted M112 load control.");
        Segments = Array.AsReadOnly(segments.ToArray());
        foreach (var record in variant.Population!.Records.Where(r => r.WritesData && r.Field is not null))
            if (!Segments.Any(s => s.MemoryType == 3 && s.Address <= record.Field!.ByteOffset
                && s.Address + s.Size >= record.Field.ByteOffset + (record.Field.BitOffset + record.Field.BitLength + 7) / 8))
                throw new InvalidOperationException("A parameter writer is outside the loaded segments.");
        foreach (var source in variant.Sources.Where(s => s.Kind == NativeImageBuildSourceKind.NativeCode))
            if (!Segments.Any(s => s.Address <= source.ByteOffset && s.Address + s.Size >= source.ByteOffset + source.Length))
                throw new InvalidOperationException("Application code is outside the loaded segments.");
        foreach (var table in variant.Tables.Payloads)
            if (table.Target.Space != NativeImageBuildSpace.StandardMemory
                || !Segments.Any(s => s.MemoryType == 3 && s.Address <= table.ByteOffset
                    && s.Address + s.Size >= table.ByteOffset + table.Length))
                throw new InvalidOperationException("A generated table is outside the loaded image.");
        // The first address-table entry is the current individual address. It is not an address-change operation.
        var addressTable = variant.Tables.Payloads.Single(p => p.Role == GeneratedTableRole.GroupAddress);
        if (addressTable.ByteOffset != Segments.Single(s => s.ObjectIndex == 1).Address)
            throw new InvalidOperationException("Unsupported individual-address table placement.");
        LayoutFingerprint = NativeGeneralHash.Of(new { profile = "M112AbsoluteLayout1", image.ApplicationDefinitionHash,
            image.HardwareDefinitionHash, image.MaskVersionId, Manufacturer, ApplicationNumber, ApplicationVersion, PeiType,
            hardware = HardwareType.Sha256, LoadControlAddress, LoadStatusAddresses, CommunicationTableAddress,
            segments = Segments.Select(s => new { s.ObjectIndex, s.SegmentType, s.Address, s.Size, s.Access, s.MemoryType, s.Flags }) });
        Fingerprint = NativeGeneralHash.Of(new { profile = "M112AbsoluteFullLoad1", source = image.Binding.Fingerprint,
            Address, Manufacturer, ApplicationNumber, ApplicationVersion, PeiType, hardware = HardwareType.Sha256,
            segments = Segments.Select(s => new { s.ObjectIndex, s.SegmentType, s.Address, s.Size, s.Access, s.MemoryType,
                s.Flags, data = s.Data.Sha256, validity = s.Validity.Sha256 }), UnionCount, Objects, preserveUnlinkedTypes });

        LoadProcedureControl Expect(LoadControlKind kind, byte oi)
        {
            if (position >= controls.Length || controls[position].Kind != kind || Arg(controls[position], "LsmIdx") != oi
                || Arg(controls[position], "Occurrence") != 0) throw new InvalidOperationException("Unexpected M112 load order.");
            return controls[position++];
        }
    }

    internal static ulong Arg(LoadProcedureControl control, string name)
        => control.Arguments.Single(a => a.Name == name).UnsignedValue ?? throw new InvalidOperationException("Missing numeric control argument.");
    private static ushort U16(XElement e, string name) => ushort.Parse((string?)e.Attribute(name) ?? "", CultureInfo.InvariantCulture);
    private static byte U8(XElement e, string name) => byte.Parse((string?)e.Attribute(name) ?? "", CultureInfo.InvariantCulture);
}

public static class OfflineM112ApplicationBuild
{
    public static M112ApplicationImage Build(Project project, DeviceInstance device)
        => BuildCore(project, device, preserveUnlinkedTypes: true);

    internal static M112ApplicationImage BuildTarget(Project project, DeviceInstance device)
        => BuildCore(project, device, preserveUnlinkedTypes: false);

    private static M112ApplicationImage BuildCore(Project project, DeviceInstance device, bool preserveUnlinkedTypes)
    {
        var inputs = ArchivedImageSources.ReadInputs(project, device);
        var application = inputs.Application?.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram")
            ?? throw new InvalidOperationException("Application source is unavailable.");
        var working = NativeGeneralImageBuild.BuildProfile(project, device, [255], new(GeneratedTableFormat.M112), admitRootUnions: true);
        var image = new NativeImageBuildPlan(working, [255]);
        if (inputs.Manifest.ApplicationDefinitionHash != image.ApplicationDefinitionHash || inputs.Manifest.Binding?.IsCurrent(project, device) != true)
            throw new InvalidOperationException("Application source changed during image preparation.");
        return new(image, device, application,
            inputs.Master ?? throw new InvalidOperationException("The mask profile is unavailable."),
            working.Variants.SingleOrDefault()?.Tables?.Slots ?? Array.Empty<NativeCommunicationSlot>(), preserveUnlinkedTypes,
            inputs.Manifest.Binding!, working.ProjectInputHash ?? throw new InvalidOperationException("Image inputs have no binding."));
    }
}

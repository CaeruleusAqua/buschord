using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;

internal static class RootPropertyBuildMcpFixture
{
    internal static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    internal const string ManufacturerId = "M-FFE7";
    internal const string AppId = ManufacturerId + "_A-0100-10-0000";
    internal const string HardwareId = ManufacturerId + "_H-rootproperty";
    internal const string HardwareToProgramId = HardwareId + "_I-1_A-0100-10-0000";
    internal const string DeviceId = "D-rootproperty";
    internal const string DeviceAddress = "1.1.1";
    internal const string MaskId = "MV-0705";
    internal const int FieldCount = 130;
    internal const int FieldBytes = sizeof(uint);
    internal const int ByteLength = FieldCount * FieldBytes; // 520, not a subset ceiling.
    internal const ushort PropertyElementCount = ByteLength / 2; // 260 PDT_UNSIGNED_INT elements.
    internal const uint SavedFirstValue = 16909060; // 0x01020304.
    internal const string SavedFirstLexeme = "16909060";
    internal const string OptionName = "root_property_image_build_preview";
    internal const string ProjectionName = "offlineRootPropertyBuild";
    internal const string ProcedureJson = "{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}";
    internal static string OptionJson => "\"" + OptionName + "\":{\"procedure\":" + ProcedureJson + "}";

    // Arrays are new independent BCL values on each call; no codec/product output is consulted.
    internal static byte[] ExpectedWords(uint first = SavedFirstValue, int count = FieldCount)
    {
        var bytes = new byte[checked(count * FieldBytes)];
        for (var i = 0; i < count; i++)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * FieldBytes, FieldBytes), i == 0 ? first : (uint)i + 1);
        return bytes;
    }
    internal static byte[] ExpectedValidity(int count = FieldCount) => Enumerable.Repeat((byte)0xFF, checked(count * FieldBytes)).ToArray();
    internal static byte[] ExpectedZeroInitial(int count = FieldCount) => new byte[checked(count * FieldBytes)];
    internal static byte[] DeclaredInlineAction(byte value = 0xCC, int count = FieldCount) => Enumerable.Repeat(value, checked(count * FieldBytes)).ToArray();
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static XElement Program(XDocument app) => app.Descendants(Ns + "ApplicationProgram").Single();
    internal static XElement Static(XDocument app) => Program(app).Element(Ns + "Static")!;
    private static XElement Write(byte inlineByte, int count) => new(Ns + "LdCtrlWriteProp",
        new XAttribute("ObjIdx", 3), new XAttribute("PropId", 10), new XAttribute("StartElement", 1),
        new XAttribute("Count", checked(count * FieldBytes / 2)), new XAttribute("Verify", false),
        new XAttribute("InlineData", Convert.ToHexString(DeclaredInlineAction(inlineByte, count))));

    internal static XDocument Application(bool conditionalProcedure = false, int count = FieldCount)
    {
        var app = XDocument.Parse($$"""
            <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="{{ManufacturerId}}"><ApplicationPrograms>
            <ApplicationProgram Id="{{AppId}}" MaskVersion="{{MaskId}}" MinEtsVersion="6.0" LoadProcedureStyle="ProductProcedure"><Static>
            <Options ParameterByteOrder="BigEndian" DownloadInvisibleParameters="DefaultValue"/>
            <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="32" minInclusive="0" maxInclusive="4294967295"/></ParameterType></ParameterTypes>
            <Parameters/><ParameterRefs/><LoadProcedures/>
            </Static><Dynamic/></ApplicationProgram></ApplicationPrograms></Manufacturer></ManufacturerData></KNX>
            """);
        var stat = Static(app);
        stat.Element(Ns + "Parameters")!.Add(Enumerable.Range(0, count).Select(i =>
            new XElement(Ns + "Parameter", new XAttribute("Id", "P" + i), new XAttribute("ParameterType", "T"),
                new XAttribute("Value", i + 1), new XElement(Ns + "Property", new XAttribute("ObjectIndex", 3),
                    new XAttribute("PropertyId", 10), new XAttribute("Offset", i * FieldBytes), new XAttribute("BitOffset", 0)))));
        stat.Element(Ns + "ParameterRefs")!.Add(Enumerable.Range(0, count).Select(i =>
            new XElement(Ns + "ParameterRef", new XAttribute("Id", "R" + i), new XAttribute("RefId", "P" + i))));
        Program(app).Element(Ns + "Dynamic")!.Add(Enumerable.Range(0, count).Select(i =>
            new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "R" + i))));
        var write = Write(0xCC, count);
        XElement action = conditionalProcedure
            ? new(Ns + "choose", new XAttribute("ParamRefId", "R0"),
                new XElement(Ns + "when", new XAttribute("test", SavedFirstLexeme), write),
                new XElement(Ns + "when", new XAttribute("default", true), Write(0xDD, count)))
            : write;
        stat.Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure",
            new XElement(Ns + "LdCtrlConnect"), action, new XElement(Ns + "LdCtrlDisconnect")));
        return app;
    }

    internal static XDocument Master() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><MasterData><MaskVersions><MaskVersion Id="{{MaskId}}" MaskVersion="1797">
        <HawkConfigurationData LegacyVersion="0"><Features><Feature Name="FirstAppObjectIdx" Value="5"/></Features>
        <InterfaceObjects><InterfaceObject Index="3" ObjectType="100"><Property PropertyID="10" PropertyDataType="PDT_UNSIGNED_INT"/></InterfaceObject></InterfaceObjects>
        </HawkConfigurationData></MaskVersion></MaskVersions></MasterData></KNX>
        """); // No Regions, Code, Resources or any of the five pointer descriptors.

    internal static XDocument Hardware() => XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ManufacturerData><Manufacturer RefId="{{ManufacturerId}}"><Hardware><Hardware Id="{{HardwareId}}"><Hardware2Programs>
        <Hardware2Program Id="{{HardwareToProgramId}}"><ApplicationProgramRef RefId="{{AppId}}"/></Hardware2Program>
        </Hardware2Programs></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
        """); // Absent LoadedImage; not an empty/invalid encoded factory blob.

    internal static Project Project()
    {
        var project = new Project { Id = "P-rootproperty-mcp", Name = "Synthetic complete property MCP",
            Guid = "0000ffe7-0000-0000-0000-000000000130" };
        var installation = new Installation { Id = "I-rootproperty" };
        var area = new Area { Id = "A-rootproperty", Address = 1 };
        var line = new Line { Id = "L-rootproperty", Address = 1 };
        var device = new DeviceInstance { Id = DeviceId, Name = "Synthetic property device", ProductRefId = AppId,
            Hardware2ProgramRefId = HardwareToProgramId, Address = new(1, 1, 1) };
        device.ParameterValues["R0"] = SavedFirstLexeme;
        line.Devices.Add(device); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        return project;
    }

    // Only caller-owned, uniquely named Temp directories. No shared catalogs, assets or private projects.
    internal static string CreateArchive(string directory, string name = "root-property", bool conditionalProcedure = false, int count = FieldCount,
        Action<XDocument>? editApplication = null, Action<XDocument>? editMaster = null, Action<XDocument>? editHardware = null)
    {
        Directory.CreateDirectory(directory);
        var app = Application(conditionalProcedure, count); var master = Master(); var hardware = Hardware();
        editApplication?.Invoke(app); editMaster?.Invoke(master); editHardware?.Invoke(hardware);
        var path = Path.Combine(directory, name + ".knxproj");
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (entry, document) in new[]
        {
            ("0.xml", Knxproj.ToXml(Project())), (ManufacturerId + "/" + AppId + ".xml", app),
            ("knx_master.xml", master), (ManufacturerId + "/Hardware.xml", hardware),
        })
        {
            using var output = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            output.Write(document.ToString(SaveOptions.DisableFormatting));
        }
        return path;
    }

    internal static void AddControllerChoices(XDocument app, int count)
    {
        var procedure = app.Descendants(Ns + "LoadProcedure").Single();
        var disconnect = procedure.Element(Ns + "LdCtrlDisconnect")!;
        disconnect.AddBeforeSelf(Enumerable.Range(0, count).Select(i =>
            new XElement(Ns + "choose", new XAttribute("ParamRefId", "R" + i),
                new XElement(Ns + "when", new XAttribute("test", i == 0 ? SavedFirstLexeme : ((uint)i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new XElement(Ns + "LdCtrlRestart")))));
    }

    internal static IEnumerable<ushort> AdditionalPropertyIds(int allocationCount)
        => Enumerable.Range(11, allocationCount + 1).Where(id => id != 27).Take(allocationCount - 1).Select(id => checked((ushort)id));

    internal static void AddPropertyAllocations(XDocument app, int allocationCount)
    {
        var disconnect = app.Descendants(Ns + "LdCtrlDisconnect").Single();
        disconnect.AddBeforeSelf(AdditionalPropertyIds(allocationCount).Select(pid => new XElement(Ns + "LdCtrlWriteProp",
            new XAttribute("ObjIdx", 3), new XAttribute("PropId", pid), new XAttribute("StartElement", 1),
            new XAttribute("Count", 1), new XAttribute("Verify", false))));
    }

    internal static void AddPropertyDeclarations(XDocument master, int allocationCount)
        => master.Descendants(Ns + "InterfaceObject").Single().Add(AdditionalPropertyIds(allocationCount)
            .Select(pid => new XElement(Ns + "Property", new XAttribute("PropertyID", pid), new XAttribute("PropertyDataType", "PDT_UNSIGNED_INT"))));

    // Independent native source framing: one full Data record and one separate Count record.
    internal static void SetFactoryWithObservedCount(XDocument hardware)
    {
        using var raw = new MemoryStream();
        using (var writer = new BinaryWriter(raw, Encoding.UTF8, true))
        {
            writer.Write(0x00010001u); writer.Write(2);
            void Record(uint offset, byte[] recordData)
            {
                writer.Write(4u); writer.Write(offset); writer.Write((ushort)3); writer.Write((ushort)10);
                writer.Write((uint)recordData.Length); writer.Write((byte)3); writer.Write(recordData);
                writer.Write(Enumerable.Repeat((byte)0xFF, recordData.Length).ToArray());
            }
            Record(0, Enumerable.Repeat((byte)0x7F, ByteLength).ToArray());
            Record(0xFFFFFFFE, [0, 2]);
        }
        var data = raw.ToArray(); var stored = new byte[data.Length + 5]; stored[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(1), checked((ushort)data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(stored.AsSpan(3), unchecked((ushort)~data.Length));
        data.CopyTo(stored, 5);
        hardware.Descendants(Ns + "Hardware2Program").Single().SetAttributeValue("LoadedImage", Convert.ToBase64String(stored));
    }

    internal static string Arguments(string? option = null, bool invalidGatewaySentinel = false)
        => "{\"device_address\":\"" + DeviceAddress + "\"" + (invalidGatewaySentinel ? ",\"gateway\":\"invalid-root-property-gateway\"" : "")
            + "," + (option ?? OptionJson) + "}";

    // Raw JSON is intentional: serializing a dictionary would erase duplicate-property test inputs.
    internal static IEnumerable<(string Name, string Option)> StrictInvalidOptions()
    {
        string Wrap(string value) => "\"" + OptionName + "\":" + value;
        foreach (var literal in new[] { "false", "true", "null", "0", "\"yes\"", "[]", "{}" })
            yield return ("option " + literal, Wrap(literal));
        yield return ("duplicate top-level", OptionJson + "," + OptionJson);
        yield return ("unknown root field", Wrap("{\"procedure\":" + ProcedureJson + ",\"values\":[]}"));
        yield return ("duplicate procedure", Wrap("{\"procedure\":" + ProcedureJson + ",\"procedure\":" + ProcedureJson + "}"));
        foreach (var literal in new[] { "false", "null", "\"all\"", "[]", "{}" })
            yield return ("procedure " + literal, Wrap("{\"procedure\":" + literal + "}"));
        foreach (var field in new[] { "full", "parameters", "groups" })
        {
            var original = "\"" + field + "\":" + (field == "full" ? "true" : "false");
            foreach (var replacement in new[] { "null", "0", "\"false\"" })
                yield return (field + " wrong kind " + replacement, OptionJson.Replace(original, "\"" + field + "\":" + replacement, StringComparison.Ordinal));
            yield return ("duplicate " + field, OptionJson.Replace(original, original + "," + original, StringComparison.Ordinal));
            yield return ("missing " + field, OptionJson.Replace("," + original, "", StringComparison.Ordinal));
        }
        yield return ("duplicate subtype", OptionJson.Replace("\"subtype\":\"all\"", "\"subtype\":\"all\",\"subtype\":\"par\"", StringComparison.Ordinal));
        yield return ("duplicate access", OptionJson.Replace("\"access\":\"remote\"", "\"access\":\"remote\",\"access\":\"remote\"", StringComparison.Ordinal));
        yield return ("unknown subtype", OptionJson.Replace("\"all\"", "\"unknown\"", StringComparison.Ordinal));
        yield return ("nonstring subtype", OptionJson.Replace("\"subtype\":\"all\"", "\"subtype\":1", StringComparison.Ordinal));
        yield return ("wrong access", OptionJson.Replace("\"remote\"", "\"local\"", StringComparison.Ordinal));
        yield return ("nonstring access", OptionJson.Replace("\"access\":\"remote\"", "\"access\":false", StringComparison.Ordinal));
        yield return ("no parameter part", OptionJson.Replace("\"full\":true", "\"full\":false", StringComparison.Ordinal));
        yield return ("groups only", OptionJson.Replace("\"full\":true", "\"full\":false", StringComparison.Ordinal).Replace("\"groups\":false", "\"groups\":true", StringComparison.Ordinal));
        yield return ("unknown nested field", OptionJson.Replace("\"groups\":false", "\"groups\":false,\"generated_tables\":{}", StringComparison.Ordinal));
    }
}

using System.Xml.Linq;
using Knx;

internal static class M112UnionBuildChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;

    internal static ArchivedImageSourceChecks.Fixture Fixture(string directory, Action<XElement>? edit = null, bool fullLoad = false)
    {
        var app = GeneratedTableSerializerChecks.Application(GeneratedTableFormat.M112);
        var program = app.Descendants(Ns + "ApplicationProgram").Single();
        program.SetAttributeValue("MaskVersion", "MV-0701");
        program.SetAttributeValue("MinEtsVersion", "3.0d");
        var stat = program.Element(Ns + "Static")!;
        var types = XElement.Parse($$"""
            <ParameterTypes xmlns="{{Ns}}">
              <ParameterType Id="Älias"><TypeNumber Type="unsignedInt" SizeInBit="4" minInclusive="0" maxInclusive="15"/></ParameterType>
              <ParameterType Id="U8"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType>
              <ParameterType Id="U16"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="65535"/></ParameterType>
              <ParameterType Id="Empty"><TypeNone/></ParameterType>
            </ParameterTypes>
            """);
        var parameters = new XElement(Ns + "Parameters"); var references = new XElement(Ns + "ParameterRefs");
        var dynamic = program.Element(Ns + "Dynamic")!.Elements().Single();
        var data = new byte[128];
        for (var i = 0; i < 37; i++)
        {
            var wide = i < 12;
            data[i * 2] = 0xA1; data[i * 2 + 1] = 0xB2;
            parameters.Add(new XElement(Ns + "Union", new XAttribute("SizeInBit", wide ? 16 : 8),
                new XElement(Ns + "Memory", new XAttribute("CodeSegment", "PARAM"), new XAttribute("Offset", i * 2), new XAttribute("BitOffset", 0)),
                new XElement(Ns + "Parameter", new XAttribute("Id", "Default" + i), new XAttribute("ParameterType", wide ? "U16" : "U8"),
                    new XAttribute("Offset", 0), new XAttribute("BitOffset", 0), new XAttribute("Value", 0), new XAttribute("DefaultUnionParameter", true)),
                new XElement(Ns + "Parameter", new XAttribute("Id", "Alias" + i), new XAttribute("ParameterType", "Älias"),
                    new XAttribute("Offset", 0), new XAttribute("BitOffset", 0), new XAttribute("Value", 5))));
            references.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "Init" + i), new XAttribute("RefId", "Default" + i)),
                new XElement(Ns + "ParameterRef", new XAttribute("Id", "Ref" + i), new XAttribute("RefId", "Alias" + i)));
            dynamic.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "Ref" + i)));
        }
        references.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "InactiveInitialOne"), new XAttribute("RefId", "Default0"), new XAttribute("Value", 1)),
            new XElement(Ns + "ParameterRef", new XAttribute("Id", "InactiveInitialTwo"), new XAttribute("RefId", "Default0"), new XAttribute("Value", 2)));
        parameters.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Heading"), new XAttribute("ParameterType", "Empty"), new XAttribute("Value", "")));
        references.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "HeadingRef"), new XAttribute("RefId", "Heading")));
        dynamic.Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "HeadingRef")));
        stat.Add(types, parameters, references, new XElement(Ns + "Options", new XAttribute("DownloadInvisibleParameters", "Background")));
        stat.Element(Ns + "Code")!.Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "PARAM"), new XAttribute("Address", 0x5000),
            new XAttribute("Size", data.Length), new XElement(Ns + "Data", Convert.ToBase64String(data))));
        edit?.Invoke(program);
        var master = GeneratedTableSerializerChecks.Master(GeneratedTableFormat.M112);
        var mask = master.Descendants(Ns + "MaskVersion").Single();
        mask.SetAttributeValue("Id", "MV-0701"); mask.SetAttributeValue("MaskVersion", 1793);
        if (fullLoad)
        {
            program.SetAttributeValue("ApplicationNumber", 256); program.SetAttributeValue("ApplicationVersion", 16); program.SetAttributeValue("PeiType", 1);
            stat.Element(Ns + "Code")!.Elements(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "PARAM").SetAttributeValue("Address", 0x4420);
            var controls = stat.Descendants(Ns + "LoadProcedure").Single(); controls.RemoveNodes();
            controls.Add(new XElement(Ns + "LdCtrlConnect"), new XElement(Ns + "LdCtrlCompareProp", new XAttribute("ObjIdx", 0), new XAttribute("PropId", 78), new XAttribute("InlineData", "00000000000100000000")));
            for (var oi = 1; oi <= 3; oi++) controls.Add(new XElement(Ns + "LdCtrlUnload", new XAttribute("LsmIdx", oi)));
            foreach (var (oi, address, size) in new[] { (1, 0x4000, 16), (2, 0x4201, 16), (3, 0x4400, 160) })
            {
                controls.Add(new XElement(Ns + "LdCtrlLoad", new XAttribute("LsmIdx", oi)));
                if (oi == 3)
                    foreach (var (start, length, kind) in new[] { (0x700, 8, 0), (0x708, 1, 1) })
                    {
                        stat.Element(Ns + "Code")!.Add(new XElement(Ns + "AbsoluteSegment", new XAttribute("Id", "RAM" + kind), new XAttribute("Address", start), new XAttribute("Size", length), new XAttribute("MemoryType", "RAM")));
                        controls.Add(new XElement(Ns + "LdCtrlAbsSegment", new XAttribute("LsmIdx", oi), new XAttribute("SegType", kind), new XAttribute("Address", start), new XAttribute("Size", length), new XAttribute("Access", 0), new XAttribute("MemType", 2), new XAttribute("SegFlags", 0)));
                    }
                controls.Add(
                    new XElement(Ns + "LdCtrlAbsSegment", new XAttribute("LsmIdx", oi), new XAttribute("SegType", 0), new XAttribute("Address", address), new XAttribute("Size", size), new XAttribute("Access", 255), new XAttribute("MemType", 3), new XAttribute("SegFlags", 128)),
                    new XElement(Ns + "LdCtrlTaskSegment", new XAttribute("LsmIdx", oi), new XAttribute("Address", address)), new XElement(Ns + "LdCtrlLoadCompleted", new XAttribute("LsmIdx", oi)));
            }
            controls.Add(new XElement(Ns + "LdCtrlRestart"), new XElement(Ns + "LdCtrlDisconnect"));
            var config = mask.Element(Ns + "HawkConfigurationData")!;
            config.Add(new XElement(Ns + "Features", new XElement(Ns + "Feature", new XAttribute("Name", "PropertyMappedLsms"), new XAttribute("Value", 0))));
            foreach (var (name, oi) in new[] { ("GroupAddressTable", 1), ("GroupAssociationTable", 2), ("Application", 3) })
                foreach (var suffix in new[] { "LoadControl", "LoadStatus" })
                    config.Element(Ns + "Resources")!.Add(new XElement(Ns + "Resource", new XAttribute("Name", name + suffix), new XAttribute("Access", "remote"),
                        new XElement(Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", suffix == "LoadControl" ? 0x104 : 0xB6E9 + oi)),
                        new XElement(Ns + "ResourceType", new XAttribute("Length", suffix == "LoadControl" ? 12 : 1), new XAttribute("Flavour", "LoadControl_M112"))));
        }
        var fixture = ArchivedImageSourceChecks.Load(directory, app: app, master: master, hardware: PropertyInitializationChecks.Hardware());
        fixture.Project.Installations.Single().Areas.Single().Lines.Single().MediumTypeRefId = "MT-0";
        return fixture;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool result) { checks++; Console.WriteLine((result ? "PASS " : "FAIL ") + "M112Unions " + name); if (!result) failures++; }
        static NativeImageBuildPlan Build(ArchivedImageSourceChecks.Fixture f) => OfflineM112ImageBuild.Preview(f.Project, f.Device);
        static string Explain(NativeImageBuildPlan p) => string.Join(';', p.Issues.Select(i => i.ToString()).Concat(p.Variants.SelectMany(v => v.Issues).Select(i => i.ToString())));
        var source = Fixture(directory); var before = Knxproj.ToXml(source.Project).ToString();
        var image = Build(source); var variant = image.Variants.SingleOrDefault();
        Check("37 mixed-width unions and source controllers compile: " + Explain(image), image.AllRequestedAssumptionsAvailable);
        if (variant?.IsAvailable == true)
        {
            var chunk = variant.Chunks.Single(c => c.ByteOffset <= 0x5000 && c.ByteOffset + c.Length >= 0x5080);
            var offset = checked((int)(0x5000 - chunk.ByteOffset));
            for (var i = 0; i < 37; i++)
            {
                Check("active nibble overlays initialized code " + i, chunk.WorkingData[offset + i * 2] == 0x51);
                Check("unclaimed initialized bits survive " + i, chunk.WorkingData[offset + i * 2 + 1] == 0xB2);
            }
            Check("empty metadata has no physical storage", variant.Population!.NoStorageCount == 1);
            Check("read-only plan carries no execution permission", !image.DownloadEligible && !image.CompleteApplicationImage && !image.ExecutionEligible);
            Check("binding starts current", image.Binding?.IsCurrent(source.Project, source.Device) == true);
            source.Device.ParameterValues["Ref0"] = "6";
            Check("saved alias changes stale the image", image.Binding?.IsCurrent(source.Project, source.Device) == false);
            source.Device.ParameterValues.Remove("Ref0");
        }
        Check("image construction preserves project", Knxproj.ToXml(source.Project).ToString() == before);
        var strict = OfflineNativeImageBuild.Preview(source.Project, source.Device, new([255], new(GeneratedTableFormat.M112)));
        Check("ordinary preview keeps its earlier union boundary", !strict.IsAvailable);

        foreach (var (name, edit) in new (string, Action<XElement>)[]
        {
            ("missing default", p => p.Descendants(Ns + "Parameter").First(e => (string?)e.Attribute("Id") == "Default0").Attribute("DefaultUnionParameter")!.Remove()),
            ("incomplete initialized bytes", p => p.Descendants(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "PARAM").Element(Ns + "Data")!.Value = "AA=="),
            ("unknown initialization mask", p => p.Descendants(Ns + "AbsoluteSegment").Single(e => (string?)e.Attribute("Id") == "PARAM").Add(new XElement(Ns + "Mask", Convert.ToBase64String(new byte[128])))),
            ("alias exceeds union", p => p.Descendants(Ns + "Parameter").First(e => (string?)e.Attribute("Id") == "Alias0").SetAttributeValue("Offset", 2)),
            ("overlapping unions", p => p.Descendants(Ns + "Union").Skip(1).First().Element(Ns + "Memory")!.SetAttributeValue("Offset", 0)),
            ("unsupported byte order", p => p.Descendants(Ns + "Options").Single().SetAttributeValue("ParameterByteOrder", "LittleEndian")),
        })
            Check(name + " fails closed", !Build(Fixture(directory, edit)).IsAvailable);

        var conflict = Build(Fixture(directory, p =>
        {
            var stat = p.Element(Ns + "Static")!;
            stat.Element(Ns + "Parameters")!.Element(Ns + "Union")!.Add(new XElement(Ns + "Parameter", new XAttribute("Id", "Conflict"),
                new XAttribute("ParameterType", "Älias"), new XAttribute("Offset", 0), new XAttribute("BitOffset", 0), new XAttribute("Value", 6)));
            stat.Element(Ns + "ParameterRefs")!.Add(new XElement(Ns + "ParameterRef", new XAttribute("Id", "ConflictRef"), new XAttribute("RefId", "Conflict")));
            p.Element(Ns + "Dynamic")!.Elements().Single().Add(new XElement(Ns + "ParameterRefRef", new XAttribute("RefId", "ConflictRef")));
        }));
        Check("conflicting active aliases publish no partial payload", !conflict.IsAvailable && conflict.Variants.All(v => v.Chunks.Count == 0));
        Console.WriteLine($"M112Unions: {checks} checks, {failures} failures");
        return failures;
    }
}

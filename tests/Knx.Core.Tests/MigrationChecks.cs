using System.Xml.Linq;
using Knx;
using Knx.Catalog;
using Knx.Workbench;

internal static class MigrationChecks
{
    private const string Old = "M-0001_A-old", Next = "M-0001_A-new";
    private sealed record Fixture(Project Project, DeviceInstance Device, XDocument Source, XDocument Target);

    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  PASS " : "  FAIL ") + "Migration: " + name); if (!ok) failures++; }
        void Reject(string name, Fixture fixture, Action action)
        {
            var before = Knxproj.ToXml(fixture.Project).ToString(); var rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException) { rejected = true; }
            Check(name, rejected && Knxproj.ToXml(fixture.Project).ToString() == before);
        }
        var f = Create(); var sourceBefore = f.Source.ToString(); var targetBefore = f.Target.ToString();
        var baseline = Knxproj.ToXml(f.Project).ToString();
        var plan = Preview(f);
        Check("preview is detached and lists retained values, links and target numbers", Knxproj.ToXml(f.Project).ToString() == baseline
            && plan.Parameters.Single() == new ProductParameterMigration(Old + "_P-1_R-1", Next + "_P-1_R-1", "7")
            && plan.Objects.Single().SourceId == Old + "_O-1_R-1" && plan.Objects.Single().TargetId == Next + "_O-1_R-1"
            && plan.Objects.Single().OldNumber == 1 && plan.Objects.Single().NewNumber == 2 && plan.Objects.Single().KeptLinks == 1
            && Knxproj.LoadApplicationProgram(f.Project, Next) is null);
        ProductReplacer.Apply(f.Project, plan);
        var obj = f.Device.ComObjectRefs.Single(); var metadata = f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, obj.Id).ToString()];
        Check("apply migrates references and explicit values without suffix guessing", f.Device.ProductRefId == Next
            && f.Device.Hardware2ProgramRefId == Next && f.Device.ParameterValues.Count == 1 && f.Device.ParameterValues[Next + "_P-1_R-1"] == "7"
            && obj.Id == "O-1_R-1" && obj.ComObjectId == "O-1" && obj.Number == 2);
        Check("links, text, function, connector facts, role and channel overrides survive", obj.Links.SequenceEqual(["G-1"])
            && obj.Text == "User text" && obj.FunctionText == "User function" && obj.Device == "opaque-device-fact"
            && metadata.Role == "switch" && metadata.ChannelId == "CH-1" && metadata.SendingGroupId == "G-1"
            && metadata.Connectors["G-1"] is { Send: true, Acknowledge: true, Priority: 2, Slot: 3 }
            && f.Device.Channels.Single().Name == "User channel");
        Check("source and target documents survive in an independent archive overlay", Knxproj.LoadApplicationProgram(f.Project, Old) is not null
            && Knxproj.LoadApplicationProgram(f.Project, Next) is not null && f.Source.ToString() == sourceBefore && f.Target.ToString() == targetBefore);
        Check("replacement clears unproven old Secure capability and increments the revision", f.Project.Workbench.Devices[f.Device.Id].SecureCapable is null
            && f.Project.Workbench.Devices[f.Device.Id].Revision == 6 && f.Project.Workbench.Devices[f.Device.Id].DownloadedRevision == 4);
        Reject("applied plans cannot be reused", f, () => ProductReplacer.Apply(f.Project, plan));

        var channelFixture = Create();
        AddObject(channelFixture.Target, "O-2", 3);
        var channelNamespace = channelFixture.Target.Root!.Name.Namespace;
        var targetChannel = new XElement(channelNamespace + "Channel", new XAttribute("Id", Next + "_CH-2"), new XAttribute("Text", "Target second channel"));
        channelFixture.Target.Descendants(channelNamespace + "Dynamic").Single().Add(targetChannel);
        foreach (var placement in channelFixture.Target.Descendants(channelNamespace + "ComObjectRefRef").ToArray())
        { placement.Remove(); targetChannel.Add(placement); }
        var channelMetadata = channelFixture.Project.Workbench.Objects[new ObjectKey(channelFixture.Device.Id, "O-1_R-1").ToString()];
        channelMetadata.StructuralDeclaredChannelId = "CH-1"; channelMetadata.ChannelSource = ObjectChannelSource.Derived;
        ProductReplacer.Apply(channelFixture.Project, Preview(channelFixture));
        Check("migration derives both existing and newly active objects from target placements", channelFixture.Device.ComObjectRefs.Count == 2
            && channelFixture.Device.ComObjectRefs.All(o => channelFixture.Project.Workbench.Objects[new ObjectKey(channelFixture.Device.Id, o.Id).ToString()].ChannelId == "CH-2")
            && channelFixture.Project.Workbench.Objects[new ObjectKey(channelFixture.Device.Id, "O-2_R-1").ToString()].StructuralDeclaredChannelName == "Target second channel");
        var channelFile = Path.Combine(Path.GetTempPath(), "migration-channels-" + Guid.NewGuid().ToString("N") + ".knxproj");
        try
        {
            Knxproj.Save(channelFixture.Project, channelFile); var reloadedChannels = Knxproj.Load(channelFile);
            Check("target channel grouping is identical immediately and after reopen", ProjectQuery.DeviceList(reloadedChannels).Single().ComObjectRefs.All(o =>
                reloadedChannels.Workbench.Objects[new ObjectKey(channelFixture.Device.Id, o.Id).ToString()].ChannelId == "CH-2"));
        }
        finally { File.Delete(channelFile); }
        var pinnedFixture = Create();
        pinnedFixture.Device.Channels.Clear(); pinnedFixture.Device.Channels.Add(new() { Id = "project-channel-instance", RefId = "CH-1", Name = "Pinned instance" });
        pinnedFixture.Device.ComObjectRefs.Single().ChannelId = "project-channel-instance";
        var pinnedMetadata = pinnedFixture.Project.Workbench.Objects[new ObjectKey(pinnedFixture.Device.Id, "O-1_R-1").ToString()];
        pinnedMetadata.ChannelId = "project-channel-instance"; pinnedMetadata.ChannelSource = ObjectChannelSource.Native;
        ProductReplacer.Apply(pinnedFixture.Project, Preview(pinnedFixture));
        Check("native channel instance identity and explicit object pin are retained through migration", pinnedFixture.Device.Channels.Single().Id == "project-channel-instance"
            && pinnedFixture.Device.Channels.Single().RefId == "CH-1" && pinnedFixture.Device.ComObjectRefs.Single().ChannelId == "project-channel-instance"
            && pinnedFixture.Project.Workbench.Objects[new ObjectKey(pinnedFixture.Device.Id, "O-1_R-1").ToString()].ChannelSource == ObjectChannelSource.Native);
        var invalidPin = Create(); invalidPin.Device.Channels.Clear();
        invalidPin.Device.Channels.Add(new() { Id = "native-instance", RefId = "CH-1", Name = "Native" });
        invalidPin.Device.ComObjectRefs.Single().ChannelId = "CH-1";
        Reject("a native object pin cannot be resolved through a declaration RefId fallback", invalidPin, () => Preview(invalidPin));

        f = Create();
        foreach (var (document, app) in new[] { (f.Source, Old), (f.Target, Next) })
            document.Descendants().Single(e => e.Name.LocalName == "ComObjects").Add(new XElement(document.Root!.Name.Namespace + "ComObject",
                new XAttribute("Id", app + "_O-unused"), new XAttribute("Number", "99")));
        Check("unused object definitions do not invent migrated references", Preview(f).Objects.Count == 1);
        f.Source.Descendants().Single(e => e.Name.LocalName == "Dynamic").Add(new XElement(f.Source.Root!.Name.Namespace + "choose",
            new XAttribute("ParamRefId", Old + "_P-1_R-1"), new XElement(f.Source.Root.Name.Namespace + "when", new XAttribute("test", "8"),
                new XElement(f.Source.Root.Name.Namespace + "ComObjectRefRef", new XAttribute("RefId", Old + "_O-unused")))));
        Reject("inactive placements cannot substitute a missing object reference", f, () => Preview(f));

        f = Create(); f.Device.ComObjectRefs[0].Number = 42;
        ProductReplacer.Apply(f.Project, Preview(f));
        Check("a valid explicit object-number override is retained separately from the new declaration", f.Device.ComObjectRefs.Single().Number == 42
            && f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-1").ToString()].StructuralDeclaredNumber == 2);
        f = Create(); f.Device.ComObjectRefs[0].Text = null; f.Device.ComObjectRefs[0].FunctionText = null;
        ProductReplacer.Apply(f.Project, Preview(f));
        Check("inherited target text comes from the target declaration", f.Device.ComObjectRefs.Single().Text == "Target text"
            && f.Device.ComObjectRefs.Single().FunctionText == "Target function");
        f = Create(); var nativeReference = f.Device.ComObjectRefs.Single();
        var nativeObject = new ComObjectInstanceRef { Id = nativeReference.Id, ComObjectId = nativeReference.Id, Number = 1, Text = "Native override" };
        nativeObject.Links.AddRange(nativeReference.Links); f.Device.ComObjectRefs.Clear(); f.Device.ComObjectRefs.Add(nativeObject);
        ProductReplacer.Apply(f.Project, Preview(f));
        Check("native ComObjectId equal to its exact RefId resolves through the declaration", f.Device.ComObjectRefs.Single().Number == 2
            && f.Device.ComObjectRefs.Single().Text == "Native override" && f.Device.ComObjectRefs.Single().Links.SequenceEqual(["G-1"]));
        f = Create(); AddModuleParameters(f.Source, 3); AddModuleParameters(f.Target, 1);
        f.Device.ParameterValues[Old + "_P-1_R-1"] = "3";
        var moduleParameters = Parameters();
        foreach (var (index, value) in new[] { (2, "22"), (3, "33") })
        {
            var id = "_MD-1_M-1_MI-" + index + "_P-1_R-1";
            f.Device.ParameterValues[Old + id] = value; moduleParameters[Old + id] = Next + id;
        }
        var modulePlan = ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target, moduleParameters, Objects(), Channels());
        ProductReplacer.Apply(f.Project, modulePlan);
        Check("mapped Count rebuilds target-default-one before saved instances two and three are validated", modulePlan.Parameters.Count == 3
            && f.Device.ParameterValues[Next + "_MD-1_M-1_MI-2_P-1_R-1"] == "22" && f.Device.ParameterValues[Next + "_MD-1_M-1_MI-3_P-1_R-1"] == "33"
            && f.Device.ParameterValues[Next + "_P-1_R-1"] == "3");
        f = Create(); AddModuleParameters(f.Source, 3); AddModuleParameters(f.Target, 1);
        f.Device.ParameterValues.Clear(); f.Device.ParameterValues["P-1_R-1"] = "3";
        f.Device.ParameterValues["MD-1_M-1_MI-2_P-1_R-1"] = "22"; f.Device.ParameterValues["MD-1_M-1_MI-3_P-1_R-1"] = "33";
        ProductReplacer.Apply(f.Project, ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target, moduleParameters, Objects(), Channels()));
        Check("native parameter IDs without application prefix shape the same detached structure", f.Device.ParameterValues[Next + "_MD-1_M-1_MI-3_P-1_R-1"] == "33");
        f = Create(); plan = Preview(f); f.Target.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("Number", "55");
        ProductReplacer.Apply(f.Project, plan);
        Check("changing caller-owned target XML after preview cannot alter a prepared migration", f.Device.ComObjectRefs.Single().Number == 2
            && Knxproj.LoadApplicationProgram(f.Project, Next)!.Descendants().Single(e => e.Name.LocalName == "ComObject").Attribute("Number")!.Value == "2");

        f = Create(); Reject("same suffix/name/number does not establish cross-application identity", f,
            () => ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target));
        f = Create(); var identical = new XDocument(f.Source);
        identical.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram").SetAttributeValue("Id", Next);
        var automatic = ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, identical);
        Check("identical complete declaration IDs with compatible storage types map automatically", automatic.Parameters.Single().TargetId == Old + "_P-1_R-1"
            && automatic.Objects.Single().NewNumber == 1);
        f = Create(); Reject("legacy reference swapping is refused before mutation", f, () => ProductReplacer.Replace(f.Project, Old, Next, Next));
        Check("legacy discovery and unchanged references remain available", ProductReplacer.Replace(f.Project, Old, Next, Next, true).Count == 1
            && ProductReplacer.Replace(f.Project, Old, Old, Old).Count == 1 && f.Device.ProductRefId == Old);
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "TypeNumber").SetAttributeValue("maxInclusive", "5");
        Reject("a saved value outside the target restriction is refused", f, () => Preview(f));
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "TypeNumber").SetAttributeValue("SizeInBit", "8");
        Reject("different parameter storage widths are refused", f, () => Preview(f));
        f = Create(); f.Device.ParameterValues[Old + "_P-1_R-1"] = "65536";
        Reject("invalid source values cannot be legitimized by a new application", f, () => Preview(f));
        f = Create();
        foreach (var document in new[] { f.Source, f.Target })
        {
            var ns = document.Root!.Name.Namespace;
            document.Descendants(ns + "TypeNumber").Single().ReplaceWith(new XElement(ns + "TypeIPAddress", new XAttribute("Version", "IPv4"), new XAttribute("AddressType", "HostAddress")));
            document.Descendants(ns + "Parameter").Single().SetAttributeValue("Value", "192.168.1.1");
        }
        f.Device.ParameterValues[Old + "_P-1_R-1"] = "192.168.1.2";
        Check("declared IP address types migrate without an invented SizeInBit attribute", Preview(f).Parameters.Single().Value == "192.168.1.2");
        f.Target.Descendants().Single(e => e.Name.LocalName == "TypeIPAddress").SetAttributeValue("AddressType", "GatewayAddress");
        Reject("IP address semantics participate in storage compatibility", f, () => Preview(f));
        f = Create();
        foreach (var document in new[] { f.Source, f.Target })
        {
            var ns = document.Root!.Name.Namespace;
            document.Descendants(ns + "TypeNumber").Single().ReplaceWith(new XElement(ns + "TypeTime", new XAttribute("SizeInBit", "16"),
                new XAttribute("Unit", "Seconds"), new XAttribute("minInclusive", "0"), new XAttribute("maxInclusive", "100"), new XAttribute("UIHint", "Duration_hhmmss")));
        }
        Check("known identical duration storage migrates without changing its integer wire value", Preview(f).Parameters.Single().Value == "7");
        f.Target.Descendants().Single(e => e.Name.LocalName == "TypeTime").SetAttributeValue("Unit", "Minutes");
        Reject("a time-unit change cannot reinterpret saved duration values", f, () => Preview(f));
        f.Target.Descendants().Single(e => e.Name.LocalName == "TypeTime").SetAttributeValue("Unit", "Seconds");
        f.Target.Descendants().Single(e => e.Name.LocalName == "TypeTime").SetAttributeValue("UIHint", "Time_ss");
        Reject("the preserved time profile participates in migration compatibility", f, () => Preview(f));
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("DatapointType", "DPST-1-2");
        Reject("equal object size does not hide a different DPT subtype", f, () => Preview(f));
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("ObjectSize", "1 Byte");
        Reject("DPT/bit-size contradictions are refused", f, () => Preview(f));
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("Number", "65536");
        Reject("out-of-range object numbers are refused", f, () => Preview(f));
        f = Create();
        f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-1").ToString()].Bits = 8;
        Reject("saved metadata overrides are validated rather than silently erased", f, () => Preview(f));
        f = Create(); ProjectQuery.FindGroupAddressById(f.Project, "G-1")!.DatapointType = "1.002";
        Reject("all retained group links are checked against target capabilities", f, () => Preview(f));
        f = Create(); f.Project.Workbench.Groups["G-1"].Secure = true;
        Reject("old Secure capability is not transferred to different hardware", f, () => Preview(f));
        var secure = ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target,
            Parameters(), Objects(), Channels(), targetSecureCapable: true);
        Check("explicitly proven target Secure capability permits compatible links", secure.Objects.Single().KeptLinks == 1);
        f = Create(); plan = Preview(f); f.Device.ParameterValues[Old + "_P-1_R-1"] = "8";
        Reject("a changed saved value makes the preview stale", f, () => ProductReplacer.Apply(f.Project, plan));
        f = Create(); plan = Preview(f); f.Project.Workbench.Objects[new ObjectKey(f.Device.Id, "O-1_R-1").ToString()].Connectors["G-1"].Priority = 1;
        Reject("a changed connector override makes the preview stale", f, () => ProductReplacer.Apply(f.Project, plan));
        f = Create(); plan = Preview(f); ProjectQuery.FindGroupAddressById(f.Project, "G-1")!.DatapointType = "1.002";
        Reject("changed group capability makes the preview stale", f, () => ProductReplacer.Apply(f.Project, plan));
        f = Create(); plan = Preview(f);
        var other = Create(); Reject("a plan cannot be applied to a different project with equal data", other, () => ProductReplacer.Apply(other.Project, plan));
        f = Create(); plan = Preview(f); other = Create("D-2"); var foreignPlan = Preview(other);
        Reject("all plans are checked before the first device is changed", f, () => ProductReplacer.Apply(f.Project, plan, foreignPlan));
        f = Create(); f.Device.ComObjectRefs[0].Number = 42; AddObject(f.Target, "O-2", 42);
        Reject("preserved number overrides cannot collide with new active objects", f, () => Preview(f));
        f = Create(); f.Device.ParameterValues["unknown"] = "9";
        Reject("unknown saved parameter IDs are never discarded", f, () => Preview(f));
        foreach (var attribute in new[] { "Broken", "DownloadInfoIncomplete", "RequiresExternalSoftware" })
        {
            f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram").SetAttributeValue(attribute, "true");
            Reject("unsupported target application profile is refused: " + attribute, f, () => Preview(f));
        }
        f = Create(); f.Source.Descendants().Single(e => e.Name.LocalName == "ParameterRef").SetAttributeValue("Plugin", "synthetic-plugin");
        Reject("source parameter plugins cannot silently pass migration", f, () => Preview(f));
        f = Create(); f.Target.Descendants().Single(e => e.Name.LocalName == "Static").Add(new XElement(f.Target.Root!.Name.Namespace + "Extension", new XAttribute("EtsUiPlugin", "synthetic-plugin")));
        Reject("DCA and UI extension declarations are explicitly unsupported", f, () => Preview(f));
        var reverseParameters = Parameters().ToDictionary(p => p.Value, p => p.Key);
        var reverseObjects = Objects().ToDictionary(p => p.Value, p => p.Key);
        var reverseChannels = Channels().ToDictionary(p => p.Value, p => p.Key);
        foreach (var entry in new[] { "M-0001/vendor.xml", "M-0001/assets/config_A-old.xml", "M-0001/M-0001_A-vendor.xml" })
        {
            f = Create();
            var opaqueParts = new Dictionary<string, byte[]> { [entry] = System.Text.Encoding.UTF8.GetBytes("<Opaque><A/> <B/></Opaque>") };
            ProductReplacer.Apply(f.Project, ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target,
                Parameters(), Objects(), Channels(), targetParts: opaqueParts));
            var changedOpaque = new Dictionary<string, byte[]> { [entry] = System.Text.Encoding.UTF8.GetBytes("<Opaque><A/><B/></Opaque>") };
            Reject("opaque XML whitespace differences stay byte-strict: " + entry, f,
                () => ProductReplacer.Preview(f.Project, f.Device, Old, Old, f.Target, f.Source,
                    reverseParameters, reverseObjects, reverseChannels, targetParts: changedOpaque));
        }
        var formatted = XDocument.Parse(f.Source.ToString().Replace("\n", "\n  ", StringComparison.Ordinal), LoadOptions.PreserveWhitespace);
        var equivalent = ProductReplacer.Preview(f.Project, f.Device, Old, Old, f.Target, formatted, reverseParameters, reverseObjects, reverseChannels);
        Check("application format whitespace may differ without changing declaration identity", equivalent.Objects.Single().NewNumber == 1 && f.Device.ProductRefId == Next);
        var changedScript = new XDocument(f.Source); changedScript.Descendants().Single(e => e.Name.LocalName == "Script").Value = "// different literal";
        Reject("different script literals retain application identity conflict protection", f,
            () => ProductReplacer.Preview(f.Project, f.Device, Old, Old, f.Target, changedScript, reverseParameters, reverseObjects, reverseChannels));

        var directory = Path.Combine(Path.GetTempPath(), "buschord-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            f = Create(); var file = Path.Combine(directory, "migrated.knxproj");
            ProductReplacer.Apply(f.Project, Preview(f)); Knxproj.Save(f.Project, file);
            var reopened = Knxproj.Load(file); var device = ProjectQuery.DeviceList(reopened).Single();
            Check("save/reopen retains both applications and the migrated parameter/link overrides", Knxproj.LoadApplicationProgram(reopened, Old) is not null
                && Knxproj.LoadApplicationProgram(reopened, Next) is not null && device.ProductRefId == Next
                && device.ParameterValues[Next + "_P-1_R-1"] == "7" && device.ComObjectRefs.Single().Links.SequenceEqual(["G-1"])
                && device.ComObjectRefs.Single().Text == "User text");
            var undo = new UndoStack(); undo.Reset(reopened);
            var reverse = ProductReplacer.Preview(reopened, device, Old, Old, f.Target, f.Source,
                Parameters().ToDictionary(p => p.Value, p => p.Key), Objects().ToDictionary(p => p.Value, p => p.Key), Channels().ToDictionary(p => p.Value, p => p.Key));
            ProductReplacer.Apply(reopened, reverse); undo.Push(reopened);
            var restored = undo.Undo()!;
            Check("existing project snapshots undo the whole migration and preserve application overlays", ProjectQuery.DeviceList(restored).Single().ProductRefId == Next
                && Knxproj.LoadApplicationProgram(restored, Old) is not null && Knxproj.LoadApplicationProgram(restored, Next) is not null
                && ProjectQuery.DeviceList(undo.Redo()!).Single().ProductRefId == Old);
            var conflicting = new XDocument(f.Target); conflicting.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("Number", "3");
            var conflictFixture = new Fixture(reopened, device, f.Source, conflicting);
            Reject("a known application identity cannot be overwritten by different manufacturer content", conflictFixture,
                () => ProductReplacer.Preview(reopened, device, Next, Next, f.Source, conflicting, Parameters(), Objects(), Channels()));
            var catalogPath = Path.Combine(directory, "catalog.xml"); f.Target.Save(catalogPath);
            var catalog = KnxProductCatalog.Open(catalogPath); var document = catalog.LoadProgramDocument(Next)!;
            document.Descendants().Single(e => e.Name.LocalName == "ComObject").SetAttributeValue("Number", "99");
            var fresh = catalog.LoadProgramDocument(Next)!;
            Check("catalog documents are detached and preserve original namespace, script and translations", fresh.Root!.Name.Namespace == f.Target.Root!.Name.Namespace
                && fresh.Descendants().Single(e => e.Name.LocalName == "ComObject").Attribute("Number")!.Value == "2"
                && fresh.Descendants().Any(e => e.Name.LocalName == "Script") && fresh.Descendants().Any(e => e.Name.LocalName == "Languages")
                && XNode.DeepEquals(XDocument.Load(catalogPath), f.Target));
            Check("plain XML catalogues expose no invented manufacturer archive parts", catalog.ReadManufacturerParts().Count == 0);
            f = Create(); var productId = "M-0001_H-target_P-target"; var programId = "M-0001_H-target_HP-1";
            var hardware = XDocument.Parse($$"""
                <KNX xmlns="http://knx.org/xml/project/23"><ManufacturerData><Manufacturer RefId="M-0001"><Hardware><Hardware Id="M-0001_H-target" Name="Synthetic hardware" BusCurrent="42">
                <Products><Product Id="{{productId}}" Text="Synthetic product" /></Products><Hardware2Programs><Hardware2Program Id="{{programId}}" MediumTypes="MT-0"><ApplicationProgramRef RefId="{{Next}}" /></Hardware2Program></Hardware2Programs>
                </Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
                """);
            var archivePath = Path.Combine(directory, "catalog.knxprod");
            using (var zip = System.IO.Compression.ZipFile.Open(archivePath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntry("M-0001/"); zip.CreateEntry("M-0001/images/");
                using (var output = zip.CreateEntry("M-0001/Hardware.xml").Open()) hardware.Save(output);
                using (var output = zip.CreateEntry("M-0001/" + Next + ".xml").Open()) f.Target.Save(output);
                using (var output = zip.CreateEntry("M-0001/" + Old + ".xml").Open()) f.Source.Save(output);
                using (var output = zip.CreateEntry("M-0001/opaque.bin").Open()) output.Write(new byte[] { 1, 2, 3 });
            }
            var archiveBefore = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath));
            var vendor = KnxProductCatalog.Open(archivePath); var parts = vendor.ReadManufacturerParts();
            Check("manufacturer transfer excludes legitimate empty ZIP directory entries", parts.Count == 4 && parts.Keys.All(name => !name.EndsWith('/')));
            var selectedParts = vendor.ReadManufacturerParts(programId);
            Check("selected migration retains hardware/resources without unrelated application copies", selectedParts.Count == 3
                && selectedParts.ContainsKey("M-0001/" + Next + ".xml") && !selectedParts.ContainsKey("M-0001/" + Old + ".xml")
                && selectedParts.ContainsKey("M-0001/Hardware.xml") && selectedParts.ContainsKey("M-0001/opaque.bin"));
            f.Project.Hardware["other-hardware"] = new() { HardwareId = "other", BusCurrent = 3 };
            var hardwareUndo = new UndoStack(); hardwareUndo.Reset(f.Project);
            var withParts = ProductReplacer.Preview(f.Project, f.Device, productId, programId, f.Source, vendor.LoadProgramDocument(Next)!,
                Parameters(), Objects(), Channels(), targetParts: parts);
            parts["M-0001/opaque.bin"][0] = 99;
            ProductReplacer.Apply(f.Project, withParts); hardwareUndo.Push(f.Project);
            Check("target hardware facts are available immediately while unrelated device facts survive", f.Project.Hardware[programId].BusCurrent == 42
                && f.Project.Hardware[productId].MediumTypeIds.SequenceEqual(["MT-0"]) && f.Project.Hardware["other-hardware"].BusCurrent == 3);
            var undoneHardware = hardwareUndo.Undo()!; var redoneHardware = hardwareUndo.Redo()!;
            Check("existing undo snapshots include detached hardware planning changes", !undoneHardware.Hardware.ContainsKey(programId)
                && undoneHardware.Hardware["other-hardware"].BusCurrent == 3 && redoneHardware.Hardware[programId].BusCurrent == 42);
            Knxproj.Save(f.Project, file);
            var migrated = Knxproj.Load(file);
            using (var output = System.IO.Compression.ZipFile.OpenRead(file))
            using (var data = output.GetEntry("M-0001/opaque.bin")!.Open())
            {
                Check("hardware resolution and private opaque manufacturer copies survive save/reopen", Knxproj.LoadApplicationProgram(migrated, programId) is not null
                    && ProjectQuery.DeviceList(migrated).Single().Hardware2ProgramRefId == programId && data.ReadByte() == 1
                    && vendor.ReadManufacturerParts()["M-0001/opaque.bin"][0] == 1
                    && System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(archivePath)).SequenceEqual(archiveBefore));
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine($"MIGRATION CHECKS: {failures} failures"); return failures;
    }

    private static Dictionary<string, string> Parameters() => new() { [Old + "_P-1_R-1"] = Next + "_P-1_R-1" };
    private static Dictionary<string, string> Objects() => new() { [Old + "_O-1_R-1"] = Next + "_O-1_R-1" };
    private static Dictionary<string, string> Channels() => new() { [Old + "_CH-1"] = Next + "_CH-1" };
    private static ProductMigrationPlan Preview(Fixture f) => ProductReplacer.Preview(f.Project, f.Device, Next, Next, f.Source, f.Target, Parameters(), Objects(), Channels());
    private static Fixture Create(string deviceId = "D-1")
    {
        var project = new Project { Id = "P-migration", Name = "Synthetic migration" };
        var installation = new Installation { Id = "I-1" }; var area = new Area { Id = "A-1", Address = 1 }; var line = new Line { Id = "L-1", Address = 1 };
        project.Installations.Add(installation); installation.Areas.Add(area); area.Lines.Add(line);
        var device = new DeviceInstance { Id = deviceId, ProductRefId = Old, Hardware2ProgramRefId = Old, Address = IndividualAddress.Parse("1.1.1") };
        line.Devices.Add(device); device.ParameterValues[Old + "_P-1_R-1"] = "7";
        device.Channels.Add(new() { Id = "CH-1", Name = "User channel" });
        var obj = new ComObjectInstanceRef { Id = "O-1_R-1", ComObjectId = "O-1", Number = 1, Text = "User text", FunctionText = "User function", Device = "opaque-device-fact" };
        obj.Links.Add("G-1"); device.ComObjectRefs.Add(obj);
        var range = new GroupRange { Id = "GR-1", RangeStart = 0, RangeEnd = 65535 };
        installation.MainRanges.Add(range); range.Addresses.Add(new() { Id = "G-1", Name = "Synthetic group", Address = GroupAddress.Parse("1/1/1"), DatapointType = "1.001" });
        project.Workbench.Groups["G-1"] = new() { Bits = 1, Secure = false };
        project.Workbench.Devices[device.Id] = new() { SecureCapable = true, Revision = 5, DownloadedRevision = 4 };
        project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new()
        {
            Dpt = "1.001", Bits = 1, Flags = "CRWT", ChannelId = "CH-1", Role = "switch", SendingGroupId = "G-1", StructuralDeclaredNumber = 1,
            Connectors = new() { ["G-1"] = new() { Send = true, Acknowledge = true, Priority = 2, Slot = 3 } },
        };
        return new(project, device, Definition(Old, 1), Definition(Next, 2));
    }
    private static XDocument Definition(string program, int number) => XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ManufacturerData><Manufacturer RefId="M-0001"><ApplicationPrograms><ApplicationProgram Id="{{program}}" Name="Synthetic">
        <Static><ParameterTypes><ParameterType Id="{{program}}_T-1" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="100" /></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="{{program}}_P-1" ParameterType="{{program}}_T-1" Value="3" /></Parameters><ParameterRefs><ParameterRef Id="{{program}}_P-1_R-1" RefId="{{program}}_P-1" /></ParameterRefs>
        <ComObjects><ComObject Id="{{program}}_O-1" Number="{{number}}" ObjectSize="1 Bit" DatapointType="DPST-1-1" Text="Target text" FunctionText="Target function" CommunicationFlag="Enabled" ReadFlag="Enabled" WriteFlag="Enabled" TransmitFlag="Enabled" /></ComObjects>
        <ComObjectRefs><ComObjectRef Id="{{program}}_O-1_R-1" RefId="{{program}}_O-1" /></ComObjectRefs><Script>// synthetic script retained, never executed</Script></Static>
        <Dynamic><Channel Id="{{program}}_CH-1" Name="Channel"><ParameterBlock Id="{{program}}_PB-1"><ParameterRefRef RefId="{{program}}_P-1_R-1" /></ParameterBlock><ComObjectRefRef RefId="{{program}}_O-1_R-1" /></Channel></Dynamic>
        </ApplicationProgram></ApplicationPrograms><Languages><Language Identifier="en-US"><TranslationUnit RefId="{{program}}"><TranslationElement RefId="{{program}}_O-1"><Translation AttributeName="Text" Text="Target text" /></TranslationElement></TranslationUnit></Language></Languages></Manufacturer></ManufacturerData></KNX>
        """);
    private static void AddObject(XDocument document, string id, int number)
    {
        var definition = new XElement(document.Descendants().Single(e => e.Name.LocalName == "ComObject"));
        definition.SetAttributeValue("Id", Next + "_" + id); definition.SetAttributeValue("Number", number); definition.Parent?.Remove();
        document.Descendants().Single(e => e.Name.LocalName == "ComObjects").Add(definition);
        var ns = definition.Name.Namespace;
        document.Descendants().Single(e => e.Name.LocalName == "ComObjectRefs").Add(new XElement(ns + "ComObjectRef", new XAttribute("Id", Next + "_" + id + "_R-1"), new XAttribute("RefId", Next + "_" + id)));
        document.Descendants().Single(e => e.Name.LocalName == "Channel").Add(new XElement(ns + "ComObjectRefRef", new XAttribute("RefId", Next + "_" + id + "_R-1")));
    }
    private static void AddModuleParameters(XDocument document, int defaultCount)
    {
        var program = document.Descendants().Single(e => e.Name.LocalName == "ApplicationProgram"); var id = (string)program.Attribute("Id")!;
        var ns = program.Name.Namespace; var topStatic = program.Elements(ns + "Static").Single();
        topStatic.Descendants(ns + "Parameter").Single().SetAttributeValue("Value", defaultCount);
        topStatic.Add(new XElement(ns + "ModuleDefs", XElement.Parse($$"""
            <ModuleDef xmlns="http://knx.org/xml/project/23" Id="{{id}}_MD-1" Name="Synthetic module"><Static>
            <Parameters><Parameter Id="{{id}}_MD-1_P-1" ParameterType="{{id}}_T-1" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="{{id}}_MD-1_P-1_R-1" RefId="{{id}}_MD-1_P-1" /></ParameterRefs></Static>
            <Dynamic><ParameterRefRef RefId="{{id}}_MD-1_P-1_R-1" /></Dynamic></ModuleDef>
            """)));
        program.Element(ns + "Dynamic")!.Descendants(ns + "Channel").Single().Add(new XElement(ns + "Repeat",
            new XAttribute("Id", id + "_X-1"), new XAttribute("Count", "0"), new XAttribute("ParameterRefId", id + "_P-1_R-1"),
            new XElement(ns + "Module", new XAttribute("Id", id + "_MD-1_M-1"), new XAttribute("RefId", id + "_MD-1"))));
    }
}

using System.Xml.Linq;
using System.IO.Compression;
using Knx;

internal static class ModuleAllocationChecks
{
    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var xml = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber SizeInBit="16" Type="unsignedInt" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-count" Text="Count" ParameterType="T" Value="1" /></Parameters><ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /></ParameterRefs>
            <Allocators><Allocator Id="A" Name="Objects" Start="3" maxInclusive="20" /></Allocators>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" Name="Base" Allocates="2" Alignment="4" /></Arguments><Static>
            <Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="T" Value="1" BaseValue="APP_MD-1_A-1" /></Parameters>
            <ParameterRefs><ParameterRef Id="APP_MD-1_P-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs>
            <ComObjects><ComObject Id="APP_MD-1_O-1" Number="1" BaseNumber="APP_MD-1_A-1" ObjectSize="1 Bit" Text="Object" /></ComObjects></Static>
            <Dynamic><ParameterRefRef RefId="APP_MD-1_P-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><ParameterRefRef RefId="R-count" /><Repeat Id="X" Name="Repeated" Count="1" ParameterRefId="R-count">
            <Module Id="APP_MD-1_M-1" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" AllocatorRefId="A" /></Module>
            </Repeat></Dynamic></ApplicationProgram></KNX>
            """);
        var parsed = Ksxml.Parse(xml, out var state);
        Check("Repeat adds literal and parameter counts and uses one-based identities", parsed.Instances.Any(p => p.Id == "APP_MD-1_M-1_MI-1_P-1_R-1")
            && parsed.Instances.Any(p => p.Id == "APP_MD-1_M-1_MI-2_P-1_R-1") && parsed.Instances.Count == 3);
        Check("Allocator keeps its unaligned first start and aligns subsequent starts", parsed.ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9 }));
        Check("Module parameter BaseValue adds the numeric argument", parsed.Instances.Where(p => p.ParameterId != "P-count").Select(p => p.Value).SequenceEqual(new[] { "4", "9" }));
        var changed = Ksxml.Parse(xml, out _, new Dictionary<string, string?> { ["R-count"] = "2" });
        Check("Saved repeat counts shape the inspected instance set", changed.ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9, 13 }));
        Check("Structural repeat controls cannot silently edit a fixed row set", !parsed.Instances.Single(p => p.Id == "R-count").IsEnabled);
        var exceeded = new XDocument(xml); exceeded.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "4");
        var invalid = Ksxml.Parse(exceeded, out var invalidState);
        Check("Allocator bounds reject the whole expansion without partial instances", invalid.ComObjects.Count == 0 && invalidState.Unsupported.Count > 0);
        var exact = new XDocument(xml); exact.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "10");
        Check("Allocator limit accepts start plus size exactly, with no minus-one adjustment", Ksxml.Parse(exact).ComObjects.Count == 2);
        exact.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "9");
        Check("Allocator limit rejects the immediately smaller bound", Ksxml.Parse(exact, out var boundary).ComObjects.Count == 0 && boundary.Unsupported.Count > 0);
        var zero = new XDocument(xml); zero.Descendants("Repeat").Single().SetAttributeValue("Count", "0");
        Check("Zero repeats publish no template instances", Ksxml.Parse(zero, out _, new Dictionary<string, string?> { ["R-count"] = "0" }).Instances.Count == 1);
        foreach (var count in new[] { "-1", "NaN", "257", "9223372036854775807" })
        {
            var rejected = Ksxml.Parse(xml, out var failure, new Dictionary<string, string?> { ["R-count"] = count });
            Check("Invalid or oversized repeat count is rejected atomically: " + count, rejected.ComObjects.Count == 0 && failure.Unsupported.Count > 0);
        }
        var literal = new XDocument(xml); literal.Descendants("NumericArg").Single().SetAttributeValue("Value", "2");
        Check("Allocator first start includes the numeric literal only once", Ksxml.Parse(literal).ComObjects.Select(o => o.Number).SequenceEqual(new[] { 6, 9 }));
        var conditional = new XDocument(xml); var repeat = conditional.Descendants("Repeat").Single();
        repeat.ReplaceWith(new XElement("choose", new XAttribute("ParamRefId", "R-count"),
            new XElement("when", new XAttribute("test", "1"), new XElement(repeat))));
        Check("Conditional repeat allocation follows its selected branch", Ksxml.Parse(conditional, out var activity).ComObjects.Count == 2 && activity.Unsupported.Count == 0);
        Check("Inactive conditional repeats publish no module objects", Ksxml.Parse(conditional, out var inactiveActivity,
            new Dictionary<string, string?> { ["R-count"] = "0" }).ComObjects.Count == 0 && inactiveActivity.Unsupported.Count == 0);
        var unsupportedBase = new XDocument(xml); unsupportedBase.Descendants("NumericArg").Single().SetAttributeValue("BaseValue", "unknown");
        Check("Unproven allocator BaseValue scopes never fall back to zero", Ksxml.Parse(unsupportedBase, out var scope).ComObjects.Count == 0 && scope.Unsupported.Count > 0);
        Check("Expansion leaves the manufacturer declaration unchanged", xml.Descendants("Repeat").Single().Attribute("ParameterRefId")?.Value == "R-count"
            && xml.Descendants("NumericArg").Single().Attribute("AllocatorRefId")?.Value == "A" && xml.Descendants("Module").Count() == 1);
        var malformed = new XDocument(xml); malformed.Descendants("Repeat").Single().SetAttributeValue("Count", "-1");
        Check("Negative literal counts cannot be rescued by a positive parameter", Ksxml.Parse(malformed, out var negative,
            new Dictionary<string, string?> { ["R-count"] = "2" }).ComObjects.Count == 0 && negative.Unsupported.Count > 0);
        var wrapper = new XDocument(xml); var wrapped = wrapper.Descendants("Repeat").Single();
        wrapped.ReplaceWith(new XElement("UnknownWrapper", new XElement(wrapped)));
        Check("Unknown structural wrappers never publish repeat instances", Ksxml.Parse(wrapper, out var unknown).ComObjects.Count == 0 && unknown.Unsupported.Count > 0);
        var nested = new XDocument(xml); var outer = nested.Descendants("Repeat").Single();
        outer.Elements().Single().ReplaceWith(new XElement("Repeat", new XAttribute("Id", "Y"), new XAttribute("Count", "2"), new XElement(outer.Elements().Single())));
        nested.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "100");
        var nestedResult = Ksxml.Parse(nested);
        Check("Nested repeats use ordered one-based dimension identities", nestedResult.Instances.Where(p => p.Id != "R-count").Select(p => p.Id)
            .SequenceEqual(new[] { "1-1", "1-2", "2-1", "2-2" }.Select(n => "APP_MD-1_M-1_MI-" + n + "_P-1_R-1")));
        var sibling = new XDocument(xml); var originalModule = sibling.Descendants("Module").Single();
        var secondModule = new XElement(originalModule); secondModule.SetAttributeValue("Id", "APP_MD-1_M-2");
        sibling.Descendants("Dynamic").Last().Add(secondModule);
        sibling.Descendants("Allocator").Single().SetAttributeValue("maxInclusive", "100");
        Check("Sibling modules share allocation in structural XML order", Ksxml.Parse(sibling).ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9, 13 }));
        var mixed = new XDocument(sibling);
        var successorDefinition = XElement.Parse(mixed.Descendants("ModuleDef").Single().ToString().Replace("APP_MD-1", "APP_MD-2", StringComparison.Ordinal));
        successorDefinition.Descendants("Argument").Single().SetAttributeValue("Alignment", "2");
        mixed.Descendants("ModuleDefs").Single().Add(successorDefinition);
        var successor = mixed.Descendants("Module").Last(); successor.SetAttributeValue("Id", "APP_MD-2_M-1"); successor.SetAttributeValue("RefId", "APP_MD-2");
        successor.Elements().Single().SetAttributeValue("RefId", "APP_MD-2_A-1");
        Check("Successor allocation uses its own alignment rather than the predecessor's", Ksxml.Parse(mixed).ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9, 11 }));
        var reevaluated = DynamicParameters.Evaluate(xml.Root!, new Dictionary<string, string?> { ["R-count"] = "2" });
        Check("Reevaluation preserves saved-count structure and locks its control", reevaluated.Parameters.ContainsKey("APP_MD-1_M-1_MI-3_P-1_R-1")
            && !reevaluated.Parameters["R-count"].IsEnabled && reevaluated.Parameters["R-count"].Value == "2");
        var unrelated = new XDocument(xml);
        unrelated.Descendants("Parameters").First().Add(new XElement("Parameter", new XAttribute("Id", "P-other"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        unrelated.Descendants("ParameterRefs").First().Add(new XElement("ParameterRef", new XAttribute("Id", "R-other"), new XAttribute("RefId", "P-other")));
        unrelated.Descendants("Dynamic").Last().Add(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-other"), new XAttribute("Value", "7")));
        var independent = Ksxml.Parse(unrelated, out var independentState);
        Check("Unrelated assignments do not block repeat allocation", independent.ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9 })
            && independentState.Parameters["R-other"].Value == "7" && independentState.Unsupported.Count == 0);
        unrelated.Descendants("Assign").Single().SetAttributeValue("TargetParamRefRef", "R-count");
        unrelated.Descendants("Assign").Single().SetAttributeValue("Value", "2");
        Check("A valid count assignment settles before central snapshot expansion", Ksxml.Parse(unrelated, out var assignedCount).ComObjects.Select(o => o.Number).SequenceEqual(new[] { 4, 9, 13 })
            && assignedCount.Parameters["R-count"].Value == "2" && !assignedCount.StructureFailed);
        var calculated = new XDocument(xml);
        var calculation = XElement.Parse("""
            <ParameterCalculation Language="JavaScript"><LParameters><ParameterRefRef RefId="R-other" AliasName="left" /></LParameters>
            <RParameters><ParameterRefRef RefId="R-other2" AliasName="right" /></RParameters><LRTransformation>right = left;</LRTransformation><RLTransformation>left = right;</RLTransformation></ParameterCalculation>
            """);
        calculated.Descendants("Static").First().Add(calculation);
        Check("Unrelated calculations preserve structure even without a runtime capability", Ksxml.Parse(calculated, out var noRuntime).ComObjects.Count == 2
            && noRuntime.Unsupported.Any(s => s.Contains("JavaScript", StringComparison.Ordinal)));
        foreach (var side in new[] { "LParameters", "RParameters" })
        {
            var unsafeCalculation = new XDocument(calculated);
            unsafeCalculation.Descendants(side).Single().Elements().Single().SetAttributeValue("RefId", "R-count");
            Check("A calculation touching a count requires a runtime capability: " + side,
                Ksxml.Parse(unsafeCalculation, out var computedCount).ComObjects.Count == 0 && computedCount.Unsupported.Count > 0);
        }
        var aliases = new XDocument(xml);
        aliases.Descendants("ParameterRefs").First().Add(new XElement("ParameterRef", new XAttribute("Id", "R-count-alias"), new XAttribute("RefId", "P-count")));
        var lockedAliases = Ksxml.Parse(aliases, out var aliasState);
        Check("All references to a structural count parameter remain read-only", lockedAliases.ComObjects.Count == 2
            && !aliasState.Parameters["R-count"].IsEnabled && !aliasState.Parameters["R-count-alias"].IsEnabled);
        aliases.Descendants("Dynamic").Last().Add(new XElement("Assign", new XAttribute("TargetParamRefRef", "R-count-alias"), new XAttribute("Value", "5")));
        Check("A typed alias assignment settles without changing a different count reference", Ksxml.Parse(aliases, out var aliasWrite).ComObjects.Count == 2
            && aliasWrite.Parameters["R-count-alias"].Value == "5" && aliasWrite.Parameters["R-count"].Value == "1" && !aliasWrite.StructureFailed);
        var scoped = new XDocument(xml);
        scoped.Descendants("ParameterRefs").Last().Add(new XElement("ParameterRef", new XAttribute("Id", "APP_MD-1_R-count"), new XAttribute("RefId", "APP_MD-1_P-1")));
        scoped.Descendants("Dynamic").First().Add(new XElement("Assign", new XAttribute("TargetParamRefRef", "APP_MD-1_R-count"), new XAttribute("Value", "7")));
        Check("A same-named module-local reference is not a root count alias", Ksxml.Parse(scoped, out var scopeState).ComObjects.Count == 2 && scopeState.Unsupported.Count == 0);
        var instantiatedAlias = new XDocument(xml);
        instantiatedAlias.Descendants("ParameterRefs").Last().Add(new XElement("ParameterRef", new XAttribute("Id", "APP_MD-1_R-global-count"), new XAttribute("RefId", "P-count")));
        instantiatedAlias.Descendants("Dynamic").First().Add(new XElement("ParameterRefRef", new XAttribute("RefId", "APP_MD-1_R-global-count")));
        var aliasInstances = Ksxml.Parse(instantiatedAlias).Instances.Where(p => p.Id.EndsWith("_R-global-count", StringComparison.Ordinal)).ToList();
        Check("Instantiated module aliases of a global count remain read-only", aliasInstances.Count == 2 && aliasInstances.All(p => p.IsVisible && !p.IsEnabled));
        var project = Knx.Workbench.WorkbenchJson.CreateDemo();
        var device = ProjectQuery.DeviceList(project).First();
        device.Hardware2ProgramRefId = null; device.ProductRefId = "M-0001_A-module";
        device.ParameterValues.Clear(); device.ParameterValues["R-count"] = "2";
        device.ComObjectRefs.Clear();
        device.ComObjectRefs.Add(new() { Id = "MD-1_M-1_MI-3_O-1", ComObjectId = "MD-1_M-1_MI-3_O-1" });
        var line = ProjectQuery.LocateDevice(project, device.Address)!.Value.Line!;
        var copy = ProjectOperations.CopyDevice(project, device, line).Value!;
        copy.ParameterValues["R-count"] = "1";
        copy.ComObjectRefs.Clear(); copy.ComObjectRefs.Add(new() { Id = "MD-1_M-1_MI-2_O-1", ComObjectId = "MD-1_M-1_MI-2_O-1" });
        var archive = Path.Combine(directory, "repeat-counts.knxproj"); Knxproj.Save(project, archive);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("0.xml")!;
            XDocument native;
            using (var input = entry.Open()) native = XDocument.Load(input);
            foreach (var number in native.Descendants().Where(e => e.Name.LocalName == "ComObjectInstanceRef")
                .Attributes(XName.Get("Number", "urn:buschord:project:1")).ToArray()) number.Remove();
            entry.Delete();
            using (var output = zip.CreateEntry("0.xml").Open()) native.Save(output);
            using (var output = zip.CreateEntry("M-0001/M-0001_A-module.xml").Open())
                XDocument.Parse(xml.ToString().Replace("APP", "M-0001_A-module", StringComparison.Ordinal)).Save(output);
        }
        var imported = Knxproj.Load(archive);
        Check("Archive metadata expansion uses each device's saved count", ProjectQuery.DeviceList(imported).Single(d => d.Id == device.Id).ComObjectRefs.Single().Number == 13
            && ProjectQuery.DeviceList(imported).Single(d => d.Id == copy.Id).ComObjectRefs.Single().Number == 9);
        return failures;
    }
}

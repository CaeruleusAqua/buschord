using System.Xml.Linq;
using System.Reflection;
using Knx;

internal static class ModuleScopeChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var application = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="16" /></ParameterType></ParameterTypes>
            <Allocators><Allocator Id="G" Start="100" maxInclusive="200" /></Allocators>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" Name="OuterBase" Allocates="20" Alignment="4" /></Arguments>
            <Static><Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="T" Value="2" /></Parameters><ParameterRefs><ParameterRef Id="APP_MD-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs>
            <Allocators><Allocator Id="APP_MD-1_L-1" Start="3" maxInclusive="10" /></Allocators></Static>
            <SubModuleDefs><ModuleDef Id="APP_MD-1_SM-1"><Arguments><Argument Id="APP_MD-1_SM-1_A-1" Name="InnerBase" Allocates="2" Alignment="4" />
            <Argument Id="APP_MD-1_SM-1_A-2" Name="LiteralBase" /></Arguments><Static>
            <Parameters><Parameter Id="APP_MD-1_SM-1_P-1" ParameterType="T" Value="1" BaseValue="APP_MD-1_SM-1_A-1" />
            <Parameter Id="APP_MD-1_SM-1_P-2" ParameterType="T" Value="1" BaseValue="APP_MD-1_SM-1_A-2" /></Parameters>
            <ParameterRefs><ParameterRef Id="APP_MD-1_SM-1_R-1" RefId="APP_MD-1_SM-1_P-1" /><ParameterRef Id="APP_MD-1_SM-1_R-2" RefId="APP_MD-1_SM-1_P-2" /></ParameterRefs>
            <ComObjects><ComObject Id="APP_MD-1_SM-1_O-1" Number="0" BaseNumber="APP_MD-1_SM-1_A-1" /></ComObjects></Static>
            <Dynamic><ParameterRefRef RefId="APP_MD-1_SM-1_R-1" /><ParameterRefRef RefId="APP_MD-1_SM-1_R-2" /></Dynamic></ModuleDef></SubModuleDefs>
            <Dynamic><ParameterRefRef RefId="APP_MD-1_R-1" /><Repeat Id="APP_MD-1_X-1" Count="0" ParameterRefId="APP_MD-1_R-1">
            <Module Id="APP_MD-1_SM-1_M-1" RefId="APP_MD-1_SM-1"><NumericArg RefId="APP_MD-1_SM-1_A-1" AllocatorRefId="APP_MD-1_L-1" BaseValue="APP_MD-1_A-1" />
            <NumericArg RefId="APP_MD-1_SM-1_A-2" Value="7" BaseValue="APP_MD-1_A-1" /></Module></Repeat></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Repeat Id="X-root" Count="2"><Module Id="APP_MD-1_M-1" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" AllocatorRefId="G" /></Module></Repeat></Dynamic>
            </ApplicationProgram></KNX>
            """);
        var before = application.ToString();
        var parsed = Ksxml.Parse(application, out var state);
        var expectedNumbers = new[] { 103, 108, 123, 128 };
        Check("Module-local allocators reset per outer instance and include the outer base", parsed.ComObjects.Select(o => o.Number).SequenceEqual(expectedNumbers)
            && state.Unsupported.Count == 0);
        var expectedIds = new[] { (1, 1), (1, 2), (2, 1), (2, 2) }.Select(pair =>
            $"APP_MD-1_M-1_MI-{pair.Item1}_SM-1_M-1_MI-{pair.Item1}-1-{pair.Item2}_O-1");
        Check("Inner repeat identities retain the complete outer multidimensional counter", parsed.ComObjects.Select(o => o.Id).SequenceEqual(expectedIds));
        Check("Nested parameter bases use their own computed child arguments", parsed.Instances.Where(p => p.Id.EndsWith("_R-1") && p.ParameterId!.Contains("_SM-1_"))
            .Select(p => p.Value).SequenceEqual(expectedNumbers.Select(n => (n + 1).ToString())));
        Check("NumericArg BaseValue without an allocator adds the outer numeric argument", parsed.Instances.Where(p => p.Id.EndsWith("_R-2"))
            .Select(p => p.Value).SequenceEqual(new[] { "108", "108", "128", "128" }));
        var saved = new Dictionary<string, string?> { ["APP_MD-1_M-1_MI-1_R-1"] = "1" };
        var reduced = Ksxml.Parse(application, out _, saved);
        Check("Module-local saved counts are isolated by their outer instance identity", reduced.ComObjects.Select(o => o.Number).SequenceEqual(new[] { 103, 123, 128 }));
        var bounds = new XDocument(application); bounds.Descendants("Allocator").Single(e => (string?)e.Attribute("Id") == "APP_MD-1_L-1").SetAttributeValue("maxInclusive", "9");
        var invalid = Ksxml.Parse(bounds, out var invalidState);
        Check("Local allocator limits subtract the base and reject the whole expansion", invalid.ComObjects.Count == 0 && invalid.Instances.Count == 0 && invalidState.Unsupported.Count > 0);
        var global = new XDocument(application);
        var nestedAllocation = global.Descendants("NumericArg").First(e => e.Attribute("AllocatorRefId")?.Value == "APP_MD-1_L-1");
        nestedAllocation.SetAttributeValue("AllocatorRefId", "G");
        Check("Shared global allocator ordering is depth-first structural order", Ksxml.Parse(global).ComObjects.Select(o => o.Number).SequenceEqual(new[] { 120, 124, 148, 152 }));
        var unknownBase = new XDocument(application); unknownBase.Descendants("NumericArg").First(e => e.Attribute("BaseValue") is not null).SetAttributeValue("BaseValue", "APP_other_A-1");
        Check("A numeric base cannot escape its outer module argument scope", Ksxml.Parse(unknownBase, out var wrongScope).ComObjects.Count == 0 && wrongScope.Unsupported.Count > 0);
        var ambiguous = new XDocument(application);
        ambiguous.Descendants("Arguments").First().Add(new XElement(ambiguous.Descendants("Argument").First()));
        Check("Duplicate module argument declarations fail as a complete unsupported snapshot", Ksxml.Parse(ambiguous, out var ambiguousState).ComObjects.Count == 0
            && ambiguousState.Unsupported.Count > 0);
        var repeatedAllocator = new XDocument(application);
        var duplicateAllocation = repeatedAllocator.Descendants("NumericArg").Single(e => (string?)e.Attribute("Value") == "7");
        duplicateAllocation.SetAttributeValue("AllocatorRefId", "APP_MD-1_L-1");
        Check("One module cannot silently allocate two segments at the same allocator order", Ksxml.Parse(repeatedAllocator, out var repeatedAllocationState).ComObjects.Count == 0
            && repeatedAllocationState.Unsupported.Count > 0);
        Check("Nested expansion never changes the manufacturer XML", application.ToString() == before);
        var expander = typeof(Ksxml).Assembly.GetType("Knx.ApplicationModules")!;
        var expanded = (XElement)expander.GetMethod("Expand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [application.Root!, null, null, false, null])!;
        var native = (IReadOnlyList<ApplicationModuleInstance>)expander.GetMethod("ReadInstances", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [expanded])!;
        Check("Native module metadata includes every outer and inner instance", native.Count == 6 && native.Select(i => i.Id).Distinct().Count() == 6);
        var outer = native.Single(i => i.Id == "MD-1_M-1_MI-1");
        Check("Native top-level module references and RepeatIndex retain original declaration order", outer.RefId == "MD-1_M-1"
            && outer.RepeatIndex == "1x1 2x1" && outer.Arguments.Single() == new ApplicationModuleArgument("MD-1_A-1", "G"));
        var inner = native.Single(i => i.Id == "MD-1_M-1_MI-1_SM-1_M-1_MI-1-1-2");
        Check("Native inner RepeatIndex includes parent module and local repeat dimensions", inner.RefId == "MD-1_M-1"
            && inner.RepeatIndex == "1x1 2x1 2x2 3x1");
        Check("Native arguments retain original allocator ids and literals rather than evaluated offsets", inner.Arguments.SequenceEqual(new[]
        { new ApplicationModuleArgument("SM-1_A-1", "MD-1_L-1"), new ApplicationModuleArgument("SM-1_A-2", "7") }));
        var failedExpanded = (XElement)expander.GetMethod("Expand", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [bounds.Root!, null, null, false, null])!;
        var failedNative = (IReadOnlyList<ApplicationModuleInstance>)expander.GetMethod("ReadInstances", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [failedExpanded])!;
        Check("Failed local expansion never publishes partial native instance metadata", failedNative.Count == 0);
        var aliases = new XDocument(application);
        aliases.Descendants("ParameterRefs").First().Add(new XElement("ParameterRef", new XAttribute("Id", "APP_MD-1_R-alias"), new XAttribute("RefId", "APP_MD-1_P-1")));
        aliases.Descendants("Dynamic").Last(e => e.Parent?.Name.LocalName == "ModuleDef")
            .AddFirst(new XElement("ParameterRefRef", new XAttribute("RefId", "APP_MD-1_R-alias")));
        var aliasRows = Ksxml.Parse(aliases, out _, editableStructure: true).Instances.Where(p => p.Id.EndsWith("_R-alias", StringComparison.Ordinal)).ToList();
        Check("Module-local structural count aliases remain locked in every outer instance", aliasRows.Count == 2 && aliasRows.All(p => p.IsVisible && !p.IsEnabled));
        aliases.Descendants("Dynamic").Last(e => e.Parent?.Name.LocalName == "ModuleDef")
            .AddFirst(new XElement("Assign", new XAttribute("TargetParamRefRef", "APP_MD-1_R-alias"), new XAttribute("Value", "3")));
        var conservativeAlias = (XElement)expander.GetMethod("Expand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [aliases.Root!, null, null, false, null])!;
        Check("A direct module-local alias assignment remains guarded by default", ReferenceEquals(conservativeAlias, aliases.Root));
        var enabledAlias = (XElement)expander.GetMethod("Expand", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [aliases.Root!, null, null, true, null])!;
        Check("Calculated-count expansion requires explicit caller opt-in", !ReferenceEquals(enabledAlias, aliases.Root)
            && ((IReadOnlyList<ApplicationModuleInstance>)expander.GetMethod("ReadInstances", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [enabledAlias])!).Count == 6);
        var conditional = new XDocument(application);
        var topStatic = conditional.Root!.Element("ApplicationProgram")!.Element("Static")!;
        topStatic.Add(new XElement("Parameters", new XElement("Parameter", new XAttribute("Id", "Mode"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "1"))));
        topStatic.Add(new XElement("ParameterRefs", new XElement("ParameterRef", new XAttribute("Id", "ModeRef"), new XAttribute("RefId", "Mode"))));
        var repeatedModule = conditional.Descendants("Module").Last();
        repeatedModule.ReplaceWith(new XElement("choose", new XAttribute("ParamRefId", "ModeRef"),
            new XElement("when", new XAttribute("test", "1"), new XElement(repeatedModule))));
        Check("A schema-valid choose inside a repeat expands the selected module branch", Ksxml.Parse(conditional, out var conditionalState).ComObjects.Select(o => o.Number).SequenceEqual(expectedNumbers)
            && conditionalState.Unsupported.Count == 0);
        Check("An inactive branch inside a repeat publishes no module objects", Ksxml.Parse(conditional, out var inactiveState,
            new Dictionary<string, string?> { ["ModeRef"] = "0" }).ComObjects.Count == 0 && inactiveState.Unsupported.Count == 0);
        var textBase = new XDocument(application);
        textBase.Descendants("Argument").First().SetAttributeValue("Type", "Text");
        var textArgument = textBase.Descendants("NumericArg").Last();
        textArgument.ReplaceWith(new XElement("TextArg", new XAttribute("RefId", "APP_MD-1_A-1"), new XAttribute("Value", "100")));
        Check("A numeric-looking text argument cannot become an outer numeric base", Ksxml.Parse(textBase, out var textBaseState).ComObjects.Count == 0
            && textBaseState.Unsupported.Count > 0);
        var mismatchedType = new XDocument(application);
        mismatchedType.Descendants("Argument").First().SetAttributeValue("Type", "Text");
        Check("Module argument occurrences must match their declared numeric or text type", Ksxml.Parse(mismatchedType, out var mismatchedTypeState).ComObjects.Count == 0
            && mismatchedTypeState.Unsupported.Count > 0);
        var typedBase = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="Byte"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" Name="Base" /></Arguments><Static>
            <Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="Byte" Value="1" BaseValue="APP_MD-1_A-1" /></Parameters>
            <ParameterRefs><ParameterRef Id="APP_MD-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs></Static>
            <Dynamic><ParameterRefRef RefId="APP_MD-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Module Id="APP_MD-1_M-1" RefId="APP_MD-1"><NumericArg RefId="APP_MD-1_A-1" Value="255" /></Module></Dynamic>
            </ApplicationProgram></KNX>
            """);
        var typedBefore = typedBase.ToString();
        Check("Unsigned8 base 255 plus one rejects the whole expansion", Ksxml.Parse(typedBase, out var overflowingBase).Instances.Count == 0
            && overflowingBase.StructureFailed && typedBase.ToString() == typedBefore);
        typedBase.Descendants("NumericArg").Single().SetAttributeValue("Value", "254");
        Check("Unsigned8 base 254 plus one preserves the boundary value", Ksxml.Parse(typedBase, out var boundaryBase).Instances.Single().Value == "255"
            && !boundaryBase.StructureFailed);
        typedBase.Descendants("TypeNumber").Single().SetAttributeValue("maxInclusive", "254");
        Check("Parameter base addition respects an explicit numeric restriction", Ksxml.Parse(typedBase, out var restrictedBase).Instances.Count == 0
            && restrictedBase.StructureFailed);
        return failures;
    }
}

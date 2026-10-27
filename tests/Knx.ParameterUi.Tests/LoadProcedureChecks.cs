using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class LoadProcedureChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private const string AppId = "M-FFF0_A-1", MaskId = "MV-0705";
    private static XElement Control(string name, params object[] content) => new(Ns + "LdCtrl" + name, content);
    private static XDocument Application(string style = "ProductProcedure", params XElement[] groups)
    {
        if (groups.Length == 0) groups = [new(Ns + "LoadProcedure", Control("Connect"), Control("Unload", new XAttribute("LsmIdx", 1)), Control("Restart"), Control("Disconnect"))];
        var stat = new XElement(Ns + "Static", new XElement(Ns + "Code",
            Segment("AS", 4096), Segment("Assoc", 8192), Segment("Objects", 16384)),
            new XElement(Ns + "AddressTable", new XAttribute("CodeSegment", "AS"), new XAttribute("Offset", 0), new XAttribute("MaxEntries", 5)),
            new XElement(Ns + "AssociationTable", new XAttribute("CodeSegment", "Assoc"), new XAttribute("Offset", 0), new XAttribute("MaxEntries", 5)),
            new XElement(Ns + "ComObjectTable", new XAttribute("CodeSegment", "Objects"), new XAttribute("Offset", 0)),
            new XElement(Ns + "LoadProcedures", groups));
        return new(new XElement(Ns + "KNX", new XElement(Ns + "ManufacturerData", new XElement(Ns + "Manufacturer", new XAttribute("RefId", "M-FFF0"),
            new XElement(Ns + "ApplicationPrograms", new XElement(Ns + "ApplicationProgram", new XAttribute("Id", AppId),
                new XAttribute("MaskVersion", MaskId), new XAttribute("LoadProcedureStyle", style), stat))))));
    }
    private static XElement Segment(string id, uint address) => new(Ns + "AbsoluteSegment", new XAttribute("Id", id), new XAttribute("Address", address),
        new XAttribute("Size", 16), new XAttribute("MemoryType", "EEPROM"), new XElement(Ns + "Data", Convert.ToBase64String(new byte[16])));
    private static XElement Static(XDocument document) => document.Descendants(Ns + "Static").Single();
    private static XDocument Master(params XElement[] controls) => new(new XElement(Ns + "KNX", new XElement(Ns + "MasterData",
        new XElement(Ns + "MaskVersions", new XElement(Ns + "MaskVersion", new XAttribute("Id", MaskId), new XAttribute("MaskVersion", 1797),
            new XElement(Ns + "HawkConfigurationData", new XAttribute("LegacyVersion", 0), new XElement(Ns + "Procedures",
                new XElement(Ns + "Procedure", new XAttribute("ProcedureType", "Load"), new XAttribute("ProcedureSubType", "all"),
                    new XAttribute("Access", "remote local2"), controls.Length == 0 ? [Control("Connect"), Control("Merge", new XAttribute("MergeId", 2)), Control("Disconnect")] : controls))))))));
    private static LoadProcedurePlan Compile(XDocument application, XDocument? master = null, LoadParts parts = LoadParts.Full, string subtype = "all")
        => LoadProcedureCompiler.Compile(application, master, new(subtype, LoadProcedureAccess.Remote, parts));
    private static XElement Group(byte id, params XElement[] controls) => new(Ns + "LoadProcedure", new XAttribute("MergeId", id), controls);

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool value) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + "LoadProcedure " + name); if (!value) failures++; }
        void Refused(string name, XDocument application, XDocument? master = null, LoadProcedureIssueCode? expected = null)
        {
            var plan = Compile(application, master);
            Check(name, !plan.IsAvailable && plan.WitnessHash is null && plan.Controls.Count == 0 && plan.MergeDecisions.Count == 0
                && plan.ResourceReferences.Count == 0 && plan.TableRequirements.Count == 0 && plan.Issues.Count == 1
                && (expected is null || plan.Issues[0].Code == expected) && !plan.ExecutionEligible && !plan.DownloadEligible && !plan.CompleteApplicationImage);
        }
        var app = Application(); var master = Master(); var before = app.ToString(SaveOptions.DisableFormatting); var masterBefore = master.ToString(SaveOptions.DisableFormatting);
        var product = Compile(app, master);
        Check("product root preserves declared order and exact profile", product.IsAvailable && product.ProfileVersion == 1
            && product.Scope == "PrimaryProgramProcedure" && product.Style == LoadProcedureStyle.ProductProcedure
            && product.Controls.Select(c => c.Kind).SequenceEqual(new[] { LoadControlKind.Connect, LoadControlKind.Unload, LoadControlKind.Restart, LoadControlKind.Disconnect }));
        Check("the result cannot authorize execution or a complete image", !product.ExecutionEligible && !product.CompleteApplicationImage && !product.DownloadEligible
            && product.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.FinalizationNotCompiled)
            && product.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.GeneratedTablesNotBuilt)
            && product.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.ParameterActivationUnresolved));
        Check("all three table responsibilities retain exact nominal source placement", product.TableRequirements.Count == 3
            && product.TableRequirements.Select(t => t.Role).Distinct().Count() == 3 && product.TableRequirements[0] is { CodeSegmentId: "AS", Offset: 0, ByteOffsetInArena: 4096, MaxEntries: 5 });
        Check("supplied code resources are hashed without size expansion", product.ResourceReferences.Count == 3
            && product.ResourceReferences.All(r => r.DataLength == 16 && r.MaskLength is null && r.DataHash?.Length == 64));
        Check("primary compilation retains separate definition, profile and result witnesses", product.ApplicationId == AppId && product.MaskVersionId == MaskId
            && product.ApplicationDefinitionHash?.Length == 64 && product.SelectedProfileHash?.Length == 64 && product.WitnessHash?.Length == 64);
        var noMaster = Compile(app);
        Check("a product declaration can be inspected without inventing master data", noMaster.IsAvailable && noMaster.SelectedProfileHash is null
            && noMaster.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.MissingArchivedMaster));
        var supplied = new XElement(Ns + "LoadProcedure", Control("Connect"), Control("WriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27),
            new XAttribute("Verify", false), new XAttribute("InlineData", "0102030405060708090A")), Control("Disconnect"));
        var write = Compile(Application("ProductProcedure", supplied), master);
        Check("ten inline action bytes do not become inferred property data", write.IsAvailable && write.Controls[1].InlineData?.ToHexString() == "0102030405060708090A"
            && write.Controls[1].Arguments.Single(a => a.Name == "Count").UnsignedValue == 1 && write.Controls[1].Arguments.Single(a => a.Name == "StartElement").UnsignedValue == 1);
        var inlineCopy = write.Controls[1].InlineData!.ToArray(); inlineCopy[0] = 255;
        Check("inline bytes are defensively owned", write.Controls[1].InlineData![0] == 1);
        var listProtected = false; try { ((IList<LoadProcedureControl>)product.Controls).Clear(); } catch (NotSupportedException) { listProtected = true; }
        Check("control publication is read only", listProtected && product.Controls.Count == 4);

        var conditionalNames = new[] { "auto", "all", "full", "par", "grp", "full,par", "full,grp", "par,grp" };
        for (var bits = 0; bits < 8; bits++)
        {
            var flags = (LoadParts)bits; var full = (flags & LoadParts.Full) != 0; var par = (flags & LoadParts.Parameters) != 0; var grp = (flags & LoadParts.Groups) != 0;
            var expected = new[] { true, true, full, !full && par, !full && grp, full || par, full || grp, !full && par && grp };
            for (var i = 0; i < conditionalNames.Length; i++)
            {
                var condition = Application("ProductProcedure", new XElement(Ns + "LoadProcedure", Control("Restart", new XAttribute("AppliesTo", conditionalNames[i]))));
                var result = Compile(condition, parts: flags);
                Check("AppliesTo=" + conditionalNames[i] + " parts=" + bits, result.IsAvailable && result.Controls.Count == (expected[i] ? 1 : 0));
            }
        }
        var merged = Application("MergedProcedure", Group(2, Control("WriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Verify", false), new XAttribute("InlineData", "00010203040506070809"))),
            Group(4, Control("WriteRelMem", new XAttribute("ObjIdx", 4), new XAttribute("Offset", 2), new XAttribute("Size", 12), new XAttribute("Verify", true))),
            Group(7, Control("LoadImageProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Count", 6))));
        var mergeMaster = Master(Control("Connect"), Control("Merge", new XAttribute("MergeId", 1)), Control("Merge", new XAttribute("MergeId", 2)),
            Control("Merge", new XAttribute("MergeId", 4)), Control("Merge", new XAttribute("MergeId", 7)), Control("Disconnect"));
        var mergedBefore = merged.ToString(SaveOptions.DisableFormatting);
        var compiled = Compile(merged, mergeMaster);
        Check("merge groups splice into template order and absent groups skip", compiled.IsAvailable && compiled.Controls.Select(c => c.Kind).SequenceEqual(new[]
            { LoadControlKind.Connect, LoadControlKind.WriteProperty, LoadControlKind.WriteRelativeMemory, LoadControlKind.LoadImageProperty, LoadControlKind.Disconnect })
            && compiled.MergeDecisions[0] is { MergeId: 1, Decision: LoadMergeDecisionKind.MissingGroupSkipped, EmittedControls: 0 });
        Check("inserted auto declarations become effective all without altering their source", compiled.Controls[1] is
            { Origin: LoadControlOrigin.ProductMerge, MergeId: 2, DeclaredAppliesTo: LoadControlCondition.Auto, EffectiveAppliesTo: LoadControlCondition.All, TemplateOrdinal: 2 });
        Check("source XML does not acquire effective auto normalization", !merged.Descendants().Attributes("AppliesTo").Any() && mergedBefore == merged.ToString(SaveOptions.DisableFormatting));
        var memberCondition = new XDocument(merged); memberCondition.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("AppliesTo", "par");
        var filtered = Compile(memberCondition, mergeMaster, LoadParts.Full);
        Check("inserted explicit conditions are evaluated and reported", filtered.IsAvailable && filtered.MergeDecisions.Single(d => d.MergeId == 2).Decision == LoadMergeDecisionKind.AllControlsFiltered);
        var defaultPlan = Compile(Application("DefaultProcedure"), Master(Control("Connect"), Control("Disconnect")));
        Check("default style uses the selected mask rather than the product root", defaultPlan.IsAvailable && defaultPlan.Controls.Count == 2 && defaultPlan.Controls.All(c => c.Origin == LoadControlOrigin.Mask));
        var groupMaster = Master(Control("Restart")); groupMaster.Descendants(Ns + "Procedure").Single().SetAttributeValue("ProcedureSubType", "grp");
        var groupPlan = Compile(Application("DefaultProcedure"), groupMaster, LoadParts.Groups, "grp");
        Check("an explicit native subtype selects exactly that template", groupPlan.IsAvailable && groupPlan.Controls.Single().Kind == LoadControlKind.Restart);
        Refused("missing exact subtype does not choose a different template", Application("DefaultProcedure"), groupMaster, LoadProcedureIssueCode.MissingProcedure);
        var isolated = new XDocument(master); isolated.Descendants(Ns + "MaskVersions").Single().Add(new XElement(Ns + "MaskVersion", new XAttribute("Id", "Other"), new XAttribute("MaskVersion", 1)));
        Check("unrelated mask metadata does not alter the selected profile", Compile(app, isolated).WitnessHash == product.WitnessHash);
        var changedProc = new XDocument(master); var otherProc = new XElement(changedProc.Descendants(Ns + "Procedure").Single()); otherProc.SetAttributeValue("ProcedureSubType", "grp");
        changedProc.Descendants(Ns + "Procedures").Single().Add(otherProc);
        Check("unselected same-profile procedure declarations are bound", Compile(app, changedProc).WitnessHash != product.WitnessHash);
        var changedResource = new XDocument(app); Static(changedResource).Element(Ns + "Code")!.Add(Segment("Unused", 32768));
        Check("unselected source-resource declarations are bound", Compile(changedResource, master).WitnessHash != product.WitnessHash);
        var shortPayload = new XDocument(app); Static(shortPayload).Element(Ns + "Code")!.Elements().First().SetAttributeValue("Size", 128);
        var shortPlan = Compile(shortPayload, master);
        Check("shorter initial data remains short with an unresolved requirement", shortPlan.IsAvailable && shortPlan.ResourceReferences[0] is { DeclaredSize: 128, DataLength: 16 }
            && shortPlan.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.IncompleteResourcePayload && r.ReferenceId == "AS"));
        var noData = new XDocument(app); Static(noData).Element(Ns + "Code")!.Elements().First().Element(Ns + "Data")!.Remove();
        var noDataPlan = Compile(noData, master);
        Check("missing initial data never becomes zero-filled bytes", noDataPlan.IsAvailable && noDataPlan.ResourceReferences[0].DataLength is null && noDataPlan.ResourceReferences[0].DataHash is null);
        var relative = new XDocument(app); var code = Static(relative).Element(Ns + "Code")!;
        foreach (var (node, lsm) in code.Elements().Select((e, i) => (e, i + 1)).ToArray())
        { node.Name = Ns + "RelativeSegment"; node.Attribute("Address")!.Remove(); node.Attribute("MemoryType")!.Remove(); node.SetAttributeValue("Offset", 4); node.SetAttributeValue("LoadStateMachine", lsm); }
        var relativePlan = Compile(relative, master);
        Check("equal offsets in different LSM arenas remain separate", relativePlan.IsAvailable && relativePlan.ResourceReferences.All(r => r.Arena.Kind == ResourcePatchArenaKind.RelativeMemory)
            && relativePlan.TableRequirements.Select(t => t.Arena!.LoadStateMachine).SequenceEqual(new byte?[] { 1, 2, 3 }));
        foreach (var (label, change, expected) in new (string, Action<XDocument>, LoadProcedureIssueCode?)[]
        {
            ("duplicate product root", d => Static(d).Element(Ns + "LoadProcedures")!.Add(new XElement(Ns + "LoadProcedure")), LoadProcedureIssueCode.AmbiguousProcedure),
            ("explicit MergeId zero", d => Static(d).Element(Ns + "LoadProcedures")!.Elements().Single().SetAttributeValue("MergeId", 0), LoadProcedureIssueCode.AmbiguousProcedure),
            ("unknown control", d => Static(d).Element(Ns + "LoadProcedures")!.Elements().Single().Add(Control("Unknown")), LoadProcedureIssueCode.UnsupportedControl),
            ("unknown semantic attribute", d => d.Descendants(Ns + "LdCtrlUnload").Single().SetAttributeValue("Unproven", 1), LoadProcedureIssueCode.InvalidControl),
            ("two object selectors", d => d.Descendants(Ns + "LdCtrlUnload").Single().SetAttributeValue("ObjType", 1), LoadProcedureIssueCode.InvalidControl),
            ("missing object selector", d => d.Descendants(Ns + "LdCtrlUnload").Single().Attribute("LsmIdx")!.Remove(), LoadProcedureIssueCode.InvalidControl),
            ("invalid AppliesTo spelling", d => d.Descendants(Ns + "LdCtrlRestart").Single().SetAttributeValue("AppliesTo", "fullpar"), LoadProcedureIssueCode.InvalidControl),
            ("numeric style is not a native style", d => d.Descendants(Ns + "ApplicationProgram").Single().SetAttributeValue("LoadProcedureStyle", "0"), LoadProcedureIssueCode.InvalidApplication),
            ("cross-kind duplicate resource identity", d => Static(d).Element(Ns + "Code")!.Add(Segment("AS", 32768)), LoadProcedureIssueCode.InvalidResource),
            ("unselected colliding resource owner", d => Static(d).Element(Ns + "Code")!.Add(Segment("Unused", 4100)), LoadProcedureIssueCode.AmbiguousResourceOwnership),
            ("dangling table resource", d => Static(d).Element(Ns + "AddressTable")!.SetAttributeValue("CodeSegment", "Missing"), LoadProcedureIssueCode.InvalidResource),
            ("unknown code resource shape", d => Static(d).Element(Ns + "Code")!.Elements().First().SetAttributeValue("ExternalArena", 1), LoadProcedureIssueCode.InvalidControl),
        }) { var d = Application(); change(d); Refused(label, d, master, expected); }
        var duplicateMaster = new XDocument(master); duplicateMaster.Descendants(Ns + "Procedures").Single().Add(new XElement(duplicateMaster.Descendants(Ns + "Procedure").Single()));
        Refused("colliding template selectors are rejected even for product style", app, duplicateMaster, LoadProcedureIssueCode.AmbiguousProcedure);
        var unselectedDuplicate = new XDocument(master); var unused = new XElement(unselectedDuplicate.Descendants(Ns + "Procedure").Single()); unused.SetAttributeValue("ProcedureSubType", "grp");
        unselectedDuplicate.Descendants(Ns + "Procedures").Single().Add(unused, new XElement(unused));
        Refused("unselected duplicate procedure selectors also fail closed", app, unselectedDuplicate, LoadProcedureIssueCode.AmbiguousProcedure);
        var duplicateCurrent = new XDocument(master); duplicateCurrent.Descendants(Ns + "MaskVersion").Single().Add(new XElement(duplicateCurrent.Descendants(Ns + "HawkConfigurationData").Single()));
        Refused("current-zero profile is never selected by first match", app, duplicateCurrent, LoadProcedureIssueCode.InvalidMaster);
        var foreignTemplate = new XDocument(master);
        foreach (var element in foreignTemplate.Descendants(Ns + "Procedure").Single().DescendantsAndSelf()) element.Name = XNamespace.Get("urn:foreign:procedure") + element.Name.LocalName;
        Refused("a foreign template cannot borrow the native configuration namespace", Application("DefaultProcedure"), foreignTemplate, LoadProcedureIssueCode.InvalidMaster);
        var processing = new XDocument(app); processing.AddFirst(new XProcessingInstruction("unsupported", "data"));
        Refused("top-level processing instructions are rejected before definition hashing", processing, master, LoadProcedureIssueCode.InvalidApplication);
        var topComment = new XDocument(master); topComment.AddFirst(new XComment(new string('x', 16 * 1_048_576 + 1)));
        Refused("top-level master text is included in the source budget", app, topComment, LoadProcedureIssueCode.LimitExceeded);
        var moduleProcedure = new XDocument(app); moduleProcedure.Descendants(Ns + "ApplicationProgram").Single().Add(new XElement(Ns + "ModuleDefs",
            new XElement(Ns + "ModuleDef", new XAttribute("Id", "Module"), new XElement(Ns + "Static", new XElement(Ns + "LoadProcedures", new XElement(Ns + "LoadProcedure", new XAttribute("MergeId", 2), Control("Restart")))))));
        Check("module procedures remain a visible uncompiled requirement", Compile(moduleProcedure, master).Requirements.Any(r => r.Code == LoadProcedureRequirementCode.ModuleProceduresNotCompiled));
        var recursive = Application("MergedProcedure", Group(2, Control("Merge", new XAttribute("MergeId", 2))));
        Refused("recursive product merge cannot become an executable control", recursive, master, LoadProcedureIssueCode.UnsupportedControl);
        var badHex = Application("ProductProcedure", new XElement(Ns + "LoadProcedure", Control("WriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Verify", false), new XAttribute("InlineData", "0"))));
        Refused("odd inline hex fails atomically", badHex, master, LoadProcedureIssueCode.InvalidControl);
        foreach (var raw in new[] { "0 1", "AA BB", "AB\tCD", "AB\r\nCD" })
        {
            var spacedHex = new XDocument(badHex); spacedHex.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("InlineData", raw);
            Refused("internal whitespace is not removed from inline hex " + raw.Replace("\r", "R").Replace("\n", "N").Replace("\t", "T"), spacedHex, master, LoadProcedureIssueCode.InvalidControl);
        }
        var paddedHex = new XDocument(badHex); paddedHex.Descendants(Ns + "LdCtrlWriteProp").Single().SetAttributeValue("InlineData", " \tABCD\r\n");
        var paddedPlan = Compile(paddedHex, master);
        Check("surrounding XML whitespace does not change valid inline bytes", paddedPlan.IsAvailable && paddedPlan.Controls.Single().InlineData?.ToHexString() == "ABCD");
        foreach (var ns in new[] { "http://knx.org/xml/project/22", "urn:unknown", "" })
        {
            var d = new XDocument(app); foreach (var e in d.Descendants()) e.Name = XNamespace.Get(ns) + e.Name.LocalName;
            Refused("unsupported application namespace " + ns, d, master, LoadProcedureIssueCode.UnsupportedApplicationNamespace);
        }
        var tooMany = Application("ProductProcedure", new XElement(Ns + "LoadProcedure", Enumerable.Range(0, 4097).Select(_ => Control("Restart"))));
        Refused("source-control budget has no partial publication", tooMany, master, LoadProcedureIssueCode.LimitExceeded);
        var bigInline = Application("ProductProcedure", new XElement(Ns + "LoadProcedure", Control("WriteProp", new XAttribute("ObjIdx", 4), new XAttribute("PropId", 27), new XAttribute("Verify", false), new XAttribute("InlineData", new string('0', 2 * (1_048_576 + 1))))));
        Refused("inline byte budget precedes byte publication", bigInline, master, LoadProcedureIssueCode.LimitExceeded);
        var badRequest = LoadProcedureCompiler.Compile(app, master, new("all", (LoadProcedureAccess)1, LoadParts.Full));
        Check("unrecognized access does not silently become remote", !badRequest.IsAvailable && badRequest.Issues[0].Code == LoadProcedureIssueCode.InvalidRequest);
        badRequest = LoadProcedureCompiler.Compile(app, master, new("all", LoadProcedureAccess.Remote, (LoadParts)8));
        Check("unknown part bits are refused", !badRequest.IsAvailable && badRequest.Issues[0].Code == LoadProcedureIssueCode.InvalidRequest);
        var typed = Compile(Application("ProductProcedure", new XElement(Ns + "LoadProcedure", Control("CompareProp", new XAttribute("ObjType", 123), new XAttribute("Occurrence", 2),
            new XAttribute("PropId", 7), new XAttribute("InlineData", "ABCD"), new XAttribute("Mask", "FF00"), new XAttribute("Range", "[0,255]u"),
            new XElement(Ns + "OnError", new XAttribute("Cause", "CompareMismatch"), new XAttribute("Ignore", true), new XAttribute("MessageRef", "MSG-1"))))), master);
        Check("typed comparison arguments and ordered error policy are retained", typed.IsAvailable && typed.Controls.Single().InlineMask?.ToHexString() == "FF00"
            && typed.Controls.Single().Arguments.Single(a => a.Name == "Occurrence").UnsignedValue == 2
            && typed.Controls.Single().OnError.Single() is { Cause: LoadControlErrorCause.CompareMismatch, Ignore: true, MessageRef: "MSG-1" });
        var path = Path.Combine(directory, "load-procedure-source-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            app.Save(path); var sourceHash = SHA256.HashData(File.ReadAllBytes(path));
            _ = Compile(XDocument.Load(path), master);
            Check("compilation leaves source bytes and both documents unchanged", SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(sourceHash)
                && app.ToString(SaveOptions.DisableFormatting) == before && master.ToString(SaveOptions.DisableFormatting) == masterBefore);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
        Console.WriteLine($"Load procedures: {checks - failures}/{checks} passed");
        return failures;
    }
}

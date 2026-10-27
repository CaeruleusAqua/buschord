using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class TextReferenceChecks
{
    public static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var xml = XDocument.Parse("""
            <KNX><ApplicationProgram Id="M-0001_A-text"><Static><ParameterTypes>
            <ParameterType Id="S"><TypeText SizeInBit="512" /></ParameterType><ParameterType Id="N"><TypeNumber SizeInBit="8" Type="unsignedInt" /></ParameterType>
            </ParameterTypes><Parameters><Parameter Id="P-label" Text="Label" ParameterType="S" Value="" />
            <Parameter Id="P-value" Text="Value {{0:Unnamed}}" TextParameterRefId="R-label" ParameterType="N" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="R-label" RefId="P-label" /><ParameterRef Id="R-value" RefId="P-value" /></ParameterRefs>
            <ComObjectTable><ComObject Id="M-0001_A-text_O-1" Number="1" Text="Output {{0:Unnamed}}" TextParameterRefId="R-label" ObjectSize="1 Bit" /></ComObjectTable>
            <ComObjectRefs><ComObjectRef Id="M-0001_A-text_O-1_R-1" RefId="M-0001_A-text_O-1" /></ComObjectRefs>
            </Static><Dynamic><ParameterBlock Id="B" Text="Channel {{0:Unnamed}}" TextParameterRefId="R-label">
            <ParameterRefRef RefId="R-label" /><ParameterRefRef RefId="R-value" /></ParameterBlock></Dynamic></ApplicationProgram></KNX>
            """);
        var saved = new Dictionary<string, string?> { ["R-label"] = "Kitchen $1 {{0}}" };
        var parsed = Ksxml.Parse(xml, out _, saved);
        Check("Saved text references resolve block and parameter labels literally", parsed.Blocks.Single().Name == "Channel Kitchen $1 {{0}}"
            && parsed.Instances.Single(p => p.Id == "R-value").Name == "Value Kitchen $1 {{0}}");
        DynamicParameters.Apply(DynamicParameters.Evaluate(xml.Root!, new Dictionary<string, string?> { ["R-label"] = "Office" }), parsed.Instances, parsed.Blocks);
        Check("Text references refresh from their original template", parsed.Blocks.Single().Name == "Channel Office"
            && parsed.Instances.Single(p => p.Id == "R-value").Name == "Value Office");
        var defaults = Ksxml.Parse(xml);
        Check("Empty default text uses the declared placeholder fallback", defaults.Blocks.Single().Name == "Channel Unnamed");
        var nonemptyDefault = new XDocument(xml);
        nonemptyDefault.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-label").SetAttributeValue("Value", "Factory");
        Check("Explicitly cleared nonempty defaults do not resurrect fallback text", Ksxml.Parse(nonemptyDefault, out _,
            new Dictionary<string, string?> { ["R-label"] = "" }).Blocks.Single().Name == "Channel ");
        var module = XDocument.Parse("""
            <KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id="S"><TypeText SizeInBit="512" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="S" Value="Kitchen" /></Parameters><ParameterRefs><ParameterRef Id="L" RefId="P" /></ParameterRefs>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Arguments><Argument Id="APP_MD-1_A-1" Name="ChNo" Type="Text" /></Arguments><Static>
            <Parameters><Parameter Id="APP_MD-1_P-1" ParameterType="S" Value="" Text="Value {{ChNo}} {{0:Fallback}}" TextParameterRefId="L" /></Parameters>
            <ParameterRefs><ParameterRef Id="APP_MD-1_P-1_R-1" RefId="APP_MD-1_P-1" /></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="APP_MD-1_P-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Module Id="APP_MD-1_M-1" RefId="APP_MD-1"><TextArg RefId="APP_MD-1_A-1" Value="{{0}}" /></Module></Dynamic></ApplicationProgram></KNX>
            """);
        Check("Named module text and parameter text are substituted in one literal pass", Ksxml.Parse(module).Instances
            .Single(p => p.Id.Contains("_MI-1_")).Name == "Value {{0}} Kitchen");
        var path = Path.Combine(directory, "textrefs.knxproj");
        var project = Knx.Workbench.WorkbenchJson.CreateDemo();
        var device = ProjectQuery.DeviceList(project).First();
        device.Hardware2ProgramRefId = null; device.ProductRefId = "M-0001_A-text";
        device.ComObjectRefs.Clear(); device.ComObjectRefs.Add(new() { Id = "O-1_R-1", ComObjectId = "O-1_R-1" });
        device.ParameterValues["R-label"] = "Kitchen";
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        using (var output = zip.CreateEntry("M-0001/M-0001_A-text.xml").Open()) xml.Save(output);
        var imported = Knxproj.Load(path);
        var native = ProjectQuery.DeviceList(imported).Single(d => d.Id == device.Id);
        Check("Native object labels resolve saved text references", native.ComObjectRefs.Single().Text == "Output Kitchen");
        var serialized = Knxproj.ToXml(imported).Descendants().Single(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == device.Id);
        Check("Inherited display text is never persisted as an explicit override", serialized.Descendants().Single(e => e.Name.LocalName == "ComObjectInstanceRef").Attribute("Text") is null);
        var undo = new UndoStack(); undo.Reset(imported);
        native.ParameterValues["R-label"] = "Office"; ApplicationTexts.RefreshObjects(imported, native); undo.Push(imported);
        Check("Parameter edits re-render object text without freezing the template", native.ComObjectRefs.Single().Text == "Output Office");
        Check("Undo restores both text values and runtime bindings", ProjectQuery.DeviceList(undo.Undo()!).Single(d => d.Id == native.Id).ComObjectRefs.Single().Text == "Output Kitchen");
        Check("Redo restores both text values and runtime bindings", ProjectQuery.DeviceList(undo.Redo()!).Single(d => d.Id == native.Id).ComObjectRefs.Single().Text == "Output Office");
        var line = ProjectQuery.LocateDevice(imported, native.Address)!.Value.Line!;
        var copy = ProjectOperations.CopyDevice(imported, native, line).Value!;
        copy.ParameterValues["R-label"] = "Bedroom"; ApplicationTexts.RefreshObjects(imported, copy);
        Check("Copied devices keep independent text bindings", copy.ComObjectRefs.Single().Text == "Output Bedroom"
            && native.ComObjectRefs.Single().Text == "Output Office");
        native.ComObjectRefs.Single().Text = "Manual override"; ApplicationTexts.RefreshObjects(imported, native);
        Check("Explicit object text overrides remain authoritative", native.ComObjectRefs.Single().Text == "Manual override");
        Check("Text expansion preserves whitespace and literal replacement characters", ApplicationTexts.Render("[{{0}}]", " $1 ") == "[ $1 ]");
        var bounded = false;
        try { ApplicationTexts.Render(string.Concat(Enumerable.Repeat("{{0}}", 2000)), new string('x', 4096)); }
        catch (InvalidDataException) { bounded = true; }
        Check("Text expansion is bounded before unbounded output allocation", bounded);
        return failures;
    }
}

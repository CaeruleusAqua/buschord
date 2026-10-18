using System.IO.Compression;
using System.Xml.Linq;
using Knx;
using Knx.Catalog;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
var xml = """
<ApplicationProgram Id="M-0083_A-1" Name="test"><Static>
<ParameterTypes>
 <ParameterType Id="n" Name="number"><TypeNumber Type="signedInt" SizeInBit="8" minInclusive="-10" maxInclusive="12"/></ParameterType>
 <ParameterType Id="u" Name="unsigned"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType>
 <ParameterType Id="e" Name="enum"><TypeRestriction Base="Value" SizeInBit="8"><Enumeration Id="E1" Value="0" Text="Off"/><Enumeration Id="E2" Value="9" Text="On"/></TypeRestriction></ParameterType>
 <ParameterType Id="f" Name="float"><TypeFloat Encoding="DPT 9" minInclusive="-1.5" maxInclusive="12.5"/></ParameterType>
 <ParameterType Id="t" Name="text"><TypeText SizeInBit="32"/></ParameterType>
 <ParameterType Id="p" Name="pattern"><TypeText SizeInBit="32" Pattern="[a-z]*"/></ParameterType>
 <ParameterType Id="legacy" Name="legacy" DataType="Number" SizeInBit="16"/>
 <ParameterType Id="large" Name="large"><TypeNumber Type="unsignedInt" SizeInBit="64" minInclusive="9007199254740992" maxInclusive="9007199254740992"/></ParameterType>
 <ParameterType Id="unknown" Name="unknown"><TypeFuture/></ParameterType>
</ParameterTypes>
<Parameters><Parameter Id="P1" Text="Number" ParameterType="n" Value="4"/>
<Parameter Id="P2" Name="Enum" ParameterType="e" Value="9"/>
<Parameter Id="P3" Name="Unset" ParameterType="t"/>
<ParameterInstance Id="P4" Name="Synthetic" ParameterTypeId="legacy" DefaultValue="3"/>
<Union><Parameter Id="P5" Name="Union" ParameterType="n" Value="2"/></Union></Parameters>
<ParameterRefs><ParameterRef Id="R1" RefId="P1" Text="Override" Value="7"/>
<ParameterRef Id="R2" RefId="P1" Access="Read"/></ParameterRefs>
<ComObjectTable><ComObject Id="O1" Number="1" ObjectSize="1 Bit"/><ComObject Id="O2" Number="2" ObjectSize="2 Bytes"/></ComObjectTable>
</Static><Dynamic><ParameterBlock Id="B1" Text="Block"><Choose ParamRefId="R1"><When test="7"><ParameterRefRef RefId="R2"/><Assign/></When></Choose></ParameterBlock></Dynamic></ApplicationProgram>
""";
var parsed = Ksxml.Parse(XDocument.Parse(xml));
var types = parsed.Types.ToDictionary(t => t.Id);
Check(types["n"].DataType == ParameterDataType.Number && types["n"].SizeInBit == 8, "TypeNumber");
Check(types["f"].DataType == ParameterDataType.Float && types["f"].SizeInBit == 16, "TypeFloat");
Check(types["t"].DataType == ParameterDataType.Text && types["t"].SizeInBit == 32, "TypeText");
Check(types["e"].Restrictions[1].EnumerationValue == "9" && types["e"].Restrictions[1].EnumerationText == "On", "value is not label");
Check(types["legacy"].DataType == ParameterDataType.Number, "legacy compatible");
Check(types["unknown"].UnsupportedReason is not null, "unknown explicit");
var r1 = parsed.Instances.Single(i => i.Id == "R1");
Check(r1.ParameterId == "P1" && r1.Name == "Override" && r1.Value == "7" && r1.DefaultValue == "7" && r1.ParameterType == types["n"], "reference identity/value/type");
Check(!parsed.Instances.Single(i => i.Id == "R2").IsEnabled, "read only");
Check(parsed.Instances.Single(i => i.Id == "P3").Value is null && parsed.Instances.Single(i => i.Id == "P3").DefaultValue is null, "no manufactured default");
Check(parsed.Instances.Single(i => i.Id == "P4").DefaultValue == "3", "legacy default");
Check(parsed.ComObjects.Select(o => o.SizeInBit).SequenceEqual([1, 16]), "object sizes");
Check(Ksxml.UnsupportedFeatures(XElement.Parse(xml)).SequenceEqual(["Assign", "Choose", "When"]), "dynamics explicit");
// Real manufacturer KSXML uses lower case element names; the warning must not depend on casing.
Check(Ksxml.UnsupportedFeatures(XElement.Parse(
        "<A><Dynamic><choose><when test=\"0\"><assign/></when></choose></Dynamic></A>"))
    .SequenceEqual(["assign", "choose", "when"]), "lowercase dynamics explicit");
// ---- dynamic parameters: choose/when/assign, visibility, blocks, limits ----
const string dynXml = """
<ApplicationProgram Id="M-0083_A-DYN" Name="dynamic"><Static>
<ParameterTypes>
 <ParameterType Id="n" Name="number"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="255"/></ParameterType>
 <ParameterType Id="e" Name="enum"><TypeRestriction Base="Value" SizeInBit="8"><Enumeration Id="E0" Value="0" Text="Off"/><Enumeration Id="E1" Value="5" Text="On"/></TypeRestriction></ParameterType>
</ParameterTypes>
<Parameters>
 <Parameter Id="P1" Text="Mode" ParameterType="e" Value="0"/>
 <Parameter Id="P2" Text="Level" ParameterType="n" Value="7"/>
 <Parameter Id="P3" Text="Copy" ParameterType="n"/>
 <Parameter Id="P4" Text="Always" ParameterType="n" Value="1"/>
 <Parameter Id="P5" Text="Read only" ParameterType="n" Value="2"/>
</Parameters>
<ParameterRefs>
 <ParameterRef Id="R1" RefId="P1"/>
 <ParameterRef Id="R2" RefId="P2"/>
 <ParameterRef Id="R3" RefId="P3"/>
 <ParameterRef Id="R4" RefId="P4"/>
 <ParameterRef Id="R5" RefId="P5" Access="Read"/>
</ParameterRefs>
</Static><Dynamic><Channel Id="CH-1">
 <ParameterBlock Id="B-MODE" Name="Mode">
  <choose ParamRefId="R1">
   <when test="0">
    <ParameterBlock Id="B-ON" Name="On"><ParameterRefRef RefId="R4"/></ParameterBlock>
    <choose ParamRefId="R2">
     <when test="7"><ParameterRefRef RefId="R5"/></when>
     <when default="true"><ParameterRefRef RefId="R2"/></when>
    </choose>
    <Assign TargetParamRefRef="R3" SourceParamRefRef="R2"/>
    <Assign TargetParamRefRef="R4" Value="9"/>
   </when>
   <when test="5"><ParameterRefRef RefId="R5"/></when>
   <when default="true"><ParameterRefRef RefId="R2"/></when>
  </choose>
 </ParameterBlock>
</Channel></Dynamic></ApplicationProgram>
""";
var dynRoot = XElement.Parse(dynXml);
var dyn = DynamicParameters.Evaluate(dynRoot);
Check(dyn.TracksVisibility && dyn.Converged && dyn.Passes == 2, "dynamic converges in two passes");
Check(dyn.EvaluatedChooses == 2 && dyn.SelectedBranches == 2 && dyn.UnmatchedChooses == 0, "nested chooses select");
Check(dyn.Parameters["R4"].IsVisible && dyn.Parameters["R4"].Value == "9" && dyn.Parameters["R4"].DeclaredValue == "1", "assign value override keeps declaration");
Check(dyn.Parameters["R3"].Value == "7" && dyn.Parameters["R3"].DeclaredValue is null && !dyn.Parameters["R3"].IsVisible, "assign copies source into hidden ref");
Check(dyn.Parameters["R5"].IsVisible && !dyn.Parameters["R2"].IsVisible, "nested branch visibility");
Check(dyn.KnownBlocks.SetEquals(["B-MODE", "B-ON"]) && dyn.VisibleBlocks.SetEquals(dyn.KnownBlocks), "blocks instantiated");
var parsedDyn = Ksxml.Parse(XDocument.Parse(dynXml), out var parsedDynEval);
Check(parsedDynEval.Parameters["R5"].IsVisible, "parse exposes initial evaluation");
var dynRows = parsedDyn.Instances.ToDictionary(i => i.Id);
Check(dynRows["R4"].Value == "9" && dynRows["R4"].DefaultValue == "1", "row keeps effective value and declaration");
Check(dynRows["R5"].IsVisible && !dynRows["R5"].IsEnabled, "read-only dynamic row stays disabled");
Check(!dynRows["R2"].IsVisible && !dynRows["R3"].IsVisible, "unselected refs hidden");
Check(parsedDyn.Blocks.Single(b => b.Id == "B-ON").IsVisible, "dynamic block visible");
var editedDyn = DynamicParameters.Evaluate(dynRoot, new Dictionary<string, string?> { ["R1"] = "5" });
DynamicParameters.Apply(editedDyn, parsedDyn.Instances, parsedDyn.Blocks);
Check(editedDyn.Parameters["R5"].IsVisible && !editedDyn.Parameters["R4"].IsVisible
    && editedDyn.Parameters["R3"].Value is null && !editedDyn.VisibleBlocks.Contains("B-ON"),
    "re-evaluation follows an edited value");
Check(dynRows["R4"].Value == "1" && dynRows["R4"].DefaultValue == "1" && !dynRows["R4"].IsVisible, "re-apply restores declared value");
DynamicParameters.Apply(DynamicParameters.Evaluate(dynRoot), parsedDyn.Instances, parsedDyn.Blocks);
Check(dynRows["R4"].IsVisible && dynRows["R4"].Value == "9"
    && parsedDyn.Blocks.Single(b => b.Id == "B-ON").IsVisible, "re-apply restores dynamic visibility");

string ConditionProgram(string test, string value)
{
    var escaped = test.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    return $"""
<ApplicationProgram><Static><Parameters>
<Parameter Id="P" ParameterType="n" Value="{value}"/><Parameter Id="Q" ParameterType="n" Value="0"/>
</Parameters><ParameterRefs><ParameterRef Id="R" RefId="P"/><ParameterRef Id="T" RefId="Q"/></ParameterRefs>
</Static><Dynamic><ParameterBlock Id="B"><choose ParamRefId="R">
<when test="{escaped}"><Assign TargetParamRefRef="T" Value="1"/></when>
<when default="true"><Assign TargetParamRefRef="T" Value="0"/></when>
</choose></ParameterBlock></Dynamic></ApplicationProgram>
""";
}
bool ConditionMatches(string test, string value) =>
    DynamicParameters.Evaluate(XElement.Parse(ConditionProgram(test, value))).Parameters["T"].Value == "1";
Check(ConditionMatches("7", "7") && !ConditionMatches("7", "8"), "dynamic equality");
Check(ConditionMatches("1 2", "2") && !ConditionMatches("1 2", "3"), "dynamic alternative values");
Check(ConditionMatches("!=0", "1") && !ConditionMatches("!=0", "0"), "dynamic inequality");
Check(ConditionMatches("<16", "15") && !ConditionMatches("<16", "16"), "dynamic less than");
Check(ConditionMatches(">=4", "4") && !ConditionMatches(">=4", "3"), "dynamic greater or equal");
Check(ConditionMatches("<=90", "90") && !ConditionMatches("<=90", "91"), "dynamic less or equal");
Check(ConditionMatches(">0", "1") && !ConditionMatches(">0", "0"), "dynamic greater than");
var rangeDyn = DynamicParameters.Evaluate(XElement.Parse(ConditionProgram("1..3", "2")));
Check(rangeDyn.Parameters["T"].Value == "0"
    && rangeDyn.Unsupported.Any(u => u.Contains("Unsupported dynamic condition")), "range flagged, default applies");
var cycleDyn = DynamicParameters.Evaluate(XElement.Parse("""
<ApplicationProgram><Static><Parameters><Parameter Id="P" ParameterType="n" Value="0"/></Parameters>
<ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><choose ParamRefId="R">
<when test="0"><Assign TargetParamRefRef="R" Value="1"/></when>
<when test="1"><Assign TargetParamRefRef="R" Value="0"/></when>
</choose></Dynamic></ApplicationProgram>
"""));
Check(!cycleDyn.Converged && cycleDyn.Passes == DynamicParameters.MaxPasses
    && cycleDyn.Unsupported.Any(u => u.Contains("did not converge")), "assignment cycle bounded and reported");
var nestedDyn = new XElement("ParameterRefRef", new XAttribute("RefId", "R"));
for (var depth = 0; depth < DynamicParameters.MaxDepth + 6; depth++)
    nestedDyn = new XElement("choose", new XAttribute("ParamRefId", "R"),
        new XElement("when", new XAttribute("default", "true"), nestedDyn));
var deepDyn = DynamicParameters.Evaluate(new XElement("ApplicationProgram",
    new XElement("Static",
        new XElement("Parameters", new XElement("Parameter", new XAttribute("Id", "P"),
            new XAttribute("ParameterType", "n"), new XAttribute("Value", "1"))),
        new XElement("ParameterRefs", new XElement("ParameterRef", new XAttribute("Id", "R"),
            new XAttribute("RefId", "P")))),
    new XElement("Dynamic",
        new XElement("ParameterBlock", new XAttribute("Id", "B"), nestedDyn))));
Check(deepDyn.DepthLimitReached && deepDyn.Unsupported.Any(u => u.Contains("nesting exceeds")), "nesting depth limit");
var rejectedDyn = DynamicParameters.Evaluate(XElement.Parse("""
<ApplicationProgram><Static><ParameterTypes/><Parameters><Parameter Id="P" ParameterType="n" Value="1"/></Parameters>
<ParameterRefs><ParameterRef Id="R" RefId="P"/><ParameterRef Id="X" RefId="MISSING"/></ParameterRefs>
<ParameterCalculations><ParameterCalculation Id="PC" Language="JavaScript"><LRTransformation>return 1;</LRTransformation></ParameterCalculation></ParameterCalculations>
</Static><Dynamic><ParameterBlock Id="B"><Repeat Id="RPT" Count="2"><Module RefId="MD"/></Repeat></ParameterBlock></Dynamic></ApplicationProgram>
"""));
Check(rejectedDyn.Unsupported.Any(u => u.Contains("Unsupported dynamic construct: Repeat")), "unknown construct flagged");
Check(rejectedDyn.Unsupported.Any(u => u.Contains("Parameter calculations")), "calculations rejected");
Check(rejectedDyn.Unsupported.Any(u => u.Contains("Unresolved parameter reference: MISSING")), "external ref flagged");

foreach (var (id, value, valid) in new (string, string?, bool)[] {
    ("n", "-10", true), ("n", "13", false), ("n", "1.2", false), ("n", "1,2", false),
    ("u", "255", true), ("u", "256", false), ("u", "-1", false),
    ("f", "1.25", true), ("f", "NaN", false), ("f", "Infinity", false), ("f", "13", false),
    ("e", "9", true), ("e", "On", false), ("t", "abcd", true), ("t", "abcde", false),
    ("t", null, false), ("p", "abc", false), ("unknown", "0", false),
    ("large", "9007199254740992", true), ("large", "9007199254740993", false) })
    Check(ParameterValidation.TryValidate(types[id], value, out _) == valid, $"validation {id}/{value}");

var root = Path.Combine(Path.GetTempPath(), "knx-parameters-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    string Split(string name, string program)
    {
        var path = Path.Combine(root, name + ".knxprod");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Part(string part, string content) { using var w = new StreamWriter(zip.CreateEntry("M-0083/" + part).Open()); w.Write(content); }
        Part("Hardware.xml", $"<KNX><Manufacturer RefId='M-0083'><Hardware><Hardware Id='H'><Products><Product Id='Product' Text='{name}'/></Products><Hardware2Programs><Hardware2Program Id='HP'><ApplicationProgramRef RefId='{program}'/></Hardware2Program></Hardware2Programs></Hardware></Hardware></Manufacturer></KNX>");
        Part("Catalog.xml", "<KNX><Manufacturer RefId='M-0083'><Catalog/></Manufacturer></KNX>");
        Part(program + ".xml", "<KNX><Manufacturer RefId='M-0083'>" + xml.Replace("M-0083_A-1", program) + "<Languages><Language Identifier='de-DE'><TranslationUnit><TranslationElement RefId='E2'><Translation AttributeName='Text' Text='Ein'/></TranslationElement><TranslationElement RefId='P2'><Translation AttributeName='Text' Text='Modus'/></TranslationElement></TranslationUnit></Language></Languages></Manufacturer></KNX>");
        return path;
    }
    var a = Split("a", "M-0083_A-1");
    var b = Split("b", "M-0083_A-2");
    var store = new CatalogStore(Path.Combine(root, "library"));
    var ca = store.Import(a);
    var cb = store.Import(b);
    Check(ca.Path != cb.Path && store.LoadAll().Count == 2, "same manufacturer coexist");
    Check(store.Import(a).Path == ca.Path && store.LoadAll().Count == 2, "reimport idempotent");
    File.Delete(a);
    var program = ca.FindProgramFor(ca.Products[0])!;
    Check(program is not null && program.ParameterInstances.Count > 0, "lazy backing moved to store");
    Check(program!.ParameterTypes.Single(t => t.Id == "e").Restrictions[1].EnumerationText == "Ein", "enum translation");
    Check(program.ParameterInstances.Single(i => i.Id == "P2").Name == "Modus", "parameter translation");
    Check(program.ParameterReferences.Count == 2 && program.UnsupportedFeatures.Contains("Choose"), "program refs/dynamic warning");
    Check(!store.Remove(b) && File.Exists(b), "no outside-store deletion");
    Check(store.Remove(cb.Path) && !store.Remove(cb.Path), "remove existing/missing");
    var wrapper = Path.Combine(root, "wrapper.zip");
    using (var zip = ZipFile.Open(wrapper, ZipArchiveMode.Create)) zip.CreateEntryFromFile(b, "inner.knxprod");
    var nested = store.Import(wrapper);
    Check(nested.FindProgramFor(nested.Products[0])?.ParameterInstances.Count > 0, "nested archive remains usable");
    Check(store.LoadAll().All(c => c.FindProgramFor(c.Products[0]) is not null), "reload lazy/nested");
    var renamed = Path.Combine(root, "renamed.knxprod");
    File.Copy(b, renamed);
    Check(store.Import(b).Path == store.Import(renamed).Path, "identity independent of source filename");
    var nestedPath = wrapper;
    for (var depth = 0; depth < 5; depth++)
    {
        var outer = Path.Combine(root, "nested-" + depth + ".zip");
        using (var zip = ZipFile.Open(outer, ZipArchiveMode.Create)) zip.CreateEntryFromFile(nestedPath, "inner.zip");
        nestedPath = outer;
    }
    var rejected = false;
    try { KnxProductCatalog.Open(nestedPath); }
    catch (InvalidDataException) { rejected = true; }
    Check(rejected, "archive nesting bounded");
}
finally { Directory.Delete(root, true); }

var samples = Environment.GetEnvironmentVariable("KNX_CATALOG_SAMPLES") ?? @"sample-catalogs\mdt";
if (Directory.Exists(samples))
{
    var files = 0; var programs = 0; var instances = 0; var enums = 0; var numbers = 0; var texts = 0;
    var editable = new HashSet<ParameterDataType>();
    foreach (var file in Directory.EnumerateFiles(samples, "*.knxprod"))
    {
        var catalog = KnxProductCatalog.Open(file);
        Check(catalog.Products.Count > 0, "sample products " + file);
        foreach (var product in catalog.Products.Where(p => p.ProgramRefIds.Count > 0))
            Check(catalog.FindProgramFor(product) is not null, "sample resolves " + file);
        foreach (var program in catalog.Programs)
        {
            programs++; instances += program.ParameterInstances.Count;
            enums += program.ParameterTypes.Count(t => t.DataType == ParameterDataType.Enumeration);
            numbers += program.ParameterTypes.Count(t => t.DataType == ParameterDataType.Number);
            texts += program.ParameterTypes.Count(t => t.DataType == ParameterDataType.Text);
            Check(program.ParameterInstances.All(i => i.ParameterType is not null), "all sample types resolved " + program.Id);
            Check(program.ComObjects.All(o => o.SizeInBit > 0), "sample object sizes " + program.Id);
            foreach (var kind in new[] { ParameterDataType.Number, ParameterDataType.Enumeration, ParameterDataType.Text })
                if (program.ParameterInstances.Any(i => i.ParameterType?.DataType == kind && ParameterValidation.TryValidate(i, i.Value ?? i.DefaultValue, out _)))
                    editable.Add(kind);
        }
        files++;
    }
    Check(programs > 0 && instances > 0 && enums > 0 && numbers > 0 && texts > 0, "real child shapes exercised");
    Check(editable.Count == 3, "real numeric, enum and text defaults can be edited and validated");

    // Real manufacturer dynamic programs: every archive and program part must evaluate.
    var archives = Directory.EnumerateFiles(samples, "*.knxprod").Count();
    var opened = 0; var dynamicPrograms = 0; var dynamicChooses = 0; var dynamicSelected = 0;
    var dynamicAssignments = 0; var dynamicVisible = 0; var maxPasses = 0; var nonConverged = 0;
    var dynamicKinds = new HashSet<string>(StringComparer.Ordinal);
    foreach (var file in Directory.EnumerateFiles(samples, "*.knxprod"))
    {
        using var archive = ZipFile.OpenRead(file);
        opened++;
        foreach (var part in archive.Entries.Where(e =>
                     e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                     e.FullName.Contains("_A-", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = part.Open();
            var evaluation = DynamicParameters.Evaluate(XDocument.Load(stream).Root!);
            dynamicPrograms++;
            dynamicChooses += evaluation.EvaluatedChooses;
            dynamicSelected += evaluation.SelectedBranches;
            dynamicAssignments += evaluation.Assignments;
            dynamicVisible += evaluation.Parameters.Values.Count(p => p.IsVisible);
            maxPasses = Math.Max(maxPasses, evaluation.Passes);
            if (!evaluation.Converged) nonConverged++;
            foreach (var issue in evaluation.Unsupported) dynamicKinds.Add(issue);
        }
    }
    Check(opened == archives && archives > 0, "dynamic corpus archives opened");
    Check(dynamicPrograms >= 273, "dynamic corpus programs evaluated");
    Check(dynamicChooses > 0 && dynamicSelected > 0 && dynamicAssignments > 0 && dynamicVisible > 0,
        "dynamic corpus exercised");
    Check(nonConverged == 0 && maxPasses <= DynamicParameters.MaxPasses, "dynamic corpus converges within limit");
    Console.WriteLine($"Dynamic MDT: {opened} archives, {dynamicPrograms} programs; {dynamicChooses} chooses, " +
        $"{dynamicSelected} selected, {dynamicAssignments} assignments, {dynamicVisible} visible refs; " +
        $"{dynamicKinds.Count} unsupported kinds, max {maxPasses} passes");
    foreach (var kind in dynamicKinds.Order(StringComparer.Ordinal)) Console.WriteLine("  unsupported: " + kind);
    Console.WriteLine($"MDT: {files} files, {programs} programs, {instances} instances; types: {numbers} number, {enums} enum, {texts} text");
}
else Console.WriteLine("SKIP MDT corpus unavailable");
Console.WriteLine($"PASS {checks} parameter checks");

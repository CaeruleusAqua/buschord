using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class NeverPlacedObjectChecks
{
    private const string App = "M-0001_A-neverplaced";
    private static string Placed(int instance) => $"MD-1_M-1_MI-{instance}_O-1_R-1";
    private static string Bare(int instance) => $"MD-1_M-1_MI-{instance}_O-2";

    private static XDocument Application() => XDocument.Parse($"""
        <KNX><ApplicationProgram Id="{App}"><Static>
        <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" minInclusive="0" maxInclusive="2" /></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-count" ParameterType="T" Value="1" /></Parameters>
        <ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /></ParameterRefs>
        <ModuleDefs><ModuleDef Id="{App}_MD-1"><Static>
        <ComObjects><ComObject Id="{App}_MD-1_O-1" Number="1" ObjectSize="1 Bit" CommunicationFlag="Enabled" Text="Placed" />
        <ComObject Id="{App}_MD-1_O-2" Number="2" ObjectSize="2 Bytes" CommunicationFlag="Enabled" Text="Never referenced" /></ComObjects>
        <ComObjectRefs><ComObjectRef Id="{App}_MD-1_O-1_R-1" RefId="{App}_MD-1_O-1" /></ComObjectRefs>
        </Static><Dynamic><ComObjectRefRef RefId="{App}_MD-1_O-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
        <Dynamic><ParameterRefRef RefId="R-count" /><Repeat Id="Repeat" Count="0" ParameterRefId="R-count">
        <Module Id="{App}_MD-1_M-1" RefId="{App}_MD-1" /></Repeat></Dynamic>
        </ApplicationProgram></KNX>
        """);

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var source = Application();
        var original = source.ToString();
        var one = ApplicationSnapshot.Evaluate(source, new Dictionary<string, string?> { ["R-count"] = "1" });
        var two = ApplicationSnapshot.Evaluate(source, new Dictionary<string, string?> { ["R-count"] = "2" });
        Check("Never-referenced module definitions remain valid and known inactive", one.IsValid && two.IsValid
            && one.ModuleObjectNumbers.Count == 2 && one.ModuleObjectNumbers[Bare(1)] == 2
            && one.ActiveObjects.SetEquals(new[] { Placed(1) }) && !one.ObjectReferenceNumbers.ContainsKey(Bare(1)));
        Check("Structural object counts exclude bare definitions while retaining actual references", two.ObjectReferenceNumbers.Count == 2
            && two.ObjectReferenceNumbers.Keys.Except(one.ObjectReferenceNumbers.Keys).SequenceEqual(new[] { Placed(2) })
            && two.ModuleObjectNumbers.Count == 4 && two.ActiveObjects.SetEquals(new[] { Placed(1), Placed(2) }));

        var project = WorkbenchJson.CreateDemo();
        var device = ProjectQuery.DeviceList(project).First();
        device.ComObjectRefs.Clear();
        two.PrepareObjectUpdate(project, device, one)();
        Check("Growing a module never creates phantom object references for bare definitions", device.ComObjectRefs.Count == 2
            && device.ComObjectRefs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal).SetEquals(new[] { Placed(1), Placed(2) }));

        var retainedBare = new ComObjectInstanceRef { Id = Bare(1), ComObjectId = Bare(1), Number = 2 };
        device.ComObjectRefs.Add(retainedBare);
        var retainedKey = new ObjectKey(device.Id, retainedBare.Id).ToString();
        var retainedMetadata = new ObjectMetadata { Active = true, Role = "retained" };
        project.Workbench.Objects[retainedKey] = retainedMetadata;
        two.PrepareObjectUpdate(project, device, one)();
        Check("A saved bare definition remains inactive while its declaration is still present", !retainedMetadata.Active
            && retainedMetadata.StructuralDeclaredNumber == 2 && retainedMetadata.Role == "retained"
            && device.ComObjectRefs.Count == 3 && device.ComObjectRefs.Contains(retainedBare));

        var savedProject = WorkbenchJson.CreateDemo();
        var savedDevice = ProjectQuery.DeviceList(savedProject).First();
        savedDevice.ComObjectRefs.Clear();
        var savedBare = new ComObjectInstanceRef { Id = Bare(2), ComObjectId = Bare(2), Number = 2, Text = "Saved bare object" };
        var group = ProjectQuery.GroupAddresses(savedProject).First().Id;
        savedBare.Links.Add(group);
        savedDevice.ComObjectRefs.Add(savedBare);
        var key = new ObjectKey(savedDevice.Id, savedBare.Id).ToString();
        var metadata = new ObjectMetadata { Active = true, Dpt = "9.001", Bits = 16, Role = "saved role" };
        savedProject.Workbench.Objects[key] = metadata;
        var commit = one.PrepareObjectUpdate(savedProject, savedDevice, two);
        Check("Preparing retirement of a saved bare definition is atomic", metadata.Active && savedBare.Links.SequenceEqual(new[] { group })
            && savedDevice.ComObjectRefs.Count == 1 && !savedProject.Workbench.Objects.ContainsKey(new ObjectKey(savedDevice.Id, Placed(1)).ToString()));
        commit();
        Check("Retiring a saved bare definition keeps its identity and links while marking it inactive", savedDevice.ComObjectRefs.Contains(savedBare)
            && !metadata.Active && metadata.StructuralPreviousActive == true && metadata.StructuralDeclaredNumber == 2
            && metadata.Dpt == "9.001" && metadata.Bits == 16 && metadata.Role == "saved role"
            && savedBare.Text == "Saved bare object" && savedBare.Links.SequenceEqual(new[] { group })
            && savedDevice.ComObjectRefs.Count == 2 && savedDevice.ComObjectRefs.Any(o => o.Id == Placed(1)));

        var missing = new XDocument(source);
        missing.Descendants("ComObject").Single(e => (string?)e.Attribute("Id") == App + "_MD-1_O-1").Remove();
        var missingSnapshot = ApplicationSnapshot.Evaluate(missing, new Dictionary<string, string?> { ["R-count"] = "1" });
        Check("A real module reference with a missing declaration still fails closed", !missingSnapshot.IsValid && !missingSnapshot.SupportsLiveStructure);
        missing.Descendants("ComObjectRefRef").Remove();
        var unplacedMissing = ApplicationSnapshot.Evaluate(missing, new Dictionary<string, string?> { ["R-count"] = "1" });
        Check("An unplaced real reference with a missing declaration is not a valid bare definition", !unplacedMissing.IsValid
            && !unplacedMissing.SupportsLiveStructure);
        var unknown = new XDocument(source);
        var placement = unknown.Descendants("ComObjectRefRef").Single();
        placement.ReplaceWith(new XElement("Unknown", new XElement(placement)));
        var unknownSnapshot = ApplicationSnapshot.Evaluate(unknown, new Dictionary<string, string?> { ["R-count"] = "1" });
        Check("Unknown object placement wrappers still fail closed", !unknownSnapshot.IsValid && !unknownSnapshot.SupportsLiveStructure);
        var inactiveBarePlacement = new XDocument(source);
        inactiveBarePlacement.Descendants("Dynamic").First().Add(new XElement("choose", new XAttribute("ParamRefId", "R-count"),
            new XElement("when", new XAttribute("test", "0"), new XElement("ComObjectRefRef", new XAttribute("RefId", App + "_MD-1_O-2")))));
        var inactiveBareSnapshot = ApplicationSnapshot.Evaluate(inactiveBarePlacement, new Dictionary<string, string?> { ["R-count"] = "1" });
        Check("An inactive placement of a bare definition cannot masquerade as a never-referenced object", !inactiveBareSnapshot.IsValid
            && !inactiveBareSnapshot.SupportsLiveStructure);
        Check("Object activity classification preserves the manufacturer XML", source.ToString() == original);
        return failures;
    }
}

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Knx;

// Self-check for the building tree (Locations): model queries, native persistence,
// undo snapshots, unknown-XML retention and semantic export.
// Run: dotnet run --project tests/Knx.Buildings.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

var directory = Path.Combine(Path.GetTempPath(), "opencode", "buschord-buildings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);

// Independent fixture: native Locations shape plus vendor payload, unknown type and
// unknown attributes that the model does not cover.
const string FixtureXml = """
    <KNX xmlns="http://knx.org/xml/project/23" xmlns:v="urn:vendor" created-by="fixture" v:root="keep">
      <MasterData Id="MD-1" Version="198"/>
      <Project Id="P-B" Name="Gebäude">
        <Installations><Installation InstallationId="I-1" Name="Haus" Type="Regular">
          <Topology><Area Id="A-1" Address="1" Name="EG"><Line Id="L-1" Address="1" Name="Linie" MediumTypeRefId="MT-0">
            <DeviceInstance Id="dev-1" Address="1.1.1" ProductRefId="M-0001_P-1" Name="D1"/>
            <DeviceInstance Id="dev-2" Address="1.1.2" ProductRefId="M-0001_P-1" Name="D2"/>
          </Line></Area></Topology>
          <Locations v:container="keep">
            <Space Id="sp-b" Name="Haus" Type="Building" Puid="11" CompletionStatus="Completed" v:space="keep">
              <Space Id="sp-f" Name="EG" Type="Floor" Puid="12">
                <Space Id="sp-r" Name="Wohnen" Type="Room" Puid="13">
                  <DeviceInstanceRef RefId="dev-1"/>
                  <Function Id="fn-1" Name="Licht" Puid="14" v:function="keep"/>
                  <v:Payload><v:Value>opaque</v:Value></v:Payload>
                </Space>
              </Space>
              <Space Id="sp-x" Name="Dach" Type="RoofTop" Puid="15"/>
            </Space>
          </Locations>
          <GroupAddresses><GroupRanges/></GroupAddresses>
        </Installation></Installations>
      </Project>
    </KNX>
    """;

try
{
    // --- Tree operations ---
    {
        var project = NewProject();
        var tree = project.Installations[0].Locations;
        Check("Tree: leer", tree.IsEmpty && !tree.AllNodes.Any());

        var building = tree.Add(null, "Haus", SpaceKind.Building);
        var floor = tree.Add(building, "EG", SpaceKind.Floor);
        var room = tree.Add(floor, "Wohnen", SpaceKind.Room);
        var corridor = tree.Add(floor, "Flur", SpaceKind.Corridor);
        var function = tree.Add(room, "Licht", SpaceKind.Function);

        Check("Tree: Wurzel", tree.Roots.Single() == building && building.Parent is null);
        Check("Tree: Kinder", floor.Children.Count == 2 && room.Parent == floor);
        Check("Tree: Pfad", room.Path == "Haus / EG / Wohnen", room.Path);
        Check("Tree: Nachfahren", building.Descendants.Count() == 4, building.Descendants.Count().ToString());
        Check("Tree: Find per Id", tree.Find(room.Id) == room);
        Check("Tree: Find per Elternteil/Name", tree.Find(floor.Id, "Flur") == corridor);
        Check("Tree: Funktion ist Kind", function.Parent == room && function.Kind == SpaceKind.Function);
        Check("Tree: Staircase-Alias = Stairway", SpaceKind.Staircase == SpaceKind.Stairway);

        var generatedId = tree.Add(null, "Anbau", SpaceKind.BuildingPart).Id;
        Check("Tree: generierte Id stabil/gefüllt", generatedId.StartsWith("SP-", StringComparison.Ordinal) && generatedId.Length > 3);

        Check("Tree: Remove Kind", floor.Remove(corridor) && corridor.Parent is null && floor.Children.Count == 1);
        Check("Tree: Remove Wurzel", tree.Remove(tree.Roots.Last()));
        Check("Tree: doppeltes Anhängen verweigert", Throws(() => floor.Add(room)));
        Check("Tree: Zyklus verweigert", Throws(() => room.Add(building)));
    }

    // --- BuildingEdits: validated rename and delete that keeps children ---
    {
        var tree = NewProject().Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-house");
        var floor = tree.Add(house, "EG", SpaceKind.Floor, "SP-floor");
        var roomA = tree.Add(floor, "Wohnen", SpaceKind.Room, "SP-roomA");
        var roomB = tree.Add(floor, "Kueche", SpaceKind.Room, "SP-roomB");
        tree.Add(house, "Verteilung", SpaceKind.DistributionBoard, "SP-db");

        Check("Edit: Umbenennen trimmt", BuildingEdits.Rename(roomA, "  Wohnen EG  ") == SpaceEditResult.Ok
            && roomA.Name == "Wohnen EG");
        Check("Edit: leerer Name verweigert", BuildingEdits.Rename(roomA, "   ") == SpaceEditResult.EmptyName
            && roomA.Name == "Wohnen EG");
        Check("Edit: fehlender Name verweigert", BuildingEdits.Rename(roomA, null) == SpaceEditResult.EmptyName);

        Check("Edit: Löschen behält Kinder", BuildingEdits.RemoveKeepingChildren(tree, floor));
        Check("Edit: Kinder wandern zum Elternteil", roomA.Parent == house && roomB.Parent == house,
            $"{roomA.Parent?.Name}/{roomB.Parent?.Name}");
        Check("Edit: Reihenfolge der Kinder bleibt",
            house.Children.Select(n => n.Id).SequenceEqual(["SP-roomA", "SP-roomB", "SP-db"]),
            string.Join(",", house.Children.Select(n => n.Id)));
        Check("Edit: gelöschter Knoten getrennt", floor.Parent is null && !house.Children.Contains(floor));

        var annex = tree.Add(null, "Anbau", SpaceKind.Building, "SP-annex");
        Check("Edit: Wurzel löschen hebt Kinder zu Wurzeln", BuildingEdits.RemoveKeepingChildren(tree, house)
            && annex.Parent is null
            && tree.Roots.Select(n => n.Id).SequenceEqual(["SP-roomA", "SP-roomB", "SP-db", "SP-annex"]),
            string.Join(",", tree.Roots.Select(n => n.Id)));
        Check("Edit: fremder Knoten wird verweigert", !BuildingEdits.RemoveKeepingChildren(tree, floor));

        // Devices installed directly in the removed node lose their reference (become unassigned);
        // devices on promoted children stay.
        var project = NewProject(Dev("dev-1", "1.1.1"), Dev("dev-2", "1.1.2"));
        var tree2 = project.Installations[0].Locations;
        var house2 = tree2.Add(null, "Haus", SpaceKind.Building, "SP-2");
        var room2 = tree2.Add(house2, "Wohnen", SpaceKind.Room, "SP-3");
        room2.AssignDevice("dev-1");
        house2.AssignDevice("dev-2");
        BuildingEdits.RemoveKeepingChildren(tree2, house2);
        Check("Edit: Gerät am gelöschten Knoten wird frei",
            project.UnassignedDevices().Select(d => d.Id).SequenceEqual(["dev-2"]),
            string.Join(",", project.UnassignedDevices().Select(d => d.Id)));
        Check("Edit: Gerät am Kind bleibt zugewiesen",
            room2.DeviceRefs.SequenceEqual(["dev-1"]) && room2.Parent is null);
    }

    // --- Device assignment / unassignment ---
    {
        var project = NewProject(Dev("dev-1", "1.1.1"), Dev("dev-2", "1.1.2"), Dev("dev-3", "1.1.3"));
        var installation = project.Installations[0];
        var tree = installation.Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building);
        var floor = tree.Add(house, "EG", SpaceKind.Floor);
        var roomA = tree.Add(floor, "Wohnen", SpaceKind.Room);
        var roomB = tree.Add(floor, "Kueche", SpaceKind.Room);

        var devices = ProjectQuery.DeviceList(project).ToList();
        Check("Gerät: Zuweisung per Referenz", roomA.AssignDevice(devices[0]) && roomA.DeviceRefs.Contains("dev-1"));
        Check("Gerät: Zuweisung per Id", roomA.AssignDevice("dev-2"));
        Check("Gerät: Doppelzuweisung ist No-op", !roomA.AssignDevice("dev-1") && roomA.DeviceRefs.Count == 2);
        Check("Gerät: Geräte im Teilbaum", BuildingTree.DevicesUnder(house).Count() == 2);
        Check("Gerät: FindDevice", tree.FindDevice("dev-2") == roomA);
        Check("Gerät: Nicht zugewiesen", project.UnassignedDevices().Select(d => d.Id).SequenceEqual(["dev-3"]),
            string.Join(",", project.UnassignedDevices().Select(d => d.Id)));

        tree.Assign(roomB, "dev-1");
        Check("Gerät: Ummontage entfernt alte Referenz", !roomA.DeviceRefs.Contains("dev-1") && roomB.DeviceRefs.Contains("dev-1"));
        Check("Gerät: Unassign", roomB.UnassignDevice(devices[0]) && !roomB.DeviceRefs.Contains("dev-1"));
        Check("Gerät: Unassign per Id unbekannt ist false", !roomB.UnassignDevice("dev-9"));

        // Dangling references are data, not errors: they stay and do not mark devices as assigned.
        roomB.AssignDevice("dev-gone");
        Check("Gerät: hängende Referenz bleibt", tree.FindDevice("dev-gone") == roomB);
        Check("Gerät: hängende Referenz zählt nicht als zugewiesen", project.UnassignedDevices().Select(d => d.Id).SequenceEqual(["dev-1", "dev-3"]),
            string.Join(",", project.UnassignedDevices().Select(d => d.Id)));
    }

    // --- Save/load round trip (native Locations) ---
    var roundTripPath = Path.Combine(directory, "roundtrip.knxproj");
    {
        var project = NewProject(Dev("dev-1", "1.1.1"), Dev("dev-2", "1.1.2"));
        var tree = project.Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-house", puid: 11);
        var floor = tree.Add(house, "EG", SpaceKind.Floor, "SP-floor", puid: 12);
        var room = tree.Add(floor, "Wohnen", SpaceKind.Room, "SP-room", puid: 13);
        room.Comment = "Kommentar";
        room.Number = "1.01";
        tree.Add(room, "Licht", SpaceKind.Function, "SP-fn", puid: 14);
        room.AssignDevice("dev-1");
        room.AssignDevice("dev-2");

        Knxproj.Save(project, roundTripPath);
        var reloaded = Knxproj.Load(roundTripPath);
        var reloadedTree = reloaded.Installations[0].Locations;
        var reloadedHouse = reloadedTree.Find("SP-house");
        var reloadedRoom = reloadedTree.Find("SP-room");
        Check("Persistenz: Wurzel", reloadedTree.Roots.Single().Id == "SP-house" && reloadedHouse?.Kind == SpaceKind.Building);
        Check("Persistenz: Hierarchie", reloadedRoom?.Path == "Haus / EG / Wohnen", reloadedRoom?.Path);
        Check("Persistenz: Puid", reloadedHouse?.Puid == 11 && reloadedTree.Find("SP-fn")?.Puid == 14);
        Check("Persistenz: Attribute", reloadedRoom?.Comment == "Kommentar" && reloadedRoom?.Number == "1.01");
        Check("Persistenz: Gerätereferenzen", reloadedRoom!.DeviceRefs.SequenceEqual(["dev-1", "dev-2"]),
            string.Join(",", reloadedRoom.DeviceRefs));
        Check("Persistenz: Funktion", reloadedTree.Find("SP-fn")?.Kind == SpaceKind.Function);
        Check("Persistenz: zugewiesen nach Reload", !reloaded.UnassignedDevices().Any());

        // Native XML shape: Locations sits between Topology and GroupAddresses, and the
        // per-space sequence is Space*, DeviceInstanceRef*, Function*.
        var doc = ReadProjectXml(roundTripPath);
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        var children = doc.Descendants(ns + "Installation").Single().Elements().Select(e => e.Name.LocalName).ToList();
        Check("XML: Containerreihenfolge", string.Join(",", children) == "Topology,Locations,GroupAddresses", string.Join(",", children));
        var roomXml = doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "SP-room");
        Check("XML: Kind als Type-Attribut", (string?)roomXml.Attribute("Type") == "Room");
        Check("XML: Reihenfolge im Space",
            string.Join(",", roomXml.Elements().Select(e => e.Name.LocalName)) == "DeviceInstanceRef,DeviceInstanceRef,Function",
            string.Join(",", roomXml.Elements().Select(e => e.Name.LocalName)));
        Check("XML: DeviceInstanceRef RefId", (string?)roomXml.Elements(ns + "DeviceInstanceRef").First().Attribute("RefId") == "dev-1");
    }

    // --- Schema order: a Locations container added to an archive that has none ---
    const string NoLocationsXml = """
        <KNX xmlns="http://knx.org/xml/project/23">
          <MasterData Id="MD-1" Version="198"/>
          <Project Id="P-O" Name="Ordnung">
            <Installations><Installation InstallationId="I-1" Name="Haus" Type="Regular">
              <Topology><Area Id="A-1" Address="1" Name="EG"><Line Id="L-1" Address="1" Name="Linie" MediumTypeRefId="MT-0"/></Area></Topology>
              <GroupAddresses><GroupRanges/></GroupAddresses>
            </Installation></Installations>
          </Project>
        </KNX>
        """;
    {
        var sourcePath = Path.Combine(directory, "nolocations.knxproj");
        File.WriteAllBytes(sourcePath, Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(NoLocationsXml) }));
        var project = Knxproj.Load(sourcePath);
        project.Installations[0].Locations.Add(null, "Haus", SpaceKind.Building, "SP-order");
        var addedPath = Path.Combine(directory, "order-added.knxproj");
        Knxproj.Save(project, addedPath);
        Check("XML-Ordnung: Locations neu einsortiert", ContainerOrder(addedPath) == "Topology,Locations,GroupAddresses",
            ContainerOrder(addedPath));

        // Deleting the last space drops the container; adding one again must re-create it in place.
        var reopened = Knxproj.Load(addedPath);
        var reopenedTree = reopened.Installations[0].Locations;
        reopenedTree.Remove(reopenedTree.Roots.Single());
        var emptiedPath = Path.Combine(directory, "order-emptied.knxproj");
        Knxproj.Save(reopened, emptiedPath);
        Check("XML-Ordnung: letzter Bereich entfernt",
            !ReadProjectXml(emptiedPath).Descendants((XNamespace)Knxproj.ProjectNamespace + "Locations").Any());

        var again = Knxproj.Load(emptiedPath);
        again.Installations[0].Locations.Add(null, "Haus", SpaceKind.Building, "SP-order");
        var readdedPath = Path.Combine(directory, "order-readded.knxproj");
        Knxproj.Save(again, readdedPath);
        Check("XML-Ordnung: nach erneutem Anlegen", ContainerOrder(readdedPath) == "Topology,Locations,GroupAddresses",
            ContainerOrder(readdedPath));
        Check("XML-Ordnung: Bereich wieder da",
            Knxproj.Load(readdedPath).Installations[0].Locations.Roots.Single().Id == "SP-order");
    }

    // --- Deleting a device also clears its building assignment ---
    {
        var project = NewProject(Dev("dev-1", "1.1.1"), Dev("dev-2", "1.1.2"));
        var tree = project.Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-del");
        var room = tree.Add(house, "Wohnen", SpaceKind.Room, "SP-del-room");
        room.AssignDevice("dev-1");
        var second = new Installation { Id = "I-2", Name = "Zweit" };
        second.Locations.Add(null, "Haus 2", SpaceKind.Building, "SP-del-2").AssignDevice("dev-1");
        project.Installations.Add(second);

        var removed = ProjectQuery.RemoveDevice(project, ProjectQuery.DeviceList(project).First(d => d.Id == "dev-1"));
        Check("Gerät löschen: aus Topologie entfernt", removed && ProjectQuery.DeviceList(project).All(d => d.Id != "dev-1"));
        Check("Gerät löschen: Zuweisung im Raum entfernt", !room.DeviceRefs.Contains("dev-1"), string.Join(",", room.DeviceRefs));
        Check("Gerät löschen: Zuweisung in zweiter Installation entfernt",
            !project.Installations[1].Locations.AllDeviceRefs().Contains("dev-1"));
        Check("Gerät löschen: Gerät ist nicht mehr zugewiesen",
            project.UnassignedDevices().Select(d => d.Id).SequenceEqual(["dev-2"]),
            string.Join(",", project.UnassignedDevices().Select(d => d.Id)));

        var deletedPath = Path.Combine(directory, "device-deleted.knxproj");
        Knxproj.Save(project, deletedPath);
        var doc = ReadProjectXml(deletedPath);
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        Check("Gerät löschen: kein Gerät im XML", !doc.Descendants(ns + "DeviceInstance")
            .Any(e => (string?)e.Attribute("Id") == "dev-1"));
        Check("Gerät löschen: keine hängende DeviceInstanceRef", !doc.Descendants(ns + "DeviceInstanceRef").Any(),
            string.Join(",", doc.Descendants(ns + "DeviceInstanceRef").Select(e => (string?)e.Attribute("RefId"))));
    }

    // --- Unknown kind, ids and Puid ---
    {
        var project = NewProject();
        var tree = project.Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-u");
        var unknown = tree.Add(house, "Dach", SpaceKind.Unknown, "SP-unknown");
        var path = Path.Combine(directory, "unknown.knxproj");
        Knxproj.Save(project, path);
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        var unknownXml = ReadProjectXml(path).Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "SP-unknown");
        Check("Unbekannt: kein Type=Unknown", unknownXml.Attribute("Type") is null, (string?)unknownXml.Attribute("Type"));

        unknown.RawType = "RoofTop";
        var rawPath = Path.Combine(directory, "unknownraw.knxproj");
        Knxproj.Save(project, rawPath);
        Check("Unbekannt: RawType verbatim",
            (string?)ReadProjectXml(rawPath).Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "SP-unknown").Attribute("Type") == "RoofTop");
        Check("Unbekannt: Puid 0 ist stabil",
            (string?)ReadProjectXml(rawPath).Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "SP-u").Attribute("Puid") == "0");
    }

    // --- New ids are unique beyond the siblings ---
    {
        var project = NewProject();
        var tree = project.Installations[0].Locations;
        var first = tree.Add(null, "Haus", SpaceKind.Building, "SP-dup");
        var second = tree.Add(null, "Anbau", SpaceKind.Building, "SP-dup");
        var child = tree.Add(first, "Wohnen", SpaceKind.Room, "SP-dup");
        Check("Id: Wurzel behält angefragte Id", first.Id == "SP-dup");
        Check("Id: doppelte Id unter anderem Elternteil wird eindeutig",
            second.Id != "SP-dup" && child.Id != "SP-dup" && second.Id != child.Id,
            $"{second.Id}/{child.Id}");
        var path = Path.Combine(directory, "unique.knxproj");
        Knxproj.Save(project, path);
        var ids = Knxproj.Load(path).Installations[0].Locations.AllNodes.Select(n => n.Id).ToList();
        Check("Id: eindeutig nach Save/Load", ids.Count == 3 && ids.Distinct().Count() == 3, string.Join(",", ids));
    }

    // --- Undo/redo snapshots the building tree ---
    {
        var project = NewProject(Dev("dev-1", "1.1.1"));
        var tree = project.Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-house");
        tree.Add(house, "EG", SpaceKind.Floor, "SP-floor");
        var undo = new UndoStack();
        undo.Reset(project);

        var room = tree.Add(tree.Find("SP-floor")!, "Wohnen", SpaceKind.Room, "SP-room");
        room.AssignDevice("dev-1");
        undo.Push(project);

        var back = undo.Undo();
        Check("Undo: Raum verschwindet", back is not null && back.Installations[0].Locations.Find("SP-room") is null);
        Check("Undo: Baum bleibt", back?.Installations[0].Locations.Roots.Single().Id == "SP-house");
        Check("Undo: Gerät wieder unzugewiesen", back!.UnassignedDevices().Select(d => d.Id).SequenceEqual(["dev-1"]));

        var forward = undo.Redo();
        var forwardRoom = forward?.Installations[0].Locations.Find("SP-room");
        Check("Redo: Raum und Gerät zurück", forwardRoom?.Path == "Haus / EG / Wohnen" && forwardRoom.DeviceRefs.SequenceEqual(["dev-1"]),
            forwardRoom?.Path);

        var changed = undo.Undo()!;
        changed.Installations[0].Locations.Find("SP-house")!.Name = "Haus (neu)";
        undo.Push(changed);
        Check("Undo: Umbenennung rückgängig", undo.Undo()?.Installations[0].Locations.Roots.Single().Name == "Haus");
    }

    // --- Unknown native XML survives save/load and edits ---
    var fixturePath = Path.Combine(directory, "fixture.knxproj");
    File.WriteAllBytes(fixturePath, Zip(new Dictionary<string, byte[]>
    {
        ["0.xml"] = Encoding.UTF8.GetBytes(FixtureXml),
        ["unknown/part.bin"] = [1, 2, 3],
    }));
    var untouchedBytes = File.ReadAllBytes(fixturePath);
    var extensionPath = Path.Combine(directory, "extension.knxproj");
    {
        var project = Knxproj.Load(fixturePath);
        var tree = project.Installations[0].Locations;
        var house = tree.Find("sp-b");
        var floor = tree.Find("sp-f");
        var room = tree.Find("sp-r");
        Check("Fremd: Baum gelesen", house?.Kind == SpaceKind.Building && floor?.Path == "Haus / EG" && room?.Path == "Haus / EG / Wohnen");
        Check("Fremd: unbekannter Typ bleibt", tree.Find("sp-x")?.Kind == SpaceKind.Unknown && tree.Find("sp-x")?.RawType == "RoofTop");
        Check("Fremd: Funktion kind", tree.Find("fn-1")?.Kind == SpaceKind.Function);
        Check("Fremd: Geräteref gelesen", room!.DeviceRefs.SequenceEqual(["dev-1"]));

        house!.Name = "Haus 2";
        var kitchen = tree.Add(floor!, "Kueche", SpaceKind.Room, "sp-k");
        kitchen.AssignDevice("dev-2");
        Knxproj.Save(project, extensionPath);

        var doc = ReadProjectXml(extensionPath);
        var vendor = (XNamespace)"urn:vendor";
        var ns = (XNamespace)Knxproj.ProjectNamespace;
        Check("Fremd: Quellarchiv unverändert", File.ReadAllBytes(fixturePath).SequenceEqual(untouchedBytes));
        Check("Fremd: Vendor-Attribut am Container", (string?)doc.Descendants(ns + "Locations").Single().Attribute(vendor + "container") == "keep");
        Check("Fremd: Vendor-Attribut am Space", (string?)doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "sp-b").Attribute(vendor + "space") == "keep");
        Check("Fremd: Vendor-Element am Space", doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "sp-r")
            .Descendants(vendor + "Value").Single().Value == "opaque");
        Check("Fremd: Vendor-Attribut an Function", (string?)doc.Descendants(ns + "Function").Single().Attribute(vendor + "function") == "keep");
        Check("Fremd: unbekanntes Attribut bleibt", (string?)doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "sp-b").Attribute("CompletionStatus") == "Completed");
        Check("Fremd: unbekannter Typ wird zurückgeschrieben", (string?)doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "sp-x").Attribute("Type") == "RoofTop");
        Check("Fremd: Puid bleibt", (string?)doc.Descendants(ns + "Space").Single(e => (string?)e.Attribute("Id") == "sp-b").Attribute("Puid") == "11");

        var reloaded = Knxproj.Load(extensionPath);
        var reloadedTree = reloaded.Installations[0].Locations;
        Check("Fremd: Änderung persistiert", reloadedTree.Find("sp-b")?.Name == "Haus 2" && reloadedTree.Find("sp-k")?.DeviceRefs.SequenceEqual(["dev-2"]) == true);
        Check("Fremd: unbekannter Typ nach Reload", reloadedTree.Find("sp-x")?.RawType == "RoofTop");
        Check("Fremd: Archivteil bleibt", Unzip(File.ReadAllBytes(extensionPath))["unknown/part.bin"].SequenceEqual(new byte[] { 1, 2, 3 }));

        // Deleting a modeled space (and its payload) persists; untouched siblings keep vendor data.
        reloadedTree.Remove(reloadedTree.Find("sp-k")!);
        var deletePath = Path.Combine(directory, "delete.knxproj");
        Knxproj.Save(reloaded, deletePath);
        Check("Fremd: Löschen persistiert", Knxproj.Load(deletePath).Installations[0].Locations.Find("sp-k") is null);
        Check("Fremd: Löschen behält Vendor-Daten", ReadProjectXml(deletePath).Descendants((XNamespace)"urn:vendor" + "Value").Any());
    }

    // --- Semantic export with and without building data ---
    {
        var plain = NewProject(Dev("dev-1", "1.1.1"));
        var plainTurtle = SemanticExport.ToTurtle(plain);
        var plainJson = SemanticExport.ToJsonLd(plain);
        Check("Export (ohne Baum): kein loc-Präfix", !plainTurtle.Contains("loc:", StringComparison.Ordinal));
        Check("Export (ohne Baum): JSON unverändert", !plainJson.Contains("loc:", StringComparison.Ordinal));
        Check("Export (ohne Baum): Kerninhalt bleibt", plainTurtle.Contains("core:Device") && plainTurtle.Contains("core:Installation"));

        var withTree = NewProject(Dev("dev-1", "1.1.1"), Dev("dev-2", "1.1.2"));
        var tree = withTree.Installations[0].Locations;
        var house = tree.Add(null, "Haus", SpaceKind.Building, "SP-house");
        var floor = tree.Add(house, "EG", SpaceKind.Floor, "SP-floor");
        var room = tree.Add(floor, "Wohnen", SpaceKind.Room, "SP-room");
        var corridor = tree.Add(floor, "Flur", SpaceKind.Corridor, "SP-corridor");
        room.AssignDevice("dev-1");
        room.AssignDevice("dev-gone");
        var turtle = SemanticExport.ToTurtle(withTree);

        Check("Export: Präfix loc", turtle.Contains("@prefix loc: <" + SemanticExport.LocNamespace + ">"));
        Check("Export: Building/Room/Floor-Klassen",
            turtle.Contains("a loc:Building") && turtle.Contains("a loc:Room") && turtle.Contains("a loc:Floor"));
        Check("Export: Korridor als generischer Space", turtle.Contains("a loc:Space"));
        Check("Export: isSpaceOf-Verknüpfung", turtle.Contains("loc:isSpaceOf <urn:knx:space:PB:SPfloor>"),
            FirstLine(turtle, "isSpaceOf"));
        Check("Export: Gerät im Raum", turtle.Contains("loc:containsEquipment <urn:knx:device:PB:1101>"),
            FirstLine(turtle, "containsEquipment"));
        Check("Export: hängende Referenz wird nicht exportiert", !turtle.Contains("dev-gone"));
        Check("Export: Raum ohne Geräte hat kein containsEquipment",
            !FirstStatement(turtle, "SPcorridor").Contains("containsEquipment"));

        var json = SemanticExport.ToJsonLd(withTree);
        using var parsed = JsonDocument.Parse(json);
        var graph = parsed.RootElement.GetProperty("@graph").EnumerateArray().ToList();
        var roomNode = graph.Single(n => n.TryGetProperty("@id", out var id) && id.GetString() == "urn:knx:space:PB:SProom");
        Check("Export JSON: loc-Klasse", roomNode.GetProperty("@type").GetString() == "loc:Room");
        Check("Export JSON: isSpaceOf", roomNode.GetProperty("isSpaceOf").GetProperty("@id").GetString() == "urn:knx:space:PB:SPfloor");
        Check("Export JSON: containsEquipment", roomNode.GetProperty("containsEquipment")[0].GetString() == "urn:knx:device:PB:1101");
        Check("Export JSON: Kontext loc", parsed.RootElement.GetProperty("@context").TryGetProperty("loc", out var loc)
            && loc.GetString() == SemanticExport.LocNamespace);
    }
}
finally
{
    foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
    Directory.Delete(directory);
}

Console.WriteLine(failures.Count == 0 ? "\nALLE BUILDING CHECKS OK" : $"\n{failures.Count} FEHLER: {string.Join("; ", failures)}");
return failures.Count == 0 ? 0 : 1;

static DeviceInstance Dev(string id, string address) => new()
{
    Id = id,
    ProductRefId = "M-0001_P-1",
    Address = IndividualAddress.Parse(address),
    Name = id,
};

static Project NewProject(params DeviceInstance[] devices)
{
    var project = new Project { Id = "P-B", Name = "Gebäude" };
    var installation = new Installation { Id = "I-1", Name = "Haus", Type = InstallationType.Regular };
    var area = new Area { Id = "A-1", Address = 1, Name = "EG" };
    var line = new Line { Id = "L-1", Address = 1, Name = "Linie 1" };
    line.Devices.AddRange(devices);
    area.Lines.Add(line);
    installation.Areas.Add(area);
    project.Installations.Add(installation);
    return project;
}

static bool Throws(Action action)
{
    try { action(); return false; }
    catch (Exception) { return true; }
}

static string FirstLine(string text, string needle)
    => text.Split('\n').FirstOrDefault(l => l.Contains(needle, StringComparison.Ordinal)) ?? "(fehlt)";

static string FirstStatement(string text, string needle)
{
    var index = text.IndexOf(needle, StringComparison.Ordinal);
    if (index < 0) return "";
    var start = text.LastIndexOf('\n', index) + 1;
    var end = text.IndexOf(" .", index, StringComparison.Ordinal);
    return end < 0 ? text[start..] : text[start..(end + 2)];
}

static XDocument ReadProjectXml(string path)
{
    using var stream = new MemoryStream(Unzip(File.ReadAllBytes(path))["0.xml"]);
    return XDocument.Load(stream);
}

static string ContainerOrder(string path) => string.Join(",",
    ReadProjectXml(path).Descendants((XNamespace)Knxproj.ProjectNamespace + "Installation").Single()
        .Elements().Select(e => e.Name.LocalName));

static byte[] Zip(Dictionary<string, byte[]> entries)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    return buffer.ToArray();
}

static Dictionary<string, byte[]> Unzip(byte[] bytes)
{
    using var buffer = new MemoryStream(bytes);
    using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
    return zip.Entries.ToDictionary(e => e.FullName, e =>
    {
        using var stream = e.Open();
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    });
}

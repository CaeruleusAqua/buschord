using System.Diagnostics;
using System.Text.Json;
using Knx;
using Knx.Workbench;

var project = new Project { Id = "P-Benchmark", Name = "Synthetic Workbench benchmark" };
var installation = new Installation { Id = "I-Benchmark" };
var area = new Area { Id = "A-Benchmark", Address = 1 };
project.Installations.Add(installation); installation.Areas.Add(area);
var building = new SpaceNode { Id = "Building", Name = "Benchmark", Kind = SpaceKind.Building };
installation.Locations.AddRoot(building);
for (var r = 0; r < 100; r++) building.Add(new() { Id = $"Room-{r}", Name = $"Room {r}", Kind = SpaceKind.Room });
for (byte l = 1; l <= 6; l++)
{
    var line = new Line { Id = $"Line-{l}", Address = l }; area.Lines.Add(line);
    for (byte d = 1; d <= 200; d++)
    {
        var index = (l - 1) * 200 + d - 1;
        var device = new DeviceInstance { Id = $"Device-{index}", Name = $"Device {index}",
            ProductRefId = "SYNTHETIC", Address = new(1, l, d) };
        line.Devices.Add(device); building.Children[index % 100].AssignDevice(device);
        for (var n = 0; n < 12; n++)
        {
            var obj = new ComObjectInstanceRef { Id = $"Object-{n}", ComObjectId = $"Definition-{n}",
                Number = n, Text = $"Switch {n}" };
            device.ComObjectRefs.Add(obj);
            project.Workbench.Objects[new ObjectKey(device.Id, obj.Id).ToString()] = new()
            { Dpt = "1.001", Bits = 1, Flags = "CW", Active = true, ChannelId = $"Channel-{n / 4}" };
        }
    }
}
var all = WorkbenchProject.ScopeObjects(project);
if (all.Count != 14_400 || all.Select(r => r.Key).Distinct().Count() != all.Count)
    throw new Exception("Large projection has missing or duplicated communication objects.");
var room = WorkbenchProject.ScopeObjects(project, new(WorkbenchScopeKind.Room, "Room-0", Related: false));
if (room.Count != 144 || room.Any(r => r.External || r.InstalledRoomId != "Room-0"))
    throw new Exception("Room projection lost its native device membership.");
if (!ReferenceEquals(all[0].Device, area.Lines[0].Devices[0]))
    throw new Exception("Projection copied canonical native devices.");

double[] Measure(Action action)
{
    action();
    return Enumerable.Range(0, 20).Select(_ =>
    {
        var watch = Stopwatch.StartNew(); action(); return watch.Elapsed.TotalMilliseconds;
    }).Order().ToArray();
}
var projection = Measure(() => _ = WorkbenchProject.ScopeObjects(project));
var roomProjection = Measure(() => _ = WorkbenchProject.ScopeObjects(project,
    new(WorkbenchScopeKind.Room, "Room-0", Related: false)));
var filtering = Measure(() => _ = all.Where(r => r.Object.Text?.Contains("Switch 1", StringComparison.OrdinalIgnoreCase) == true).ToList());
Console.WriteLine(JsonSerializer.Serialize(new
{
    Devices = 1200, Objects = all.Count, RoomObjects = room.Count, Samples = 20,
    ProjectionP95Ms = projection[18], RoomProjectionP95Ms = roomProjection[18], FilterP95Ms = filtering[18],
    ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: true),
    Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ProcessorCount = Environment.ProcessorCount,
    Coverage = "Domain projection/filter only; not native rendering, virtualization or monitor concurrency."
}, new JsonSerializerOptions { WriteIndented = true }));
GC.KeepAlive(project);
GC.KeepAlive(all);
Console.WriteLine("WORKBENCH LARGE-PROJECTION CHECKS OK");

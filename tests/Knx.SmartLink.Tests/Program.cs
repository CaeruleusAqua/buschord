using Knx;

var checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

(Project Project, DeviceInstance Device, ComObjectInstanceRef Object) Fixture(params string?[] dpts)
{
    var project = new Project { Id = "P", Name = "Smart links" };
    var installation = new Installation { Id = "I" };
    var area = new Area { Id = "A" };
    var device = new DeviceInstance { Id = "D", ProductRefId = "Product" };
    var obj = new ComObjectInstanceRef { Id = "CO", ComObjectId = "O", Text = "Wohnzimmer Licht" };
    device.ComObjectRefs.Add(obj);
    area.Devices.Add(device);
    installation.Areas.Add(area);
    var range = new GroupRange { Id = "R" };
    for (var i = 0; i < dpts.Length; i++)
        range.Addresses.Add(new GroupAddressEntry
        {
            Id = "GA-" + i, Name = "Wohnzimmer Licht", DatapointType = dpts[i],
            Address = new GroupAddress((ushort)(i + 1)),
        });
    installation.MainRanges.Add(range);
    project.Installations.Add(installation);
    return (project, device, obj);
}

foreach (var other in new[] { "DPST-10-1", "10.001", "DPT-10", "16", "DPST-16-1", "DPST-1-2", "1.010" })
{
    var (project, _, _) = Fixture(other);
    Check(SmartLinking.Suggest(project, (_, _) => "DPST-1-1", minimumScore: 0).Count == 0,
        "Known incompatible type must be excluded despite identical names: " + other);
}

foreach (var equivalent in new[] { "DPST-1-1", "1.001", "001.0001", " dpst-01-001 " })
{
    var (project, _, _) = Fixture(equivalent);
    var suggestion = SmartLinking.Suggest(project, (_, _) => "1.001").Single();
    Check(suggestion.Score == 140 && suggestion.Reason.Contains("DPT"), "Equivalent subtype: " + equivalent);
    ProjectQuery.GroupAddresses(project).Single().Name = "Different";
    Check(SmartLinking.Suggest(project, (_, _) => "DPST-1-1").Single().Score == 40,
        "DPT-only match should retain its boost");
}

foreach (var main in new[] { "DPT-1", "1", "001" })
{
    var (project, _, _) = Fixture("DPST-1-2", "DPST-10-1");
    Check(SmartLinking.Suggest(project, (_, _) => main).Single().GroupAddress.Id == "GA-0",
        "Main-only object metadata matches same main only");
    (project, _, _) = Fixture(main);
    Check(SmartLinking.Suggest(project, (_, _) => "1.002").Single().Score == 140,
        "Main-only address metadata matches a concrete subtype");
}

foreach (var unknown in new string?[] { null, "", " ", "garbage", "DPST-1", "DPT-1-1", "1.",
             "1.1.1", "0", "-1", "1.-1", "999999999999", "DPST-1-999999999999", "+1", "1e0" })
{
    var (project, _, _) = Fixture(unknown);
    Check(SmartLinking.Suggest(project, (_, _) => "1.001").Single().Score == 100,
        "Unknown candidate permits names but no DPT boost: " + unknown);
    Check(SmartLinking.Suggest(project, (_, _) => unknown).Single().Score == 100,
        "Identical unknown strings must not imply compatibility: " + unknown);
    (project, _, _) = Fixture("DPST-10-1");
    Check(SmartLinking.Suggest(project, (_, _) => unknown).Single().Score == 100,
        "Unknown object permits name heuristic: " + unknown);
    ProjectQuery.GroupAddresses(project).Single().Name = "Different";
    Check(SmartLinking.Suggest(project, (_, _) => unknown).Count == 0,
        "Unknown metadata alone must not produce a suggestion");
}

{
    var (project, device, obj) = Fixture("1.001", "1.001", "1.001", "1.001");
    Check(SmartLinking.Suggest(project).Count == 3, "Default candidate limit");
    Check(SmartLinking.Suggest(project, perObject: 1).Single().GroupAddress.Id == "GA-0", "Stable tie order and limit");
    Check(SmartLinking.Suggest(project, perObject: 0).Count == 0 &&
          SmartLinking.Suggest(project, perObject: -1).Count == 0, "Nonpositive limit yields no suggestions");
    var suggestions = SmartLinking.Suggest(project, (_, _) => "1.001", perObject: 2);
    Check(SmartLinking.Apply(suggestions.Concat(suggestions)) == 2, "Apply keeps explicit multiple choices and deduplicates");
    Check(SmartLinking.Apply(suggestions) == 0 && obj.Links.Count == 2, "Apply is idempotent");
    Check(SmartLinking.Suggest(project).Count == 0, "Already linked objects are skipped");

    var other = new DeviceInstance { Id = "D2", ProductRefId = "Other" };
    other.ComObjectRefs.Add(new ComObjectInstanceRef { Id = obj.Id, ComObjectId = "O", Text = obj.Text });
    project.Installations[0].Areas[0].Devices.Add(other);
    Check(SmartLinking.Suggest(project, (d, _) => d == device ? "1.001" : "10.001").Count == 0,
        "Metadata lookup retains device identity even when object IDs coincide");
}

Console.WriteLine($"SmartLinking: {checks} checks passed");

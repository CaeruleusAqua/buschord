using Knx;

// Checks for project-wide search and rename replacement: hit kinds, regex, case sensitivity,
// limit, scope, preview without mutation, apply plus snapshot undo, stale previews and no-ops.
// Run: dotnet run --project tests/Knx.Search.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

static Project BuildProject()
{
    var project = new Project { Id = "P-0000000001", Name = "Suchprojekt" };
    var installation = new Installation { Id = "I-1", Name = "Haus Suche", Address = new IndividualAddress(1, 1, 0) };
    var area = new Area { Id = "A-0", Address = 0, Name = "Bereich Suche" };
    var line = new Line { Id = "L-0", Address = 0, Name = "Linie Suche" };
    var segment = new Segment { Id = "S-0", Address = 0, Name = "Segment Suche" };

    var device = new DeviceInstance
    {
        Id = "1.1.5",
        Address = new IndividualAddress(1, 1, 5),
        Name = "Aktor Suche",
        Comment = "Kommentar Suche",
        ProductRefId = "M-00FA_H-Suche",
    };
    device.ComObjectRefs.Add(new ComObjectInstanceRef
    {
        Id = "CO-1",
        ComObjectId = "CO-1",
        Text = "Text Suche",
        FunctionText = "Funktion Suche",
        Number = 3,
    });
    segment.Devices.Add(device);
    line.Segments.Add(segment);
    area.Lines.Add(line);
    installation.Areas.Add(area);

    var main = new GroupRange { Id = "GR-1", Name = "Hauptbereich", RangeStart = 1, RangeEnd = 1 };
    var middle = new GroupRange { Id = "GR-2", Name = "Mittelbereich", RangeStart = 1, RangeEnd = 1 };
    middle.Addresses.Add(new GroupAddressEntry
    {
        Id = "GA-1",
        Name = "Decke Suche",
        Comment = "GA Suche",
        Address = GroupAddress.ThreeLevel(1, 1, 1),
        DatapointType = "DPST-1-1",
    });
    main.Ranges.Add(middle);
    installation.MainRanges.Add(main);
    project.Installations.Add(installation);
    return project;
}

static string Xml(Project project) => Knxproj.ToXml(project).ToString();

var project = BuildProject();

// ---------------------------------------------------------------- hit kinds and fields

var all = ProjectSearch.Search(project, new SearchQuery { Text = "Suche" });
var kinds = all.Hits.Select(hit => hit.Kind).Distinct().OrderBy(kind => kind).ToList();
Check("all scopes find devices, group addresses, topology and communication objects",
    all.Total == 11, all.Total.ToString());
Check("hit kinds are typed", string.Join(",", kinds) ==
    "communication_object,device,group_address,topology", string.Join(",", kinds));
Check("hit carries id, path, field and snippet",
    all.Hits.All(hit => hit.Id.Length > 0 && hit.Path.Length > 0 && hit.Field.Length > 0 && hit.Snippet.Length > 0));
Check("device product reference is searchable",
    all.Hits.Any(hit => hit.Kind == ProjectSearch.DeviceKind && hit.Field == "product_ref"));
Check("group address matched fields include name, address, datapoint type",
    ProjectSearch.Search(project, new SearchQuery { Text = "1/1/1", Scope = SearchScope.GroupAddresses })
        .Hits.Any(hit => hit.Field == "address"));
Check("communication object number is searchable",
    ProjectSearch.Search(project, new SearchQuery { Text = "3", Scope = SearchScope.CommunicationObjects })
        .Hits.Any(hit => hit.Kind == ProjectSearch.CommunicationObjectKind && hit.Field == "number"));

// ---------------------------------------------------------------- text, regex, case

var insensitive = ProjectSearch.Search(project, new SearchQuery { Text = "aktor suche" });
Check("plain search is case-insensitive by default", insensitive.Total == 1, insensitive.Total.ToString());

var sensitive = ProjectSearch.Search(project, new SearchQuery { Text = "aktor suche", CaseSensitive = true });
Check("case_sensitive rejects a different case", sensitive.Total == 0, sensitive.Total.ToString());

var regex = ProjectSearch.Search(project, new SearchQuery { Text = @"^Aktor \w+$", Regex = true, Scope = SearchScope.Devices });
Check("regex search matches a device name", regex.Total == 1 &&
    regex.Hits[0].Field == "name", regex.Total.ToString());

try
{
    ProjectSearch.Search(project, new SearchQuery { Text = "([", Regex = true });
    Check("invalid regex throws", false);
}
catch (ArgumentException ex)
{
    Check("invalid regex reports a readable error",
        ex.Message.Contains("invalid regular expression", StringComparison.Ordinal), ex.Message);
}

try
{
    ProjectSearch.Search(project, new SearchQuery { Text = "   " });
    Check("empty query throws", false);
}
catch (ArgumentException)
{
    Check("empty query throws", true);
}

// ---------------------------------------------------------------- limit, scope, ordering

var limited = ProjectSearch.Search(project, new SearchQuery { Text = "Suche", Limit = 3 });
Check("limit caps the returned hits", limited.Hits.Count == 3, limited.Hits.Count.ToString());
Check("limit keeps the total match count", limited.Total == 11 && limited.Truncated, limited.Total.ToString());

var again = ProjectSearch.Search(project, new SearchQuery { Text = "Suche", Limit = 3 });
Check("ordering is deterministic",
    limited.Hits.Select(hit => hit.Kind + "|" + hit.Path + "|" + hit.Field)
        .SequenceEqual(again.Hits.Select(hit => hit.Kind + "|" + hit.Path + "|" + hit.Field)));

var scoped = ProjectSearch.Search(project, new SearchQuery { Text = "Suche", Scope = SearchScope.Topology });
Check("scope limits hits to topology nodes",
    scoped.Total == 4 && scoped.Hits.All(hit => hit.Kind == ProjectSearch.TopologyKind), scoped.Total.ToString());

Check("scope name round-trips",
    ProjectSearch.TryParseScope("group_addresses", out var parsedScope) &&
    parsedScope == SearchScope.GroupAddresses && ProjectSearch.ScopeName(parsedScope) == "group_addresses");
Check("unknown scope is rejected", !ProjectSearch.TryParseScope("catalogue", out _));

// ---------------------------------------------------------------- preview does not mutate

var beforePreview = Xml(project);
var preview = ProjectSearch.Preview(project, new ReplaceQuery { Text = "Suche", Replacement = "Test" });
Check("preview does not touch the project", Xml(project) == beforePreview);
Check("preview counts fields and items", preview.FieldCount == 10 && preview.ItemCount == 7,
    preview.FieldCount + "/" + preview.ItemCount);
Check("preview reports before and after per field",
    preview.Changes.Any(change => change.Field == "name" && change.Before == "Decke Suche" &&
                                  change.After == "Decke Test"));
Check("preview never rewrites addresses or references",
    preview.Changes.All(change => change.Field is "name" or "comment" or "text" or "function_text"));

var addressPreview = ProjectSearch.Preview(project, new ReplaceQuery { Text = "1/1/1", Replacement = "2/2/2" });
Check("address-only match produces no replacement", addressPreview.FieldCount == 0,
    addressPreview.FieldCount.ToString());

var scopedPreview = ProjectSearch.Preview(project, new ReplaceQuery
{
    Text = "Suche",
    Replacement = "Test",
    Scope = SearchScope.Topology,
});
Check("replace scope limits changes", scopedPreview.FieldCount == 4, scopedPreview.FieldCount.ToString());

// ---------------------------------------------------------------- apply and snapshot undo

var stack = new UndoStack();
stack.Reset(project);
var applied = ProjectSearch.Apply(project, preview);
Check("apply reports every changed item", applied.ItemCount == 7 && applied.FieldCount == 10 &&
    applied.ChangedIds.Count == 7, applied.ItemCount + "/" + applied.FieldCount);
Check("apply renames group addresses and communication object text",
    project.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name == "Decke Test" &&
    project.Installations[0].Areas[0].Lines[0].Segments[0].Devices[0].ComObjectRefs[0].Text == "Text Test");
Check("apply leaves addresses untouched",
    project.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Address == GroupAddress.ThreeLevel(1, 1, 1));

stack.Push(project);
var restored = stack.Undo();
Check("caller snapshot restores the pre-replace project",
    restored is not null &&
    restored.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name == "Decke Suche" &&
    restored.Installations[0].Areas[0].Lines[0].Segments[0].Devices[0].ComObjectRefs[0].Text == "Text Suche");

// ---------------------------------------------------------------- stale preview fails atomically

var staleProject = BuildProject();
var stalePreview = ProjectSearch.Preview(staleProject, new ReplaceQuery { Text = "Suche", Replacement = "Test" });
staleProject.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name = "Zwischenzeitlich";
var beforeStaleApply = Xml(staleProject);
try
{
    ProjectSearch.Apply(staleProject, stalePreview);
    Check("stale preview is rejected", false);
}
catch (InvalidOperationException)
{
    Check("stale preview is rejected", true);
}
Check("stale preview changes nothing", Xml(staleProject) == beforeStaleApply);

// ---------------------------------------------------------------- no match, literal replacement

var noMatch = ProjectSearch.Preview(project, new ReplaceQuery { Text = "gibt-es-nicht", Replacement = "x" });
Check("no-match preview is empty", noMatch.FieldCount == 0);
var noMatchResult = ProjectSearch.Apply(project, noMatch);
Check("no-match apply is a no-op", noMatchResult.FieldCount == 0 && noMatchResult.ChangedIds.Count == 0);

var literalProject = BuildProject();
var literal = ProjectSearch.Preview(literalProject,
    new ReplaceQuery { Text = "Suche", Replacement = "$1", Regex = true });
ProjectSearch.Apply(literalProject, literal);
Check("regex replacement text is literal, not capture expansion",
    literalProject.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name == "Decke $1",
    literalProject.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name);

// ---------------------------------------------------------------- preview freshness (search pane contract)

// The pane keeps one preview and applies it only while a freshly computed preview still equals it.
var paneProject = BuildProject();
var paneQuery = new ReplaceQuery { Text = "Suche", Replacement = "Test" };
var panePreview = ProjectSearch.Preview(paneProject, paneQuery);
var unchangedPreview = ProjectSearch.Preview(paneProject, new ReplaceQuery { Text = "Suche", Replacement = "Test" });
Check("regenerated preview matches the stored preview",
    unchangedPreview.Changes.SequenceEqual(panePreview.Changes));

var changedPreview = ProjectSearch.Preview(paneProject, new ReplaceQuery { Text = "Suche", Replacement = "Neu" });
Check("a changed query yields a different preview", !changedPreview.Changes.SequenceEqual(panePreview.Changes));
Check("a changed query preview uses the new replacement",
    changedPreview.Changes.All(change => change.After == change.Before.Replace("Suche", "Neu")));

paneProject.Installations[0].MainRanges[0].Ranges[0].Addresses[0].Name = "Decke Zwischenstand";
var editedPreview = ProjectSearch.Preview(paneProject, paneQuery);
Check("a preview after a project edit no longer matches the stored preview",
    !editedPreview.Changes.SequenceEqual(panePreview.Changes));
var beforePaneApply = Xml(paneProject);
try
{
    ProjectSearch.Apply(paneProject, panePreview);
    Check("a stale pane preview is rejected", false);
}
catch (InvalidOperationException)
{
    Check("a stale pane preview is rejected", true);
}
Check("a rejected pane preview changes nothing", Xml(paneProject) == beforePaneApply);

try
{
    ProjectSearch.Preview(paneProject, new ReplaceQuery { Text = "   ", Replacement = "x" });
    Check("replace without a query is rejected", false);
}
catch (ArgumentException)
{
    Check("replace without a query is rejected", true);
}

// ---------------------------------------------------------------- summary

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL TESTS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} TEST(S) FAILED");
foreach (var failure in failures) Console.WriteLine("  - " + failure);
return 1;

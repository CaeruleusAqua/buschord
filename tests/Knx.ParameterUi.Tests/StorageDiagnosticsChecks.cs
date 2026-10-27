using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Views;
using Knx.Catalog;

internal static class StorageDiagnosticsChecks
{
    private const string AppId = "M-0001_A-storage";
    private const string ManufacturerDescription = "Synthetic manufacturer description";
    private static string W(string key, params object[] args) => WorkbenchStrings.Get(key, args);

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Storage UI: " + name); if (!ok) failures++; }
        var previousLanguage = Strings.Language;
        try
        {
            Strings.Language = "en";
            var parsed = Ksxml.Parse(Document());
            var rows = ParameterItemViewModel.Build(parsed.Instances, parsed.Types).ToDictionary(r => r.Instance.Id);
            Check("absolute diagnostic reports effective byte address and bit without replacing description", rows["R-A"].StorageText!.Contains(W("StorageAbsoluteAddress", "0x00001003"), StringComparison.Ordinal)
                && rows["R-A"].StorageText!.Contains(W("StorageBitOffset", "2"), StringComparison.Ordinal) && rows["R-A"].Description == ManufacturerDescription);
            Check("relative diagnostic reports LSM offset rather than an absolute memory address", rows["R-R"].StorageText!.Contains(W("StorageRelativeOffset", "0x00000013"), StringComparison.Ordinal)
                && rows["R-R"].StorageText!.Contains(W("StorageLoadStateMachine", "4"), StringComparison.Ordinal)
                && !rows["R-R"].StorageText!.Contains(W("StorageAbsolute"), StringComparison.Ordinal));
            Check("property index is explicitly XML-native and the byte offset remains separate", rows["R-I"].StorageText!.Contains(W("StorageXmlObjectIndex", "3"), StringComparison.Ordinal)
                && rows["R-I"].StorageText!.Contains(W("StoragePropertyOffset", "7"), StringComparison.Ordinal)
                && rows["R-I"].StorageText!.Contains(W("StoragePropertyId", "10"), StringComparison.Ordinal));
            Check("property type and occurrence are displayed without guessing an index", rows["R-T"].StorageText!.Contains(W("StorageObjectType", "123"), StringComparison.Ordinal)
                && rows["R-T"].StorageText!.Contains(W("StorageOccurrence", "2"), StringComparison.Ordinal)
                && !rows["R-T"].StorageText!.Contains(W("StorageXmlObjectIndex", "0"), StringComparison.Ordinal));
            Check("unresolved bases show the reference but never Memory0", rows["R-M"].StorageText!.Contains(W("StorageMissingArgument"), StringComparison.Ordinal)
                && rows["R-M"].StorageText!.Contains(W("StorageArgument", "missing-base"), StringComparison.Ordinal)
                && !rows["R-M"].StorageText!.Contains("0x", StringComparison.Ordinal) && rows["R-M"].IsEnabled);
            Check("unknown segment remains traceable without an invented address", rows["R-U"].StorageText!.Contains(W("StorageUnknownSegment"), StringComparison.Ordinal)
                && rows["R-U"].StorageText!.Contains(W("StorageSegment", "missing-segment"), StringComparison.Ordinal)
                && !rows["R-U"].StorageText!.Contains("0x", StringComparison.Ordinal));
            Check("invalid and out-of-range declarations stay visible as diagnostics", rows["R-B"].StorageText == W("StorageInvalid")
                && rows["R-X"].StorageText == W("StorageOutOfRange") && rows["R-B"].IsEnabled && rows["R-X"].IsEnabled);
            Check("undeclared storage has no extra details and raw capacity remains a byte maximum", !rows["R-N"].HasStorage && rows["R-N"].StorageText is null
                && rows["R-RAW"].StorageText!.Contains(W("StorageRawDataMaximum", 128), StringComparison.Ordinal) && !rows["R-RAW"].IsEnabled);
            Check("unsupported forms remain separate from value editing restrictions", rows["R-S"].StorageText!.StartsWith(W("StorageUnsupported"), StringComparison.Ordinal)
                && rows["R-S"].Description == ManufacturerDescription && rows["R-S"].IsEnabled);
            Check("fixed geometry separates declared resource size, verified bits and spanned positions", rows["R-A"].StorageText!.Contains(W("StorageResourceSize", 256), StringComparison.Ordinal)
                && rows["R-A"].StorageText!.Contains(W("StorageFootprintBits", 8), StringComparison.Ordinal)
                && rows["R-A"].StorageText!.Contains(W("StorageFootprintBytes", 2), StringComparison.Ordinal)
                && rows["R-A"].StorageText!.Contains(W("StorageCoverageCovered"), StringComparison.Ordinal));
            Check("a property type width does not imply a property resource capacity", rows["R-I"].StorageText!.Contains(W("StorageResourceSizeUnknown"), StringComparison.Ordinal)
                && rows["R-I"].StorageText!.Contains(W("StorageFootprintBits", 8), StringComparison.Ordinal)
                && rows["R-I"].StorageText!.Contains(W("StorageCoverageUnknown"), StringComparison.Ordinal));
            Check("raw maximum capacity does not become a known footprint or coverage", rows["R-RAW"].StorageText!.Contains(W("StorageRawDataMaximum", 128), StringComparison.Ordinal)
                && rows["R-RAW"].StorageText!.Contains(W("StorageFootprintUnknown"), StringComparison.Ordinal)
                && rows["R-RAW"].StorageText!.Contains(W("StorageCoverageUnknown"), StringComparison.Ordinal));
            ParameterItemViewModel GeometryRow(XDocument document)
            {
                var model = Ksxml.Parse(document); var instance = model.Instances.Single(i => i.Id == "R-A");
                return new(instance, instance.ParameterType);
            }
            foreach (var size in new string?[] { null, "bad", "0", "1" })
            {
                var geometryDocument = Document(); geometryDocument.Descendants("AbsoluteSegment").Single(s => (string?)s.Attribute("Id") == "AS").SetAttributeValue("Size", size);
                var geometry = GeometryRow(geometryDocument); var unknownSize = size is null or "bad";
                Check("resource size " + (size ?? "missing") + " stays distinct from a write gate", geometry.StorageText!.Contains(unknownSize ? W("StorageResourceSizeUnknown") : W("StorageResourceSize", size!), StringComparison.Ordinal)
                    && geometry.StorageText!.Contains(W(unknownSize ? "StorageCoverageUnknown" : "StorageCoverageUncovered"), StringComparison.Ordinal)
                    && geometry.IsEnabled && geometry.Description == ManufacturerDescription && geometry.Value == "0");
            }
            var textDocument = Document(); var firstType = textDocument.Descendants("ParameterType").Single(t => (string?)t.Attribute("Id") == "N");
            firstType.Elements().Single().ReplaceWith(new XElement("TypeText", new XAttribute("SizeInBit", 32)));
            Check("declared text capacity remains an unknown formatted footprint", GeometryRow(textDocument).StorageText!.Contains(W("StorageFootprintUnknown"), StringComparison.Ordinal));
            var wideDocument = Document(); wideDocument.Descendants("TypeNumber").Single().SetAttributeValue("SizeInBit", 65);
            Check("out-of-profile type width reports no footprint numbers or known coverage", GeometryRow(wideDocument).StorageText!.Contains(W("StorageFootprintOutOfRange"), StringComparison.Ordinal)
                && GeometryRow(wideDocument).StorageText!.Contains(W("StorageCoverageUnknown"), StringComparison.Ordinal)
                && !GeometryRow(wideDocument).StorageText!.Contains(W("StorageFootprintBits", 65), StringComparison.Ordinal));
            foreach (var status in new[] { ParameterFootprintStatus.Unknown, ParameterFootprintStatus.OutOfRange })
            {
                // Simulate an incomplete diagnostic producer: the renderer must obey its status,
                // never display stale numeric fields or stale Covered from an unproven result.
                var partial = new ParameterInstance(rows["R-A"].Instance);
                typeof(ParameterInstance).GetProperty(nameof(ParameterInstance.Storage))!.SetValue(partial, partial.Storage with
                { Footprint = new(status, 999, 888, ParameterResourceCoverage.Covered) });
                var diagnostic = new ParameterItemViewModel(partial, partial.ParameterType).StorageText!;
                Check(status + " suppresses stale footprint numbers and coverage", !diagnostic.Contains("999", StringComparison.Ordinal)
                    && !diagnostic.Contains("888", StringComparison.Ordinal) && !diagnostic.Contains(W("StorageCoverageCovered"), StringComparison.Ordinal)
                    && diagnostic.Contains(W("StorageCoverageUnknown"), StringComparison.Ordinal));
            }
            var row = rows["R-A"]; row.IsStorageExpanded = true;
            var changedDocument = Document(); changedDocument.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-A").Element("Memory")!.SetAttributeValue("Offset", "9");
            var changed = Ksxml.Parse(changedDocument); var fresh = changed.Instances.Single(i => i.Id == "R-A");
            var notified = new HashSet<string>(); row.PropertyChanged += (_, e) => { if (e.PropertyName is { } name) notified.Add(name); };
            typeof(ParameterItemViewModel).GetMethod("Rebind", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(row, [fresh, fresh.ParameterType]);
            Check("rebind refreshes effective storage and accessible context while retaining this row's expansion", row.StorageText!.Contains("0x00001009", StringComparison.Ordinal)
                && row.IsStorageExpanded && notified.IsSupersetOf(new[] { "StorageText", "HasStorage", "StorageAutomationName" }));
            Check("a recycled logical row starts collapsed instead of inheriting another row's state", !rows["R-M"].IsStorageExpanded);
            var changedResource = Document(); changedResource.Descendants("AbsoluteSegment").Single(s => (string?)s.Attribute("Id") == "AS").SetAttributeValue("Size", "1");
            var resourceModel = Ksxml.Parse(changedResource); var resourceInstance = resourceModel.Instances.Single(i => i.Id == "R-A");
            notified.Clear(); typeof(ParameterItemViewModel).GetMethod("Rebind", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(row, [resourceInstance, resourceInstance.ParameterType]);
            Check("rebind refreshes resource coverage without collapsing details or disabling editing", row.StorageText!.Contains(W("StorageResourceSize", 1), StringComparison.Ordinal)
                && row.StorageText!.Contains(W("StorageCoverageUncovered"), StringComparison.Ordinal) && row.IsStorageExpanded && row.IsEnabled && notified.Contains("StorageText"));
            var localizedKeys = WorkbenchStrings.Keys.Where(k => k.StartsWith("Storage", StringComparison.Ordinal) || k.StartsWith("ParameterStorage", StringComparison.Ordinal)).ToArray();
            foreach (var language in Strings.Available)
            {
                Strings.Language = language;
                Check("localized storage labels and formatting in " + language, localizedKeys.Length >= 34
                    && localizedKeys.All(key => W(key, "7") != key && !W(key, "7").Contains('{'))
                    && row.StorageAutomationName.Contains(row.Name, StringComparison.Ordinal) && rows["R-M"].Description == ManufacturerDescription);
            }
            var (vm, _) = Fixture(Path.Combine(directory, "storage-console"));
            using var pane = new ParameterPane(vm); pane.Load(vm.Devices[0]);
            var loaded = pane.Session.Rows.Single(r => r.Instance.Id == "R-A"); var changes = 0;
            loaded.PropertyChanged += (_, e) => { if (e.PropertyName == "StorageText") changes++; };
            Strings.Language = "en"; var english = loaded.StorageText; Strings.Language = "de";
            Check("existing pane refreshes storage on a live language change without creating a draft", changes > 0 && loaded.StorageText != english
                && !pane.HasUnappliedChanges && !vm.CanUndo && loaded.Description == ManufacturerDescription);
            pane.Dispose(); var disposedChanges = changes; Strings.Language = "fr";
            Check("disposed panes retain no storage language-change listener", changes == disposedChanges);
            var (uncoveredVm, _) = Fixture(Path.Combine(directory, "storage-uncovered"), resourceSize: "1");
            using var uncoveredPane = new ParameterPane(uncoveredVm); uncoveredPane.Load(uncoveredVm.Devices[0]);
            uncoveredPane.Session.Rows.Single(r => r.Instance.Id == "R-A").Value = "1";
            Check("uncovered declared geometry does not add an Apply or Undo gate", uncoveredPane.Session.CanApply && !uncoveredPane.Session.HasValidationErrors
                && uncoveredPane.Apply() && uncoveredVm.CanUndo && uncoveredVm.Devices[0].Device.ParameterValues["R-A"] == "1");
            uncoveredVm.UndoCommand.Execute(null);
            Check("one undo restores values while uncovered geometry remains informational", !uncoveredVm.CanUndo && uncoveredVm.Devices[0].Device.ParameterValues.Count == 0
                && uncoveredVm.LoadParametersFor(uncoveredVm.Devices[0]).Instances.Single(i => i.Id == "R-A").Storage.Footprint?.Coverage == ParameterResourceCoverage.Uncovered);
        }
        finally { Strings.Language = previousLanguage; }
        return failures;
    }

    internal static async Task<int> RunNative(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Native storage UI: " + name); if (!ok) failures++; }
        var previousLanguage = Strings.Language; Strings.Language = "en";
        var (vm, _) = Fixture(Path.Combine(directory, "storage-native"), resourceSize: "1", maskProfile: true);
        using var pane = new ParameterPane(vm); pane.Load(vm.Devices[0]);
        var window = new Window { Content = pane, Width = 620, Height = 600 };
        try
        {
            window.Show(); await Task.Delay(150);
            var list = pane.FindControl<ItemsControl>("ParameterList")!;
            async Task<Expander> Details(string id)
            {
                for (var attempt = 0; attempt < 30; attempt++)
                {
                    var row = pane.Session.Rows.Single(r => r.Instance.Id == id);
                    list.ScrollIntoView(row); await Task.Delay(20);
                    var current = pane.GetVisualDescendants().OfType<Expander>().SingleOrDefault(e => e.Classes.Contains("wb-storage-details")
                        && ReferenceEquals(e.DataContext, row));
                    if (current?.Content is TextBlock text && current.IsExpanded == row.IsStorageExpanded
                        && (!current.IsExpanded || ReferenceEquals(text.DataContext, row) && text.Text == row.StorageText && text.Bounds.Height > 0))
                        return current;
                }
                var realized = string.Join(", ", pane.GetVisualDescendants().OfType<Expander>().Where(e => e.Classes.Contains("wb-storage-details"))
                    .Select(e => (e.DataContext as ParameterItemViewModel)?.Instance.Id ?? "<empty>"));
                throw new InvalidOperationException($"Storage detail row {id} did not realize and bind after ScrollIntoView; current rows: {realized}.");
            }
            var missing = await Details("R-M"); var absolute = await Details("R-A");
            var absoluteRow = pane.Session.Rows.Single(r => r.Instance.Id == "R-A");
            Check("storage detail headers are compact and collapsed with parameter-specific automation names", !absolute.IsExpanded && !missing.IsExpanded
                && Equals(absolute.Header, W("ParameterStorageDetails")) && AutomationProperties.GetName(absolute) == W("ParameterStorageAccessible", absoluteRow.Name));
            var header = absolute.GetVisualDescendants().OfType<ToggleButton>().First();
            Check("details expose a focusable keyboard tab stop", header.Focusable && KeyboardNavigation.GetIsTabStop(header) && header.Focus());
            header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Space });
            header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.Space }); await Task.Delay(100);
            var body = (TextBlock)absolute.Content!;
            Check("routed keyboard activation opens this row's readonly effective location", absolute.IsExpanded && absoluteRow.IsStorageExpanded
                && body.Text == absoluteRow.StorageText && body.TextWrapping == TextWrapping.Wrap && body.Bounds.Height > 0 && !pane.HasUnappliedChanges);
            var alternateRow = pane.Session.Rows.Single(r => r.Instance.Id == "R-R");
            absolute.DataContext = alternateRow; await Task.Delay(25);
            var alternateCollapsed = !absolute.IsExpanded && !alternateRow.IsStorageExpanded;
            absolute.DataContext = absoluteRow; await Task.Delay(25);
            Check("recycled detail controls restore expansion by logical row instead of leaking it", alternateCollapsed && absolute.IsExpanded && absoluteRow.IsStorageExpanded);
            // The simulation must not leave a local DataContext on a real virtualized container.
            absolute.ClearValue(Avalonia.StyledElement.DataContextProperty);
            absolute = await Details("R-A"); body = (TextBlock)absolute.Content!;
            Check("native details show uncovered declared geometry as neutral information", body.Text!.Contains(W("StorageResourceSize", 1), StringComparison.Ordinal)
                && body.Text.Contains(W("StorageFootprintBits", 8), StringComparison.Ordinal) && body.Text.Contains(W("StorageFootprintBytes", 2), StringComparison.Ordinal)
                && body.Text.Contains(W("StorageCoverageUncovered"), StringComparison.Ordinal) && !body.Classes.Contains("wb-error"));
            var editor = absolute.GetVisualAncestors().OfType<Grid>().First().GetVisualDescendants().OfType<NumericUpDown>().Single();
            editor.Value = 1; await Task.Delay(25);
            var remainsEditable = absoluteRow.IsEnabled && pane.Session.CanApply && !pane.Session.HasValidationErrors && pane.HasUnappliedChanges;
            absolute = await Details("R-A");
            editor = absolute.GetVisualAncestors().OfType<Grid>().First().GetVisualDescendants().OfType<NumericUpDown>().Single();
            editor.Value = 0; await Task.Delay(25);
            Check("uncovered geometry leaves the actual value editor and review eligibility independent", remainsEditable && !pane.HasUnappliedChanges && !vm.CanUndo);
            missing = await Details("R-M"); missing.IsExpanded = true;
            missing = await Details("R-M");
            var missingBody = (TextBlock)missing.Content!;
            Check("unresolved storage remains accessible without disabling the value editor or inventing an address", missing.IsEnabled
                && pane.Session.Rows.Single(r => r.Instance.Id == "R-M").IsEnabled && missingBody.Text!.Contains(W("StorageArgument", "missing-base"), StringComparison.Ordinal)
                && !missingBody.Text.Contains("0x", StringComparison.Ordinal) && !vm.CanUndo);
            foreach (var language in new[] { "de", "en", "fr" })
            {
                Strings.Language = language; await Task.Delay(35);
                absolute = await Details("R-A"); body = (TextBlock)absolute.Content!;
                absoluteRow = pane.Session.Rows.Single(r => r.Instance.Id == "R-A");
                Check("open details hot-swap to " + language, body.Text == absoluteRow.StorageText
                    && body.Text!.StartsWith(W("StorageAbsolute"), StringComparison.Ordinal) && Equals(absolute.Header, W("ParameterStorageDetails"))
                    && AutomationProperties.GetName(absolute) == W("ParameterStorageAccessible", absoluteRow.Name));
            }
            var property = await Details("R-I");
            Check("profile-derived details start collapsed independently of memory rows", !property.IsExpanded
                && pane.Session.Rows.Single(r => r.Instance.Id == "R-I").IsEnabled);
            property.IsExpanded = true;
            foreach (var language in new[] { "en", "de", "fr" })
            {
                Strings.Language = language; await Task.Delay(35);
                property = await Details("R-I"); var propertyBody = (TextBlock)property.Content!;
                Check("property profile hot-swap preserves native index and separate application zero in " + language,
                    propertyBody.Text == pane.Session.Rows.Single(r => r.Instance.Id == "R-I").StorageText
                    && propertyBody.Text!.Contains(W("StorageXmlObjectIndex", 3), StringComparison.Ordinal)
                    && propertyBody.Text.Contains(W("StorageApplicationObjectIndex", 0), StringComparison.Ordinal)
                    && propertyBody.Text.Contains(W("StoragePdtElementBytes", 2), StringComparison.Ordinal)
                    && propertyBody.Text.Contains(W("StorageCoverageUnknown"), StringComparison.Ordinal)
                    && !pane.HasUnappliedChanges && !vm.CanUndo);
            }
            foreach (var applicationNamespace in new[] { "http://knx.org/xml/project/22", "urn:unknown:application" })
            {
                var application = Document(); SetProfileNamespace(application, applicationNamespace);
                var rejected = Ksxml.Parse(application, out _, profile: ParameterProfileContext.Read(MaskProfileHostChecks.Master(), "MV-0705"))
                    .Instances.Single(i => i.Id == "R-I");
                var rejectedRow = new ParameterItemViewModel(rejected, rejected.ParameterType) { IsStorageExpanded = true };
                property.DataContext = rejectedRow; await Task.Delay(35);
                var rejectedBody = (TextBlock)property.Content!;
                Check("unsupported application namespace suppresses derived native detail figures: " + applicationNamespace,
                    rejected.Storage.PropertyProfile?.Selection.Status == MaskProfileStatus.UnsupportedApplicationNamespace
                    && property.IsExpanded && rejectedBody.Text == rejectedRow.StorageText && rejectedRow.IsEnabled
                    && rejectedBody.Text!.Contains(W("StorageXmlObjectIndex", 3), StringComparison.Ordinal)
                    && !rejectedBody.Text.Contains(W("StorageApplicationObjectIndex", 0), StringComparison.Ordinal)
                    && !rejectedBody.Text.Contains(W("StoragePdtElementBytes", 2), StringComparison.Ordinal)
                    && !pane.HasUnappliedChanges && !vm.CanUndo);
                property.ClearValue(Avalonia.StyledElement.DataContextProperty); property = await Details("R-I");
            }
            window.Width = 360; await Task.Delay(60);
            absolute = await Details("R-A"); body = (TextBlock)absolute.Content!;
            Check("detail text wraps inside a small viewport using the shared semantic text class", body.TextWrapping == TextWrapping.Wrap
                && body.Classes.Contains("wb-muted") && body.Bounds.Width <= window.ClientSize.Width
                && pane.Session.Rows.Single(r => r.Instance.Id == "R-A").Description == ManufacturerDescription);
            var notifications = 0; absoluteRow.PropertyChanged += (_, e) => { if (e.PropertyName == "StorageText") notifications++; };
            pane.Dispose(); var before = notifications; Strings.Language = "de"; await Task.Delay(30);
            Check("native disposal removes the storage language subscription", notifications == before && !vm.CanUndo && vm.Devices[0].Device.ParameterValues.Count == 0);
        }
        finally { window.Close(); Strings.Language = previousLanguage; }
        return failures;
    }

    private static (MainWindowViewModel Vm, string Path) Fixture(string directory, string? resourceSize = null, bool maskProfile = false)
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "storage.knxproj");
        var project = ProjectWizard.Create(new() { Name = "Synthetic storage details", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", Name = "Synthetic", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        var application = Document();
        if (resourceSize is not null) application.Descendants("AbsoluteSegment").Single(s => (string?)s.Attribute("Id") == "AS").SetAttributeValue("Size", resourceSize);
        if (maskProfile) SetProfileNamespace(application, "http://knx.org/xml/project/20");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            using (var stream = archive.CreateEntry("M-0001/" + AppId + ".xml").Open()) application.Save(stream);
            if (maskProfile) { using var stream = archive.CreateEntry("knx_master.xml").Open(); MaskProfileHostChecks.Master().Save(stream); }
        }
        var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "catalog"))));
        vm.CreateProject(Knxproj.Load(path), null); vm.ProjectPath = path;
        return (vm, path);
    }

    private static void SetProfileNamespace(XDocument application, string applicationNamespace)
    {
        application.Descendants("ApplicationProgram").Single().SetAttributeValue("MaskVersion", "MV-0705");
        foreach (var element in application.Root!.DescendantsAndSelf()) element.Name = XNamespace.Get(applicationNamespace) + element.Name.LocalName;
    }

    private static XDocument Document()
    {
        var document = XDocument.Parse($$"""
            <KNX><ApplicationProgram Id="{{AppId}}"><Static><Code>
            <AbsoluteSegment Id="AS" Address="4096" Size="256" MemoryType="EEPROM"/>
            <RelativeSegment Id="RS" LoadStateMachine="4" Offset="16" Size="256" MemoryType="EEPROM"/>
            <AbsoluteSegment Id="US" Address="0" Size="256" MemoryType="EEPROM" UserMemory="true"/>
            </Code><ParameterTypes><ParameterType Id="N" Name="Number"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType>
            <ParameterType Id="RAW" Name="Raw"><TypeRawData MaxSize="128"/></ParameterType></ParameterTypes>
            <Parameters/><ParameterRefs/></Static><Dynamic/></ApplicationProgram></KNX>
            """);
        var declarations = new (string Id, string? Location, string Type)[]
        {
            ("A", "<Memory CodeSegment='AS' Offset='3' BitOffset='2'/>", "N"),
            ("M", "<Memory CodeSegment='AS' Offset='3' BitOffset='0' BaseOffset='missing-base'/>", "N"),
            ("R", "<Memory CodeSegment='RS' Offset='3' BitOffset='1'/>", "N"),
            ("I", "<Property ObjectIndex='3' PropertyId='10' Offset='7' BitOffset='1'/>", "N"),
            ("T", "<Property ObjectType='123' Occurrence='2' PropertyId='10' Offset='7' BitOffset='1'/>", "N"),
            ("U", "<Memory CodeSegment='missing-segment' Offset='3' BitOffset='0'/>", "N"),
            ("B", "<Memory CodeSegment='AS' Offset='bad' BitOffset='0'/>", "N"),
            ("X", "<Property ObjectIndex='3' PropertyId='256' Offset='7' BitOffset='1'/>", "N"),
            ("S", "<Memory CodeSegment='US' Offset='0' BitOffset='0'/>", "N"),
            ("N", null, "N"), ("RAW", "<Memory CodeSegment='AS' Offset='0' BitOffset='0'/>", "RAW"),
        };
        foreach (var (id, location, type) in declarations)
        {
            var parameter = new XElement("Parameter", new XAttribute("Id", "P-" + id), new XAttribute("Text", "Synthetic " + id),
                new XAttribute("Description", ManufacturerDescription), new XAttribute("ParameterType", type), new XAttribute("Value", type == "RAW" ? "" : "0"));
            if (location is not null) parameter.Add(XElement.Parse(location));
            document.Descendants("Parameters").Single().Add(parameter);
            document.Descendants("ParameterRefs").Single().Add(new XElement("ParameterRef", new XAttribute("Id", "R-" + id), new XAttribute("RefId", "P-" + id)));
            document.Descendants("Dynamic").Single().Add(new XElement("ParameterRefRef", new XAttribute("RefId", "R-" + id)));
        }
        return document;
    }
}

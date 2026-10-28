using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Catalog;

internal static class MaskProfileHostChecks
{
    private const string App = "M-0001_A-mask-host";
    private const string Mask = "MV-0705";
    internal static XDocument Master(string first = "3", string ns = "http://knx.org/xml/project/20") => XDocument.Parse($$"""
        <KNX xmlns="{{ns}}"><MasterData Version="1"><MaskVersions>
        <MaskVersion Id="{{Mask}}" MaskVersion="1797"><HawkConfigurationData><Features>
        <Feature Name="FirstAppObjectIdx" Value="{{first}}"/></Features><InterfaceObjects>
        <InterfaceObject Index="0" ObjectType="0"><Property PropertyID="10" PropertyDataType="PDT_VERSION"/></InterfaceObject>
        <InterfaceObject Index="3" ObjectType="123"><Property PropertyID="10" PropertyDataType="PDT_UNSIGNED_INT"/></InterfaceObject>
        </InterfaceObjects></HawkConfigurationData><HawkConfigurationData LegacyVersion="1"><Features>
        <Feature Name="FirstAppObjectIdx" Value="99"/></Features></HawkConfigurationData></MaskVersion>
        <MaskVersion Id="MV-0020" MaskVersion="32"><HawkConfigurationData><Features>
        <Feature Name="FirstAppObjectIdx" Value="4"/></Features></HawkConfigurationData></MaskVersion>
        </MaskVersions></MasterData></KNX>
        """);

    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Mask host: " + name); if (!ok) failures++; }
        var language = Strings.Language;
        try
        {
            Strings.Language = "en";
            var path = Fixture(Path.Combine(directory, "mask-host"), Master());
            var vm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "mask-host-catalog"))));
            vm.CreateProject(Knxproj.Load(path), path);
            ParameterInstance Row() => vm.LoadParametersFor(vm.Devices[0]).Instances.Single();
            string? Definition() => (string?)typeof(MainWindowViewModel).GetProperty("LoadedStructureDefinition", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
            var initial = Row(); var definition = Definition();
            Check("initial archived context preserves XML index and derives application zero separately", initial.Storage is
                { ObjectIndex: 3, PropertyProfile: { Selection.Status: MaskProfileStatus.Supported, ApplicationObjectIndex: 0,
                    Domain: ParameterPropertyDomain.Application, DeclaredPdt: "PDT_UNSIGNED_INT", ElementBytes: 2 },
                    Footprint.Coverage: ParameterResourceCoverage.Unknown, Resource: null });
            Check("repeat loads reuse the immutable selected profile", ReferenceEquals(initial.Storage.PropertyProfile!.Selection, Row().Storage.PropertyProfile!.Selection));
            var snapshotCallback = (Func<IReadOnlyDictionary<string, string?>, string?, ApplicationSnapshot>?)typeof(MainWindowViewModel)
                .GetProperty("LoadedStructureEvaluation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
            var live = snapshotCallback?.Invoke(new Dictionary<string, string?> { ["R"] = "2" }, "R");
            Check("captured live snapshot receives the same explicit context", live is { IsValid: true } && live.Instances.Single().Storage.PropertyProfile is
                { Selection.Status: MaskProfileStatus.Supported, Domain: ParameterPropertyDomain.Application, ApplicationObjectIndex: 0, ElementBytes: 2 });

            File.Delete(path); vm.ProjectPath = Path.Combine(directory, "irrelevant-renamed-path.knxproj");
            Check("immutable archive context survives original removal and path changes", Row().Storage.PropertyProfile?.Selection == initial.Storage.PropertyProfile.Selection && Definition() == definition);
            var state = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            void ReplaceArchive(XDocument? master, string name)
            {
                var loaded = Knxproj.Load(Fixture(Path.Combine(directory, name), master));
                state.SetValue(vm.Project, state.GetValue(loaded));
            }
            var unrelated = Master(); unrelated.Descendants().Last(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "9");
            unrelated.Descendants().Single(e => e.Name.LocalName == "MasterData").SetAttributeValue("Version", "999");
            ReplaceArchive(unrelated, "mask-unrelated"); var unrelatedRow = Row();
            Check("revision refresh excludes unrelated master bytes from definition identity", !ReferenceEquals(initial.Storage.PropertyProfile.Selection, unrelatedRow.Storage.PropertyProfile!.Selection)
                && unrelatedRow.Storage.PropertyProfile.Selection == initial.Storage.PropertyProfile.Selection && Definition() == definition);

            var session = new ParameterEditingSession(vm); session.Load(vm.Devices[0]); session.Rows.Single().Value = "2";
            ReplaceArchive(Master("4"), "mask-selected-change");
            Check("changed selected profile rejects an existing reviewed draft before mutation", !session.Apply() && vm.Project!.Installations[0].Areas[0].Lines[0].Devices[0].ParameterValues.Count == 0 && !vm.CanUndo);
            var changed = Row();
            Check("same-project application revision invalidates the cached profile", changed.Storage is
                { ObjectIndex: 3, PropertyProfile: { Domain: ParameterPropertyDomain.System, ApplicationObjectIndex: null, Selection.FirstAppObjectIndex: 4 } }
                && Definition() != definition);

            var saved = Path.Combine(directory, "mask-host-save-as.knxproj"); Knxproj.Save(vm.Project!, saved); vm.ProjectPath = saved;
            Check("Save As preserves exact master metadata and native application index", ParameterProfileContext.Read(Knxproj.LoadMasterData(Knxproj.Load(saved)), Mask).Selection.FirstAppObjectIndex == 4
                && Knxproj.LoadApplicationProgram(Knxproj.Load(saved), App)!.Descendants(XName.Get("Property", "http://knx.org/xml/project/20")).Single().Attribute("ObjectIndex")!.Value == "3");
            var missing = Fixture(Path.Combine(directory, "mask-missing"), null);
            vm.CreateProject(Knxproj.Load(missing), missing); var absent = Row();
            Check("project-reference switch cannot reuse a previous project's profile", absent.Storage.PropertyProfile is
                { Selection.Status: MaskProfileStatus.MissingMasterData, Domain: null, ElementBytes: null } && absent.IsEnabled);
            var missingSession = new ParameterEditingSession(vm); missingSession.Load(vm.Devices[0]); missingSession.Rows.Single().Value = "2";
            Check("missing profile metadata grants no extra Apply restriction", missingSession.CanApply && missingSession.Apply()
                && vm.Devices[0].Device.ParameterValues["R"] == "2" && vm.CanUndo);
            var malformed = Fixture(Path.Combine(directory, "mask-malformed"), Master());
            using (var zip = ZipFile.Open(malformed, ZipArchiveMode.Update))
            { zip.GetEntry("knx_master.xml")!.Delete(); using var stream = new StreamWriter(zip.CreateEntry("knx_master.xml").Open()); stream.Write("<KNX>"); }
            vm.CreateProject(Knxproj.Load(malformed), malformed); var unreadable = Row();
            Check("unreadable master metadata remains a diagnostic without disabling value editing", unreadable.Storage.PropertyProfile is
                { Selection.Status: MaskProfileStatus.UnreadableMasterData, Domain: null, ElementBytes: null } && unreadable.IsEnabled && unreadable.UnsupportedReason is null);

            var rendered = new ParameterItemViewModel(initial, initial.ParameterType);
            Check("read-only details label native and derived indices without a property capacity", rendered.StorageText!.Contains(WorkbenchStrings.Get("StorageXmlObjectIndex", 3), StringComparison.Ordinal)
                && rendered.StorageText.Contains(WorkbenchStrings.Get("StorageApplicationObjectIndex", 0), StringComparison.Ordinal)
                && rendered.StorageText.Contains(WorkbenchStrings.Get("StoragePdtElementBytes", 2), StringComparison.Ordinal)
                && rendered.StorageText.Contains(WorkbenchStrings.Get("StorageCoverageUnknown"), StringComparison.Ordinal) && rendered.IsEnabled);
            rendered.IsStorageExpanded = true; var notifications = new HashSet<string>();
            rendered.PropertyChanged += (_, e) => { if (e.PropertyName is { } name) notifications.Add(name); };
            typeof(ParameterItemViewModel).GetMethod("Rebind", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rendered, [changed, changed.ParameterType]);
            Check("rebind refreshes profile interpretation without collapsing or disabling the editor", rendered.StorageText!.Contains(WorkbenchStrings.Get("StorageDomainSystem"), StringComparison.Ordinal)
                && !rendered.StorageText.Contains(WorkbenchStrings.Get("StorageApplicationObjectIndex", 0), StringComparison.Ordinal)
                && rendered.IsStorageExpanded && rendered.IsEnabled && notifications.IsSupersetOf(new[] { "StorageText", "HasStorage", "StorageAutomationName" }));
            foreach (var status in new[] { MaskProfileStatus.MissingFirstAppObject, MaskProfileStatus.MaskMismatch })
            {
                var partial = new ParameterInstance(initial);
                typeof(ParameterInstance).GetProperty(nameof(ParameterInstance.Storage))!.SetValue(partial, partial.Storage with
                { PropertyProfile = initial.Storage.PropertyProfile! with { Selection = initial.Storage.PropertyProfile!.Selection with { Status = status } } });
                var text = new ParameterItemViewModel(partial, partial.ParameterType).StorageText!;
                Check(status + " suppresses stale derived domain and PDT width", !text.Contains(WorkbenchStrings.Get("StorageApplicationObjectIndex", 0), StringComparison.Ordinal)
                    && !text.Contains(WorkbenchStrings.Get("StoragePdtElementBytes", 2), StringComparison.Ordinal) && text.Contains(WorkbenchStrings.Get("StoragePdtUnknown"), StringComparison.Ordinal));
            }
            var oversizedPath = Fixture(Path.Combine(directory, "mask-oversized"), Master(), new string('X', 4096));
            vm.CreateProject(Knxproj.Load(oversizedPath), oversizedPath); var oversized = Row();
            var cached = (System.Collections.IDictionary)typeof(MainWindowViewModel).GetField("_maskProfiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            var masterRead = (bool)typeof(MainWindowViewModel).GetField("_maskMasterRead", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            Check("oversized references are diagnosed before master I/O and never retained as cache keys", oversized.Storage.PropertyProfile is
                { Selection.Status: MaskProfileStatus.InvalidDeclaration, Selection.MaskVersionId: null, Domain: null, ElementBytes: null }
                && cached.Count == 0 && !masterRead && oversized.IsEnabled);
            var namespacePath = Fixture(Path.Combine(directory, "mask-app22-master21"), Master(ns: "http://knx.org/xml/project/21"),
                applicationNamespace: "http://knx.org/xml/project/22");
            var namespaceVm = new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(directory, "mask-namespace-catalog"))));
            namespaceVm.AdoptProject(Knxproj.Load(namespacePath), namespacePath);
            var rawApplication = Knxproj.LoadApplicationProgram(namespaceVm.Project!, App)!;
            var archiveProfile = typeof(MainWindowViewModel).GetMethod("ArchiveMaskProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ParameterProfileContext Profile(XDocument application) => (ParameterProfileContext)archiveProfile.Invoke(namespaceVm, [namespaceVm.Project!, application])!;
            var first22 = Profile(rawApplication);
            var accepted20 = new XDocument(rawApplication);
            foreach (var element in accepted20.Root!.DescendantsAndSelf()) element.Name = XName.Get(element.Name.LocalName, "http://knx.org/xml/project/20");
            var next20 = Profile(accepted20);
            var again22 = Profile(rawApplication);
            Check("application namespace is checked after the raw profile cache lookup",
                first22.Selection.Status == MaskProfileStatus.UnsupportedApplicationNamespace
                && next20.Selection.Status == MaskProfileStatus.Supported
                && again22.Selection.Status == MaskProfileStatus.UnsupportedApplicationNamespace
                && Profile(accepted20).Selection.Fingerprint == next20.Selection.Fingerprint);
            var namespaceRow = namespaceVm.LoadParametersFor(namespaceVm.Devices[0]).Instances.Single();
            Check("application22 retains native selectors without derived mask indices or PDT widths", namespaceRow.Storage is
                { ObjectIndex: 3, PropertyId: 10, PropertyProfile: { Selection.Status: MaskProfileStatus.UnsupportedApplicationNamespace,
                    ApplicationObjectIndex: null, Domain: null, ElementBytes: null } });
            Check("unsupported application format has a precise localized mask explanation",
                new ParameterItemViewModel(namespaceRow, namespaceRow.ParameterType).StorageText!.Contains(WorkbenchStrings.Get("StorageMaskApplicationUnsupported"), StringComparison.Ordinal));
        }
        finally { Strings.Language = language; }
        return failures;
    }

    private static string Fixture(string directory, XDocument? master, string mask = Mask, string applicationNamespace = "http://knx.org/xml/project/20")
    {
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "mask.knxproj");
        var project = ProjectWizard.Create(new() { Name = "Synthetic mask profile", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D", Name = "Synthetic", ProductRefId = App, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        var application = XDocument.Parse($$"""
            <KNX xmlns="{{applicationNamespace}}"><ApplicationProgram Id="{{App}}" MaskVersion="{{mask}}"><Static>
            <ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8"/></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="T" Value="1"><Property ObjectIndex="3" PropertyId="10" Offset="0" BitOffset="0"/></Parameter></Parameters>
            <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs>
            <ComObjectTable><ComObject Id="O" Number="1" ObjectSize="1 Bit"/></ComObjectTable><ComObjectRefs><ComObjectRef Id="OR" RefId="O"/></ComObjectRefs>
            </Static><Dynamic><ParameterRefRef RefId="R"/><ComObjectRefRef RefId="OR"/></Dynamic></ApplicationProgram></KNX>
            """);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        using (var stream = zip.CreateEntry("M-0001/" + App + ".xml").Open()) application.Save(stream);
        if (master is not null) { using var stream = zip.CreateEntry("knx_master.xml").Open(); master.Save(stream); }
        return path;
    }
}

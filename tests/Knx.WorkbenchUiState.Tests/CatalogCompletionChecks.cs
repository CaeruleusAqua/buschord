using Knx;
using Knx.App.Services;

internal static class CatalogCompletionChecks
{
    internal static async Task<int> RunAsync(string root)
    {
        var failures = 0;
        void Check(string name, bool value)
        {
            Console.WriteLine((value ? "PASS " : "FAIL ") + "Catalogue completion " + name);
            if (!value) failures++;
        }
        foreach (var lookup in new[] { "reason", "parameters", "none" })
        {
            var directory = Path.Combine(root, "completion-" + lookup);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "synthetic.knxprod"), """
                <KNX><ManufacturerData><Manufacturer RefId="M-COMPLETE">
                <Hardware><Hardware Id="M-COMPLETE_H-1">
                  <Products><Product Id="M-COMPLETE_H-1_P-1" Text="Synthetic panel"/></Products>
                  <Hardware2Programs><Hardware2Program Id="M-COMPLETE_H-1_HP-1">
                    <ApplicationProgramRef RefId="M-COMPLETE_A-1"/>
                  </Hardware2Program></Hardware2Programs>
                </Hardware></Hardware>
                <ApplicationPrograms><ApplicationProgram Id="M-COMPLETE_A-1" ApplicationNumber="1" ApplicationVersion="1">
                  <Static><Extension EtsUiPlugin="{11111111-1111-1111-1111-111111111111}"/>
                    <ParameterTypes><ParameterType Id="PT-1" Name="Brightness" SizeInBit="8" DataType="Number"/></ParameterTypes>
                    <Parameters><Parameter Id="P-1" Name="Brightness" ParameterType="PT-1" Value="50"/></Parameters>
                    <ParameterRefs><ParameterRef Id="PR-1" RefId="P-1"/></ParameterRefs>
                  </Static>
                </ApplicationProgram></ApplicationPrograms>
                </Manufacturer></ManufacturerData></KNX>
                """);
            var library = new CatalogLibrary(new Knx.Catalog.CatalogStore(directory));
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            // Publish Current, then hold the event before the view-model subscriber.
            library.Changed += (_, _) =>
            {
                published.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Publication gate.");
            };
            var vm = new MainWindowViewModel(library);
            var notices = 0;
            vm.CatalogLoaded += (_, _) => Interlocked.Increment(ref notices);
            library.Changed += (_, _) => delivered.TrySetResult();
            var project = new Project { Id = "P-COMPLETE", Name = "Synthetic completion" };
            var installation = new Installation { Id = "I" };
            var area = new Area { Id = "A", Address = 1 };
            var line = new Line { Id = "L", Address = 1 };
            var device = new DeviceInstance { Id = "D", Address = new(1, 1, 7), ProductRefId = "M-COMPLETE_H-1_P-1" };
            project.Installations.Add(installation); installation.Areas.Add(area); area.Lines.Add(line); line.Devices.Add(device);
            vm.CreateProject(project, null);
            var session = new ParameterEditingSession(vm);
            Task<CatalogSnapshot>? loading = null;
            try
            {
                session.Load(vm.Devices.Single());
                loading = library.GetAsync();
                await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("pane starts empty while completion is pending / " + lookup, session.Rows.Length == 0 && vm.IsAwaitingCatalog);
                if (lookup == "reason") Check("interleaved reason sees the ready DCA snapshot", vm.ParameterReadOnlyReason(device) is { Length: > 0 } && !vm.IsAwaitingCatalog);
                if (lookup == "parameters") Check("another consumer can read the ready rows", vm.LoadParametersFor(vm.Devices.Single()).Instances.Count == 1 && !vm.IsAwaitingCatalog);
                release.Set();
                await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await loading;
                Check("pending completion still reaches the pane once / " + lookup, notices == 1);
                if (notices == 1) { session.Refresh(); session.SetReadOnly(vm.ParameterReadOnlyReason(device)); }
                Check("completion fills read-only rows without applying values / " + lookup,
                    session.Rows.Length == 1 && session.IsReadOnly && !session.CanApply && !session.CanReset && device.ParameterValues.Count == 0);
                library.Add(library.Current!.Catalogs);
                Check("an already warm snapshot does not repeat first-load notification / " + lookup, notices == 1);
            }
            finally
            {
                release.Set();
                if (loading is not null) await loading;
            }
        }
        return failures;
    }
}

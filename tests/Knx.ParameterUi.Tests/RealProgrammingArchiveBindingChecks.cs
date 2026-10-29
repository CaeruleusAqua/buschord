using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Knx;
using Knx.App;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus;
using Knx.Catalog;

/// <summary>Archive-only replacement with unchanged native project/configuration, using the real admitted M112 fixture.</summary>
internal static class RealProgrammingArchiveBindingChecks
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static async Task<int> RunAsync(string root)
    {
        var failed = 0;
        void Check(string name, bool value) { Console.WriteLine((value ? "PASS " : "FAIL ") + "QueueArchive " + name); if (!value) failed++; }
        var fixture = Create(root); var modelA = fixture.Project;
        var beforeXml = Knxproj.ToXml(modelA).ToString(SaveOptions.DisableFormatting);
        var beforeSignature = DeviceConfigurationSnapshot.Freeze(modelA, fixture.Device).Signature;
        var oldFingerprint = VerifiedM112ReceiptStore.ModelFingerprint(modelA);
        var imageA = OfflineM112ApplicationBuild.Build(modelA, fixture.Device);
        ReplaceApplicationCode(fixture.Path); var modelB = Knxproj.Load(fixture.Path); var deviceB = ProjectQuery.DeviceList(modelB).Single();
        var imageB = OfflineM112ApplicationBuild.Build(modelB, deviceB);
        Check("application-only ZIP replacement preserves project XML and device signature", Knxproj.ToXml(modelB).ToString(SaveOptions.DisableFormatting) == beforeXml
            && DeviceConfigurationSnapshot.Freeze(modelB, deviceB).Signature == beforeSignature);
        Check("application-only ZIP replacement changes real compiled EEPROM data", imageA.Segments.Zip(imageB.Segments).Any(pair => !pair.First.Data.ToArray().SequenceEqual(pair.Second.Data.ToArray())));
        Check("source fingerprint separates image A from archive-only image B", oldFingerprint != VerifiedM112ReceiptStore.ModelFingerprint(modelB));
        // Reproduce the pre-fix receipt: the old open guard accepted model A while pinning the already replaced file B.
        var journal = Path.Combine(root, "journal"); Directory.CreateDirectory(journal); var recovery = Path.Combine(journal, "synthetic-before-image.json");
        await File.WriteAllTextAsync(recovery, "Synthetic inspection seam only; no recovery/write authority");
        var identity = new ManagementDeviceIdentity("1.1.1", 0x0701, "0001", "000100000001", "0001010010");
        var inspection = new M112RecoveryInspection(recovery, await M112PrivatePaths.HashAsync(recovery), identity, new('A', 64), new('B', 64), []);
        var binding = ProgrammingStateBinding.Create(modelA, fixture.Path)!; var fileB = await M112PrivatePaths.HashAsync(fixture.Path); var history = Path.Combine(root, "history");
        var oldStore = new VerifiedM112ReceiptStore(history, binding, fileB, oldFingerprint, (_, _, _) => Task.FromResult(inspection));
        await oldStore.RecordVerifiedAsync(modelA, fixture.Device, new(fixture.Device.Id, "1.1.1", beforeSignature, new('C', 64), new('D', 64), new('E', 64),
            identity, recovery, inspection.FileSha256, "knx-ip:192.0.2.192:3671", DateTimeOffset.UtcNow), inspection);
        var reopened = new VerifiedM112ReceiptStore(history, binding, fileB, VerifiedM112ReceiptStore.ModelFingerprint(modelB), (_, _, _) => Task.FromResult(inspection));
        Check("reopening file B never imports a receipt for in-memory image A", (await reopened.ReadHistoryAsync(modelB)).Count == 0);
        var saved = Path.Combine(root, "resaved.knxproj"); Knxproj.Save(modelB, saved);
        Check("native save/reload normalization retains the complete source fingerprint", VerifiedM112ReceiptStore.ModelFingerprint(Knxproj.Load(saved)) == VerifiedM112ReceiptStore.ModelFingerprint(modelB));
        foreach (var kind in new[] { "master", "hardware" })
        {
            var other = Create(Path.Combine(root, kind)); var before = VerifiedM112ReceiptStore.ModelFingerprint(other.Project);
            Rewrite(other.Path, entry => kind == "master" ? entry == "knx_master.xml" : entry.EndsWith("/Hardware.xml", StringComparison.Ordinal), document =>
            {
                if (kind == "master") document.Descendants().First(element => element.Attribute("StartAddress") is not null).SetAttributeValue("StartAddress", "261");
                else document.Descendants().Single(element => element.Name.LocalName == "Hardware2Program").SetAttributeValue("LoadedImage", "AQIDBA==");
            });
            Check(kind + " image source cannot hide behind identical native project fields", before != VerifiedM112ReceiptStore.ModelFingerprint(Knxproj.Load(other.Path)));
        }
        return failed;
    }
    internal static ArchivedImageSourceChecks.Fixture Create(string root)
    {
        Directory.CreateDirectory(root); var fixture = M112UnionBuildChecks.Fixture(root, fullLoad: true);
        var path = Path.Combine(root, "target.knxproj"); Knxproj.Save(fixture.Project, path); var project = Knxproj.Load(path);
        return fixture with { Project = project, Device = ProjectQuery.DeviceList(project).Single(), Path = path };
    }
    internal static void ReplaceApplicationCode(string path)
        => Rewrite(path, entry => entry.Contains("_A-", StringComparison.Ordinal) && entry.EndsWith(".xml", StringComparison.Ordinal), document =>
        {
            var data = document.Descendants().Single(e => e.Name.LocalName == "AbsoluteSegment" && (string?)e.Attribute("Id") == "PARAM").Elements().Single(e => e.Name.LocalName == "Data");
            var bytes = Convert.FromBase64String(data.Value); bytes[^1] ^= 0x5A; data.Value = Convert.ToBase64String(bytes);
        });
    private static void Rewrite(string path, Func<string, bool> select, Action<XDocument> edit)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.Entries.Single(e => select(e.FullName));
        XDocument document; using (var input = entry.Open()) document = XDocument.Load(input, LoadOptions.PreserveWhitespace);
        edit(document);
        var name = entry.FullName; entry.Delete(); using var output = zip.CreateEntry(name).Open(); document.Save(output);
    }
    [STAThread]
    internal static int RunNative()
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-queue-archive-native-" + Guid.NewGuid().ToString("N"));
        var fixture = Create(root); var failures = 0; var providers = 0;
        Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
        typeof(CatalogLibrary).GetField("_store", Private)!.SetValue(CatalogLibrary.Shared, new CatalogStore(Path.Combine(root, "shared-catalog")));
        App.ForcedLanguage = "en"; App.ForcedTheme = "light";
        App.TestWindowFactory = () =>
        {
            var catalog = new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog"))); var vm = new MainWindowViewModel(catalog); vm.CreateProject(fixture.Project, fixture.Path);
            return new WorkbenchWindow(vm, new(), Path.Combine(root, "layout.json"), catalog, new ProgrammingStateStore(Path.Combine(root, "programming")),
                credentialVaultOwner: new WorkbenchCredentialVaultOwner(() => { providers++; throw new Exception("No provider in source binding gate test"); }));
        };
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().AfterSetup(_ => DispatcherTimer.RunOnce(async () =>
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!; desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var shell = (WorkbenchWindow)desktop.MainWindow!; var refused = false; Task? opening = null;
            try
            {
                ReplaceApplicationCode(fixture.Path);
                typeof(WorkbenchWindow).GetMethod("EnsureSessions", Private)!.Invoke(shell, null);
                var sessions = (WorkbenchSessions)typeof(WorkbenchWindow).GetField("_sessions", Private)!.GetValue(shell)!;
                var projected = Knx.Workbench.DeviceProgrammingState.Empty(fixture.Device.Id).ApplySuccess(
                    Knx.Workbench.DeviceLoadFlags.ApplicationProgram | Knx.Workbench.DeviceLoadFlags.Parameters | Knx.Workbench.DeviceLoadFlags.GroupAddresses,
                    DeviceConfigurationSnapshot.Freeze(fixture.Project, fixture.Device).Signature, DateTimeOffset.UtcNow, "synthetic-history", Knx.Workbench.ProgrammingOrigin.Real);
                typeof(ProgrammingSession).GetMethod("ImportVerifiedM112History", Private)!.Invoke(sessions.Programming, new object[] { new[] { projected } });
                Console.WriteLine((shell.ViewModel.IsDirty ? "FAIL " : "PASS ") + "QueueArchiveNative unchanged in-memory target remains clean");
                if (shell.ViewModel.IsDirty) failures++;
                opening = (Task)typeof(WorkbenchWindow).GetMethod("ShowRealProgrammingAsync", Private)!.Invoke(shell, null)!;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (!opening.IsCompleted && !desktop.Windows.OfType<RealProgrammingQueueWindow>().Any()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
                foreach (var queue in desktop.Windows.OfType<RealProgrammingQueueWindow>().ToArray()) { queue.Close(); await queue.ClosedCompletion; }
                try { await opening; } catch (IOException) { refused = true; }
                Console.WriteLine((refused ? "PASS " : "FAIL ") + "QueueArchiveNative real queue refuses model A against same-path archive B before showing job controls");
                if (!refused) failures++;
                Console.WriteLine((sessions.Programming.DeviceStates.Count == 0 ? "PASS " : "FAIL ") + "QueueArchiveNative source refusal withdraws a prior unchanged receipt projection");
                if (sessions.Programming.DeviceStates.Count != 0) failures++;
                Console.WriteLine((providers == 0 && !KnxBus.WritesEnabled ? "PASS " : "FAIL ") + "QueueArchiveNative source refusal has zero provider or write authority");
                if (providers != 0 || KnxBus.WritesEnabled) failures++;
                Knxproj.Save(fixture.Project, fixture.Path, "synthetic-project-password-only");
                opening = (Task)typeof(WorkbenchWindow).GetMethod("ShowRealProgrammingAsync", Private)!.Invoke(shell, null)!;
                while (!desktop.Windows.Any(w => w != shell)) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(10, timeout.Token); }
                var passwordPrompt = desktop.Windows.Single(w => w != shell);
                shell.RequestShutdown(); shell.Close();
                var joined = shell.ConnectionCloseCompletion;
                var drained = await Task.WhenAny(joined, Task.Delay(1000)) == joined;
                Console.WriteLine((drained && !passwordPrompt.IsVisible ? "PASS " : "FAIL ") + "QueueArchiveNative owner close cancels and joins pending source password prompt");
                if (!drained || passwordPrompt.IsVisible) failures++;
                // Red-case cleanup is explicit and limited to this test's own prompt.
                if (passwordPrompt.IsVisible) passwordPrompt.Close();
                try { await opening; } catch (OperationCanceledException) { }
                await joined;
            }
            catch (Exception error) { Console.WriteLine(error); failures++; }
            finally
            {
                foreach (var queue in desktop.Windows.OfType<RealProgrammingQueueWindow>().ToArray()) { queue.Close(); await queue.ClosedCompletion; }
                shell.RequestShutdown(); shell.Close(); await shell.ConnectionCloseCompletion;
                Console.WriteLine("QUEUE ARCHIVE NATIVE failures=" + failures); desktop.Shutdown(failures);
            }
        }, TimeSpan.FromMilliseconds(200))).StartWithClassicDesktopLifetime([]);
        return Environment.ExitCode;
    }
}

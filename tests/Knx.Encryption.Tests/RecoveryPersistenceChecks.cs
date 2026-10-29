using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Knx;
using Knx.Catalog;
using Knx.Recovery;
using Knx.Workbench;

// Target: Knx.Encryption.Tests, an explicitly declared Core friend assembly.
// Offline synthetic recovery, archive-boundary and private-filesystem checks.
internal static class RecoveryPersistenceChecks
{
    public static void Run(Action<string, bool> check) => RunAsync(check).GetAwaiter().GetResult();
    private static async Task RunAsync(Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-recovery-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Project NewProject() => new() { Id = "P-1", Name = "Synthetic recovery", Guid = "11111111-2222-3333-4444-555555555555" };
        string Session() => Guid.NewGuid().ToString("N");
        RecoverySource Unsaved(Project project, string session) => new(RecoveryBindingKind.UnsavedProject, project.Id, project.Guid, null, null, null, session);
        RecoverySource Saved(Project project, string path)
        {
            var bytes = File.ReadAllBytes(path); return new(RecoveryBindingKind.SavedArchive, project.Id, project.Guid, Path.GetFullPath(path),
                RecoveryContractValidation.PathIdentity(path), new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length), null);
        }
        async Task Case(string name, Func<Task<bool>> run)
        {
            bool ok;
            try { ok = await run(); }
            catch (Exception error) { Console.WriteLine(error.GetType().Name + ": " + error.Message); ok = false; }
            check("recovery: " + name, ok);
        }
        try
        {
            await Case("ordinary Undo records fact-only changes above recovery metadata limits while export refuses", async () =>
            {
                var project = NewProject(); var largeName = new string('x', 65536);
                for (var index = 0; index < 513; index++) project.Hardware["hardware-" + index] = new() { HardwareId = "hardware-" + index, Name = largeName };
                var undo = new UndoStack(2); undo.ResetUnsaved(project);
                project.Hardware["hardware-0"] = project.Hardware["hardware-0"] with { Name = "Edited" }; undo.Push(project);
                var previous = undo.Undo(); var next = undo.Redo();
                undo.Push(next!); // A genuine no-op remains a no-op even above checkpoint limits.
                var session = Session(); var refused = false;
                try { undo.ExportRecovery(next!, session, Unsaved(next!, session)); }
                catch (RecoveryRejectedException error) { refused = error.Failure == RecoveryFailure.LimitExceeded; }
                await Task.CompletedTask;
                return previous?.Hardware["hardware-0"].Name == largeName && next?.Hardware["hardware-0"].Name == "Edited"
                    && undo.Steps == 1 && undo.GetRecoveryFingerprint().Length == 64 && refused;
            });
            await Case("failing recorder and failing fallback cannot throw after a snapshot commit", async () =>
            {
                var project = ProjectWizard.Create(new() { TopologyTemplate = NewProjectTopologyTemplate.TpFromOne }); project.Name = "Before";
                project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "device", ProductRefId = "DEMO-SYNTHETIC", Address = new(1, 1, 1), Name = "Device" });
                var calls = 0; var undo = new UndoStack();
                undo.SetRecoveryDependencyRecorder(current =>
                {
                    calls++; current.Installations.Add(null!); // Inject a secondary inventory-enumeration failure AFTER snapshot capture.
                    throw new InvalidOperationException("Synthetic recorder failure");
                });
                undo.ResetUnsaved(project); project.Installations.RemoveAll(installation => installation is null);
                project.Name = "After"; undo.Push(project); project.Installations.RemoveAll(installation => installation is null);
                project = undo.Undo()!; var session = Session(); var source = Unsaved(project, session);
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "failed-recorder"));
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source));
                var candidate = await store.LoadCandidateAsync(checkpoint.Id, source);
                return calls == 2 && project.Name == "Before" && undo.CanRedo && candidate.Notices.Contains("Recovery.ModelOnlyDeclarations");
            });
            await Case("unsaved restart keeps Undo/Redo, dirty marker and Save-As obligation", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var binding = Unsaved(project, session);
                project.Name = "One"; undo.Push(project); project.Name = "Two"; undo.Push(project); project = undo.Undo()!;
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "unsaved")); var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var reopened = new WorkbenchRecoveryStore(Path.Combine(root, "unsaved")); var item = (await reopened.ListAsync()).Items.Single();
                var restartBinding = new RecoverySource(item.BindingKind!.Value, item.ProjectId!, item.ProjectGuid, null, null, null, item.SessionId);
                var candidate = await reopened.LoadCandidateAsync(item.Id, restartBinding);
                var restored = candidate.Take(); var redo = restored.Undo.Redo(); var back = restored.Undo.Undo();
                return restored.Project.Name == "One" && restored.Undo.IsDirty && redo?.Name == "Two" && back?.Name == "One"
                    && candidate.Notices.Contains("Recovery.RequiresSaveAs") && restored.Source.FullPath is null && restartBinding == binding;
            });
            await Case("saved index survives restore and returning to the saved step clears dirty", async () =>
            {
                var project = NewProject(); var path = Path.Combine(root, "saved.knxproj"); Knxproj.Save(project, path); project = Knxproj.Load(path);
                var binding = Saved(project, path); var undo = new UndoStack(); undo.Reset(project); var session = Session();
                project.Name = "Edited"; undo.Push(project);
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "saved")); var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var restored = (await store.LoadCandidateAsync(checkpoint.Id, binding)).Take();
                return restored.Undo.IsDirty && restored.Undo.Undo()?.Name == "Synthetic recovery" && !restored.Undo.IsDirty && !restored.Undo.CanUndo;
            });
            await Case("new branch discards Redo while independent audit keeps every accepted event", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var binding = Unsaved(project, session);
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "audit")); var before = undo.GetRecoveryFingerprint();
                project.Name = "One"; undo.Push(project); var one = undo.GetRecoveryFingerprint();
                var edit = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "Edit one", before, one);
                await store.SaveAsync(undo.ExportRecovery(project, session, binding), [edit]);
                project.Name = "Two"; undo.Push(project); var two = undo.GetRecoveryFingerprint();
                var second = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "Edit two", one, two);
                await store.SaveAsync(undo.ExportRecovery(project, session, binding), [edit, second]);
                project = undo.Undo()!;
                var undone = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Undo, "Undo two", two, one);
                project.Name = "New branch"; undo.Push(project);
                var branch = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "New branch", one, undo.GetRecoveryFingerprint());
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding), [undone, branch]);
                var restored = (await store.LoadCandidateAsync(checkpoint.Id, binding)).Take();
                return checkpoint.Audit.Sequence == 4 && !restored.Undo.CanRedo && restored.Project.Name == "New branch";
            });
            await Case("changed or missing saved source refuses without rewriting the project", async () =>
            {
                var project = NewProject(); var path = Path.Combine(root, "conflict.knxproj"); Knxproj.Save(project, path); project = Knxproj.Load(path);
                var binding = Saved(project, path); var undo = new UndoStack(); undo.Reset(project); var session = Session();
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "conflict")); var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                project.Name = "External save"; Knxproj.Save(project, path); var changed = File.ReadAllBytes(path);
                var refused = await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding), RecoveryFailure.SourceChanged);
                var fingerprint = undo.GetRecoveryFingerprint();
                var auditRefused = await Rejected(() => store.AppendAuditAsync(undo.ExportRecoveryAuditContext(project, session, binding),
                    [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Save, "Changed-source audit must not persist", fingerprint, fingerprint)]), RecoveryFailure.SourceChanged);
                if (!refused || !auditRefused || !File.ReadAllBytes(path).SequenceEqual(changed)) return false;
                File.Delete(path); return await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding), RecoveryFailure.SourceMissing);
            });
            await Case("older protected history refuses before blobs and only minimal omission is listed", async () =>
            {
                var project = NewProject(); project.ArchiveState = new(Zip(new()), null, Encrypted: true);
                var undo = new UndoStack(); undo.ResetUnsaved(project); project.ArchiveState = null; project.Name = "Current plain"; undo.Push(project);
                var session = Session(); var directory = Path.Combine(root, "protected"); var store = new WorkbenchRecoveryStore(directory);
                var protectedFailure = false;
                try { undo.ExportRecovery(project, session, Unsaved(project, session)); }
                catch (RecoveryRejectedException error) { protectedFailure = error.Failure == RecoveryFailure.ProtectedSource; }
                var auditProtectedFailure = false;
                try { undo.ExportRecoveryAuditContext(project, session, Unsaved(project, session)); }
                catch (RecoveryRejectedException error) { auditProtectedFailure = error.Failure == RecoveryFailure.ProtectedSource; }
                await store.RecordProtectedOmissionAsync(session);
                var list = await store.ListAsync();
                return protectedFailure && auditProtectedFailure && Directory.GetFiles(Path.Combine(directory, "blobs")).Length == 0
                    && list.Items is [{ Status: RecoveryListingStatus.ProtectedSource, ProjectName: null, ProjectId: null, Dirty: null, BindingKind: null, ProjectGuid: null }];
            });
            await Case("raw ZIP encryption flags cannot be hidden by an unprotected state flag", () =>
            {
                var project = NewProject(); var bytes = Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString()) });
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) | 1));
                project.ArchiveState = new(bytes, null, false); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session();
                try { undo.ExportRecovery(project, session, Unsaved(project, session)); return Task.FromResult(false); }
                catch (RecoveryRejectedException error) { return Task.FromResult(error.Failure == RecoveryFailure.ProtectedSource); }
            });
            await Case("sixty snapshots share one exact source archive blob", async () =>
            {
                var project = NewProject(); var source = Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString()), ["opaque.data"] = new byte[128 * 1024] });
                var path = Path.Combine(root, "source.knxproj"); File.WriteAllBytes(path, source); project = Knxproj.Load(path);
                var undo = new UndoStack(); undo.ResetUnsaved(project);
                for (var index = 1; index < 60; index++) { project.Name = "Step " + index; undo.Push(project); }
                var session = Session(); var directory = Path.Combine(root, "dedupe"); var store = new WorkbenchRecoveryStore(directory);
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, Unsaved(project, session)));
                var sourceBlob = Path.Combine(directory, "blobs", Convert.ToHexString(SHA256.HashData(source)) + ".blob");
                var files = Directory.GetFiles(Path.Combine(directory, "blobs"));
                return File.ReadAllBytes(sourceBlob).SequenceEqual(source) && files.Sum(file => new FileInfo(file).Length) < source.Length + 512 * 1024
                    && (await store.LoadCandidateAsync(checkpoint.Id, Unsaved(project, session))).Take().Undo.Steps == 59;
            });
            await Case("overlay-only, hardware and text bindings form genuine Undo snapshots", async () =>
            {
                var project = NewProject(); var path = Path.Combine(root, "overlays.knxproj"); Knxproj.Save(project, path); project = Knxproj.Load(path);
                project.ArchiveState = ArchiveState.WithManufacturerParts(project.ArchiveState, new Dictionary<string, byte[]> { ["M-0001/one.data"] = [1] });
                project.Hardware["hardware"] = new() { HardwareId = "hardware", Name = "One", BusCurrent = 10, MediumTypeIds = ["MT-0"] };
                project.ObjectTextDefinitions[new("device", "object")] = new("Text", "Function", "Reference", "Default", true,
                    new(new Dictionary<string, string> { ["named"] = "literal" }, true, "argument"), new("channel", "Name", "declared", "native"));
                var undo = new UndoStack(); undo.ResetUnsaved(project);
                project.ArchiveState = ArchiveState.WithManufacturerParts(project.ArchiveState, new Dictionary<string, byte[]> { ["M-0001/two.data"] = [2] });
                project.Hardware["hardware"] = project.Hardware["hardware"] with { Name = "Two" }; undo.Push(project);
                var session = Session(); var store = new WorkbenchRecoveryStore(Path.Combine(root, "overlays"));
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, Unsaved(project, session)));
                var restored = (await store.LoadCandidateAsync(checkpoint.Id, Unsaved(project, session))).Take();
                var previous = restored.Undo.Undo();
                return previous?.Hardware["hardware"].Name == "One" && restored.Project.Hardware["hardware"].Name == "Two"
                    && previous.ArchiveState!.GeneratedParts!.ContainsKey("M-0001/one.data") && !previous.ArchiveState.GeneratedParts.ContainsKey("M-0001/two.data")
                    && restored.Project.ObjectTextDefinitions[new("device", "object")].Arguments!.Named["named"] == "literal"
                    && restored.Project.ObjectTextDefinitions[new("device", "object")].Channel!.NativeChannelId == "native";
            });
            await Case("compound values, binary identities and complete Workbench facts survive", async () =>
            {
                var project = ProjectWizard.Create(new() { TopologyTemplate = NewProjectTopologyTemplate.TpFromOne });
                var line = project.Installations[0].Areas[0].Lines[0];
                var device = new DeviceInstance { Id = "device", ProductRefId = "DEMO-SYNTHETIC", Address = new(1, 1, 1), Name = "Device" };
                line.Devices.Add(device); device.ParameterValues["compound"] = "literal:alpha|beta={0033}";
                device.BinaryData["payload"] = [0, 1, 0, 255]; device.BinaryDataInfo["payload"] = new("binary-id", null, true, true); device.BinaryDataCopyUnsafe = true;
                device.Channels.Add(new() { Id = "channel", RefId = "CH-1", Name = "Channel" });
                device.ComObjectRefs.Add(new() { Id = "object", ComObjectId = "CO-1", Number = 7, ChannelId = "channel" });
                project.Workbench.Devices[device.Id] = new() { Demo = true, SecureCapable = false, Application = "synthetic", Revision = 9, DownloadedRevision = 4 };
                var key = new ObjectKey(device.Id, "object").ToString();
                project.Workbench.Objects[key] = new() { Dpt = "DPST-1-1", Bits = 1, Flags = "CT", Active = false,
                    StructuralPreviousActive = true, StructuralDeclaredNumber = 7, StructuralDeclaredChannelId = "declared", StructuralDeclaredChannelName = "Derived",
                    ChannelId = "manual", ChannelSource = ObjectChannelSource.Manual, Role = "Role" };
                project.Workbench.FloorShortNames["floor"] = "A"; ProjectRevisions.Capture(project, description: "Synthetic revision");
                var undo = new UndoStack(); undo.ResetUnsaved(project);
                project.Name = "Compound edited"; undo.Push(project); project = undo.Undo()!;
                var session = Session(); var binding = Unsaved(project, session);
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "compound")); var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var candidate = await store.LoadCandidateAsync(checkpoint.Id, binding); var restored = candidate.Take(); var after = ProjectQuery.DeviceList(restored.Project).Single();
                var facts = restored.Project.Workbench.Objects[key];
                var intact = after.ParameterValues["compound"] == device.ParameterValues["compound"] && after.BinaryData["payload"].SequenceEqual(device.BinaryData["payload"])
                    && after.BinaryDataInfo["payload"].Id == "binary-id" && after.BinaryDataInfo["payload"].DoNotCopy && after.BinaryDataCopyUnsafe
                    && after.ComObjectRefs.Single().ChannelId == "channel" && facts is { Active: false, StructuralPreviousActive: true, StructuralDeclaredNumber: 7,
                        StructuralDeclaredChannelId: "declared", ChannelId: "manual", ChannelSource: ObjectChannelSource.Manual }
                    && restored.Project.Workbench.Devices[device.Id].Revision == 9 && restored.Project.Workbench.Devices[device.Id].DownloadedRevision == 4
                    && restored.Project.Workbench.FloorShortNames["floor"] == "A" && restored.Project.Workbench.Revisions.Count == 1
                    && candidate.Notices.Contains("Recovery.ModelOnlyDeclarations");
                var redone = restored.Undo.Redo(); var undone = restored.Undo.Undo();
                return intact && redone?.Workbench.Objects[key].StructuralDeclaredChannelId == "declared"
                    && undone?.Workbench.Objects[key].StructuralDeclaredChannelName == "Derived";
            });
            await Case("native directory layout and opaque retained sidecar bytes remain exact", async () =>
            {
                const string guid = "11111111-2222-3333-4444-555555555555";
                var data = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\"><Installations><Installation InstallationId=\"I-1\"><Topology /></Installation></Installations></Project></KNX>";
                var information = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\"><ProjectInformation Name=\"Native\" Guid=\"" + guid + "\" /></Project></KNX>";
                var retained = "<Project xmlns=\"urn:buschord:project:1\" Version=\"2\" Id=\"P-1\"><RetiredModule DeviceId=\"old-device\" Historical=\"true\" ApplicationId=\"M-0001_A-old\"><Opaque xmlns=\"urn:synthetic\" Value=\"retained\" /></RetiredModule></Project>";
                var source = Zip(new() { ["P-1/0.xml"] = Encoding.UTF8.GetBytes(data), ["P-1/project.xml"] = Encoding.UTF8.GetBytes(information),
                    ["P-1/BusChord/project.xml"] = Encoding.UTF8.GetBytes(retained), ["opaque/vendor.bin"] = [0, 255, 1] });
                var path = Path.Combine(root, "native.knxproj"); File.WriteAllBytes(path, source); var project = Knxproj.Load(path);
                var retainedKey = new ObjectKey("historical-device", "historical-object").ToString();
                project.Workbench.Objects[retainedKey] = new() { Active = false, StructuralPreviousActive = true,
                    StructuralDeclaredChannelId = "declared-retained", StructuralDeclaredChannelName = "Retained channel", ChannelId = "manual-retained",
                    ChannelSource = ObjectChannelSource.Manual, SendingGroupId = "historical-ga",
                    Connectors = new() { ["historical-ga"] = new() { Send = true, Acknowledge = true, Priority = 2, Slot = 3 } } };
                project.Workbench.FloorShortNames["historical-floor"] = "RET";
                var undo = new UndoStack(); undo.ResetUnsaved(project); project.Name = "Native edited"; undo.Push(project);
                var session = Session(); var binding = Unsaved(project, session); var store = new WorkbenchRecoveryStore(Path.Combine(root, "native"));
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var restored = (await store.LoadCandidateAsync(checkpoint.Id, binding)).Take();
                var previous = restored.Undo.Undo();
                return restored.Project.ArchiveState!.Layout!.Prefix == "P-1/" && restored.Project.ArchiveState.Bytes.SequenceEqual(source)
                    && restored.Project.Name == "Native edited" && previous?.Name == "Native"
                    && previous.Workbench.Objects[retainedKey].StructuralDeclaredChannelId == "declared-retained"
                    && restored.Project.Workbench.Objects[retainedKey].Connectors["historical-ga"] is { Send: true, Acknowledge: true, Priority: 2, Slot: 3 }
                    && previous.Workbench.FloorShortNames["historical-floor"] == "RET";
            });
            await Case("external requirements are exact and empty projects need no catalogue", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var binding = Unsaved(project, session);
                var program = "<KNX><ApplicationProgram Id=\"M-0001_A-test\" /></KNX>"u8.ToArray();
                var requirement = new RecoveryDependency("M-0001_A-test", new(Convert.ToHexString(SHA256.HashData(program)), program.Length),
                    RecoveryDependencyKind.ExternalCatalogue, "synthetic-catalogue", "1", [], "Recorded synthetic declaration only");
                undo.SetRecoveryDependencyRecorder(_ => new RecoveryDependencyInventory([requirement], [], RecoveryDependencyCoverage.RecordedRequirements));
                undo.ResetUnsaved(project);
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "dependencies"));
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var missing = await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding), RecoveryFailure.CatalogMissing);
                var wrong = await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding, new Resolver([1, 2, 3])), RecoveryFailure.DependencyChanged);
                var accepted = await store.LoadCandidateAsync(checkpoint.Id, binding, new Resolver(program));
                return missing && wrong && accepted.Take().Project.Name == project.Name;
            });
            await Case("corrupt blob, unknown manifest schema and incomplete writes preserve old head", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var binding = Unsaved(project, session);
                var directory = Path.Combine(root, "corrupt"); var store = new WorkbenchRecoveryStore(directory);
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, binding));
                var head = File.ReadAllBytes(Path.Combine(directory, "heads", session + ".json"));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                var stopped = false; try { await store.SaveAsync(undo.ExportRecovery(project, session, binding), cancellationToken: cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                if (!stopped || !File.ReadAllBytes(Path.Combine(directory, "heads", session + ".json")).SequenceEqual(head)) return false;
                var blob = Directory.GetFiles(Path.Combine(directory, "blobs")).First(); File.AppendAllText(blob, "damage");
                var rejected = await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding), RecoveryFailure.Corrupt);
                var manifest = Path.Combine(directory, "manifests", checkpoint.Id + ".json"); File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"Schema\":1", "\"Schema\":2", StringComparison.Ordinal));
                var version = await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, binding), RecoveryFailure.VersionMismatch);
                return rejected && version;
            });
            await Case("native source information only fills a missing project GUID", async () =>
            {
                var project = NewProject();
                var data = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\" Guid=\"" + project.Guid
                    + "\"><Installations><Installation InstallationId=\"I-1\"><Topology /></Installation></Installations></Project></KNX>";
                var information = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\"><ProjectInformation Name=\"Native\" Guid=\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\" /></Project></KNX>";
                var path = Path.Combine(root, "guid.knxproj");
                File.WriteAllBytes(path, Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(data), ["project.xml"] = Encoding.UTF8.GetBytes(information) }));
                project = Knxproj.Load(path); var source = Saved(project, path); var undo = new UndoStack(); undo.Reset(project); var session = Session();
                var store = new WorkbenchRecoveryStore(Path.Combine(root, "guid"));
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source));
                return (await store.LoadCandidateAsync(checkpoint.Id, source)).Take().Project.Guid == "11111111-2222-3333-4444-555555555555";
            });
            await Case("missing immutable blob refuses with a stable corruption reason", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var source = Unsaved(project, session);
                var directory = Path.Combine(root, "missing"); var store = new WorkbenchRecoveryStore(directory);
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source));
                foreach (var blob in Directory.GetFiles(Path.Combine(directory, "blobs"))) File.Delete(blob);
                return await Rejected(() => store.LoadCandidateAsync(checkpoint.Id, source), RecoveryFailure.Corrupt);
            });
            await Case("uncommitted torn audit tail preserves the old checkpoint and refuses append", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var source = Unsaved(project, session);
                var directory = Path.Combine(root, "torn-audit"); var store = new WorkbenchRecoveryStore(directory);
                var fingerprint = undo.GetRecoveryFingerprint();
                var audit = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Restore, "Synthetic initial checkpoint", fingerprint, fingerprint);
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source), [audit]);
                var headPath = Path.Combine(directory, "heads", session + ".json"); var oldHead = File.ReadAllBytes(headPath);
                using (var append = new FileStream(Path.Combine(directory, "audit", session + ".log"), FileMode.Append, FileAccess.Write)) append.WriteByte(0xAA);
                var candidate = await store.LoadCandidateAsync(checkpoint.Id, source);
                var refused = await Rejected(() => store.SaveAsync(undo.ExportRecovery(project, session, source)), RecoveryFailure.Corrupt);
                var appendRefused = await Rejected(() => store.AppendAuditAsync(undo.ExportRecoveryAuditContext(project, session, source), [audit]), RecoveryFailure.Corrupt);
                return refused && appendRefused && candidate.Notices.Contains("Recovery.AuditUnverifiedSuffix")
                    && File.ReadAllBytes(headPath).SequenceEqual(oldHead) && candidate.Take().Project.Name == project.Name;
            });
            await Case("external declared content shares the unique-byte checkpoint budget", async () =>
            {
                var project = NewProject(); var undo = new UndoStack();
                var requirements = Enumerable.Range(0, 20).Select(index => new RecoveryDependency("M-0001_A-budget-" + index,
                    new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic-unresolved-" + index))), 128L * 1024 * 1024),
                    RecoveryDependencyKind.ExternalCatalogue, "synthetic-catalogue", "1", [], "Unresolved synthetic budget descriptor")).ToArray();
                undo.SetRecoveryDependencyRecorder(_ => new(requirements, [], RecoveryDependencyCoverage.RecordedRequirements));
                undo.ResetUnsaved(project); var session = Session(); var store = new WorkbenchRecoveryStore(Path.Combine(root, "budget"));
                return await Rejected(() => store.SaveAsync(undo.ExportRecovery(project, session, Unsaved(project, session))), RecoveryFailure.LimitExceeded);
            });
            await Case("independent audit append is idempotent and cancellation never rewrites snapshots or head", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var source = Unsaved(project, session);
                var directory = Path.Combine(root, "independent-audit"); var store = new WorkbenchRecoveryStore(directory);
                var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source));
                var head = File.ReadAllBytes(Path.Combine(directory, "heads", session + ".json"));
                var blobs = Directory.GetFiles(Path.Combine(directory, "blobs")).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
                var fingerprint = undo.GetRecoveryFingerprint(); var context = undo.ExportRecoveryAuditContext(project, session, source);
                var draft = new RecoveryAuditDraft(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "Committed action only", fingerprint, fingerprint);
                var first = await store.AppendAuditAsync(context, [draft]); var repeated = await store.AppendAuditAsync(context, [draft]);
                using var cancel = new CancellationTokenSource(); cancel.Cancel(); var stopped = false;
                try { await store.AppendAuditAsync(context, [draft], cancel.Token); } catch (OperationCanceledException) { stopped = true; }
                var candidate = await store.LoadCandidateAsync(checkpoint.Id, source);
                var after = Directory.GetFiles(Path.Combine(directory, "blobs")).Order(StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
                return first == repeated && first.Sequence == 1 && stopped && candidate.Notices.Contains("Recovery.AuditAheadOfCheckpoint")
                    && File.ReadAllBytes(Path.Combine(directory, "heads", session + ".json")).SequenceEqual(head)
                    && Directory.GetFiles(Path.Combine(directory, "manifests")).Length == 1 && blobs.Length == after.Length
                    && blobs.Zip(after).All(pair => pair.First.SequenceEqual(pair.Second));
            });
            await Case("independent audit cannot bypass raw ZIP protection and persists no event text", async () =>
            {
                var project = NewProject(); var bytes = Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString()) });
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(6)) | 1));
                project.ArchiveState = new(bytes, null, false); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session();
                var directory = Path.Combine(root, "protected-audit"); var store = new WorkbenchRecoveryStore(directory);
                var context = undo.ExportRecoveryAuditContext(project, session, Unsaved(project, session)); var fingerprint = undo.GetRecoveryFingerprint();
                const string sentinel = "SYNTHETIC-TEXT-MUST-NOT-PERSIST";
                var refused = await Rejected(() => store.AppendAuditAsync(context,
                    [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, sentinel, fingerprint, fingerprint)]), RecoveryFailure.ProtectedSource);
                return refused && Directory.GetFiles(Path.Combine(directory, "audit")).Length == 0
                    && Directory.GetFiles(directory, "*", SearchOption.AllDirectories).All(path => !File.ReadAllText(path).Contains(sentinel, StringComparison.Ordinal));
            });
            await Case("independent audit journal-byte limit preserves the existing checkpoint head", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var source = Unsaved(project, session);
                var directory = Path.Combine(root, "audit-limit"); var store = new WorkbenchRecoveryStore(directory);
                await store.SaveAsync(undo.ExportRecovery(project, session, source)); var headPath = Path.Combine(directory, "heads", session + ".json");
                var before = File.ReadAllBytes(headPath);
                using (var oversized = new FileStream(Path.Combine(directory, "audit", session + ".log"), FileMode.Open, FileAccess.Write)) oversized.SetLength(64L * 1024 * 1024 + 1);
                var fingerprint = undo.GetRecoveryFingerprint();
                var refused = await Rejected(() => store.AppendAuditAsync(undo.ExportRecoveryAuditContext(project, session, source),
                    [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "Budget must refuse", fingerprint, fingerprint)]), RecoveryFailure.LimitExceeded);
                return refused && File.ReadAllBytes(headPath).SequenceEqual(before);
            });
            await Case("audit source rebind preserves frozen history and rejects identity or binding downgrade", async () =>
            {
                var project = NewProject(); var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var unsaved = Unsaved(project, session);
                var context = undo.ExportRecoveryAuditContext(project, session, unsaved);
                var path = Path.Combine(root, "audit-rebind.knxproj"); Knxproj.Save(project, path); var saved = Saved(project, path);
                var rebound = context.WithSource(saved); var directory = Path.Combine(root, "audit-rebind"); var store = new WorkbenchRecoveryStore(directory);
                var fingerprint = undo.GetRecoveryFingerprint();
                await store.AppendAuditAsync(rebound, [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Save, "Successful save", fingerprint, fingerprint)]);
                var saveAsPath = Path.Combine(root, "audit-rebind-as.knxproj"); Knxproj.Save(project, saveAsPath); var savedAs = Saved(project, saveAsPath);
                var moved = rebound.WithSource(savedAs);
                var anchor = await store.AppendAuditAsync(moved, [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.SaveAs, "Successful save as", fingerprint, fingerprint)]);
                var mismatch = await Rejected(() => Task.FromResult(context.WithSource(saved with { ProjectGuid = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE" })), RecoveryFailure.ProjectIdentityMismatch);
                var changedId = await Rejected(() => Task.FromResult(context.WithSource(saved with { ProjectId = "P-other" })), RecoveryFailure.ProjectIdentityMismatch);
                var changedSession = await Rejected(() => Task.FromResult(context.WithSource(unsaved with { UnsavedSessionId = Session() })), RecoveryFailure.Corrupt);
                var downgrade = await Rejected(() => Task.FromResult(moved.WithSource(unsaved)), RecoveryFailure.SourceChanged);
                var raw = Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString()) });
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(6)) | 1));
                project.ArchiveState = new(raw, null, false); undo.ResetUnsaved(project);
                var historical = undo.ExportRecoveryAuditContext(project, session, unsaved);
                project.ArchiveState = null; undo.ResetUnsaved(project); // Later UI history must not erase the frozen transaction scope.
                var historicalRebound = historical.WithSource(savedAs);
                var before = File.ReadAllBytes(Path.Combine(directory, "audit", session + ".log"));
                var refused = await Rejected(() => store.AppendAuditAsync(historicalRebound,
                    [new(Session(), DateTimeOffset.UtcNow, RecoveryOperationKind.Edit, "Protected history must not append", fingerprint, fingerprint)]), RecoveryFailure.ProtectedSource);
                return anchor.Sequence == 2 && context.Source == unsaved && ReferenceEquals(context.Archives, rebound.Archives)
                    && ReferenceEquals(historical.Archives, historicalRebound.Archives) && mismatch && changedId && changedSession && downgrade && refused
                    && File.ReadAllBytes(Path.Combine(directory, "audit", session + ".log")).SequenceEqual(before);
            });
            await Case("selected-source probe binds exact identity and bytes across supported archive layouts", async () =>
            {
                var project = NewProject();
                var native = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\" Guid=\"" + project.Guid + "\"><Installations /></Project></KNX>";
                var information = "<KNX xmlns=\"http://knx.org/xml/project/22\"><Project Id=\"P-1\"><ProjectInformation Guid=\"AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE\" /></Project></KNX>";
                var inner = Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(native), ["project.xml"] = Encoding.UTF8.GetBytes(information) });
                byte[][] sources = [
                    Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(Knxproj.ToXml(project).ToString()), ["M-0001/M-0001_A-opaque.xml"] = [255, 0, 1] }),
                    Zip(new() { ["P-1/0.xml"] = Encoding.UTF8.GetBytes(native), ["P-1/project.xml"] = Encoding.UTF8.GetBytes(information) }),
                    Zip(new() { ["P-1.zip"] = inner, ["project.xml"] = Encoding.UTF8.GetBytes(information) })
                ];
                for (var index = 0; index < sources.Length; index++)
                {
                    var path = Path.Combine(root, "probe-layout-" + index + ".knxproj"); File.WriteAllBytes(path, sources[index]);
                    var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
                    var result = await RecoverySourceProbe.ReadSelectedAsync(path);
                    if (result != Saved(project, path) || result.ProjectGuid != project.Guid || result.ProjectId != "P-1"
                        || !OperatingSystem.IsWindows() && !HasUnixMode(path, mode)) return false;
                }
                return true;
            });
            await Case("selected-source protection is classified before malformed primary XML is parsed", async () =>
            {
                var inner = Zip(new() { ["0.xml"] = "<invalid"u8.ToArray() });
                BinaryPrimitives.WriteUInt16LittleEndian(inner.AsSpan(6), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(6)) | 1));
                var before = Directory.GetDirectories(root).Length;
                var direct = Path.Combine(root, "probe-protected.knxproj"); File.WriteAllBytes(direct, inner);
                var nested = Path.Combine(root, "probe-protected-nested.knxproj"); File.WriteAllBytes(nested, Zip(new() { ["P-1.zip"] = inner }));
                return await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(direct), RecoveryFailure.ProtectedSource)
                    && await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(nested), RecoveryFailure.ProtectedSource)
                    && Directory.GetDirectories(root).Length == before;
            });
            await Case("selected-source probe bounds and cancellation reject without project or store writes", async () =>
            {
                var missing = Path.Combine(root, "probe-missing.knxproj"); using var cancel = new CancellationTokenSource(); cancel.Cancel(); var stopped = false;
                try { await RecoverySourceProbe.ReadSelectedAsync(missing, cancel.Token); } catch (OperationCanceledException) { stopped = true; }
                var missingRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(missing), RecoveryFailure.SourceMissing);
                var relativeRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync("relative.knxproj"), RecoveryFailure.Corrupt);
                var empty = Path.Combine(root, "probe-empty.knxproj"); File.WriteAllBytes(empty, []);
                var emptyRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(empty), RecoveryFailure.Corrupt);
                var large = Path.Combine(root, "probe-large.knxproj"); using (var stream = new FileStream(large, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(128L * 1024 * 1024 + 1);
                var sizeRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(large), RecoveryFailure.LimitExceeded);
                var xml = "<KNX xmlns=\"http://knx.org/xml/project/23\"><Project Id=\"P-1\">" + string.Concat(Enumerable.Repeat("<Nested>", 130))
                    + string.Concat(Enumerable.Repeat("</Nested>", 130)) + "</Project></KNX>";
                var deep = Path.Combine(root, "probe-deep.knxproj"); File.WriteAllBytes(deep, Zip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(xml) }));
                var depthRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(deep), RecoveryFailure.LimitExceeded);
                var malformed = Path.Combine(root, "probe-invalid-guid.knxproj");
                File.WriteAllBytes(malformed, Zip(new() { ["0.xml"] = "<KNX xmlns=\"http://knx.org/xml/project/23\"><Project Id=\"P-1\" Guid=\"not-a-guid\" /></KNX>"u8.ToArray() }));
                var identityRefused = await Rejected(() => RecoverySourceProbe.ReadSelectedAsync(malformed), RecoveryFailure.Corrupt);
                return stopped && missingRefused && relativeRefused && emptyRefused && sizeRefused && depthRefused && identityRefused
                    && !File.Exists(missing) && new FileInfo(large).Length == 128L * 1024 * 1024 + 1;
            });
            if (!OperatingSystem.IsWindows())
            {
                await Case("Unix store uses 0700 directories and 0600 files without user-name path components", async () =>
                {
                    var project = NewProject(); project.Name = "../../literal-user-project/name";
                    var undo = new UndoStack(); undo.ResetUnsaved(project); var session = Session(); var source = Unsaved(project, session);
                    var parent = Path.Combine(root, "unix-parent"); Directory.CreateDirectory(parent);
                    var parentMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                    File.SetUnixFileMode(parent, parentMode);
                    var directory = Path.Combine(parent, "store"); var store = new WorkbenchRecoveryStore(directory);
                    var fingerprint = undo.GetRecoveryFingerprint();
                    await store.SaveAsync(undo.ExportRecovery(project, session, source), [new(Session(), DateTimeOffset.UtcNow,
                        RecoveryOperationKind.Edit, "../../literal-description", fingerprint, fingerprint)]);
                    return PrivateModes(directory) && File.GetUnixFileMode(parent) == parentMode && (await store.ListAsync()).Items.Single().ProjectName == project.Name
                        && Directory.GetDirectories(directory).Length == 5;
                });
                await Case("Unix reopen repairs permissive store modes while source-file modes stay untouched", async () =>
                {
                    var project = NewProject(); var path = Path.Combine(root, "unix-source.knxproj"); Knxproj.Save(project, path); project = Knxproj.Load(path);
                    var sourceMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                    File.SetUnixFileMode(path, sourceMode);
                    var undo = new UndoStack(); undo.Reset(project); var session = Session(); var source = Saved(project, path);
                    var directory = Path.Combine(root, "unix-reopen"); var store = new WorkbenchRecoveryStore(directory);
                    var checkpoint = await store.SaveAsync(undo.ExportRecovery(project, session, source));
                    var permissive = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                    foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(file, permissive);
                    foreach (var folder in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(folder, permissive);
                    File.SetUnixFileMode(directory, permissive);
                    var reopened = new WorkbenchRecoveryStore(directory);
                    var restored = (await reopened.LoadCandidateAsync(checkpoint.Id, source)).Take();
                    return PrivateModes(directory) && File.GetUnixFileMode(path) == sourceMode && restored.Project.Name == project.Name;
                });
            }
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(resolved).StartsWith("buschord-recovery-checks-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
    private static async Task<bool> Rejected(Func<Task> operation, RecoveryFailure expected)
    { try { await operation(); return false; } catch (RecoveryRejectedException error) { return error.Failure == expected; } }
    private static bool PrivateModes(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;
        return File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)
            && Directory.GetDirectories(directory, "*", SearchOption.AllDirectories).All(path => HasUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            && Directory.GetFiles(directory, "*", SearchOption.AllDirectories).All(path => HasUnixMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite));
    }
    private static bool HasUnixMode(string path, UnixFileMode mode) => !OperatingSystem.IsWindows() && File.GetUnixFileMode(path) == mode;
    private static byte[] Zip(Dictionary<string, byte[]> entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true)) foreach (var (name, bytes) in entries)
        { using var target = zip.CreateEntry(name, CompressionLevel.NoCompression).Open(); target.Write(bytes); }
        return output.ToArray();
    }
    private sealed class Resolver(byte[] program) : IRecoveryDependencyResolver
    {
        public long Generation => 1;
        public ValueTask<Stream?> OpenProgramAsync(RecoveryDependency requirement, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(new MemoryStream(program, false));
        public ValueTask<Stream?> OpenResourceAsync(RecoveryDependency requirement, RecoveryPart resource, CancellationToken cancellationToken = default) => ValueTask.FromResult<Stream?>(null);
    }
}

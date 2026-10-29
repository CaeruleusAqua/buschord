using System.Collections.Concurrent;
using System.Diagnostics;
using Knx;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Recovery;

if (args.Contains("--native")) return RecoveryNativeChecks.Run(args.Contains("--de") ? "de" : "en");

var root = Path.Combine(Path.GetTempPath(), "buschord-recovery-app-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root); Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(root, "settings.json"));
var checks = 0;
void Check(string name, bool ok) { if (!ok) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
async Task<bool> Rejected(Func<Task> action) { try { await action(); return false; } catch (IOException) { return true; } catch (OperationCanceledException) { return true; } }
using var ui = new OwnedDispatcher();
var store = new WorkbenchRecoveryStore(Path.Combine(root, "recovery"));
var persistence = new ObservedPersistence(store, ui);
await using var recorder = new ProjectRecoveryRecorder(persistence);
var vm = await ui.InvokeAsync(() => new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog")))));
await ui.InvokeAsync(() => { vm.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null); return true; });
var privateDrafts = false; var programming = false;
var reader = new DelayedReader(new ProjectRecoveryReader(store));
var controller = await ui.InvokeAsync(() => new ProjectRecoveryController(vm, recorder, reader, ui, () => privateDrafts, () => programming));
var source = await ui.InvokeAsync(() => ProjectRecoveryController.Unsaved(vm.Project!));
await controller.BindAsync(source); await controller.DrainAsync();
var first = (await store.ListAsync()).Items.Single();
Check("never-saved project remains dirty and has GUID-backed restart metadata", await ui.InvokeAsync(() => vm.IsDirty)
    && first.Dirty == true && first.BindingKind == RecoveryBindingKind.UnsavedProject && first.ProjectGuid == source.ProjectGuid);
var initialHead = first.Id;
await controller.DrainAsync();
Check("drain without new state does not supersede a selected checkpoint", (await store.ListAsync()).Items.Single().Id == initialHead);
await ui.InvokeAsync(() => { privateDrafts = true; vm.Edit("synthetic edit", p => p.Name = "private-draft-boundary"); vm.UndoCommand.Execute(null); vm.RedoCommand.Execute(null); return true; });
await controller.DrainAsync();
var candidate = await store.LoadCandidateAsync(initialHead, source);
Check("private drafts skip checkpoint but independent edit undo redo audit survives", controller.StatusCode == "Recovery.PrivateDraftsSkipped"
    && (await store.ListAsync()).Items.Single().Id == initialHead && candidate.Notices.Contains("Recovery.AuditAheadOfCheckpoint") && recorder.Status.CompletedAudits == 3);
Check("draft gate refuses restore without consuming or discarding private state", await Rejected(async () => await controller.PrepareAdoptionAsync(initialHead, source,
    () => Task.FromResult(true), candidate.Notices)) && privateDrafts && await ui.InvokeAsync(() => vm.Project!.Name == "private-draft-boundary"));
await ui.InvokeAsync(() => { privateDrafts = false; programming = true; return true; });
Check("active programming refuses restoration", await Rejected(async () => await controller.PrepareAdoptionAsync(initialHead, source,
    () => Task.FromResult(true), candidate.Notices)));
await ui.InvokeAsync(() => { programming = false; return true; });
// A checkpoint from another logical session remains selectable while current edits are checkpointed.
var otherProject = Knx.Workbench.WorkbenchJson.CreateDemo(); otherProject.Guid = Guid.NewGuid().ToString("D"); otherProject.Name = "restore-me";
var otherUndo = new UndoStack(); otherUndo.ResetUnsaved(otherProject);
var otherSource = ProjectRecoveryController.Unsaved(otherProject); var otherId = otherSource.UnsavedSessionId!;
var saved = await store.SaveAsync(otherUndo.ExportRecovery(otherProject, otherId, otherSource));
var verified = await controller.ValidateAsync(saved.Id, otherSource);
var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var gateRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var approving = controller.PrepareAdoptionAsync(saved.Id, otherSource, () => { gateEntered.TrySetResult(); return gateRelease.Task; }, verified.Notices);
await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
await ui.InvokeAsync(() => { vm.Edit("edit while discard prompt is open", p => p.Name = "preserve-during-gate"); return true; });
gateRelease.SetResult(true);
Check("changes during the discard gate require a new approval", await Rejected(async () => await approving)
    && await ui.InvokeAsync(() => vm.Project!.Name == "preserve-during-gate"));
await controller.DrainAsync();
reader.BlockNext();
var preparing = controller.PrepareAdoptionAsync(saved.Id, otherSource, () => Task.FromResult(true), verified.Notices);
await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
await ui.InvokeAsync(() => { vm.Edit("edit during validation", p => p.Name = "preserve-during-validation"); return true; });
reader.Release();
Check("approval baseline rejects edits during delayed validation before a ticket can be issued", await Rejected(async () => await preparing)
    && await ui.InvokeAsync(() => vm.Project!.Name == "preserve-during-validation"));
await controller.DrainAsync();
var ticket = await controller.PrepareAdoptionAsync(saved.Id, otherSource, () => Task.FromResult(true), verified.Notices);
await ui.InvokeAsync(() => { vm.Edit("concurrent mutation", p => p.Name = "keep-live-edit"); return true; });
Check("post-gate mutation rejects stale adoption and preserves current project", await Rejected(() => controller.AdoptAsync(ticket))
    && await ui.InvokeAsync(() => vm.Project!.Name == "keep-live-edit"));
await controller.DrainAsync();
verified = await controller.ValidateAsync(saved.Id, otherSource);
ticket = await controller.PrepareAdoptionAsync(saved.Id, otherSource, () => Task.FromResult(true), verified.Notices);
var oldSession = controller.SessionId;
await controller.AdoptAsync(ticket);
Check("adoption replaces Project Undo Source together and starts a fresh unsaved session", await ui.InvokeAsync(() => vm.Project!.Name == "restore-me" && vm.IsDirty && vm.ProjectPath == "")
    && controller.SessionId != oldSession && controller.Source?.UnsavedSessionId == controller.SessionId);
Check("adoption ticket is one-use", await Rejected(() => controller.AdoptAsync(ticket)));
await controller.DrainAsync();
Check("fresh restoration journal leaves parent checkpoint available", (await store.ListAsync()).Items.Any(item => item.Id == saved.Id));
// Force the persistence layer to wait while the owning project thread remains responsive.
persistence.BlockNextWrite();
await ui.InvokeAsync(() => { vm.Edit("slow disk", p => p.Name = "slow-write"); return true; });
await persistence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
var clock = Stopwatch.StartNew();
Check("blocked persistence never owns the UI thread", await ui.InvokeAsync(() => Environment.CurrentManagedThreadId == ui.ThreadId) && clock.ElapsedMilliseconds < 300 && !persistence.ObservedUiIo);
var close = controller.CloseAndDrainAsync(); await Task.Delay(40);
Check("close completion awaits the global writer", !close.IsCompleted);
persistence.Release(); await close;
await controller.DisposeAsync();

// Independent 60-state coalescing exercise and measured owning-thread capture duration.
var vm2 = await ui.InvokeAsync(() => new MainWindowViewModel(new CatalogLibrary(new CatalogStore(Path.Combine(root, "catalog2")))));
await ui.InvokeAsync(() => { vm2.CreateProject(Knx.Workbench.WorkbenchJson.CreateDemo(), null); return true; });
var controller2 = await ui.InvokeAsync(() => new ProjectRecoveryController(vm2, recorder, store, ui, () => false, () => false));
await controller2.BindAsync(await ui.InvokeAsync(() => ProjectRecoveryController.Unsaved(vm2.Project!)));
await ui.InvokeAsync(() => { for (var i = 0; i < 59; i++) { var n = i; vm2.Edit("burst " + i, p => p.Name = "burst-" + n); } return true; });
await controller2.DrainAsync();
Check("59 rapid edits retain independent audit and coalesce checkpoint capture", controller2.CaptureCount <= 2 && recorder.Status.CompletedAudits >= 64);
Check("60-state capture has a measured bounded synthetic UI duration", controller2.LastCaptureMilliseconds < 500);
Console.WriteLine($"MEASURE 60-state capture {controller2.LastCaptureMilliseconds:F2}ms; checkpoints {controller2.CaptureCount}; UI I/O {persistence.ObservedUiIo}");
var path = Path.Combine(root, "saved.knxproj");
await ui.InvokeAsync(() => { vm2.SaveProjectAs(path); return true; }); await controller2.DrainAsync();
Check("successful Save As refreshes exact source hash and preserves audit", controller2.Source?.Kind == RecoveryBindingKind.SavedArchive
    && controller2.Source.FullPath == path && await ui.InvokeAsync(() => !vm2.IsDirty));
await ui.InvokeAsync(() => { vm2.SaveProject(); return true; }); await controller2.DrainAsync();
Check("successful Save audit is separate from undo branching", recorder.Status.CompletedAudits >= 66);
verified = await controller2.ValidateAsync(saved.Id, otherSource);
var savedGate = await controller2.PrepareAdoptionAsync(saved.Id, otherSource, async () =>
{ await ui.InvokeAsync(() => { vm2.SaveProject(); return true; }); return true; }, verified.Notices);
Check("explicitly approved Save inside discard gate completes binding before ticket baseline", savedGate is not null);
var sourceBytes = File.ReadAllBytes(path); File.AppendAllText(path, "changed-source-fixture");
Check("active source bytes are checked again before adopting another session", await Rejected(() => controller2.AdoptAsync(savedGate)));
File.WriteAllBytes(path, sourceBytes);
persistence.BlockNextWrite();
await ui.InvokeAsync(() => { vm2.Edit("bounded drain fixture", p => p.Name = "bounded-drain"); return true; });
await persistence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
var sharedDrain = recorder.DrainAsync(controller2.SessionId);
Check("parallel same-session drains share one bounded pending task", Enumerable.Range(0, 10000).All(_ => ReferenceEquals(sharedDrain, recorder.DrainAsync(controller2.SessionId))));
persistence.Release(); await controller2.DrainAsync();
var countBeforeOff = recorder.Status.CompletedAudits;
await ui.InvokeAsync(() => { controller2.SetRecordingEnabled(false); vm2.Edit("off means no plaintext capture", p => p.Name = "not-recorded"); return true; });
await controller2.DrainAsync();
Check("explicit opt-out adds no checkpoint or audit", recorder.Status.CompletedAudits == countBeforeOff);
await ui.InvokeAsync(() => { controller2.SetRecordingEnabled(true); return true; });
await controller2.BindAsync(await ProjectRecoveryController.ReadSelectedSourceAsync(path)); await controller2.DrainAsync();
// A failed replacement binding must never reuse the prior project's matching scalar IDs.
var priorSource = controller2.Source;
await ui.InvokeAsync(() =>
{
    var replacement = new Project { Id = vm2.Project!.Id, Guid = vm2.Project.Guid, Name = "same scalar identity B" };
    vm2.CreateProject(replacement, null); vm2.Edit("B with same scalar identity", p => p.Name = "B-not-A"); return true;
});
Check("project replacement synchronously invalidates old same-ID/GUID source", priorSource is not null && controller2.Source is null);
await controller2.BindAsync(await ui.InvokeAsync(() => ProjectRecoveryController.Unsaved(vm2.Project!)));
var selected = await ProjectRecoveryController.ReadSelectedSourceAsync(path);
Check("only explicitly selected local source yields exact saved binding", selected.FullPath == path && selected.SavedFile is not null);
foreach (var language in RecoveryUiStrings.Languages)
    Check("complete recovery translations " + language, RecoveryUiStrings.Keys.All(key => !string.IsNullOrWhiteSpace(RecoveryUiStrings.GetForLanguage(language, key))));
await controller2.DisposeAsync();
Console.WriteLine($"RECOVERY APP {checks} checks PASS; evidence root {root}");
return 0;

sealed class OwnedDispatcher : IRecoveryDispatcher, IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(); private readonly Thread _thread;
    public int ThreadId { get; private set; }
    public OwnedDispatcher() { _thread = new(() => { ThreadId = Environment.CurrentManagedThreadId; foreach (var action in _queue.GetConsumingEnumerable()) action(); }) { IsBackground = true }; _thread.Start(); }
    public Task<T> InvokeAsync<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == ThreadId) return Task.FromResult(action());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() => { try { result.SetResult(action()); } catch (Exception ex) { result.SetException(ex); } }); return result.Task;
    }
    public void Dispose() { _queue.CompleteAdding(); _thread.Join(); _queue.Dispose(); }
}
sealed class ObservedPersistence(WorkbenchRecoveryStore store, OwnedDispatcher ui) : IProjectRecoveryPersistence
{
    private TaskCompletionSource? _release; public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ObservedUiIo { get; private set; }
    public void BlockNextWrite() { _release = new(TaskCreationOptions.RunContinuationsAsynchronously); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Release() => _release!.TrySetResult();
    private async Task Observe()
    {
        ObservedUiIo |= Environment.CurrentManagedThreadId == ui.ThreadId;
        if (_release is { } wait) { Entered.TrySetResult(); await wait.Task; _release = null; }
    }
    public async Task SaveAsync(RecoveryExport export, CancellationToken token) { await Observe(); await store.SaveAsync(export, cancellationToken: token); }
    public async Task AppendAuditAsync(RecoveryAuditContext context, IReadOnlyList<RecoveryAuditDraft> drafts, CancellationToken token) { await Observe(); await store.AppendAuditAsync(context, drafts, token); }
    public async Task OmitProtectedAsync(string sessionId, CancellationToken token) { await Observe(); await store.RecordProtectedOmissionAsync(sessionId, token); }
}
sealed class DelayedReader(IProjectRecoveryReader inner) : IProjectRecoveryReader
{
    private TaskCompletionSource? _release;
    public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void BlockNext() { _release = new(TaskCreationOptions.RunContinuationsAsynchronously); Entered = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Release() => _release!.TrySetResult();
    public Task<RecoveryListPage> ListAsync(string? continuation, CancellationToken token) => inner.ListAsync(continuation, token);
    public async Task<RecoveryCandidate> LoadCandidateAsync(string id, RecoverySource source, IRecoveryDependencyResolver? dependencies, CancellationToken token)
    {
        if (_release is { } release) { Entered.TrySetResult(); await release.Task.WaitAsync(token); _release = null; }
        return await inner.LoadCandidateAsync(id, source, dependencies, token);
    }
}

using Knx.Recovery;

namespace Knx.App.Services;

public sealed record RecoveryCommittedOperation(RecoveryOperationKind Kind, string Description, string Before, string After);

public sealed partial class MainWindowViewModel
{
    internal UndoStack RecoveryUndo => _undo;
    internal Func<Project, RecoveryDependencyInventory>? RecoveryDependencyRecorder { get; set; }
    private long _recoveryCatalogGeneration;
    internal long RecoveryCatalogGeneration => Interlocked.Read(ref _recoveryCatalogGeneration);
    internal bool RecoveryFingerprintEnabled { get; set; } = true;
    internal event EventHandler<RecoveryCommittedOperation>? RecoveryCommitted;
    internal event EventHandler? RecoveryCommitFailed;
    internal event EventHandler? RecoverySessionReplaced;
    private string? _recoveryPrevious;
    internal void ResetRecoveryFingerprint() => _recoveryPrevious = _undo.Index < 0 ? null : _undo.GetRecoveryFingerprint();
    private void NotifyProjectEdit(string description, RecoveryOperationKind kind = RecoveryOperationKind.Edit)
    {
        if (RecoveryCommitted is not null)
        {
            try
            {
                var after = RecoveryFingerprintEnabled ? _undo.GetRecoveryFingerprint() : "";
                var before = _recoveryPrevious ?? after;
                _recoveryPrevious = after;
                RecoveryCommitted.Invoke(this, new(kind, description, before, after));
            }
            catch { RecoveryCommitFailed?.Invoke(this, EventArgs.Empty); }
        }
        ProjectEdited?.Invoke(this, description);
    }
    internal void AdoptRecovery(RestoredRecoverySession restored, Func<Project, RecoveryDependencyInventory>? recorder, Action<object> publishSource)
    {
        restored.Undo.SetRecoveryDependencyRecorder(recorder);
        RecoveryDependencyRecorder = recorder;
        // Assign the complete tuple and source before emitting any observable property notification.
        _undo = restored.Undo;
        _project = restored.Project;
        _projectPath = restored.Source.Kind == RecoveryBindingKind.SavedArchive ? restored.Source.FullPath! : "";
        ProjectSessionIdentity = new();
        publishSource(ProjectSessionIdentity);
        _catalogPrograms.Clear();
        _restoringHistory = true;
        try { OnProjectChanged(restored.Project); }
        finally { _restoringHistory = false; }
        OnPropertyChanged(nameof(Project)); OnPropertyChanged(nameof(ProjectPath)); OnProjectPathChanged(_projectPath);
        ClearSelection(); ResetRecoveryFingerprint(); NotifyEditState(); Refresh();
    }
}

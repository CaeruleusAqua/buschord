using Knx.App.Workbench;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private readonly ProgrammingStateStore _programmingStateStore;
    private ProgrammingStatePersistence? _programmingPersistence;

    private void BindProgrammingState(WorkbenchSessions sessions, Project project)
    {
        _programmingPersistence = new(sessions.Programming, _programmingStateStore, project, ViewModel.ProjectPath,
            error => ViewModel.Status = W("ProgrammingFailed", error));
    }
}

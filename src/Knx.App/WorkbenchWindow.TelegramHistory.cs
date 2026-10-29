using Knx.App.Workbench;
using Knx.Workbench;
using Avalonia.Controls;
using Avalonia.Data;

namespace Knx.App;

public partial class WorkbenchWindow
{
    private TelegramHistoryWindow? _telegramHistoryWindow;
    private Button CreateTelegramHistoryButton(MonitorSession monitor)
    {
        var button=ActionButton(W("HistoryTitle"),ShowTelegramHistory,"quiet");button.Name="OpenTelegramHistory";
        button.Bind(ContentControl.ContentProperty,new Binding(nameof(MonitorSession.HistoryActionLabel)){Source=monitor});
        return button;
    }
    private void ShowTelegramHistory()
    {
        EnsureSessions();var monitor=_sessions?.Monitor;
        if(_telegramHistoryWindow is { } previous){previous.Activate();return;}
        var identity=ViewModel.ProjectSessionIdentity;
        var window=new TelegramHistoryWindow(monitor,()=>ViewModel.Project,()=>ViewModel.ProjectSessionIdentity,
            monitor?.Filter,target=>{if(ReferenceEquals(identity,ViewModel.ProjectSessionIdentity))OpenTarget(target);});
        _telegramHistoryWindow=window;_toolWindows.Add(window);
        window.Closed+=(_,_)=>
        {
            _toolWindows.Remove(window);if(ReferenceEquals(_telegramHistoryWindow,window))_telegramHistoryWindow=null;
            _pendingSessionDisposals.RemoveAll(task=>task.IsCompleted);
            if(!window.CloseCompletion.IsCompleted)_pendingSessionDisposals.Add(ObserveHistoryCloseAsync(window.CloseCompletion));
        };
        window.Show(this);
    }
    private async Task ObserveHistoryCloseAsync(Task completion)
    {try{await completion;}catch(Exception ex){ViewModel.Status=W("ToolSessionEnded",ex.Message);}}
}

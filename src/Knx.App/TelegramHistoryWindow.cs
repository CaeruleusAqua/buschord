using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.App.Workbench;
using Knx.Bus.History;
using Knx.Workbench;

namespace Knx.App;

/// <summary>Local queries and explicit capture controls; opening this window never connects to a bus.</summary>
public sealed class TelegramHistoryWindow : Window
{
    private const int MaximumLoadedRows=2000;
    private readonly MonitorSession? _monitor;
    private readonly Func<object?> _identity;
    private readonly object? _expectedIdentity;
    private readonly Func<Project?> _project;
    private readonly Action<WorkbenchTarget>? _navigate;
    private readonly bool _externalStore;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly HashSet<Task> _pending=[];
    private readonly Dictionary<string,TelegramHistoryRow> _loaded=new(StringComparer.Ordinal);
    private readonly TextBox _from=Field("HistoryFrom"),_until=Field("HistoryUntil"),_sources=Field("HistorySources"),_destinations=Field("HistoryDestinations"),
        _services=Field("HistoryServices"),_interfaces=Field("HistoryInterfaces"),_projects=Field("HistoryProjects");
    private readonly ComboBox _origin=new(){Name="HistoryOrigin",SelectedIndex=0,MinWidth=120};
    private readonly NumericUpDown _context=new(){Name="HistoryContext",Minimum=0,Maximum=20,Value=2,Width=90};
    private readonly ListBox _rows=new(){Name="HistoryRows",HorizontalAlignment=HorizontalAlignment.Stretch};
    private readonly TextBlock _status=new(){Name="HistoryStatus",TextWrapping=TextWrapping.Wrap},_coverage=new(){Name="HistoryCoverage",TextWrapping=TextWrapping.Wrap},
        _loadedRange=new(){Name="HistoryLoadedRange",TextWrapping=TextWrapping.Wrap},_captureStatus=new(){Name="HistoryCaptureStatus",TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _coverageCaption=new(){Name="HistoryCoverageCaption",TextWrapping=TextWrapping.Wrap,FontSize=11,IsVisible=false};
    private readonly TextBox _details=new(){Name="HistoryDetails",IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=150};
    private readonly CheckBox _capture=new(){Name="HistoryCapture"};
    private readonly Button _search=Button("HistorySearch"),_next=Button("HistoryNext"),_cancel=Button("HistoryCancel"),_backup=Button("HistoryBackup"),
        _export=Button("HistoryExportLoaded"),_retain=Button("HistoryRetention"),_openGroup=Button("HistoryOpenGroup"),_openDevice=Button("HistoryOpenDevice");
    private readonly Border _retentionPrompt=new(){IsVisible=false,Padding=new(10),Classes={"wb-panel"}};
    private readonly TextBlock _retentionText=new(){Name="HistoryRetentionText",TextWrapping=TextWrapping.Wrap};
    private readonly ScrollViewer _controlsScroll=new(){Name="HistoryControlsScroll",HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
    private readonly ScrollViewer _detailsScroll=new(){HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
    private readonly Expander _detailsExpander=new(){Name="HistoryDetailsExpander",IsExpanded=false};
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private TelegramHistoryStorePool.Lease? _lease;
    private ITelegramHistoryStore? _store;
    private CancellationTokenSource? _queryCancellation;
    private TelegramQuery? _query;
    private TelegramHistoryQueryInput? _inputAtQuery;
    private TelegramCoverage? _coverageResult;
    private string? _cursor;
    private long _request;
    private bool _closed,_syncCapture,_filtersBuilding=true,_busy,_initialEmptyScope;
    private DateTimeOffset? _retentionBoundary;
    private string _statusKey="HistoryOpening";
    private object[] _statusArgs=[];
    private Task? _initialization;
    public Task CloseCompletion { get; private set; }=Task.CompletedTask;

    public TelegramHistoryWindow(MonitorSession? monitor,Func<Project?> project,Func<object?> sessionIdentity,
        MonitorFilter? initialScope=null,Action<WorkbenchTarget>? navigate=null,ITelegramHistoryStore? store=null)
    {
        _monitor=monitor;_project=project;_identity=sessionIdentity;_expectedIdentity=sessionIdentity();_navigate=navigate;_store=store;_externalStore=store is not null;
        Width=1180;Height=850;MinWidth=680;MinHeight=460;Classes.Add("wb-dialog");
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Bind(TitleProperty,Text("HistoryTitle"));
        var layout=new DockPanel{Margin=new(18),LastChildFill=true};Content=layout;
        var top=new StackPanel{Spacing=8};_controlsScroll.Content=top;DockPanel.SetDock(_controlsScroll,Dock.Top);layout.Children.Add(_controlsScroll);
        top.Children.Add(Label("HistoryNotice"));BindContent(_capture,"HistoryCapture");top.Children.Add(_capture);top.Children.Add(_captureStatus);
        var fields=new Grid{ColumnDefinitions=new("*,*,*,*"),RowDefinitions=new("Auto,Auto"),ColumnSpacing=10,RowSpacing=8};
        var filterItems=new[]{FieldPanel("HistoryFrom",_from),FieldPanel("HistoryUntil",_until),FieldPanel("HistorySources",_sources),FieldPanel("HistoryDestinations",_destinations),
            FieldPanel("HistoryServices",_services),FieldPanel("HistoryInterfaces",_interfaces),FieldPanel("HistoryProjects",_projects)};
        for(var i=0;i<filterItems.Length;i++){Grid.SetColumn(filterItems[i],i%4);Grid.SetRow(filterItems[i],i/4);fields.Children.Add(filterItems[i]);}
        var extra=new StackPanel{Spacing=4};extra.Children.Add(FieldPanel("HistoryOrigin",_origin));extra.Children.Add(FieldPanel("HistoryContext",_context));Grid.SetRow(extra,1);Grid.SetColumn(extra,3);fields.Children.Add(extra);
        var filterContent=new StackPanel{Spacing=8};filterContent.Children.Add(Label("HistoryFilterHint"));filterContent.Children.Add(fields);
        var filters=new Expander{IsExpanded=true,Content=new ScrollViewer{Content=filterContent,MaxHeight=280,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled}};
        filters.Bind(Expander.HeaderProperty,Text("HistoryFilters"));top.Children.Add(filters);
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};
        foreach(var button in new[]{_search,_next,_cancel,_backup,_export,_retain}){button.Margin=new(0,0,6,6);actions.Children.Add(button);}
        var clear=Button("HistoryClearFilters");clear.Click+=(_,_)=>ClearFilters();actions.Children.Add(clear);
        DockPanel.SetDock(actions,Dock.Top);layout.Children.Add(actions);
        var summary=new StackPanel{Spacing=4,Margin=new(0,0,0,6)};summary.Children.Add(_status);summary.Children.Add(_loadedRange);summary.Children.Add(_coverageCaption);DockPanel.SetDock(summary,Dock.Top);layout.Children.Add(summary);
        top.Children.Add(_coverage);top.Children.Add(_retentionPrompt);
        AutomationProperties.SetLiveSetting(_status,AutomationLiveSetting.Polite);
        _rows.Bind(AutomationProperties.NameProperty,Text("HistoryRowsLabel"));_details.Bind(AutomationProperties.NameProperty,Text("HistoryDetailsLabel"));
        _context.TemplateApplied+=(_,e)=>e.NameScope.Find<TextBox>("PART_TextBox")?.Bind(AutomationProperties.NameProperty,Text("HistoryContext"));
        _search.IsDefault=true;
        AddHandler(KeyDownEvent,(_,e)=>{if(e.Key!=Key.Escape)return;if(_retentionPrompt.IsVisible){_retentionPrompt.IsVisible=false;_retentionBoundary=null;}else if(_busy)_queryCancellation?.Cancel();else Close();e.Handled=true;},RoutingStrategies.Tunnel);
        var retention=new StackPanel{Spacing=8};retention.Children.Add(_retentionText);var retentionButtons=new WrapPanel();
        var confirm=Button("HistoryConfirmRetention");confirm.Name="HistoryConfirmRetention";var dismiss=Button("HistoryCancelRetention");
        confirm.Click+=(_,_)=>ConfirmRetention();dismiss.Click+=(_,_)=>{_retentionPrompt.IsVisible=false;_retentionBoundary=null;};retentionButtons.Children.Add(confirm);retentionButtons.Children.Add(dismiss);retention.Children.Add(retentionButtons);_retentionPrompt.Child=retention;
        var detailPanel=new StackPanel{Spacing=6,Margin=new(0,8,0,0)};_detailsScroll.Content=detailPanel;_detailsExpander.Content=_detailsScroll;
        _detailsExpander.Bind(Expander.HeaderProperty,Text("HistoryDetailsLabel"));DockPanel.SetDock(_detailsExpander,Dock.Bottom);layout.Children.Add(_detailsExpander);
        detailPanel.Children.Add(_details);var navigation=new WrapPanel();navigation.Children.Add(_openGroup);navigation.Children.Add(_openDevice);detailPanel.Children.Add(navigation);
        _openGroup.Click+=(_,_)=>NavigateSelected(true);_openDevice.Click+=(_,_)=>NavigateSelected(false);
        var table=new Grid{RowDefinitions=new("Auto,*"),MinWidth=1040};var heading=new Grid{ColumnDefinitions=Columns(),ColumnSpacing=6};
        var headings=new[]{"ColumnTime","ColumnSource","ColumnDestination","ColumnService","HistoryPayload","HistoryInterfaces","ColumnOrigin","HistoryContextMarker"};
        for(var i=0;i<headings.Length;i++){var label=Label(headings[i]);Grid.SetColumn(label,i);heading.Children.Add(label);}table.Children.Add(heading);
        Grid.SetRow(_rows,1);table.Children.Add(_rows);
        layout.Children.Add(new ScrollViewer{Content=table,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,VerticalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled});
        _rows.ItemTemplate=new FuncDataTemplate<TelegramHistoryRow>((row,_)=>RowView(row));
        _rows.SelectionChanged+=(_,_)=>RefreshDetails();
        _search.Click+=(_,_)=>Search();_next.Click+=(_,_)=>Next();_cancel.Click+=(_,_)=>_queryCancellation?.Cancel();
        _backup.Click+=(_,_)=>Track(BackupAsync);_export.Click+=(_,_)=>Track(ExportAsync);_retain.Click+=(_,_)=>ProposeRetention();
        _capture.IsCheckedChanged+=(_,_)=>{if(!_syncCapture)Track(()=>SetCaptureAsync(_capture.IsChecked==true));};
        _timer.Tick+=(_,_)=>RefreshCapture();
        _from.Text=DateTimeOffset.UtcNow.AddHours(-1).ToString("O",CultureInfo.InvariantCulture);_until.Text=DateTimeOffset.UtcNow.AddMinutes(1).ToString("O",CultureInfo.InvariantCulture);
        ApplyScope(initialScope??new(MonitorScope.All));
        foreach(var field in new[]{_from,_until,_sources,_destinations,_services,_interfaces,_projects})field.TextChanged+=(_,_)=>FiltersChanged();
        _origin.SelectionChanged+=(_,_)=>FiltersChanged();_context.ValueChanged+=(_,_)=>FiltersChanged();
        Strings.Instance.PropertyChanged+=LanguageChanged;RefreshLanguage();_filtersBuilding=false;UpdateControls();
        WorkbenchDialogChrome.Apply(this);Opened+=(_,_)=>{Fit();_timer.Start();_initialization=Track(InitializeAsync);};ScalingChanged+=(_,_)=>Fit();
        SizeChanged+=(_,_)=>UpdateLayoutBudget();_detailsExpander.PropertyChanged+=(_,e)=>{if(e.Property==Expander.IsExpandedProperty)UpdateLayoutBudget();};UpdateLayoutBudget();
        Closed+=(_,_)=>CloseResources();
    }

    private static ColumnDefinitions Columns()=>new("190,75,105,125,120,170,65,65");
    private static Binding Text(string key)=>new("["+key+"]"){Source=WorkbenchStrings.Instance};
    private static void BindContent(ContentControl control,string key)=>control.Bind(ContentControl.ContentProperty,Text(key));
    private static TextBlock Label(string key){var label=new TextBlock{TextWrapping=TextWrapping.Wrap};label.Bind(TextBlock.TextProperty,Text(key));return label;}
    private static TextBox Field(string key){var field=new TextBox{Name=key,Classes={"wb-field"}};field.Bind(AutomationProperties.NameProperty,Text(key));return field;}
    private static Button Button(string key){var button=new Button{Name=key,Classes={"wb-button"}};BindContent(button,key);return button;}
    private static Control FieldPanel(string key,Control control)
    {control.Bind(AutomationProperties.NameProperty,Text(key));var panel=new StackPanel{Spacing=4};panel.Children.Add(Label(key));panel.Children.Add(control);return panel;}
    private static string W(string key,params object[] args)=>WorkbenchStrings.Get(key,args);
    private static string Time(DateTimeOffset time)=>time.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss.fff 'UTC'",CultureInfo.InvariantCulture);

    private Control RowView(TelegramHistoryRow? row)
    {
        if(row is null)return new Border();
        var e=row.Entry;var grid=new Grid{ColumnDefinitions=Columns(),ColumnSpacing=6,MinHeight=30};
        var values=new[]{Time(e.TimestampUtc),e.Source,e.Destination,e.Service,e.DataHex,e.SourceId,e.Origin,row.IsMatch?"✓":"…"};
        for(var i=0;i<values.Length;i++){var label=new TextBlock{Text=values[i],TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};if(!row.IsMatch)label.Classes.Add("wb-muted");Grid.SetColumn(label,i);grid.Children.Add(label);}
        return grid;
    }
    private void ApplyScope(MonitorFilter scope)
    {
        var project=_project();if(project is null)return;
        if(scope.Scope==MonitorScope.None)_initialEmptyScope=true;
        if(scope.Scope!=MonitorScope.All)_projects.Text=string.IsNullOrWhiteSpace(project.Guid)?project.Id:project.Guid;
        IEnumerable<string>? ids=scope.Scope switch
        {
            MonitorScope.GroupAddress=>[scope.Id??""],
            MonitorScope.Device=>ProjectQuery.ComObjectRefs(project).Where(o=>o.Device.Id==scope.Id).SelectMany(o=>o.Ref.Links),
            MonitorScope.ComObject=>ProjectQuery.ComObjectRefs(project).Where(o=>o.Ref.Id==scope.Id&&(scope.DeviceId is null||o.Device.Id==scope.DeviceId)).SelectMany(o=>o.Ref.Links),
            MonitorScope.Room=>WorkbenchProject.ScopeObjects(project,new(WorkbenchScopeKind.Room,scope.Id)).SelectMany(o=>o.Object.Links),
            _=>null,
        };
        if(ids is not null){var selected=ids.ToHashSet(StringComparer.Ordinal);_destinations.Text=string.Join(';',ProjectQuery.GroupAddresses(project).Where(g=>selected.Contains(g.Id)).Select(g=>g.Address.ToString()).Distinct());_initialEmptyScope=string.IsNullOrWhiteSpace(_destinations.Text);}
    }
    private async Task InitializeAsync()
    {
        if(_store is null){_lease=await TelegramHistoryStorePool.AcquireAsync(cancellationToken:_lifetime.Token);_store=_lease.Store;}
        if(_closed)return;RefreshCapture();UpdateControls();Search();
    }
    private void FiltersChanged()
    {
        if(_filtersBuilding||_closed)return;
        // Deferred TextChanged/SelectionChanged notifications can arrive after an eager query.
        // Invalidate only if the input actually differs from that query's bound input.
        if(_inputAtQuery==Input())return;
        _request++;_queryCancellation?.Cancel();_query=null;_cursor=null;_busy=false;
        if(!string.IsNullOrWhiteSpace(_destinations.Text))_initialEmptyScope=false;
        SetStatus("HistoryFiltersChanged");UpdateControls();
    }
    private void ClearFilters()
    {
        _initialEmptyScope=false;_filtersBuilding=true;
        foreach(var field in new[]{_sources,_destinations,_services,_interfaces,_projects})field.Text="";
        _origin.SelectedIndex=0;_filtersBuilding=false;FiltersChanged();Search();
    }
    private TelegramHistoryQueryInput Input()=>new(_from.Text??"",_until.Text??"",_sources.Text??"",_destinations.Text??"",_services.Text??"",_interfaces.Text??"",_projects.Text??"",_origin.SelectedIndex switch{1=>"Real",2=>"Demo",_=>null},(int)(_context.Value??0));
    private bool SessionMatches()=>ReferenceEquals(_identity(),_expectedIdentity);
    private void Search()
    {
        if(_closed||_store is null)return;
        if(_initialEmptyScope){SetStatus("HistoryScopeHasNoTargets");return;}
        if(!SessionMatches()){SetStatus("HistoryProjectChanged");return;}
        _queryCancellation?.Cancel();_queryCancellation?.Dispose();_queryCancellation=CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var request=++_request;
        try{var input=Input();_query=input.Parse(request);_inputAtQuery=input;}catch(FormatException ex){_query=null;_cursor=null;_busy=false;SetStatus(ex.Message switch{"HistoryInvalidContext"=>"HistoryInvalidContext","HistoryTooManyFilters"=>"HistoryTooManyFilters",_=>"HistoryInvalidTimeRange"});UpdateControls();return;}
        _loaded.Clear();_rows.ItemsSource=Array.Empty<TelegramHistoryRow>();_cursor=null;_coverageResult=null;RefreshSummary();
        Track(()=>LoadAsync(_query!,request,_queryCancellation.Token));
    }
    private void Next()
    {
        if(_query is null||_cursor is null||_busy)return;
        var request=++_request;var query=_query with{Cursor=_cursor,RequestGeneration=request};
        Track(()=>LoadAsync(query,request,_queryCancellation!.Token));
    }
    private async Task LoadAsync(TelegramQuery query,long request,CancellationToken token)
    {
        _busy=true;SetStatus("HistoryLoading");UpdateControls();
        try
        {
            var page=await _store!.QueryAsync(query,token);var coverage=await _store.GetCoverageAsync(query.FromUtc,query.UntilUtc,token);
            if(_closed||request!=_request||page.RequestGeneration!=request)return;
            if(!SessionMatches()){_loaded.Clear();_rows.ItemsSource=Array.Empty<TelegramHistoryRow>();SetStatus("HistoryProjectChanged");return;}
            _coverageResult=coverage;
            if(page.Status==TelegramQueryStatus.StaleCursor||page.StoreGeneration!=coverage.Generation){_cursor=null;SetStatus("HistoryStaleCursor");RefreshSummary();return;}
            foreach(var row in page.Rows)
            {if(_loaded.TryGetValue(row.Entry.Id,out var previous))_loaded[row.Entry.Id]=row with{IsMatch=row.IsMatch||previous.IsMatch};else if(_loaded.Count<MaximumLoadedRows)_loaded.Add(row.Entry.Id,row);}
            _cursor=_loaded.Count>=MaximumLoadedRows?null:page.NextCursor;
            var selectedId=(_rows.SelectedItem as TelegramHistoryRow)?.Entry.Id;
            _rows.ItemsSource=LoadedRows();
            if(selectedId is not null)_rows.SelectedItem=_loaded.GetValueOrDefault(selectedId);
            SetStatus(_loaded.Count>=MaximumLoadedRows?"HistoryLoadedLimit":page.Status==TelegramQueryStatus.ScanLimit?"HistoryScanLimit":_loaded.Count==0?"HistoryNoMatches":"HistoryLoaded",_loaded.Count);
            RefreshSummary();
        }
        catch(OperationCanceledException)when(token.IsCancellationRequested){if(!_closed&&request==_request)SetStatus("HistoryCancelled");}
        catch(Exception ex){if(!_closed&&request==_request)SetStatus("HistoryOperationFailed",ex.Message);}
        finally{if(request==_request){_busy=false;UpdateControls();}}
    }
    private TelegramHistoryRow[] LoadedRows()=>_loaded.Values.OrderByDescending(r=>r.Entry.TimestampUtc).ThenByDescending(r=>r.Entry.Id,StringComparer.Ordinal).ToArray();

    private void RefreshSummary()
    {
        _loadedRange.Text=_loaded.Count==0?W("HistoryLoadedEmpty"):W("HistoryLoadedRange",_loaded.Count,Time(_loaded.Values.Min(r=>r.Entry.TimestampUtc)),Time(_loaded.Values.Max(r=>r.Entry.TimestampUtc)));
        ToolTip.SetTip(_loadedRange,W("HistoryLoaded",_loaded.Count));
        if(_coverageResult is not { } c){_coverage.Text="";_coverageCaption.Text="";_coverageCaption.IsVisible=false;return;}
        var seconds=c.KnownCaptured.Sum(r=>(r.UntilUtc-r.FromUtc).TotalSeconds);
        _coverageCaption.Text=W("HistoryCoverageCompact",Math.Round(seconds),c.Gaps.Count,c.CaptureIntervals.Count(i=>i.IsActive),c.CaptureIntervals.Count(i=>i.EndUnknown));_coverageCaption.IsVisible=true;
        _coverage.Text=W("HistoryCoverageSummary",Math.Round(seconds),c.Gaps.Count,c.CaptureIntervals.Count(i=>i.IsActive),c.CaptureIntervals.Count(i=>i.EndUnknown))
            +Environment.NewLine+W("HistoryCoverageScope")
            +(c.RetainedFromUtc is { } retained?Environment.NewLine+W("HistoryRetainedFrom",Time(retained)):"")
            +(c.HasUnverifiedTail?Environment.NewLine+W("HistoryRecoveryWarning"):"");
    }
    private void RefreshDetails()
    {
        if(_rows.SelectedItem is not TelegramHistoryRow row){_details.Text="";_openGroup.IsEnabled=_openDevice.IsEnabled=false;return;}
        var e=row.Entry;var unknown=W("Unknown");
        _details.Text=W("HistoryDetailsText",e.Id,Time(e.TimestampUtc),e.SourceId,e.ProjectId??unknown,e.HopCount?.ToString(CultureInfo.InvariantCulture)??unknown,
            e.Flags??unknown,e.Priority??unknown,e.Acknowledged?.ToString()??unknown,e.RawCemiHex??unknown);
        _details.Text+=Environment.NewLine+W(ProjectMatches(e,_project())?"HistoryCurrentLookupNotice":"HistoryProjectLookupUnavailable");
        _openGroup.IsEnabled=ResolveTarget(true) is not null;_openDevice.IsEnabled=ResolveTarget(false) is not null;
    }
    private WorkbenchTarget? ResolveTarget(bool group)
    {
        if(_navigate is null||!SessionMatches()||_rows.SelectedItem is not TelegramHistoryRow row||_project() is not { } project)return null;
        if(!ProjectMatches(row.Entry,project))return null;
        if(group){var address=row.Entry.IsGroup?ProjectQuery.GroupAddresses(project).FirstOrDefault(g=>g.Address.ToString()==row.Entry.Destination):null;return address is null?null:new("ga",address.Id);}
        var device=ProjectQuery.DeviceList(project).FirstOrDefault(d=>d.Address.ToString()==row.Entry.Source);return device is null?null:new("device",device.Id);
    }
    private void NavigateSelected(bool group){if(ResolveTarget(group) is { } target)_navigate?.Invoke(target);}
    private static bool ProjectMatches(TelegramHistoryEntry entry,Project? project)=>project is not null
        &&Guid.TryParse(project.Guid,out var current)&&current!=Guid.Empty&&Guid.TryParse(entry.ProjectId,out var recorded)&&current==recorded;

    private async Task SetCaptureAsync(bool enabled)
    {
        if(!SessionMatches())throw new InvalidOperationException(W("HistoryProjectChanged"));
        if(_monitor is null)throw new InvalidOperationException(W("HistoryNoMonitor"));
        await _monitor.SetHistoryEnabledAsync(enabled,_lifetime.Token);RefreshCapture();
    }
    private void RefreshCapture()
    {
        if(_externalStore){_captureStatus.Text=W("HistoryExternalViewer");_capture.IsEnabled=false;return;}
        if(_monitor is null){_captureStatus.Text=W("HistoryNoMonitor");_capture.IsEnabled=false;return;}
        _syncCapture=true;_capture.IsChecked=_monitor.History.IsEnabled;_syncCapture=false;
        var state=_monitor.History;
        _captureStatus.Text=state.Failure switch
        {
            MonitorHistoryFailure.QueueOverflow=>W("HistoryQueueOverflow",state.DroppedCount),
            MonitorHistoryFailure.StoreUnavailable or MonitorHistoryFailure.WriteFailed=>W("HistoryCaptureFailed",state.ErrorDetail??""),
            _=>W(state.IsEnabled?(_monitor.IsRunning&&!_monitor.IsPaused?"HistoryCaptureRunning":"HistoryCaptureReady"):"HistoryCaptureDisabled",state.WrittenCount),
        };
        if(state.SkippedBeforeAnchorCount>0)_captureStatus.Text+=Environment.NewLine+W("HistoryPreAnchorSkipped",state.SkippedBeforeAnchorCount);
    }
    private async Task BackupAsync()
    {
        if(_store is null)return;
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title=W("HistoryBackup"),SuggestedFileName="buschord-history-backup-"+DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+".bch",DefaultExtension="bch"});if(file is null)return;
        var path=file.TryGetLocalPath()??throw new NotSupportedException(W("HistoryLocalPathRequired"));
        if(File.Exists(path))throw new IOException(W("HistoryBackupRequiresNewFile"));
        await _store.BackupAsync(path,_lifetime.Token);SetStatus("HistoryBackupDone");
    }
    private async Task ExportAsync()
    {
        var rows=LoadedRows();if(rows.Length==0)return;
        var file=await StorageProvider.SaveFilePickerAsync(new(){Title=W("HistoryExportLoaded"),SuggestedFileName="buschord-history-loaded.csv",DefaultExtension="csv"});if(file is null)return;
        await using var output=file.TryGetLocalPath() is { } path
            ?OpenPrivateExport(path)
            :await file.OpenWriteAsync();output.SetLength(0);await using var writer=new StreamWriter(output,new System.Text.UTF8Encoding(false));
        await writer.WriteAsync(TelegramHistoryExport.Csv(rows).AsMemory(),_lifetime.Token);SetStatus("HistoryExportDone",rows.Length);
    }
    private static FileStream OpenPrivateExport(string path)
    {
        var options=new FileStreamOptions{Mode=FileMode.Create,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.Asynchronous};
        if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
        return new(path,options);
    }
    private void ProposeRetention()
    {
        if(!TelegramHistoryQueryInput.TryUtc(_from.Text??"",out var boundary)||boundary>DateTimeOffset.UtcNow){SetStatus("HistoryInvalidRetention");return;}
        if(_monitor?.History.IsEnabled==true){SetStatus("HistoryDisableBeforeRetention");return;}
        _retentionBoundary=boundary;_retentionText.Text=W("HistoryRetentionNotice",boundary.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture));_retentionPrompt.IsVisible=true;
        Dispatcher.UIThread.Post(()=>{if(!_closed&&_retentionPrompt.IsVisible)_retentionPrompt.BringIntoView();});
    }
    private void ConfirmRetention()
    {
        if(_retentionBoundary is not { } boundary||_store is null)return;
        _retentionPrompt.IsVisible=false;_retentionBoundary=null;
        Track(async()=>{if(_monitor?.History.IsEnabled==true)throw new InvalidOperationException(W("HistoryDisableBeforeRetention"));await _store.RetainFromAsync(boundary,_lifetime.Token);Search();});
    }
    private void UpdateControls()
    {
        var ready=_store is not null&&!_closed;
        _capture.IsEnabled=ready&&!_externalStore&&_monitor is not null&&SessionMatches();_search.IsEnabled=ready;
        _next.IsEnabled=ready&&!_busy&&_query is not null&&_cursor is not null&&_loaded.Count<MaximumLoadedRows;
        _cancel.IsEnabled=ready&&_busy;_backup.IsEnabled=ready&&_store!.Capabilities.Backup;
        _retain.IsEnabled=ready&&_store!.Capabilities.Retention;_export.IsEnabled=ready&&_loaded.Count>0;
    }
    private void SetStatus(string key,params object[] args){_statusKey=key;_statusArgs=args;_status.Text=W(key,args);_status.IsVisible=key!="HistoryLoaded";}
    private void LanguageChanged(object? sender,System.ComponentModel.PropertyChangedEventArgs e){if(e.PropertyName=="Item[]")RefreshLanguage();}
    private void RefreshLanguage()
    {
        var index=_origin.SelectedIndex;_filtersBuilding=true;_origin.ItemsSource=new[]{W("HistoryAnyOrigin"),W("HistoryReal"),W("HistoryDemo")};_origin.SelectedIndex=Math.Max(0,index);_filtersBuilding=false;
        _status.Text=W(_statusKey,_statusArgs);RefreshCapture();RefreshSummary();RefreshDetails();
    }
    private Task Track(Func<Task> action)
    {
        var task=Run();_pending.Add(task);_ = Remove(task);return task;
        async Task Run(){try{await action();}catch(OperationCanceledException)when(_lifetime.IsCancellationRequested){}catch(Exception ex){if(!_closed){SetStatus("HistoryOperationFailed",ex.Message);UpdateControls();}}}
        async Task Remove(Task pending){await pending;_pending.Remove(pending);}
    }
    private void Fit()
    {
        var screen=Screens.ScreenFromWindow(this);if(screen is null)return;var width=Math.Max(1,screen.WorkingArea.Width/RenderScaling-32);var height=Math.Max(1,screen.WorkingArea.Height/RenderScaling-32);
        MinWidth=Math.Min(MinWidth,width);MinHeight=Math.Min(MinHeight,height);Width=Math.Clamp(Width,MinWidth,width);Height=Math.Clamp(Height,MinHeight,height);
    }
    private void UpdateLayoutBudget()
    {
        var height=ClientSize.Height>0?ClientSize.Height:Height;
        _detailsScroll.MaxHeight=Math.Clamp((height-400)*.35,60,140);
        var metadata=_detailsExpander.IsExpanded?_detailsScroll.MaxHeight+40:0;
        // Give expanded metadata space from the scrollable filters, preserving the telegram viewport.
        _controlsScroll.MaxHeight=Math.Max(48,(height-248)*.55-metadata);
    }
    private void CloseResources()
    {
        if(_closed)return;_closed=true;_timer.Stop();Strings.Instance.PropertyChanged-=LanguageChanged;_lifetime.Cancel();_queryCancellation?.Cancel();CloseCompletion=DisposeAfterOperationsAsync();_ = ObserveCloseAsync();
    }
    private async Task DisposeAfterOperationsAsync()
    {
        try{await Task.WhenAll(_pending.ToArray());}finally{if(_lease is { } lease)await lease.DisposeAsync();_queryCancellation?.Dispose();_lifetime.Dispose();}
    }
    private async Task ObserveCloseAsync(){try{await CloseCompletion;}catch(Exception){ /* Owner may await CloseCompletion; always observe failures. */ }}
}

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Services;
using Knx.Catalog;
using Knx.Core.Catalog.Downloads;

internal static class CatalogDownloadsDialogChecks
{
    internal static async Task Run(WorkbenchWindow shell, string theme, Action<string, bool> check)
    {
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-catalog-download-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var originalLanguage = Strings.Language;
        var projectBefore = Knxproj.ToXml(shell.ViewModel.Project!).ToString();
        var undoBefore = shell.ViewModel.CanUndo;
        var handler = new FakeCatalogHandler(Catalogue());
        var store = new CatalogStore(Path.Combine(root, "catalogues"));
        var library = new CatalogLibrary(store);
        await library.GetAsync();
        CatalogDownloadSource Source(string id, string name, string manufacturer, string manufacturerName,
            CatalogSourceRights rights = CatalogSourceRights.Unknown, CatalogSourceAvailability availability = CatalogSourceAvailability.Direct) =>
            new(id, name, manufacturer, new Uri("https://catalog.example.org/downloads"),
                availability == CatalogSourceAvailability.Direct ? new Uri("https://catalog.example.org/" + id + ".knxprod") : null,
                ["catalog.example.org"], availability, rights, new Uri("https://catalog.example.org/terms"),
                "synthetic-terms-v1", "Synthetic source metadata.", "knxprod", DateTimeOffset.UtcNow, manufacturerName);
        CatalogDownloadSource[] sources =
        [
            Source("personal", "Personal source", "M-0001", "Alpha"),
            Source("publisher", "Publisher source", "M-0001", "Alpha", CatalogSourceRights.ManufacturerGranted),
            Source("restricted", "Restricted source", "M-0002", "Beta", CatalogSourceRights.Restricted),
            Source("manual", "Manual source", "M-0003", "Gamma", availability: CatalogSourceAvailability.Manual),
        ];
        using var service = new CatalogDownloadService(store, Path.Combine(root, "downloads"), handler,
            new CatalogDownloadOptions { MinimumHostIntervalMilliseconds = 0 }, sources);
        var dialog = new CatalogDownloadsWindow(library, service);
        var closed = dialog.ShowDialog(shell);
        void Check(string name, bool value) => check(theme + " catalogue acquisition: " + name, value);
        T Control<T>(string name) where T : Control => dialog.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static async Task Settle()
        {
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        static async Task Until(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(30, timeout.Token); }
            await Settle();
        }
        try
        {
            await dialog.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await Settle();
            var manufacturers = Control<ListBox>("ManufacturerList");
            var sourceList = Control<ListBox>("DownloadSourceList");
            var accept = Control<CheckBox>("DownloadPermissionCheck");
            var evidence = Control<TextBox>("DownloadPermissionEvidence");
            var save = Control<Button>("SaveDownloadPermission");
            var selected = Control<Button>("DownloadSelectedSources");
            var all = Control<Button>("DownloadAuthorizedSources");
            var search = Control<TextBox>("ManufacturerSearch");
            Check("opening reads only local state", handler.Requests == 0 && !Directory.Exists(service.DataDirectory));
            Check("profiles work without a copied full registry", manufacturers.ItemsSource!.Cast<object>().Count() == 3
                && Control<TextBlock>("RegistrySummary").Text == CatalogDownloadStrings.Get("RegistryEmpty"));
            Check("unknown source has no implicit permission", !selected.IsEnabled && !all.IsEnabled && !save.IsEnabled
                && accept.IsChecked != true && Control<TextBlock>("DownloadRightsNotice").Text == CatalogDownloadStrings.Get("UnknownRights"));
            Check("search and lists have localized accessible names", ControlAutomationPeer.CreatePeerForElement(search)?.GetName() == CatalogDownloadStrings.Get("Search")
                && ControlAutomationPeer.CreatePeerForElement(manufacturers)?.GetName() == CatalogDownloadStrings.Get("Manufacturers")
                && ControlAutomationPeer.CreatePeerForElement(sourceList)?.GetName() == CatalogDownloadStrings.Get("Sources"));
            evidence.BringIntoView(); evidence.Focus(NavigationMethod.Tab);
            await Settle();
            Check("evidence editor has a localized accessible name at focus", evidence.IsFocused
                && ControlAutomationPeer.CreatePeerForElement(evidence)?.GetName() == CatalogDownloadStrings.Get("Evidence"));
            accept.IsChecked = true;
            Check("a checkbox alone cannot authorize unknown rights", !save.IsEnabled && !selected.IsEnabled);
            evidence.Text = "Too short";
            await Settle();
            Check("short evidence is refused", !save.IsEnabled);
            const string note = "Synthetic permission from vendor on 2026-10-28 for personal source.";
            evidence.Text = note;
            await Until(() => save.IsEnabled);
            Check("source-specific declaration requires both controls", save.IsEnabled && !selected.IsEnabled && handler.Requests == 0);
            Strings.Language = originalLanguage == "en" ? "de" : "en";
            await Settle();
            Check("live language change preserves unsaved declaration", evidence.Text == note && accept.IsChecked == true
                && selected.Content?.ToString() == CatalogDownloadStrings.Get("DownloadSelected"));
            Strings.Language = originalLanguage;
            await Settle();
            Click(save);
            await Until(() => service.GetAuthorization(sources[0]) == CatalogSourceAuthorization.UserDeclaredAuthorization && selected.IsEnabled);
            Check("saving records personal permission without a manufacturer grant", service.GetConsent("personal") is { Kind: CatalogConsentKind.UserDeclaredAuthorization, EvidenceNote: note }
                && sources[0].Rights == CatalogSourceRights.Unknown && handler.Requests == 0 && all.IsEnabled);
            Click(selected);
            await Until(() => service.LoadReports().Count == 1 && selected.IsEnabled);
            Control<Expander>("DownloadReportExpander").IsExpanded = true;
            await Settle();
            Check("download imports into and refreshes the existing library", handler.Requests == 1
                && library.Current?.Catalogs.Count == 1 && service.LoadReports().Single().CatalogsAdded == 1
                && Control<ListBox>("DownloadReports").ItemsSource!.Cast<object>().Any(row => row.ToString()!.Contains(CatalogDownloadStrings.Get("StatusImported"), StringComparison.Ordinal)
                    && row.ToString()!.Contains("Personal source", StringComparison.Ordinal)));
            Click(selected);
            await Until(() => service.LoadReports().Count == 2 && selected.IsEnabled);
            Check("repeat download skips identical catalogue", handler.Requests == 2 && library.Current?.Catalogs.Count == 1
                && service.LoadReports().Any(report => report.Items.Any(item => item.Status == CatalogDownloadItemStatus.IdenticalSkipped)));
            Click(Control<Button>("RevokeDownloadPermission"));
            await Until(() => service.GetConsent("personal") is null && !selected.IsEnabled);
            Check("revocation stops future requests and keeps installed catalogues", !all.IsEnabled && handler.Requests == 2 && library.Current?.Catalogs.Count == 1);

            sourceList.SelectedItem = sourceList.ItemsSource!.Cast<object>().Single(row => row.ToString()!.StartsWith("Publisher source", StringComparison.Ordinal));
            await Settle();
            Check("publisher terms remain separate from personal evidence", !evidence.IsEffectivelyVisible
                && Control<TextBlock>("DownloadRightsNotice").Text == CatalogDownloadStrings.Get("ManufacturerRights") && !save.IsEnabled);
            accept.IsChecked = true;
            Check("documented manufacturer terms need explicit consent", save.IsEnabled && !selected.IsEnabled);
            Click(save);
            await Until(() => service.GetAuthorization(sources[1]) == CatalogSourceAuthorization.ManufacturerTerms && selected.IsEnabled);
            handler.BlockNextRequest();
            Click(all);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Settle();
            Check("active job disables source changes and offers cancellation", !search.IsEnabled && !sourceList.IsEnabled
                && Control<Button>("CancelCatalogueDownload").IsVisible && !all.IsEnabled);
            Click(Control<Button>("CancelCatalogueDownload"));
            await Until(() => service.LoadReports().Count == 3 && all.IsEnabled);
            Check("cancellation has a durable partial report and preserves catalogue", service.LoadReports().Any(report => report.Cancelled)
                && library.Current?.Catalogs.Count == 1 && service.GetConsent("publisher") is not null);

            search.Text = "M-0002";
            await Settle();
            Check("manufacturer search includes exact manufacturer codes", manufacturers.ItemsSource!.Cast<object>().Count() == 1
                && manufacturers.SelectedItem!.ToString()!.Contains("Beta", StringComparison.Ordinal));
            Check("explicit restrictions cannot be waived by this dialog", !accept.IsEnabled && !save.IsEnabled && !selected.IsEnabled
                && Control<TextBlock>("DownloadRightsNotice").Text == CatalogDownloadStrings.Get("RestrictedRights"));
            search.Text = "Gamma";
            await Settle();
            Check("manual-only source cannot enter automatic queue", !selected.IsEnabled && !save.IsEnabled && !accept.IsEnabled
                && sourceList.SelectedItem!.ToString()!.Contains(CatalogDownloadStrings.Get("AuthManualOnly"), StringComparison.Ordinal));
            var beforeRefresh = handler.Requests;
            Click(Control<Button>("RefreshManufacturerRegistry"));
            await Settle();
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!;
            var consentDialog = lifetime.Windows.Single(window => window != shell && window != dialog);
            var fetch = consentDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ConfirmRegistryFetch");
            Check("full registry refresh requires an explicit local retrieval decision", !fetch.IsEnabled && handler.Requests == beforeRefresh
                && consentDialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked != true);
            Click(consentDialog.GetVisualDescendants().OfType<Button>().Single(button => button.IsCancel));
            await Settle();
            Check("cancelled registry confirmation sends no requests", handler.Requests == beforeRefresh && !File.Exists(Path.Combine(service.DataDirectory, "registry.json")));
            Check("catalogue acquisition does not edit project or undo", Knxproj.ToXml(shell.ViewModel.Project!).ToString() == projectBefore && shell.ViewModel.CanUndo == undoBefore);
            handler.BlockNextRequest();
            Click(all);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            dialog.Close();
            Check("Close requests cancellation before releasing the active dialog", dialog.IsVisible);
            await closed;
            Check("Close waits for a durable cancellation report", service.LoadReports().Count == 4
                && service.LoadReports().Count(report => report.Cancelled) == 2 && library.Current?.Catalogs.Count == 1);

            var warningRoot = Path.Combine(root, "report-warning");
            var warningStore = new CatalogStore(Path.Combine(warningRoot, "catalogues"));
            var warningLibrary = new CatalogLibrary(warningStore);
            await warningLibrary.GetAsync();
            var warningHandler = new FakeCatalogHandler(Catalogue());
            using var warningService = new CatalogDownloadService(warningStore, Path.Combine(warningRoot, "downloads"), warningHandler,
                new CatalogDownloadOptions { MinimumHostIntervalMilliseconds = 0 }, sources);
            await warningService.RecordConsentAsync("personal", CatalogConsentKind.UserDeclaredAuthorization, note);
            var blockedReports = Path.Combine(warningService.DataDirectory, "reports");
            File.WriteAllText(blockedReports, "report-directory-blocker");
            var warningDialog = new CatalogDownloadsWindow(warningLibrary, warningService);
            var warningClosed = warningDialog.ShowDialog(shell);
            try
            {
                await warningDialog.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await Settle();
                var download = warningDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DownloadSelectedSources");
                Click(download);
                await Until(() => warningLibrary.Current?.Catalogs.Count == 1 && download.IsEnabled);
                warningDialog.GetVisualDescendants().OfType<Expander>().Single(expander => expander.Name == "DownloadReportExpander").IsExpanded = true;
                await Settle();
                var warningText = warningDialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "DownloadMessage").Text ?? "";
                var warningPrefix = CatalogDownloadStrings.Get("ReportPersistenceWarning", "").Trim();
                Check("report persistence failure still refreshes successfully imported catalogue", warningHandler.Requests == 1
                    && warningLibrary.Current?.Catalogs.Count == 1 && File.ReadAllText(blockedReports) == "report-directory-blocker");
                Check("report persistence failure is visibly localized", warningText.Contains(warningPrefix, StringComparison.Ordinal)
                    && warningDialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "DownloadReports")
                        .ItemsSource!.Cast<object>().Any(row => row.ToString()!.Contains(warningPrefix, StringComparison.Ordinal)));
            }
            finally { warningDialog.Close(); await warningClosed; }

            var reloadRoot = Path.Combine(root, "cancel-reload");
            Directory.CreateDirectory(reloadRoot);
            var baselineFile = Path.Combine(reloadRoot, "baseline.knxprod");
            File.WriteAllBytes(baselineFile, Catalogue("P-BASE"));
            var reloadStore = new CatalogStore(Path.Combine(reloadRoot, "catalogues"));
            reloadStore.Import(baselineFile);
            var reloadLibrary = new CatalogLibrary(reloadStore);
            var blockedLoad = new BlockingCatalogProgress();
            var initialLoad = reloadLibrary.GetAsync(blockedLoad);
            await blockedLoad.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var reloadService = new CatalogDownloadService(reloadStore, Path.Combine(reloadRoot, "downloads"),
                new FakeCatalogHandler(Catalogue()), new CatalogDownloadOptions { MinimumHostIntervalMilliseconds = 0 }, sources);
            await reloadService.RecordConsentAsync("personal", CatalogConsentKind.UserDeclaredAuthorization, note);
            var reloadDialog = new CatalogDownloadsWindow(reloadLibrary, reloadService);
            var reloadClosed = reloadDialog.ShowDialog(shell);
            try
            {
                await reloadDialog.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await Settle();
                Click(reloadDialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "DownloadSelectedSources"));
                await Until(() => reloadService.LoadReports().Count == 1 && reloadDialog.LibraryReloadTask is not null);
                Check("completed import waits independently of a shared library load", !initialLoad.IsCompleted
                    && reloadService.LoadReports().Single().CatalogsAdded == 1);
                reloadDialog.Close();
                await reloadClosed.WaitAsync(TimeSpan.FromSeconds(10));
                Check("Close cancels the view waiter while the shared load continues", !initialLoad.IsCompleted
                    && reloadDialog.LibraryReloadContinues && reloadDialog.LibraryReloadTask is { IsCompleted: false });
                blockedLoad.Release.TrySetResult();
                await initialLoad.WaitAsync(TimeSpan.FromSeconds(10));
                await reloadDialog.LibraryReloadTask!.WaitAsync(TimeSpan.FromSeconds(10));
                Check("background load eventually publishes both preserved and imported catalogues", reloadLibrary.Current?.Catalogs.Count == 2);
            }
            finally
            {
                blockedLoad.Release.TrySetResult();
                if (reloadDialog.IsVisible) { reloadDialog.Close(); await reloadClosed; }
                await initialLoad.WaitAsync(TimeSpan.FromSeconds(10));
                if (reloadDialog.LibraryReloadTask is { } pending) await pending.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            Strings.Language = originalLanguage;
            if (dialog.IsVisible) { dialog.Close(); await closed; }
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(parent)
                && Path.GetFileName(resolved).StartsWith("buschord-catalog-download-ui-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    private static byte[] Catalogue(string productId = "P-1")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open(), Encoding.UTF8))
            writer.Write($"""
                <KNX xmlns="http://knx.org/xml/project/23"><ManufacturerData><Manufacturer RefId="M-0001">
                <Hardware><Hardware Id="H-1"><Products><Product Id="{productId}" Text="Synthetic catalogue" OrderNumber="{productId}" />
                </Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>
                """);
        return bytes.ToArray();
    }

    private sealed class BlockingCatalogProgress : IProgress<CatalogProgress>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Report(CatalogProgress value)
        {
            Entered.TrySetResult();
            if (!Release.Task.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Controlled catalogue load was not released.");
        }
    }

    private sealed class FakeCatalogHandler(byte[] archive) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        private bool _blockNext;
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void BlockNextRequest() { _blockNext = true; Started = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (_blockNext)
            {
                _blockNext = false;
                Started.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive), RequestMessage = request };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            return response;
        }
    }
}

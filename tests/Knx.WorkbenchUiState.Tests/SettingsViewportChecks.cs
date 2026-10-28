using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;

internal static class SettingsViewportChecks
{
    internal static async Task Run(WorkbenchWindow shell, IClassicDesktopStyleApplicationLifetime lifetime,
        string theme, Action<string, bool> check)
    {
        if (!Dispatcher.UIThread.CheckAccess()) throw new InvalidOperationException("Settings checks require the UI thread.");
        var settings = new AppSettings
        {
            Theme = theme == "light" ? "Light" : "Dark", Language = Strings.Language,
            BusInterface = "unchanged interface", TunnelAddress = "1.1.250"
        };
        var before = JsonSerializer.Serialize(settings);
        var settingsPath = AppSettings.Path;
        var savedBefore = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        var project = shell.ViewModel.Project;
        var projectBefore = project is null ? null : Knxproj.ToXml(project).ToString();
        var canUndo = shell.ViewModel.CanUndo;
        var dialog = new SettingsWindow(settings) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var closed = dialog.ShowDialog<bool>(shell);
        void Check(string name, bool value) => check(theme + " settings viewport: " + name, value);
        static async Task Settle()
        {
            await Task.Delay(100);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        bool InsideClient(Control control)
        {
            var origin = control.TranslatePoint(default, dialog);
            return origin is { } point && control.Bounds.Width > 0 && control.Bounds.Height > 0
                && point.X >= -1 && point.Y >= -1
                && point.X + control.Bounds.Width <= dialog.ClientSize.Width + 1
                && point.Y + control.Bounds.Height <= dialog.ClientSize.Height + 1;
        }
        bool FrameFits()
        {
            var screen = dialog.Screens?.ScreenFromWindow(dialog);
            if (screen is null || dialog.FrameSize is not { } frame) return false;
            var area = screen.WorkingArea;
            return dialog.Position.X >= area.X - 1 && dialog.Position.Y >= area.Y - 1
                && dialog.Position.X + frame.Width * dialog.RenderScaling <= area.Right + 1
                && dialog.Position.Y + frame.Height * dialog.RenderScaling <= area.Bottom + 1;
        }
        try
        {
            await Settle();
            Check("opens the real owned dialog", dialog.IsVisible && ReferenceEquals(dialog.Owner, shell)
                && lifetime.Windows.Contains(dialog));
            var screen = dialog.Screens?.ScreenFromWindow(dialog)
                ?? throw new InvalidOperationException("The settings dialog has no actual screen.");
            var frame = dialog.FrameSize ?? throw new InvalidOperationException("The settings dialog has no native frame.");
            var availableHeight = screen.WorkingArea.Height / dialog.RenderScaling - (frame.Height - dialog.ClientSize.Height);
            Check("default frame stays within the current working area", FrameFits());
            Check("normal 620-DIP height is preserved when space permits",
                availableHeight < 620 || Math.Abs(dialog.ClientSize.Height - 620) <= 1);
            Check("minimum height permits the actual client viewport", dialog.MinHeight <= dialog.MaxHeight
                && dialog.MinHeight <= availableHeight + 1 && dialog.ClientSize.Height <= availableHeight + 1);

            var buttons = dialog.GetVisualDescendants().OfType<Button>().ToArray();
            var cancel = buttons.Single(button => button.IsCancel);
            var save = buttons.Single(button => Equals(button.Content, Strings.Get("BtnSaveSettings")));
            var editor = dialog.FindControl<TextBox>("ArchiveSaltBox")!;
            var body = editor.GetVisualAncestors().OfType<ScrollViewer>().First();
            Check("both footer buttons are visible inside the client", InsideClient(cancel) && InsideClient(save));
            Check("footer remains outside the scrolling body", !cancel.GetVisualAncestors().Contains(body)
                && !save.GetVisualAncestors().Contains(body));

            dialog.Height = screen.WorkingArea.Height / dialog.RenderScaling + 300;
            await Settle();
            Check("an excessive requested height cannot extend the actual frame beyond the screen", FrameFits());
            Check("footer remains reachable after the excessive request", InsideClient(cancel) && InsideClient(save));

            dialog.Height = Math.Min(480, dialog.MaxHeight);
            await Settle();
            Check("the compact client uses a nonempty vertical scroll viewport", body.Viewport.Height > 0
                && body.Extent.Height > body.Viewport.Height);
            editor.Focus();
            editor.BringIntoView();
            await Settle();
            var editorPosition = editor.TranslatePoint(default, body);
            Check("the final editor can be brought into the real scroll viewport", editor.IsFocused
                && editorPosition is { } position && position.Y >= -1
                && position.Y + editor.Bounds.Height <= body.Viewport.Height + 1);
            Check("scrolling keeps Cancel and Save inside the client", InsideClient(cancel) && InsideClient(save) && FrameFits());

            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("Cancel closes without applying settings", !await closed.WaitAsync(TimeSpan.FromSeconds(5))
                && !dialog.IsVisible && JsonSerializer.Serialize(settings) == before);
            Check("no project or settings file was changed", ReferenceEquals(shell.ViewModel.Project, project)
                && (project is null ? shell.ViewModel.Project is null : Knxproj.ToXml(project).ToString() == projectBefore)
                && shell.ViewModel.CanUndo == canUndo
                && (savedBefore is null ? !File.Exists(settingsPath)
                    : File.Exists(settingsPath) && File.ReadAllBytes(settingsPath).AsSpan().SequenceEqual(savedBefore)));
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close(false);
            await closed.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}

using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Knx.App;
using Knx.App.Localization;
using Knx.App.Views;

internal static class DialogChromeChecks
{
    // Called by the parent dialog runner on the existing native UI thread, in each theme/language.
    internal static async Task Run(WorkbenchWindow owner, string theme, Action<string, bool> check)
    {
        void Check(string name, bool ok) => check(theme + " dialog chrome: " + name, ok);
        static T Named<T>(Window window, string name) where T : Control =>
            window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var nativeFlag = Environment.GetCommandLineArgs().Any(arg => arg.Equals("--native-titlebar", StringComparison.OrdinalIgnoreCase))
            || Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_TITLEBAR") is "1" or "true";
        var custom = OperatingSystem.IsWindows() && !nativeFlag;

        // Use the actual generic-dialog factory: its callers assign Content after creation.
        var dialog = (Window)typeof(WorkbenchWindow).GetMethod("CreateDialog", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(owner, ["Caption probe", 460d, 300d])!;
        var input = new TextBox { Text = "keep this draft", Classes = { "wb-field" } };
        var applyCount = 0;
        var apply = new Button { Content = "Apply", IsDefault = true };
        apply.Click += (_, _) => applyCount++;
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        var footer = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { cancel, apply } };
        var content = new Grid { RowDefinitions = new("*,Auto") };
        content.Children.Add(input);
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);
        dialog.Content = content;
        WorkbenchDialogChrome.Apply(dialog); // A feature may opt in even when its factory already did.
        dialog.Opened += (_, _) => input.Focus();
        var completed = dialog.ShowDialog(owner);
        try
        {
            await Task.Delay(100);
            Check("keeps owner and existing initial editor focus", ReferenceEquals(dialog.Owner, owner)
                && ReferenceEquals(dialog.FocusManager!.GetFocusedElement(), input));
            Check("preserves draft, default action and cancel action", input.Text == "keep this draft"
                && apply.IsDefault && cancel.IsCancel && applyCount == 0);
            if (!custom)
            {
                Check("platform/explicit fallback keeps native decorations", !dialog.Classes.Contains("wb-dialog-chrome")
                    && !dialog.ExtendClientAreaToDecorationsHint && ReferenceEquals(dialog.Content, content));
                return;
            }

            var header = Named<Border>(dialog, "DialogChromeHeader");
            var close = Named<Button>(dialog, "DialogChromeClose");
            var title = Named<TextBlock>(dialog, "DialogChromeTitle");
            var presenter = Named<ContentPresenter>(dialog, "DialogChromeContent");
            Check("one app caption replaces the native caption while preserving the frame",
                dialog.Classes.Contains("wb-dialog-chrome") && dialog.IsExtendedIntoWindowDecorations
                && dialog.WindowDecorations == WindowDecorations.Full && dialog.WindowDecorationsTheme is not null
                && Math.Abs(header.Bounds.Height - WorkbenchDialogChrome.CaptionHeight) < 0.5
                && dialog.GetVisualDescendants().OfType<Border>().Count(border => border.Name == "DialogChromeHeader") == 1);
            Check("keeps the original content instance below the caption", ReferenceEquals(presenter.Content, content)
                && content.TranslatePoint(default, dialog) is { Y: >= WorkbenchDialogChrome.CaptionHeight });
            Check("caption moves the window while its close and content remain client controls",
                WindowDecorationProperties.GetElementRole(header) == WindowDecorationsElementRole.TitleBar
                && WindowDecorationProperties.GetElementRole(close) == WindowDecorationsElementRole.User
                && WindowDecorationProperties.GetElementRole(presenter) == WindowDecorationsElementRole.User);
            Check("close is a localized, focusable UIA button without default/cancel side effects",
                close.Focusable && !close.IsDefault && !close.IsCancel
                && AutomationProperties.GetAutomationId(close) == "DialogChromeClose"
                && ControlAutomationPeer.CreatePeerForElement(close)?.GetName() == WorkbenchStrings.Get("WindowClose"));
            dialog.Title = new string('W', 180);
            await Task.Delay(40);
            Check("live long titles trim without displacing the close button", title.Text == dialog.Title
                && title.TextTrimming == TextTrimming.CharacterEllipsis
                && close.TranslatePoint(default, dialog) is { } closeOrigin
                && closeOrigin.X >= 0 && closeOrigin.X + close.Bounds.Width <= dialog.ClientSize.Width + 1);

            foreach (var (background, foreground, selected, selectedText) in new[]
            {
                (Colors.Black, Colors.White, Colors.Yellow, Colors.Black),
                (Colors.White, Colors.Black, Colors.Navy, Colors.White),
            })
            {
                // Local synthetic palette, no OS preference or application resource changes.
                foreach (var key in new[] { "WbPaper", "WbBackground" }) dialog.Resources[key] = new SolidColorBrush(background);
                foreach (var key in new[] { "WbText", "WbMuted", "WbLine", "WbAccent" }) dialog.Resources[key] = new SolidColorBrush(foreground);
                dialog.Resources["WbSelected"] = new SolidColorBrush(selected);
                dialog.Resources["WbSelectedText"] = new SolidColorBrush(selectedText);
                dialog.Classes.Add("wb-high-contrast");
                foreach (var state in new[] { "normal", "pointerover", "pressed", "focus-visible" })
                {
                    ((IPseudoClasses)close.Classes).Set(":pointerover", state == "pointerover");
                    ((IPseudoClasses)close.Classes).Set(":pressed", state == "pressed");
                    ((IPseudoClasses)close.Classes).Set(":focus-visible", state == "focus-visible");
                    dialog.UpdateLayout();
                    var active = state is "pointerover" or "pressed";
                    var border = close.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "CaptionBorder");
                    Check("contrast retains complete caption/close color pairs in " + state,
                        ColorIs(header.Background, background) && ColorIs(title.Foreground, foreground)
                        && ColorIs(close.Foreground, active ? selectedText : foreground)
                        && (!active || ColorIs(border.Background, selected)));
                }
            }
            ((IPseudoClasses)close.Classes).Set(":pointerover", false);
            ((IPseudoClasses)close.Classes).Set(":pressed", false);
            ((IPseudoClasses)close.Classes).Set(":focus-visible", false);

            var replacement = new TextBox { Text = "replacement content" };
            dialog.Content = replacement;
            await Task.Delay(40);
            Check("later content replacement preserves the caption and exact replacement control",
                ReferenceEquals(Named<ContentPresenter>(dialog, "DialogChromeContent").Content, replacement)
                && dialog.GetVisualDescendants().OfType<Button>().Count(button => button.Name == "DialogChromeClose") == 1);
            Click(close);
            await completed;
            Check("caption close dismisses without invoking the default action", !dialog.IsVisible && applyCount == 0);
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            await completed;
        }

        if (custom)
        {
            foreach (var typed in new Window[] { new NewProjectWindow(), new CompareWindow(), new ReportWindow(),
                         new ReplaceProductWindow(), new HelpWindow() })
            {
                var shown = typed.ShowDialog(owner);
                try
                {
                    await Task.Delay(50);
                    Check(typed.GetType().Name + " uses the shared, reachable caption",
                        typed.Classes.Contains("wb-dialog-chrome")
                        && Named<Border>(typed, "DialogChromeHeader").IsEffectivelyVisible
                        && Named<Button>(typed, "DialogChromeClose").Bounds.Width == 46);
                    Click(Named<Button>(typed, "DialogChromeClose"));
                }
                finally { if (typed.IsVisible) typed.Close(); await shown; }
            }
            var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            var confirmation = ConfirmDialog.AskDeleteAsync(owner, "synthetic item");
            await Task.Delay(50);
            var confirm = lifetime.Windows.Single(window => window is ConfirmDialog && window.IsVisible);
            Check("confirmation preserves size-to-content and the non-resizable native frame",
                !confirm.CanResize && confirm.SizeToContent == SizeToContent.Height
                && confirm.Classes.Contains("wb-dialog-chrome"));
            Click(Named<Button>(confirm, "DialogChromeClose"));
            Check("closing a destructive confirmation is cancellation", !await confirmation);
        }

        // Explicit rollback must be decided before adding any wrapper/theme to a new window.
        var before = Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_TITLEBAR");
        var fallbackContent = new TextBox { Text = "native fallback" };
        var fallback = new Window { Content = fallbackContent };
        try
        {
            Environment.SetEnvironmentVariable("BUSCHORD_NATIVE_TITLEBAR", "1");
            WorkbenchDialogChrome.Apply(fallback);
            Check("native-titlebar rollback leaves content and decorations untouched",
                ReferenceEquals(fallback.Content, fallbackContent) && !fallback.ExtendClientAreaToDecorationsHint
                && fallback.WindowDecorationsTheme is null && !fallback.Classes.Contains("wb-dialog-chrome"));
        }
        finally { Environment.SetEnvironmentVariable("BUSCHORD_NATIVE_TITLEBAR", before); fallback.Close(); }
    }

    private static bool ColorIs(IBrush? brush, Color expected) => brush is ISolidColorBrush solid && solid.Color == expected;
}

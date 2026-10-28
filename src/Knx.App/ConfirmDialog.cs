using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Knx;
using Knx.App.Localization;

namespace Knx.App;

/// <summary>Answer of a <see cref="ConfirmDialog"/>; Cancel covers the window close button.</summary>
public enum ConfirmChoice
{
    Save,
    Discard,
    Yes,
    No,
    Cancel,
}

/// <summary>
/// Code-only dialog for the three questions that can lose data: save before replacing a
/// project, delete confirmation and a plain message. One window type keeps the XAML file
/// count down; it uses the shared V4 dialog chrome and button classes.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private enum ButtonKind
    {
        Quiet,
        Primary,
        Danger,
    }

    private ConfirmChoice? _chosen;

    private ConfirmDialog(string title, string message,
        params (string Text, ConfirmChoice Choice, ButtonKind Kind)[] buttons)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("wb-dialog");

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        var header = new Border
        {
            Padding = new Thickness(22, 20, 22, 8),
            Child = new TextBlock
            {
                Text = title,
                TextWrapping = TextWrapping.Wrap,
                Classes = { "wb-heading" },
            },
        };
        root.Children.Add(header);

        var body = new Border
        {
            Classes = { "wb-dialog-surface" },
            Margin = new Thickness(22, 0, 22, 18),
            Padding = new Thickness(18),
            Child = new ScrollViewer
            {
                MaxHeight = 360,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Classes = { "wb-text" },
                },
            },
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var built = new List<Button>();
        foreach (var (text, choice, kind) in buttons)
        {
            var button = new Button
            {
                Content = text,
                IsCancel = choice is ConfirmChoice.Cancel or ConfirmChoice.No,
            };
            button.Classes.Add("wb-button");
            button.Classes.Add(kind switch
            {
                ButtonKind.Primary => "wb-primary",
                ButtonKind.Danger => "wb-danger",
                _ => "wb-quiet",
            });
            button.Click += (_, _) => Choose(choice);
            row.Children.Add(button);
            built.Add(button);
        }

        // The safe path owns Enter and initial focus: the primary action when there is one,
        // otherwise the quiet cancel. A destructive button is never the default.
        var defaultIndex = Array.FindIndex(buttons, b => b.Kind == ButtonKind.Primary);
        if (defaultIndex < 0)
            defaultIndex = Array.FindIndex(buttons, b => b.Kind != ButtonKind.Danger);
        if (defaultIndex < 0) defaultIndex = 0;
        var defaultButton = built[defaultIndex];
        defaultButton.IsDefault = true;

        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = row };
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;
        WorkbenchDialogChrome.Apply(this);

        var escapeChoice = buttons.Any(b => b.Choice == ConfirmChoice.Cancel)
            ? ConfirmChoice.Cancel
            : buttons.FirstOrDefault().Choice;
        Opened += (_, _) => defaultButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Choose(escapeChoice);
            e.Handled = true;
        };
        Closed += (_, _) => _chosen ??= ConfirmChoice.Cancel;
    }

    private void Choose(ConfirmChoice choice)
    {
        _chosen = choice;
        Close();
    }

    private static async Task<ConfirmChoice> ShowAsync(Window owner, string title, string message,
        params (string Text, ConfirmChoice Choice, ButtonKind Kind)[] buttons)
    {
        var dialog = new ConfirmDialog(title, message, buttons);
        await dialog.ShowDialog(owner);
        return dialog._chosen ?? ConfirmChoice.Cancel;
    }

    /// <summary>Save / Discard / Cancel before the current project is replaced or the app quits.</summary>
    public static Task<ConfirmChoice> AskUnsavedAsync(Window owner, string project)
        => ShowAsync(owner, Wb("AskUnsavedTitle"), Wb("AskUnsavedText", project),
            (Wb("DoNotSave"), ConfirmChoice.Discard, ButtonKind.Danger),
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, ButtonKind.Quiet),
            (Wb("Save"), ConfirmChoice.Save, ButtonKind.Primary));

    /// <summary>Destructive demo reset; never touches a native project without explicit consent.</summary>
    public static async Task<bool> AskDemoResetAsync(Window owner)
        => await ShowAsync(owner, Wb("AskDemoResetTitle"), Wb("AskDemoResetText"),
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, ButtonKind.Quiet),
            (Wb("Reset"), ConfirmChoice.Yes, ButtonKind.Danger)) == ConfirmChoice.Yes;

    /// <summary>Restoring a backup never overwrites the original without consent.</summary>
    public static async Task<bool> AskRestoreAsync(Window owner)
        => await ShowAsync(owner, Wb("AskRestoreTitle"), Wb("AskRestoreText"),
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, ButtonKind.Quiet),
            (Wb("Restore"), ConfirmChoice.Yes, ButtonKind.Primary)) == ConfirmChoice.Yes;

    /// <summary>Draft consent is separate from project Save and never applies the editor.</summary>
    public static async Task<bool> AskDiscardDraftsAsync(Window owner)
        => await ShowAsync(owner, Wb("AskDiscardTitle"), Wb("AskDiscardText"),
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, ButtonKind.Quiet),
            (Wb("DiscardDrafts"), ConfirmChoice.Discard, ButtonKind.Danger)) == ConfirmChoice.Discard;

    /// <summary>Yes / No before a destructive edit (settings: "ask before deleting").</summary>
    public static async Task<bool> AskDeleteAsync(Window owner, string subject)
        => await ShowAsync(owner, Wb("AskDeleteTitle"), Wb("AskDeleteText", subject),
            (Strings.Get("BtnCancel"), ConfirmChoice.No, ButtonKind.Quiet),
            (Wb("Delete"), ConfirmChoice.Yes, ButtonKind.Danger)) == ConfirmChoice.Yes;

    private static string Wb(string key, params object[] args) => WorkbenchStrings.Get(key, args);

    /// <summary>Single-button message dialog for errors that must not disappear with the status line.</summary>
    public static Task ShowMessageAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message, (Strings.Get("BtnClose"), ConfirmChoice.Yes, ButtonKind.Primary));
}

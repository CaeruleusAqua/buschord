using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Knx;

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
/// count down; it reuses the application's Window/Button styles and resources.
/// </summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmChoice? _chosen;

    private ConfirmDialog(string title, string message,
        params (string Text, ConfirmChoice Choice, bool Primary)[] buttons)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Margin = new Thickness(16), Spacing = 14 };
        body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        foreach (var (text, choice, primary) in buttons)
        {
            var button = new Button
            {
                Content = text,
                IsDefault = primary,
                IsCancel = choice is ConfirmChoice.Cancel or ConfirmChoice.No,
            };
            if (primary) button.Classes.Add("primary");
            button.Click += (_, _) => Choose(choice);
            row.Children.Add(button);
        }
        body.Children.Add(row);
        Content = body;
        var first = row.Children.OfType<Button>().FirstOrDefault();
        Opened += (_, _) => first?.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            var cancel = row.Children.OfType<Button>().FirstOrDefault(b => b.IsCancel);
            (cancel ?? first)?.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
        params (string Text, ConfirmChoice Choice, bool Primary)[] buttons)
    {
        var dialog = new ConfirmDialog(title, message, buttons);
        await dialog.ShowDialog(owner);
        return dialog._chosen ?? ConfirmChoice.Cancel;
    }

    /// <summary>Save / Discard / Cancel before the current project is replaced or the app quits.</summary>
    public static Task<ConfirmChoice> AskUnsavedAsync(Window owner, string project)
        => ShowAsync(owner, "Ungespeicherte Änderungen",
            $"Das Projekt „{project}“ enthält ungespeicherte Änderungen.\n\nVor dem Fortfahren speichern?",
            (Strings.Get("BtnSave"), ConfirmChoice.Save, true),
            ("Nicht speichern", ConfirmChoice.Discard, false),
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, false));

    /// <summary>Destructive demo reset; never touches a native project without explicit consent.</summary>
    public static async Task<bool> AskDemoResetAsync(Window owner)
        => await ShowAsync(owner, "Demo zurücksetzen",
            "Der aktuelle Demo-Stand wird verworfen und das ursprüngliche Demo-Projekt geladen.\n\nFortfahren?",
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, true),
            ("Zurücksetzen", ConfirmChoice.Yes, false)) == ConfirmChoice.Yes;

    /// <summary>Draft consent is separate from project Save and never applies the editor.</summary>
    public static async Task<bool> AskDiscardDraftsAsync(Window owner)
        => await ShowAsync(owner, "Nicht übernommene Parameter",
            "Parameterentwürfe wurden noch nicht übernommen. Projekt speichern speichert diese Entwürfe nicht.\n\nEntwürfe verwerfen und fortfahren?",
            (Strings.Get("BtnCancel"), ConfirmChoice.Cancel, true),
            ("Entwürfe verwerfen", ConfirmChoice.Discard, false)) == ConfirmChoice.Discard;

    /// <summary>Yes / No before a destructive edit (settings: "ask before deleting").</summary>
    public static async Task<bool> AskDeleteAsync(Window owner, string subject)
        => await ShowAsync(owner, "Löschen bestätigen", $"{subject} wirklich löschen?",
            ("Löschen", ConfirmChoice.Yes, false),
            (Strings.Get("BtnCancel"), ConfirmChoice.No, true)) == ConfirmChoice.Yes;

    /// <summary>Single-button message dialog for errors that must not disappear with the status line.</summary>
    public static Task ShowMessageAsync(Window owner, string title, string message)
        => ShowAsync(owner, title, message, (Strings.Get("BtnClose"), ConfirmChoice.Yes, true));
}

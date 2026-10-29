using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Automation;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Knx.Bus.Security;

namespace Knx.App.Services;

/// <summary>Ephemeral masked input; the returned owned character buffer belongs to the importer.</summary>
public sealed class PromptSecurePasswordSource(Window fallbackOwner, string? explanation = null, string? confirmation = null) : ISecurePasswordSource
{
    public ValueTask<char[]> ReadOwnedAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<char[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(async () =>
        {
            try { completion.TrySetResult(await PromptAsync(token)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch { completion.TrySetException(new IOException("Secure credential input failed.")); }
        });
        return new(completion.Task);
    }
    private async Task<char[]> PromptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
            .LastOrDefault(window => window.IsVisible && window.IsActive) ?? fallbackOwner;
        var input = new TextBox { PasswordChar = '●', Classes = { "wb-field" } };
        AutomationProperties.SetName(input, Localization.SecureConnectionStrings.Get("Password"));
        var dialog = new Window { Title = Localization.SecureConnectionStrings.Get("Password"), Width = 460, Height = explanation is null ? 260 : 300, MinWidth = 350, MinHeight = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "wb-dialog" } };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = explanation ?? Localization.SecureConnectionStrings.Get("PasswordPrompt"), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        char[]? result = null;
        var cancel = new Button { Content = Strings.Get("BtnCancel"), IsCancel = true, Classes = { "wb-button", "wb-quiet" } };
        var confirm = new Button { Content = confirmation ?? Localization.WorkbenchStrings.Get("Open"), IsDefault = true, Classes = { "wb-button", "wb-primary" } };
        cancel.Click += (_, _) => dialog.Close(); confirm.Click += (_, _) => { result = (input.Text ?? "").ToCharArray(); dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(confirm);
        var root = new Grid { RowDefinitions = new("*,Auto") }; root.Children.Add(new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        var footer = new Border { Classes = { "wb-dialog-footer" }, Child = actions }; Grid.SetRow(footer, 1); root.Children.Add(footer); dialog.Content = root;
        dialog.Opened += (_, _) => input.Focus(); WorkbenchDialogChrome.Apply(dialog);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        char[]? owned = null;
        try
        {
            await dialog.ShowDialog(owner);
            if (token.IsCancellationRequested || result is null) throw new OperationCanceledException(token);
            owned = result; result = null;
        }
        finally
        {
            try { input.Text = ""; }
            catch { if (owned is not null) Array.Clear(owned); throw; }
            finally { if (result is not null) Array.Clear(result); }
        }
        return owned;
    }
    public override string ToString() => "[masked secure credential input]";
}

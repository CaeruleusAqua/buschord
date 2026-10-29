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
public sealed class PromptSecurePasswordSource(Window fallbackOwner) : ISecurePasswordSource
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
        var dialog = new Window { Title = Localization.SecureConnectionStrings.Get("Password"), Width = 460, Height = 230, MinWidth = 350, MinHeight = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Classes = { "wb-dialog" } };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Localization.SecureConnectionStrings.Get("PasswordPrompt"), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        char[]? result = null;
        var cancel = new Button { Content = Strings.Get("BtnCancel"), IsCancel = true, Classes = { "wb-button", "wb-quiet" } };
        var confirm = new Button { Content = Localization.WorkbenchStrings.Get("Open"), IsDefault = true, Classes = { "wb-button", "wb-primary" } };
        cancel.Click += (_, _) => dialog.Close(); confirm.Click += (_, _) => { result = (input.Text ?? "").ToCharArray(); dialog.Close(); };
        actions.Children.Add(cancel); actions.Children.Add(confirm); panel.Children.Add(actions); dialog.Content = panel;
        dialog.Opened += (_, _) => input.Focus(); WorkbenchDialogChrome.Apply(dialog);
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Close));
        try
        {
            await dialog.ShowDialog(owner);
            if (token.IsCancellationRequested || result is null) { if (result is not null) Array.Clear(result); throw new OperationCanceledException(token); }
            return result;
        }
        finally { input.Text = ""; }
    }
    public override string ToString() => "[masked secure credential input]";
}

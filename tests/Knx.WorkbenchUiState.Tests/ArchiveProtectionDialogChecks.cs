using System.Text.Json;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Knx;
using Knx.App;

internal static class ArchiveProtectionDialogChecks
{
    internal static async Task Run(WorkbenchWindow shell, string theme, Action<string, bool> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "buschord-protection-dialog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "archive-protection.json");
        var settingsPath = Path.Combine(root, "settings.json");
        var oldProtection = Environment.GetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION");
        var oldSettings = Environment.GetEnvironmentVariable("BUSCHORD_SETTINGS");
        var project = shell.ViewModel.Project!;
        var before = Knxproj.ToXml(project).ToString();
        var undo = shell.ViewModel.CanUndo;
        var dialogs = new List<(SettingsWindow Dialog, Task<bool> Closed)>();
        void Check(string name, bool value) => check(theme + " archive protection: " + name, value);
        AppSettings Settings() => new() { Theme = theme == "light" ? "Light" : "Dark", Language = Strings.Language };
        static async Task Settle()
        {
            await Task.Delay(80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        }
        async Task<(SettingsWindow Dialog, Task<bool> Closed)> Open(AppSettings settings)
        {
            var dialog = new SettingsWindow(settings) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var pair = (dialog, dialog.ShowDialog<bool>(shell));
            dialogs.Add(pair);
            await Settle();
            return pair;
        }
        static TextBox Salt(SettingsWindow dialog) => dialog.FindControl<TextBox>("ArchiveSaltBox")!;
        static TextBlock Error(SettingsWindow dialog) => dialog.FindControl<TextBlock>("SettingsErrorText")!;
        static void Click(SettingsWindow dialog, bool save) => dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => save ? Equals(button.Content, Strings.Get("BtnSaveSettings")) : button.IsCancel)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static bool BytesEqual(string file, byte[] bytes) => File.Exists(file) && File.ReadAllBytes(file).AsSpan().SequenceEqual(bytes);
        try
        {
            Environment.SetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION", path);
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settingsPath);
            var missingSettings = Settings();
            var missing = await Open(missingSettings);
            Check("missing profile shows masked built-in defaults without creating files",
                Salt(missing.Dialog).Text == ArchiveProtectionSettings.Load().Salt && !string.IsNullOrEmpty(Salt(missing.Dialog).Text)
                && Salt(missing.Dialog).PasswordChar != '\0'
                && !Salt(missing.Dialog).RevealPassword && !File.Exists(path) && !File.Exists(settingsPath));
            Salt(missing.Dialog).Text = Guid.NewGuid().ToString("N");
            Click(missing.Dialog, save: false);
            Check("Cancel on a missing profile writes neither settings file", !await missing.Closed
                && !File.Exists(path) && !File.Exists(settingsPath));

            var initial = " " + Guid.NewGuid().ToString("N") + " ";
            var profile = new ArchiveProtectionSettings { Salt = initial, Iterations = 123457, HashAlgorithm = "SHA384", KeyLength = 48, PasswordEncoding = "utf-16le" };
            profile.Save(path);
            var original = File.ReadAllBytes(path);
            var settings = Settings();
            var settingsBefore = JsonSerializer.Serialize(settings);
            var cancel = await Open(settings);
            var field = Salt(cancel.Dialog);
            field.BringIntoView();
            field.Focus(NavigationMethod.Tab);
            await Settle();
            var peer = ControlAutomationPeer.CreatePeerForElement(field);
            Check("actual focused salt editor has its visible localized label", field.IsFocused
                && peer?.GetName() == Strings.Get("OptionsArchiveProtectionSalt")
                && cancel.Dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == Strings.Get("OptionsArchiveProtectionSalt")));
            Check("loaded salt preserves exact surrounding whitespace and is masked",
                field.Text == initial && field.PasswordChar != '\0' && !field.RevealPassword);
            Check("the scoped editor keeps the real TextBox template", field.GetVisualDescendants().OfType<TextPresenter>().Any()
                && field.Bounds.Width > 0 && field.Bounds.Height > 0);
            var exposesValue = false;
            try { exposesValue = peer?.GetProvider<IValueProvider>()?.Value?.Contains(initial, StringComparison.Ordinal) == true; }
            catch (InvalidOperationException) { }
            Check("password editor does not expose its salt through the value provider", !exposesValue);
            Check("salt peer suppresses child text peers and text providers", peer is not null && peer.GetChildren().Count == 0
                && !peer.GetType().GetInterfaces().Any(type => type.Name.Contains("TextProvider", StringComparison.Ordinal)));
            Check("salt peer retains edit semantics keyboard focus and enabled state", peer is not null
                && peer.GetAutomationControlType() == AutomationControlType.Edit && peer.HasKeyboardFocus()
                && peer.IsEnabled() && peer.IsKeyboardFocusable());
            field.Text = Guid.NewGuid().ToString("N");
            Click(cancel.Dialog, save: false);
            Check("Cancel preserves the complete existing profile and preferences", !await cancel.Closed
                && BytesEqual(path, original) && JsonSerializer.Serialize(settings) == settingsBefore && !File.Exists(settingsPath));

            var savedSettings = Settings();
            var save = await Open(savedSettings);
            var replacement = "  " + Guid.NewGuid().ToString("N") + " ";
            Salt(save.Dialog).Text = replacement;
            // Another writer may update non-UI profile properties while this dialog is open.
            new ArchiveProtectionSettings { Salt = initial, Iterations = 234567, HashAlgorithm = "SHA512", KeyLength = 64, PasswordEncoding = "utf-8" }.Save(path);
            Click(save.Dialog, save: true);
            Check("Save closes successfully", await save.Closed.WaitAsync(TimeSpan.FromSeconds(5)) && !save.Dialog.IsVisible);
            var saved = ArchiveProtectionSettings.Load(path);
            Check("Save preserves the exact edited salt without trimming", saved.Salt == replacement);
            Check("Save preserves the latest independent profile fields", saved.Iterations == 234567 && saved.HashAlgorithm == "SHA512"
                && saved.KeyLength == 64 && saved.PasswordEncoding == "utf-8");
            Check("salt stays outside ordinary application preferences", File.Exists(settingsPath)
                && !File.ReadAllText(settingsPath).Contains(replacement, StringComparison.Ordinal)
                && File.ReadAllText(settingsPath).IndexOf("salt", StringComparison.OrdinalIgnoreCase) < 0);
            var reopened = await Open(Settings());
            Check("reopening reads the persisted exact profile value", Salt(reopened.Dialog).Text == replacement);
            Click(reopened.Dialog, save: false); await reopened.Closed;

            foreach (var empty in new string?[] { null, "" })
            {
                new ArchiveProtectionSettings { Salt = empty }.Save(path);
                var unchanged = await Open(Settings());
                Click(unchanged.Dialog, save: true);
                Check("unchanged blank profile retains its null or empty representation", await unchanged.Closed
                    && ArchiveProtectionSettings.Load(path).Salt == empty);
            }

            var malformed = "{";
            File.WriteAllText(path, malformed);
            var invalidSettings = Settings();
            var invalidBefore = JsonSerializer.Serialize(invalidSettings);
            var invalid = await Open(invalidSettings);
            Check("invalid profile opens a controlled localized error", invalid.Dialog.IsVisible && !Salt(invalid.Dialog).IsEnabled
                && Error(invalid.Dialog).IsVisible && Error(invalid.Dialog).Text == Strings.Get("OptionsArchiveProtectionLoadError"));
            var settingsBytes = File.ReadAllBytes(settingsPath);
            Click(invalid.Dialog, save: true);
            await Settle();
            Check("invalid profile cannot be silently replaced by Save", invalid.Dialog.IsVisible && File.ReadAllText(path) == malformed
                && JsonSerializer.Serialize(invalidSettings) == invalidBefore && BytesEqual(settingsPath, settingsBytes));
            Click(invalid.Dialog, save: false);
            Check("Cancel after a load error preserves invalid bytes", !await invalid.Closed && File.ReadAllText(path) == malformed);

            profile.Save(path);
            var changedSettings = Settings();
            var changedBefore = JsonSerializer.Serialize(changedSettings);
            var changed = await Open(changedSettings);
            Salt(changed.Dialog).Text = Guid.NewGuid().ToString("N");
            File.WriteAllText(path, malformed);
            Click(changed.Dialog, save: true);
            await Settle();
            Check("a newly invalid profile is refused without replacing its bytes", changed.Dialog.IsVisible && File.ReadAllText(path) == malformed
                && Error(changed.Dialog).Text == Strings.Get("OptionsSaveError") && JsonSerializer.Serialize(changedSettings) == changedBefore);
            Check("error messages contain neither loaded nor edited profile values", !Error(changed.Dialog).Text!.Contains(initial, StringComparison.Ordinal)
                && !Error(changed.Dialog).Text!.Contains(Salt(changed.Dialog).Text!, StringComparison.Ordinal));
            Click(changed.Dialog, save: false); await changed.Closed;

            profile.Save(path); var validBytes = File.ReadAllBytes(path);
            var failedSettings = Settings(); var failedBefore = JsonSerializer.Serialize(failedSettings);
            var failed = await Open(failedSettings);
            var blocker = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blocker, "{}");
            Environment.SetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION", Path.Combine(blocker, "profile.json"));
            Click(failed.Dialog, save: true);
            await Settle();
            Check("profile save I/O failure remains in the dialog with a generic error", failed.Dialog.IsVisible
                && Error(failed.Dialog).IsVisible && Error(failed.Dialog).Text == Strings.Get("OptionsSaveError"));
            Check("profile save failure preserves prior profile and preferences", BytesEqual(path, validBytes)
                && JsonSerializer.Serialize(failedSettings) == failedBefore && File.ReadAllText(blocker) == "{}");
            Click(failed.Dialog, save: false); await failed.Closed;

            Environment.SetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION", path);
            var preferencesBytes = File.ReadAllBytes(settingsPath);
            var preferencesBlocker = Path.Combine(root, "preferences-blocker");
            File.WriteAllText(preferencesBlocker, "{}");
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", Path.Combine(preferencesBlocker, "settings.json"));
            var blockedSettings = Settings(); var blockedBefore = JsonSerializer.Serialize(blockedSettings);
            var blocked = await Open(blockedSettings);
            Salt(blocked.Dialog).Text = Guid.NewGuid().ToString("N");
            blocked.Dialog.FindControl<ComboBox>("ThemeBox")!.SelectedIndex = theme == "light" ? 2 : 1;
            blocked.Dialog.FindControl<TextBox>("BusInterfaceBox")!.Text = "edited interface";
            Click(blocked.Dialog, save: true);
            await Settle();
            Check("blocked ordinary settings target does not partially save the archive profile", blocked.Dialog.IsVisible
                && Error(blocked.Dialog).Text == Strings.Get("OptionsSaveError") && BytesEqual(path, validBytes)
                && BytesEqual(settingsPath, preferencesBytes) && JsonSerializer.Serialize(blockedSettings) == blockedBefore);
            Click(blocked.Dialog, save: false);
            Check("Cancel after an ordinary settings failure leaves both prior files and live preferences unchanged", !await blocked.Closed
                && BytesEqual(path, validBytes) && BytesEqual(settingsPath, preferencesBytes)
                && JsonSerializer.Serialize(blockedSettings) == blockedBefore && File.ReadAllText(preferencesBlocker) == "{}");

            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", settingsPath);
            if (OperatingSystem.IsWindows())
            {
                var rollbackSettings = Settings(); var rollbackBefore = JsonSerializer.Serialize(rollbackSettings);
                var rollback = await Open(rollbackSettings);
                Salt(rollback.Dialog).Text = Guid.NewGuid().ToString("N");
                rollback.Dialog.FindControl<ComboBox>("ThemeBox")!.SelectedIndex = theme == "light" ? 2 : 1;
                // Reads and staging remain possible; replacing the profile is denied.
                using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Click(rollback.Dialog, save: true);
                    await Settle();
                    Check("second-file replacement failure restores the already committed ordinary preferences", rollback.Dialog.IsVisible
                        && Error(rollback.Dialog).Text == Strings.Get("OptionsSaveError") && BytesEqual(path, validBytes)
                        && BytesEqual(settingsPath, preferencesBytes) && JsonSerializer.Serialize(rollbackSettings) == rollbackBefore);
                }
                Click(rollback.Dialog, save: false);
                Check("Cancel after rollback keeps both complete original files", !await rollback.Closed
                    && BytesEqual(path, validBytes) && BytesEqual(settingsPath, preferencesBytes)
                    && JsonSerializer.Serialize(rollbackSettings) == rollbackBefore);
            }
            Check("successful rollback leaves no transaction staging or backup files", !Directory.EnumerateFiles(root, ".buschord-settings-*").Any());
            Check("dialog operations preserve the open project and history", ReferenceEquals(shell.ViewModel.Project, project)
                && Knxproj.ToXml(project).ToString() == before && shell.ViewModel.CanUndo == undo);
        }
        finally
        {
            foreach (var (dialog, closed) in dialogs)
            {
                if (dialog.IsVisible) dialog.Close(false);
                await closed.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Environment.SetEnvironmentVariable("BUSCHORD_ARCHIVE_PROTECTION", oldProtection);
            Environment.SetEnvironmentVariable("BUSCHORD_SETTINGS", oldSettings);
        }
    }
}

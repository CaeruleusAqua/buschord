using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Knx.App;

internal static class HighContrastChecks
{
    internal static (int Checks, int Failures) Run(Window shell)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var application = Application.Current!;
        var type = typeof(App).Assembly.GetType("Knx.App.Workbench.WorkbenchHighContrast")!;
        var controller = type.GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var apply = type.GetMethod("Apply", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Color ColorAt(Window window, string key)
        {
            window.TryFindResource(key, window.ActualThemeVariant, out var value);
            return ((ISolidColorBrush)value!).Color;
        }
        var baselineText = ColorAt(shell, "WbText");
        var baselinePaper = ColorAt(shell, "WbPaper");
        var unrelatedKey = new object();
        application.Resources[unrelatedKey] = "keep";
        try
        {
            foreach (var colors in new[]
            {
                new[] { Colors.Black, Colors.White, Colors.Yellow, Colors.Black },
                new[] { Colors.White, Colors.Black, Colors.Navy, Colors.White },
            })
            {
                apply.Invoke(controller, [true, colors[0], colors[1], colors[2], colors[3]]);
                Check("High contrast colors the client surfaces and text", ColorAt(shell, "WbPaper") == colors[0]
                    && ColorAt(shell, "WbText") == colors[1] && ColorAt(shell, "WbMuted") == colors[1]
                    && ColorAt(shell, "WbDisabledText") == colors[1] && ColorAt(shell, "WbLine") == colors[1]);
                Check("High contrast uses complete selected and primary color pairs", ColorAt(shell, "WbSelected") == colors[2]
                    && ColorAt(shell, "WbSelectedText") == colors[3] && ColorAt(shell, "WbAccent") == colors[1]
                    && ColorAt(shell, "WbOnAccent") == colors[0] && ColorAt(shell, "WbStatusProjectText") == colors[1]);
                Check("Existing shell receives contrast state", shell.Classes.Contains("wb-high-contrast"));
                var active = shell.GetVisualDescendants().OfType<Grid>().FirstOrDefault(grid => grid.Classes.Contains("wb-row-active"));
                Check("Selected navigation text uses the matching highlight foreground", active is not null
                    && active.GetVisualDescendants().OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text))
                        .All(text => text.Foreground is ISolidColorBrush brush && brush.Color == colors[3]));
                Check("High contrast keeps checkbox captions legible", shell.FindControl<CheckBox>("RelatedBox")!.Foreground is ISolidColorBrush checkBrush
                    && checkBrush.Color == colors[1]);
                var floating = CreateContrastProbe();
                floating.Classes.Add("wb-host");
                floating.Show(shell);
                Check("New floating windows receive the same contrast state and palette", floating.Classes.Contains("wb-high-contrast")
                    && ColorAt(floating, "WbPaper") == colors[0] && ColorAt(floating, "WbText") == colors[1]);
                CheckControlContrast(shell, floating, colors[0], colors[1], Check);
                CheckPaletteAndSettings(shell, colors[0], colors[1], Check);
                apply.Invoke(controller, [false, default(Color), default(Color), default(Color), default(Color)]);
                Check("Leaving high contrast restores the chosen theme in every open window", ColorAt(shell, "WbText") == baselineText
                    && ColorAt(shell, "WbPaper") == baselinePaper && !shell.Classes.Contains("wb-high-contrast")
                    && !floating.Classes.Contains("wb-high-contrast"));
                floating.Close();
            }
            Check("Contrast transitions preserve unrelated application resources", Equals(application.Resources[unrelatedKey], "keep"));
            var transitions = CheckThemeChanges(shell, controller, type);
            checks += transitions.Checks; failures += transitions.Failures;
        }
        finally
        {
            apply.Invoke(controller, [false, default(Color), default(Color), default(Color), default(Color)]);
            application.Resources.Remove(unrelatedKey);
        }
        return (checks, failures);
    }

    private static (int Checks, int Failures) CheckThemeChanges(Window shell, object controller, Type type)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var application = Application.Current!;
        var originalTheme = application.RequestedThemeVariant;
        var platformField = type.GetField("_platform", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var originalPlatform = platformField.GetValue(controller);
        var refresh = type.GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var platform = DispatchProxy.Create<IPlatformSettings, ContrastPlatformSettings>();
        var platformState = (ContrastPlatformSettings)(object)platform;
        string[] keys = ["WbPaper", "WbText", "WbMuted", "WbAccent", "WbSelected", "WbStatusProjectText", "EtsText", "EtsPanel"];
        Color[] Palette(Window window) => keys.Select(key =>
        {
            window.TryFindResource(key, window.ActualThemeVariant, out var value);
            return ((ISolidColorBrush)value!).Color;
        }).ToArray();
        var directKeys = keys.Where(application.Resources.ContainsKey).ToArray();
        var overrideKey = "ToolTipBorderBrush";
        var hadOverride = application.Resources.TryGetValue(overrideKey, out var originalOverride);
        var customOverride = new SolidColorBrush(Colors.Magenta);
        var expected = new Dictionary<string, Color[]>();
        Window? floating = null;
        try
        {
            // Only the controller's platform input is replaced. No system preference or input changes.
            platformField.SetValue(controller, platform);
            foreach (var theme in new[] { "light", "dark" })
            {
                SettingsWindow.ApplyTheme(theme);
                expected[theme] = Palette(shell);
            }
            Check("Theme transition baselines distinguish Light and Dark palettes", !expected["light"].SequenceEqual(expected["dark"]));
            application.Resources[overrideKey] = customOverride;
            foreach (var (before, after) in new[] { ("light", "dark"), ("dark", "light") })
            {
                SettingsWindow.ApplyTheme(before);
                platformState.HighContrast = true;
                refresh.Invoke(controller, null);
                var contrastPalette = Palette(shell);
                floating = CreateContrastProbe();
                floating.Classes.Add("wb-host");
                floating.Show(shell);
                SettingsWindow.ApplyTheme(after);
                Check($"High contrast survives the actual {before} to {after} theme change", shell.Classes.Contains("wb-high-contrast")
                    && floating.Classes.Contains("wb-high-contrast") && Palette(shell).SequenceEqual(contrastPalette)
                    && Palette(floating).SequenceEqual(contrastPalette));
                CheckControlContrast(shell, floating, contrastPalette[0], contrastPalette[1], Check);
                CheckPaletteAndSettings(shell, contrastPalette[0], contrastPalette[1], Check);
                platformState.HighContrast = false;
                refresh.Invoke(controller, null);
                Check($"Leaving contrast restores the new {after} palette in existing and floating windows", !shell.Classes.Contains("wb-high-contrast")
                    && !floating.Classes.Contains("wb-high-contrast") && Palette(shell).SequenceEqual(expected[after])
                    && Palette(floating).SequenceEqual(expected[after]));
                Check($"The {after} restoration preserves direct overrides without pinning theme resources", keys.Where(application.Resources.ContainsKey).SequenceEqual(directKeys)
                    && ReferenceEquals(application.Resources[overrideKey], customOverride));
                SettingsWindow.ApplyTheme(before);
                Check($"Palette remains theme-responsive after the {before} to {after} contrast cycle", Palette(shell).SequenceEqual(expected[before])
                    && Palette(floating).SequenceEqual(expected[before]));
                floating.Close(); floating = null;
            }
        }
        finally
        {
            platformState.HighContrast = false;
            refresh.Invoke(controller, null);
            floating?.Close();
            platformField.SetValue(controller, originalPlatform);
            application.RequestedThemeVariant = originalTheme;
            if (hadOverride) application.Resources[overrideKey] = originalOverride!;
            else application.Resources.Remove(overrideKey);
        }
        return (checks, failures);
    }

    private static Window CreateContrastProbe() => new()
    {
        Width = 300, Height = 200, ShowInTaskbar = false,
        Content = new StackPanel { Children =
        {
            new CheckBox { Content = "Unchecked", IsChecked = false },
            new CheckBox { Content = "Checked", IsChecked = true },
            new CheckBox { Content = "Indeterminate", IsThreeState = true, IsChecked = null },
        } },
    };

    private static void CheckControlContrast(Window shell, Window probe, Color background, Color foreground, Action<string, bool> check)
    {
        bool ColorIs(IBrush? brush, Color expected) => brush is ISolidColorBrush solid && solid.Color == expected;
        shell.UpdateLayout(); probe.UpdateLayout();
        var primary = shell.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("primary")
            && button.Content is string text && text == Knx.App.Localization.WorkbenchStrings.Get("Programming") && button.IsVisible);
        var labels = primary?.GetVisualDescendants().OfType<TextBlock>().Where(text => !string.IsNullOrEmpty(text.Text)).ToArray() ?? [];
        foreach (var state in new[] { "normal", "pointerover", "pressed", "focus-visible" })
        {
            if (primary is not null)
            {
                ((IPseudoClasses)primary.Classes).Set(":pointerover", state == "pointerover");
                ((IPseudoClasses)primary.Classes).Set(":pressed", state == "pressed");
                ((IPseudoClasses)primary.Classes).Set(":focus-visible", state == "focus-visible");
            }
            var presenter = primary?.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                .FirstOrDefault(item => item.Name == "PART_ContentPresenter");
            check($"Real primary-button label uses its contrast pair in {state}", primary is not null && labels.Length > 0
                && presenter is not null && ColorIs(presenter.Background, foreground) && ColorIs(presenter.Foreground, background)
                && ColorIs(primary.Foreground, background) && labels.All(text => ColorIs(text.Foreground, background)));
        }
        if (primary is not null)
        {
            ((IPseudoClasses)primary.Classes).Set(":pointerover", false);
            ((IPseudoClasses)primary.Classes).Set(":pressed", false);
            ((IPseudoClasses)primary.Classes).Set(":focus-visible", false);
        }
        foreach (var box in probe.GetVisualDescendants().OfType<CheckBox>())
        {
            var border = box.GetVisualDescendants().OfType<Border>().FirstOrDefault(item => item.Name == "NormalRectangle");
            var glyph = box.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().FirstOrDefault(item => item.Name == "CheckGlyph");
            foreach (var state in new[] { "normal", "pointerover", "pressed", "disabled" })
            {
                var pseudo = (IPseudoClasses)box.Classes;
                pseudo.Set(":pointerover", state == "pointerover");
                pseudo.Set(":pressed", state == "pressed");
                box.IsEnabled = state != "disabled";
                check($"{box.Content} checkbox keeps its contrast border/glyph in {state}", border is not null
                    && ColorIs(border.BorderBrush, foreground)
                    && ColorIs(border.Background, box.IsChecked is false ? background : foreground)
                    && (box.IsChecked is false || glyph is not null && ColorIs(glyph.Fill, background)));
            }
            ((IPseudoClasses)box.Classes).Set(":pointerover", false);
            ((IPseudoClasses)box.Classes).Set(":pressed", false);
            box.IsEnabled = true;
        }
    }

    private static void CheckPaletteAndSettings(Window shell, Color background, Color foreground, Action<string, bool> check)
    {
        bool ColorIs(IBrush? brush, Color expected) => brush is ISolidColorBrush solid && solid.Color == expected;
        Color Resource(Window window, string name)
        {
            window.TryFindResource(name, window.ActualThemeVariant, out var value);
            return ((ISolidColorBrush)value!).Color;
        }
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var windows = lifetime.Windows.ToHashSet();
        typeof(WorkbenchWindow).GetMethod("ShowPalette", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, null);
        var palette = lifetime.Windows.Single(window => !windows.Contains(window));
        try
        {
            palette.UpdateLayout();
            var query = palette.GetVisualDescendants().OfType<TextBox>().Single();
            var results = palette.GetVisualDescendants().OfType<ListBox>().Single();
            check("Actual command palette receives contrast state", palette.Classes.Contains("wb-high-contrast"));
            CheckInput(query, background, foreground, check);
            palette.UpdateLayout(); results.UpdateLayout();
            var border = results.GetVisualDescendants().OfType<Border>().FirstOrDefault(part => part.Name == "border");
            check("Actual command result surface uses the contrast background", border is not null && ColorIs(border.Background, background)
                && ColorIs(results.Background, background));
            var item = results.GetVisualDescendants().OfType<ListBoxItem>().First();
            var selected = Resource(palette, "WbSelected"); var selectedText = Resource(palette, "WbSelectedText");
            foreach (var state in new[] { "normal", "pointerover", "pressed", "selected", "selected-pointerover" })
            {
                results.SelectedIndex = state.StartsWith("selected", StringComparison.Ordinal) ? 0 : -1;
                var pseudo = (IPseudoClasses)item.Classes;
                pseudo.Set(":pointerover", state is "pointerover" or "selected-pointerover"); pseudo.Set(":pressed", state == "pressed");
                palette.UpdateLayout();
                var presenter = item.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
                var active = state.StartsWith("selected", StringComparison.Ordinal);
                check($"Actual command result uses its complete contrast pair in {state}", presenter is not null
                    && ColorIs(presenter.Background, active ? selected : background) && ColorIs(presenter.Foreground, active ? selectedText : foreground)
                    && item.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text))
                        .All(t => ColorIs(t.Foreground, active ? selectedText : foreground)));
            }
        }
        finally { palette.Close(); }

        // Options intentionally has no wb-host class. It must still receive the
        // same contrast rules, including content shown in a separate popup root.
        var settings = new SettingsWindow(new Knx.AppSettings()) { ShowInTaskbar = false };
        settings.Show(shell);
        try
        {
            settings.UpdateLayout();
            check("Actual options window receives contrast without wb-host", settings.Classes.Contains("wb-high-contrast") && !settings.Classes.Contains("wb-host"));
            var caption = settings.FindControl<CheckBox>("ConfirmDeleteBox")!;
            var captionPresenter = caption.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
            var captionLabels = caption.GetVisualDescendants().OfType<TextBlock>().Where(t => !string.IsNullOrEmpty(t.Text)).ToArray();
            check("Actual options checkbox caption remains readable", ColorIs(caption.Foreground, foreground) && captionPresenter is not null
                && ColorIs(captionPresenter.Foreground, foreground) && captionLabels.Length > 0 && captionLabels.All(t => ColorIs(t.Foreground, foreground)));
            CheckInput(settings.FindControl<TextBox>("TunnelAddressBox")!, background, foreground, check);
            var theme = settings.FindControl<ComboBox>("ThemeBox")!;
            theme.IsDropDownOpen = true; settings.UpdateLayout();
            var items = theme.Items.OfType<ComboBoxItem>().ToArray();
            foreach (var entry in items) { entry.ApplyTemplate(); entry.UpdateLayout(); }
            var popup = items[0].GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Name == "PopupBorder");
            check("Actual options dropdown popup uses the contrast surface", popup is not null && ColorIs(popup.Background, background) && ColorIs(popup.BorderBrush, foreground));
            var selected = Resource(settings, "WbSelected"); var selectedText = Resource(settings, "WbSelectedText");
            foreach (var state in new[] { "normal", "pointerover", "selected", "selected-pointerover" })
            {
                theme.SelectedIndex = state.StartsWith("selected", StringComparison.Ordinal) ? 0 : -1;
                ((IPseudoClasses)items[0].Classes).Set(":pointerover", state.Contains("pointerover", StringComparison.Ordinal));
                settings.UpdateLayout();
                var presenter = items[0].GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");
                var active = state.StartsWith("selected", StringComparison.Ordinal);
                check($"Actual options dropdown item uses its complete contrast pair in {state}", presenter is not null
                    && ColorIs(presenter.Background, active ? selected : background) && ColorIs(presenter.Foreground, active ? selectedText : foreground));
            }
            theme.IsDropDownOpen = false;
        }
        finally { settings.Close(); }
    }

    private static void CheckInput(TextBox input, Color background, Color foreground, Action<string, bool> check)
    {
        bool ColorIs(IBrush? brush, Color expected) => brush is ISolidColorBrush solid && solid.Color == expected;
        var originalText = input.Text; var originalEnabled = input.IsEnabled;
        try
        {
            input.Text = "contrast query";
            foreach (var state in new[] { "normal", "pointerover", "focus", "disabled" })
            {
                var pseudo = (IPseudoClasses)input.Classes;
                pseudo.Set(":pointerover", state == "pointerover"); pseudo.Set(":focus", state == "focus");
                input.IsEnabled = state != "disabled"; input.UpdateLayout();
                var border = input.GetVisualDescendants().OfType<Border>().FirstOrDefault(p => p.Name == "PART_BorderElement");
                var presenter = input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault(p => p.Name == "PART_TextPresenter");
                check($"{input.Name ?? "Command search"} input template uses a complete contrast pair in {state}", border is not null && presenter is not null
                    && ColorIs(border.Background, background) && ColorIs(border.BorderBrush, foreground)
                    && ColorIs(input.Foreground, foreground) && ColorIs(presenter.Foreground, foreground) && ColorIs(input.CaretBrush, foreground));
            }
        }
        finally
        {
            ((IPseudoClasses)input.Classes).Set(":pointerover", false); ((IPseudoClasses)input.Classes).Set(":focus", false);
            input.IsEnabled = originalEnabled; input.Text = originalText;
        }
    }

    private class ContrastPlatformSettings : DispatchProxy
    {
        public ContrastPlatformSettings() { }
        internal bool HighContrast { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name == nameof(IPlatformSettings.GetColorValues)
            ? new PlatformColorValues { ContrastPreference = HighContrast ? ColorContrastPreference.High : ColorContrastPreference.NoPreference }
            : throw new NotSupportedException(method?.Name);
    }
}

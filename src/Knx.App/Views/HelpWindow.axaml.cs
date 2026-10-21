using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Knx.App.Localization;

namespace Knx.App.Views;

/// <summary>Localized V4 help; content stays the same sections as the former message box.</summary>
public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        Title = WorkbenchStrings.Get("HelpTitle");
        AutomationProperties.SetName(this, Title);
        CloseButton.Content = WorkbenchStrings.Get("Close");
        CloseButton.IsCancel = true;
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) => CloseButton.Focus();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        AddSection("HelpSectionNavigation", "HelpIntro");
        AddSection("HelpSectionShortcuts", "HelpShortcuts");
        AddSection("HelpSectionLinks", "HelpLinks");
        AddSection("HelpSectionParameters", "HelpParameters");
        AddSection("HelpSectionSessions", "HelpSessions");
        AddSection("HelpSectionBoundaries", "HelpBoundaries");
    }

    private void AddSection(string headingKey, string textKey)
    {
        var section = new StackPanel { Spacing = 5 };
        section.Children.Add(new TextBlock { Text = WorkbenchStrings.Get(headingKey), Classes = { "help-heading" } });
        section.Children.Add(new TextBlock
        {
            Text = WorkbenchStrings.Get(textKey).TrimEnd('\r', '\n'),
            Classes = { "help-text" }
        });
        Sections.Children.Add(section);
    }
}

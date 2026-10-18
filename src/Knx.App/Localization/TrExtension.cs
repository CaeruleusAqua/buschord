using Avalonia.Markup.Xaml;
using Knx;

namespace Knx.App.Localization;

/// <summary>
/// XAML markup extension for UI strings: {loc:Tr MenuFileOpen}.
/// Resolved once when the control is loaded; a language change therefore takes effect
/// for windows opened afterwards (see <see cref="Strings.Language"/>).
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}

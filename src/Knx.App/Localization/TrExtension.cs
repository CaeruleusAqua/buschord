using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;
using Knx;

namespace Knx.App.Localization;

/// <summary>
/// Indexer source behind {loc:Tr Key}. Avalonia indexer bindings refresh on the indexer
/// property name "Item"; the core dictionary raises "Item[]" for a language change, so this
/// thin adapter owns the notification the bindings listen for.
/// </summary>
public sealed class TrSource : INotifyPropertyChanged
{
    /// <summary>Static-to-static subscription: no window or control is retained.</summary>
    public static TrSource Instance { get; } = new();

    private TrSource()
    {
        Strings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Item[]")
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        };
    }

    public string this[string key] => Strings.Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// XAML markup extension for UI strings: {loc:Tr MenuFileOpen}.
/// Returns a one-way binding to the indexer of <see cref="TrSource"/>, so the text
/// follows <see cref="Strings.Language"/> while the window stays open.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }

    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = TrSource.Instance, Mode = BindingMode.OneWay };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Knx.App.Views;

/// <summary>
/// Material Symbols icon selected by resource name, e.g. <c>&lt;views:Icon Symbol="FolderOpen" /&gt;</c>.
/// The geometry is drawn in its native 960-unit box (viewBox 0 -960 960 960), so every icon keeps the
/// optical size and stroke weight of the source set instead of being stretched to its own bounds.
/// </summary>
public sealed class Icon : TemplatedControl
{
    public static readonly StyledProperty<string?> SymbolProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Symbol));

    static Icon()
    {
        AffectsRender<Icon>(SymbolProperty);
        WidthProperty.OverrideDefaultValue<Icon>(18);
        HeightProperty.OverrideDefaultValue<Icon>(18);
    }

    public string? Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>Looks up <c>Icon.&lt;name&gt;</c> in the application resources; throws when it is missing.</summary>
    public static Geometry GeometryFor(string name)
    {
        if (Application.Current?.TryFindResource("Icon." + name, out var value) == true && value is Geometry geometry)
            return geometry;
        throw new KeyNotFoundException($"Icon '{name}' is not defined: add Icon.{name} to Assets/Icons.axaml");
    }

    public override void Render(DrawingContext context)
    {
        if (Symbol is not { } name) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 960;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(0, 960 * scale)))
            context.DrawGeometry(Foreground, null, GeometryFor(name));
    }
}

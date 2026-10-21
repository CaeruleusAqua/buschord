using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Knx.App.Views;

/// <summary>
/// Concept line icon selected by name, e.g. <c>&lt;views:ConceptIcon Symbol="building" /&gt;</c>.
/// The geometry stays in its native 24x24 viewBox and is stroked with 1.7 px and round caps/joins,
/// so every icon keeps the line weight of the KNX Workbench concept set instead of a filled glyph.
/// </summary>
public sealed class ConceptIcon : TemplatedControl
{
    public static readonly StyledProperty<string?> SymbolProperty =
        AvaloniaProperty.Register<ConceptIcon, string?>(nameof(Symbol));

    /// <summary>Stroke width in the 24-unit source viewBox.</summary>
    public const double StrokeWidth = 1.7;

    static ConceptIcon()
    {
        AffectsRender<ConceptIcon>(SymbolProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<ConceptIcon>(17);
        HeightProperty.OverrideDefaultValue<ConceptIcon>(17);
    }

    public string? Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    /// <summary>Looks up <c>Concept.&lt;name&gt;</c> in the application resources; throws when it is missing.</summary>
    public static Geometry GeometryFor(string name)
    {
        if (Application.Current?.TryFindResource("Concept." + name, out var value) == true && value is Geometry geometry)
            return geometry;
        throw new KeyNotFoundException($"Concept icon '{name}' is not defined: add Concept.{name} to Assets/ConceptIcons.axaml");
    }

    public override void Render(DrawingContext context)
    {
        if (Symbol is not { } name) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, GeometryFor(name));
    }
}

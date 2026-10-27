using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Knx.App.Views;

var checks = 0;
void Check(string name, bool passed)
{
    if (!passed) throw new Exception(name);
    checks++;
    Console.WriteLine("PASS " + name);
}

AppBuilder.Configure<Knx.App.App>().UsePlatformDetect().SetupWithoutStarting();
var app = Application.Current!;

// Bounds the Material Symbols path data actually occupies inside its 0 -960 960 960 viewBox.
// Null skips the exact-bounds regression check; the shape checks below still run for every icon.
(string Name, Rect? Bounds)[] icons =
[
    ("FolderOpen", new Rect(80, -800, 860, 640)),
    ("NoteAdd", new Rect(160, -880, 640, 800)),
    ("Save", new Rect(120, -840, 720, 720)),
    ("Search", new Rect(120, -840, 720, 720)),
    ("Category", new Rect(120, -880, 760, 800)),

    // Expected icon set.
    ("AccountTree", null),
    ("FormatListBulleted", null),
    ("LibraryBooks", null),
    ("Cable", null),
    ("FactCheck", null),
    ("Settings", null),
    ("Undo", null),
    ("Redo", null),
    ("Radar", null),
    ("FiberManualRecord", null),
    ("Stop", null),
    ("Link", null),
    ("Tune", null),

    // Workbench shell affordances.
    ("UnfoldMore", null),
    ("OpenInNew", null),
    ("Fullscreen", null),
    ("Close", null),
    ("KeyboardArrowDown", null),
    ("KeyboardArrowRight", null),
];

foreach (var (name, expected) in icons)
{
    Check($"{name} resource exists", app.TryFindResource("Icon." + name, out var value));
    var geometry = value as StreamGeometry ?? throw new Exception($"{name} is not a StreamGeometry");
    Check($"{name} non-empty", geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0);
    if (expected is { } bounds) Check($"{name} expected bounds {bounds}", geometry.Bounds == bounds);
    Check($"{name} inside material viewBox",
        geometry.Bounds.X >= 0 && geometry.Bounds.Y >= -960
        && geometry.Bounds.Right <= 960 && geometry.Bounds.Bottom <= 0);
    Check($"{name} lookup returns the resource", ReferenceEquals(Icon.GeometryFor(name), geometry));
}

try
{
    Icon.GeometryFor("DoesNotExist");
    Check("unknown name is rejected", false);
}
catch (KeyNotFoundException ex)
{
    Check("unknown name is rejected with a clear message", ex.Message.Contains("Icon.DoesNotExist"));
}

var icon = new Icon { Symbol = "FolderOpen" };
icon.Measure(Size.Infinity);
Check("default icon size is 18", icon.DesiredSize == new Size(18, 18));
Check("foreground inherits from the parent", Icon.ForegroundProperty.Inherits);

// End to end: every icon renders inside the 18px slot, and a glyph that spans 640 of the
// 960 units stays 12px tall. Fitting the glyph bounds (as PathIcon does) would make it 18px.
foreach (var (name, _) in icons)
{
    var rendered = new Icon { Symbol = name, Foreground = Brushes.Black };
    rendered.Measure(new Size(18, 18));
    rendered.Arrange(new Rect(0, 0, 18, 18));
    using var target = new RenderTargetBitmap(new PixelSize(18, 18), new Vector(96, 96));
    target.Render(rendered);
    var (left, top, right, bottom) = InkBox(target);
    Check($"{name} renders", right > left && bottom > top);
    Check($"{name} stays inside 18x18", left >= 0 && top >= 0 && right < 18 && bottom < 18);
}

var folder = new Icon { Symbol = "FolderOpen", Foreground = Brushes.Black };
folder.Measure(new Size(18, 18));
folder.Arrange(new Rect(0, 0, 18, 18));
using var folderTarget = new RenderTargetBitmap(new PixelSize(18, 18), new Vector(96, 96));
folderTarget.Render(folder);
var (_, folderTop, _, folderBottom) = InkBox(folderTarget);
var folderHeight = folderBottom - folderTop + 1;
Check($"FolderOpen keeps the material scale (height {folderHeight}, not 18)",
    folderHeight >= 11 && folderHeight <= 13);

// Concept line icons: every path from the KNX Workbench V4 bundle is a 24x24 stroked geometry.
string[] concepts =
[
    "building", "topology", "groups", "device", "home", "folder", "chevron", "down", "search",
    "plus", "close", "more", "link", "monitor", "check", "warning", "upload", "download", "clock",
    "undo", "redo", "settings", "layers", "panel", "split", "arrow", "lock", "eye", "catalog",
    "play", "stop", "pin", "maximize", "doc", "help", "bulb", "trash", "export", "refresh",
    "flash", "flag", "move",
];
foreach (var name in concepts)
{
    Check($"Concept.{name} resource exists", app.TryFindResource("Concept." + name, out var value));
    var geometry = value as StreamGeometry ?? throw new Exception($"Concept.{name} is not a StreamGeometry");
    // Zero-height paths are intentional (round caps turn them into dots), so one axis may be flat.
    Check($"Concept.{name} non-empty", geometry.Bounds.Width > 0 || geometry.Bounds.Height > 0);
    // Arc bounds may include bezier control points slightly outside the visible stroke.
    Check($"Concept.{name} inside 24x24 viewBox",
        geometry.Bounds.X >= -0.5 && geometry.Bounds.Y >= -0.5
        && geometry.Bounds.Right <= 24.5 && geometry.Bounds.Bottom <= 24.5);
    Check($"Concept.{name} lookup returns the resource", ReferenceEquals(ConceptIcon.GeometryFor(name), geometry));
}

try
{
    ConceptIcon.GeometryFor("DoesNotExist");
    Check("unknown concept name is rejected", false);
}
catch (KeyNotFoundException ex)
{
    Check("unknown concept name is rejected with a clear message", ex.Message.Contains("Concept.DoesNotExist"));
}

var concept = new ConceptIcon { Symbol = "building" };
concept.Measure(Size.Infinity);
Check("default concept icon size is 17", concept.DesiredSize == new Size(17, 17));
Check("concept foreground inherits from the parent", ConceptIcon.ForegroundProperty.Inherits);

foreach (var name in concepts)
{
    var rendered = new ConceptIcon { Symbol = name, Foreground = Brushes.Black };
    rendered.Measure(new Size(17, 17));
    rendered.Arrange(new Rect(0, 0, 17, 17));
    using var target = new RenderTargetBitmap(new PixelSize(17, 17), new Vector(96, 96));
    target.Render(rendered);
    var (left, top, right, bottom) = InkBox(target);
    Check($"{name} concept icon renders", right > left || bottom > top);
    Check($"{name} concept icon stays inside 17x17", left >= 0 && top >= 0 && right < 17 && bottom < 17);
}

foreach (var (theme, brush, limit) in new (string, IBrush, int)[] { ("light", Brushes.Black, 60), ("dark", Brushes.White, 180) })
{
    var rendered = new ConceptIcon { Symbol = "building", Foreground = brush };
    rendered.Measure(new Size(17, 17));
    rendered.Arrange(new Rect(0, 0, 17, 17));
    using var target = new RenderTargetBitmap(new PixelSize(17, 17), new Vector(96, 96));
    target.Render(rendered);
    var (red, green, blue) = InkColor(target);
    Check($"concept icon follows the {theme} foreground", theme == "light"
        ? red <= limit && green <= limit && blue <= limit
        : red >= limit && green >= limit && blue >= limit);
}

Console.WriteLine($"OK {checks} checks");

static (int Left, int Top, int Right, int Bottom) InkBox(RenderTargetBitmap bitmap)
{
    var size = bitmap.PixelSize.Width;
    var stride = size * 4;
    var buffer = Marshal.AllocHGlobal(size * stride);
    try
    {
        bitmap.CopyPixels(new PixelRect(0, 0, size, size), buffer, size * stride, stride);
        var pixels = new byte[size * stride];
        Marshal.Copy(buffer, pixels, 0, pixels.Length);
        int left = size, top = size, right = -1, bottom = -1;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            if (pixels[y * stride + x * 4 + 3] <= 32) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
            top = Math.Min(top, y);
            bottom = Math.Max(bottom, y);
        }
        return (left, top, right, bottom);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

// Colour of the most opaque pixel, to prove the stroke uses Foreground in both themes.
static (byte Red, byte Green, byte Blue) InkColor(RenderTargetBitmap bitmap)
{
    var size = bitmap.PixelSize.Width;
    var stride = size * 4;
    var buffer = Marshal.AllocHGlobal(size * stride);
    try
    {
        bitmap.CopyPixels(new PixelRect(0, 0, size, size), buffer, size * stride, stride);
        var pixels = new byte[size * stride];
        Marshal.Copy(buffer, pixels, 0, pixels.Length);
        var best = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > pixels[best + 3]) best = i;
        return (pixels[best + 2], pixels[best + 1], pixels[best]);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

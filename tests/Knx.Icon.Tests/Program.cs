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

    // Navigation and device icon set.
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

Console.WriteLine($"OK {checks} checks");

static (int Left, int Top, int Right, int Bottom) InkBox(RenderTargetBitmap bitmap)
{
    const int size = 18, stride = size * 4;
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

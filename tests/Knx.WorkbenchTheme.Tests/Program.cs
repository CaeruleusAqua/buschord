using System.Xml.Linq;

var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "WorkbenchTheme.axaml"));
XNamespace a = "https://github.com/avaloniaui";
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
var resources = document.Descendants(a + "Styles.Resources").Single().Element(a + "ResourceDictionary")!;
var palettes = resources.Element(a + "ResourceDictionary.ThemeDictionaries")!.Elements().ToList();
Check(palettes.Select(p => (string?)p.Attribute(x + "Key")).SequenceEqual(new[] { "Light", "Dark" }), "two explicit palettes");
var light = palettes[0].Elements().ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string?)e.Attribute("Color"));
var dark = palettes[1].Elements().ToDictionary(e => (string)e.Attribute(x + "Key")!, e => (string?)e.Attribute("Color"));
Check(light.Keys.Order().SequenceEqual(dark.Keys.Order()), "matching palette keys");
foreach (var (key, color) in new[] { ("WbAccent", "#087e81"), ("WbBackground", "#f5f6f8"), ("WbPaper", "#ffffff"), ("WbLine", "#e1e5eb"), ("WbText", "#202c3c") })
    Check(light[key] == color, key + " reference color");
foreach (var (key, value) in new[] { ("WbButtonHeight", "30"), ("WbCompactHeight", "26"), ("WbFontSize", "13"), ("WbDocumentTabHeight", "39"), ("WbSplitterSize", "5") })
    Check(resources.Elements().Single(e => (string?)e.Attribute(x + "Key") == key).Value == value, key + " geometry");
foreach (var style in document.Elements().Single().Elements(a + "Style"))
    Check(((string)style.Attribute("Selector")!).Split(',').All(s => s.Contains(".wb-", StringComparison.Ordinal)), "opt-in selector " + style.Attribute("Selector")!.Value);
Check(resources.Descendants().Attributes(x + "Key").All(k => k.Value is "Light" or "Dark" || k.Value.StartsWith("Wb", StringComparison.Ordinal)), "no global token overrides");
foreach (var (palette, name) in new[] { (light, "light"), (dark, "dark") })
{
    foreach (var (foreground, background, minimum) in new[]
             {
                 ("WbText", "WbPaper", 7.0), ("WbMuted", "WbRail", 4.5),
                 ("WbMuted", "WbStatusbar", 4.5), ("WbDisabledText", "WbDisabled", 4.5),
                 ("WbSelectedText", "WbSelected", 4.5),
             })
        Check(Contrast(palette[foreground]!, palette[background]!) >= minimum,
            $"{name} contrast {foreground}/{background} >= {minimum:0.0}:1");
}
Console.WriteLine("PASS theme source contract and text contrast (not a native rendering test)");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
}

static double Contrast(string foreground, string background)
{
    var light = Math.Max(Luminance(foreground), Luminance(background));
    var dark = Math.Min(Luminance(foreground), Luminance(background));
    return (light + 0.05) / (dark + 0.05);
}

static double Luminance(string hex)
{
    var rgb = new[] { 1, 3, 5 }
        .Select(offset => Convert.ToInt32(hex.Substring(offset, 2), 16) / 255.0)
        .Select(value => value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4))
        .ToArray();
    return 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2];
}

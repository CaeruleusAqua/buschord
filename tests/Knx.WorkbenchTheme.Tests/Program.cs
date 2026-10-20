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
Console.WriteLine("PASS theme source contract (not a native rendering test)");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
}

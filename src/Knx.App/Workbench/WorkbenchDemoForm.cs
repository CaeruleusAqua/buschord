using System.Globalization;
using System.Text.Json;
using Knx.App.Localization;

namespace Knx.App.Workbench;

/// <summary>
/// Contract of the V4 demo parameter editor. Demo values are synthetic JSON key/value pairs
/// rather than <c>ParameterInstance</c> rows, so they cannot be hosted by ParameterPane;
/// the editor therefore shares ParameterPane's field geometry (<c>wb-label</c>/<c>wb-field</c>),
/// its sticky review/apply footer and its per-field validation feedback, and keeps the key
/// vocabulary, encoding and numeric range here so both the UI and the checks use one source.
/// </summary>
public static class WorkbenchDemoForm
{
    /// <summary>Keys the demo JSON importer accepts, in display order.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "mode", "led", "interface", "debounce", "holdTime", "brightnessThreshold",
        "setpoint", "deadband", "minimum", "fade", "onDelay", "offDelay"
    ];

    public static bool IsKnown(string key) => Keys.Contains(key, StringComparer.Ordinal);

    /// <summary>mode, led and interface are JSON strings; every other demo value is a number.</summary>
    public static bool IsText(string key) => key is "mode" or "led" or "interface";

    public static bool IsNumeric(string key) => IsKnown(key) && !IsText(key);

    public static int SortOrder(string key) => key switch { "mode" => 0, "led" => 1, "interface" => 2, _ => 3 };

    public static string Label(string key) => key switch
    {
        "mode" => WorkbenchStrings.Get("Function"),
        "led" => WorkbenchStrings.Get("StatusLed"),
        "interface" => WorkbenchStrings.Get("Interface"),
        "debounce" => WorkbenchStrings.Get("DebounceMs"),
        "holdTime" => WorkbenchStrings.Get("HoldTime"),
        "brightnessThreshold" => WorkbenchStrings.Get("BrightnessThreshold"),
        "setpoint" => WorkbenchStrings.Get("Setpoint"),
        "deadband" => WorkbenchStrings.Get("Deadband"),
        "minimum" => WorkbenchStrings.Get("MinimumBrightness"),
        "fade" => WorkbenchStrings.Get("FadeTime"),
        "onDelay" => WorkbenchStrings.Get("OnDelay"),
        "offDelay" => WorkbenchStrings.Get("OffDelay"),
        _ => key
    };

    /// <summary>Demo string parameters are stored JSON-quoted; numbers stay raw.</summary>
    public static string Serialize(string key, string value) => IsText(key) ? JsonSerializer.Serialize(value) : value;

    /// <summary>Field error text, or null when the value is valid. Mirrors the importer's range.</summary>
    public static string? Validate(string key, string value)
    {
        if (!IsNumeric(key)) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            return WorkbenchStrings.Get("DemoNumberRequired");
        var minimum = key == "setpoint" ? -50 : 0;
        return number < minimum || number > 1_000_000
            ? WorkbenchStrings.Get("DemoNumberRange", Format(minimum), Format(1_000_000))
            : null;
    }

    public static string Unquote(string raw)
    {
        try { return JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch (JsonException) { return raw; }
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

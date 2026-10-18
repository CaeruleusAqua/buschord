namespace Knx.Bus;

/// <summary>
/// Value coding for the common datapoint subtypes, keyed by the DPT id
/// (as stored in the project, e.g. "DPST-1-1"). Only the subset used by the UI.
/// </summary>
public static class Dpt
{
    /// <summary>Decodes a group value payload to a display string.</summary>
    public static string Decode(string? datapointType, byte[] data) => datapointType switch
    {
        "DPST-1-1" => data.Length > 0 && data[0] != 0 ? "On" : "Off",
        "DPST-1-2" => data.Length > 0 && data[0] != 0 ? "true" : "false",
        "DPST-1-10" => data.Length > 0 && data[0] != 0 ? "Start" : "Stop",
        "DPST-1-11" => data.Length > 0 && data[0] != 0 ? "State active" : "State inactive",
        "DPST-5-1" or "DPST-5-3" => data.Length > 0 ? Math.Round(data[0] / 255.0 * 100).ToString("F0") + "%" : "?",
        "DPST-9-1" => data.Length >= 2 ? ((short)((data[0] << 8) | data[1]) / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "?", // 2-byte float, e.g. °C
        "DPST-9-7" => data.Length >= 2 ? (((data[0] << 8) | data[1]) / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "?", // humidity %
        _ => BitConverter.ToString(data),
    };

    /// <summary>Encodes a display value into a group value payload.</summary>
    public static byte[] Encode(string? datapointType, string value) => datapointType switch
    {
        "DPST-1-1" or "DPST-1-2" or "DPST-1-10" or "DPST-1-11"
            => [IsOn(value) ? (byte)1 : (byte)0],
        "DPST-5-1" or "DPST-5-3"
            => [TryPercent(value, out var p) ? (byte)Math.Round(p / 100.0 * 255) : (byte)0],
        "DPST-9-1" or "DPST-9-7"
            => TryFloat(value, out var f) ? [(byte)(((short)(f * 100) >> 8) & 0xFF), (byte)((short)(f * 100) & 0xFF)] : [0, 0],
        _ => TryHex(value) ?? [],
    };

    private static bool IsOn(string value) => value.Trim().ToLowerInvariant() is "1" or "on" or "true" or "start" or "state active";

    private static bool TryPercent(string value, out double percent)
    {
        percent = 0;
        return double.TryParse(value.Trim().TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture, out percent);
    }

    private static bool TryFloat(string value, out double f)
        => double.TryParse(value.Trim().Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out f);

    private static byte[]? TryHex(string value)
    {
        var text = value.Trim().Replace(" ", "");
        if (text.Length % 2 != 0) return null;
        try
        {
            return Enumerable.Range(0, text.Length / 2)
                .Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16))
                .ToArray();
        }
        catch
        {
            return null;
        }
    }
}

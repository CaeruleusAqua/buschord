namespace Knx.Bus;

/// <summary>
/// Value coding for the common datapoint subtypes, keyed by the DPT id
/// (as stored in the project, e.g. "DPST-1-1"). Only the subset used by the UI.
/// </summary>
public static class Dpt
{
    /// <summary>Decodes a group value payload to a display string.</summary>
    public static string Decode(string? datapointType, byte[] data) => DptId.Normalize(datapointType) switch
    {
        "1.001" => data.Length > 0 && data[0] != 0 ? "On" : "Off",
        "1.002" => data.Length > 0 && data[0] != 0 ? "true" : "false",
        "1.010" => data.Length > 0 && data[0] != 0 ? "Start" : "Stop",
        "1.011" => data.Length > 0 && data[0] != 0 ? "State active" : "State inactive",
        "5.001" or "5.003" => data.Length > 0 ? Math.Round(data[0] / 255.0 * 100).ToString("F0") + "%" : "?",
        "9.001" => data.Length >= 2 ? ((short)((data[0] << 8) | data[1]) / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "?", // 2-byte float, e.g. °C
        "9.007" => data.Length >= 2 ? (((data[0] << 8) | data[1]) / 100.0).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "?", // humidity %
        _ => BitConverter.ToString(data),
    };

    /// <summary>
    /// Bit length of the common DPT main types. Accepts both the project id
    /// (<c>DPST-9-1</c>) and the human form (<c>9.001</c>). Unknown types return false
    /// so callers can reject them instead of creating an unlinkable group address.
    /// </summary>
    public static bool TryBitSize(string? datapointType, out int bits) => DptId.TryBitSize(datapointType, out bits);

    /// <summary>Encodes a display value into a group value payload.</summary>
    public static byte[] Encode(string? datapointType, string value) => DptId.Normalize(datapointType) switch
    {
        "1.001" or "1.002" or "1.010" or "1.011"
            => [IsOn(value) ? (byte)1 : (byte)0],
        "5.001" or "5.003"
            => [TryPercent(value, out var p) ? (byte)Math.Round(p / 100.0 * 255) : (byte)0],
        "9.001" or "9.007"
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

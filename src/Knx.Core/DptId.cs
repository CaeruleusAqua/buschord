namespace Knx;

/// <summary>DPT spelling, not a registry of supported subtypes. Stored project text is unchanged.</summary>
public static class DptId
{
    /// <summary>Canonical full code or main-only code; malformed/opaque identifiers stay unknown.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.StartsWith("DPST-", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        else if (text.StartsWith("DPT-", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        var parts = text.Replace('-', '.').Split('.');
        if (parts.Length is < 1 or > 2 || !Digits(parts[0])
            || !int.TryParse(parts[0], out var main) || main == 0) return null;
        if (parts.Length == 2 && parts[1] != "x"
            && (!Digits(parts[1]) || !int.TryParse(parts[1], out _))) return null;
        return Owl.DatapointTypes.NormalizeCode(text);
    }

    /// <summary>Fixed sizes of supported main types only; no guess for unknown or malformed types.</summary>
    public static bool TryBitSize(string? value, out int bits)
    {
        var code = Normalize(value);
        bits = code?.Split('.')[0] switch
        {
            "1" => 1, "2" => 2, "3" => 4,
            "4" or "5" or "6" or "17" or "18" or "20" or "21" => 8,
            "7" or "8" or "9" or "22" => 16,
            "10" or "11" => 24,
            "12" or "13" or "14" or "15" => 32,
            "16" => 112, "19" => 64,
            _ => 0,
        };
        return bits > 0;
    }

    /// <summary>Resolves manufacturer main/subtype declarations without choosing between alternatives.</summary>
    public static string? FromDeclaration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var codes = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Distinct().ToList();
        if (codes.Any(c => c is null)) return null;
        var mains = codes.Select(c => c!.Split('.')[0]).Distinct().ToList();
        if (mains.Count != 1) return null;
        var subtypes = codes.Where(c => c!.Contains('.') && !c.EndsWith(".x", StringComparison.Ordinal)).ToList();
        return subtypes.Count == 1 ? subtypes[0] : mains[0] + ".x";
    }

    private static bool Digits(string text) => text.Length > 0 && text.All(c => c is >= '0' and <= '9');
}

using System.Globalization;

namespace Knx.Owl;

/// <summary>One KNX datapoint type individual from the ontology (KIM: knx:DatapointType).</summary>
public sealed record DptInfo(
    string Subject,
    string Name,
    int Major,
    int? Minor,
    int? BitSize,
    IReadOnlyList<string> Codes,
    int FieldCount,
    int? FieldBits)
{
    /// <summary>Canonical code: "1.001", or "1.x" for a major-type individual.</summary>
    public string Code => Minor is int minor
        ? Major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString("D3", CultureInfo.InvariantCulture)
        : Major.ToString(CultureInfo.InvariantCulture) + ".x";

    /// <summary>Bit size rounded up to whole bytes, if known.</summary>
    public int? ByteSize => BitSize is int bits ? (bits + 7) / 8 : null;
}

/// <summary>
/// Index over the knx:DatapointType individuals of the ontology. Lookup accepts the
/// ontology subject form ("1.001"), the ETS id ("DPST-1-1") and major/minor numbers.
/// </summary>
public sealed class DatapointTypes
{
    private const string DptClass = SemanticExport.KnxNamespace + "DatapointType";
    private const string MajorProperty = SemanticExport.KnxNamespace + "dptMajorNumber";
    private const string MinorProperty = SemanticExport.KnxNamespace + "dptMinorNumber";
    private const string BitSizeProperty = SemanticExport.KnxNamespace + "dptBitSizeMax";
    private const string AssembledWithProperty = SemanticExport.KnxNamespace + "isAssembledWith";
    private const string FieldSizeProperty = SemanticExport.KnxNamespace + "fieldSize";

    private readonly Dictionary<string, DptInfo> _byCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DptInfo> _all = [];

    public DatapointTypes(Ontology ontology)
    {
        ArgumentNullException.ThrowIfNull(ontology);

        foreach (var subject in ontology.IndividualsOfClass(DptClass))
        {
            if (IntValue(ontology, subject, MajorProperty) is not int major) continue;
            var minor = IntValue(ontology, subject, MinorProperty);
            var codes = ontology.LiteralValues(subject, Ontology.DctSubject).ToList();
            var fields = ontology.IriObjects(subject, AssembledWithProperty).ToList();
            var fieldBits = fields.Sum(f => IntValue(ontology, f, FieldSizeProperty) ?? 0);

            var info = new DptInfo(
                subject,
                ontology.Label(subject) ?? subject,
                major,
                minor,
                IntValue(ontology, subject, BitSizeProperty),
                codes,
                fields.Count,
                fieldBits > 0 ? fieldBits : null);

            _all.Add(info);
            Index(info);
        }
    }

    public IReadOnlyList<DptInfo> All => _all;

    /// <summary>Resolves "1.001", "1.1", "DPST-1-1", "1.x" or "urn:knx:dpt.switch".</summary>
    public DptInfo? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var key = code.Trim();
        if (_byCode.TryGetValue(key, out var direct)) return direct;
        var normalized = NormalizeCode(key);
        return normalized is not null && _byCode.TryGetValue(normalized, out var info) ? info : null;
    }

    /// <summary>Resolves major/minor; without minor this returns the major-type individual ("1.x").</summary>
    public DptInfo? Find(int major, int? minor = null)
        => Find(minor is int value
            ? major.ToString(CultureInfo.InvariantCulture) + "." + value.ToString("D3", CultureInfo.InvariantCulture)
            : major.ToString(CultureInfo.InvariantCulture) + ".x");

    /// <summary>Normalizes a DPT code to "M.mmm" or "M.x"; null when it is not a code at all.</summary>
    public static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        if (text.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.StartsWith("DPST-", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        else if (text.StartsWith("DPT-", StringComparison.OrdinalIgnoreCase)) text = text[4..];

        text = text.Replace('-', '.');
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)) return null;

        if (parts.Length == 1) return major.ToString(CultureInfo.InvariantCulture) + ".x";

        var minor = parts[1];
        if (minor is "x" or "xxx" or "major") return major.ToString(CultureInfo.InvariantCulture) + ".x";
        if (!int.TryParse(minor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minorNumber)) return null;
        return major.ToString(CultureInfo.InvariantCulture) + "." + minorNumber.ToString("D3", CultureInfo.InvariantCulture);
    }

    private void Index(DptInfo info)
    {
        _byCode.TryAdd(info.Subject, info);
        _byCode.TryAdd(info.Code, info);
        _byCode.TryAdd("DPST-" + info.Major + "-" + (info.Minor?.ToString(CultureInfo.InvariantCulture) ?? "x"), info);

        foreach (var code in info.Codes)
        {
            if (string.IsNullOrWhiteSpace(code)) continue;
            _byCode.TryAdd(code.Trim(), info);
            if (NormalizeCode(code) is { } normalized) _byCode.TryAdd(normalized, info);
        }
    }

    private static int? IntValue(Ontology ontology, string subject, string predicate)
        => int.TryParse(ontology.Value(subject, predicate), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}

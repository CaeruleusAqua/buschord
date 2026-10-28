using System.Globalization;

namespace Knx;

/// <summary>A bounded integer condition without whitespace normalization or script evaluation.</summary>
internal sealed class StrictIntegerCondition
{
    internal string Operator { get; }
    internal IReadOnlyList<long> Values { get; }
    private StrictIntegerCondition(string op, long[] values)
    { Operator = op; Values = Array.AsReadOnly(values); }
    internal bool Matches(long value) => Operator switch
    {
        "=" => value == Values[0], "!=" => value != Values[0], ">" => value > Values[0],
        "<" => value < Values[0], ">=" => value >= Values[0], "<=" => value <= Values[0],
        _ => Values.Contains(value),
    };
    internal static bool TryParse(string text, out StrictIntegerCondition? condition)
    {
        condition = null;
        if (text.Length is 0 or > 4096 || text.Any(c => c > 127 || char.IsControl(c))) return false;
        var tokens = text.Split(' ', 257, StringSplitOptions.None);
        if (tokens.Length is 0 or > 256) return false;
        var op = new[] { ">=", "<=", "!=", "=", ">", "<" }.FirstOrDefault(p => tokens[0].StartsWith(p, StringComparison.Ordinal));
        if (op is not null)
        {
            if (tokens.Length != 1 || !Integer(tokens[0][op.Length..], out var value)) return false;
            condition = new(op, [value]); return true;
        }
        var values = new long[tokens.Length];
        for (var i = 0; i < tokens.Length; i++) if (!Integer(tokens[i], out values[i])) return false;
        condition = new("set", values); return true;
    }
    private static bool Integer(string text, out long value)
    {
        value = 0;
        var digits = text.StartsWith('-') ? text.AsSpan(1) : text.AsSpan();
        return digits.Length > 0 && !digits.ContainsAnyExceptInRange('0', '9')
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
}

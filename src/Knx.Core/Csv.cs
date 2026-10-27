namespace Knx;

/// <summary>
/// CSV field/row formatting shared by every exporter. Fields are quoted per RFC 4180 and
/// spreadsheet formulas are neutralised with a leading apostrophe.
/// </summary>
public static class Csv
{
    /// <summary>
    /// Formats one field. A single quote is prefixed when the first non-whitespace character
    /// is <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c>, or when the value starts with a tab, CR or
    /// LF. Spreadsheets then treat the cell as text (Excel/LibreOffice do not show the quote).
    /// Negative numbers therefore become text; genuine spreadsheet formulas can no longer run.
    /// The field is quoted when it contains the separator, a quote or a line break.
    /// </summary>
    public static string Field(string? value, char separator = ';')
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = Neutralize(value);
        return text.Contains(separator) || text.Contains('"') || text.Contains('\r') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : text;
    }

    /// <summary>Formats one row; the caller appends the line break and header text is written raw.</summary>
    public static string Row(char separator, params string?[] fields)
        => string.Join(separator, fields.Select(field => Field(field, separator)));

    private static string Neutralize(string value)
    {
        if (value[0] is '\t' or '\r' or '\n') return "'" + value;
        var trimmed = value.TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@' ? "'" + value : value;
    }
}

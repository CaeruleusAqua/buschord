using System.Globalization;
using System.Numerics;
using System.Text;

namespace Knx;

/// <summary>Static storage-value validation, without evaluating dynamic KSXML.</summary>
public static class ParameterValidation
{
    public static bool TryValidate(ParameterInstance instance, string? value, out string? error)
    {
        if (!instance.IsEnabled || instance.UnsupportedReason is not null)
        {
            error = instance.UnsupportedReason ?? "Parameter is read-only.";
            return false;
        }
        if (instance.ParameterType?.DataType == ParameterDataType.RawData)
        {
            error = "Raw data requires a verified profile adapter.";
            return false;
        }
        return TryValidate(instance.ParameterType, value, out error);
    }

    public static bool TryValidate(ParameterType? type, string? value, out string? error)
    {
        error = null;
        if (type is null || type.UnsupportedReason is not null)
            error = type?.UnsupportedReason ?? "Parameter type is unknown.";
        else if (value is null) error = "A value is required.";
        else if (type.DataType == ParameterDataType.Boolean)
        {
            if (value is not ("0" or "1")) error = "Use 0 or 1 for a Boolean value.";
        }
        else if (type.DataType == ParameterDataType.Enumeration)
        {
            if (!type.Restrictions.Any(r => r.EnumerationValue == value)) error = "Select a declared enumeration value (not its label).";
        }
        else if (type.DataType is ParameterDataType.Number or ParameterDataType.Float)
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                error = "Use a finite invariant-culture number.";
            else if (type.DataType == ParameterDataType.Number)
            {
                if (!BigInteger.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
                    error = "Use an integer.";
                else if (type.NumberType == "unsignedInt" && integer < 0)
                    error = "Use an unsigned integer.";
                else if (type.SizeInBit is > 0 and <= 64)
                {
                    var signed = type.NumberType == "signedInt";
                    var limit = BigInteger.One << (type.SizeInBit - (signed ? 1 : 0));
                    if (integer < (signed ? -limit : BigInteger.Zero) || integer >= limit)
                        error = "Value does not fit the declared bit size.";
                }
                if (error is null && type.Restrictions.Any(r =>
                    BigInteger.TryParse(r.MinimumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var min) && integer < min ||
                    BigInteger.TryParse(r.MaximumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var max) && integer > max))
                    error = "Value is outside the declared range.";
            }
            if (error is null && type.Restrictions.Any(r => r.Min is { } min && number < min || r.Max is { } max && number > max))
                error = "Value is outside the declared range.";
        }
        else if (type.DataType == ParameterDataType.Time)
        {
            error = TimeDeclarationError(type);
            if (error is null)
            {
                if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var duration))
                    error = "Use an Int64 integer in the declared time unit.";
                else if (TryTimeRange(type, out var minimum, out var maximum) && (duration < minimum || duration > maximum))
                    error = "Value is outside the declared time range.";
            }
        }
        else if (type.DataType == ParameterDataType.IPAddress)
        {
            // Preserve the declared family. Avoid the permissive IPv4 forms accepted
            // by IPAddress.TryParse (single integers, shortened addresses and hex).
            var version = type.AddressVersion ?? "IPv4";
            var valid = version == "IPv4"
                ? value.Split('.') is { Length: 4 } octets && octets.All(o => o.Length is >= 1 and <= 3
                    && o.All(char.IsAsciiDigit) && byte.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                : version == "IPv6" && !value.Contains('%') && value == value.Trim()
                    && System.Net.IPAddress.TryParse(value, out var address)
                    && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (!valid) error = "Use an address in the declared IP version.";
            else if (type.AddressType is not (null or "HostAddress" or "GatewayAddress" or "UnicastAddress" or "BroadcastAddress" or "MulticastAddress" or "SubnetMask"))
                error = "Unknown IP address type.";
        }
        else if (type.DataType == ParameterDataType.Text)
        {
            // Conservative UTF-8 storage bound; no truncation or encoding conversion on open.
            if (type.SizeInBit > 0 && Encoding.UTF8.GetByteCount(value) > type.SizeInBit / 8)
                error = "Text exceeds the declared storage size.";
            else if (type.Restrictions.Any(r => r.Pattern is not null))
                error = "XML-schema text patterns are not evaluated by the static editor.";
        }
        else if (type.DataType == ParameterDataType.RawData)
            TryDecodeRawData(type, value, out _, out error);
        else error = "Validation for this parameter type is not supported.";
        return error is null;
    }

    /// <summary>Decodes bounded Base64 storage data for a verified caller; this grants no editor or plugin capability.</summary>
    public static bool TryDecodeRawData(ParameterType? type, string? value, out byte[] data, out string? error)
    {
        data = [];
        error = type is null ? "Parameter type is unknown." : type.UnsupportedReason ?? RawDataDeclarationError(type);
        if (error is not null) return false;
        if (value is null) { error = "A value is required."; return false; }
        // Convert.FromBase64String accepts ASCII whitespace. Keep that behavior without allowing
        // unbounded whitespace or an oversized decode allocation. Never truncate the stored value.
        if (value.Length > 16 * 1024 * 1024) { error = "Raw data text exceeds the safety limit."; return false; }
        var maximum = type!.RawDataMaxSize!.Value;
        var encodedLimit = checked((maximum + 2) / 3 * 4);
        var encodedCharacters = 0;
        foreach (var character in value)
            if (character is not (' ' or '\t' or '\r' or '\n') && ++encodedCharacters > encodedLimit)
            { error = "Raw data exceeds the declared byte limit."; return false; }
        try
        {
            var decoded = Convert.FromBase64String(value);
            if (decoded.Length > maximum) { error = "Raw data exceeds the declared byte limit."; return false; }
            data = decoded;
            return true;
        }
        catch (FormatException) { error = "Use Base64 storage data."; return false; }
    }

    /// <summary>Produces canonical Base64 storage data after checking the declared decoded byte limit.</summary>
    public static string EncodeRawData(ParameterType type, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(type);
        var error = type.UnsupportedReason ?? RawDataDeclarationError(type);
        if (error is not null) throw new InvalidDataException(error);
        if (data.Length > type.RawDataMaxSize!.Value) throw new InvalidDataException("Raw data exceeds the declared byte limit.");
        return Convert.ToBase64String(data);
    }

    internal static string? RawDataDeclarationError(ParameterType type) => type.DataType != ParameterDataType.RawData
        || type.SizeInBit != 0 || type.RawDataMaxSize is not (>= 1 and <= 1_048_572)
        ? "TypeRawData requires MaxSize from 1 to 1048572 bytes without a bit-size declaration." : null;

    internal static bool IsKnownTimeUnit(string? unit) => unit is "Hours" or "Minutes" or "Seconds"
        or "HundredMilliseconds" or "TenMilliseconds" or "Milliseconds"
        or "PackedSecondsAndMilliseconds" or "PackedDaysHoursMinutesAndSeconds" or "PackedMinutesSecondsAndMilliseconds";

    internal static string? TimeDeclarationError(ParameterType type)
    {
        if (type.SizeInBit is < 8 or > 64) return "TypeTime requires a declared bit size from 8 to 64.";
        if (!IsKnownTimeUnit(type.TimeUnit)) return "Unknown or missing TypeTime unit.";
        if (!TryTimeRange(type, out var minimum, out var maximum) || minimum > maximum)
            return "TypeTime requires an ordered pair of Int64 bounds.";
        if (type.TimeUIHint is not (null or "Time_ss" or "Time_ssf" or "Time_ssff" or "Time_ssfff"
            or "Time_mmss" or "Time_mmssf" or "Time_mmssff" or "Time_mmssfff"
            or "Time_hhmm" or "Time_hhmmss" or "Time_hhmmssf" or "Time_hhmmssff" or "Time_hhmmssfff"
            or "Time_dhh" or "Time_dhhmm" or "Time_dhhmmss"
            or "Duration_mmss" or "Duration_mmssf" or "Duration_mmssff" or "Duration_mmssfff"
            or "Duration_hhmm" or "Duration_hhmmss" or "Duration_hhmmssf" or "Duration_hhmmssff" or "Duration_hhmmssfff"))
            return "Unknown TypeTime display hint.";
        return null;
    }

    private static bool TryTimeRange(ParameterType type, out long minimum, out long maximum)
    {
        minimum = maximum = 0;
        var ranges = type.Restrictions.Where(r => r.MinimumValue is not null || r.MaximumValue is not null).ToArray();
        return ranges.Length == 1
            && long.TryParse(ranges[0].MinimumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out minimum)
            && long.TryParse(ranges[0].MaximumValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out maximum);
    }
}

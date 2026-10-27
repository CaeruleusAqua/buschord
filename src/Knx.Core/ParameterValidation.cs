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
        else if (type.DataType == ParameterDataType.Text)
        {
            // Conservative UTF-8 storage bound; no truncation or encoding conversion on open.
            if (type.SizeInBit > 0 && Encoding.UTF8.GetByteCount(value) > type.SizeInBit / 8)
                error = "Text exceeds the declared storage size.";
            else if (type.Restrictions.Any(r => r.Pattern is not null))
                error = "XML-schema text patterns are not evaluated by the static editor.";
        }
        else error = "Validation for this parameter type is not supported.";
        return error is null;
    }
}

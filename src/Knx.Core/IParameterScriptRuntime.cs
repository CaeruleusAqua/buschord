using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Knx;

/// <summary>Explicit capability boundary: the core never loads a script engine or exposes host objects.</summary>
public interface IParameterScriptRuntime
{
    IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<string> outputs);
}

/// <summary>Optional capability for bounded shared function declarations; the core still owns no engine.</summary>
public interface IParameterFunctionScriptRuntime : IParameterScriptRuntime
{
    IReadOnlyDictionary<string, object?> ExecuteFunction(ParameterFunctionScript program, ParameterFunctionInvocation invocation);
}

/// <summary>A detached, content-addressed common script and its explicitly permitted entry points.</summary>
public sealed class ParameterFunctionScript
{
    public const int MaximumSourceCharacters = 1_048_576;
    public const int MaximumFunctions = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public ParameterFunctionScript(string commonSource, IEnumerable<string> functionNames)
    {
        if (string.IsNullOrWhiteSpace(commonSource) || commonSource.Length > MaximumSourceCharacters)
            throw new InvalidDataException("Common script exceeds its source limit.");
        var names = functionNames.Take(MaximumFunctions + 1).ToArray();
        if (names.Length is 0 or > MaximumFunctions || names.Any(name => !Identifier(name))
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Invalid common-script function names.");
        Array.Sort(names, StringComparer.Ordinal);
        byte[] sourceBytes;
        try { sourceBytes = StrictUtf8.GetBytes(commonSource); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Invalid common-script Unicode.", ex); }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("BusChord.FunctionScript.v1"u8);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, sourceBytes.Length);
        hash.AppendData(length); hash.AppendData(sourceBytes);
        foreach (var name in names) { hash.AppendData(StrictUtf8.GetBytes(name)); hash.AppendData("\0"u8); }
        Id = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        CommonSource = commonSource;
        SourceUtf8Bytes = sourceBytes.Length;
        FunctionNames = Array.AsReadOnly(names);
    }

    public string Id { get; }
    public string CommonSource { get; }
    public int SourceUtf8Bytes { get; }
    public IReadOnlyList<string> FunctionNames { get; }
    public static bool Identifier(string? value) => value is not null
        && Regex.IsMatch(value, @"\A[A-Za-z_$][A-Za-z0-9_$]{0,63}\z", RegexOptions.NonBacktracking);
}

/// <summary>Primitive input/output seed values and JSON data, never generated manufacturer code.</summary>
public sealed class ParameterFunctionInvocation
{
    public const int MaximumBindings = 256, MaximumStringCharacters = 4096, MaximumContextCharacters = 4096;

    public ParameterFunctionInvocation(string functionName, IEnumerable<string> inputAliases, IEnumerable<string> outputAliases,
        IEnumerable<object?> values, string contextJson, int budgetMilliseconds = 8000)
    {
        var inputs = inputAliases.Take(MaximumBindings + 1).ToArray();
        var outputs = outputAliases.Take(MaximumBindings + 1).ToArray();
        var copiedValues = values.Take(MaximumBindings + 1).ToArray();
        var aliases = inputs.Concat(outputs).ToArray();
        if (!ParameterFunctionScript.Identifier(functionName) || inputs.Length == 0 || outputs.Length == 0
            || aliases.Length > MaximumBindings || aliases.Length != copiedValues.Length
            || aliases.Any(alias => !ParameterFunctionScript.Identifier(alias)) || aliases.Distinct(StringComparer.Ordinal).Count() != aliases.Length
            || copiedValues.Any(value => !Scalar(value)) || budgetMilliseconds is < 1 or > 8000)
            throw new InvalidDataException("Invalid function invocation bindings, primitive values or budget.");
        FunctionName = functionName;
        InputAliases = Array.AsReadOnly(inputs); OutputAliases = Array.AsReadOnly(outputs); Values = Array.AsReadOnly(copiedValues);
        ContextJson = NormalizeContext(contextJson);
        BudgetMilliseconds = budgetMilliseconds;
    }

    public string FunctionName { get; }
    public IReadOnlyList<string> InputAliases { get; }
    public IReadOnlyList<string> OutputAliases { get; }
    public IReadOnlyList<object?> Values { get; }
    public string ContextJson { get; }
    public int BudgetMilliseconds { get; }

    public static bool Scalar(object? value) => value switch
    { null or bool => true, string text => text.Length <= MaximumStringCharacters, double number => double.IsFinite(number), _ => false };

    private static string NormalizeContext(string text)
    {
        if (text.Length > MaximumContextCharacters) throw new InvalidDataException("Function context exceeds its limit.");
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 9 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Function context must be an object.");
            var nodes = 0;
            void Validate(JsonElement value, int depth)
            {
                if (++nodes > 256 || depth > 8) throw new InvalidDataException("Function context exceeds its data budget.");
                if (value.ValueKind == JsonValueKind.Object)
                {
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in value.EnumerateObject())
                    { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate context key."); Validate(property.Value, depth + 1); }
                }
                else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Validate(child, depth + 1);
                else if (value.ValueKind == JsonValueKind.Number && (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
                    throw new InvalidDataException("Non-finite context number.");
                else if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Invalid context data.");
            }
            Validate(document.RootElement, 0);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid function context JSON.", ex); }
    }
}

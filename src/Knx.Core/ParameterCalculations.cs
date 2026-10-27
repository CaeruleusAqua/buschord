using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Knx;

internal sealed class ParameterCalculations(XElement root, IParameterScriptRuntime? runtime, string? editedReference)
{
    private readonly Stopwatch _budget = Stopwatch.StartNew();
    private readonly Dictionary<XElement, (Dictionary<string, object?> Inputs, IReadOnlyDictionary<string, object?> Outputs)> _cache = [];
    private readonly Dictionary<XElement, string> _failures = [];
    private readonly Dictionary<XElement, string> _sources = [];
    private readonly Dictionary<XElement, (string Function, string Context)> _functionCalls = [];
    private ParameterFunctionScript? _functionProgram;
    private readonly List<XElement> _calculations = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterCalculation"
        && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
    private readonly Dictionary<string, ParameterType?> _types = runtime is not null
        && root.DescendantsAndSelf().Any(e => e.Name.LocalName == "ParameterCalculation") ? Types(root) : [];
    private readonly Dictionary<XElement, bool>? _directions = Directions(root, editedReference);
    internal int Executions { get; private set; }
    internal int Failures => _failures.Count;

    internal void Apply(Dictionary<string, string?> values, Func<string, string?> read,
        HashSet<string> assigned, HashSet<string> unsupported, HashSet<string> blocked)
    {
        foreach (var calculation in _calculations)
        {
            if (runtime is null) { unsupported.Add("Parameter calculations (JavaScript) are not executed."); continue; }
            var left = Side(calculation, "LParameters");
            var right = Side(calculation, "RParameters");
            var reverse = _directions?.GetValueOrDefault(calculation) == true;
            var inputBindings = reverse ? right : left;
            var outputBindings = reverse ? left : right;
            var bindings = left.Concat(right).ToList();
            try
            {
                if (_failures.TryGetValue(calculation, out var failure)) throw new InvalidDataException(failure);
                if (_directions is null) throw new InvalidDataException("Calculation dependency graph exceeds its safety limit.");
                if (_calculations.Count > 2048 || Executions >= 4096 || _budget.Elapsed > TimeSpan.FromSeconds(8))
                    throw new InvalidDataException("Parameter calculation budget exceeded.");
                if ((string?)calculation.Attribute("Language") != "JavaScript" || left.Count == 0 || right.Count == 0 || bindings.Count > 256
                    || bindings.Any(b => b.RefId.Length == 0 || b.Alias.Length == 0 || !values.ContainsKey(b.RefId))
                    || bindings.Select(b => b.Alias).Distinct().Count() != bindings.Count
                    || bindings.Select(b => b.RefId).Distinct().Count() != bindings.Count)
                    throw new InvalidDataException("Invalid parameter calculation bindings or language.");
                var functionProfile = calculation.Attribute((reverse ? "RL" : "LR") + "TransformationFunc") is not null;
                var functionRuntime = functionProfile ? runtime as IParameterFunctionScriptRuntime : null;
                string? source = null;
                if (functionRuntime is null)
                {
                    if (!_sources.TryGetValue(calculation, out source))
                        _sources[calculation] = source = Source(calculation, reverse, inputBindings, outputBindings);
                }
                else
                {
                    _functionProgram ??= FunctionProgram();
                    if (!_functionCalls.ContainsKey(calculation))
                    {
                        var direction = reverse ? "RLTransformation" : "LRTransformation";
                        var function = (string?)calculation.Attribute(direction + "Func") ?? "";
                        if (!Identifier(function) || calculation.Elements().Any(e => e.Name.LocalName == direction)
                            || bindings.Any(binding => !Identifier(binding.Alias)))
                            throw new InvalidDataException("Invalid or ambiguous parameter transformation function.");
                        var contextText = (string?)calculation.Attribute(direction + "Parameters");
                        _functionCalls[calculation] = (function, Context(string.IsNullOrEmpty(contextText) ? "{}" : contextText));
                    }
                }
                var consumed = inputBindings.Select(b => b.RefId).ToHashSet(StringComparer.Ordinal);
                var inputs = bindings.ToDictionary(b => b.Alias, b => Input(read(b.RefId), _types.GetValueOrDefault(b.RefId), consumed.Contains(b.RefId)));
                IReadOnlyDictionary<string, object?> outputs;
                if (_cache.TryGetValue(calculation, out var cached) && cached.Inputs.Count == inputs.Count
                    && inputs.All(p => cached.Inputs.TryGetValue(p.Key, out var previous) && Equals(previous, p.Value))) outputs = cached.Outputs;
                else
                {
                    if (functionRuntime is not null)
                    {
                        var remaining = (int)(8000 - _budget.Elapsed.TotalMilliseconds);
                        if (remaining <= 0) throw new InvalidDataException("Parameter calculation budget exceeded.");
                        var call = _functionCalls[calculation];
                        var invocation = new ParameterFunctionInvocation(call.Function,
                            inputBindings.Select(binding => binding.Alias), outputBindings.Select(binding => binding.Alias),
                            inputBindings.Concat(outputBindings).Select(binding => inputs[binding.Alias]), call.Context, Math.Min(8000, remaining));
                        Executions++;
                        outputs = functionRuntime.ExecuteFunction(_functionProgram!, invocation);
                        if (_budget.Elapsed > TimeSpan.FromSeconds(8)) throw new InvalidDataException("Parameter calculation budget exceeded.");
                    }
                    else
                    {
                        var runtimeInputs = functionProfile ? inputBindings.Concat(outputBindings)
                            .Select((b, index) => (Key: $"__knxValue{index}", Value: inputs[b.Alias]))
                            .ToDictionary(p => p.Key, p => p.Value) : inputs;
                        Executions++;
                        outputs = runtime.Execute(source!, runtimeInputs, outputBindings.Select(b => b.Alias).ToArray());
                    }
                    _cache[calculation] = (inputs, outputs);
                }
                var changes = new Dictionary<string, string?>();
                foreach (var binding in outputBindings)
                {
                    if (!outputs.TryGetValue(binding.Alias, out var output)) throw new InvalidDataException("Missing calculation output.");
                    if (Equals(inputs[binding.Alias], output))
                    {
                        if (!ParameterValidation.TryValidate(_types.GetValueOrDefault(binding.RefId), read(binding.RefId), out _))
                            throw new InvalidDataException("Unchanged calculation output violates its parameter type.");
                        if (values[binding.RefId] != read(binding.RefId)) changes[binding.RefId] = read(binding.RefId);
                        continue;
                    }
                    var text = output switch
                    {
                        null => null, string s when s.Length <= 4096 => s, bool b => b ? "1" : "0",
                        double n when double.IsFinite(n) => n.ToString("R", CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException("Invalid calculation output."),
                    };
                    if (_types.GetValueOrDefault(binding.RefId) is not { } type || !ParameterValidation.TryValidate(type, text, out _))
                        throw new InvalidDataException("Calculation output violates its parameter type.");
                    changes[binding.RefId] = text;
                }
                foreach (var (id, value) in changes) { values[id] = value; assigned.Add(id); }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                _failures[calculation] = ex is InvalidDataException ? ex.Message : "Invalid parameter calculation.";
                unsupported.Add(_failures[calculation]);
                foreach (var binding in outputBindings) blocked.Add(binding.RefId);
            }
        }
    }

    private ParameterFunctionScript FunctionProgram()
    {
        var scripts = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Script" && e.Parent?.Name.LocalName == "Static"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).Take(2).ToArray();
        if (scripts.Length != 1) throw new InvalidDataException("A transformation function requires one unambiguous static script.");
        var names = _calculations.SelectMany(calculation => new[]
            { (string?)calculation.Attribute("LRTransformationFunc"), (string?)calculation.Attribute("RLTransformationFunc") })
            .Where(name => name is not null).Select(name => name!).Distinct(StringComparer.Ordinal);
        return new ParameterFunctionScript(scripts[0].Value, names);
    }

    private string Source(XElement calculation, bool reverse, List<(string RefId, string Alias)> inputs,
        List<(string RefId, string Alias)> outputs)
    {
        var direction = reverse ? "RLTransformation" : "LRTransformation";
        var inline = calculation.Elements().Where(e => e.Name.LocalName == direction).ToList();
        var function = (string?)calculation.Attribute(direction + "Func");
        if (function is null)
        {
            if (inline.Count != 1 || string.IsNullOrWhiteSpace(inline[0].Value)) throw new InvalidDataException("Missing parameter transformation.");
            if (inline[0].Value.Length > 65536) throw new InvalidDataException("Parameter transformation exceeds its safety limit.");
            return inline[0].Value;
        }
        if (inline.Count != 0 || !Identifier(function) || inputs.Concat(outputs).Any(b => !Identifier(b.Alias)))
            throw new InvalidDataException("Invalid or ambiguous parameter transformation function.");
        var scripts = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "Script" && e.Parent?.Name.LocalName == "Static"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
        if (scripts.Count != 1 || string.IsNullOrWhiteSpace(scripts[0].Value))
            throw new InvalidDataException("A transformation function requires one unambiguous static script.");
        if (scripts[0].Value.Length > 65536) throw new InvalidDataException("Static script exceeds its safety limit.");
        var contextText = (string?)calculation.Attribute(direction + "Parameters");
        var context = Context(string.IsNullOrEmpty(contextText) ? "{}" : contextText);
        // Attribute data is serialized; common code executes only inside the explicit isolated capability.
        // Capture existing primitive values before common declarations enter their own function scope.
        var source = new StringBuilder("(function(__knxValues,__knxContext,__knxObject,__knxGlobal){\n")
            .Append("const __knxInput=__knxObject.create(null),__knxOutput=__knxObject.create(null);\n");
        var bindings = inputs.Concat(outputs).ToList();
        for (var index = 0; index < bindings.Count; index++)
            source.Append(index < inputs.Count ? "__knxInput[" : "__knxOutput[")
                .Append(JsonSerializer.Serialize(bindings[index].Alias)).Append("]=__knxValues[").Append(index).Append("];\n");
        source.Append(scripts[0].Value).Append('\n')
            .Append("if(typeof ").Append(function).Append("!=='function')throw new Error('Missing transformation function');\n")
            .Append(function).Append("(__knxInput,__knxOutput,__knxContext);\n");
        foreach (var output in outputs)
        {
            var key = JsonSerializer.Serialize(output.Alias);
            source.Append("if(!__knxObject.prototype.hasOwnProperty.call(__knxOutput,").Append(key).Append("))throw new Error('Missing output');")
                .Append("__knxGlobal[").Append(key).Append("]=__knxOutput[").Append(key).Append("];\n");
        }
        source.Append("})([").Append(string.Join(',', bindings.Select((_, index) => $"__knxValue{index}")))
            .Append("],JSON.parse(").Append(JsonSerializer.Serialize(context)).Append("),Object,globalThis);\n");
        if (source.Length > 65536) throw new InvalidDataException("Parameter transformation exceeds its safety limit.");
        return source.ToString();
    }

    private static bool Identifier(string value) => Regex.IsMatch(value, @"\A[A-Za-z_$][A-Za-z0-9_$]{0,63}\z", RegexOptions.NonBacktracking);

    private static string Context(string text)
    {
        if (text.Length > 4096) throw new InvalidDataException("Transformation context exceeds its safety limit.");
        var position = 0; var nodes = 0;
        void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        string Quoted()
        {
            var start = position++;
            while (position < text.Length)
            {
                var character = text[position++];
                if (character == '\\') { position++; continue; }
                if (character != '"') continue;
                try
                {
                    using var value = JsonDocument.Parse(text[start..position]);
                    return JsonSerializer.Serialize(value.RootElement.GetString());
                }
                catch (JsonException) { break; }
            }
            throw new InvalidDataException("Invalid transformation context string.");
        }
        string Value(int depth)
        {
            Space();
            if (++nodes > 256 || depth > 8 || position >= text.Length) throw new InvalidDataException("Invalid transformation context or context budget exceeded.");
            if (text[position] == '"') return Quoted();
            if (text[position] is '{' or '[')
            {
                var opening = text[position++]; var closing = opening == '{' ? '}' : ']';
                var result = new StringBuilder().Append(opening); var keys = new HashSet<string>(StringComparer.Ordinal);
                Space();
                if (position < text.Length && text[position] == closing) { position++; return result.Append(closing).ToString(); }
                while (true)
                {
                    Space();
                    if (opening == '{')
                    {
                        string key;
                        if (position < text.Length && text[position] == '"') key = Quoted();
                        else
                        {
                            var start = position;
                            while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] is '_' or '$')) position++;
                            var name = text[start..position];
                            if (!Identifier(name)) throw new InvalidDataException("Invalid transformation context key.");
                            key = JsonSerializer.Serialize(name);
                        }
                        if (!keys.Add(key)) throw new InvalidDataException("Duplicate transformation context key.");
                        Space();
                        if (position >= text.Length || text[position++] != ':') throw new InvalidDataException("Invalid transformation context object.");
                        result.Append(key).Append(':');
                    }
                    result.Append(Value(depth + 1)); Space();
                    if (position >= text.Length) throw new InvalidDataException("Incomplete transformation context.");
                    var separator = text[position++];
                    if (separator == closing) return result.Append(closing).ToString();
                    if (separator != ',') throw new InvalidDataException("Invalid transformation context separator.");
                    result.Append(',');
                }
            }
            var literalStart = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not (',' or '}' or ']')) position++;
            var literal = text[literalStart..position];
            try
            {
                using var value = JsonDocument.Parse(literal);
                if (value.RootElement.ValueKind is JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null) return literal;
                if (value.RootElement.ValueKind == JsonValueKind.Number && value.RootElement.TryGetDouble(out var number) && double.IsFinite(number)) return literal;
            }
            catch (JsonException) { }
            throw new InvalidDataException("Transformation context contains an unsupported expression.");
        }
        Space();
        if (position >= text.Length || text[position] != '{') throw new InvalidDataException("Transformation context must be an object.");
        var json = Value(0); Space();
        if (position != text.Length) throw new InvalidDataException("Trailing transformation context expression.");
        return json;
    }

    private static List<(string RefId, string Alias)> Side(XElement calculation, string side) => calculation.Elements()
        .Where(e => e.Name.LocalName == side).SelectMany(e => e.Elements()).Where(e => e.Name.LocalName == "ParameterRefRef")
        .Select(e => ((string?)e.Attribute("RefId") ?? "", (string?)e.Attribute("AliasName") ?? "")).ToList();

    private static Dictionary<XElement, bool>? Directions(XElement root, string? edited)
    {
        var directions = new Dictionary<XElement, bool>();
        if (edited is null) return directions;
        var calculations = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterCalculation"
            && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef")).ToList();
        if (calculations.Count > 2048) return null;
        var dependents = new Dictionary<string, List<(XElement Calculation, bool Reverse)>>();
        var outputs = new Dictionary<(XElement, bool), List<(string RefId, string Alias)>>();
        foreach (var calculation in calculations)
        {
            var left = Side(calculation, "LParameters"); var right = Side(calculation, "RParameters");
            if (left.Count + right.Count > 256) return null;
            outputs[(calculation, true)] = left; outputs[(calculation, false)] = right;
            foreach (var (reference, reverse) in left.Select(b => (b.RefId, false)).Concat(right.Select(b => (b.RefId, true))))
            {
                if (!dependents.TryGetValue(reference, out var list)) dependents[reference] = list = [];
                list.Add((calculation, reverse));
            }
        }
        var queue = new Queue<string>(); queue.Enqueue(edited);
        var visited = new HashSet<string>();
        while (queue.TryDequeue(out var reference))
        {
            if (!visited.Add(reference)) continue;
            if (visited.Count > 4096) return null;
            foreach (var (calculation, reverse) in dependents.GetValueOrDefault(reference) ?? [])
            {
                if (directions.ContainsKey(calculation)) continue;
                directions[calculation] = reverse;
                foreach (var binding in outputs[(calculation, reverse)]) queue.Enqueue(binding.RefId);
            }
        }
        return directions;
    }

    private static object? Input(string? value, ParameterType? type, bool consumed)
    {
        if (type is null || type.UnsupportedReason is not null) throw new InvalidDataException("Unknown or unsupported calculation input type.");
        if (type.DataType is not (ParameterDataType.Number or ParameterDataType.Float or ParameterDataType.Enumeration
            or ParameterDataType.Boolean or ParameterDataType.Text or ParameterDataType.IPAddress or ParameterDataType.Time))
            throw new InvalidDataException("Calculation input encoding is not supported for this parameter type.");
        if (type.DataType == ParameterDataType.Time && ParameterValidation.TimeDeclarationError(type) is not null)
            throw new InvalidDataException("Calculation time unit or declaration is not supported.");
        if (consumed && !ParameterValidation.TryValidate(type, value, out _))
            throw new InvalidDataException("Calculation input violates its parameter type.");
        if (value is null) return null;
        if (value.Length > 4096) throw new InvalidDataException("Script input exceeds its safety limit.");
        if (type.DataType is ParameterDataType.Text or ParameterDataType.IPAddress) return value;
        if (type.DataType == ParameterDataType.Boolean) return value switch
        {
            "1" or "true" or "True" => true, "0" or "false" or "False" => false,
            _ => throw new InvalidDataException("Invalid Boolean calculation input."),
        };
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new InvalidDataException("Invalid numeric calculation input.");
        if (type?.DataType != ParameterDataType.Float && Math.Abs(number) > 9007199254740991d)
            throw new InvalidDataException("Integer calculation input exceeds exact JavaScript precision.");
        return number;
    }

    private static Dictionary<string, ParameterType?> Types(XElement root)
    {
        var types = root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterType").Select(Ksxml.ParseParameterType)
            .OfType<ParameterType>().GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        var parameters = root.DescendantsAndSelf().Where(e => e.Name.LocalName is "Parameter" or "ParameterInstance")
            .Where(e => e.Attribute("Id") is not null).GroupBy(e => (string)e.Attribute("Id")!).ToDictionary(g => g.Key, g => g.First());
        return root.DescendantsAndSelf().Where(e => e.Name.LocalName == "ParameterRef" && e.Attribute("Id") is not null)
            .GroupBy(e => (string)e.Attribute("Id")!).ToDictionary(g => g.Key, g =>
            {
                var reference = g.First();
                parameters.TryGetValue((string?)reference.Attribute("RefId") ?? "", out var parameter);
                return types.GetValueOrDefault((string?)reference.Attribute("ParameterType") ?? (string?)reference.Attribute("ParameterTypeId")
                    ?? (string?)parameter?.Attribute("ParameterType") ?? (string?)parameter?.Attribute("ParameterTypeId") ?? "");
            });
    }
}

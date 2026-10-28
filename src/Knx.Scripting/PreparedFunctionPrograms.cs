using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Acornima;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Runtime.Descriptors;

namespace Knx.Scripting;

public sealed record FunctionScriptStatistics(int CachedPrograms, int CachedNodes, int CachedCodeBytes,
    long Preparations, long CacheHits, long CacheMisses, long Evictions, long Executions, double PrepareMilliseconds);

internal sealed class FunctionProgramMissingException() : Exception("Prepared program is unavailable.");

/// <summary>Only engine-neutral prepared syntax survives a call. Each invocation gets its own realm.</summary>
internal sealed class PreparedFunctionPrograms
{
    internal const int MaximumPrograms = 2, MaximumAstNodes = 250_000, MaximumCachedNodes = 500_000;
    internal const int MaximumGeneratedCharacters = 131_072, MaximumGeneratedNodes = 32_768, MaximumCachedCodeBytes = 8_388_608;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(8);
    private static readonly ScriptPreparationOptions Preparation = new()
    { ParsingOptions = new ScriptParsingOptions { CompileRegex = false, RegexTimeout = TimeSpan.FromMilliseconds(25), Tolerant = false } };
    private static readonly Prepared<Script> Hardening = Engine.PrepareScript(
        "globalThis.Date=undefined;globalThis.Temporal=undefined;globalThis.Intl=undefined;Math.random=function(){throw new Error('Unavailable');};", options: Preparation);
    private static readonly Prepared<Script> Invocation = Engine.PrepareScript("__knxRun(JSON.parse(__knxFrameJson),Object);", options: Preparation);
    private readonly Dictionary<string, Entry> _programs = new(StringComparer.Ordinal);
    private long _preparations, _hits, _misses, _evictions, _executions;
    private double _prepareMilliseconds;
    private sealed record Entry(Prepared<Script> Script, string[] Functions, int Nodes, int CodeBytes, long Created)
    { public long Used { get; set; } = Created; }

    public FunctionScriptStatistics Statistics => new(_programs.Count, _programs.Values.Sum(entry => entry.Nodes),
        _programs.Values.Sum(entry => entry.CodeBytes), _preparations, _hits, _misses, _evictions, _executions, _prepareMilliseconds);

    public void RemoveExpired()
    {
        foreach (var key in _programs.Where(pair => Stopwatch.GetElapsedTime(pair.Value.Created) >= Lifetime).Select(pair => pair.Key).ToArray())
        { _programs.Remove(key); _evictions++; }
    }

    public TimeSpan? UntilExpiry => _programs.Count == 0 ? null : _programs.Values.Select(entry => Lifetime - Stopwatch.GetElapsedTime(entry.Created)).Min();

    public void Register(ParameterFunctionScript program, string expectedId)
    {
        if (!string.Equals(program.Id, expectedId, StringComparison.Ordinal)) throw new InvalidDataException("Prepared program identity mismatch.");
        RemoveExpired();
        if (_programs.TryGetValue(program.Id, out var existing)) { existing.Used = Stopwatch.GetTimestamp(); return; }
        var timer = Stopwatch.StartNew();
        // Preparation is not covered by Engine execution constraints. Bound source syntax first,
        // then the combined factory AST, with the client deadline and child heap as outer limits.
        CountSourceNodes(program.CommonSource, MaximumAstNodes);
        var generated = Factory(program.FunctionNames, "");
        if (generated.Length > MaximumGeneratedCharacters) throw new InvalidDataException("Generated wrapper exceeds its limit.");
        CountSourceNodes(generated, MaximumGeneratedNodes);
        var prepared = Engine.PrepareScript(Factory(program.FunctionNames, program.CommonSource), "manufacturer-functions.js", options: Preparation);
        var nodes = CountPreparedNodes(prepared.Program!, MaximumAstNodes);
        var codeBytes = program.SourceUtf8Bytes + Encoding.UTF8.GetByteCount(generated);
        if (codeBytes > MaximumCachedCodeBytes) throw new InvalidDataException("Prepared program exceeds its cache budget.");
        while (_programs.Count >= MaximumPrograms || _programs.Values.Sum(entry => entry.Nodes) + nodes > MaximumCachedNodes
            || _programs.Values.Sum(entry => entry.CodeBytes) + codeBytes > MaximumCachedCodeBytes)
        { _programs.Remove(_programs.MinBy(pair => pair.Value.Used).Key); _evictions++; }
        _programs.Add(program.Id, new(prepared, program.FunctionNames.ToArray(), nodes, codeBytes, Stopwatch.GetTimestamp()));
        _preparations++; _prepareMilliseconds += timer.Elapsed.TotalMilliseconds;
    }

    public Dictionary<string, object?> Execute(string id, ParameterFunctionInvocation invocation)
    {
        RemoveExpired();
        if (!_programs.TryGetValue(id, out var entry)) { _misses++; throw new FunctionProgramMissingException(); }
        if (!entry.Functions.Contains(invocation.FunctionName, StringComparer.Ordinal)) throw new InvalidDataException("Unknown function entry point.");
        entry.Used = Stopwatch.GetTimestamp(); _hits++; _executions++;
        using var context = JsonDocument.Parse(invocation.ContextJson);
        var frame = JsonSerializer.Serialize(new { invocation.FunctionName, invocation.InputAliases, invocation.OutputAliases,
            invocation.Values, Context = context.RootElement });
        if (frame.Length > 1_200_000) throw new InvalidDataException("Function invocation exceeds its wire budget.");
        using var engine = new Engine(options => options.LimitMemory(8_000_000).MaxStatements(10_000)
            .TimeoutInterval(TimeSpan.FromMilliseconds(Math.Min(100, invocation.BudgetMilliseconds))).LimitRecursion(32)
            .RegexTimeoutInterval(TimeSpan.FromMilliseconds(25)).DisableStringCompilation());
        engine.Execute(in Hardening);
        engine.SetValue("__knxFrameJson", frame);
        var prepared = entry.Script;
        engine.Execute(in prepared);
        var result = engine.Evaluate(in Invocation);
        if (!result.IsObject()) throw new InvalidDataException("Invalid function result.");
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in invocation.OutputAliases)
        {
            // Do not invoke manufacturer-defined getters while marshalling outside execution.
            var descriptor = result.AsObject().GetOwnProperty(name);
            if (ReferenceEquals(descriptor, PropertyDescriptor.Undefined) || !descriptor.IsDataDescriptor())
                throw new InvalidDataException("Function output is missing or is an accessor.");
            var value = descriptor.Value;
            values[name] = value.IsNull() ? null : value.IsBoolean() ? value.AsBoolean()
                : value.IsNumber() && double.IsFinite(value.AsNumber()) ? value.AsNumber()
                : value.IsString() && value.AsString().Length <= 4096 ? value.AsString()
                : throw new InvalidDataException("Invalid primitive function output.");
        }
        return values;
    }

    private static int CountSourceNodes(string source, int maximum)
    {
        var nodes = 0;
        var parser = new Parser(new ParserOptions { RegexTimeout = TimeSpan.FromMilliseconds(25),
            OnNode = (Node node, in OnNodeContext context) => { if (++nodes > maximum) throw new InvalidDataException("Script AST exceeds its node budget."); } });
        parser.ParseScript(source);
        return nodes;
    }

    private static int CountPreparedNodes(Node root, int maximum)
    {
        var nodes = 0; var stack = new Stack<(Node Node, int Depth)>(); stack.Push((root, 1));
        while (stack.TryPop(out var current))
        {
            if (++nodes > maximum || current.Depth > 512) throw new InvalidDataException("Prepared AST exceeds its node or depth budget.");
            foreach (var child in current.Node.ChildNodes) stack.Push((child, current.Depth + 1));
        }
        return nodes;
    }

    private static string Factory(IReadOnlyList<string> functions, string common)
    {
        var source = new StringBuilder("function __knxRun(__knxFrame,__knxObject){const __knxInput=__knxObject.create(null),__knxOutput=__knxObject.create(null);for(let i=0;i<__knxFrame.InputAliases.length;i++)__knxInput[__knxFrame.InputAliases[i]]=__knxFrame.Values[i];for(let i=0;i<__knxFrame.OutputAliases.length;i++)__knxOutput[__knxFrame.OutputAliases[i]]=__knxFrame.Values[i+__knxFrame.InputAliases.length];\n");
        source.Append(common).Append("\nswitch(__knxFrame.FunctionName){");
        foreach (var function in functions)
            source.Append("case ").Append(JsonSerializer.Serialize(function)).Append(":if(typeof ").Append(function)
                .Append("!=='function')throw new Error('Missing function');").Append(function).Append("(__knxInput,__knxOutput,__knxFrame.Context);break;");
        return source.Append("default:throw new Error('Unknown function');}return __knxOutput;}\n").ToString();
    }
}

/// <summary>A line cap applies during reading, before constructing a JSON document.</summary>
internal sealed class BoundedScriptLineReader(TextReader reader)
{
    private readonly char[] _buffer = new char[4096];
    private int _offset, _count;

    public async Task<string?> ReadLineAsync(int maximum)
    {
        var result = new StringBuilder();
        while (true)
        {
            if (_offset == _count)
            {
                _count = await reader.ReadAsync(_buffer.AsMemory()); _offset = 0;
                if (_count == 0) return result.Length == 0 ? null : result.ToString();
            }
            var newline = Array.IndexOf(_buffer, '\n', _offset, _count - _offset);
            var length = (newline < 0 ? _count : newline) - _offset;
            if (result.Length + length > maximum) throw new InvalidDataException("Script protocol line exceeds its limit.");
            result.Append(_buffer, _offset, length); _offset += length;
            if (newline < 0) continue;
            _offset++;
            if (result.Length > 0 && result[^1] == '\r') result.Length--;
            return result.ToString();
        }
    }
}

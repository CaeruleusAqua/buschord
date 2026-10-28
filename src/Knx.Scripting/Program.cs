using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Knx;
using Knx.Scripting;

var lines = new BoundedScriptLineReader(Console.In);
var functions = new PreparedFunctionPrograms();
while (true)
{
    string? line;
    try
    {
        functions.RemoveExpired();
        var reading = lines.ReadLineAsync(8_388_608);
        var idle = Stopwatch.StartNew();
        while (!reading.IsCompleted)
        {
            var untilIdle = TimeSpan.FromSeconds(30) - idle.Elapsed;
            if (untilIdle <= TimeSpan.Zero) return;
            var untilExpiry = functions.UntilExpiry;
            var wait = untilExpiry is { } expiry && expiry < untilIdle ? expiry : untilIdle;
            if (wait <= TimeSpan.Zero) { functions.RemoveExpired(); continue; }
            if (await Task.WhenAny(reading, Task.Delay(wait)) == reading) break;
            functions.RemoveExpired();
        }
        line = await reading;
    }
    catch (Exception ex) when (ex is TimeoutException or InvalidDataException) { break; }
    if (line is null) break;
    Dictionary<string, object?>? values = null;
    string? error = null, errorCode = null, programId = null;
    var executionStarted = false;
    try
    {
        using var request = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
        var root = request.RootElement;
        var kind = root.TryGetProperty("Kind", out var kindValue) ? kindValue.GetString() : "execute";
        if (kind == "prepareFunction")
        {
            Fields(root, "Kind", "ProgramId", "CommonSource", "FunctionNames");
            var program = new ParameterFunctionScript(root.GetProperty("CommonSource").GetString()!,
                Names(root.GetProperty("FunctionNames"), ParameterFunctionScript.MaximumFunctions));
            functions.Register(program, root.GetProperty("ProgramId").GetString()!);
            programId = program.Id; values = new();
        }
        else if (kind == "executeFunction")
        {
            if (line.Length > 1_200_000) throw new InvalidDataException();
            Fields(root, "Kind", "ProgramId", "Invocation");
            var call = root.GetProperty("Invocation");
            Fields(call, "FunctionName", "InputAliases", "OutputAliases", "Values", "ContextJson", "BudgetMilliseconds");
            var invocation = new ParameterFunctionInvocation(call.GetProperty("FunctionName").GetString()!,
                Names(call.GetProperty("InputAliases"), 256), Names(call.GetProperty("OutputAliases"), 256),
                call.GetProperty("Values").EnumerateArray().Take(257).Select(IsolatedScriptRuntime.Scalar),
                call.GetProperty("ContextJson").GetString()!, call.GetProperty("BudgetMilliseconds").GetInt32());
            executionStarted = true;
            values = functions.Execute(root.GetProperty("ProgramId").GetString()!, invocation);
        }
        else if (kind == "execute")
        {
            if (line.Length > 1_200_000) throw new InvalidDataException();
            var code = root.GetProperty("Source").GetString()!;
            var inputs = root.GetProperty("Inputs").EnumerateObject().ToArray();
            var outputs = root.GetProperty("Outputs").EnumerateArray().Select(element => element.GetString()!).ToArray();
            if (code.Length > 65536 || inputs.Length > 256 || outputs.Length > 256) throw new InvalidDataException();
            if (inputs.Any(input => !ParameterFunctionScript.Identifier(input.Name)) || outputs.Any(output => !ParameterFunctionScript.Identifier(output))
                || outputs.Distinct(StringComparer.Ordinal).Count() != outputs.Length) throw new InvalidDataException();
            using var engine = new Engine(options => options.LimitMemory(8_000_000).MaxStatements(10_000)
                .TimeoutInterval(TimeSpan.FromMilliseconds(100)).LimitRecursion(32)
                .RegexTimeoutInterval(TimeSpan.FromMilliseconds(25)).DisableStringCompilation());
            engine.Execute("globalThis.Date=undefined;globalThis.Temporal=undefined;globalThis.Intl=undefined;Math.random=function(){throw new Error('Unavailable');};");
            foreach (var input in inputs)
            {
                var value = IsolatedScriptRuntime.Scalar(input.Value);
                switch (value)
                {
                    case null: engine.SetValue(input.Name, JsValue.Null); break;
                    case string text: engine.SetValue(input.Name, text); break;
                    case bool flag: engine.SetValue(input.Name, flag); break;
                    case double number: engine.SetValue(input.Name, number); break;
                    default: throw new InvalidDataException();
                }
            }
            executionStarted = true;
            engine.Execute(code);
            values = new();
            foreach (var name in outputs)
            {
                var value = engine.GetValue(name);
                values[name] = value.IsNull() ? null : value.IsBoolean() ? value.AsBoolean()
                    : value.IsNumber() && double.IsFinite(value.AsNumber()) ? value.AsNumber()
                    : value.IsString() && value.AsString().Length <= 4096 ? value.AsString()
                    : throw new InvalidDataException();
            }
        }
        else throw new InvalidDataException();
    }
    catch (FunctionProgramMissingException)
    { values = null; error = "Prepared program is unavailable."; errorCode = "ProgramMissing"; executionStarted = false; }
    catch (Exception)
    { values = null; error = "Script rejected or resource limit reached."; errorCode = "Rejected"; }
    var response = JsonSerializer.Serialize(new { Values = values, Error = error, ErrorCode = errorCode,
        ExecutionStarted = executionStarted, ProgramId = programId, FunctionStatistics = functions.Statistics });
    if (response.Length > 1_200_000)
        response = JsonSerializer.Serialize(new { Values = (object?)null, Error = "Script result exceeds its wire budget.",
            ErrorCode = "Rejected", ExecutionStarted = executionStarted, FunctionStatistics = functions.Statistics });
    try { await Console.Out.WriteLineAsync(response); }
    catch (IOException) { break; }
}

static IEnumerable<string> Names(JsonElement array, int maximum)
{
    if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw new InvalidDataException();
    return array.EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException()).ToArray();
}
static void Fields(JsonElement value, params string[] expected)
{
    if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var property in value.EnumerateObject())
        if (!expected.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw new InvalidDataException();
    if (seen.Count != expected.Length) throw new InvalidDataException();
}

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Scripting;

internal static class PreparedFunctionChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool valid) { Console.WriteLine((valid ? "PASS " : "FAIL ") + "PREPARED " + name); if (!valid) failures++; }
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
        const string simple = "function forward(input,output,context){output.right=input.left*context.factor;}";
        ParameterFunctionInvocation Call(double value, double seed = 0) => new("forward", ["left"], ["right"], [value, seed], "{\"factor\":2}");
        var names = new[] { "forward" }; var program = new ParameterFunctionScript(simple, names); var identity = program.Id;
        names[0] = "changed";
        var inputs = new[] { "left" }; var values = new object?[] { 3d, 0d };
        var invocation = new ParameterFunctionInvocation("forward", inputs, ["right"], values, "{}");
        inputs[0] = "changed"; values[0] = 99d;
        Check("program and invocation copy caller-owned collections", program.FunctionNames.Single() == "forward" && program.Id == identity
            && invocation.InputAliases.Single() == "left" && Equals(invocation.Values[0], 3d));
        Check("canonical entry-point ordering is part of a stable source identity", new ParameterFunctionScript(simple, ["backward", "forward"]).Id
            == new ParameterFunctionScript(simple, ["forward", "backward"]).Id
            && new ParameterFunctionScript(simple + " ", ["forward"]).Id != identity);
        Check("the optional capability introduces no engine dependency into Core", typeof(ParameterFunctionScript).Assembly.GetReferencedAssemblies()
            .All(reference => reference.Name is not ("Jint" or "Acornima")));
        Check("program source identifiers and Unicode are bounded", Rejected(() => new ParameterFunctionScript(new string(' ', 1_048_577), ["forward"]))
            && Rejected(() => new ParameterFunctionScript(simple, ["bad.name"])) && Rejected(() => new ParameterFunctionScript(simple, ["forward", "forward"]))
            && Rejected(() => new ParameterFunctionScript("//" + new string((char)0xd800, 1), ["forward"])));
        Check("invocations accept only finite primitive values and disjoint aliases", Rejected(() => new ParameterFunctionInvocation("forward", ["same"], ["same"], [1d, 0d], "{}"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [double.NaN, 0d], "{}"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [new object(), 0d], "{}"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [new string('x',4097), 0d], "{}")));
        Check("context remains an object of bounded data", Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [1d, 0d], "[]"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [1d, 0d], "{\"x\":()=>1}"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [1d, 0d], "{\"x\":1,\"x\":2}"))
            && Rejected(() => new ParameterFunctionInvocation("forward", ["left"], ["right"], [1d, 0d], "{\"x\":1e400}")));
        using (var runtime = new IsolatedScriptRuntime())
            Check("legacy transport preserves its existing double-only numeric contract", Rejected(() => runtime.Execute("output=input;",
                new Dictionary<string,object?> { ["input"] = 3, ["output"] = 0d }, ["output"]))
                && Equals(runtime.Execute("output=input*2;", new Dictionary<string,object?> { ["input"] = 3d, ["output"] = 0d }, ["output"])["output"],6d));

        using (var runtime = new IsolatedScriptRuntime())
        {
            var large = new ParameterFunctionScript("/*" + new string('x', 100_000) + "*/" +
                "function forward(input,output,context){globalThis.seen=(globalThis.seen||0)+1;output.right=input.left*context.factor+globalThis.seen;}", ["forward"]);
            var correct = true;
            for (var index = 0; index < 148; index++) correct &= Equals(runtime.ExecuteFunction(large, Call(index))["right"], index * 2d + 1);
            Check("148 invocations reuse one large prepared program with a fresh realm per call", correct && runtime.FunctionStatistics.Preparations == 1
                && runtime.FunctionStatistics.Executions == 148 && runtime.FunctionStatistics.CachedPrograms == 1 && runtime.FunctionStatistics.CachedNodes > 0);
            var before = runtime.FunctionStatistics;
            System.Threading.Thread.Sleep(8200);
            var afterExpiry = runtime.ExecuteFunction(large, Call(5));
            Check("expired programs re-register only after the explicit unstarted acknowledgement", Equals(afterExpiry["right"], 11d)
                && runtime.FunctionStatistics.Preparations == before.Preparations + 1 && runtime.FunctionStatistics.CacheMisses == before.CacheMisses + 1
                && runtime.FunctionStatistics.Executions == before.Executions + 1);
        }
        using (var runtime = new IsolatedScriptRuntime())
        {
            foreach (var suffix in new[] { "a", "b", "c" }) runtime.ExecuteFunction(new ParameterFunctionScript(simple + "/*" + suffix + "*/", ["forward"]), Call(3));
            Check("the worker retains at most two bounded programs", runtime.FunctionStatistics.CachedPrograms == 2 && runtime.FunctionStatistics.Evictions == 1
                && runtime.FunctionStatistics.CachedNodes <= 500_000 && runtime.FunctionStatistics.CachedCodeBytes <= 8_388_608);
            var noOp = new ParameterFunctionScript("function forward(input,output,context){return {right:999};}", ["forward"]);
            Check("outputs remain preinitialized and returned objects are ignored", Equals(runtime.ExecuteFunction(noOp, Call(3, 7))["right"], 7d));
            var aliases = new ParameterFunctionScript("function forward(input,output,context){output.JSON=input.Object+context.n;}", ["forward"]);
            Check("data aliases cannot shadow the wrapper intrinsics", Equals(runtime.ExecuteFunction(aliases,
                new("forward", ["Object"], ["JSON"], [3d, 0d], "{\"n\":2}"))["JSON"], 5d));
            var proto = new ParameterFunctionScript("function forward(input,output,context){output.__proto__=input.left;}", ["forward"]);
            Check("prototype-like output aliases remain own data fields", Equals(runtime.ExecuteFunction(proto,
                new("forward", ["left"], ["__proto__"], [3d, 0d], "{}"))["__proto__"], 3d));
        }
        foreach (var script in new[] { "function forward(i,o,c){o.right={value:1};}", "function forward(i,o,c){delete o.right;}",
            "function forward(i,o,c){Object.defineProperty(o,'right',{get:function(){return 9;}});}",
            "function forward(i,o,c){while(true){}}", "function forward(i,o,c){o.right=Function('return 1')();}" })
        {
            using var runtime = new IsolatedScriptRuntime();
            Check("unsupported results accessors loops and code generation are rejected", Rejected(() => runtime.ExecuteFunction(new(script, ["forward"]), Call(3))));
        }
        using (var runtime = new IsolatedScriptRuntime())
            Check("source AST budget is enforced before any invocation", Rejected(() => runtime.ExecuteFunction(
                new ParameterFunctionScript(string.Concat(Enumerable.Repeat("0;", 150_000)), ["forward"]), Call(3))));

        var document = Application("/*" + new string('x',100_000) + "*/" + simple);
        using (var runtime = new IsolatedScriptRuntime())
        {
            var state = DynamicParameters.Evaluate(document.Root!, scripts: runtime);
            Check("Core opts into shared function transport without changing typed publication", state.ScriptFailures == 0 && state.Parameters["R"].Value == "6"
                && runtime.FunctionStatistics.Preparations == 1);
            var legacy = new LegacyOnlyRuntime(runtime);
            var fallback = DynamicParameters.Evaluate(document.Root!, scripts: legacy);
            Check("legacy capabilities retain their existing 64 KiB fallback limit", fallback.ScriptFailures > 0 && fallback.Parameters["R"].Value == "0" && legacy.Calls == 0);
            var small = DynamicParameters.Evaluate(Application(simple).Root!, scripts: legacy);
            Check("small function and inline fallback remain usable without the optional interface", small.ScriptFailures == 0 && small.Parameters["R"].Value == "6" && legacy.Calls > 0);
        }

        using (var worker = new ProtocolWorker())
        {
            var missing = worker.Send(new { Kind = "executeFunction", ProgramId = new string('0',64), Invocation = Call(3) });
            Check("unknown-program responses explicitly precede execution", missing.GetProperty("ErrorCode").GetString() == "ProgramMissing"
                && missing.GetProperty("ExecutionStarted").ValueKind == JsonValueKind.False
                && missing.GetProperty("FunctionStatistics").GetProperty("Executions").GetInt64() == 0);
            var mismatch = worker.Send(new { Kind = "prepareFunction", ProgramId = new string('0',64), program.CommonSource, program.FunctionNames });
            Check("the worker recomputes registration identities", mismatch.GetProperty("ErrorCode").GetString() == "Rejected"
                && mismatch.GetProperty("FunctionStatistics").GetProperty("CachedPrograms").GetInt32() == 0);
        }
        Console.WriteLine($"PREPARED FUNCTION CHECKS {failures} failure(s)");
        return failures;
    }

    private sealed class LegacyOnlyRuntime(IParameterScriptRuntime inner) : IParameterScriptRuntime
    {
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<string> outputs)
        { Calls++; return inner.Execute(source, inputs, outputs); }
    }

    private static XDocument Application(string script)
    {
        var doc = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="16" minInclusive="0" maxInclusive="1000" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-L" ParameterType="T" Value="3" /><Parameter Id="P-R" ParameterType="T" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="L" RefId="P-L" /><ParameterRef Id="R" RefId="P-R" /></ParameterRefs><Script />
            <ParameterCalculations><ParameterCalculation Language="JavaScript" LRTransformationFunc="forward" LRTransformationParameters="{factor:2}">
            <LParameters><ParameterRefRef RefId="L" AliasName="left" /></LParameters><RParameters><ParameterRefRef RefId="R" AliasName="right" /></RParameters>
            </ParameterCalculation></ParameterCalculations></Static><Dynamic><ParameterRefRef RefId="L" /><ParameterRefRef RefId="R" /></Dynamic></ApplicationProgram></KNX>
            """);
        doc.Descendants("Script").Single().Value = script; return doc;
    }

    private sealed class ProtocolWorker : IDisposable
    {
        private readonly Process _process;
        public ProtocolWorker()
        {
            var root = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!.FullName;
            var start = new ProcessStartInfo(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            var entry = Assembly.GetEntryAssembly()!.Location;
            foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(entry,".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(entry,".deps.json"), typeof(IsolatedScriptRuntime).Assembly.Location }) start.ArgumentList.Add(argument);
            start.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
            _process = Process.Start(start)!; _process.ErrorDataReceived += (_,_)=>{}; _process.BeginErrorReadLine();
        }
        public JsonElement Send(object value)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(value)); _process.StandardInput.Flush();
            var line = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var response = JsonDocument.Parse(line!); return response.RootElement.Clone();
        }
        public void Dispose()
        { try { if (!_process.HasExited) _process.Kill(entireProcessTree:true); } finally { _process.Dispose(); } }
    }
}

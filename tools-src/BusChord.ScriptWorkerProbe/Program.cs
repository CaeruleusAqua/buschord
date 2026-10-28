using Knx;
using Knx.Scripting;
using System.Text.Json;

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]) || !Directory.Exists(args[0]))
    throw new ArgumentException("Provide the absolute extracted application's directory.");
using (var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(args[0], "ScriptWorker", "Knx.Scripting.runtimeconfig.json"))))
{
    var options = configuration.RootElement.GetProperty("runtimeOptions");
    if (options.TryGetProperty("framework", out _) || options.TryGetProperty("frameworks", out _)
        || !options.GetProperty("includedFrameworks").EnumerateArray().Any(value => value.GetProperty("name").GetString() == "Microsoft.NETCore.App"))
        throw new InvalidDataException("The packaged worker must contain its own .NET runtime.");
}
// Make an accidentally framework-dependent worker fail instead of finding a host on this machine.
Environment.SetEnvironmentVariable("PATH", "");
foreach (var name in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)" })
    Environment.SetEnvironmentVariable(name, Path.Combine(Path.GetTempPath(), "buschord-no-dotnet-runtime"));
Environment.SetEnvironmentVariable("DOTNET_MULTILEVEL_LOOKUP", "0");
using var runtime = new IsolatedScriptRuntime(args[0]);
var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidDataException("Script-worker package check failed: " + name);
    checks++; Console.WriteLine("PASS " + name);
}
var scalar = runtime.Execute("var result = input * 7;", new Dictionary<string, object?> { ["input"] = 6d }, ["result"]);
Check(Equals(scalar["result"], 42d), "packaged apphost executes scalar script without a discoverable .NET installation");
var program = new ParameterFunctionScript("function forward(input,output,context){output.right=input.left*context.factor;}", ["forward"]);
var result = runtime.ExecuteFunction(program, new ParameterFunctionInvocation("forward", ["left"], ["right"], [3d, 0d], "{\"factor\":2}"));
Check(Equals(result["right"], 6d), "packaged apphost prepares and executes a function");
var rejected = false;
try { runtime.Execute("while(true){}", new Dictionary<string, object?>(), []); }
catch (InvalidDataException) { rejected = true; }
Check(rejected, "unbounded script is rejected");
Check(Equals(runtime.Execute("var result=9;", new Dictionary<string, object?>(), ["result"])["result"], 9d),
    "runtime recovers after a rejected script");
Console.WriteLine(JsonSerializer.Serialize(new { Checks = checks, PackageRoot = Path.GetFullPath(args[0]), Host = "ScriptWorker/Knx.Scripting", DotnetOnPath = false }));

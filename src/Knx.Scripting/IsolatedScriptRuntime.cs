using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Knx.Scripting;

public sealed class IsolatedScriptRuntime : IParameterFunctionScriptRuntime, IDisposable
{
    public static IsolatedScriptRuntime Shared { get; } = new();
    private readonly object _gate = new();
    private readonly HashSet<string> _registered = new(StringComparer.Ordinal);
    private Process? _worker;
    private BoundedScriptLineReader? _responses;
    private FunctionScriptStatistics _statistics = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public FunctionScriptStatistics FunctionStatistics { get { lock (_gate) return _statistics; } }
    static IsolatedScriptRuntime() => AppDomain.CurrentDomain.ProcessExit += (_, _) => Shared.Dispose();

    public IReadOnlyDictionary<string, object?> Execute(string source, IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyList<string> outputs)
    {
        if (source.Length > 65536 || inputs.Count > 256 || outputs.Count > 256)
            throw new InvalidDataException("Script request exceeds its safety limit.");
        if (inputs.Values.Any(value => !ParameterFunctionInvocation.Scalar(value)))
            throw new InvalidDataException("Only finite primitive script inputs are allowed.");
        lock (_gate)
        {
            try
            {
                var clock = Stopwatch.StartNew();
                var maximum = TimeSpan.FromSeconds(_worker is null || _worker.HasExited ? 5 : 1);
                TimeSpan Remaining() => Left(clock, maximum, maximum);
                EnsureWorker();
                using var response = Exchange(new { Source = source, Inputs = inputs, Outputs = outputs }, 1_200_000, Remaining);
                Success(response.RootElement);
                return Values(response.RootElement);
            }
            catch (Exception ex) when (TransportFailure(ex))
            { Stop(); throw new InvalidDataException("Isolated script execution failed or exceeded its safety limits."); }
        }
    }

    public IReadOnlyDictionary<string, object?> ExecuteFunction(ParameterFunctionScript program, ParameterFunctionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(program); ArgumentNullException.ThrowIfNull(invocation);
        lock (_gate)
        {
            try
            {
                var clock = Stopwatch.StartNew(); var budget = TimeSpan.FromMilliseconds(invocation.BudgetMilliseconds);
                TimeSpan Until(TimeSpan deadline) => Left(clock, deadline < budget ? deadline : budget, budget);
                EnsureWorker();
                void Register()
                {
                    var deadline = clock.Elapsed + TimeSpan.FromSeconds(5);
                    using var prepared = Exchange(new { Kind = "prepareFunction", ProgramId = program.Id,
                        program.CommonSource, program.FunctionNames }, 8_388_608, () => Until(deadline));
                    Success(prepared.RootElement);
                    if (prepared.RootElement.GetProperty("ProgramId").GetString() != program.Id)
                        throw new InvalidDataException("Prepared-program acknowledgement mismatch.");
                    if (_registered.Count >= 2 && !_registered.Contains(program.Id)) _registered.Clear();
                    _registered.Add(program.Id);
                }
                if (!_registered.Contains(program.Id)) Register();
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var deadline = clock.Elapsed + TimeSpan.FromSeconds(1);
                    using var response = Exchange(new { Kind = "executeFunction", ProgramId = program.Id, Invocation = invocation }, 1_200_000, () => Until(deadline));
                    var root = response.RootElement;
                    if (root.TryGetProperty("ErrorCode", out var code) && code.GetString() == "ProgramMissing"
                        && root.TryGetProperty("ExecutionStarted", out var started) && started.ValueKind == JsonValueKind.False)
                    {
                        // The worker emits this acknowledgement before creating an engine.
                        // A completed, failed or unacknowledged execution is never retried.
                        if (attempt != 0) throw new InvalidDataException("Prepared program remained unavailable.");
                        _registered.Remove(program.Id); Register(); continue;
                    }
                    Success(root);
                    return Values(root);
                }
                throw new InvalidDataException("Function invocation did not complete.");
            }
            catch (Exception ex) when (TransportFailure(ex))
            { Stop(); throw new InvalidDataException("Isolated function execution failed or exceeded its safety limits."); }
        }
    }

    private static TimeSpan Left(Stopwatch clock, TimeSpan budget, TimeSpan maximum)
    {
        var remaining = budget - clock.Elapsed;
        return remaining > TimeSpan.Zero ? (remaining < maximum ? remaining : maximum) : throw new TimeoutException();
    }

    private JsonDocument Exchange(object request, int maximumRequest, Func<TimeSpan> remaining)
    {
        var text = JsonSerializer.Serialize(request);
        if (text.Length > maximumRequest) throw new InvalidDataException("Script request exceeds its wire budget.");
        _worker!.StandardInput.WriteLineAsync(text).WaitAsync(remaining()).GetAwaiter().GetResult();
        _worker.StandardInput.FlushAsync().WaitAsync(remaining()).GetAwaiter().GetResult();
        var line = _responses!.ReadLineAsync(1_200_000).WaitAsync(remaining()).GetAwaiter().GetResult();
        if (line is null) throw new IOException("Script worker returned no response.");
        var response = JsonDocument.Parse(line);
        try
        {
            if (response.RootElement.TryGetProperty("FunctionStatistics", out var statistics))
                _statistics = statistics.Deserialize<FunctionScriptStatistics>() ?? throw new InvalidDataException("Invalid script statistics.");
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    private static void Success(JsonElement response)
    {
        if (response.GetProperty("Error").ValueKind != JsonValueKind.Null)
            throw new InvalidDataException("Script execution failed or exceeded its safety limits.");
    }

    private static IReadOnlyDictionary<string, object?> Values(JsonElement response)
    {
        var values = response.GetProperty("Values");
        if (values.ValueKind != JsonValueKind.Object || values.EnumerateObject().Count() > 256)
            throw new InvalidDataException("Invalid script values.");
        return values.EnumerateObject().ToDictionary(property => property.Name, property => Scalar(property.Value));
    }

    private void EnsureWorker()
    {
        if (_worker is not null && !_worker.HasExited) return;
        Stop();
        var entry = Assembly.GetEntryAssembly()!.Location;
        var root = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        var host = root is null ? "" : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(host)) throw new IOException("The current .NET installation has no worker host.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = AppContext.BaseDirectory };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(entry, ".runtimeconfig.json"),
                     "--depsfile", Path.ChangeExtension(entry, ".deps.json"), typeof(IsolatedScriptRuntime).Assembly.Location })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "DOTNET_ROOT", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "LANG" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOTNET_GCHeapHardLimit"] = "0x10000000";
        _worker = Process.Start(start) ?? throw new IOException("Script worker could not start.");
        _responses = new(_worker.StandardOutput);
        _worker.ErrorDataReceived += (_, _) => { }; // Manufacturer source and values never enter logs.
        _worker.BeginErrorReadLine();
    }

    private static bool TransportFailure(Exception error) => error is IOException or InvalidDataException or TimeoutException
        or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException;

    internal static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when value.GetString()!.Length <= 4096 => value.GetString(),
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number,
        _ => throw new InvalidDataException("Only finite primitive script values are allowed."),
    };

    private void Stop()
    {
        _registered.Clear(); _responses = null; _statistics = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (_worker is null) return;
        try { if (!_worker.HasExited) _worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        finally { _worker.Dispose(); _worker = null; }
    }
    public void Dispose() { lock (_gate) Stop(); }
}

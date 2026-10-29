using System.Net;
using System.Text;
using System.Text.Json;
using Knx.Bus;

namespace Knx.Mcp;

/// <summary>Explicit single-device commissioning diagnostics; normal MCP sessions remain read-only.</summary>
internal static class ManagementProbe
{
    public static async Task<int> RunAsync(string host, string[] args)
        => await RunCoreAsync(host, args, null);

    internal static Task<int> RunWithConnectionsAsync(string host, string[] args,
        Func<IPEndPoint, IndividualAddress, IndividualAddress, CancellationToken, Task<IManagementConnection>> open)
        => RunCoreAsync(host, args, open);

    private static async Task<int> RunCoreAsync(string host, string[] args,
        Func<IPEndPoint, IndividualAddress, IndividualAddress, CancellationToken, Task<IManagementConnection>>? open)
    {
        try
        {
            var options = ParseOptions(args);
            string? Option(string name) => options.GetValueOrDefault(name);
            if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || Option("--target") is not { Length: > 0 } targetText)
                throw new ArgumentException("management-probe requires a gateway IPv4 and --target=area.line.device.");
            var target = IndividualAddress.Parse(targetText);
            if (target.Device is 0 or 255 || target.ToString() != targetText)
                throw new ArgumentException("--target must be one canonical area.line.device address with device 1..254.");
            var mode = Option("--mode-cycle-confirm");
            var full = Option("--m112-full-confirm");
            var change = Option("--m112-change-confirm");
            var recover = Option("--m112-recover");
            if ((mode is not null ? 1 : 0) + (full is not null ? 1 : 0) + (recover is not null ? 1 : 0) + (change is not null ? 1 : 0) > 1)
                throw new ArgumentException("Choose only one of mode-cycle, M112 reload, configuration change, or recovery.");
            ValidateModeOptions(options, mode is not null ? "mode" : full is not null ? "full" : change is not null ? "change" : recover is not null ? "recover" : "read");
            if (change is not null && !options.ContainsKey("--m112-preview"))
            {
                var review = Required(options, "--review-fingerprint");
                if (review.Length != 64 || !review.All(char.IsAsciiHexDigit))
                    throw new ArgumentException("--review-fingerprint must be the 64 hexadecimal characters from the reviewed preview.");
            }
            using var caller = new CancellationTokenSource();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(caller.Token);
            timeout.CancelAfter(change is not null ? TimeSpan.FromMinutes(10) : full is not null || recover is not null ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(60));
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; caller.Cancel(); };
            Console.CancelKeyPress += onCancel;
            try
            {
                async Task<IManagementConnection> Open(CancellationToken token)
                    => open is null ? await KnxManagementConnection.OpenAsync(new IPEndPoint(address, 3671), target,
                        new IndividualAddress(1, 1, 249), ct: token) : await open(new IPEndPoint(address, 3671), target, new IndividualAddress(1, 1, 249), token);
                if (mode is not null || full is not null || recover is not null || change is not null)
                {
                    var confirmed = mode ?? full ?? change ?? Option("--m112-recovery-confirm");
                    if (confirmed != targetText)
                        throw new ArgumentException("The selected mode's confirmation must exactly equal --target.");
                    var expectedPath = PrivatePath(Required(options, "--expected-identity"), existingFile: true);
                    if (new FileInfo(expectedPath).Length > 16384) throw new InvalidDataException("Expected identity is oversized.");
                    var expected = JsonSerializer.Deserialize<ManagementDeviceIdentity>(await File.ReadAllTextAsync(expectedPath, timeout.Token))
                        ?? throw new InvalidDataException("Expected identity is missing.");
                    if (expected.Address != targetText || expected.MaskVersion != 0x0701
                        || expected.Manufacturer is not { Length: 4 } || expected.Serial is not { Length: 12 }
                        || expected.Application is not { Length: 10 }
                        || !expected.Manufacturer.All(char.IsAsciiHexDigit) || !expected.Serial.All(char.IsAsciiHexDigit)
                        || !expected.Application.All(char.IsAsciiHexDigit))
                        throw new ArgumentException("Expected identity must name exactly --target and a valid M112 identity.");
                    if (recover is not null)
                    {
                        var recoveryPath = PrivatePath(recover, existingFile: true);
                        timeout.Token.ThrowIfCancellationRequested();
                        Console.Error.WriteLine("M112 recovery started; caller cancellation cannot interrupt restoration. Independent timeout: five minutes.");
                        using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                        var result = await M112ApplicationDownload.RecoverAsync(Open, recoveryPath, expected, confirmed,
                            phase => Console.Error.WriteLine("m112: " + phase), recoveryTimeout.Token);
                        Console.WriteLine(JsonSerializer.Serialize(result));
                        return result.OriginalConfigurationRestoredAndVerified && result.Error is null ? 0 : 1;
                    }
                    var journalDirectory = PrivatePath(Required(options, "--journal-directory"), existingFile: false);
                    if (mode is not null)
                    {
                        var result = await ProgrammingModeCycle.RunAsync(Open, expected, confirmed, journalDirectory, timeout.Token);
                        Console.WriteLine(JsonSerializer.Serialize(result));
                        return result.ActivatedAndVerified && result.RestoredAndVerified && result.Error is null ? 0 : 1;
                    }
                    var projectPath = PrivatePath(Required(options, "--project"), existingFile: true);
                    if (!Path.GetExtension(projectPath).Equals(".knxproj", StringComparison.OrdinalIgnoreCase))
                        throw new ArgumentException("--project must name an existing private .knxproj copy.");
                    var project = LoadProject(projectPath, Option("--project-password-env"));
                    timeout.Token.ThrowIfCancellationRequested();
                    var devices = ProjectQuery.DeviceList(project).Where(d => d.Address == target).ToArray();
                    if (devices.Length != 1) throw new InvalidDataException("The project must contain exactly one device at --target.");
                    var device = devices[0];
                    if (change is not null)
                    {
                        var baselinePath = PrivatePath(Required(options, "--baseline-project"), existingFile: true);
                        if (!Path.GetExtension(baselinePath).Equals(".knxproj", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("--baseline-project must name an existing private .knxproj copy.");
                        var baselineProject = LoadProject(baselinePath, Option("--baseline-password-env") ?? Option("--project-password-env"));
                        var baselineDevices = ProjectQuery.DeviceList(baselineProject).Where(d => d.Address == target).ToArray();
                        if (baselineDevices.Length != 1) throw new InvalidDataException("The baseline must contain exactly one device at --target.");
                        var plan = M112ApplicationChangePlan.Prepare(baselineProject, baselineDevices[0], project, device);
                        if (expected.Application != $"{plan.Desired.Manufacturer:X4}{plan.Desired.ApplicationNumber:X4}{plan.Desired.ApplicationVersion:X2}")
                            throw new InvalidDataException("The change plan does not match the confirmed application.");
                        timeout.Token.ThrowIfCancellationRequested();
                        if (options.ContainsKey("--m112-preview"))
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new { previewOnly = true, devicePreflightPerformed = false,
                                target = targetText, plan.Fingerprint, layoutFingerprint = plan.Desired.LayoutFingerprint,
                                baselineFingerprint = plan.Baseline.Fingerprint, desiredFingerprint = plan.Desired.Fingerprint,
                                changes = plan.Changes.Select(c => new { c.Address, c.Length, c.RequiresDeviceRead,
                                    baseline = c.BaselineData.Sha256, desired = c.DesiredData.Sha256 }),
                                recoveryScope = "CapturedConfigurationWithSourceLoadRecipe" }));
                            return 0;
                        }
                        var result = await M112ApplicationDownload.ApplyChangesAsync(Open, plan, Required(options, "--review-fingerprint"), expected,
                            confirmed, journalDirectory, phase => Console.Error.WriteLine("m112: " + phase), timeout.Token);
                        Console.WriteLine(JsonSerializer.Serialize(result));
                        return result.DownloadedAndVerified && result.Error is null ? 0 : 1;
                    }
                    var image = OfflineM112ApplicationBuild.Build(project, device);
                    if (image.Address != targetText || expected.Application != $"{image.Manufacturer:X4}{image.ApplicationNumber:X4}{image.ApplicationVersion:X2}")
                        throw new InvalidDataException("The source image's address/application does not match the confirmed identity.");
                    timeout.Token.ThrowIfCancellationRequested();
                    if (options.ContainsKey("--m112-preview"))
                    {
                        Console.WriteLine(JsonSerializer.Serialize(new { previewOnly = true, devicePreflightPerformed = false,
                            target = targetText, image.Fingerprint, image.UnionCount, image.LoadControlAddress, image.LoadStatusAddresses,
                            segments = image.Segments.Select(s => new { s.ObjectIndex, s.Address, s.Size, s.MemoryType }),
                            recoveryScope = "CapturedConfigurationWithSourceLoadRecipe" }));
                        return 0;
                    }
                    var download = await M112ApplicationDownload.RunAsync(Open, project, device, image, expected, confirmed, journalDirectory,
                        phase => Console.Error.WriteLine("m112: " + phase), timeout.Token);
                    Console.WriteLine(JsonSerializer.Serialize(download));
                    return download.DownloadedAndVerified && download.Error is null ? 0 : 1;
                }
                await using var connection = await Open(timeout.Token);
                var identity = await connection.Client.ReadIdentityAsync(timeout.Token);
                var status = identity.MaskVersion == 0x0701
                    ? Convert.ToHexString(await connection.Client.ReadMemoryAsync(0x60, 1, timeout.Token)) : null;
                Console.WriteLine(JsonSerializer.Serialize(new { readOnly = true, identity, m112Status = status }));
                if (Option("--identity-output") is { Length: > 0 } output)
                {
                    await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await JsonSerializer.SerializeAsync(file, identity, cancellationToken: timeout.Token);
                    file.Flush(flushToDisk: true);
                }
                return 0;
            }
            finally { Console.CancelKeyPress -= onCancel; }
        }
        catch (ArgumentException ex) { Console.Error.WriteLine("management-probe: " + ex.Message); return 2; }
        catch (Exception ex)
        {
            Console.Error.WriteLine("management-probe: " + ex.Message);
            return 1;
        }
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var valued = new HashSet<string>(["--management-probe", "--target", "--identity-output", "--mode-cycle-confirm",
            "--expected-identity", "--journal-directory", "--m112-full-confirm", "--project", "--project-password-env",
            "--m112-recover", "--m112-recovery-confirm"], StringComparer.Ordinal);
        valued.UnionWith(["--m112-change-confirm", "--baseline-project", "--baseline-password-env", "--review-fingerprint"]);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=');
            var name = separator < 0 ? arg : arg[..separator];
            if (name.Equals("--management-probe", StringComparison.OrdinalIgnoreCase)) name = "--management-probe";
            var value = separator < 0 ? "" : arg[(separator + 1)..];
            if (name == "--m112-preview" ? separator >= 0 : !valued.Contains(name) || separator < 0 || value.Length == 0)
                throw new ArgumentException("Unknown or malformed management-probe option. Password values must never be command-line arguments.");
            if (!result.TryAdd(name, value)) throw new ArgumentException("Duplicate management-probe options are not allowed.");
        }
        return result;
    }

    private static void ValidateModeOptions(Dictionary<string, string> options, string mode)
    {
        var allowed = new HashSet<string>(["--management-probe", "--target"], StringComparer.Ordinal);
        allowed.UnionWith(mode switch
        {
            "mode" => ["--mode-cycle-confirm", "--expected-identity", "--journal-directory"],
            "full" => ["--m112-full-confirm", "--project", "--project-password-env", "--expected-identity", "--journal-directory", "--m112-preview"],
            "change" => ["--m112-change-confirm", "--project", "--project-password-env", "--expected-identity", "--journal-directory", "--m112-preview",
                "--baseline-project", "--baseline-password-env", "--review-fingerprint"],
            "recover" => ["--m112-recover", "--m112-recovery-confirm", "--expected-identity"],
            _ => new[] { "--identity-output" }
        });
        if (options.Keys.Any(k => !allowed.Contains(k)))
            throw new ArgumentException("Options from different management-probe modes cannot be combined.");
    }

    private static string Required(Dictionary<string, string> options, string name)
        => options.GetValueOrDefault(name) ?? throw new ArgumentException("Missing required option: " + name);

    private static string PrivatePath(string path, bool existingFile)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Project, identity, recovery and journal paths must be absolute and outside Git checkouts.");
        path = Path.GetFullPath(path);
        if (existingFile && !File.Exists(path)) throw new FileNotFoundException("The selected private input file does not exist.");
        if (!existingFile && File.Exists(path)) throw new ArgumentException("The journal path must be a directory.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Private commissioning paths cannot use filesystem links.");
        for (var directory = new DirectoryInfo(existingFile ? Path.GetDirectoryName(path)! : path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Private commissioning paths cannot use filesystem links.");
            if (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                throw new ArgumentException("Private commissioning inputs and journals must stay outside Git checkouts.");
        }
        return path;
    }

    private static Project LoadProject(string path, string? passwordEnvironmentName)
    {
        string? password = null;
        try
        {
            if (passwordEnvironmentName is not null)
            {
                if (passwordEnvironmentName.Length is < 1 or > 128
                    || !(char.IsAsciiLetter(passwordEnvironmentName[0]) || passwordEnvironmentName[0] == '_')
                    || passwordEnvironmentName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new ArgumentException("--project-password-env requires an environment VARIABLE NAME, never its value.");
                password = Environment.GetEnvironmentVariable(passwordEnvironmentName)
                    ?? throw new ArgumentException("The selected project-password environment variable is not set.");
            }
            try { return Knxproj.Load(path, password, scripts: null); }
            catch (Exception ex) when (passwordEnvironmentName is null && Knxproj.IsPasswordError(ex))
            {
                password = ReadHiddenPassword();
                return Knxproj.Load(path, password, scripts: null);
            }
        }
        finally { password = null; }
    }

    private static string ReadHiddenPassword()
    {
        if (Console.IsInputRedirected)
            throw new ArgumentException("A protected project requires interactive hidden input or --project-password-env=VARIABLE_NAME.");
        Console.Error.Write("Project password (hidden; Escape cancels): ");
        var value = new StringBuilder();
        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException("Project password input cancelled.");
                if (key.Key == ConsoleKey.Enter) return value.ToString();
                if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
                else if (!char.IsControl(key.KeyChar))
                {
                    if (value.Length >= 4096) throw new ArgumentException("Project password input exceeds the supported limit.");
                    value.Append(key.KeyChar);
                }
            }
        }
        finally { value.Clear(); Console.Error.WriteLine(); }
    }
}

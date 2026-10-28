using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knx.Updates;

if (args.Length > 3 && args[0] == "--silent" && args[1] == "apply")
{
    var index = Array.IndexOf(args, "--rootDir");
    if (index < 0 || index + 1 >= args.Length) return 2;
    var fixtureRoot = Path.GetFullPath(args[index + 1]);
    var allowedTemp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar + "buschord-update-tests-";
    if (!fixtureRoot.StartsWith(allowedTemp, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(fixtureRoot, "fake-native-fixture"))) return 2;
    var target = Path.Combine(fixtureRoot, "current");
    Directory.Move(target, Path.Combine(fixtureRoot, "current-before-fake-native"));
    Directory.CreateDirectory(target);
    File.Copy(typeof(Handler).Assembly.Location, Path.Combine(target, "BusChord.dll"));
    File.WriteAllText(Path.Combine(target, "BusChord.exe"), "fixture apphost");
    var realVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Handler).Assembly.Location).ProductVersion!.Split('+')[0];
    File.WriteAllText(Path.Combine(target, "essential.bin"), "must be present");
    InstalledCodeInventory.Create(target, "BusChord", realVersion, "win-x64", "win-x64-stable");
    File.WriteAllText(Path.Combine(target, "sq.version"), $"<package><metadata><id>BusChord</id><version>{realVersion}</version><rid>win-x64</rid><channel>win-x64-stable</channel><mainExe>BusChord.exe</mainExe></metadata></package>");
    File.Delete(Path.Combine(target, "essential.bin")); // A partial native swap still reports success.
    return 0;
}
if (args is ["--native-lease-child", var childReady, var childRelease, var childDone])
{
    File.WriteAllText(childReady, "ready");
    while (!File.Exists(childRelease)) await Task.Delay(20);
    File.WriteAllText(childDone, "done");
    return 0;
}
if (args is ["--crashable-broker", var brokerState, var brokerReady, var brokerRelease, var brokerDone])
{
    using var brokerLease = UpdateProcessLease.TryEnterInstaller(brokerState)!;
    var nativeArgs = new List<string>();
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") nativeArgs.Add(typeof(Handler).Assembly.Location);
    nativeArgs.AddRange(["--native-lease-child", brokerReady, brokerRelease, brokerDone]);
    using var native = InheritedLeaseProcess.Start(Environment.ProcessPath!, nativeArgs, brokerState, brokerLease.Handles);
    await native.WaitAsync(); return 0;
}
var directory = Path.Combine(Path.GetTempPath(), "buschord-update-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var failures = 0; var checks = 0;
void Check(string name, bool condition) { checks++; Console.WriteLine((condition ? "PASS " : "FAIL ") + name); if (!condition) failures++; }
bool Reject(Action action) { try { action(); return false; } catch (Exception) { return true; } }
async Task<bool> RejectAsync(Func<Task> action) { try { await action(); return false; } catch (Exception) { return true; } }
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var trust = new UpdateTrust(new Uri("https://updates.example.test/"), new Dictionary<string, string> { ["test"] = key.ExportSubjectPublicKeyInfoPem() });
var package = Package("BusChord", "1.1.0", "win-x64", "win-x64-stable");
var now = DateTimeOffset.UtcNow;
var asset = new UpdateAsset("BusChord-1.1.0-full.nupkg", "1.1.0", "https://updates.example.test/package", Convert.ToHexString(SHA256.HashData(package)), package.Length);
var manifest = new UpdateManifest(1, "BusChord", "win-x64", "stable", 10, now.AddMinutes(-1), now.AddDays(7), asset);
string Sign(UpdateManifest m) => UpdateManifests.Sign(m, "test", key);
VerifiedUpdate Verify(string envelope, RollbackState? previous = null) => UpdateManifests.Verify(envelope, trust, "win-x64", "stable", "1.0.0", now, previous);
try
{
    var good = Verify(Sign(manifest));
    var scopedPackage = Path.Combine(directory, "scope.nupkg"); File.WriteAllBytes(scopedPackage, package);
    UpdatePackageScope.Verify(scopedPackage, manifest);
    Check("package identity matches authenticated scope", true);
    Check("publisher mislabeled package RID rejected", Reject(() => UpdatePackageScope.Verify(scopedPackage, manifest with { Rid = "linux-x64" })));
    Check("publisher mislabeled package version rejected", Reject(() => UpdatePackageScope.Verify(scopedPackage, manifest with { Asset = asset with { Version = "2.0.0" } })));
    Check("publisher signature verifies", good.Manifest == manifest);
    Check("tampering is rejected", Reject(() => Verify(Sign(manifest).Replace("test", "evil", StringComparison.Ordinal))));
    using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Check("different signing key rejected", Reject(() => Verify(UpdateManifests.Sign(manifest, "test", other))));
    foreach (var (name, bad) in new (string, UpdateManifest)[] {
        ("RID", manifest with { Rid = "linux-x64" }), ("channel", manifest with { Channel = "preview" }),
        ("app identity", manifest with { AppId = "Other" }), ("expiry", manifest with { ExpiresAt = now.AddSeconds(-1) }),
        ("future issue", manifest with { PublishedAt = now.AddHours(1) }),
        ("downgrade", manifest with { Asset = asset with { Version = "0.9.0" } }),
        ("stable prerelease", manifest with { Asset = asset with { Version = "1.2.0-beta.1" } }),
        ("path traversal", manifest with { Asset = asset with { FileName = "../evil-full.nupkg" } }),
        ("oversize", manifest with { Asset = asset with { Size = long.MaxValue } }),
        ("HTTP", manifest with { Asset = asset with { Url = "http://updates.example.test/file" } }),
        ("credentials", manifest with { Asset = asset with { Url = "https://user:password@updates.example.test/file" } }) })
        Check(name + " rejected", Reject(() => Verify(Sign(bad))));
    var previous = new RollbackState(10, "1.1.0", good.PayloadHash);
    Check("same signed metadata remains usable offline", Verify(Sign(manifest), previous).PayloadHash == good.PayloadHash);
    Check("rollback sequence rejected", Reject(() => Verify(Sign(manifest with { Sequence = 9 }), previous)));
    Check("same sequence equivocation rejected", Reject(() => Verify(Sign(manifest with { ExpiresAt = now.AddDays(8) }), previous)));
    Check("higher sequence cannot lower version", Reject(() => Verify(Sign(manifest with { Sequence = 11, Asset = asset with { Version = "1.0.5" } }), previous)));
    Check("two readers exclude installer", ReadersExcludeInstaller(directory));
    using (var exclusive = UpdateProcessLease.TryEnterInstaller(directory))
        Check("installer excludes app startup", exclusive is not null && Reject(() => UpdateProcessLease.EnterApplication(directory).Dispose()));
    var inherited = UpdateProcessLease.TryEnterInstaller(directory)!;
    var childArguments = new List<string>();
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") childArguments.Add(typeof(Handler).Assembly.Location);
    var ready = Path.Combine(directory, "child-ready"); var release = Path.Combine(directory, "child-release"); var done = Path.Combine(directory, "child-done");
    childArguments.AddRange(["--native-lease-child", ready, release, done]);
    using (var child = InheritedLeaseProcess.Start(Environment.ProcessPath!, childArguments, directory, inherited.Handles))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!File.Exists(ready)) await Task.Delay(20, timeout.Token);
        inherited.Dispose(); // The parent/broker is now gone; only the native child owns the locks.
        Check("parent exit leaves native swap lease alive", Reject(() => UpdateProcessLease.EnterApplication(directory).Dispose()));
        Check("a second broker cannot enter mid-swap", UpdateProcessLease.TryEnterInstaller(directory) is null);
        File.WriteAllText(release, "complete");
        Check("native child completes successfully", await child.WaitAsync(timeout.Token) == 0);
    }
    using (var afterSwap = UpdateProcessLease.EnterApplication(directory)) Check("new app starts only after swap completion", true);
    var crashReady = Path.Combine(directory, "crash-ready"); var crashRelease = Path.Combine(directory, "crash-release"); var crashDone = Path.Combine(directory, "crash-done");
    var crashStart = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") crashStart.ArgumentList.Add(typeof(Handler).Assembly.Location);
    foreach (var arg in new[] { "--crashable-broker", directory, crashReady, crashRelease, crashDone }) crashStart.ArgumentList.Add(arg);
    using (var broker = System.Diagnostics.Process.Start(crashStart)!)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (!File.Exists(crashReady)) await Task.Delay(20, timeout.Token);
            broker.Kill(entireProcessTree: false); await broker.WaitForExitAsync(timeout.Token);
            Check("killed broker leaves swap protected", Reject(() => UpdateProcessLease.EnterApplication(directory).Dispose()));
            File.WriteAllText(crashRelease, "complete");
            while (!File.Exists(crashDone)) await Task.Delay(20, timeout.Token);
            UpdateProcessLease? recovered = null;
            while (recovered is null) { try { recovered = UpdateProcessLease.EnterApplication(directory); } catch (IOException) { await Task.Delay(20, timeout.Token); } }
            using (recovered) Check("broker crash recovers when native swap finishes", true);
        }
        finally { File.WriteAllText(crashRelease, "cleanup"); if (!broker.HasExited) broker.Kill(); }
    }
    var calls = 0;
    using (var disabled = new TrustedUpdateSource(UpdateTrust.Unconfigured, "win-x64", "stable", "1.0.0", directory,
        new Handler(_ => { calls++; return new(HttpStatusCode.OK); })))
        Check("unconfigured makes no request", await disabled.CheckAsync() is null && calls == 0);
    using (var emptyRelease = new TrustedUpdateSource(trust, "win-x64", "stable", "1.0.0", directory,
        new Handler(_ => new(HttpStatusCode.NotFound))))
        Check("configured future feed 404 means no update", await emptyRelease.CheckAsync() is null);
    var previewManifest = manifest with { Channel = "preview", Asset = asset with { Version = "1.2.0-beta.1" } };
    var previewCalls = 0;
    using (var previewSource = new TrustedUpdateSource(trust with { Feed = new Uri("https://github.com/example/project/releases/latest/download/") },
        "win-x64", "preview", "1.0.0", Path.Combine(directory, "preview"), new Handler(request =>
        {
            previewCalls++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(request.RequestUri!.IdnHost == "api.github.com"
                ? "[{\"draft\":false,\"prerelease\":true,\"assets\":[{\"name\":\"updates.win-x64.preview.json\",\"browser_download_url\":\"https://github.com/example/project/releases/download/v1.2.0-beta.1/updates.win-x64.preview.json\"}]}]"
                : Sign(previewManifest))) };
        })))
        Check("GitHub prerelease discovery verifies the preview feed", (await previewSource.CheckAsync())?.Manifest.Channel == "preview" && previewCalls == 2);
    var current = package;
    using var source = new TrustedUpdateSource(trust, "win-x64", "stable", "1.0.0", directory,
        new Handler(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(Sign(manifest)) : current) }));
    var update = await source.CheckAsync();
    var target = Path.Combine(directory, asset.FileName);
    await source.DownloadReleaseEntry(null!, TrustedUpdateSource.ToAsset(update!), target, _ => { });
    await source.MarkReadyAsync(directory);
    Check("authenticated download stages and reopens offline", File.ReadAllBytes(target).SequenceEqual(package) && await source.PendingAsync(directory) is not null);
    await source.RecordApplyFailureAsync(TrustedUpdateSource.ToAsset(update!), 42);
    Check("failed apply does not retry at every startup", await source.PendingAsync(directory) is null);
    source.ClearApplyFailureForUserRetry();
    Check("explicit retry reopens a verified pending update", await source.PendingAsync(directory) is not null);
    File.WriteAllText(target, "tampered");
    Check("post-download tamper rejected at apply boundary", await RejectAsync(() => source.PendingAsync(directory)));
    current = package.Select(b => (byte)(b ^ 1)).ToArray();
    Check("wrong streamed bytes rejected", await RejectAsync(() => source.DownloadReleaseEntry(null!, TrustedUpdateSource.ToAsset(update!), target, _ => { })));
    Check("failed staging is cleaned", !Directory.EnumerateFiles(directory, "*.staging").Any());
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    Check("cancel before commit preserves destination", await RejectAsync(() => source.DownloadReleaseEntry(null!, TrustedUpdateSource.ToAsset(update!), target, _ => { }, cancelled.Token)) && File.ReadAllText(target) == "tampered");
    var code = Path.Combine(directory, "fake-current"); Directory.CreateDirectory(code); File.WriteAllText(Path.Combine(code, "BusChord.exe"), "old-code");
    var backup = await WindowsCodeBackup.CreateAsync(code, directory);
    Directory.Delete(code, true); // Models SDK failure after it removes current/temp_old.
    await WindowsCodeBackup.RestoreAsync(backup, code);
    Check("independent backup restores SDK current-missing failure", File.ReadAllText(Path.Combine(code, "BusChord.exe")) == "old-code");
    File.WriteAllText(Path.Combine(backup, "files", "BusChord.exe"), "damaged");
    Check("damaged recovery copy is rejected without touching current", await RejectAsync(() => WindowsCodeBackup.RestoreAsync(backup, code)) && File.ReadAllText(Path.Combine(code, "BusChord.exe")) == "old-code");
    var inventoried = Path.Combine(directory, "inventoried-current"); Directory.CreateDirectory(inventoried);
    var known = Path.Combine(inventoried, "BusChord.dll"); File.WriteAllText(known, "published-code");
    InstalledCodeInventory.Create(inventoried, "BusChord", "1.0.0", "win-x64", "win-x64-stable");
    var inventoryBytes = File.ReadAllBytes(Path.Combine(inventoried, InstalledCodeInventory.FileName));
    var sdkMetadata = "<package><metadata><id>BusChord</id><version>1.0.0</version><rid>win-x64</rid><channel>win-x64-stable</channel><mainExe>BusChord.exe</mainExe></metadata></package>";
    File.WriteAllText(Path.Combine(inventoried, "sq.version"), sdkMetadata);
    bool CleanInventory() => InstalledCodeInventory.Check(inventoried, "BusChord", "1.0.0", "win-x64").Safe;
    Check("published code and narrowly scoped SDK metadata pass inventory", CleanInventory());
    var note = Path.Combine(inventoried, "private-notes.unknown"); File.WriteAllText(note, "user data");
    Check("arbitrary user file defers apply without deleting it", !CleanInventory() && File.ReadAllText(note) == "user data"); File.Delete(note);
    var extraDirectory = Path.Combine(inventoried, "Private empty folder"); Directory.CreateDirectory(extraDirectory);
    Check("unexpected empty directory defers apply", !CleanInventory()); Directory.Delete(extraDirectory);
    File.WriteAllText(known, "published-CODE"); Check("same-size code modification defers apply", !CleanInventory()); File.WriteAllText(known, "published-code");
    File.Delete(known); Check("missing code file defers apply", !CleanInventory()); File.WriteAllText(known, "published-code");
    File.WriteAllText(Path.Combine(inventoried, "sq.version"), sdkMetadata.Replace("win-x64-stable", "win-x64-preview", StringComparison.Ordinal));
    Check("changed SDK metadata scope defers apply", !CleanInventory()); File.WriteAllText(Path.Combine(inventoried, "sq.version"), sdkMetadata);
    File.WriteAllText(Path.Combine(inventoried, InstalledCodeInventory.FileName), File.ReadAllText(Path.Combine(inventoried, InstalledCodeInventory.FileName)).Replace("published", "modified", StringComparison.Ordinal) + " ");
    // Change a hashed manifest field while retaining its old self checksum.
    File.WriteAllText(Path.Combine(inventoried, InstalledCodeInventory.FileName), System.Text.Encoding.UTF8.GetString(inventoryBytes).Replace("1.0.0", "2.0.0", StringComparison.Ordinal));
    Check("changed inventory scope or self checksum defers apply", !CleanInventory()); File.WriteAllBytes(Path.Combine(inventoried, InstalledCodeInventory.FileName), inventoryBytes);
    if (OperatingSystem.IsLinux())
    {
        var linked = Path.Combine(inventoried, "unexpected-link"); File.CreateSymbolicLink(linked, known);
        Check("linked installed content defers apply", !CleanInventory()); File.Delete(linked);
    }
    Check("removing user data restores eligibility", CleanInventory());
    var automaticRoot = Path.Combine(directory, "automatic-root"); var automaticCurrent = Path.Combine(automaticRoot, "current");
    var automaticState = Path.Combine(directory, "automatic-state"); var automaticPackages = Path.Combine(automaticRoot, "packages");
    Directory.CreateDirectory(automaticCurrent); Directory.CreateDirectory(automaticPackages);
    File.WriteAllText(Path.Combine(automaticCurrent, "BusChord.dll"), "old-code");
    InstalledCodeInventory.Create(automaticCurrent, "BusChord", "1.0.0", "win-x64", "win-x64-stable");
    File.WriteAllText(Path.Combine(automaticCurrent, "sq.version"), sdkMetadata);
    var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
    try
    {
        if (OperatingSystem.IsLinux()) Environment.SetEnvironmentVariable("APPIMAGE", Path.Combine(automaticRoot, "BusChord.AppImage"));
        var locator = new Velopack.Locators.TestVelopackLocator("BusChord", "1.0.0", automaticPackages, automaticCurrent, automaticRoot, Path.Combine(automaticRoot, "Update.exe"), "win-x64-stable");
        using var automatic = new AutomaticUpdates(trust, "win-x64", "stable", automaticState, locator,
            new Handler(request => new(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".json", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(Sign(manifest)) : package) }));
        var status = ""; automatic.StatusChanged += value => status = value;
        await automatic.CheckAndDownloadAsync(UpdatePolicy.Automatic);
        Check("automatic coordinator prepares an authenticated update", status == "ready");
        using var failureWriter = new TrustedUpdateSource(trust, "win-x64", "stable", "1.0.0", automaticState, new Handler(_ => new(HttpStatusCode.NotFound)));
        await failureWriter.RecordApplyFailureAsync(TrustedUpdateSource.ToAsset(good), 42);
        await automatic.CheckAndDownloadAsync(UpdatePolicy.Automatic);
        Check("routine check retains retry-required instead of ready", status == "retry-required");
        await automatic.RetryPendingUpdateAsync();
        await automatic.CheckAndDownloadAsync(UpdatePolicy.Automatic);
        Check("explicit retry clears failure suppression without bypassing verification", status == "ready");
    }
    finally { Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage); }
    if (OperatingSystem.IsWindows() && Path.GetFileNameWithoutExtension(Environment.ProcessPath) != "dotnet")
    {
        var partialRoot = Path.Combine(directory, "partial-native-root"); var partialCurrent = Path.Combine(partialRoot, "current");
        var partialState = Path.Combine(directory, "partial-native-state"); var partialPackages = Path.Combine(directory, "partial-native-packages");
        Directory.CreateDirectory(partialCurrent); Directory.CreateDirectory(partialPackages);
        File.WriteAllText(Path.Combine(partialRoot, "fake-native-fixture"), "private test only");
        File.WriteAllText(Path.Combine(partialCurrent, "old-code.txt"), "original preserved");
        InstalledCodeInventory.Create(partialCurrent, "BusChord", "0.9.0", "win-x64", "win-x64-stable");
        File.WriteAllText(Path.Combine(partialCurrent, "sq.version"), sdkMetadata.Replace("1.0.0", "0.9.0", StringComparison.Ordinal));
        var targetVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Handler).Assembly.Location).ProductVersion!.Split('+')[0];
        var partialBytes = Package("BusChord", targetVersion, "win-x64", "win-x64-stable");
        var partialAsset = new UpdateAsset($"BusChord-{targetVersion}-full.nupkg", targetVersion, asset.Url, Convert.ToHexString(SHA256.HashData(partialBytes)), partialBytes.Length);
        var partialManifest = manifest with { Asset = partialAsset };
        using var partialSource = new TrustedUpdateSource(trust, "win-x64", "stable", "0.9.0", partialState,
            new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(Sign(partialManifest))) }));
        await partialSource.CheckAsync(); File.WriteAllBytes(Path.Combine(partialPackages, partialAsset.FileName), partialBytes); await partialSource.MarkReadyAsync(partialPackages);
        var codeResult = await ApplyBroker.ApplyAsync(new("win-x64", "stable", "0.9.0", partialState, partialRoot, partialPackages, Environment.ProcessPath!, Environment.ProcessPath!, [], false), trust);
        Check("native Exit0 incomplete current restores independent backup", codeResult == -4 && File.ReadAllText(Path.Combine(partialCurrent, "old-code.txt")) == "original preserved");
        Check("partial native success is suppressed until explicit retry", await partialSource.PendingAsync(partialPackages) is null);
    }
    if (OperatingSystem.IsLinux())
    {
        var image = Path.Combine(directory, "BusChord.AppImage"); var bytes = Encoding.UTF8.GetBytes("new-authenticated-image");
        File.WriteAllText(image, "old-image");
        await AtomicAppImageSwap.StageAndReplaceAsync(new MemoryStream(bytes), bytes.Length, image);
        Check("same-filesystem AppImage swap preserves old inode", File.ReadAllBytes(image).SequenceEqual(bytes) && File.ReadAllText(image + ".previous") == "old-image");
        var before = File.ReadAllBytes(image);
        Check("disk-full staging fault preserves image", await RejectAsync(() => AtomicAppImageSwap.StageAndReplaceAsync(new FailingReadStream(new IOException("Synthetic ENOSPC")), 100, image)) && File.ReadAllBytes(image).SequenceEqual(before));
        Check("permission staging fault preserves image", await RejectAsync(() => AtomicAppImageSwap.StageAndReplaceAsync(new FailingReadStream(new UnauthorizedAccessException("Synthetic EACCES")), 100, image)) && File.ReadAllBytes(image).SequenceEqual(before));
        Check("interrupted AppImage write preserves image", await RejectAsync(() => AtomicAppImageSwap.StageAndReplaceAsync(new MemoryStream(bytes), bytes.Length, image, cancelled.Token)) && File.ReadAllBytes(image).SequenceEqual(before));
        Check("incomplete AppImage write preserves image", await RejectAsync(() => AtomicAppImageSwap.StageAndReplaceAsync(new MemoryStream(bytes), bytes.Length + 1, image)) && File.ReadAllBytes(image).SequenceEqual(before));
        Check("failed AppImage stages are cleaned", !Directory.EnumerateFiles(directory, ".buschord-stage-*").Any());
    }
    Console.WriteLine($"{checks} checks, {failures} failures");
}
finally { Directory.Delete(directory, true); }
return failures == 0 ? 0 : 1;

static bool ReadersExcludeInstaller(string directory)
{
    using var first = UpdateProcessLease.EnterApplication(directory); using var second = UpdateProcessLease.EnterApplication(directory);
    using var writer = UpdateProcessLease.TryEnterInstaller(directory); return writer is null;
}
static byte[] Package(string appId, string version, string rid, string channel)
{
    using var output = new MemoryStream();
    using (var archive = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, true))
    {
        using (var writer = new StreamWriter(archive.CreateEntry(appId + ".nuspec").Open()))
            writer.Write($"<package><metadata><id>{appId}</id><version>{version}</version><rid>{rid}</rid><channel>{channel}</channel><os>{rid.Split('-')[0]}</os></metadata></package>");
        using (var writer = new StreamWriter(archive.CreateEntry("lib/app/payload.txt").Open())) writer.Write("Synthetic package; never executed.");
    }
    return output.ToArray();
}
sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); } }
sealed class FailingReadStream(Exception error) : MemoryStream
{ public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => ValueTask.FromException<int>(error); }

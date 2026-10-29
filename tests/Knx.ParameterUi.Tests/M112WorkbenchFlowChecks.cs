using Knx.App.Workbench;
using Knx;
using Knx.Bus;
using System.Text.Json.Nodes;

/// <summary>Exercises the actual UI backend's offline compiler with the existing synthetic M112 source profile.</summary>
internal static class M112WorkbenchFlowChecks
{
    internal static async Task<int> RunAsync(string directory)
    {
        var failures = 0;
        void Check(string name, bool passed) { Console.WriteLine((passed ? "PASS " : "FAIL ") + "M112Workbench " + name); if (!passed) failures++; }
        var baseline = M112UnionBuildChecks.Fixture(directory, fullLoad: true);
        var desired = M112UnionBuildChecks.Fixture(directory, fullLoad: true);
        desired.Device.ParameterValues["Ref0"] = "6";
        var backend = new M112CommissioningBackend();
        var review = await backend.PrepareAsync(baseline.Project, baseline.Device, desired.Project, desired.Device, CancellationToken.None);
        Check("real offline adapter compiles the supported source profile", review.IsCurrent() && review.Target == desired.Device.Address.ToString());
        Check("real review binds one exact nonempty fingerprint", review.Fingerprint.Length == 64);
        Check("real review exposes the changed persistent byte range", review.Ranges.Any(range => range.Address <= 0x4420 && range.Address + range.Length > 0x4420));
        Check("real identity expectations contain manufacturer and application bytes", review.ExpectedManufacturer.Length == 4
            && review.ExpectedApplication.Length == 10 && review.ExpectedApplication.StartsWith(review.ExpectedManufacturer, StringComparison.Ordinal));
        desired.Device.ParameterValues["Ref0"] = "7";
        Check("real compiled review detects subsequent target parameter drift", !review.IsCurrent());
        var image = OfflineM112ApplicationBuild.Build(baseline.Project, baseline.Device);
        var device = new M112DownloadChecks.FakeDevice(image);
        var initial = await M112ApplicationDownload.RunAsync(device.OpenAsync, baseline.Project, baseline.Device, image, device.Identity, device.Identity.Address, directory);
        Check("inspection fixture has a genuine executor before-image", initial.DownloadedAndVerified && initial.Error is null);
        var inspection = await M112ApplicationDownload.InspectRecoveryAsync(initial.RecoveryPath);
        Check("inspection exposes bounded validated metadata without memory payload", inspection.Identity == device.Identity
            && inspection.FileSha256.Length == 64 && inspection.Segments.Count == image.Segments.Count && inspection.Segments.All(segment => segment.OriginalSha256.Length == 64));
        var opens = 0;
        Task<IManagementConnection> NeverOpen(CancellationToken _) { opens++; throw new InvalidOperationException("No connection may open."); }
        var refused = false;
        try { await M112ApplicationDownload.RecoverInspectedAsync(NeverOpen, inspection, "1.1.254"); } catch (ArgumentException) { refused = true; }
        Check("inspected recovery wrong target opens no device", refused && opens == 0);
        var original = await File.ReadAllBytesAsync(initial.RecoveryPath);
        await File.AppendAllTextAsync(initial.RecoveryPath, " "); refused = false;
        try { await M112ApplicationDownload.RecoverInspectedAsync(NeverOpen, inspection, device.Identity.Address); } catch (InvalidDataException) { refused = true; }
        Check("inspected recovery detects file replacement even with unchanged snapshot", refused && opens == 0);
        var broken = JsonNode.Parse(original)!; broken["Sha256"] = new string('0', 64);
        await File.WriteAllTextAsync(initial.RecoveryPath, broken.ToJsonString()); refused = false;
        try { await M112ApplicationDownload.InspectRecoveryAsync(initial.RecoveryPath); } catch (InvalidDataException) { refused = true; }
        Check("inspection refuses corrupt snapshot checksum before any I/O backend", refused && opens == 0);
        refused = false;
        try { await M112ApplicationDownload.RecoverAsync(NeverOpen, initial.RecoveryPath, device.Identity, device.Identity.Address); } catch (InvalidDataException) { refused = true; }
        Check("legacy recovery uses the same checksum validator", refused && opens == 0);
        await File.WriteAllBytesAsync(initial.RecoveryPath, original);
        device.Memory[0xB6EA] = device.Memory[0xB6EB] = device.Memory[0xB6EC] = 0;
        device.RejectApplicationWhileUnloaded = true;
        var restored = await M112ApplicationDownload.RecoverInspectedAsync(device.OpenAsync, inspection, device.Identity.Address);
        Check("cold inspected recovery succeeds with unloaded application identity unavailable", restored.OriginalConfigurationRestoredAndVerified && restored.Error is null
            && device.UnloadedApplicationReadAttempts == 0 && device.Loaded);
        return failures;
    }
}

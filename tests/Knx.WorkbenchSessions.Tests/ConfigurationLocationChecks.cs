using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knx;
using Knx.App.Workbench;
using Knx.Workbench;

internal static class ConfigurationLocationChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok)
        { Console.WriteLine((ok ? "PASS " : "FAIL ") + "Configuration location: " + name); if (!ok) failures++; }
        var (project, line, segment, device) = Fixture();
        var baseline = DeviceConfigurationSnapshot.Freeze(project, device);
        using (var payload = JsonDocument.Parse(baseline.PayloadJson))
            Check("frozen projection explicitly identifies version 2", payload.RootElement.GetProperty("Version").GetInt32() == 2);
        var loaded = DeviceProgrammingState.Empty(device.Id).ApplySuccess(DeviceLoadFlags.All, baseline.Signature,
            DateTimeOffset.Parse("2026-10-27T10:00:00Z"), "synthetic-demo", ProgrammingOrigin.Demo);
        void NetworkChange(string name, Action edit, Action restore, string field, string? value)
        {
            edit(); var changed = DeviceConfigurationSnapshot.Freeze(project, device);
            var kind = DeviceConfigurationSnapshot.ClassifyChange(baseline, changed);
            using var payload = JsonDocument.Parse(changed.PayloadJson);
            Check(name + " changes the signature and captures the exact fact", baseline.Signature != changed.Signature
                && payload.RootElement.GetProperty(field).GetString() == value && loaded.NeedsProgramming(changed.Signature));
            Check(name + " invalidates only network configuration", kind == DeviceChangeKind.Configuration
                && loaded.ResetForChange(kind).Flags == (DeviceLoadFlags.All & ~DeviceLoadFlags.Configuration));
            restore();
            Check(name + " restoration produces the same current signature", DeviceConfigurationSnapshot.Freeze(project, device) == baseline);
        }
        NetworkChange("segment medium", () => segment.MediumTypeRefId = "MT-5", () => segment.MediumTypeRefId = null, "SegmentMedium", "MT-5");
        NetworkChange("line domain", () => line.DomainAddress = "00a10F", () => line.DomainAddress = null, "LineDomainAddress", "00a10F");
        NetworkChange("segment domain", () => segment.DomainAddress = "001234", () => segment.DomainAddress = null, "SegmentDomainAddress", "001234");
        NetworkChange("explicit empty domain", () => segment.DomainAddress = "", () => segment.DomainAddress = null, "SegmentDomainAddress", "");
        using (var original = JsonDocument.Parse(baseline.PayloadJson))
            Check("enqueue-time payload retains its original network facts", original.RootElement.GetProperty("SegmentMedium").ValueKind == JsonValueKind.Null
                && original.RootElement.GetProperty("LineDomainAddress").ValueKind == JsonValueKind.Null
                && original.RootElement.GetProperty("SegmentDomainAddress").ValueKind == JsonValueKind.Null);
        var unrelated = new Segment { Id = "unrelated", Address = 7, MediumTypeRefId = "MT-5", DomainAddress = "123456" }; line.Segments.Add(unrelated);
        Check("another segment's network facts do not change this device", DeviceConfigurationSnapshot.Freeze(project, device) == baseline);
        line.Name = "Renamed line"; segment.Name = "Renamed segment"; device.Name = "Renamed device";
        Check("location labels remain irrelevant to configuration", DeviceConfigurationSnapshot.Freeze(project, device) == baseline
            && DeviceConfigurationSnapshot.ClassifyChange(baseline, DeviceConfigurationSnapshot.Freeze(project, device)) == DeviceChangeKind.None);
        segment.Address = 2;
        Check("segment number remains a topology change", DeviceConfigurationSnapshot.ClassifyChange(baseline, DeviceConfigurationSnapshot.Freeze(project, device)) == DeviceChangeKind.Topology);
        segment.Address = 0; segment.MediumTypeRefId = "MT-5"; device.Address = new(1, 1, 3);
        var combined = DeviceConfigurationSnapshot.ClassifyChange(baseline, DeviceConfigurationSnapshot.Freeze(project, device));
        Check("simultaneous address and network edits retain both precise classes", combined == (DeviceChangeKind.IndividualAddress | DeviceChangeKind.Configuration)
            && DeviceProgrammingState.AffectedFlags(combined) == (DeviceLoadFlags.IndividualAddress | DeviceLoadFlags.Configuration));
        segment.MediumTypeRefId = null; device.Address = new(1, 1, 1);

        // Independent reproduction of the former unversioned hash on this empty-object fixture.
        var oldProjection = new
        {
            Address = device.Address.ToString(), Line = (byte?)line.Address, Area = (byte)1, Segment = (byte?)segment.Address,
            Medium = line.MediumTypeRefId, device.ProductRefId, device.Hardware2ProgramRefId,
            Application = (string?)null, Secure = (bool?)null,
            Params = device.ParameterValues.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), Objects = Array.Empty<object>(),
        };
        var oldHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(oldProjection))));
        var oldState = loaded with { ImageHash = oldHash };
        var restored = DeviceProgrammingStates.Parse(DeviceProgrammingStates.Serialize(project.Id, [oldState]), project.Id)[device.Id];
        Check("persisted opaque legacy signatures remain loadable but stale", oldHash.Length == 64 && oldHash != baseline.Signature
            && restored.ImageHash == oldHash && restored.NeedsProgramming(baseline.Signature));

        var legacy = JsonNode.Parse(baseline.PayloadJson)!.AsObject(); legacy.Remove("Version");
        legacy.Remove("SegmentMedium"); legacy.Remove("LineDomainAddress"); legacy.Remove("SegmentDomainAddress");
        var legacySnapshot = new FrozenConfiguration(baseline.Signature, legacy.ToJsonString());
        Check("equal opaque hashes cannot conceal an unversioned frozen projection", DeviceProgrammingState.AffectedFlags(
            DeviceConfigurationSnapshot.ClassifyChange(legacySnapshot, baseline)) == DeviceLoadFlags.All);
        var future = JsonNode.Parse(baseline.PayloadJson)!.AsObject(); future["Version"] = 999;
        var unknown = new FrozenConfiguration(baseline.Signature, future.ToJsonString());
        Check("future projection versions conservatively invalidate all flags", DeviceProgrammingState.AffectedFlags(
            DeviceConfigurationSnapshot.ClassifyChange(unknown, baseline)) == DeviceLoadFlags.All);
        future["Version"] = "2";
        Check("non-numeric projection versions cannot claim equality", DeviceProgrammingState.AffectedFlags(
            DeviceConfigurationSnapshot.ClassifyChange(new(baseline.Signature, future.ToJsonString()), baseline)) == DeviceLoadFlags.All);
        Check("unchanged current projections still preserve every load flag", DeviceConfigurationSnapshot.ClassifyChange(baseline, baseline) == DeviceChangeKind.None);
        return failures;
    }

    private static (Project, Line, Segment, DeviceInstance) Fixture()
    {
        var project = new Project { Id = "synthetic-network", Name = "Synthetic network configuration" };
        var installation = new Installation { Id = "I" }; var area = new Area { Id = "A", Address = 1 };
        var line = new Line { Id = "L", Address = 1, MediumTypeRefId = "MT-0" }; var segment = new Segment { Id = "S", Address = 0 };
        var device = new DeviceInstance { Id = "D", ProductRefId = "synthetic-product", Address = new(1, 1, 1) };
        segment.Devices.Add(device); line.Segments.Add(segment); area.Lines.Add(line); installation.Areas.Add(area); project.Installations.Add(installation);
        return (project, line, segment, device);
    }
}

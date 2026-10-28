using System.Collections.ObjectModel;

namespace Knx.Catalog;

/// <summary>Known manufacturer metadata requiring an external DCA, without claiming an executable adapter.</summary>
public sealed record KnownDcaRequirement(DcaApplicationIdentity Application, string AppId, string DisplayName, string ManifestSource);

/// <summary>Bounded application identities declared by official manifests; no package code is loaded.</summary>
public static class KnownDcaRequirementsRegistry
{
    private static readonly IReadOnlyDictionary<DcaApplicationIdentity, KnownDcaRequirement> ByApplication =
        new ReadOnlyDictionary<DcaApplicationIdentity, KnownDcaRequirement>(Build().ToDictionary(requirement => requirement.Application));

    public static IReadOnlyList<KnownDcaRequirement> Requirements { get; } = Array.AsReadOnly(ByApplication.Values.ToArray());

    public static bool RequiresDca(string? applicationId) => Find(applicationId) is not null;

    public static KnownDcaRequirement? Find(string? applicationId) => DcaApplicationIdentity.TryParse(applicationId, out var identity)
        ? ByApplication.GetValueOrDefault(identity) : null;

    private static IEnumerable<KnownDcaRequirement> Build()
    {
        // Versions are hexadecimal KSXML bytes. Links identify the corresponding manufacturer packages.
        const string mdt64 = "https://www.mdt.de/fileadmin/user_upload/user_upload/download/MDT_DCA_SCN_04_DALIControl_64_2x64_v1_1_2_0.etsapp";
        const string mdtPro = "https://www.mdt.de/fileadmin/user_upload/user_upload/download/SCN-DA641P-04S_MDT_DCA_V2_1_2_0.etsapp";
        const string jung = "https://application.jung.de/downloads/download/62121/de";
        const string gira = "https://partner.gira.de/data3/GiraDaliGwDca_v2.0.0.1471.zip";
        foreach (var program in new ushort[] { 0x0154, 0x0155 })
            foreach (var version in new byte[] { 0x10, 0x11 })
                yield return new(new(0x0083, program, version), "M0083-A0158", "MDT DALI Control 64/2x64 DCA", mdt64);
        foreach (var version in new byte[] { 0x20, 0x21 })
            yield return new(new(0x0083, 0x0153, version), "M0083-A01B0", "MDT DALI Control PRO64 DCA", mdtPro);
        foreach (var version in new byte[] { 0x11, 0x12, 0x13, 0x14, 0x15 })
            yield return new(new(0x0004, 0x5018, version), "M0004-A0131", "JUNG Smart Panel 8 DCA", jung);
        foreach (var program in new ushort[] { 0xC030, 0xC031 })
            foreach (var version in new byte[] { 0x21, 0x22 })
                yield return new(new(0x0008, program, version), "M0008-A01A2", "Gira DALI Gateway Colour DCA", gira);
    }
}

using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Catalog;

namespace Knx.Catalog.Tests;

internal static class KnownDcaRequirementChecks
{
    internal static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool ok) => check("DCA registry: " + name, ok, null);
        // Independent declarations copied from the four inspected official manifests.
        var declared = new[]
        {
            ("M-0083_A-0154-10-0000", "M0083-A0158"), ("M-0083_A-0154-11-0000", "M0083-A0158"),
            ("M-0083_A-0155-10-0000", "M0083-A0158"), ("M-0083_A-0155-11-0000", "M0083-A0158"),
            ("M-0083_A-0153-20-0000", "M0083-A01B0"), ("M-0083_A-0153-21-0000", "M0083-A01B0"),
            ("M-0004_A-5018-11-0000", "M0004-A0131"), ("M-0004_A-5018-12-0000", "M0004-A0131"),
            ("M-0004_A-5018-13-0000", "M0004-A0131"), ("M-0004_A-5018-14-0000", "M0004-A0131"),
            ("M-0004_A-5018-15-0000", "M0004-A0131"), ("M-0008_A-C030-21-0000", "M0008-A01A2"),
            ("M-0008_A-C030-22-0000", "M0008-A01A2"), ("M-0008_A-C031-21-0000", "M0008-A01A2"),
            ("M-0008_A-C031-22-0000", "M0008-A01A2"),
        };
        Check("all and only the fifteen observed identities are registered", KnownDcaRequirementsRegistry.Requirements.Count == declared.Length
            && declared.All(d => KnownDcaRequirementsRegistry.Find(d.Item1)?.AppId == d.Item2)
            && KnownDcaRequirementsRegistry.Requirements.Select(r => r.Application).Distinct().Count() == declared.Length);
        Check("actual catalogue fingerprints and original manufacturers use the same three fields", new[]
        {
            "M-0083_A-0154-11-B606-O00EF", "M-0083_A-0155-11-FA68-O00EF", "M-0004_A-5018-15-17D9",
            "M-0008_A-C030-22-8D1A-O000A", "M-0008_A-C031-22-B9C9-O000A",
        }.All(KnownDcaRequirementsRegistry.RequiresDca));
        Check("older public PRO generation is not inferred from the current manifest", !KnownDcaRequirementsRegistry.RequiresDca("M-0083_A-0153-10-297A-O00EF"));
        Check("manufacturer program and version remain exact", new[]
        {
            "M-0004_A-0154-11-B606", "M-0083_A-5018-15-17D9", "M-0008_A-C032-22-B9C9-O000A",
            "M-0008_A-C031-23-B9C9-O000A", "M-0004_A-5018-16-17D9", "M-0083_A-0154-12-B606",
        }.All(id => !KnownDcaRequirementsRegistry.RequiresDca(id)));
        Check("names suffixes paths and malformed identities cannot trigger detection", new string?[]
        {
            null, "", "JUNG Smart Panel 8", "M-0004_A-5018-15", "M-0004_A-5018-15-17D9.xml",
            "M-0004_A-5018-15-17D9-extra", "M-0004_A-5018-15-17D9/anything", "M-0004_A-5018-15-17D9 ",
        }.All(id => KnownDcaRequirementsRegistry.Find(id) is null));
        Check("requirements carry descriptive package information without an installed adapter claim", KnownDcaRequirementsRegistry.Requirements.All(r =>
            r.AppId.Length > 0 && r.DisplayName.Length > 0 && Uri.TryCreate(r.ManifestSource, UriKind.Absolute, out var source) && source.Scheme == "https"));
        var readOnly = false;
        try { ((IList<KnownDcaRequirement>)KnownDcaRequirementsRegistry.Requirements).Clear(); }
        catch (NotSupportedException) { readOnly = true; }
        Check("callers cannot alter the shared metadata registry", readOnly && KnownDcaRequirementsRegistry.Requirements.Count == 15);
        Check("catalogue headers require a DCA without eager model or extension loading", declared.All(d =>
        {
            var header = new CatalogApplicationProgram { Id = d.Item1 };
            return header.RequiresDca && !header.IsStaticallyEditable && !header.IsEditable && header.Extensions.Count == 0
                && header.ParameterInstances.Count == 0 && header.ParameterTypes.Count == 0;
        }));
        var ordinary = new CatalogApplicationProgram { Id = "M-0008_A-20E0-22-B419-O000A", Name = "DALI Smart Panel DCA" };
        Check("ordinary Gira heating and unrelated MDT applications remain statically editable", !ordinary.RequiresDca && ordinary.IsEditable
            && !new CatalogApplicationProgram { Id = "M-0083_A-0317-30-0000" }.RequiresDca);
        Check("hardware aliases must first resolve to a real application header", !KnownDcaRequirementsRegistry.RequiresDca("M-0004_H-panel_I-0000_A-5018-15-17D9"));
        Check("malformed fingerprint or original manufacturer cannot broaden a registry match", new[]
        {
            "M-0004_A-5018-15-17D", "M-0004_A-5018-15-17D90", "M-0004_A-5018-15-17D9-O00F",
            "M-0004_A-5018-15-17D9-O000F-extra", "M-0004_A-5018-15-17d9",
        }.All(id => !KnownDcaRequirementsRegistry.RequiresDca(id)));
        var renamed = new CatalogApplicationProgram { Id = "M-0004_A-5018-15-17D9", Name = "Ordinary heater", ApplicationNumber = "0000", ApplicationVersion = "00" };
        Check("the application header identity controls detection independently of its text fields", renamed.RequiresDca && !renamed.IsEditable);
        Check("version fields are hexadecimal bytes rather than decimal labels", KnownDcaRequirementsRegistry.Find("M-0083_A-0154-10-0000")?.Application.ProgramVersion == 0x10
            && KnownDcaRequirementsRegistry.Find("M-0083_A-0153-20-0000")?.Application.ProgramVersion == 0x20);
        Check("read-only compatibility traces contain exactly the three binding fields", KnownDcaRequirementsRegistry.Find("M-0008_A-C031-22-B9C9-O000A")?.Application.ToString()
            == "M-0008_A-C031-22");
        var pure = ApplicationSnapshot.Evaluate(XDocument.Parse("<KNX><ApplicationProgram Id='M-0004_A-5018-15-17D9'><Static/><Dynamic/></ApplicationProgram></KNX>"), new Dictionary<string, string?>());
        Check("pure snapshot evaluation stays usable by a future verified adapter", pure.IsValid && pure.Evaluation.ScriptExecutions == 0
            && new CatalogApplicationProgram { Id = "M-0004_A-5018-15-17D9" }.RequiresDca);
        var identities = string.Join("", declared.Where(d => d.Item2 == "M0083-A0158").Select(d => "<ApplicationProgram Id='" + d.Item1 + "'/>"));
        var manifest = DcaPackageManifest.Parse(Encoding.UTF8.GetBytes("<Knxapp AppId='M0083-A0158' AddInAssembly='metadata.dll'><DeviceConfiguration>" + identities + "</DeviceConfiguration></Knxapp>"));
        Check("registry matching agrees with the bounded manifest reader", declared.Where(d => d.Item2 == manifest.AppId).All(d =>
            manifest.DeclaresSupportFor(d.Item1.Replace("0000", "1234") + "-O00EF")
            && KnownDcaRequirementsRegistry.Find(d.Item1)?.Application == KnownDcaRequirementsRegistry.Find(d.Item1.Replace("0000", "1234") + "-O00EF")?.Application));
    }
}

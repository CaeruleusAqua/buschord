using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Knx.Catalog;

namespace Knx.Catalog.Tests;

internal static class DcaManifestChecks
{
    private const string Xml = """
        <Knxapp AppId="M0083-A0158" AddInAssembly="adapter.dll"><Configuration Is64BitCompatible="true" />
        <DisplayNames><DisplayName LanguageId="en-US" Name="Synthetic DCA" /></DisplayNames>
        <DeviceConfiguration><ApplicationProgram Id="M-0083_A-0154-11-0000" /><ApplicationProgram Id="M-0083_A-0155-11-0000" /></DeviceConfiguration></Knxapp>
        """;

    internal static void Run(Action<string, bool, string?> check)
    {
        void Check(string name, bool ok) => check("DCA manifest: " + name, ok, null);
        void Reject(string name, Action action)
        {
            var rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(name, rejected);
        }
        var manifest = DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml));
        Check("device identity ignores only fingerprint and original manufacturer", manifest.DeclaresSupportFor("M-0083_A-0154-11-B606-O00EF")
            && manifest.DeclaresSupportFor("M-0083_A-0154-11-1234") && manifest.Applications.Count == 2);
        Check("manufacturer, program and version all remain binding", !manifest.DeclaresSupportFor("M-0004_A-0154-11-B606")
            && !manifest.DeclaresSupportFor("M-0083_A-0153-11-B606") && !manifest.DeclaresSupportFor("M-0083_A-0154-10-B606"));
        Check("newer PRO manifest cannot match the older public program", !DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(
            Xml.Replace("0154-11", "0153-20").Replace("0155-11", "0153-21"))).DeclaresSupportFor("M-0083_A-0153-10-297A-O00EF"));
        Check("canonical numeric identity has exact field widths", DcaApplicationIdentity.TryParse("M-0004_A-5018-15-17D9", out var identity)
            && identity.ToString() == "M-0004_A-5018-15" && !DcaApplicationIdentity.TryParse("M-0004_A-5018-1-17D9", out _)
            && !DcaApplicationIdentity.TryParse("M-0004_A-5018-15-17D9-extra", out _));
        Check("metadata stays descriptive without claiming package authentication", manifest.AppId == "M0083-A0158"
            && manifest.DisplayNames["en-US"] == "Synthetic DCA" && manifest.PackageSha256 is null && manifest.AssemblySha256 is null);
        Reject("DTD and entity expansion are prohibited", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes("<!DOCTYPE Knxapp [<!ENTITY x 'ignored'>]>" + Xml)));
        Reject("manifest byte budget is enforced", () => DcaPackageManifest.Parse(new byte[1024 * 1024 + 1]));
        Reject("unknown XML roots are rejected", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml.Replace("Knxapp", "Other"))));
        Reject("invalid package identity is rejected", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml.Replace("M0083-A0158", "other"))));
        Reject("invalid declared program identity is rejected", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml.Replace("M-0083_A-0154-11-0000", "MDT"))));
        Reject("ambiguous configuration roots are rejected", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml.Replace("</Knxapp>", "<DeviceConfiguration /></Knxapp>"))));
        Reject("assembly traversal cannot enter a descriptor", () => DcaPackageManifest.Parse(Encoding.UTF8.GetBytes(Xml.Replace("adapter.dll", "../adapter.dll"))));

        var directory = Path.Combine(Path.GetTempPath(), "buschord-dca-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "synthetic.etsapp");
            var probe = "probe-" + Guid.NewGuid().ToString("N") + ".txt";
            void Package(bool assembly = true, bool duplicateManifest = false)
            {
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                void Add(string name, byte[] bytes) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
                Add("AddInManifest.xml", Encoding.UTF8.GetBytes(Xml));
                if (duplicateManifest) Add("ADDINMANIFEST.XML", Encoding.UTF8.GetBytes(Xml));
                if (assembly) Add("adapter.dll", Encoding.ASCII.GetBytes("Synthetic metadata fixture, not executable."));
                Add("images/", []); Add("../" + probe, Encoding.UTF8.GetBytes("Never extract this data."));
            }
            Package();
            var before = SHA256.HashData(File.ReadAllBytes(path));
            var read = DcaPackageManifest.Read(path);
            Check("package inspection hashes exact bytes without extracting or running members", read.PackageSha256 == Convert.ToHexString(before).ToLowerInvariant()
                && read.AssemblySha256?.Length == 64 && Directory.GetFiles(directory).Length == 1
                && !File.Exists(Path.Combine(Path.GetTempPath(), probe)) && SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(before));
            Check("package compatibility and manifest compatibility agree", read.Applications.SequenceEqual(manifest.Applications)
                && read.DeclaresSupportFor("M-0083_A-0155-11-FA68-O00EF"));
            File.Delete(path); Package(assembly: false);
            Reject("missing declared assembly is rejected", () => DcaPackageManifest.Read(path));
            File.Delete(path); Package(duplicateManifest: true);
            Reject("case-colliding manifests are ambiguous", () => DcaPackageManifest.Read(path));
        }
        finally { Directory.Delete(directory, true); }
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Knx.Catalog;

/// <summary>The three fields used by ETS DCA compatibility, independent of fingerprint and original manufacturer.</summary>
public readonly record struct DcaApplicationIdentity(ushort ManufacturerId, ushort ProgramType, byte ProgramVersion)
{
    private static readonly Regex Pattern = new(@"\AM-([0-9A-F]{4})_A-([0-9A-F]{4})-([0-9A-F]{2})-[0-9A-F]{4}(?:-O[0-9A-F]{4})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool TryParse(string? id, out DcaApplicationIdentity identity)
    {
        identity = default;
        if (id is null || id.Length > 64) return false;
        var match = Pattern.Match(id);
        if (!match.Success) return false;
        identity = new(ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            ushort.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(match.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        return true;
    }

    public override string ToString() => $"M-{ManufacturerId:X4}_A-{ProgramType:X4}-{ProgramVersion:X2}";
}

/// <summary>Static declared compatibility of an ETS app package. Reading never installs or executes its code.</summary>
public sealed class DcaPackageManifest
{
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxPackageBytes = 128 * 1024 * 1024;
    public string AppId { get; }
    public string AssemblyEntry { get; }
    public IReadOnlyDictionary<string, string> DisplayNames { get; }
    public IReadOnlyList<string> DeclaredApplicationIds { get; }
    public IReadOnlyList<DcaApplicationIdentity> Applications { get; }
    /// <summary>Identity of the inspected bytes; this does not verify the publisher's signature.</summary>
    public string? PackageSha256 { get; }
    public string? AssemblySha256 { get; }

    private DcaPackageManifest(string appId, string assembly, Dictionary<string, string> names,
        string[] declared, string? packageHash = null, string? assemblyHash = null)
    {
        AppId = appId; AssemblyEntry = assembly;
        DisplayNames = new ReadOnlyDictionary<string, string>(names);
        DeclaredApplicationIds = Array.AsReadOnly(declared);
        Applications = Array.AsReadOnly(declared.Select(id => { DcaApplicationIdentity.TryParse(id, out var value); return value; }).Distinct().ToArray());
        PackageSha256 = packageHash; AssemblySha256 = assemblyHash;
    }

    public bool DeclaresSupportFor(string applicationId) => DcaApplicationIdentity.TryParse(applicationId, out var identity)
        && Applications.Contains(identity);

    /// <summary>Reads bounded metadata and byte hashes from an unencrypted package without extracting files.</summary>
    public static DcaPackageManifest Read(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxPackageBytes) throw new InvalidDataException("ETS app package exceeds the 128 MiB limit.");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("ETS app package changed while reading.");
        var entries = EncryptedArchive.ReadAll(bytes, null);
        var manifests = entries.Where(e => e.Key.Equals("AddInManifest.xml", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("Exactly one AddInManifest.xml is required.");
        var parsed = Parse(manifests[0].Value);
        var assemblies = entries.Where(e => e.Key.Replace('\\', '/').Equals(parsed.AssemblyEntry, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (assemblies.Length != 1) throw new InvalidDataException("Declared ETS app assembly is missing or ambiguous.");
        return new(parsed.AppId, parsed.AssemblyEntry, new(parsed.DisplayNames, StringComparer.OrdinalIgnoreCase),
            parsed.DeclaredApplicationIds.ToArray(), Hash(bytes), Hash(assemblies[0].Value));
    }

    public static DcaPackageManifest Parse(byte[] manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Length > MaxManifestBytes) throw new InvalidDataException("ETS app manifest exceeds the 1 MiB limit.");
        try
        {
            using var stream = new MemoryStream(manifest, false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxManifestBytes });
            var document = XDocument.Load(reader);
            var root = document.Root;
            if (root?.Name != "Knxapp") throw new InvalidDataException("Unsupported ETS app manifest root.");
            var appId = (string?)root.Attribute("AppId") ?? "";
            if (!Regex.IsMatch(appId, @"\AM[0-9A-F]{4}-A[0-9A-F]{4}\z", RegexOptions.NonBacktracking))
                throw new InvalidDataException("Invalid ETS app identity.");
            var assembly = Member((string?)root.Attribute("AddInAssembly") ?? "");
            var containers = root.Elements("DeviceConfiguration").ToArray();
            if (containers.Length > 1) throw new InvalidDataException("Ambiguous device-configuration declarations.");
            var declarations = containers.SelectMany(c => c.Elements()).ToArray();
            if (declarations.Length > 512 || declarations.Any(e => e.Name != "ApplicationProgram"))
                throw new InvalidDataException("Unsupported device-configuration declaration.");
            var ids = declarations.Select(e => (string?)e.Attribute("Id") ?? "").Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Any(id => !DcaApplicationIdentity.TryParse(id, out _))) throw new InvalidDataException("Invalid declared application identity.");
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in root.Elements("DisplayNames").Elements("DisplayName"))
            {
                var language = (string?)name.Attribute("LanguageId") ?? ""; var value = (string?)name.Attribute("Name") ?? "";
                if (language.Length is < 1 or > 64 || value.Length > 1024 || names.Count >= 256 || !names.TryAdd(language, value))
                    throw new InvalidDataException("Invalid or ambiguous ETS app display names.");
            }
            return new(appId, assembly, names, ids);
        }
        catch (XmlException ex) { throw new InvalidDataException("Invalid ETS app manifest XML.", ex); }
    }

    private static string Member(string entry)
    {
        var normalized = entry.Replace('\\', '/');
        if (normalized.Length is < 1 or > 1024 || normalized.Split('/').Any(p => p is "" or "." or ".."
            || p.Any(c => char.IsControl(c) || c == ':'))) throw new InvalidDataException("Invalid ETS app member path.");
        return normalized;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

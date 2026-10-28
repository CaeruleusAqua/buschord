using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Knx.App.Services;

public sealed partial class MainWindowViewModel
{
    private const uint ArchiveMaskProfileVersion = 0;
    private Project? _maskProfileProject;
    private long _maskProfileRevision = -1;
    private bool _maskMasterRead, _maskMasterUnreadable;
    private XDocument? _maskMaster;
    private readonly Dictionary<(string? MaskId, uint Version), ParameterProfileContext> _maskProfiles = [];

    private void ClearArchiveMaskProfiles()
    {
        _maskProfiles.Clear(); _maskProfileProject = null; _maskProfileRevision = -1;
        _maskMaster = null; _maskMasterRead = _maskMasterUnreadable = false;
    }

    /// <summary>Current0 metadata from this immutable archive only; never installed/catalogue fallback or a password prompt.</summary>
    private ParameterProfileContext ArchiveMaskProfile(Project project, XDocument application)
    {
        var programs = application.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").Take(2).ToArray();
        var maskId = programs.Length == 1 ? (string?)programs[0].Attribute("MaskVersion") : null;
        ParameterProfileContext ForApplication(ParameterProfileContext context) => programs.Length == 0 ? context
            : context.ForApplication(maskId, programs.Length == 1 ? programs[0].Name.NamespaceName : null, programs.Length > 1);
        var revision = project.ApplicationDataRevision;
        if (!ReferenceEquals(project, _maskProfileProject) || revision != _maskProfileRevision)
        {
            ClearArchiveMaskProfiles(); _maskProfileProject = project; _maskProfileRevision = revision;
        }
        // Rejected references must neither retain a potentially large string as a cache key
        // nor cause optional master I/O. Let the Core factory own their diagnostic status.
        if (maskId is { Length: > 256 }) return ForApplication(ParameterProfileContext.Read(null, maskId));
        var key = (maskId, ArchiveMaskProfileVersion);
        if (_maskProfiles.TryGetValue(key, out var cached)) return ForApplication(cached);
        if (!_maskMasterRead)
        {
            try { _maskMaster = Knxproj.LoadMasterData(project); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException or FormatException or NotSupportedException)
            { _maskMasterUnreadable = true; }
            _maskMasterRead = true;
        }
        var profile = _maskMasterUnreadable ? ParameterProfileContext.Unavailable(maskId) : ParameterProfileContext.Read(_maskMaster, maskId);
        _maskProfiles.Add(key, profile);
        return ForApplication(profile);
    }

    private string ParameterDefinitionFingerprint(Project project, XDocument application)
    {
        using var hash = SHA256.Create();
        using var output = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write, leaveOpen: true);
        output.Write("BusChord.ParameterDefinition.v2\n"u8);
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), OmitXmlDeclaration = true, Indent = false,
            NewLineHandling = NewLineHandling.None, CloseOutput = false,
        })) application.WriteTo(writer);
        output.Write("\nArchiveMaskProfile:"u8);
        output.Write(Encoding.UTF8.GetBytes(ArchiveMaskProfile(project, application).Selection.Fingerprint));
        output.FlushFinalBlock();
        return Convert.ToHexString(hash.Hash!);
    }
}

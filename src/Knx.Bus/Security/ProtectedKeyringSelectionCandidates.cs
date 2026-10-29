using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using Knx.Bus.Connections;

namespace Knx.Bus.Security;

/// <summary>Unverified public selectors from explicitly chosen source bytes. These never authorize a connection or save.</summary>
public sealed record UnverifiedKeyringSelection(string ProjectLabel, string SourceFingerprint, IReadOnlyList<ProtectedTunnelChoice> Interfaces);

/// <summary>Bounded public metadata discovery only. Password/signature verification remains mandatory through the importer.</summary>
public static class ProtectedKeyringSelectionCandidates
{
    public static async Task<UnverifiedKeyringSelection> ReadAsync(string path, string expectedProjectLabel, CancellationToken ct = default)
    {
        byte[] xml = [];
        try
        {
            xml = await SecureLocalFiles.ReadPrivateAsync(path, ProtectedKnxKeyringImporter.MaximumXmlBytes, ct).ConfigureAwait(false);
            using var input = new MemoryStream(xml, false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = ProtectedKnxKeyringImporter.MaximumXmlBytes, IgnoreWhitespace = true, IgnoreComments = true });
            var choices = new List<ProtectedTunnelChoice>(); var slots = new HashSet<(ushort, byte)>(); var rootSeen = false; var elements = 0;
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.Depth > 16 || ++elements > 30000) throw Rejected();
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.NamespaceURI != ProtectedKnxKeyringImporter.XmlNamespace || reader.Prefix.Length != 0) throw Rejected();
                if (reader.Depth == 0)
                {
                    if (rootSeen || reader.LocalName != "Keyring" || reader.GetAttribute("Project") != expectedProjectLabel) throw Rejected();
                    rootSeen = true; continue;
                }
                if (reader.Depth != 1 || reader.LocalName != "Interface" || reader.GetAttribute("Type") != "Tunneling") continue;
                // As in the verified profile reader, entries without a complete Secure credential tuple are not choices.
                if (string.IsNullOrEmpty(reader.GetAttribute("Host")) || string.IsNullOrEmpty(reader.GetAttribute("UserID"))
                    || string.IsNullOrEmpty(reader.GetAttribute("Password")) || string.IsNullOrEmpty(reader.GetAttribute("Authentication"))) continue;
                if (!IndividualAddress.TryParse(reader.GetAttribute("Host") ?? "", out var host)
                    || !IndividualAddress.TryParse(reader.GetAttribute("IndividualAddress") ?? "", out var tunnel)
                    || !byte.TryParse(reader.GetAttribute("UserID"), NumberStyles.None, CultureInfo.InvariantCulture, out var user) || user is < 1 or > 127
                    || !slots.Add((host.Raw, user)) || choices.Count >= 1024) throw Rejected();
                choices.Add(new(host, user, tunnel));
            }
            if (!rootSeen) throw Rejected();
            return new(expectedProjectLabel, Convert.ToHexString(SHA256.HashData(xml)), choices.AsReadOnly());
        }
        catch (OperationCanceledException) { throw; }
        catch (KnxConnectionException) { throw; }
        catch (XmlException) { throw Rejected(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
        finally { CryptographicOperations.ZeroMemory(xml); }
    }
    public static async Task VerifyCurrentSourceAsync(SecureKeyringReference reference, CancellationToken ct = default)
    {
        byte[] xml = [];
        try
        {
            xml = await SecureLocalFiles.ReadPrivateAsync(reference.Path, ProtectedKnxKeyringImporter.MaximumXmlBytes, ct).ConfigureAwait(false);
            if (reference.ExpectedSourceFingerprint is null || Convert.ToHexString(SHA256.HashData(xml)) != reference.ExpectedSourceFingerprint)
                throw new KnxConnectionException(KnxConnectionFailure.CredentialBindingMismatch);
        }
        finally { CryptographicOperations.ZeroMemory(xml); }
    }
    private static KnxConnectionException Rejected() => new(KnxConnectionFailure.KeyringRejected);
}

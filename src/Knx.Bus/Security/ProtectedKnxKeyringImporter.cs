using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Knx.Bus.Security;

/// <summary>
/// Password-verified ETS project exports, namespace /keyring/1: Backbone, Tunneling/USB/Backbone
/// interfaces, assigned groups/senders, GroupAddresses and Devices. Certificate-only/FDSK backups,
/// extensions, missing project labels and unsigned/cleartext formats are deliberately unsupported.
/// KNX's public contract specifies PBKDF2-HMAC-SHA256/65536/128 bits with salt
/// "1.keyring.ets.knx.org", canonical signature and AES-CBC fields:
/// https://support.knx.org/hc/en-us/articles/18968368409874-Keyring-File-Format
/// https://support.knx.org/hc/en-us/articles/360001582259-Use-keyring-outside-ETS-Falcon-SDK
/// UTF-8 strings and one-octet UTF-8 byte lengths follow the public XKNX reference (commit
/// 82e1a851ad9e31669bd8b7dbed59e6066bb7a9f7, xknx/secure/keyring.py); Calimero Keyring
/// eccdabc72b37203cfec66313432e750a24384178 independently confirms the ASCII-password contract.
/// Unsupported derivation profiles are refused.
/// </summary>
public static class ProtectedKnxKeyringImporter
{
    public const string XmlNamespace = "http://knx.org/xml/keyring/1";
    public const int MaximumXmlBytes = 1_048_576;
    public const int MaximumElements = 8192;
    public const int MaximumPasswordUtf8Bytes = 1024;
    private const ulong MaximumSequence = 0xFFFFFFFFFFFF;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// Imports an owned snapshot. expectedProjectName is mandatory and compared ordinally against
    /// the signed Project label; it is not a unique project ID. Password and input remain caller-owned.
    /// All fields are verified and decrypted atomically before an imported keyring is returned.
    /// </summary>
    public static ProtectedKnxKeyring Import(ReadOnlySpan<byte> xml, ReadOnlySpan<char> password, string expectedProjectName)
    {
        if (xml.Length is 0 or > MaximumXmlBytes) throw Failure(ProtectedKeyringFailure.LimitExceeded);
        if (string.IsNullOrWhiteSpace(expectedProjectName) || Utf8.GetByteCount(expectedProjectName) > 255)
            throw new ArgumentException("An explicit expected project label is required.", nameof(expectedProjectName));
        int passwordBytes;
        try { passwordBytes = Utf8.GetByteCount(password); }
        catch (EncoderFallbackException) { throw Failure(ProtectedKeyringFailure.InvalidFormat); }
        if (passwordBytes is 0 or > MaximumPasswordUtf8Bytes) throw Failure(ProtectedKeyringFailure.LimitExceeded);

        var snapshot = xml.ToArray();
        var encodedPassword = new byte[passwordBytes];
        Span<byte> key = stackalloc byte[16];
        Span<byte> iv = stackalloc byte[32];
        var secrets = new ProtectedKeyringSecrets();
        try
        {
            var root = ReadBoundedDocument(snapshot);
            ValidateShape(root);
            Utf8.GetBytes(password, encodedPassword);
            Rfc2898DeriveBytes.Pbkdf2(encodedPassword, "1.keyring.ets.knx.org"u8, key, 65_536, HashAlgorithmName.SHA256);
            VerifySignature(root, key);
            var project = Required(root, "Project");
            if (!string.Equals(project, expectedProjectName, StringComparison.Ordinal)) throw Failure(ProtectedKeyringFailure.ProjectMismatch);
            var created = Required(root, "Created");
            if (!DateTime.TryParseExact(created, ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)) throw Failure(ProtectedKeyringFailure.InvalidFormat);
            var createdBytes = Utf8.GetBytes(created);
            SHA256.HashData(createdBytes, iv);
            CryptographicOperations.ZeroMemory(createdBytes);

            var interfaces = new Dictionary<IndividualAddress, ProtectedKeyringInterface>();
            var devices = new Dictionary<IndividualAddress, ProtectedKeyringDevice>();
            var groups = new Dictionary<GroupAddress, ProtectedKeyringSecret>();
            ProtectedKeyringBackbone? backbone = null;
            foreach (var child in root.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "Backbone":
                        var multicastText = Required(child, "MulticastAddress");
                        if (!IPAddress.TryParse(multicastText, out var multicast) || multicast.AddressFamily != AddressFamily.InterNetwork
                            || multicast.GetAddressBytes()[0] is < 224 or > 239) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                        backbone = new(multicastText, OptionalUshort(child, "Latency"), DecryptOptional(child, "Key", false, key, iv[..16], secrets));
                        break;
                    case "Interface":
                        var entry = ReadInterface(child, key, iv[..16], secrets);
                        if (!interfaces.TryAdd(entry.Address, entry)) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                        break;
                    case "GroupAddresses":
                        foreach (var group in child.Elements())
                        {
                            var address = Group(Required(group, "Address"));
                            if (groups.ContainsKey(address)) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                            groups.Add(address, DecryptRequired(group, "Key", false, key, iv[..16], secrets));
                        }
                        break;
                    case "Devices":
                        foreach (var device in child.Elements())
                        {
                            var address = Individual(Required(device, "IndividualAddress"));
                            if (devices.ContainsKey(address)) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                            var sequence = OptionalSequence(device);
                            devices.Add(address, new(address, DecryptOptional(device, "ToolKey", false, key, iv[..16], secrets),
                                DecryptOptional(device, "ManagementPassword", true, key, iv[..16], secrets),
                                DecryptOptional(device, "Authentication", true, key, iv[..16], secrets), sequence));
                        }
                        break;
                }
            }
            if (interfaces.Values.SelectMany(entry => entry.Groups.Keys).Any(group => !groups.ContainsKey(group)))
                throw Failure(ProtectedKeyringFailure.InvalidFormat);
            var slots = new HashSet<(IndividualAddress, byte)>();
            foreach (var entry in interfaces.Values)
                if (entry.Host is { } host && entry.UserId is { } user && !slots.Add((host, user)))
                    throw Failure(ProtectedKeyringFailure.InvalidFormat);
            return new(secrets, project, created, Optional(root, "CreatedBy"), Convert.ToHexString(SHA256.HashData(snapshot)),
                backbone, interfaces, devices, groups);
        }
        catch (ProtectedKeyringImportException) { secrets.Dispose(); throw; }
        catch (Exception ex) when (ex is XmlException or ArgumentException or CryptographicException or OverflowException)
        { secrets.Dispose(); throw Failure(ProtectedKeyringFailure.InvalidFormat); }
        catch { secrets.Dispose(); throw; }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(iv);
            CryptographicOperations.ZeroMemory(encodedPassword); CryptographicOperations.ZeroMemory(snapshot);
        }
    }

    /// <summary>Reads at most MaximumXmlBytes plus one sentinel byte; leaves the supplied stream open.</summary>
    public static ProtectedKnxKeyring Import(Stream input, ReadOnlySpan<char> password, string expectedProjectName)
    {
        ArgumentNullException.ThrowIfNull(input);
        var buffer = new byte[MaximumXmlBytes + 1];
        try
        {
            var length = 0;
            while (length < buffer.Length)
            {
                var read = input.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > MaximumXmlBytes) throw Failure(ProtectedKeyringFailure.LimitExceeded);
            return Import(buffer.AsSpan(0, length), password, expectedProjectName);
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static XElement ReadBoundedDocument(byte[] xml)
    {
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumXmlBytes,
            IgnoreComments = true, IgnoreWhitespace = true, CloseInput = false, ConformanceLevel = ConformanceLevel.Document,
        });
        var stack = new Stack<XElement>(); XElement? root = null; var elements = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    if (++elements > MaximumElements || reader.Depth > 3 || reader.AttributeCount > 16) throw Failure(ProtectedKeyringFailure.LimitExceeded);
                    if (reader.Prefix.Length != 0 || reader.NamespaceURI != XmlNamespace) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                    var element = new XElement(XName.Get(reader.LocalName, XmlNamespace));
                    if (reader.MoveToFirstAttribute())
                    {
                        do
                        {
                            if (reader.Name == "xmlns")
                            {
                                if (reader.Depth != 1 || reader.Value != XmlNamespace) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                                continue;
                            }
                            if (reader.Prefix.Length != 0 || reader.NamespaceURI.Length != 0) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                            if (Utf8.GetByteCount(reader.Value) > 255) throw Failure(ProtectedKeyringFailure.LimitExceeded);
                            element.Add(new XAttribute(reader.LocalName, reader.Value));
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }
                    if (stack.TryPeek(out var parent)) parent.Add(element);
                    else if (root is null) root = element;
                    else throw Failure(ProtectedKeyringFailure.InvalidFormat);
                    if (!reader.IsEmptyElement) stack.Push(element);
                    break;
                case XmlNodeType.EndElement: stack.Pop(); break;
                case XmlNodeType.XmlDeclaration:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace: break;
                default: throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
            }
        }
        return root ?? throw Failure(ProtectedKeyringFailure.InvalidFormat);
    }

    private static void ValidateShape(XElement root)
    {
        if (root.Name.LocalName != "Keyring") throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
        Attributes(root, ["Project", "Created", "Signature", "CreatedBy"], ["Project", "Created", "Signature"]);
        if (string.IsNullOrWhiteSpace(Required(root, "Project"))) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
        foreach (var name in new[] { "Backbone", "GroupAddresses", "Devices" })
            if (root.Elements().Count(element => element.Name.LocalName == name) > 1) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        foreach (var element in root.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "Backbone":
                    Attributes(element, ["MulticastAddress", "Latency", "Key"], ["MulticastAddress"]); Leaf(element); break;
                case "Interface":
                    Attributes(element, ["Type", "IndividualAddress", "Host", "UserID", "Password", "Authentication"], ["Type", "IndividualAddress"]);
                    foreach (var group in element.Elements())
                    {
                        if (group.Name.LocalName != "Group") throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                        Attributes(group, ["Address", "Senders"], ["Address"]); Leaf(group);
                    }
                    break;
                case "GroupAddresses":
                    Attributes(element, [], []);
                    foreach (var group in element.Elements())
                    {
                        if (group.Name.LocalName != "Group") throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                        Attributes(group, ["Address", "Key"], ["Address", "Key"]); Leaf(group);
                    }
                    break;
                case "Devices":
                    Attributes(element, [], []);
                    foreach (var device in element.Elements())
                    {
                        if (device.Name.LocalName != "Device") throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
                        Attributes(device, ["IndividualAddress", "ToolKey", "ManagementPassword", "Authentication", "SequenceNumber"], ["IndividualAddress"]); Leaf(device);
                    }
                    break;
                default: throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
            }
        }
    }

    private static void VerifySignature(XElement root, ReadOnlySpan<byte> key)
    {
        var supplied = Base64(Required(root, "Signature"), 16);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Canonical(root, hash);
        Span<char> base64 = stackalloc char[24];
        Span<byte> encoded = stackalloc byte[24];
        Span<byte> signature = stackalloc byte[32];
        try
        {
            if (!Convert.TryToBase64Chars(key, base64, out var length) || length != 24) throw Failure(ProtectedKeyringFailure.InvalidFormat);
            for (var i = 0; i < length; i++) encoded[i] = (byte)base64[i];
            hash.AppendData([24]); hash.AppendData(encoded);
            hash.GetHashAndReset(signature);
            if (!CryptographicOperations.FixedTimeEquals(signature[..16], supplied)) throw Failure(ProtectedKeyringFailure.SignatureMismatch);
        }
        finally { base64.Clear(); CryptographicOperations.ZeroMemory(encoded); CryptographicOperations.ZeroMemory(signature); CryptographicOperations.ZeroMemory(supplied); }
    }
    private static void Canonical(XElement element, IncrementalHash hash)
    {
        hash.AppendData([1]); AppendString(hash, element.Name.LocalName);
        foreach (var attribute in element.Attributes().Where(attribute => attribute.Name.LocalName != "Signature").OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
        { AppendString(hash, attribute.Name.LocalName); AppendString(hash, attribute.Value); }
        foreach (var child in element.Elements()) Canonical(child, hash);
        hash.AppendData([2]);
    }
    private static void AppendString(IncrementalHash hash, string value)
    {
        var length = Utf8.GetByteCount(value);
        if (length > 255) throw Failure(ProtectedKeyringFailure.LimitExceeded);
        Span<byte> encoded = stackalloc byte[256]; encoded[0] = (byte)length;
        Utf8.GetBytes(value, encoded[1..]); hash.AppendData(encoded[..(length + 1)]);
    }

    private static ProtectedKeyringInterface ReadInterface(XElement element, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ProtectedKeyringSecrets secrets)
    {
        var type = Required(element, "Type") switch
        {
            "Tunneling" => ProtectedKeyringInterfaceType.Tunneling, "USB" => ProtectedKeyringInterfaceType.Usb,
            "Backbone" => ProtectedKeyringInterfaceType.Backbone, _ => throw Failure(ProtectedKeyringFailure.UnsupportedProfile),
        };
        var address = Individual(Required(element, "IndividualAddress"));
        IndividualAddress? host = Optional(element, "Host") is { } hostText ? Individual(hostText) : null;
        byte? user = Optional(element, "UserID") is { } userText ? (byte)Number(userText, 1, 127) : null;
        var hasCredentials = Optional(element, "Password") is not null || Optional(element, "Authentication") is not null || user is not null;
        // Real public ETS exports can carry only Authentication on a scope-only tunnel.
        // Preserve optional fields independently; SelectTunneling requires a complete exact tuple.
        if (hasCredentials && type != ProtectedKeyringInterfaceType.Tunneling) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
        var groups = new Dictionary<GroupAddress, IReadOnlyList<IndividualAddress>>();
        foreach (var elementGroup in element.Elements())
        {
            var group = Group(Required(elementGroup, "Address"));
            var senders = (Optional(elementGroup, "Senders") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Individual).ToArray();
            if (senders.Distinct().Count() != senders.Length || !groups.TryAdd(group, Array.AsReadOnly(senders))) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        }
        return new(type, address, host, user, DecryptOptional(element, "Password", true, key, iv, secrets),
            DecryptOptional(element, "Authentication", true, key, iv, secrets), groups);
    }

    private static ProtectedKeyringSecret? DecryptOptional(XElement element, string name, bool password, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ProtectedKeyringSecrets secrets)
        => Optional(element, name) is null ? null : DecryptRequired(element, name, password, key, iv, secrets);
    private static ProtectedKeyringSecret DecryptRequired(XElement element, string name, bool password, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, ProtectedKeyringSecrets secrets)
    {
        var encrypted = Base64(Required(element, name), password ? 32 : 16);
        byte[]? plain = null; byte[]? extracted = null;
        var aesKey = key.ToArray();
        try
        {
            using var aes = Aes.Create(); aes.Key = aesKey;
            plain = aes.DecryptCbc(encrypted, iv, PaddingMode.None);
            if (password)
            {
                var padding = plain[^1];
                if (padding is 0 or > 24) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                var paddingStart = plain.Length - padding;
                for (var i = paddingStart; i < plain.Length; i++)
                    if (plain[i] != padding) throw Failure(ProtectedKeyringFailure.InvalidFormat);
                var bytes = plain.AsSpan(8, paddingStart - 8);
                Utf8.GetCharCount(bytes); // strict validation without creating an immutable plaintext string
                extracted = bytes.ToArray();
            }
            else { extracted = plain; plain = null; }
            var secret = secrets.Adopt(extracted, password ? ProtectedKeyringSecretKind.Utf8Password : ProtectedKeyringSecretKind.Aes128Key);
            extracted = null;
            return secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted);
            CryptographicOperations.ZeroMemory(aesKey);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
            if (extracted is not null) CryptographicOperations.ZeroMemory(extracted);
        }
    }

    private static byte[] Base64(string text, int length)
    {
        if (text.Length != ((length + 2) / 3) * 4) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        var bytes = new byte[length];
        if (!Convert.TryFromBase64String(text, bytes, out var written) || written != length || Convert.ToBase64String(bytes) != text)
        { CryptographicOperations.ZeroMemory(bytes); throw Failure(ProtectedKeyringFailure.InvalidFormat); }
        return bytes;
    }
    private static IndividualAddress Individual(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 3) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        var raw = (ushort)((Number(parts[0], 0, 15) << 12) | (Number(parts[1], 0, 15) << 8) | Number(parts[2], 0, 255));
        if (raw == 0 || (raw & 255) == 255) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
        return new(raw);
    }
    private static GroupAddress Group(string value) => new((ushort)Number(value, 1, ushort.MaxValue));
    private static ushort? OptionalUshort(XElement element, string name) => Optional(element, name) is { } value ? (ushort)Number(value, 0, ushort.MaxValue) : null;
    private static ulong? OptionalSequence(XElement element)
    {
        if (Optional(element, "SequenceNumber") is not { } value) return null;
        if (value.Length == 0 || value.Any(character => character is < '0' or > '9')
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence > MaximumSequence) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        return sequence;
    }
    private static int Number(string value, int minimum, int maximum)
    {
        if (value.Length == 0 || value.Any(character => character is < '0' or > '9')
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum) throw Failure(ProtectedKeyringFailure.InvalidFormat);
        return number;
    }
    private static string? Optional(XElement element, string name) => element.Attribute(name)?.Value;
    private static string Required(XElement element, string name) => Optional(element, name) ?? throw Failure(ProtectedKeyringFailure.InvalidFormat);
    private static void Attributes(XElement element, string[] allowed, string[] required)
    {
        if (element.Attributes().Any(attribute => !allowed.Contains(attribute.Name.LocalName, StringComparer.Ordinal))) throw Failure(ProtectedKeyringFailure.UnsupportedProfile);
        foreach (var name in required) _ = Required(element, name);
    }
    private static void Leaf(XElement element) { if (element.HasElements) throw Failure(ProtectedKeyringFailure.UnsupportedProfile); }
    private static ProtectedKeyringImportException Failure(ProtectedKeyringFailure failure) => new(failure, failure switch
    {
        ProtectedKeyringFailure.SignatureMismatch => "Keyring password verification or file integrity verification failed.",
        ProtectedKeyringFailure.ProjectMismatch => "The verified keyring does not match the explicit project label.",
        ProtectedKeyringFailure.LimitExceeded => "The protected keyring exceeds an import limit.",
        ProtectedKeyringFailure.UnsupportedProfile => "The keyring uses an unsupported profile or ambiguous scope.",
        _ => "The protected keyring is malformed.",
    });
}

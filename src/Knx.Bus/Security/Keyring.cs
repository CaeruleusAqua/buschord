using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Knx.Bus.Security;

/// <summary>Key type of a KNX Data Secure key (group key or device/individual key).</summary>
public enum KeyType
{
    Group,
    Individual,
}

/// <summary>Transport of a KNX IP Secure interface entry.</summary>
public enum InterfaceType
{
    Multicast,
    Tunneling,
}

/// <summary>Inclusive address range a key is valid for (raw 16-bit KNX address).</summary>
public sealed class KeyAddressRange
{
    public KeyAddressRange() { }

    public KeyAddressRange(ushort from, ushort to)
    {
        From = from;
        To = to;
    }

    public ushort From { get; set; }
    public ushort To { get; set; }

    public bool Contains(ushort raw) => raw >= From && raw <= To;
}

/// <summary>A single KNX Data Secure key as stored in a keyring.</summary>
public sealed class SecureKey
{
    /// <summary>Highest sequence number; KNX Data Secure uses a 48-bit counter.</summary>
    public const ulong MaxSequenceNumber = (1UL << 48) - 1;

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public KeyType Type { get; set; }

    /// <summary>16-byte AES-128 user key.</summary>
    public byte[] Key { get; set; } = new byte[16];

    /// <summary>16-byte device authentication code (salt of the key derivation).</summary>
    public byte[]? DeviceAuthenticationCode { get; set; }

    /// <summary>16-byte tool key (secure management of a device); individual keys only.</summary>
    public byte[]? ToolKey { get; set; }

    public KeyAddressRange? GroupAddressRange { get; set; }
    public KeyAddressRange? IndividualAddressRange { get; set; }

    /// <summary>Last used 48-bit secure sequence number.</summary>
    public ulong SequenceNumber { get; set; }

    /// <summary>Expected latency of the secured medium in milliseconds.</summary>
    public int Latency { get; set; } = 1000;

    /// <summary>Returns the next sequence number and stores it (monotone, wraps at 48 bits).</summary>
    public ulong NextSequenceNumber()
    {
        SequenceNumber = SequenceNumber >= MaxSequenceNumber ? 0 : SequenceNumber + 1;
        return SequenceNumber;
    }
}

/// <summary>Interface entry of a keyring (backbone multicast or tunnelling interface).</summary>
public sealed class InterfaceKey
{
    public InterfaceType Type { get; set; }
    public string? Host { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }

    /// <summary>16-byte user key derived from password + device authentication code.</summary>
    public byte[]? Key { get; set; }

    public byte[]? DeviceAuthenticationCode { get; set; }
}

/// <summary>
/// Keyring for KNX Data Secure and KNX IP Secure. Supports the BusChord JSON format
/// and the XML elements handled by <see cref="FromXml"/>.
/// </summary>
public sealed class Keyring
{
    public int FormatVersion { get; set; } = 1;
    public string? ProjectName { get; set; }

    /// <summary>16-byte KNX IP Secure backbone key (multicast routing).</summary>
    public byte[]? BackboneKey { get; set; }

    public string? MulticastAddress { get; set; } = "224.0.23.12";
    public int Latency { get; set; } = 1000;

    public List<InterfaceKey> Interfaces { get; set; } = [];
    public List<SecureKey> Keys { get; set; } = [];

    private static readonly JsonSerializerOptions IndentedOptions = CreateOptions(true);
    private static readonly JsonSerializerOptions CompactOptions = CreateOptions(false);

    private static JsonSerializerOptions CreateOptions(bool indented) => new()
    {
        WriteIndented = indented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(), new HexBytesConverter() },
    };

    public string ToJson(bool indented = true)
        => JsonSerializer.Serialize(this, indented ? IndentedOptions : CompactOptions);

    public static Keyring FromJson(string json)
        => JsonSerializer.Deserialize<Keyring>(json, IndentedOptions)
           ?? throw new FormatException("keyring JSON is empty");

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public static Keyring Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>First key whose group address range contains the address.</summary>
    public SecureKey? FindGroupKey(GroupAddress address) =>
        Keys.FirstOrDefault(k => k.Type == KeyType.Group && k.GroupAddressRange?.Contains(address.Raw) == true);

    /// <summary>First key whose individual address range contains the address.</summary>
    public SecureKey? FindIndividualKey(IndividualAddress address) =>
        Keys.FirstOrDefault(k => k.Type == KeyType.Individual
            && k.IndividualAddressRange?.Contains(address.Raw) == true);

    public SecureKey? FindKey(string idOrName) =>
        Keys.FirstOrDefault(k => k.Id == idOrName || k.Name == idOrName);

    // ---- Keyring XML -----------------------------------------------------

    /// <summary>Reads a keyring XML document. ZIP containers and signature verification are not supported.</summary>
    public static Keyring FromXml(string xml)
    {
        var root = XDocument.Parse(xml).Root
            ?? throw new FormatException("keyring XML without root element");
        if (!root.Name.LocalName.Equals("Keyring", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"unexpected keyring root element '{root.Name.LocalName}'");

        var ring = new Keyring { ProjectName = Attr(root, "ProjectId") ?? Attr(root, "Name") };

        foreach (var backbone in Children(root, "Backbone").Take(1))
        {
            ring.MulticastAddress = Attr(backbone, "MulticastAddress") ?? ring.MulticastAddress;
            ring.Latency = ParseInt(Attr(backbone, "Latency")) ?? ring.Latency;
            ring.BackboneKey = ParseKey(Attr(backbone, "Key"));

            foreach (var iface in Children(backbone, "Interface"))
            {
                ring.Interfaces.Add(new InterfaceKey
                {
                    Type = string.Equals(Attr(iface, "Type"), "Tunneling", StringComparison.OrdinalIgnoreCase)
                        ? InterfaceType.Tunneling
                        : InterfaceType.Multicast,
                    Host = Attr(iface, "Host"),
                    User = Attr(iface, "User"),
                    Password = Attr(iface, "Password"),
                    Key = ParseKey(Attr(iface, "Key")),
                    DeviceAuthenticationCode = ParseKey(Attr(iface, "Authentication")),
                });
            }

            foreach (var device in Children(backbone, "Devices").SelectMany(d => Children(d, "Device")))
            {
                var address = ParseAddress(Attr(device, "IndividualAddress"), individual: true);
                if (address is null) continue;
                ring.Keys.Add(new SecureKey
                {
                    Id = Attr(device, "Id") ?? Attr(device, "IndividualAddress") ?? "",
                    Name = Attr(device, "Name") ?? Attr(device, "IndividualAddress") ?? "",
                    Type = KeyType.Individual,
                    Key = ParseKey(Attr(device, "Key")) ?? new byte[16],
                    ToolKey = ParseKey(Attr(device, "ToolKey")),
                    DeviceAuthenticationCode = ParseKey(Attr(device, "Authentication")),
                    IndividualAddressRange = new KeyAddressRange(address.Value, address.Value),
                    SequenceNumber = ParseSequence(Attr(device, "SequenceNumber")),
                    Latency = ParseInt(Attr(device, "Latency")) ?? ring.Latency,
                });
            }
        }

        foreach (var keys in Children(root, "Keys"))
        {
            foreach (var group in Children(keys, "Group"))
                AddXmlKey(ring, group, KeyType.Group);
            foreach (var individual in Children(keys, "Individual"))
                AddXmlKey(ring, individual, KeyType.Individual);
        }

        return ring;
    }

    private static void AddXmlKey(Keyring ring, XElement element, KeyType type)
    {
        var text = Attr(element, "GroupAddressRange") ?? Attr(element, "Range") ?? Attr(element, "Address");
        var range = ParseRange(text, type == KeyType.Individual);
        ring.Keys.Add(new SecureKey
        {
            Id = Attr(element, "Id") ?? Attr(element, "Name") ?? "",
            Name = Attr(element, "Name") ?? "",
            Type = type,
            Key = ParseKey(Attr(element, "Key")) ?? new byte[16],
            ToolKey = ParseKey(Attr(element, "ToolKey")),
            DeviceAuthenticationCode = ParseKey(Attr(element, "Authentication")),
            GroupAddressRange = type == KeyType.Group ? range : null,
            IndividualAddressRange = type == KeyType.Individual ? range : null,
            SequenceNumber = ParseSequence(Attr(element, "SequenceNumber")),
            Latency = ParseInt(Attr(element, "Latency")) ?? ring.Latency,
        });
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static string? Attr(XElement element, string name) =>
        element.Attributes()
            .FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>Parses a 32 hex digit key; other encodings yield null.</summary>
    private static byte[]? ParseKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length == 32 && trimmed.All(Uri.IsHexDigit) ? Convert.FromHexString(trimmed) : null;
    }

    /// <summary>Parses 12 hex digits as a 48-bit sequence number; plain integers are decimal.</summary>
    private static ulong ParseSequence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var trimmed = text.Trim();
        return trimmed.Length == 12
            ? ulong.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var hex) ? hex : 0
            : ulong.TryParse(trimmed, out var dec) ? dec : 0;
    }

    private static int? ParseInt(string? text) => int.TryParse(text, out var value) ? value : null;

    private static ushort? ParseAddress(string? text, bool individual)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return individual ? IndividualAddress.Parse(text.Trim()).Raw : GroupAddress.Parse(text.Trim()).Raw;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static KeyAddressRange? ParseRange(string? text, bool individual)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Replace("..", "-").Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var from = ParseAddress(parts.ElementAtOrDefault(0), individual);
        if (from is null) return null;
        var to = ParseAddress(parts.ElementAtOrDefault(1), individual) ?? from;
        return new KeyAddressRange(from.Value, to.Value);
    }
}

/// <summary>Keys are stored as lowercase hex.</summary>
internal sealed class HexBytesConverter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return string.IsNullOrEmpty(text) ? [] : Convert.FromHexString(text);
    }

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Convert.ToHexString(value));
}

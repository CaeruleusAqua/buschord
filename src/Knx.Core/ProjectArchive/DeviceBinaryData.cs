using System.Xml;
using System.Xml.Linq;

namespace Knx;

/// <summary>Native device blobs, distinct from static application BinaryData declarations.</summary>
internal static class DeviceBinaryData
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    internal static void Read(DeviceInstance device, XElement source)
    {
        var containers = source.Elements(source.Name.Namespace + "BinaryData").ToArray();
        if (containers.Length == 0) return;
        if (containers.Length != 1) { device.HasOpaqueBinaryData = true; return; }
        var container = containers[0];
        var values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var facts = new Dictionary<string, DeviceBinaryDataInfo>(StringComparer.Ordinal);
        var copyUnsafe = false;
        var total = 0;
        try
        {
            if (container.Attributes().Any(a => !a.IsNamespaceDeclaration)
                || container.Elements().Any(e => e.Name != container.Name)
                || container.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                throw new InvalidDataException("Unknown binary data container shape.");
            foreach (var item in container.Elements(container.Name))
            {
                var name = (string?)item.Attribute("Name");
                ValidateName(name);
                if (values.ContainsKey(name!)) throw new InvalidDataException("Duplicate binary data name.");
                var children = item.Elements().ToArray();
                if (item.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name != "Id" && a.Name != "RefId" && a.Name != "Name" && a.Name != "DoNotCopy")
                    || children.Length > 1 || children.Any(e => e.Name != item.Name.Namespace + "Data" || e.HasElements || e.Attributes().Any(a => !a.IsNamespaceDeclaration))
                    || item.Nodes().OfType<XText>().Any(t => !string.IsNullOrWhiteSpace(t.Value)))
                    throw new InvalidDataException("Unknown binary data shape.");
                var text = children.SingleOrDefault()?.Value ?? "";
                if (text.Length > 16 * 1024 * 1024 || text.Count(c => c is not (' ' or '\t' or '\r' or '\n')) > ((MaximumBytes + 2) / 3) * 4)
                    throw new InvalidDataException("Binary data exceeds its safety limit.");
                var data = Convert.FromBase64String(text);
                total = checked(total + data.Length);
                if (total > MaximumBytes) throw new InvalidDataException("Binary data exceeds its safety limit.");
                var noCopy = (string?)item.Attribute("DoNotCopy");
                var fact = new DeviceBinaryDataInfo((string?)item.Attribute("Id"), (string?)item.Attribute("RefId"),
                    noCopy is not null && XmlConvert.ToBoolean(noCopy), children.Length != 0);
                values.Add(name!, data); facts.Add(name!, fact);
                copyUnsafe |= fact.Id is not null || fact.RefId is not null;
            }
        }
        catch (Exception error) when (error is InvalidDataException or FormatException or XmlException or OverflowException)
        {
            // Retain the entire original container. Partial identity modeling could corrupt a later merge.
            device.HasOpaqueBinaryData = true;
            return;
        }
        foreach (var pair in values) device.BinaryData.Add(pair.Key, pair.Value);
        foreach (var pair in facts) device.BinaryDataInfo.Add(pair.Key, pair.Value);
        device.BinaryDataCopyUnsafe = copyUnsafe;
    }

    internal static XElement? Write(DeviceInstance device, XNamespace ns)
    {
        if (device.HasOpaqueBinaryData && device.BinaryData.Count != 0)
            throw new InvalidDataException("Cannot change an opaque native binary data container.");
        if (device.BinaryData.Count == 0) return null;
        var result = new XElement(ns + "BinaryData");
        var total = 0;
        foreach (var (name, data) in device.BinaryData)
        {
            ValidateName(name);
            if (data is null || data.Length > MaximumBytes - total)
                throw new InvalidDataException("Binary data exceeds its safety limit.");
            total += data.Length;
            var info = device.BinaryDataInfo.GetValueOrDefault(name);
            result.Add(new XElement(ns + "BinaryData", new XAttribute("Name", name),
                info?.Id is { } id ? new XAttribute("Id", id) : null,
                info?.RefId is { } reference ? new XAttribute("RefId", reference) : null,
                info?.DoNotCopy == true ? new XAttribute("DoNotCopy", true) : null,
                data.Length != 0 || info?.HasData != false
                    ? new XElement(ns + "Data", Convert.ToBase64String(data)) : null));
        }
        return result;
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255)
            throw new InvalidDataException("Binary data requires a bounded nonempty name.");
        XmlConvert.VerifyXmlChars(name);
    }
}

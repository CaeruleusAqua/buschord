using System.Xml.Linq;

namespace Knx;

/// <summary>Exact project schema selection, independent of ZIP entry placement.</summary>
internal enum ProjectFormat { Native20, Native21, Editor23, Native22 }

internal static class ProjectFormats
{
    internal static bool TryRead(XNamespace ns, out ProjectFormat format)
    {
        format = ns.NamespaceName switch
        {
            "http://knx.org/xml/project/20" => ProjectFormat.Native20,
            "http://knx.org/xml/project/21" => ProjectFormat.Native21,
            "http://knx.org/xml/project/22" => ProjectFormat.Native22,
            _ => ProjectFormat.Editor23,
        };
        return ns == Namespace(format);
    }

    internal static XNamespace Namespace(this ProjectFormat format) => format switch
    {
        ProjectFormat.Native20 => "http://knx.org/xml/project/20",
        ProjectFormat.Native21 => "http://knx.org/xml/project/21",
        ProjectFormat.Native22 => "http://knx.org/xml/project/22",
        ProjectFormat.Editor23 => "http://knx.org/xml/project/23",
        _ => throw new InvalidDataException("Unsupported project format."),
    };

    internal static bool IsNative(this ProjectFormat format) => format is ProjectFormat.Native20 or ProjectFormat.Native21 or ProjectFormat.Native22;
    internal static bool IsNative(XNamespace? ns) => ns is not null && TryRead(ns, out var format) && format.IsNative();
    internal static ProjectFormat Of(Project project) => project.OriginalXml?.Root is { } root ? Of(root) : ProjectFormat.Editor23;
    internal static ProjectFormat Of(XElement root) => TryRead(root.Name.Namespace, out var format)
        ? format : throw new InvalidDataException("Supported project schemas are /20, /21, /22 and /23.");
}

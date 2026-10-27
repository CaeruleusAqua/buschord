using System.Globalization;
using System.Xml.Linq;
using Knx.Xml;

namespace Knx;

/// <summary>
/// Reader/writer for project archives (*.knxproj = ZIP container with XML parts).
/// Shared element names are defined by XmlNames; supported archive layouts are handled explicitly.
/// </summary>
public static class Knxproj
{
    /// <summary>Namespace written for new projects.</summary>
    public const string ProjectNamespace = "http://knx.org/xml/project/23";
    internal static readonly XNamespace ExtensionNamespace = "urn:buschord:project:1";

    // Supported project namespaces; /23 is the default for new projects.
    private const string Schema21 = "http://knx.org/xml/project/21";
    private static readonly string[] ReadNamespaces = [Schema21, "http://knx.org/xml/project/23"];

    public static void Save(Project project, string path)
    {
        if (project.OriginalXml?.Root?.Name.NamespaceName == Schema21)
            throw new InvalidDataException(
                "This project uses schema http://knx.org/xml/project/21. BusChord can read it but not save it, " +
                "because its writer implements the /23 layout. Export the project from ETS6 with a newer schema to edit it.");
        var xml = ToXml(project);
        // Refuse before any write so an existing destination stays byte-identical.
        XmlPreservation.ValidateIdentities(xml.Root!);
        ArchiveState.Save(project, xml, path);
    }

    public static Project Load(string path)
    {
        return ArchiveState.Load(path);
    }

    /// <summary>
    /// Loads a project archive, decrypting a WinZip AES protected inner archive with the
    /// ETS6 project password. Reads only; encrypted projects cannot be saved back.
    /// </summary>
    public static Project Load(string path, string? password)
    {
        return ArchiveState.Load(path, password);
    }

    /// <summary>True when the failure is a missing or wrong project password.</summary>
    public static bool IsPasswordError(Exception exception) =>
        exception is InvalidDataException &&
        (exception.Message == EncryptedArchive.PasswordRequiredMessage ||
         exception.Message == EncryptedArchive.WrongPasswordMessage);

    /// <summary>
    /// Lädt die Application-Program-XML (KSXML) eines Geräts aus dem Projektarchiv.
    /// Accepted RefId forms: "M-00FA_H-Base_I-0000_A-0001-0E-1234" (Hardware2Program)
    /// and "M-00FA_A-0001-0E-1234" (Product). Entry lives at "M-00FA/M-00FA_A-0001-0E-1234.xml".
    /// </summary>
    public static XDocument? LoadApplicationProgram(string knxprojPath, string refId)
    {
        var entryPath = ApplicationProgramEntryPath(refId);
        if (entryPath is null) return null;
        return ArchiveState.LoadPart(knxprojPath, entryPath);
    }

    /// <summary>Maps a RefId to the XML entry path inside the archive, or null when unrecognized.</summary>
    public static string? ApplicationProgramEntryPath(string refId)
    {
        // RefId layout: "M-00FA_H-Base_I-0000_A-0001-0E-1234" (Hardware2Program)
        //              "M-0083_A-008A-25-6CE4"           (Product)
        // Manufacturer code is 6 chars ("M-00FA"); the application part starts at index 6.
        if (string.IsNullOrWhiteSpace(refId) || refId.Length < 6 || refId[0] != 'M' || refId[1] != '-')
            return null;
        var manufacturer = refId[..6];

        var programMarker = refId.IndexOf("_A-", StringComparison.Ordinal);
        var application = programMarker >= 0 && programMarker > 6
            ? manufacturer + refId[programMarker..]
            : refId;

        return $"{manufacturer}/{application}.xml";
    }
    public static XDocument ToXml(Project project)
    {
        var generated = GenerateXml(project);
        return project.OriginalXml is { } original && project.BaselineXml is { } baseline
            ? XmlPreservation.Merge(original, baseline, generated) : generated;
    }

    private static XDocument GenerateXml(Project project)
    {
        var ns = WriteNamespace(project);
        var projectEl = new XElement(ns + XmlNames.Project,
            new XAttribute("Id", project.Id),
            new XAttribute("Name", project.Name),
            Attr("Guid", project.Guid),
            Attr("Comment", project.Comment),
            project.ProjectStart is { } start ? new XAttribute("ProjectStart", start.ToString("o", CultureInfo.InvariantCulture)) : null);
        var installationsEl = new XElement(ns + XmlNames.Installations);
        foreach (var i in project.Installations)
        {
            var topology = new XElement(ns + XmlNames.Topology);
            foreach (var a in i.Areas)
            {
                var areaEl = new XElement(ns + XmlNames.Area,
                    new XAttribute("Id", a.Id),
                    new XAttribute("Address", a.Address),
                    new XAttribute("Name", a.Name ?? ""));
                foreach (var d in a.Devices) areaEl.Add(DeviceXml(d, ns));
                foreach (var l in a.Lines)
                {
                    var lineEl = new XElement(ns + XmlNames.Line,
                        new XAttribute("Id", l.Id),
                        new XAttribute("Address", l.Address),
                        new XAttribute("Name", l.Name ?? ""),
                        new XAttribute("MediumTypeRefId", l.MediumTypeRefId),
                        l.DomainAddress is { } lineDomain ? new XAttribute("DomainAddress", lineDomain) : null);
                    foreach (var d in l.Devices) lineEl.Add(DeviceXml(d, ns));
                    foreach (var s in l.Segments)
                    {
                        // Serialize the segment number and its required medium.
                        var segmentEl = new XElement(ns + XmlNames.Segment,
                            new XAttribute("Id", s.Id),
                            new XAttribute("Number", s.Address),
                            new XAttribute("Name", s.Name ?? ""),
                            new XAttribute("MediumTypeRefId", s.MediumTypeRefId ?? l.MediumTypeRefId),
                            s.DomainAddress is { } segmentDomain ? new XAttribute("DomainAddress", segmentDomain) : null);
                        foreach (var d in s.Devices) segmentEl.Add(DeviceXml(d, ns));
                        lineEl.Add(segmentEl);
                    }
                    areaEl.Add(lineEl);
                }
                topology.Add(areaEl);
            }
            var mainRanges = new XElement(ns + XmlNames.GroupAddresses,
                new XElement(ns + XmlNames.GroupRanges));
            foreach (var r in i.MainRanges) mainRanges.Elements().Single().Add(WriteRange(r, ns));

            var installationEl = new XElement(ns + XmlNames.Installation,
                new XAttribute("InstallationId", i.Id),
                new XAttribute("Name", i.Name ?? ""),
                new XAttribute("GroupAddressStyle", i.GroupAddressStyle),
                new XAttribute("Type", i.Type),
                i.Address is { } addr ? new XAttribute("Address", addr.ToString()) : null,
                // Schema order: Topology, Locations, GroupAddresses.
                topology, WriteLocations(i.Locations, ns), mainRanges);
            installationsEl.Add(installationEl);
        }
        projectEl.Add(installationsEl);
        if (!project.Workbench.IsEmpty)
            projectEl.Add(new XElement(ExtensionNamespace + "Workbench", new XAttribute("Version", 1),
                new XAttribute("Data", System.Text.Json.JsonSerializer.Serialize(project.Workbench))));
        var knx = new XElement(ns + XmlNames.Knx,
            new XAttribute("xmlns", ns),
            new XAttribute(XNamespace.Xmlns + "xsi", XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance")),
            new XAttribute(XNamespace.Xmlns + "xsd", XNamespace.Get("http://www.w3.org/2001/XMLSchema")),
            new XAttribute("created-by", "KnxCore"),
            new XElement(ns + XmlNames.MasterData,
                new XAttribute("Id", project.MasterData.Id),
                new XAttribute("Version", project.MasterData.Version),
                Attr("Signature", project.MasterData.Signature)),
            projectEl);
        return new XDocument(new XDeclaration("1.0", "utf-8", null), knx);

        static XAttribute? Attr(string name, string? value) => value is null ? null : new XAttribute(name, value);
    }

    private static XElement DeviceXml(DeviceInstance device, XNamespace ns)
    {
        var el = new XElement(ns + XmlNames.DeviceInstance,
            new XAttribute("Id", device.Id),
            new XAttribute("Address", device.Address),
            new XAttribute("Name", device.Name ?? ""),
            new XAttribute("ProductRefId", device.ProductRefId),
            device.Hardware2ProgramRefId is { } prog ? new XAttribute("Hardware2ProgramRefId", prog) : null,
            device.Description is { } desc ? new XAttribute("Description", desc) : null,
            device.Comment is { } comment ? new XAttribute("Comment", comment) : null);
        if (device.ComObjectRefs.Count > 0)
        {
            var refs = new XElement(ns + XmlNames.ComObjectInstanceRefs);
            foreach (var r in device.ComObjectRefs)
                refs.Add(new XElement(ns + XmlNames.ComObjectInstanceRef,
                    new XAttribute("RefId", r.Id),
                    new XAttribute(ExtensionNamespace + "ComObjectId", r.ComObjectId),
                    new XAttribute(ExtensionNamespace + "Number", r.Number),
                    r.Text is { } t ? new XAttribute("Text", t) : null,
                    r.FunctionText is { } f ? new XAttribute("FunctionText", f) : null,
                    r.Device is { } d ? new XAttribute("Device", d) : null,
                    r.Links.Count > 0 ? new XAttribute("Links", string.Join(' ', r.Links)) : null));
            el.Add(refs);
        }
        if (device.Channels.Count > 0)
            el.Add(new XElement(ns + "Channels", device.Channels.Select(c => WriteChannel(c, ns))));
        if (device.ParameterValues.Count > 0)
            el.Add(new XElement(ExtensionNamespace + "ParameterInstanceRefs", device.ParameterValues.Select(p =>
                new XElement(ExtensionNamespace + "ParameterInstanceRef", new XAttribute("RefId", p.Key), new XAttribute("Value", p.Value)))));
        return el;
    }

    private static XElement WriteChannel(ChannelInstance channel, XNamespace ns) => new(ns + "ChannelInstance",
        new XAttribute("Id", channel.Id), new XAttribute("Name", channel.Name),
        channel.Channels.Select(c => WriteChannel(c, ns)));

    private static ChannelInstance ReadChannel(XElement element)
    {
        var channel = new ChannelInstance { Id = (string?)element.Attribute("Id") ?? "", Name = (string?)element.Attribute("Name") ?? "" };
        channel.Channels.AddRange(element.Elements(element.Name).Select(ReadChannel));
        return channel;
    }

    private static XElement WriteRange(GroupRange range, XNamespace ns)
    {
        var el = new XElement(ns + XmlNames.GroupRange,
            new XAttribute("Id", range.Id),
            new XAttribute("Name", range.Name ?? ""),
            new XAttribute("RangeStart", range.RangeStart),
            new XAttribute("RangeEnd", range.RangeEnd));
        foreach (var child in range.Ranges) el.Add(WriteRange(child, ns));
        foreach (var a in range.Addresses)
        {
            el.Add(new XElement(ns + XmlNames.GroupAddress,
                new XAttribute("Id", a.Id),
                new XAttribute("Address", a.Address.ToString()),
                new XAttribute("Name", a.Name),
                a.Description is { } desc ? new XAttribute("Description", desc) : null,
                a.Comment is { } comment ? new XAttribute("Comment", comment) : null,
                a.DatapointType is { } dp ? new XAttribute("DatapointType", dp) : null,
                new XAttribute("Puid", a.Puid)));
        }
        return el;
    }

    public static Project FromXml(XDocument doc)
    {
        var root = doc.Root ?? throw new InvalidDataException("empty XML");
        var ns = root.Name.Namespace;
        if (root.Name.LocalName != XmlNames.Knx || !ReadNamespaces.Contains(ns.NamespaceName) || Elements(root, XmlNames.Project).Count() != 1)
            throw new InvalidDataException(
                "Supported format: one KNX project in namespace /21 or /23; other schemas or multiple projects cannot be saved safely.");
        var proj = Element(root, XmlNames.Project) ?? throw new InvalidDataException("no <Project>");
        var raw21 = ns.NamespaceName == Schema21;
        var master = Element(root, XmlNames.MasterData);
        var project = new Project
        {
            Id = (string?)proj.Attribute("Id") ?? "P-0",
            Name = (string?)proj.Attribute("Name") ?? "",
            Guid = (string?)proj.Attribute("Guid"),
            Comment = (string?)proj.Attribute("Comment"),
            MasterData = new MasterDataInfo
            {
                Id = (string?)master?.Attribute("Id") ?? "MD-1",
                Version = uint.TryParse((string?)master?.Attribute("Version"), out var v) ? v : 0,
                Signature = (string?)master?.Attribute("Signature"),
            },
        };
        if (DateTimeOffset.TryParse((string?)proj.Attribute("ProjectStart"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var start))
            project.ProjectStart = start;

        foreach (var instEl in Elements(Element(proj, XmlNames.Installations), XmlNames.Installation))
        {
            var installation = new Installation
            {
                Id = (string?)instEl.Attribute("InstallationId") ?? "I-" + project.Installations.Count,
                Name = (string?)instEl.Attribute("Name"),
                Type = Enum.TryParse<InstallationType>((string?)instEl.Attribute("Type"), out var type) ? type : InstallationType.Unknown,
                GroupAddressStyle = Enum.TryParse<GroupAddressStyle>((string?)instEl.Attribute("GroupAddressStyle"), out var s)
                    ? s : GroupAddressStyle.ThreeLevel,
            };
            if (IndividualAddress.TryParse((string?)instEl.Attribute("Address") ?? "", out var ia)) installation.Address = ia;

            foreach (var areaEl in Elements(Element(instEl, XmlNames.Topology), XmlNames.Area))
            {
                var area = new Area
                {
                    Id = (string?)areaEl.Attribute("Id") ?? "A-" + (string?)areaEl.Attribute("Address"),
                    Address = byte.Parse((string?)areaEl.Attribute("Address") ?? "0"),
                    Name = (string?)areaEl.Attribute("Name"),
                };
                area.Devices.AddRange(Elements(areaEl, XmlNames.DeviceInstance).Select(d => ReadDevice(d, raw21, area.Address, 0)));
                foreach (var lineEl in Elements(areaEl, XmlNames.Line))
                {
                    var line = new Line
                    {
                        Id = (string?)lineEl.Attribute("Id") ?? "L-" + (string?)lineEl.Attribute("Address"),
                        Address = byte.Parse((string?)lineEl.Attribute("Address") ?? "1"),
                        Name = (string?)lineEl.Attribute("Name"),
                        MediumTypeRefId = (string?)lineEl.Attribute("MediumTypeRefId") ??
                            (string?)Elements(lineEl, XmlNames.Segment).FirstOrDefault()?.Attribute("MediumTypeRefId") ?? "MT-0",
                        DomainAddress = (string?)lineEl.Attribute("DomainAddress"),
                    };
                    line.Devices.AddRange(Elements(lineEl, XmlNames.DeviceInstance).Select(d => ReadDevice(d, raw21, area.Address, line.Address)));
                    foreach (var segEl in Elements(lineEl, XmlNames.Segment))
                    {
                        var segment = new Segment
                        {
                            Id = (string?)segEl.Attribute("Id") ?? "S-" + (string?)segEl.Attribute("Address"),
                            Address = byte.Parse((string?)segEl.Attribute("Address") ?? (string?)segEl.Attribute("Number") ?? "0"),
                            Name = (string?)segEl.Attribute("Name"),
                            MediumTypeRefId = (string?)segEl.Attribute("MediumTypeRefId"),
                            DomainAddress = (string?)segEl.Attribute("DomainAddress"),
                        };
                        segment.Devices.AddRange(Elements(segEl, XmlNames.DeviceInstance).Select(d => ReadDevice(d, raw21, area.Address, line.Address)));
                        line.Segments.Add(segment);
                    }
                    area.Lines.Add(line);
                }
                installation.Areas.Add(area);
            }

            foreach (var rangeEl in Elements(Element(Element(instEl, XmlNames.GroupAddresses), XmlNames.GroupRanges), XmlNames.GroupRange))
                installation.MainRanges.Add(ReadRange(rangeEl));
            if (Element(instEl, XmlNames.Locations) is { } locations) ReadLocations(installation, locations);
            project.Installations.Add(installation);
        }
        if (proj.Element(ExtensionNamespace + "Workbench") is { } workbench)
        {
            if ((int?)workbench.Attribute("Version") != 1) throw new InvalidDataException("Unsupported BusChord Workbench metadata version.");
            Workbench.WorkbenchMetadata metadata;
            try
            {
                metadata = System.Text.Json.JsonSerializer.Deserialize<Workbench.WorkbenchMetadata>((string?)workbench.Attribute("Data") ?? "")
                    ?? throw new InvalidDataException("Invalid BusChord Workbench metadata.");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException("Invalid BusChord Workbench metadata: " + ex.Message, ex);
            }
            metadata.Validate();
            project.Workbench = metadata;
        }
        project.BaselineXml = GenerateXml(project);
        project.OriginalXml = new XDocument(doc);
        NormalizeGroupAddressReferences(project);
        return project;
    }

    /// <summary>
    /// /21 group-address element ids may carry a project/range prefix while communication
    /// object links may use the local <c>GA-n</c> suffix. Normalize only unambiguous suffixes;
    /// /23 and already-resolved links stay untouched.
    /// </summary>
    private static void NormalizeGroupAddressReferences(Project project)
    {
        var groups = ProjectQuery.GroupAddresses(project).ToList();
        var ids = groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var byLocal = groups
            .Select(g => (Group: g, Local: g.Id[(g.Id.LastIndexOf('_') + 1)..]))
            .Where(x => x.Local.Length > 0)
            .GroupBy(x => x.Local, StringComparer.Ordinal)
            .Where(x => x.Count() == 1 && !ids.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Single().Group, StringComparer.Ordinal);
        if (byLocal.Count == 0) return;
        foreach (var (_, comObject) in ProjectQuery.ComObjectRefs(project))
            for (var index = 0; index < comObject.Links.Count; index++)
                if (!ids.Contains(comObject.Links[index]) && byLocal.TryGetValue(comObject.Links[index], out var group))
                    comObject.Links[index] = group.Id;
    }

    private static GroupRange ReadRange(XElement element)
    {
        var range = new GroupRange
        {
            Id = (string?)element.Attribute("Id") ?? "GR-0",
            Name = (string?)element.Attribute("Name"),
            RangeStart = int.Parse((string?)element.Attribute("RangeStart") ?? "0"),
            RangeEnd = int.Parse((string?)element.Attribute("RangeEnd") ?? "0"),
        };
        foreach (var child in Elements(element, XmlNames.GroupRange)) range.Ranges.Add(ReadRange(child));
        foreach (var ga in Elements(element, XmlNames.GroupAddress))
        {
            range.Addresses.Add(new GroupAddressEntry
            {
                Id = (string?)ga.Attribute("Id") ?? "GA-0",
                Name = (string?)ga.Attribute("Name") ?? "",
                Description = (string?)ga.Attribute("Description"),
                Comment = (string?)ga.Attribute("Comment"),
                Address = ParseGroupAddress((string?)ga.Attribute("Address")),
                DatapointType = (string?)ga.Attribute("DatapointType"),
                Puid = uint.TryParse((string?)ga.Attribute("Puid"), out var p) ? p : 0,
            });
        }
        return range;
    }

    private static DeviceInstance ReadDevice(XElement device, bool raw21, byte area, byte line)
    {
        var instance = new DeviceInstance
        {
            Id = (string?)device.Attribute("Id") ?? (string?)device.Attribute("Address") ?? "",
            Address = DeviceAddress(device, raw21, area, line),
            Name = (string?)device.Attribute("Name"),
            ProductRefId = (string?)device.Attribute("ProductRefId") ?? "",
            Hardware2ProgramRefId = (string?)device.Attribute("Hardware2ProgramRefId"),
            Description = (string?)device.Attribute("Description"),
            Comment = (string?)device.Attribute("Comment"),
        };
        foreach (var r in Elements(Element(device, XmlNames.ComObjectInstanceRefs), XmlNames.ComObjectInstanceRef))
        {
            var id = (string?)r.Attribute("RefId") ?? "";
            var comObject = new ComObjectInstanceRef
            {
                Id = id,
                ComObjectId = (string?)r.Attribute(ExtensionNamespace + "ComObjectId") ?? id,
                Number = int.TryParse((string?)r.Attribute(ExtensionNamespace + "Number"), out var number) ? number : 0,
                Text = (string?)r.Attribute("Text"),
                FunctionText = (string?)r.Attribute("FunctionText"),
                Device = (string?)r.Attribute("Device"),
            };
            var links = (string?)r.Attribute("Links");
            if (!string.IsNullOrWhiteSpace(links))
                comObject.Links.AddRange(links.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            instance.ComObjectRefs.Add(comObject);
        }
        instance.Channels.AddRange(Elements(Element(device, "Channels"), "ChannelInstance").Select(ReadChannel));
        foreach (var parameter in device.Element(ExtensionNamespace + "ParameterInstanceRefs")?.Elements(ExtensionNamespace + "ParameterInstanceRef") ?? [])
        {
            var id = (string?)parameter.Attribute("RefId") ?? throw new InvalidDataException("Parameter RefId is required.");
            if (!instance.ParameterValues.TryAdd(id, (string?)parameter.Attribute("Value") ?? ""))
                throw new InvalidDataException($"Duplicate parameter RefId: {id}");
        }
        return instance;
    }

    /// <summary>Children in the parent's project namespace, matched by local name so /21 and /23 share one reader.</summary>
    private static IEnumerable<XElement> Elements(XElement? parent, string localName) =>
        parent is null ? [] : parent.Elements().Where(e => e.Name.LocalName == localName && e.Name.Namespace == parent.Name.Namespace);

    private static XElement? Element(XElement? parent, string localName) => Elements(parent, localName).FirstOrDefault();

    // Keep a loaded schema on save so a /21 project is not rewritten as a mixed /23 document.
    private static XNamespace WriteNamespace(Project project) =>
        project.OriginalXml?.Root is { } root && ReadNamespaces.Contains(root.Name.NamespaceName)
            ? root.Name.Namespace : (XNamespace)ProjectNamespace;

    // /21 stores the device number within the line; /23 stores the full x.y.z address.
    private static IndividualAddress DeviceAddress(XElement device, bool raw21, byte area, byte line)
    {
        var value = (string?)device.Attribute("Address") ?? "";
        if (!raw21) return IndividualAddress.Parse(value.Length == 0 ? "0.0.0" : value);
        if (!byte.TryParse(value, out var deviceNumber))
            throw new InvalidDataException($"Invalid device address '{value}' in schema /21.");
        return new IndividualAddress(area, line, deviceNumber);
    }

    // /21 stores the group address as its 16-bit value ("256"), /23 as "main/middle/sub".
    private static GroupAddress ParseGroupAddress(string? value)
    {
        if (value is not null && !value.Contains('/') && ushort.TryParse(value, out var raw) && raw > 0)
            return new GroupAddress(raw);
        return GroupAddress.Parse(value ?? "0/0/0");
    }

    /// <summary>
    /// /21 keeps project-level metadata (name, start, GUID, group address style) in
    /// project.xml instead of the 0.xml Project element. Only overlays what 0.xml lacks.
    /// </summary>
    internal static void ApplyProjectInformation(Project project, XDocument document)
    {
        if (document.Root?.Name.NamespaceName != Schema21) return;
        var info = Element(Element(document.Root, XmlNames.Project), "ProjectInformation");
        if (info is null) return;
        if (string.IsNullOrEmpty(project.Name) && (string?)info.Attribute("Name") is { Length: > 0 } name) project.Name = name;
        project.Guid ??= (string?)info.Attribute("Guid");
        project.Comment ??= (string?)info.Attribute("Comment");
        if (project.ProjectStart is null &&
            DateTimeOffset.TryParse((string?)info.Attribute("ProjectStart"), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var start))
            project.ProjectStart = start;
        if (Enum.TryParse<GroupAddressStyle>((string?)info.Attribute("GroupAddressStyle"), out var style))
            foreach (var installation in project.Installations) installation.GroupAddressStyle = style;
        // FromXml built the baseline before this overlay; keep ToXml consistent with the loaded model.
        project.BaselineXml = GenerateXml(project);
    }

    /// <summary>
    /// Native Locations container. Omitted while the tree is empty so archives without
    /// building data keep their existing XML unchanged; the preservation merge then
    /// leaves any native Locations container untouched.
    /// </summary>
    private static XElement? WriteLocations(BuildingTree tree, XNamespace ns)
    {
        if (tree.IsEmpty) return null;
        var locations = new XElement(ns + XmlNames.Locations);
        foreach (var root in tree.Roots) locations.Add(WriteSpace(root, ns));
        return locations;
    }

    private static XElement WriteSpace(SpaceNode node, XNamespace ns)
    {
        var function = node.Kind == SpaceKind.Function;
        // Unknown types are only ever written back verbatim. There is no valid enumerant to
        // substitute, so the attribute is omitted rather than emitting the invalid "Unknown".
        // <Function> carries no Type: the element itself identifies the node kind.
        // Puid is xs:int and required by the schema; 0 is the stable guideline for new nodes.
        var space = new XElement(ns + (function ? XmlNames.Function : XmlNames.Space),
            new XAttribute("Id", node.Id),
            new XAttribute("Name", node.Name),
            function ? null : node.Kind switch
            {
                SpaceKind.Unknown when node.RawType is { Length: > 0 } raw => new XAttribute("Type", raw),
                SpaceKind.Unknown => null,
                _ => new XAttribute("Type", SpaceXml.KindName(node.Kind)),
            },
            node.Usage is { } usage ? new XAttribute("Usage", usage) : null,
            node.Number is { } number ? new XAttribute("Number", number) : null,
            node.Comment is { } comment ? new XAttribute("Comment", comment) : null,
            node.Description is { } description ? new XAttribute("Description", description) : null,
            new XAttribute("Puid", node.Puid));
        // Schema sequence: Space*, DeviceInstanceRef*, Function*.
        foreach (var child in node.Children.Where(c => c.Kind != SpaceKind.Function)) space.Add(WriteSpace(child, ns));
        foreach (var device in node.DeviceRefs)
            space.Add(new XElement(ns + XmlNames.DeviceInstanceRef, new XAttribute("RefId", device)));
        foreach (var child in node.Children.Where(c => c.Kind == SpaceKind.Function)) space.Add(WriteSpace(child, ns));
        return space;
    }

    private static void ReadLocations(Installation installation, XElement locations)
    {
        var ns = locations.Name.Namespace;
        foreach (var space in locations.Elements(ns + XmlNames.Space))
            installation.Locations.AddRoot(ReadSpace(space));
    }

    private static SpaceNode ReadSpace(XElement element)
    {
        var ns = element.Name.Namespace;
        var function = element.Name.LocalName == XmlNames.Function;
        var type = (string?)element.Attribute("Type");
        var kind = function ? SpaceKind.Function : SpaceXml.ParseKind(type);
        var node = new SpaceNode
        {
            Id = (string?)element.Attribute("Id") ?? "",
            Name = (string?)element.Attribute("Name") ?? "",
            Kind = kind,
            RawType = kind == SpaceKind.Unknown ? type : null,
            Number = (string?)element.Attribute("Number"),
            Description = (string?)element.Attribute("Description"),
            Comment = (string?)element.Attribute("Comment"),
            Usage = (string?)element.Attribute("Usage"),
            Puid = int.TryParse((string?)element.Attribute("Puid"), out var puid) ? puid : 0,
        };
        foreach (var child in element.Elements(ns + XmlNames.Space)) node.Add(ReadSpace(child));
        foreach (var reference in element.Elements(ns + XmlNames.DeviceInstanceRef))
            node.DeviceRefs.Add((string?)reference.Attribute("RefId") ?? "");
        foreach (var child in element.Elements(ns + XmlNames.Function)) node.Add(ReadSpace(child));
        return node;
    }
}

using System.Globalization;
using System.Xml.Linq;
using System.Xml;

namespace Knx;

/// <summary>Native state with a /20 line projection and a shared /21-/22 segment projection.</summary>
internal sealed class NativeProject
{
    internal const string SidecarEntry = "BusChord/project.xml";
    private readonly XNamespace Ns;
    private readonly ProjectFormat _format;
    private static readonly XNamespace Bc = Knxproj.ExtensionNamespace;

    internal NativeProject(ProjectFormat format) { _format = format; Ns = format.Namespace(); }

    internal XDocument ProjectModel(Project project, XDocument document)
    {
        var root = document.Root!;
        var element = root.Element(Ns + "Project")!;
        element.Attributes().Where(a => a.Name.LocalName != "Id").Remove();
        // The archive's master data lives in knx_master.xml; the model's synthetic reference is not its native XML.
        root.Elements(Ns + "MasterData").Remove();
        var index = 0;
        foreach (var installation in element.Element(Ns + "Installations")!.Elements(Ns + "Installation"))
        {
            var id = (string?)installation.Attribute("InstallationId") ?? "";
            if (id.StartsWith("I-", StringComparison.Ordinal)) id = id[2..];
            if (!int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 0 or > 15)
                number = index;
            installation.SetAttributeValue("InstallationId", number);
            installation.Attribute("GroupAddressStyle")?.Remove(); installation.Attribute("Type")?.Remove(); installation.Attribute("Address")?.Remove();
            index++;
            if (installation.Element(Ns + "Locations") is null)
                installation.Element(Ns + "Topology")!.AddAfterSelf(new XElement(Ns + "Locations"));
            foreach (var area in installation.Element(Ns + "Topology")!.Elements(Ns + "Area"))
            {
                if (_format == ProjectFormat.Native20) Project20Topology(area);
                else ProjectSegmentedTopology(area);
            }
        }
        foreach (var address in element.Descendants(Ns + "GroupAddress"))
            address.SetAttributeValue("Address", GroupAddress.Parse((string)address.Attribute("Address")!).Raw);
        element.AddFirst(InformationElement(project, null));
        return document;
    }

    private void Project20Topology(XElement area)
    {
        if (area.Elements(Ns + "DeviceInstance").Any())
            throw new InvalidDataException("Schema /20 devices must belong directly to a line.");
        foreach (var line in area.Elements(Ns + "Line"))
        {
            if (line.Elements(Ns + "Segment").Any())
                throw new InvalidDataException("Schema /20 cannot represent editor segments.");
            foreach (var device in line.Elements(Ns + "DeviceInstance")) ProjectDevice(device, area, line);
        }
    }

    private void ProjectSegmentedTopology(XElement area)
    {
        var looseAreaDevices = area.Elements(Ns + "DeviceInstance").ToArray();
        if (looseAreaDevices.Length > 0)
        {
            var line = area.Elements(Ns + "Line").FirstOrDefault(l => (string?)l.Attribute("Address") == "0");
            if (line is null)
            {
                line = new XElement(Ns + "Line", new XAttribute("Id", (string)area.Attribute("Id")! + "_L-0"), new XAttribute("Address", 0));
                area.AddFirst(line);
            }
            foreach (var device in looseAreaDevices) { device.Remove(); line.AddFirst(device); }
        }
        foreach (var line in area.Elements(Ns + "Line"))
        {
            var loose = line.Elements(Ns + "DeviceInstance").ToArray();
            var segment = line.Elements(Ns + "Segment").FirstOrDefault(s => (string?)s.Attribute("Number") == "0");
            if (segment is null && (loose.Length > 0 || !line.Elements(Ns + "Segment").Any()))
            {
                segment = new XElement(Ns + "Segment", new XAttribute("Id", (string)line.Attribute("Id")! + "_S-0"),
                    new XAttribute("Number", 0), new XAttribute("MediumTypeRefId", (string?)line.Attribute("MediumTypeRefId") ?? "MT-0"));
                line.AddFirst(segment);
            }
            foreach (var device in loose) { device.Remove(); segment!.Add(device); }
            line.Attribute("MediumTypeRefId")?.Remove(); line.Attribute("DomainAddress")?.Remove();
            foreach (var device in line.Descendants(Ns + "DeviceInstance"))
                ProjectDevice(device, area, line);
        }
    }

    private void ProjectDevice(XElement device, XElement area, XElement line)
    {
        var address = IndividualAddress.Parse((string)device.Attribute("Address")!);
        if (address.Area != (byte)(int)area.Attribute("Address")! || address.Line != (byte)(int)line.Attribute("Address")!)
            throw new InvalidDataException("A device address does not match its native topology location.");
        device.SetAttributeValue("Address", address.Device);
        if (device.Element(Bc + "ParameterInstanceRefs") is { } parameters)
        {
            parameters.Name = Ns + "ParameterInstanceRefs";
            foreach (var parameter in parameters.Elements()) parameter.Name = Ns + parameter.Name.LocalName;
            parameters.Remove(); device.AddFirst(parameters);
        }
        if (device.Element(Ns + "Channels") is { } channels)
        {
            if (channels.Descendants(Ns + "ChannelInstance").Any(c => c.Elements().Any()))
                throw new InvalidDataException("Native channel instances are flat; a nested editor channel tree cannot be exported natively.");
            channels.Name = Ns + "ChannelInstances";
        }
    }

    /// <summary>Undo snapshots include metadata and editor-only facts even when the archive stores them in separate parts.</summary>
    internal void CompleteSnapshot(Project project, XDocument document)
    {
        var element = document.Root!.Element(Ns + "Project")!;
        var info = element.Element(Ns + "ProjectInformation");
        var updated = InformationElement(project, info);
        if (info is null) element.AddFirst(updated); else info.ReplaceWith(updated);
        element.Elements(Bc + "Workbench").Remove();
        if (!project.Workbench.IsEmpty)
            element.Add(new XElement(Bc + "Workbench", new XAttribute("Version", 1),
                new XAttribute("Data", System.Text.Json.JsonSerializer.Serialize(project.Workbench))));
        var devices = ProjectQuery.DeviceList(project).ToDictionary(d => d.Id, StringComparer.Ordinal);
        foreach (var node in element.Descendants(Ns + "DeviceInstance"))
        {
            if (!devices.TryGetValue((string?)node.Attribute("Id") ?? "", out var device)) continue;
            var objects = device.ComObjectRefs.ToDictionary(o => o.Id, StringComparer.Ordinal);
            foreach (var reference in node.Descendants(Ns + "ComObjectInstanceRef"))
                if (objects.TryGetValue((string?)reference.Attribute("RefId") ?? "", out var obj))
                {
                    reference.SetAttributeValue(Bc + "ComObjectId", obj.ComObjectId);
                    reference.SetAttributeValue(Bc + "Number", obj.Number);
                }
        }
    }

    internal XElement InformationElement(Project project, XElement? original)
    {
        var info = original is null ? new XElement(Ns + "ProjectInformation") : new XElement(original);
        info.SetAttributeValue("Name", project.Name);
        info.SetAttributeValue("Guid", project.Guid);
        info.SetAttributeValue("Comment", project.Comment);
        info.SetAttributeValue("ProjectStart", project.ProjectStart?.ToString("o", CultureInfo.InvariantCulture));
        info.SetAttributeValue("GroupAddressStyle", project.Installations.FirstOrDefault()?.GroupAddressStyle ?? GroupAddressStyle.ThreeLevel);
        return info;
    }

    internal XDocument InformationDocument(Project project, XDocument? original, int lastPuid)
    {
        var result = original is null ? new XDocument(new XElement(Ns + "KNX", new XElement(Ns + "Project", new XAttribute("Id", project.Id)))) : new XDocument(original);
        var root = result.Root ?? throw new InvalidDataException("Missing native project information root.");
        if (root.Name != Ns + "KNX") throw new InvalidDataException("Unexpected native project information namespace.");
        var projects = root.Elements(Ns + "Project").ToList();
        if (projects.Count != 1 || (string?)projects[0].Attribute("Id") != project.Id)
            throw new InvalidDataException("Native project information identity does not match the project.");
        var old = projects[0].Element(Ns + "ProjectInformation");
        var updated = InformationElement(project, old);
        if (string.IsNullOrWhiteSpace((string?)updated.Attribute("Guid")))
            throw new InvalidDataException("Native export requires the project's original Guid.");
        var previous = int.TryParse((string?)old?.Attribute("LastUsedPuid"), out var puid) ? puid : 0;
        updated.SetAttributeValue("LastUsedPuid", Math.Max(lastPuid, previous));
        if (old is null) projects[0].AddFirst(updated); else old.ReplaceWith(updated);
        return result;
    }

    internal (XDocument Data, XDocument Sidecar, int LastPuid) PrepareArchive(Project project, XDocument snapshot, int previousPuid, bool informationInline = false)
    {
        if (project.Installations.Select(i => i.GroupAddressStyle).Distinct().Count() > 1)
            throw new InvalidDataException("Native projects have one project-wide group address style.");
        var document = new XDocument(snapshot);
        if (project.Name.Length > 50 || project.Installations.Count > 16 || project.Installations.Any(i => (i.Name?.Length ?? 0) > 50))
            throw new InvalidDataException("Project metadata exceeds the native format limits.");
        var modeledNames = new HashSet<string>(["Project", "Area", "Line", "Segment", "DeviceInstance", "Space", "Function", "GroupRange", "GroupAddress", "ChannelInstance", "ModuleInstance"], StringComparer.Ordinal);
        foreach (var attribute in document.Descendants().Where(e => e.Name.Namespace == Ns && modeledNames.Contains(e.Name.LocalName)).Attributes("Id"))
            try { XmlConvert.VerifyNCName(attribute.Value); }
            catch (XmlException ex) { throw new InvalidDataException("Invalid native project identity.", ex); }
        foreach (var attribute in document.Descendants().Where(e => e.Name.Namespace == Ns && (modeledNames.Contains(e.Name.LocalName) || e.Name.LocalName == "ComObjectInstanceRef"))
            .Attributes().Where(a => a.Name.LocalName is "Name" or "Text" or "FunctionText"))
            if (attribute.Value.Length > 255) throw new InvalidDataException("Native project text exceeds the format length limit.");
        if (!informationInline) document.Descendants(Ns + "ProjectInformation").Remove();
        var last = Math.Max(previousPuid, document.Descendants().Attributes("Puid").Select(a => int.TryParse(a.Value, out var n) ? n : 0).DefaultIfEmpty().Max());
        var originals = project.ArchiveState?.NativeObjectIds
            ?? project.OriginalXml?.Descendants().Attributes("Id").Select(a => a.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var element in document.Descendants().Where(e => e.Name.Namespace == Ns && e.Name.LocalName is "Area" or "Line" or "Segment" or "DeviceInstance" or "Space" or "Function" or "GroupRange" or "GroupAddress"))
            if (element.Attribute("Puid") is null || !originals.Contains((string?)element.Attribute("Id") ?? "") && (int?)element.Attribute("Puid") == 0)
                element.SetAttributeValue("Puid", checked(++last));
        if (informationInline && document.Descendants(Ns + "ProjectInformation").SingleOrDefault() is { } information)
        {
            if (string.IsNullOrWhiteSpace((string?)information.Attribute("Guid"))) throw new InvalidDataException("Native export requires the project's original Guid.");
            information.SetAttributeValue("LastUsedPuid", Math.Max(last, int.TryParse((string?)information.Attribute("LastUsedPuid"), out var prior) ? prior : 0));
        }
        var sidecar = new XElement(Bc + "Project", new XAttribute("Version", 2), new XAttribute("Id", project.Id));
        foreach (var workbench in document.Descendants(Bc + "Workbench").ToList()) { sidecar.Add(new XElement(workbench)); workbench.Remove(); }
        foreach (var device in document.Descendants(Ns + "DeviceInstance"))
        {
            var facts = new XElement(Bc + "Device", new XAttribute("Id", (string)device.Attribute("Id")!));
            foreach (var obj in device.Descendants(Ns + "ComObjectInstanceRef"))
            {
                var attributes = obj.Attributes().Where(a => a.Name.Namespace == Bc).ToArray();
                if (attributes.Length == 0) continue;
                facts.Add(new XElement(Bc + "Object", new XAttribute("RefId", (string)obj.Attribute("RefId")!),
                    new XAttribute("NumberKind", project.Workbench.Objects.GetValueOrDefault(new Workbench.ObjectKey((string)device.Attribute("Id")!, (string)obj.Attribute("RefId")!).ToString())?.StructuralDeclaredNumber == (int?)obj.Attribute(Bc + "Number") ? "Declared" : "Override"),
                    obj.Attribute("Device") is { } direction ? new XAttribute("Device", direction.Value) : null,
                    attributes.Select(a => new XAttribute(a.Name.LocalName, a.Value))));
                attributes.Remove();
                obj.Attribute("Device")?.Remove();
            }
            if (facts.HasElements) sidecar.Add(facts);
        }
        return (document, new XDocument(sidecar), last);
    }

    internal static void ValidateApplicationMigrationSource(Project project, DeviceInstance device,
        IReadOnlyList<ApplicationModuleInstance>? expectedInstances = null)
    {
        var raw = project.OriginalXml?.Descendants().SingleOrDefault(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == device.Id);
        if (raw is null) return;
        void Shape(XElement element, string[] attributes, string[] children)
        {
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && !(a.Name.NamespaceName.Length == 0 && attributes.Contains(a.Name.LocalName))
                && !(element.Name.LocalName == "ComObjectInstanceRef" && a.Name.Namespace == Bc && a.Name.LocalName is "Number" or "ComObjectId"))
                || element.Elements().Any(e => e.Name.Namespace != element.Name.Namespace || !children.Contains(e.Name.LocalName))
                || element.Nodes().Any(n => n is not XElement and not XComment && (n is not XText text || !string.IsNullOrWhiteSpace(text.Value))))
                throw new InvalidDataException("Application migration has unmapped native XML payload in " + element.Name.LocalName + ".");
        }
        foreach (var container in raw.Elements())
        {
            switch (container.Name.LocalName)
            {
                case "ModuleInstances":
                    Shape(container, [], ["ModuleInstance"]);
                    if (container.Elements().GroupBy(e => (string?)e.Attribute("Id")).Any(g => g.Count() > 1))
                        throw new InvalidDataException("Ambiguous native source module identities.");
                    foreach (var instance in container.Elements())
                    {
                        Shape(instance, ["Id", "RefId", "RepeatIndex"], ["Arguments"]);
                        foreach (var arguments in instance.Elements())
                        {
                            Shape(arguments, [], ["Argument"]);
                            if (arguments.Elements().GroupBy(e => (string?)e.Attribute("RefId")).Any(g => g.Count() > 1))
                                throw new InvalidDataException("Ambiguous native source module arguments.");
                            foreach (var argument in arguments.Elements()) Shape(argument, ["RefId", "Value"], []);
                        }
                    }
                    if (expectedInstances is not null)
                    {
                        var expected = expectedInstances.ToDictionary(i => i.Id, StringComparer.Ordinal);
                        foreach (var instance in container.Elements())
                        {
                            if (!expected.TryGetValue((string?)instance.Attribute("Id") ?? "", out var declaration)
                                || (string?)instance.Attribute("RefId") != declaration.RefId)
                                throw new InvalidDataException("Stored native module instance is not bound to the evaluated source application.");
                            var arguments = declaration.Arguments.ToDictionary(a => a.RefId, StringComparer.Ordinal);
                            foreach (var argument in instance.Elements().SelectMany(e => e.Elements()))
                                if (!arguments.TryGetValue((string?)argument.Attribute("RefId") ?? "", out var declared)
                                    || (string?)argument.Attribute("Value") != declared.Value)
                                    throw new InvalidDataException("Stored native module arguments override the evaluated source application; explicit migration is required.");
                        }
                    }
                    break;
                case "ParameterInstanceRefs":
                    Shape(container, [], ["ParameterInstanceRef"]);
                    foreach (var reference in container.Elements()) Shape(reference, ["RefId", "Value"], []);
                    break;
                case "ComObjectInstanceRefs":
                    Shape(container, [], ["ComObjectInstanceRef"]);
                    foreach (var reference in container.Elements()) Shape(reference, ["RefId", "Text", "FunctionText", "Links", "Device", "ChannelId"], []);
                    break;
                case "Channels" or "ChannelInstances":
                    void Channel(XElement channel) { Shape(channel, ["Id", "RefId", "Name"], ["ChannelInstance"]); foreach (var child in channel.Elements()) Channel(child); }
                    Shape(container, [], ["ChannelInstance"]); foreach (var channel in container.Elements()) Channel(channel);
                    break;
                case "AdditionalAddresses" or "IPConfig" or "Security" or "BusInterfaces" or "RfFastAckSlots":
                    break;
                default: throw new InvalidDataException("Application migration has unmapped native XML payload in " + container.Name.LocalName + ".");
            }
        }
    }

    private static string? ProgramIdentity(XDocument? application)
    {
        var programs = application?.Descendants().Where(e => e.Name.LocalName == "ApplicationProgram").ToArray() ?? [];
        return programs.Length == 1 ? (string?)programs[0].Attribute("Id") : null;
    }

    internal void RetainState(Project project, XDocument sidecar, XDocument existing, XDocument? original, string? password)
    {
        var devices = ProjectQuery.DeviceList(project).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var originalDevices = original?.Descendants(Ns + "DeviceInstance").ToDictionary(d => (string)d.Attribute("Id")!, StringComparer.Ordinal) ?? [];
        foreach (var element in existing.Root!.Elements().Where(e => e.Name.Namespace == Bc
            && e.Name.LocalName is "RetiredModule" or "RetiredModuleContainer" or "RetiredReferenceContainer" or "DormantParameter" or "DormantObject"))
        {
            var copy = new XElement(element); var id = (string?)copy.Attribute("DeviceId") ?? "";
            if (copy.Attribute("ApplicationId") is null && originalDevices.TryGetValue(id, out var source))
            {
                var reference = (string?)source.Attribute("Hardware2ProgramRefId") ?? (string?)source.Attribute("ProductRefId");
                if (reference is not null && ProgramIdentity(Knxproj.LoadApplicationProgram(project, reference, password)) is { } sourceId)
                    copy.SetAttributeValue("ApplicationId", sourceId);
            }
            var dormant = copy.Name.LocalName is "DormantParameter" or "DormantObject";
            var current = devices.TryGetValue(id, out var device)
                ? ProgramIdentity(Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId, password)) : null;
            if (!dormant || (bool?)copy.Attribute("Historical") == true || copy.Attribute("ApplicationId") is { } scope && scope.Value != current) sidecar.Root!.Add(copy);
        }
    }

    internal void SynchronizeModules(Project project, XDocument data, XDocument sidecar, XDocument? original,
        string? password, IParameterScriptRuntime? scripts)
    {
        var devices = ProjectQuery.DeviceList(project).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var originalDevices = original?.Descendants(Ns + "DeviceInstance").ToDictionary(d => (string)d.Attribute("Id")!, StringComparer.Ordinal) ?? [];
        foreach (var node in data.Descendants(Ns + "DeviceInstance"))
        {
            var id = (string)node.Attribute("Id")!;
            if (!devices.TryGetValue(id, out var device)) continue;
            var oldDevice = originalDevices.GetValueOrDefault(id);
            var oldValues = oldDevice?.Element(Ns + "ParameterInstanceRefs")?.Elements(Ns + "ParameterInstanceRef")
                .ToDictionary(e => (string)e.Attribute("RefId")!, e => (string?)e.Attribute("Value") ?? "", StringComparer.Ordinal) ?? [];
            var sameReference = oldDevice is not null && (string?)oldDevice.Attribute("ProductRefId") == device.ProductRefId
                && (string?)oldDevice.Attribute("Hardware2ProgramRefId") == device.Hardware2ProgramRefId;
            var unchanged = sameReference && oldValues.Count == device.ParameterValues.Count
                && device.ParameterValues.All(p => oldValues.GetValueOrDefault(p.Key) == p.Value);
            // An untouched native structure can be preserved even when its application needs an unavailable plug-in/runtime.
            if (unchanged) continue;
            var nativeContainer = node.Element(Ns + "ModuleInstances");
            var application = Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId, password);
            if (application is null)
            {
                if (nativeContainer?.HasElements == true || sidecar.Root!.Elements(Bc + "RetiredModule").Any(e => (string?)e.Attribute("DeviceId") == id))
                    throw new InvalidDataException("Native module export requires the device's manufacturer application program.");
                continue;
            }
            var appId = ProgramIdentity(application) ?? throw new InvalidDataException("Native export requires one identified application program.");
            var sourceApplication = oldDevice is null || sameReference ? application : Knxproj.LoadApplicationProgram(project,
                (string?)oldDevice.Attribute("Hardware2ProgramRefId") ?? (string?)oldDevice.Attribute("ProductRefId") ?? "", password);
            var sourceId = ProgramIdentity(sourceApplication);
            if (oldDevice is not null && sourceId is null) throw new InvalidDataException("Native application migration requires the source application definition.");
            var changedApplication = oldDevice is not null && sourceId != appId;
            foreach (var legacy in sidecar.Root!.Elements().Where(e => (string?)e.Attribute("DeviceId") == id && e.Attribute("ApplicationId") is null))
                legacy.SetAttributeValue("ApplicationId", sourceId ?? appId);
            bool CurrentScope(XElement e) => (string?)e.Attribute("DeviceId") == id && (string?)e.Attribute("ApplicationId") == appId && (bool?)e.Attribute("Historical") != true;
            if (changedApplication)
            {
                _ = Ksxml.Parse(sourceApplication!, out var sourceEvaluation, oldValues.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal), scripts);
                if (sourceEvaluation.StructureFailed || !sourceEvaluation.Converged || sourceEvaluation.ScriptFailures > 0 || sourceEvaluation.DepthLimitReached || sourceEvaluation.InvalidAssignments.Count > 0)
                    throw new InvalidDataException("Source native module arguments could not be validated for application migration.");
                ValidateApplicationMigrationSource(project, device, ApplicationModules.ReadInstances(sourceEvaluation.ExpandedRoot!));
                foreach (var history in sidecar.Root.Elements().Where(e => (string?)e.Attribute("DeviceId") == id
                    && e.Name.LocalName is "RetiredModule" or "RetiredModuleContainer" or "RetiredReferenceContainer" or "DormantParameter" or "DormantObject"))
                    history.SetAttributeValue("Historical", true);
                if (nativeContainer is not null)
                {
                    foreach (var instance in nativeContainer.Elements(Ns + "ModuleInstance"))
                        sidecar.Root.Add(new XElement(Bc + "RetiredModule", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", sourceId!), new XAttribute("Historical", true), new XElement(instance)));
                    var skeleton = new XElement(nativeContainer); skeleton.Elements(Ns + "ModuleInstance").Remove();
                    if (HasPayload(skeleton)) sidecar.Root.Add(new XElement(Bc + "RetiredModuleContainer", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", sourceId!), new XAttribute("Historical", true), skeleton));
                    nativeContainer.Remove(); nativeContainer = null;
                }
                var snapshot = project.OriginalXml?.Descendants(Ns + "DeviceInstance").SingleOrDefault(e => (string?)e.Attribute("Id") == id);
                foreach (var sourceContainer in snapshot?.Elements().Where(e => e.Name.LocalName is "ParameterInstanceRefs" or "ComObjectInstanceRefs") ?? [])
                {
                    var comments = sourceContainer.DescendantNodes().OfType<XComment>().Select(c => new XComment(c.Value)).ToArray();
                    if (comments.Length == 0) continue;
                    if (!sidecar.Root.Elements(Bc + "RetiredReferenceContainer").Any(e => (string?)e.Attribute("DeviceId") == id && (string?)e.Attribute("ApplicationId") == sourceId && e.Element(sourceContainer.Name) is not null))
                        sidecar.Root.Add(new XElement(Bc + "RetiredReferenceContainer", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", sourceId!), new XAttribute("Historical", true), new XElement(sourceContainer.Name, comments)));
                    node.Element(sourceContainer.Name)?.DescendantNodes().OfType<XComment>().Remove();
                }
            }
            var retiredContainers = sidecar.Root.Elements(Bc + "RetiredModuleContainer").Where(CurrentScope).ToList();
            if (retiredContainers.Count > 1) throw new InvalidDataException("Ambiguous native module container payload.");
            var oldContainer = nativeContainer ?? retiredContainers.SingleOrDefault()?.Element(Ns + "ModuleInstances");
            if (!changedApplication && !application.Descendants().Any(e => e.Name.LocalName == "Module")) continue;
            _ = Ksxml.Parse(application, out var evaluation,
                device.ParameterValues.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal), scripts);
            if (evaluation.StructureFailed || !evaluation.Converged || evaluation.DepthLimitReached || evaluation.ScriptFailures > 0 || evaluation.InvalidAssignments.Count > 0)
                throw new InvalidDataException("Native module structure could not be evaluated completely for export.");
            var instances = ApplicationModules.ReadInstances(evaluation.ExpandedRoot!);
            var known = instances.ToDictionary(i => i.Id, StringComparer.Ordinal);
            string? ModuleOf(string reference)
            {
                if (appId is not null && reference.StartsWith(appId + "_", StringComparison.Ordinal)) reference = reference[(appId.Length + 1)..];
                var marker = reference.LastIndexOf("_MI-", StringComparison.Ordinal);
                if (marker < 0) return null;
                var separator = reference.IndexOf('_', marker + 4);
                return separator < 0 ? null : reference[..separator];
            }
            void PreserveEmptyReferences(XElement container)
            {
                foreach (var retiredContainer in sidecar.Root!.Elements(Bc + "RetiredReferenceContainer")
                    .Where(e => CurrentScope(e) && e.Element(container.Name) is not null).ToList()) retiredContainer.Remove();
                if (container.Elements(container.Name.LocalName == "ParameterInstanceRefs" ? Ns + "ParameterInstanceRef" : Ns + "ComObjectInstanceRef").Any()) return;
                if (HasPayload(container)) sidecar.Root.Add(new XElement(Bc + "RetiredReferenceContainer", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", appId), new XElement(container)));
                container.Remove();
            }
            // A dormant instance's links and input values remain in the editor sidecar, not as dangling native references.
            if (node.Element(Ns + "ParameterInstanceRefs") is { } parameterContainer)
            {
                foreach (var parameter in parameterContainer.Elements(Ns + "ParameterInstanceRef").ToArray())
                    if (ModuleOf((string)parameter.Attribute("RefId")!) is { } moduleId && !known.ContainsKey(moduleId))
                    {
                        sidecar.Root!.Add(new XElement(Bc + "DormantParameter", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", appId), new XElement(parameter)));
                        parameter.Remove();
                    }
                PreserveEmptyReferences(parameterContainer);
            }
            if (node.Element(Ns + "ComObjectInstanceRefs") is { } objectContainer)
            {
                foreach (var obj in objectContainer.Elements(Ns + "ComObjectInstanceRef").ToArray())
                    if (ModuleOf((string)obj.Attribute("RefId")!) is { } moduleId && !known.ContainsKey(moduleId))
                    {
                        sidecar.Root!.Add(new XElement(Bc + "DormantObject", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", appId), new XElement(obj)));
                        obj.Remove();
                    }
                PreserveEmptyReferences(objectContainer);
            }
            var retired = sidecar.Root!.Elements(Bc + "RetiredModule").Where(CurrentScope).ToList();
            var originals = (oldContainer?.Elements(Ns + "ModuleInstance") ?? []).Concat(retired.Elements(Ns + "ModuleInstance")).ToList();
            if (originals.GroupBy(e => (string?)e.Attribute("Id")).Any(g => g.Count() > 1))
                throw new InvalidDataException("Ambiguous native module instance identities.");
            var oldById = originals.ToDictionary(e => (string)e.Attribute("Id")!, StringComparer.Ordinal);
            var ordered = originals.Select(e => (string)e.Attribute("Id")!).Where(known.ContainsKey)
                .Concat(instances.Select(i => i.Id)).Distinct(StringComparer.Ordinal);
            var container = oldContainer is null ? new XElement(Ns + "ModuleInstances") : new XElement(oldContainer);
            var updatedInstances = new List<XElement>();
            foreach (var instanceId in ordered)
            {
                var instance = known[instanceId];
                var instanceXml = oldById.TryGetValue(instanceId, out var old) ? new XElement(old) : new XElement(Ns + "ModuleInstance");
                instanceXml.SetAttributeValue("Id", instance.Id); instanceXml.SetAttributeValue("RefId", instance.RefId);
                instanceXml.SetAttributeValue("RepeatIndex", instance.RepeatIndex.Length == 0 ? null : instance.RepeatIndex);
                var oldArguments = instanceXml.Element(Ns + "Arguments");
                var arguments = oldArguments is null ? new XElement(Ns + "Arguments") : new XElement(oldArguments);
                var updatedArguments = new List<XElement>();
                var wantedArguments = instance.Arguments.ToDictionary(a => a.RefId, StringComparer.Ordinal);
                var existingArguments = oldArguments?.Elements(Ns + "Argument").ToList() ?? [];
                if (existingArguments.GroupBy(a => (string?)a.Attribute("RefId")).Any(g => g.Count() > 1))
                    throw new InvalidDataException("Ambiguous native module argument identities.");
                foreach (var referenceId in existingArguments.Select(a => (string)a.Attribute("RefId")!).Concat(instance.Arguments.Select(a => a.RefId)).Distinct(StringComparer.Ordinal))
                {
                    var existing = existingArguments.SingleOrDefault(a => (string?)a.Attribute("RefId") == referenceId);
                    var value = existing is null ? new XElement(Ns + "Argument") : new XElement(existing);
                    value.SetAttributeValue("RefId", referenceId);
                    if (value.Attribute("Value") is null && wantedArguments.TryGetValue(referenceId, out var argument)) value.SetAttributeValue("Value", argument.Value);
                    updatedArguments.Add(value);
                }
                ReplaceKnownChildren(arguments, Ns + "Argument", updatedArguments);
                if (oldArguments is not null)
                {
                    if (HasPayload(arguments)) oldArguments.ReplaceWith(arguments); else oldArguments.Remove();
                }
                else if (arguments.HasElements) instanceXml.AddFirst(arguments);
                updatedInstances.Add(instanceXml);
            }
            ReplaceKnownChildren(container, Ns + "ModuleInstance", updatedInstances);
            foreach (var entry in retired) entry.Remove();
            foreach (var removed in originals.Where(e => !known.ContainsKey((string)e.Attribute("Id")!)))
                sidecar.Root.Add(new XElement(Bc + "RetiredModule", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", appId), new XElement(removed)));
            foreach (var retiredContainer in retiredContainers) retiredContainer.Remove();
            if (!container.Elements(Ns + "ModuleInstance").Any())
            {
                // Native formats require an instance inside the optional container. Keep its comments elsewhere at Count zero.
                if (HasPayload(container)) sidecar.Root.Add(new XElement(Bc + "RetiredModuleContainer", new XAttribute("DeviceId", id), new XAttribute("ApplicationId", appId), container));
                nativeContainer?.Remove();
            }
            else if (nativeContainer is not null) nativeContainer.ReplaceWith(container);
            else
            {
                var anchor = node.Elements().LastOrDefault(e => e.Name.Namespace == Ns && e.Name.LocalName is "ParameterInstanceRefs" or "ComObjectInstanceRefs" or "ChannelInstances");
                if (anchor is null) node.AddFirst(container); else anchor.AddAfterSelf(container);
            }
        }
    }

    // Keep container attributes, comments and unknown children in their original slots.
    private static void ReplaceKnownChildren(XElement container, XName name, IReadOnlyList<XElement> replacements)
    {
        var index = 0;
        foreach (var existing in container.Elements(name).ToArray())
            if (index < replacements.Count) existing.ReplaceWith(replacements[index++]); else existing.Remove();
        while (index < replacements.Count) container.Add(replacements[index++]);
    }

    private static bool HasPayload(XElement element) => element.HasAttributes
        || element.Nodes().Any(n => n is not XText text || !string.IsNullOrWhiteSpace(text.Value));

    internal void ApplySidecar(Project project, XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("Missing BusChord sidecar root.");
        if (root.Name != Bc + "Project" || (int?)root.Attribute("Version") is not (1 or 2) || (string?)root.Attribute("Id") != project.Id)
            throw new InvalidDataException("Invalid BusChord sidecar identity or version.");
        if (root.Element(Bc + "Workbench") is { } workbench)
        {
            var metadata = System.Text.Json.JsonSerializer.Deserialize<Workbench.WorkbenchMetadata>((string?)workbench.Attribute("Data") ?? "")
                ?? throw new InvalidDataException("Invalid BusChord sidecar metadata.");
            metadata.Validate(); project.Workbench = metadata;
        }
        var devices = ProjectQuery.DeviceList(project).ToDictionary(d => d.Id, StringComparer.Ordinal);
        var applicationIds = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool CurrentScope(XElement element)
        {
            if ((bool?)element.Attribute("Historical") == true) return false;
            if (element.Attribute("ApplicationId") is not { } scope) return true; // Legacy v1 data belongs to the observed source application.
            var id = (string?)element.Attribute("DeviceId") ?? "";
            if (!devices.TryGetValue(id, out var device)) throw new InvalidDataException("Unknown application-scoped sidecar device.");
            if (!applicationIds.TryGetValue(id, out var application)) applicationIds[id] = application = ProgramIdentity(Knxproj.LoadApplicationProgram(project, device.Hardware2ProgramRefId ?? device.ProductRefId));
            return application == scope.Value;
        }
        var rawDevices = project.OriginalXml?.Descendants(Ns + "DeviceInstance").ToDictionary(d => (string)d.Attribute("Id")!, StringComparer.Ordinal) ?? [];
        foreach (var retired in root.Elements(Bc + "RetiredReferenceContainer").Where(CurrentScope))
        {
            if (!rawDevices.TryGetValue((string?)retired.Attribute("DeviceId") ?? "", out var rawDevice)) throw new InvalidDataException("Unknown retired reference container device.");
            var container = retired.Elements().SingleOrDefault();
            if (container is null || container.Name.Namespace != Ns || container.Name.LocalName is not ("ParameterInstanceRefs" or "ComObjectInstanceRefs")
                || container.Elements().Any(e => e.Name == Ns + "ParameterInstanceRef" || e.Name == Ns + "ComObjectInstanceRef") || rawDevice.Element(container.Name) is not null)
                throw new InvalidDataException("Invalid retired reference container.");
            rawDevice.AddFirst(new XElement(container));
        }
        void RestoreSnapshotNode(string deviceId, XElement reference, string containerName)
        {
            if (!rawDevices.TryGetValue(deviceId, out var rawDevice)) return;
            var container = rawDevice.Element(Ns + containerName);
            if (container is null) { container = new XElement(Ns + containerName); rawDevice.AddFirst(container); }
            container.Add(new XElement(reference));
        }
        var nativeObjects = devices.ToDictionary(p => p.Key, p => p.Value.ComObjectRefs.Select(o => o.Id).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var dormant in root.Elements(Bc + "DormantParameter").Where(CurrentScope))
        {
            if (!devices.TryGetValue((string?)dormant.Attribute("DeviceId") ?? "", out var device)) throw new InvalidDataException("Unknown dormant parameter device.");
            var value = dormant.Element(Ns + "ParameterInstanceRef") ?? throw new InvalidDataException("Invalid dormant parameter.");
            if (!device.ParameterValues.TryAdd((string?)value.Attribute("RefId") ?? throw new InvalidDataException("Missing dormant parameter reference."), (string?)value.Attribute("Value") ?? ""))
                throw new InvalidDataException("Ambiguous dormant parameter reference.");
            RestoreSnapshotNode(device.Id, value, "ParameterInstanceRefs");
        }
        foreach (var dormant in root.Elements(Bc + "DormantObject").Where(CurrentScope))
        {
            if (!devices.TryGetValue((string?)dormant.Attribute("DeviceId") ?? "", out var device)) throw new InvalidDataException("Unknown dormant object device.");
            var value = dormant.Element(Ns + "ComObjectInstanceRef") ?? throw new InvalidDataException("Invalid dormant object.");
            var id = (string?)value.Attribute("RefId") ?? throw new InvalidDataException("Missing dormant object reference.");
            if (device.ComObjectRefs.Any(o => o.Id == id)) throw new InvalidDataException("Ambiguous dormant object reference.");
            var obj = new ComObjectInstanceRef { Id = id, ComObjectId = id, Text = (string?)value.Attribute("Text"), FunctionText = (string?)value.Attribute("FunctionText"),
                Device = (string?)value.Attribute("Device"), ChannelId = (string?)value.Attribute("ChannelId") };
            obj.Links.AddRange(((string?)value.Attribute("Links") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            device.ComObjectRefs.Add(obj);
            RestoreSnapshotNode(device.Id, value, "ComObjectInstanceRefs");
            var key = new Workbench.ObjectKey(device.Id, id).ToString();
            if (!project.Workbench.Objects.TryGetValue(key, out var metadata)) project.Workbench.Objects[key] = metadata = new();
            metadata.StructuralPreviousActive ??= metadata.Active; metadata.Active = false;
        }
        foreach (var node in root.Elements(Bc + "Device"))
        {
            if (!devices.TryGetValue((string?)node.Attribute("Id") ?? "", out var device)) throw new InvalidDataException("Unknown BusChord sidecar device.");
            var objects = device.ComObjectRefs.ToDictionary(o => o.Id, StringComparer.Ordinal);
            foreach (var fact in node.Elements(Bc + "Object"))
            {
                if (!objects.TryGetValue((string?)fact.Attribute("RefId") ?? "", out var obj)) throw new InvalidDataException("Unknown BusChord sidecar object.");
                if (!int.TryParse((string?)fact.Attribute("Number"), out var number) || number is < 0 or > 65535)
                    throw new InvalidDataException("Invalid BusChord sidecar object number.");
                obj.Device = (string?)fact.Attribute("Device");
                if ((string?)fact.Attribute("NumberKind") != "Declared" || !project.ObjectTextDefinitions.ContainsKey(new(device.Id, obj.Id))) obj.Number = number;
                else if (project.Workbench.Objects.TryGetValue(new Workbench.ObjectKey(device.Id, obj.Id).ToString(), out var metadata)) metadata.StructuralDeclaredNumber = obj.Number;
                // Rebuild init-only ComObjectId only when the sidecar provides a distinct identity.
                if ((string?)fact.Attribute("ComObjectId") is { } id && id != obj.ComObjectId)
                {
                    var replacement = new ComObjectInstanceRef { Id = obj.Id, ComObjectId = id, Number = obj.Number,
                        Text = obj.ExplicitText, FunctionText = obj.ExplicitFunctionText, Device = obj.Device, ChannelId = obj.ChannelId,
                        InheritedText = obj.InheritedText, InheritedFunctionText = obj.InheritedFunctionText };
                    replacement.Links.AddRange(obj.Links); device.ComObjectRefs[device.ComObjectRefs.IndexOf(obj)] = replacement;
                }
            }
        }
        foreach (var device in devices.Values)
            foreach (var obj in device.ComObjectRefs.Where(o => nativeObjects[device.Id].Contains(o.Id)))
                if (project.Workbench.Objects.TryGetValue(new Workbench.ObjectKey(device.Id, obj.Id).ToString(), out var metadata)
                    && metadata.StructuralPreviousActive is { } activity)
                { metadata.Active = activity; metadata.StructuralPreviousActive = null; }
    }
}

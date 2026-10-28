using System.Globalization;
using System.Xml.Linq;
using Knx.Workbench;

namespace Knx;

internal static class ApplicationObjects
{
    internal static void Read(Project project, IEnumerable<DeviceInstance> devices, XDocument application, IParameterScriptRuntime? scripts = null)
    {
        var programId = (string?)application.Descendants().FirstOrDefault(e => e.Name.LocalName == "ApplicationProgram")?.Attribute("Id");
        foreach (var device in devices)
        {
            _ = Ksxml.Parse(application, out var evaluation,
                device.ParameterValues.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal), scripts);
            if (evaluation.StructureFailed || !evaluation.Converged || evaluation.DepthLimitReached || evaluation.ScriptFailures > 0 || evaluation.InvalidAssignments.Count > 0) continue;
            var root = evaluation.ExpandedRoot ?? throw new InvalidDataException("empty application");
            var definitions = root.Descendants().Where(e => e.Name.LocalName == "ComObject"
                && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))
                .Where(e => e.Attribute("Id") is not null).ToDictionary(e => (string)e.Attribute("Id")!);
            var references = root.Descendants().Where(e => e.Name.LocalName == "ComObjectRef"
                && !e.Ancestors().Any(a => a.Name.LocalName == "ModuleDef"))
                .Where(e => e.Attribute("Id") is not null).ToDictionary(e => (string)e.Attribute("Id")!);
            var defaults = DynamicParameters.ReadDeclaredValues(root, out _);
            var native = project.OriginalXml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "DeviceInstance"
                && (string?)e.Attribute("Id") == device.Id);
            var instanceRefs = native?.Elements().FirstOrDefault(e => e.Name.LocalName == "ComObjectInstanceRefs")?
                .Elements().Where(e => e.Name.LocalName == "ComObjectInstanceRef")
                .ToDictionary(e => (string)e.Attribute("RefId")!) ?? [];
            foreach (var instance in device.ComObjectRefs)
            {
                references.TryGetValue(instance.Id, out var reference);
                if (reference is null && programId is not null) references.TryGetValue(programId + "_" + instance.Id, out reference);
                var objectId = (string?)reference?.Attribute("RefId") ?? instance.ComObjectId;
                definitions.TryGetValue(objectId, out var definition);
                if (definition is null && programId is not null) definitions.TryGetValue(programId + "_" + objectId, out definition);
                if (definition is null) continue;
                instanceRefs.TryGetValue(instance.Id, out var source);
                string? Pick(string name) => (string?)source?.Attribute(name) ?? (string?)reference?.Attribute(name) ?? (string?)definition.Attribute(name);
                if (source?.Attribute(Knxproj.ExtensionNamespace + "Number") is null
                    && int.TryParse(Pick("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) instance.Number = number;
                var textReference = (string?)reference?.Attribute("TextParameterRefId") ?? (string?)definition.Attribute("TextParameterRefId");
                project.ObjectTextDefinitions[new(device.Id, instance.Id)] = new(
                    (string?)reference?.Attribute("Text") ?? (string?)definition.Attribute("Text"),
                    (string?)reference?.Attribute("FunctionText") ?? (string?)definition.Attribute("FunctionText"),
                    textReference, textReference is null ? null : defaults.GetValueOrDefault(textReference),
                    textReference is null || defaults.ContainsKey(textReference),
                    reference?.Attribute("TextParameterRefId") is not null ? reference.Annotation<TextArgumentBinding>()
                        : definition.Annotation<TextArgumentBinding>() ?? reference?.Annotation<TextArgumentBinding>(),
                    ApplicationChannels.Read(evaluation, device, (string?)reference?.Attribute("Id") ?? instance.Id, programId, instance.ChannelId));
                var key = new ObjectKey(device.Id, instance.Id).ToString();
                if (!project.Workbench.Objects.TryGetValue(key, out var metadata))
                    project.Workbench.Objects[key] = metadata = new();
                if (int.TryParse(Pick("Number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declaredNumber))
                    metadata.StructuralDeclaredNumber = declaredNumber;
                metadata.Dpt ??= DptId.FromDeclaration(Pick("DatapointType"));
                var bits = Ksxml.ParseObjectSize(Pick("ObjectSize"));
                if (bits > 0) metadata.Bits ??= bits;
                if (metadata.Flags is null && Pick("CommunicationFlag") == "Enabled")
                    metadata.Flags = "C" + string.Concat(new[] { ("ReadFlag", "R"), ("WriteFlag", "W"), ("TransmitFlag", "T"),
                        ("UpdateFlag", "U"), ("ReadOnInitFlag", "I") }.Where(f => Pick(f.Item1) == "Enabled").Select(f => f.Item2));
            }
            ApplicationTexts.RefreshObjects(project, device, evaluation.Parameters.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal));
        }
    }
}

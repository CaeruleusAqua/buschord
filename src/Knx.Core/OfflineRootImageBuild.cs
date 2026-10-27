using System.Xml;

namespace Knx;

/// <summary>Builds a bounded root working set from owned sources and a complete fixed-field population.</summary>
public static partial class OfflineRootImageBuild
{
    public static RootImageBuildPlan Preview(Project project, DeviceInstance device, RootImageBuildRequest request)
    {
        var manifest = new ArchivedImageSourceManifest(ArchivedImageSourceStatus.InvalidRequest);
        try
        {
            if (!WithinBudget(project, device) || request is null || request.Procedure is null
                || request.CodeMaskPolicy is not (StaticCodeMaskPolicy.SourceDefinedBits or StaticCodeMaskPolicy.NoStaticSourceBits)
                || !RootBuildGuard.Safe(request.Procedure.NativeSubtype) || !Enum.IsDefined(request.Procedure.Access)
                || (request.Procedure.Parts & ~(LoadParts.Full | LoadParts.Parameters | LoadParts.Groups)) != 0
                || (request.Procedure.Parts & (LoadParts.Full | LoadParts.Parameters)) == 0
                || request.Tables is not null && (!Enum.IsDefined(request.Tables.Format)
                    || (request.Procedure.Parts & (LoadParts.Full | LoadParts.Groups)) == 0))
                RootBuildGuard.Fail(RootImageBuildIssueCode.InvalidRequest);
            var owned = RootImageBuildBinding.Copy(request);
            if (device.HasOpaqueBinaryData || device.BinaryData.Count != 0) RootBuildGuard.Fail(RootImageBuildIssueCode.UnaccountedBinaryData);
            var inputs = ArchivedImageSources.ReadInputs(project, device); manifest = inputs.Manifest;
            if (manifest.Binding is null || !manifest.Binding.MatchesIdentity(project, device))
                RootBuildGuard.Fail(RootImageBuildIssueCode.SourceUnavailable);
            var sources = new RootParameterWriterSources(project, device, inputs);
            var context = NativeApplicationEvaluationContext.Create(project, device, inputs);
            if (!context.IsAvailable)
            {
                var issue = context.Issues.FirstOrDefault();
                RootBuildGuard.Fail(issue?.Code switch
                {
                    GeneratedTableIssueCode.ExternalConfigurationUnaccounted => RootImageBuildIssueCode.ExternalConfigurationUnaccounted,
                    GeneratedTableIssueCode.UnaccountedBinaryData => RootImageBuildIssueCode.UnaccountedBinaryData,
                    GeneratedTableIssueCode.LimitExceeded => RootImageBuildIssueCode.LimitExceeded,
                    GeneratedTableIssueCode.InputsChanged or GeneratedTableIssueCode.StaleSource => RootImageBuildIssueCode.InputsChanged,
                    _ => RootImageBuildIssueCode.ActivationUnavailable,
                }, table: issue);
            }
            var writers = new RootParameterWriterSet(inputs, sources, context);
            var procedure = LoadProcedureCompiler.Compile(inputs.Application!, inputs.Master, owned.Procedure);
            if (!procedure.IsAvailable || procedure.ProfileVersion != 1 || procedure.ConditionalSelection is not null)
                RootBuildGuard.Fail(RootImageBuildIssueCode.ProcedureUnavailable, procedure: procedure.Issues.FirstOrDefault());
            GeneratedTablePreview? tables = null;
            if (owned.Tables is not null)
            {
                tables = GeneratedTableCompiler.CompileFromContext(project, device, inputs, procedure, owned.Tables, context);
                if (!tables.IsAvailable) RootBuildGuard.Fail(RootImageBuildIssueCode.TableUnavailable, table: tables.Issues.FirstOrDefault());
            }
            var buffers = Allocate(sources.Segments, tables);
            Seed(buffers, sources.Segments, owned.CodeMaskPolicy);
            ApplyParameters(buffers, writers.Claims, sources.Policy.ByteOrder);
            var tableSummary = ApplyTables(buffers, tables);
            var chunks = buffers.Select(b => new RootImageChunk(b.Arena, b.Offset, b.Data, b.Validity,
                b.SourceIds.Order(StringComparer.Ordinal).ToArray())).ToArray();
            var spans = Missing(chunks, out var unspecified, out var desired);
            var requirements = Requirements(tables, unspecified);
            if (!WithinBudget(project, device) || !manifest.Binding.MatchesIdentity(project, device)
                || !context.MatchesInputs(project, device, inputs) || sources.CaptureCarriers(project, device, manifest.ApplicationId!) != sources.CarrierHash)
                RootBuildGuard.Fail(RootImageBuildIssueCode.InputsChanged);
            var binding = new RootImageBuildBinding(project, device, inputs, owned, sources.CarrierHash, context, writers.Population,
                sources.Policy, writers.Claims, chunks, spans, procedure, tableSummary, requirements);
            return new(manifest, binding, sources.Policy, writers.Population, procedure, tableSummary, sources.Segments.ToArray(),
                writers.Claims, chunks, spans, requirements, desired, unspecified);
        }
        catch (RootBuildRejected failure) { return new(manifest, failure.Issue); }
        catch (Exception failure) when (failure is IOException or InvalidDataException or XmlException or FormatException or OverflowException or ArgumentException)
        { return new(manifest, new(RootImageBuildIssueCode.InvalidDeclaration)); }
        catch (InvalidOperationException) { return new(manifest, new(RootImageBuildIssueCode.InputsChanged)); }
    }

    internal static bool WithinBudget(Project project, DeviceInstance device)
    {
        if (project is null || device is null || !RootBuildGuard.Safe(project.Id) || project.Guid is { } guid && !RootBuildGuard.Safe(guid)
            || !RootBuildGuard.Safe(device.Id) || !RootBuildGuard.Safe(device.ProductRefId)
            || device.Hardware2ProgramRefId is { } hardware && !RootBuildGuard.Safe(hardware)
            || device.ParameterValues.Count > RootBuildGuard.Limit) return false;
        var count = 0; var matches = 0; var member = false; long characters = 0;
        foreach (var candidate in ProjectQuery.DeviceList(project))
        {
            if (++count > RootBuildGuard.Limit) return false;
            if (candidate.Id == device.Id) matches++;
            if (ReferenceEquals(candidate, device)) member = true;
        }
        if (!member || matches != 1) return false;
        foreach (var (key, value) in device.ParameterValues)
            if (!RootBuildGuard.Safe(key) || value is null || value.Length > 65536
                || (characters += key.Length + value.Length) > RootBuildGuard.TextLimit) return false;
        return true;
    }

}

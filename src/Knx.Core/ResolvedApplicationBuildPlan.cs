namespace Knx;

/// <summary>A current, procedure-only declaration preview. It cannot execute actions or construct a device image.</summary>
public sealed class ResolvedApplicationBuildPreview
{
    public string Scope => "OfflineApplicationBuildPlan";
    public int ProfileVersion => 2;
    public bool IsAvailable => Binding is not null && Procedure?.IsAvailable == true && Issues.Count == 0;
    public bool ExecutionEligible => false;
    public bool CompleteApplicationImage => false;
    public bool DownloadEligible => false;
    public ArchivedImageSourceManifest Sources { get; }
    public LoadProcedurePlan? Procedure { get; }
    public ResolvedApplicationBuildSourceBinding? Binding { get; }
    public IReadOnlyList<LoadProcedureIssue> Issues { get; }

    internal ResolvedApplicationBuildPreview(ArchivedImageSourceManifest sources, LoadProcedureIssue issue)
    { Sources = sources; Issues = Array.AsReadOnly(new[] { issue }); }

    internal ResolvedApplicationBuildPreview(ArchivedImageSourceManifest sources, LoadProcedurePlan procedure,
        ResolvedApplicationBuildSourceBinding? binding)
    { Sources = sources; Procedure = procedure; Binding = binding; Issues = procedure.Issues; }
}

/// <summary>Replays the same procedure-only source and condition checks; never grants execution authority.</summary>
public sealed class ResolvedApplicationBuildSourceBinding
{
    private readonly ArchivedImageSourceBinding _sources;
    private readonly LoadProcedureRequest _request;
    public int ProfileVersion => 1;
    public string SourceFingerprint { get; }
    public string ProcedureWitnessHash { get; }
    public string ProcedureGraphHash { get; }
    public string EvaluationContextHash { get; }
    public string ProjectInputHash { get; }
    public string Fingerprint { get; }

    internal ResolvedApplicationBuildSourceBinding(ArchivedImageSourceBinding sources, LoadProcedureRequest request,
        LoadProcedurePlan procedure, string graphHash, string contextHash, string inputHash)
    {
        _sources = sources; _request = request with { };
        SourceFingerprint = sources.Fingerprint; ProcedureWitnessHash = procedure.WitnessHash!;
        ProcedureGraphHash = graphHash; EvaluationContextHash = contextHash; ProjectInputHash = inputHash;
        Fingerprint = ParameterPatchSourceBinding.Hash(new
        {
            scope = "ResolvedPrimaryProgramBuild", ProfileVersion, SourceFingerprint, ProcedureWitnessHash,
            ProcedureGraphHash, EvaluationContextHash, ProjectInputHash, request = _request,
        });
    }

    public bool IsCurrent(Project project, DeviceInstance device)
    {
        if (!_sources.MatchesIdentity(project, device)) return false;
        return OfflineApplicationBuildPlan.PreviewResolved(project, device, _request).Binding?.Fingerprint == Fingerprint;
    }
}

public static partial class OfflineApplicationBuildPlan
{
    /// <summary>Explicit root-condition resolution without a script runtime, generated tables or transport.</summary>
    public static ResolvedApplicationBuildPreview PreviewResolved(Project project, DeviceInstance device, LoadProcedureRequest request)
    {
        var inputs = ArchivedImageSources.ReadInputs(project, device);
        ResolvedApplicationBuildPreview Error(LoadProcedureIssueCode code) => new(inputs.Manifest, new(code));
        if (request is null) return Error(LoadProcedureIssueCode.InvalidRequest);
        if (inputs.Manifest.Status != ArchivedImageSourceStatus.Inspected || inputs.Application is null
            || device.Hardware2ProgramRefId is not null && inputs.Manifest.Hardware?.Status != ArchivedHardwareSourceStatus.Inspected)
            return Error(LoadProcedureIssueCode.InvalidApplication);
        var inputHash = InputHash(project, device);
        if (inputHash is null) return Error(LoadProcedureIssueCode.LimitExceeded);
        var revision = project.ApplicationDataRevision;
        var prepared = LoadProcedureCompiler.PrepareResolved(inputs, request);
        if (!prepared.IsAvailable)
            return new(inputs.Manifest, prepared.Issues.FirstOrDefault() ?? new(LoadProcedureIssueCode.InvalidApplication));
        var context = NativeProcedureEvaluationContext.Create(project, device, inputs, prepared);
        if (!context.IsAvailable)
            return new(inputs.Manifest, context.Issues.FirstOrDefault() ?? new(LoadProcedureIssueCode.InvalidApplication));
        if (!context.MatchesInputs(project, device, inputs, prepared)) return Error(LoadProcedureIssueCode.InputsChanged);
        var procedure = LoadProcedureCompiler.CompileResolved(prepared, context);
        if (!procedure.IsAvailable) return new(inputs.Manifest, procedure, null);
        if (project.ApplicationDataRevision != revision || !inputs.Manifest.Binding!.MatchesIdentity(project, device)
            || InputHash(project, device) != inputHash || !context.MatchesInputs(project, device, inputs, prepared))
            return Error(LoadProcedureIssueCode.InputsChanged);
        var binding = new ResolvedApplicationBuildSourceBinding(inputs.Manifest.Binding!, request, procedure,
            prepared.GraphFingerprint!, context.Fingerprint!, inputHash);
        return new(inputs.Manifest, procedure, binding);
    }
}

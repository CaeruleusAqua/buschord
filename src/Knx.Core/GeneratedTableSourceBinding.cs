namespace Knx;

/// <summary>Freshness of one explicit common-table preview; never a commit token or execution permission.</summary>
public sealed class GeneratedTableSourceBinding
{
    private readonly ArchivedImageSourceBinding _sources;
    private readonly LoadProcedureRequest _procedure;
    private readonly GeneratedTableRequest _request;
    public int ProfileVersion => 1;
    public string SourceFingerprint { get; }
    public string ProcedureWitnessHash { get; }
    public string NativeWitnessHash { get; }
    public string PayloadHash { get; }
    public string Fingerprint { get; }
    public bool RequiresScripts { get; }

    internal GeneratedTableSourceBinding(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure,
        GeneratedTableRequest request, NativeCommunicationWitness witness, GeneratedTablePayload[] payloads,
        GeneratedTableProcedureAdjustment[] adjustments)
    {
        _sources = sources; _procedure = procedure.Request! with { }; _request = request with { };
        SourceFingerprint = sources.Fingerprint; ProcedureWitnessHash = procedure.WitnessHash!;
        NativeWitnessHash = witness.Fingerprint!; RequiresScripts = witness.RequiresScripts;
        PayloadHash = ParameterPatchSourceBinding.Hash(new
        {
            payloads = payloads.Select(p => new { p.Role, p.Arena, p.ByteOffset, p.Origin, p.ResourceId, p.Length,
                data = p.Data.Sha256, writes = p.WriteMask.Sha256, before = p.BeforeData?.Sha256,
                beforeMask = p.BeforeMask?.Sha256, afterMask = p.AfterMask?.Sha256 }).ToArray(), adjustments,
        });
        Fingerprint = ParameterPatchSourceBinding.Hash(new
        { ProfileVersion, SourceFingerprint, ProcedureWitnessHash, NativeWitnessHash, PayloadHash, RequiresScripts, request = _request, procedure = _procedure });
    }

    public bool IsCurrent(Project project, DeviceInstance device, IParameterScriptRuntime? scripts = null)
    {
        if (!_sources.MatchesIdentity(project, device) || RequiresScripts && scripts is null) return false;
        return OfflineApplicationBuildPlan.Preview(project, device, _procedure, _request, scripts).GeneratedTables?.Binding?.Fingerprint == Fingerprint;
    }
}

namespace Knx;

/// <summary>Detached emission facts for an owning coordinator; no standalone replay or apply authority.</summary>
internal sealed class SharedGeneratedTableEmission
{
    internal int ProfileVersion => 2;
    internal bool IsAvailable => Fingerprint is not null && Issues.Count == 0;
    internal GeneratedTableFormat? Format { get; }
    internal LoadProcedurePlan? ActualProcedure { get; }
    internal string? SourceFingerprint { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? ProjectInputHash { get; }
    internal string? ProcedureWitnessHash => ActualProcedure?.WitnessHash;
    internal string? NativeWitnessHash { get; }
    internal string? PayloadHash { get; }
    internal string? Fingerprint { get; }
    internal bool RequiresScripts { get; }
    internal IReadOnlyList<GeneratedTableIssue> Issues { get; }
    internal IReadOnlyList<GeneratedTablePayload> Payloads { get; }
    internal IReadOnlyList<GeneratedTableProcedureAdjustment> ProcedureSizeAdjustments { get; }
    internal IReadOnlyList<GeneratedTableRequirement> Requirements { get; }

    internal SharedGeneratedTableEmission(GeneratedTableIssue issue, GeneratedTableFormat? format)
    {
        Format = format is { } selected && Enum.IsDefined(selected) ? selected : null;
        Issues = Array.AsReadOnly(new[] { issue });
        Payloads = Array.AsReadOnly(Array.Empty<GeneratedTablePayload>());
        ProcedureSizeAdjustments = Array.AsReadOnly(Array.Empty<GeneratedTableProcedureAdjustment>());
        Requirements = Array.AsReadOnly(Array.Empty<GeneratedTableRequirement>());
    }

    internal SharedGeneratedTableEmission(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure,
        GeneratedTableRequest request, NativeCommunicationWitness witness, GeneratedTablePayload[] payloads,
        GeneratedTableProcedureAdjustment[] adjustments, GeneratedTableRequirement[] requirements)
    {
        Format = request.Format; ActualProcedure = procedure; SourceFingerprint = sources.Fingerprint;
        ApplicationDefinitionHash = witness.ApplicationDefinitionHash; ProjectInputHash = witness.ProjectInputHash;
        NativeWitnessHash = witness.Fingerprint; RequiresScripts = witness.RequiresScripts;
        Issues = Array.AsReadOnly(Array.Empty<GeneratedTableIssue>());
        Payloads = Array.AsReadOnly((GeneratedTablePayload[])payloads.Clone());
        ProcedureSizeAdjustments = Array.AsReadOnly((GeneratedTableProcedureAdjustment[])adjustments.Clone());
        Requirements = Array.AsReadOnly((GeneratedTableRequirement[])requirements.Clone());
        PayloadHash = ParameterPatchSourceBinding.Hash(new
        {
            payloads = Payloads.Select(p => new { p.Role, p.Arena, p.ByteOffset, p.Origin, p.ResourceId, p.Length,
                data = p.Data.Sha256, writes = p.WriteMask.Sha256, before = p.BeforeData?.Sha256,
                beforeMask = p.BeforeMask?.Sha256, afterMask = p.AfterMask?.Sha256 }).ToArray(),
            adjustments = ProcedureSizeAdjustments,
        });
        Fingerprint = ParameterPatchSourceBinding.Hash(new
        {
            ProfileVersion, Format, SourceFingerprint, ApplicationDefinitionHash, ProjectInputHash,
            ProcedureWitnessHash, NativeWitnessHash, PayloadHash, RequiresScripts, request,
            requirements = Requirements,
        });
    }
}

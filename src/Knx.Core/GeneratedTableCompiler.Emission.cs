namespace Knx;

internal static partial class GeneratedTableCompiler
{
    private interface IEmissionMaterializer<TResult>
    {
        TResult Unavailable(GeneratedTableIssue issue, GeneratedTableFormat? format);
        TResult Success(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure, GeneratedTableRequest request,
            NativeCommunicationWitness witness, GeneratedTablePayload[] payloads,
            GeneratedTableProcedureAdjustment[] adjustments, List<GeneratedTableRequirement> requirements);
    }

    private sealed class LegacyEmissionMaterializer : IEmissionMaterializer<GeneratedTablePreview>
    {
        internal static readonly LegacyEmissionMaterializer Instance = new();
        private LegacyEmissionMaterializer() { }
        public GeneratedTablePreview Unavailable(GeneratedTableIssue issue, GeneratedTableFormat? format)
            => GeneratedTablePreview.Unavailable(issue, format);
        public GeneratedTablePreview Success(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure,
            GeneratedTableRequest request, NativeCommunicationWitness witness, GeneratedTablePayload[] payloads,
            GeneratedTableProcedureAdjustment[] adjustments, List<GeneratedTableRequirement> requirements)
        {
            var binding = new GeneratedTableSourceBinding(sources, procedure, request, witness, payloads, adjustments);
            return new(request.Format, witness.Fingerprint!, binding, payloads, adjustments, requirements.ToArray());
        }
    }

    private sealed class SharedEmissionMaterializer : IEmissionMaterializer<SharedGeneratedTableEmission>
    {
        internal static readonly SharedEmissionMaterializer Instance = new();
        private SharedEmissionMaterializer() { }
        public SharedGeneratedTableEmission Unavailable(GeneratedTableIssue issue, GeneratedTableFormat? format)
            => new(issue, format);
        public SharedGeneratedTableEmission Success(ArchivedImageSourceBinding sources, LoadProcedurePlan procedure,
            GeneratedTableRequest request, NativeCommunicationWitness witness, GeneratedTablePayload[] payloads,
            GeneratedTableProcedureAdjustment[] adjustments, List<GeneratedTableRequirement> requirements)
            => new(sources, procedure, request, witness, payloads, adjustments, requirements.ToArray());
    }
}

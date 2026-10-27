namespace Knx;

/// <summary>A sealed, detached parse of all candidate declarations, before controller selection.</summary>
internal sealed class PreparedConditionalLoadProcedure
{
    internal bool IsAvailable => Issues.Count == 0 && Graph is not null && GraphFingerprint is not null;
    internal IReadOnlyList<LoadProcedureIssue> Issues { get; }
    internal bool HasConditions { get; }
    internal bool RequiresExtendedProfile { get; }
    internal IReadOnlyList<string> ControllerReferences { get; }
    internal IReadOnlyList<string> DeclaredControllerReferences { get; }
    internal string? SourceFingerprint { get; }
    internal string? ApplicationDefinitionHash { get; }
    internal string? SelectedProfileHash => Graph?.SelectedProfileHash;
    internal string? GraphFingerprint { get; }
    internal string? ApplicationId => Graph?.ApplicationId;
    internal string? MaskVersionId => Graph?.MaskVersionId;
    internal LoadProcedureStyle? Style => Graph?.Style;
    internal LoadProcedureRequest? Request { get; }
    internal PreparedLoadGraph? Graph { get; }
    internal string? MasterDefinitionHash { get; }

    private PreparedConditionalLoadProcedure(LoadProcedureIssue issue)
    {
        Issues = Array.AsReadOnly(new[] { issue });
        ControllerReferences = Array.AsReadOnly(Array.Empty<string>());
        DeclaredControllerReferences = Array.AsReadOnly(Array.Empty<string>());
    }
    private PreparedConditionalLoadProcedure(PreparedLoadGraph graph, string source, string applicationHash, string? masterHash,
        string graphHash, LoadProcedureRequest request, bool conditions, bool extended,
        IReadOnlyList<string> requested, IReadOnlyList<string> declared)
    {
        Graph = graph; SourceFingerprint = source; ApplicationDefinitionHash = applicationHash; MasterDefinitionHash = masterHash;
        GraphFingerprint = graphHash; Request = request with { }; HasConditions = conditions; RequiresExtendedProfile = extended;
        ControllerReferences = Array.AsReadOnly(requested.ToArray()); DeclaredControllerReferences = Array.AsReadOnly(declared.ToArray());
        Issues = Array.AsReadOnly(Array.Empty<LoadProcedureIssue>());
    }
    internal static PreparedConditionalLoadProcedure Unavailable(LoadProcedureIssue issue) => new(issue);
    internal static PreparedConditionalLoadProcedure Seal(PreparedLoadGraph graph, string source, string applicationHash,
        string? masterHash, string graphHash, LoadProcedureRequest request, bool conditions, bool extended,
        IReadOnlyList<string> requested, IReadOnlyList<string> declared)
        => new(graph, source, applicationHash, masterHash, graphHash, request, conditions, extended, requested, declared);
    internal bool MatchesSources(ArchivedImageSources.Inputs inputs) => LoadProcedureCompiler.MatchesPreparedSources(this, inputs);
}

internal sealed class PreparedLoadSource(LoadProcedureSourceOrigin origin, IReadOnlyList<int> path, int ordinal)
{
    internal LoadProcedureSourceOrigin Origin { get; } = origin;
    internal IReadOnlyList<int> Path { get; } = Array.AsReadOnly(path.ToArray());
    internal int Ordinal { get; } = ordinal;
}
internal abstract class PreparedLoadNode(PreparedLoadSource source)
{
    internal PreparedLoadSource Source { get; } = source;
}
internal sealed class PreparedLoadAction(PreparedLoadSource source, LoadControlKind kind, LoadControlCondition condition,
    IReadOnlyList<LoadControlArgument> arguments, IReadOnlyList<LoadControlErrorPolicy> errors,
    ImmutablePatchBytes? data, ImmutablePatchBytes? mask) : PreparedLoadNode(source)
{
    internal LoadControlKind Kind { get; } = kind;
    internal LoadControlCondition Condition { get; } = condition;
    internal IReadOnlyList<LoadControlArgument> Arguments { get; } = Array.AsReadOnly(arguments.ToArray());
    internal IReadOnlyList<LoadControlErrorPolicy> Errors { get; } = Array.AsReadOnly(errors.ToArray());
    internal ImmutablePatchBytes? Data { get; } = data;
    internal ImmutablePatchBytes? Mask { get; } = mask;
}
internal sealed class PreparedLoadBranch(PreparedLoadSource source, bool fallback, string? rawTest,
    StrictIntegerCondition? test, IReadOnlyList<PreparedLoadNode> children)
{
    internal PreparedLoadSource Source { get; } = source;
    internal bool Default { get; } = fallback;
    internal string? RawTest { get; } = rawTest;
    internal StrictIntegerCondition? Test { get; } = test;
    internal IReadOnlyList<PreparedLoadNode> Children { get; } = Array.AsReadOnly(children.ToArray());
}
internal sealed class PreparedLoadChoose(PreparedLoadSource source, string reference, IReadOnlyList<PreparedLoadBranch> branches)
    : PreparedLoadNode(source)
{
    internal string ReferenceId { get; } = reference;
    internal IReadOnlyList<PreparedLoadBranch> Branches { get; } = Array.AsReadOnly(branches.ToArray());
}
internal sealed class PreparedLoadGraph(string application, string mask, LoadProcedureStyle style, string? profileHash,
    IReadOnlyDictionary<int, IReadOnlyList<PreparedLoadNode>> groups, IReadOnlyList<PreparedLoadNode> source,
    IReadOnlyList<LoadProcedureResourceReference> resources, IReadOnlyList<LoadProcedureTableRequirement> tables,
    IReadOnlyList<LoadProcedureRequirement> requirements, LoadProcedurePlan? legacy)
{
    internal string ApplicationId { get; } = application;
    internal string MaskVersionId { get; } = mask;
    internal LoadProcedureStyle Style { get; } = style;
    internal string? SelectedProfileHash { get; } = profileHash;
    internal IReadOnlyDictionary<int, IReadOnlyList<PreparedLoadNode>> Groups { get; }
        = new System.Collections.ObjectModel.ReadOnlyDictionary<int, IReadOnlyList<PreparedLoadNode>>(groups.ToDictionary(p => p.Key,
            p => (IReadOnlyList<PreparedLoadNode>)Array.AsReadOnly(p.Value.ToArray())));
    internal IReadOnlyList<PreparedLoadNode> Source { get; } = Array.AsReadOnly(source.ToArray());
    internal IReadOnlyList<LoadProcedureResourceReference> Resources { get; } = Array.AsReadOnly(resources.ToArray());
    internal IReadOnlyList<LoadProcedureTableRequirement> Tables { get; } = Array.AsReadOnly(tables.ToArray());
    internal IReadOnlyList<LoadProcedureRequirement> Requirements { get; } = Array.AsReadOnly(requirements.ToArray());
    internal LoadProcedurePlan? Legacy { get; } = legacy;
}

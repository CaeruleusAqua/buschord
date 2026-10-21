namespace Knx.Owl;

/// <summary>
/// Semantic checks of the project's KIM export against the shipped ontology.
/// Findings use the same <see cref="CheckFinding"/> shape as <see cref="ProjectCheck"/>.
/// </summary>
public static class KimCheck
{
    public static IReadOnlyList<CheckFinding> Run(Project project, Ontology ontology)
    {
        var kim = KimGraph.Build(project, ontology);
        var findings = new List<CheckFinding>();
        var points = kim.FunctionPoints.Select(p => p.Subject).ToHashSet(StringComparer.Ordinal);

        foreach (var point in kim.FunctionPoints)
        {
            var path = point.GroupAddressRaw is uint raw && raw is > 0 and <= 0xFFFF
                ? new GroupAddress((ushort)raw).ToString()
                : point.Subject;

            if (point.DatapointType is null)
            {
                findings.Add(point.DatapointTypeCode is null
                    ? new(CheckSeverity.Warning, "K003", path,
                        Strings.Get("KimMissingDpt", point.Label ?? point.Subject))
                    : new(CheckSeverity.Error, "K001", path,
                        Strings.Get("KimUnknownDpt", point.DatapointTypeCode)));
            }
            else
            {
                CheckDatapointType(point, path, findings);
            }
        }

        foreach (var device in kim.Devices)
        {
            var path = device.Address?.ToString() ?? device.Subject;
            if (string.IsNullOrWhiteSpace(device.ProductRefId))
                findings.Add(new(CheckSeverity.Warning, "K002", path,
                    Strings.Get("KimNoProduct", device.Label ?? device.Subject)));
        }

        foreach (var link in kim.PointLinks)
            if (!points.Contains(link.Target))
                findings.Add(new(CheckSeverity.Error, "K005", link.Subject,
                    Strings.Get("KimMissingPoint", link.Target)));

        return findings;
    }

    private static void CheckDatapointType(KimFunctionPoint point, string path, List<CheckFinding> findings)
    {
        var dpt = point.DatapointType!;

        if (point.DeclaredMajor is int declared && declared != dpt.Major)
            findings.Add(new(CheckSeverity.Warning, "K004", path,
                Strings.Get("KimWrongMajor", point.DatapointTypeCode, dpt.Major, declared)));

        if (dpt is { FieldBits: int fieldBits, BitSize: int bitSize } && fieldBits > bitSize)
            findings.Add(new(CheckSeverity.Warning, "K004", path,
                Strings.Get("KimTooManyBits", dpt.Code, dpt.FieldCount, fieldBits, bitSize)));
    }
}

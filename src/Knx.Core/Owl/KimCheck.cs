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
            var path = point.GroupAddressRaw is uint raw && raw is > 0 and <= 0x7FFF
                ? new GroupAddress((ushort)raw).ToString()
                : point.Subject;

            if (point.DatapointType is null)
            {
                findings.Add(point.DatapointTypeCode is null
                    ? new(CheckSeverity.Warning, "K003", path,
                        $"Funktionspunkt '{point.Label ?? point.Subject}' hat keinen Datapoint-Typ.")
                    : new(CheckSeverity.Error, "K001", path,
                        $"Datapoint-Typ '{point.DatapointTypeCode}' existiert nicht in der Ontologie."));
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
                    $"Gerät '{device.Label ?? device.Subject}' hat kein Produkt."));
        }

        foreach (var link in kim.PointLinks)
            if (!points.Contains(link.Target))
                findings.Add(new(CheckSeverity.Error, "K005", link.Subject,
                    $"Verknüpfung core:hasPoint zeigt auf keinen existierenden Funktionspunkt '{link.Target}'."));

        return findings;
    }

    private static void CheckDatapointType(KimFunctionPoint point, string path, List<CheckFinding> findings)
    {
        var dpt = point.DatapointType!;

        if (point.DeclaredMajor is int declared && declared != dpt.Major)
            findings.Add(new(CheckSeverity.Warning, "K004", path,
                $"Datapoint-Typ '{point.DatapointTypeCode}' gehört zu Haupttyp {dpt.Major}, exportiert ist {declared}."));

        if (dpt is { FieldBits: int fieldBits, BitSize: int bitSize } && fieldBits > bitSize)
            findings.Add(new(CheckSeverity.Warning, "K004", path,
                $"Datapoint-Typ '{dpt.Code}' hat {dpt.FieldCount} Datenfeld(er) mit {fieldBits} bit, " +
                $"knx:dptBitSizeMax erlaubt nur {bitSize} bit."));
    }
}

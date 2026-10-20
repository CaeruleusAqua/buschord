using System.Globalization;
using System.Text;

namespace Knx;

/// <summary>Input of the "new project" wizard.</summary>
public sealed class NewProjectOptions
{
    public string Name { get; set; } = "Neues Projekt";
    public string? Comment { get; set; }
    public GroupAddressStyle GroupAddressStyle { get; set; } = GroupAddressStyle.ThreeLevel;
    public int Areas { get; set; } = 1;
    public int LinesPerArea { get; set; } = 1;
    public bool CreateAreaAndLineNames { get; set; } = true;
    public uint MasterDataVersion { get; set; } = 198;
}

public sealed record ImportResult(int Imported, int Skipped, IReadOnlyList<string> Errors);

public static class ProjectWizard
{
    /// <summary>Creates an empty project with the requested topology and group ranges.</summary>
    public static Project Create(NewProjectOptions options)
    {
        var project = new Project
        {
            Id = "P-" + Random.Shared.Next(1, 9999).ToString("D4", CultureInfo.InvariantCulture),
            Name = options.Name,
            Comment = options.Comment,
            ProjectStart = DateTimeOffset.Now,
            Guid = Guid.NewGuid().ToString(),
            MasterData = new MasterDataInfo { Version = options.MasterDataVersion },
        };

        var installation = new Installation
        {
            Id = "I-1",
            Name = options.Name,
            GroupAddressStyle = options.GroupAddressStyle,
            Address = new IndividualAddress(1, 1, 0),
        };

        for (var a = 0; a < Math.Max(1, options.Areas); a++)
        {
            var area = new Area
            {
                Id = "A-" + a,
                Address = (byte)a,
                Name = options.CreateAreaAndLineNames ? $"Bereich {a}" : null,
            };
            for (var l = 0; l < Math.Max(1, options.LinesPerArea); l++)
                area.Lines.Add(new Line
                {
                    Id = $"L-{a}-{l}",
                    Address = (byte)l,
                    Name = options.CreateAreaAndLineNames ? $"Linie {a}.{l}" : null,
                });
            installation.Areas.Add(area);
        }

        if (options.GroupAddressStyle == GroupAddressStyle.ThreeLevel)
        {
            var main = new GroupRange { Id = "GR-1", Name = "Hauptbereich 1", RangeStart = 0, RangeEnd = 2047 };
            var middle = new GroupRange { Id = "GR-2", Name = "Mittelbereich 1", RangeStart = 0, RangeEnd = 255 };
            main.Ranges.Add(middle);
            installation.MainRanges.Add(main);
        }
        else
        {
            installation.MainRanges.Add(new GroupRange
            {
                Id = "GR-1", Name = "Gruppen", RangeStart = 0, RangeEnd = 0xFFFF,
            });
        }

        project.Installations.Add(installation);
        return project;
    }

    /// <summary>
    /// Imports a group address list (ETS CSV export or plain "1/2/3;Name;DPT-1").
    /// Existing addresses are skipped, unknown middle/main ranges are created.
    /// </summary>
    public static ImportResult ImportGroupAddresses(Project project, Installation installation, string content)
    {
        var errors = new List<string>();
        var imported = 0;
        var skipped = 0;
        var existing = ProjectQuery.GroupAddresses(project).Select(a => a.Address.Raw).ToHashSet();
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;

            var columns = SplitCsv(line);
            if (columns.Count == 0) continue;
            if (lineNumber == 1 && !columns[0].Contains('/')) continue; // header row

            if (!TryParseAddress(columns[0], out var address))
            {
                errors.Add($"Zeile {lineNumber}: '{columns[0]}' ist keine Gruppenadresse.");
                continue;
            }
            if (!existing.Add(address.Raw))
            {
                skipped++;
                continue;
            }

            var entry = new GroupAddressEntry
            {
                Id = NextAddressId(project),
                Name = columns.Count > 1 && columns[1].Length > 0 ? columns[1] : address.ToString(),
                Address = address,
                DatapointType = columns.Count > 2 && columns[2].Length > 0 ? NormaliseDpt(columns[2]) : null,
                Description = columns.Count > 3 && columns[3].Length > 0 ? columns[3] : null,
            };

            var range = EnsureRange(installation, address);
            range.Addresses.Add(entry);
            imported++;
        }

        return new ImportResult(imported, skipped, errors);
    }

    private static GroupRange EnsureRange(Installation installation, GroupAddress address)
    {
        var main = installation.MainRanges.FirstOrDefault(r => r.RangeStart == address.Main);
        if (main is null)
        {
            main = new GroupRange
            {
                Id = $"GR-{installation.MainRanges.Count + 1}",
                Name = $"Hauptbereich {address.Main}",
                RangeStart = address.Main,
                RangeEnd = address.Main,
            };
            installation.MainRanges.Add(main);
        }

        var middle = main.Ranges.FirstOrDefault(r => r.RangeStart == address.Middle);
        if (middle is null)
        {
            middle = new GroupRange
            {
                Id = $"{main.Id}-{address.Middle}",
                Name = $"Mittelbereich {address.Main}/{address.Middle}",
                RangeStart = address.Middle,
                RangeEnd = address.Middle,
            };
            main.Ranges.Add(middle);
        }
        return middle;
    }

    private static string NextAddressId(Project project)
    {
        var max = ProjectQuery.GroupAddresses(project)
            .Select(a => int.TryParse(a.Id.Replace("GA-", ""), out var n) ? n : 0)
            .DefaultIfEmpty(0).Max();
        return $"GA-{max + 1}";
    }

    /// <summary>
    /// Normalises a datapoint type to the ETS form DPST-&lt;major&gt;-&lt;minor&gt;.
    /// Accepts "DPST-1-1", "DPT-1-1", "1.001", "1.1" and a bare major ("DPT-1" → DPST-1-1).
    /// </summary>
    public static string NormaliseDpt(string value)
    {
        var text = value.Trim().ToUpperInvariant().Replace(" ", "");
        if (text.StartsWith("DPST-", StringComparison.Ordinal)) return text;
        if (text.StartsWith("DPT-", StringComparison.Ordinal)) text = text[4..];

        var parts = text.Split('.', '-');
        var major = parts[0];
        var minor = parts.Length > 1 ? parts[1].TrimStart('0') : "1";
        if (minor.Length == 0) minor = "0";
        return $"DPST-{major}-{minor}";
    }

    private static bool TryParseAddress(string value, out GroupAddress address)
    {
        address = default;
        try
        {
            address = GroupAddress.Parse(value.Trim());
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Minimal CSV splitter: handles ";" and "," and double quotes.</summary>
    internal static List<string> SplitCsv(string line)
    {
        var separator = line.Contains(';') ? ';' : ',';
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else current.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { fields.Add(current.ToString().Trim()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString().Trim());
        return fields;
    }
}

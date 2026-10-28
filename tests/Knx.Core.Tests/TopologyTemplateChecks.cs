using Knx;

namespace Knx.Core.Tests;

internal static class TopologyTemplateChecks
{
    internal static void Run(Action<string, bool, string?> check)
    {
        var legacy = ProjectWizard.Create(new());
        var tp = ProjectWizard.Create(new() { TopologyTemplate = NewProjectTopologyTemplate.TpFromOne, Areas = 2, LinesPerArea = 3 });
        var first = tp.Installations[0].Areas[0];
        check("topology default preserves zero-based API behavior", legacy.Installations[0].Areas[0].Address == 0 && legacy.Installations[0].Areas[0].Lines[0].Address == 0, null);
        check("topology TP template starts at 1.1 and preserves requested counts", first.Address == 1 && first.Lines[0].Address == 1 && tp.Installations[0].Areas.Count == 2 && first.Lines.Count == 3, null);
        check("topology template creates no implicit devices", !ProjectQuery.Devices(tp).Any(), null);
        check("topology TP first free device is 1.1.1", AddressStrategy.TryNextIndividual(tp.Installations[0], 1, 1, out var address) && address.ToString() == "1.1.1", null);
        var roundTrip = Knxproj.FromXml(Knxproj.ToXml(tp));
        check("topology TP survives native model serialization", roundTrip.Installations[0].Areas[0].Address == 1 && roundTrip.Installations[0].Areas[1].Lines[2].Address == 3, null);
        var maxLegacy = ProjectWizard.Create(new() { Areas = 16, LinesPerArea = 16 });
        check("topology legacy supports all sixteen address values", maxLegacy.Installations[0].Areas[^1].Address == 15 && maxLegacy.Installations[0].Areas[^1].Lines[^1].Address == 15, null);
        foreach (var options in new[]
        {
            new NewProjectOptions { Areas = 17 },
            new NewProjectOptions { LinesPerArea = int.MaxValue },
            new NewProjectOptions { TopologyTemplate = NewProjectTopologyTemplate.TpFromOne, Areas = 16 },
            new NewProjectOptions { TopologyTemplate = NewProjectTopologyTemplate.TpFromOne, LinesPerArea = 0 },
            new NewProjectOptions { TopologyTemplate = (NewProjectTopologyTemplate)99 },
        })
        {
            var rejected = false;
            try { ProjectWizard.Create(options); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check("topology rejects invalid address space or template: " + options.TopologyTemplate + "/" + options.Areas + "/" + options.LinesPerArea, rejected, null);
        }
    }
}

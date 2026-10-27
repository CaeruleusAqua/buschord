using Knx;

namespace Knx.Core.Tests;

internal static class WizardIdentityChecks
{
    internal static void Run(Action<string, bool, string?> check)
    {
        var project = ProjectWizard.Create(new NewProjectOptions { Name = "Identity check" });
        var installation = project.Installations[0];
        var originalRanges = ProjectQuery.AllRanges(installation).ToArray();
        var imported = ProjectWizard.ImportGroupAddresses(project, installation, "1/1/1;Switch;1.001\n1/2/1;Status;1.001");
        check("wizard import creates ranges without duplicate XML identities",
            imported.Imported == 2 && !ProjectCheck.Run(project).Any(f => f.Code == "P050"), null);
        check("wizard import preserves existing ranges",
            originalRanges.All(r => ProjectQuery.AllRanges(installation).Contains(r)), null);
        var reopened = Knxproj.FromXml(Knxproj.ToXml(project));
        check("wizard import identities survive XML round trip",
            ProjectQuery.GroupAddresses(reopened).Count() == 2
            && !ProjectCheck.Run(reopened).Any(f => f.Code == "P050"), null);

        var second = new Installation { Id = "I-SECOND", Name = "Second" };
        project.Installations.Add(second);
        second.MainRanges.Add(new GroupRange { Id = "GR-3", RangeStart = 0, RangeEnd = 0 });
        second.Locations.Roots.Add(new SpaceNode { Id = "GA-3", Name = "Reserved", Kind = SpaceKind.Room });
        var more = ProjectWizard.ImportGroupAddresses(project, second, "2/3/4;Additional;1.001");
        check("wizard import avoids identities used by other installations and element kinds",
            more.Imported == 1 && !ProjectCheck.Run(project).Any(f => f.Code == "P050"), null);
        var count = ProjectQuery.AllRanges(second).Count();
        var repeated = ProjectWizard.ImportGroupAddresses(project, second, "2/3/4;Duplicate\n2/3/5;Next");
        check("wizard repeated imports reuse ranges and skip existing addresses",
            repeated.Imported == 1 && repeated.Skipped == 1
            && ProjectQuery.AllRanges(second).Count() == count
            && !ProjectCheck.Run(project).Any(f => f.Code == "P050"), null);

        var simultaneous = ProjectWizard.Create(new NewProjectOptions { Name = "Shared ID namespace" });
        var target = simultaneous.Installations[0];
        var existing = target.MainRanges[0].Ranges[0];
        existing.Addresses.Add(new GroupAddressEntry { Id = "GA-1", Name = "First", Address = GroupAddress.Parse("0/0/1") });
        existing.Addresses.Add(new GroupAddressEntry { Id = "GA-2", Name = "Second", Address = GroupAddress.Parse("0/0/2") });
        target.MainRanges.Add(new GroupRange { Id = "GA", Name = "Shared prefix", RangeStart = 2, RangeEnd = 2 });
        var sameOperation = ProjectWizard.ImportGroupAddresses(simultaneous, target, "2/3/4;New");
        check("wizard reserves newly created range identities before allocating the address identity",
            sameOperation.Imported == 1 && !ProjectCheck.Run(simultaneous).Any(f => f.Code == "P050"), null);
    }
}

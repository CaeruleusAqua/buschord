using Knx;
using Knx.Workbench;

internal static class MassLinkReviewChecks
{
    internal static void Run(Action<string, bool> check)
    {
        void Check(string name, bool value) => check("mass review: " + name, value);
        Check("legacy Suggest four-argument binary signature remains available", typeof(SmartLinking).GetMethod(nameof(SmartLinking.Suggest),
            [typeof(Project), typeof(Func<DeviceInstance, ComObjectInstanceRef, string>), typeof(int), typeof(int)]) is not null);
        Check("legacy AddLinkPairs three-argument binary signature remains available", typeof(WorkbenchProject).GetMethod(nameof(WorkbenchProject.AddLinkPairs),
            [typeof(Project), typeof(IEnumerable<LinkCheckRequest>), typeof(UndoStack)]) is not null);
        var project = Fixture(); var identity = new object(); var undo = new UndoStack(); undo.Reset(project);
        var before = Knxproj.ToXml(project).ToString();
        var plan = MassLinkReview.Preview(project, identity);
        MassLinkReviewRow Row(string device) => plan.Rows.Single(row => row.Key.DeviceId == device);
        Check("all unlinked objects are represented without mutation", plan.Rows.Count == 5 && before == Knxproj.ToXml(project).ToString() && !undo.CanUndo);
        Check("unique compatible candidate is ready", Row("Alpha").State == MassLinkReviewState.Ready && Row("Alpha").SelectedGroupId == "G-Alpha");
        Check("equal top scores remain ambiguous", Row("Beta").State == MassLinkReviewState.Ambiguous && Row("Beta").SelectedGroupId is null);
        Check("known conflicting named candidate is incompatible", Row("Gamma").State == MassLinkReviewState.Incompatible && !Row("Gamma").CanApply);
        Check("unmatched object remains visible", Row("Delta").State == MassLinkReviewState.Unmatched && Row("Delta").Candidates.Count == 0);
        Check("missing facts are unverified rather than compatible", Row("Epsilon").State == MassLinkReviewState.Unverified && !Row("Epsilon").CanApply);
        var badSelection = false;
        try { MassLinkReview.SelectTargets(plan, project, identity, [new(Row("Alpha").Id, "G-Beta1"), new("foreign-row", "G-Alpha")]); }
        catch (ArgumentException) { badSelection = true; }
        Check("bad target batch does not partially edit the review", badSelection && Row("Alpha").SelectedGroupId == "G-Alpha");
        MassLinkReview.SelectTarget(plan, project, identity, Row("Beta").Id, "G-Beta2");
        Check("explicit target resolves ambiguity using the same validator", Row("Beta").CanApply && Row("Beta").SelectedGroupId == "G-Beta2");
        var mixed = MassLinkReview.Apply(plan, project, identity, undo, [Row("Alpha").Id, Row("Gamma").Id]);
        Check("mixed incompatible batch is atomic", !mixed.Ok && !undo.CanUndo && before == Knxproj.ToXml(project).ToString());
        var applied = MassLinkReview.Apply(plan, project, identity, undo, [Row("Alpha").Id, Row("Beta").Id]);
        Check("selected ready rows commit in one undo step", applied.Ok && applied.Changed == 2 && undo.Steps == 1
            && ProjectQuery.DeviceList(project).Single(device => device.Id == "Beta").ComObjectRefs.Single().Links.SequenceEqual(["G-Beta2"]));
        Check("one undo restores the complete project", undo.Undo() is { } restored && Knxproj.ToXml(restored).ToString() == before);
        var rejected = false;
        try { MassLinkReview.Apply(plan, project, identity, undo); } catch (InvalidOperationException) { rejected = true; }
        Check("applied review is single use", rejected);

        project = Fixture(); identity = new(); undo = new(); undo.Reset(project); plan = MassLinkReview.Preview(project, identity);
        rejected = false;
        try { MassLinkReview.Apply(plan, project, new object(), undo); } catch (InvalidOperationException) { rejected = true; }
        Check("foreign session is rejected", rejected && !undo.CanUndo);
        project.Name = "Source changed";
        rejected = false;
        try { MassLinkReview.Apply(plan, project, identity, undo); } catch (InvalidOperationException) { rejected = true; }
        Check("source changes invalidate the review", rejected && !undo.CanUndo && ProjectQuery.ComObjectRefs(project).All(pair => pair.Ref.Links.Count == 0));

        project = Fixture(); identity = new(); undo = new(); undo.Reset(project); plan = MassLinkReview.Preview(project, identity);
        var original = ProjectQuery.DeviceList(project).First();
        var replacement = ProjectQuery.DeviceList(Knxproj.FromXml(Knxproj.ToXml(project))).First();
        project.Installations[0].Areas[0].Lines[0].Devices[0] = replacement;
        rejected = false;
        try { MassLinkReview.Apply(plan, project, identity, undo, [plan.Rows.Single(row => row.Key.DeviceId == original.Id).Id]); }
        catch (InvalidOperationException) { rejected = true; }
        Check("identical-looking replacement identities cannot reuse approval", rejected && !undo.CanUndo);

        project = Fixture();
        var alpha = new ObjectKey("Alpha", "O1");
        var metadata = WorkbenchProject.Metadata(project, alpha)!;
        metadata.Dpt = null; metadata.Bits = 8;
        Check("known bit conflict survives other missing facts", WorkbenchProject.CheckLink(project, alpha, "G-Alpha").Reason == LinkValidationReason.BitSize);
        metadata.Dpt = "9.001"; metadata.Bits = null;
        Check("known DPT conflict survives missing bit size", WorkbenchProject.CheckLink(project, alpha, "G-Alpha").Reason == LinkValidationReason.LongMainType);
        metadata.Dpt = null; project.Workbench.Groups["G-Alpha"].Secure = true;
        Check("secure target requires explicit device capability", WorkbenchProject.CheckLink(project, alpha, "G-Alpha").Reason == LinkValidationReason.SecureCapability);
        project.Workbench.Groups["G-Alpha"].Secure = false;
        undo = new(); undo.Reset(project);
        var request = new LinkCheckRequest(alpha, "G-Alpha");
        var strict = WorkbenchProject.AddLinkPairs(project, [request], undo);
        Check("strict policy refuses unknown facts", !strict.Ok && !undo.CanUndo);
        var manual = WorkbenchProject.AddLinkPairs(project, [request], undo, allowUnverified: true);
        Check("explicit manual policy reports missing evidence", manual.Ok && manual.Changed == 1 && manual.Warnings.Count == 1 && undo.Steps == 1);
    }

    private static Project Fixture()
    {
        var project = ProjectWizard.Create(new() { Name = "Synthetic mass review" });
        project.Installations[0].Areas[0].Address = 1; project.Installations[0].Areas[0].Lines[0].Address = 1;
        var range = new GroupRange { Id = "GR-Mass", Name = "Synthetic", RangeStart = 0, RangeEnd = 65535 };
        project.Installations[0].MainRanges.Add(range);
        var address = 1;
        foreach (var (id, name, dpt, bits) in new[]
        {
            ("G-Alpha", "Alpha AlphaValue", "1.001", 1),
            ("G-Beta1", "Beta BetaValue", "1.001", 1), ("G-Beta2", "Beta BetaValue", "1.001", 1),
            ("G-Gamma", "Gamma GammaValue", "9.001", 16), ("G-Epsilon", "Epsilon EpsilonValue", "1.001", 1),
        })
        {
            range.Addresses.Add(new() { Id = id, Name = name, DatapointType = dpt, Address = GroupAddress.ThreeLevel(1, 0, (byte)address++) });
            project.Workbench.Groups[id] = new() { Bits = bits, Secure = false };
        }
        address = 1;
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon" })
        {
            var device = new DeviceInstance { Id = name, Name = name, ProductRefId = "SYNTHETIC", Address = new(1, 1, (byte)address++) };
            device.ComObjectRefs.Add(new() { Id = "O1", ComObjectId = "O1", Number = 1, Text = name + "Value" });
            project.Installations[0].Areas[0].Lines[0].Devices.Add(device);
            project.Workbench.Objects[new ObjectKey(name, "O1").ToString()] = new()
            {
                Active = true, Dpt = name == "Epsilon" ? null : name == "Delta" ? "5.001" : "1.001", Bits = name == "Epsilon" ? null : 1,
            };
        }
        return project;
    }
}

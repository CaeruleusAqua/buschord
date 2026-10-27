using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Knx;

internal static class ResolvedApplicationBuildChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Request = new("all", LoadProcedureAccess.Remote, LoadParts.Full);
    private static XDocument Application(bool conditional = true)
    {
        var doc = ArchivedImageSourceChecks.App();
        var app = doc.Descendants(Ns + "ApplicationProgram").Single(); app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        app.Element(Ns + "Static")!.Add(XElement.Parse($$"""
            <LoadProcedures xmlns="{{Ns}}"><LoadProcedure><LdCtrlConnect/>
              <choose ParamRefId="R"><when test="1"><LdCtrlWriteProp ObjIdx="4" PropId="27" Verify="false" InlineData="12"/></when>
                <when default="true"><LdCtrlRestart/></when></choose><LdCtrlDisconnect/>
            </LoadProcedure></LoadProcedures>
            """));
        if (!conditional) app.Descendants(Ns + "choose").Single().ReplaceWith(new XElement(Ns + "LdCtrlRestart"));
        return doc;
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "ResolvedApplicationBuild " + name); if (!ok) failures++; }
        var f = ArchivedImageSourceChecks.Load(directory, app: Application());
        var originalHash = SHA256.HashData(File.ReadAllBytes(f.Path)); var originalXml = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var revision = f.Project.ApplicationDataRevision; var undo = new UndoStack(); undo.Reset(f.Project);
        var plan = OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, Request); var binding = plan.Binding;
        Check("explicit root condition yields a separate bound procedure preview", plan.IsAvailable && plan.ProfileVersion == 2
            && plan.Procedure is { ProfileVersion: 2, Controls.Count: 3, ConditionalSelection.Decisions.Count: 1 }
            && plan.Procedure.Controls[1].Kind == LoadControlKind.WriteProperty && plan.Procedure.Controls[1].InlineData?.ToHexString() == "12");
        Check("source graph context and saved-input hashes remain distinct binding inputs", binding is { ProfileVersion: 1, Fingerprint.Length: 64,
            SourceFingerprint.Length: 64, ProcedureGraphHash.Length: 64, EvaluationContextHash.Length: 64, ProjectInputHash.Length: 64 }
            && binding.ProcedureWitnessHash == plan.Procedure?.WitnessHash && binding.IsCurrent(f.Project, f.Device)
            && binding.Fingerprint == OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, Request).Binding?.Fingerprint);
        Check("no execution image or download authority is granted", !plan.ExecutionEligible && !plan.CompleteApplicationImage && !plan.DownloadEligible
            && plan.Procedure is { ExecutionEligible: false, CompleteApplicationImage: false, DownloadEligible: false }
            && plan.Procedure.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.GeneratedTablesNotBuilt));
        var old = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Request);
        Check("ordinary route still refuses a conditional declaration", !old.IsAvailable && old.Binding is null);
        f.Device.ParameterValues["R"] = "2";
        var changed = OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, Request);
        Check("saved value selects default and invalidates the original binding", changed.IsAvailable && binding?.IsCurrent(f.Project, f.Device) == false
            && changed.Procedure?.Controls[1].Kind == LoadControlKind.Restart && changed.Procedure.ConditionalSelection?.Decisions.Single().UsedDefault == true
            && changed.Binding?.SourceFingerprint == binding?.SourceFingerprint && changed.Binding?.ProcedureGraphHash == binding?.ProcedureGraphHash
            && changed.Binding?.EvaluationContextHash != binding?.EvaluationContextHash && changed.Binding?.Fingerprint != binding?.Fingerprint);
        f.Device.ParameterValues.Remove("R");
        Check("restoring exact saved inputs revalidates the binding", binding?.IsCurrent(f.Project, f.Device) == true);
        f.Device.ParameterValues["R"] = "999";
        var invalidValue = OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, Request);
        Check("invalid active value publishes no procedure or binding", !invalidValue.IsAvailable && invalidValue.Binding is null && invalidValue.Procedure is null
            && invalidValue.Issues.Single().Code == LoadProcedureIssueCode.InvalidControllerValue);
        f.Device.ParameterValues.Remove("R");
        var foreign = new DeviceInstance { Id = f.Device.Id, Address = f.Device.Address, ProductRefId = f.Device.ProductRefId, Hardware2ProgramRefId = f.Device.Hardware2ProgramRefId };
        Check("detached matching device cannot use the live binding", binding?.IsCurrent(f.Project, foreign) == false
            && !OfflineApplicationBuildPlan.PreviewResolved(f.Project, foreign, Request).IsAvailable);
        var reloaded = Knxproj.Load(f.Path); var reloadedDevice = ProjectQuery.DeviceList(reloaded).Single();
        Check("reload needs its own live binding", binding?.IsCurrent(reloaded, reloadedDevice) == false);
        // Requests are deliberately required: the public route cannot infer load parts.
        var fresh = OfflineApplicationBuildPlan.PreviewResolved(reloaded, reloadedDevice, Request);
        Check("reload independently recreates a current declaration preview", fresh.IsAvailable && fresh.Binding?.IsCurrent(reloaded, reloadedDevice) == true);
        var badApp = Application(); badApp.Descendants(Ns + "when").Last().Add(new XElement(Ns + "LdCtrlUnknown"));
        var bad = ArchivedImageSourceChecks.Load(directory, app: badApp);
        var refused = OfflineApplicationBuildPlan.PreviewResolved(bad.Project, bad.Device, Request);
        Check("malformed unselected branch is atomic before condition resolution", !refused.IsAvailable && refused.Binding is null && refused.Procedure is null
            && refused.Issues.Single().Code == LoadProcedureIssueCode.UnsupportedControl);
        foreach (var request in new LoadProcedureRequest?[] { null, Request with { NativeSubtype = "unknown" }, Request with { Access = (LoadProcedureAccess)99 } })
        {
            var invalid = OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, request!);
            Check("invalid explicit request has no artifacts", !invalid.IsAvailable && invalid.Binding is null && invalid.Procedure is null
                && invalid.Issues.Single().Code == LoadProcedureIssueCode.InvalidRequest);
        }
        var primitive = ArchivedImageSourceChecks.Load(directory, app: Application(false));
        var before = OfflineApplicationBuildPlan.Preview(primitive.Project, primitive.Device, Request);
        var resolved = OfflineApplicationBuildPlan.PreviewResolved(primitive.Project, primitive.Device, Request);
        Check("primitive resolved route preserves the complete old procedure JSON and witness", before.IsAvailable && resolved.IsAvailable
            && JsonSerializer.Serialize(before.Procedure) == JsonSerializer.Serialize(resolved.Procedure)
            && resolved.Procedure is { ProfileVersion: 1, ConditionalSelection: null });
        f.Device.BinaryData["unknown-state"] = [1];
        var unknownState = OfflineApplicationBuildPlan.PreviewResolved(f.Project, f.Device, Request); f.Device.BinaryData.Clear();
        Check("opaque device binary state prevents a conditional binding", !unknownState.IsAvailable && unknownState.Binding is null && unknownState.Procedure is null);
        Check("all successful and failed previews leave source model revision and history untouched", originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(f.Path)))
            && Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) == originalXml && f.Project.ApplicationDataRevision == revision
            && undo.Index == 0 && undo.Steps == 0 && !undo.CanUndo && !undo.IsDirty);
        Console.WriteLine($"ResolvedApplicationBuild: {checks - failures}/{checks} passed"); return failures;
    }
}

using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;
using Knx.Workbench;

internal static class ApplicationBuildChecks
{
    private static readonly XNamespace Ns = "http://knx.org/xml/project/20";
    private static readonly LoadProcedureRequest Parameters = new("all", LoadProcedureAccess.Remote, LoadParts.Parameters);
    private static XDocument Application()
    {
        var doc = ArchivedImageSourceChecks.App();
        var app = doc.Descendants(Ns + "ApplicationProgram").Single();
        app.SetAttributeValue("LoadProcedureStyle", "ProductProcedure");
        app.Element(Ns + "Static")!.Add(XElement.Parse($$"""
            <LoadProcedures xmlns="{{Ns}}"><LoadProcedure>
              <LdCtrlConnect/><LdCtrlWriteProp ObjIdx="4" PropId="27" Verify="false" InlineData="1234" AppliesTo="par"/><LdCtrlDisconnect/>
            </LoadProcedure></LoadProcedures>
            """));
        return doc;
    }
    private sealed record Facts(ArchivedImageSourceChecks.Fixture Fixture, ComObjectInstanceRef Object,
        ObjectMetadata Metadata, ConnectorMetadata Connector, GroupRange Range, GroupAddressEntry Group, GroupMetadata GroupMetadata);
    private static Facts Setup(string directory)
    {
        var f = ArchivedImageSourceChecks.Load(directory, app: Application());
        f.Device.ParameterValues["R"] = "1";
        var group = new GroupAddressEntry { Id = "GA-build", Name = "Build address", Address = new(2305), DatapointType = "1.001", Puid = 1 };
        var range = new GroupRange { Id = "GR-build", Name = "Build range", RangeStart = 1, RangeEnd = 65535 };
        range.Addresses.Add(group);
        range.Addresses.Add(new() { Id = "GA-second", Name = "Second address", Address = new(2307), DatapointType = "1.001", Puid = 2 });
        f.Project.Installations[0].MainRanges.Add(range);
        var obj = new ComObjectInstanceRef { Id = "R-build", ComObjectId = "O-build", Number = 1, Device = "true" };
        obj.Links.Add(group.Id); f.Device.ComObjectRefs.Add(obj);
        var connector = new ConnectorMetadata();
        var metadata = new ObjectMetadata { Dpt = "1.001", Bits = 1, Flags = "CRWT", Active = true };
        metadata.Connectors.Add(group.Id, connector); f.Project.Workbench.Objects.Add(new ObjectKey(f.Device.Id, obj.Id).ToString(), metadata);
        var groupMetadata = new GroupMetadata { Bits = 1, Secure = false };
        f.Project.Workbench.Groups.Add(group.Id, groupMetadata);
        f.Device.BinaryData.Add("config", [1, 2, 3]);
        return new(f, obj, metadata, connector, range, group, groupMetadata);
    }

    internal static int Run(string directory)
    {
        var failures = 0; var checks = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "ApplicationBuild " + name); if (!ok) failures++; }
        var facts = Setup(directory); var f = facts.Fixture;
        var sourceBytes = File.ReadAllBytes(f.Path); var sourceHash = SHA256.HashData(sourceBytes);
        var model = Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting);
        var revision = f.Project.ApplicationDataRevision;
        var history = new UndoStack(); history.Reset(f.Project); var undoIndex = history.Index;
        var result = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters);
        var binding = result.Binding; var source = result.Sources.Binding;
        Check("exact archived sources and explicit product controls form an available plan", result.IsAvailable
            && result.Sources.Status == ArchivedImageSourceStatus.Inspected && result.Sources.Hardware?.Status == ArchivedHardwareSourceStatus.Inspected
            && result.Procedure is { IsAvailable: true, Style: LoadProcedureStyle.ProductProcedure, Controls.Count: 3 }
            && result.Procedure.Controls.Select(c => c.Kind).SequenceEqual(new[] { LoadControlKind.Connect, LoadControlKind.WriteProperty, LoadControlKind.Disconnect }));
        Check("plan and binding have separate source input and procedure hashes", binding is { ProfileVersion: 1, SourceFingerprint.Length: 64,
            ProjectInputHash.Length: 64, ProcedureWitnessHash.Length: 64, Fingerprint.Length: 64 }
            && binding.SourceFingerprint == source?.Fingerprint && binding.ProcedureWitnessHash == result.Procedure?.WitnessHash
            && binding.IsCurrent(f.Project, f.Device));
        Check("declared inline action bytes are preserved without becoming initial property data", result.Procedure?.Controls[1].InlineData?.ToHexString() == "1234"
            && result.Procedure.Controls[1].Arguments.Any(a => a.Name == "StartElement" && a.UnsignedValue == 1)
            && result.Procedure.Controls[1].Arguments.Any(a => a.Name == "Count" && a.UnsignedValue == 1));
        Check("every authority flag stays false", result.Scope == "OfflineApplicationBuildPlan" && result.ProfileVersion == 1
            && !result.ExecutionEligible && !result.CompleteApplicationImage && !result.DownloadEligible
            && result.Procedure is { ExecutionEligible: false, CompleteApplicationImage: false, DownloadEligible: false }
            && !result.Sources.CompleteApplicationImage && !result.Sources.DownloadEligible);
        Check("activation tables and finalization remain explicit unresolved work", result.Procedure?.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.ParameterActivationUnresolved) == true
            && result.Procedure.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.GeneratedTablesNotBuilt)
            && result.Procedure.Requirements.Any(r => r.Code == LoadProcedureRequirementCode.FinalizationNotCompiled));
        Check("same inputs produce the same binding", OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters).Binding?.Fingerprint == binding?.Fingerprint);
        Check("plan inspection leaves source model revision and undo unchanged", SHA256.HashData(File.ReadAllBytes(f.Path)).SequenceEqual(sourceHash)
            && Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) == model && f.Project.ApplicationDataRevision == revision
            && history.Index == undoIndex && history.Steps == 0 && !history.IsDirty && !history.CanUndo);
        Check("ordinary application build leaves generated tables absent", result.GeneratedTables is null);
        foreach (var invalid in new[]
        {
            OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters, new(GeneratedTableFormat.M112)),
            OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters with { Parts = LoadParts.Full }, new((GeneratedTableFormat)999)),
            OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters with { Parts = LoadParts.Full }, null!),
        })
            Check("invalid table requests have no partial build binding", !invalid.IsAvailable && invalid.Binding is null
                && invalid.Issues.Any(i => i.Code == LoadProcedureIssueCode.InvalidRequest));
        var unsupportedTables = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters with { Parts = LoadParts.Groups }, new(GeneratedTableFormat.M112));
        Check("unavailable requested tables retain no partial payload or application binding", !unsupportedTables.IsAvailable
            && unsupportedTables.Binding is null && unsupportedTables.GeneratedTables is { IsAvailable: false, Binding: null, Payloads.Count: 0, ProcedureSizeAdjustments.Count: 0 });
        Check("failed table opt-in leaves the ordinary procedure and binding stable", result.Binding?.IsCurrent(f.Project, f.Device) == true
            && OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters).Binding?.Fingerprint == binding?.Fingerprint
            && Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) == model && f.Project.ApplicationDataRevision == revision);

        var groups = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters with { Parts = LoadParts.Groups });
        Check("request filtering changes procedure witness and build binding while retaining source identity", groups.IsAvailable
            && groups.Procedure?.Controls.Count == 2 && groups.Procedure.Controls.All(c => c.Kind != LoadControlKind.WriteProperty)
            && groups.Binding?.Fingerprint != binding?.Fingerprint && groups.Binding?.ProcedureWitnessHash != binding?.ProcedureWitnessHash
            && groups.Binding?.SourceFingerprint == binding?.SourceFingerprint && groups.Binding?.ProjectInputHash == binding?.ProjectInputHash);
        Check("binding keeps its own immutable original request", binding?.IsCurrent(f.Project, f.Device) == true
            && Parameters.Parts == LoadParts.Parameters && result.Procedure?.Request?.Parts == LoadParts.Parameters);

        if (binding is not null && source is not null)
        {
            var mutations = new (string Name, Action Change, Action Restore)[]
            {
                ("parameter raw value", () => f.Device.ParameterValues["R"] = "2", () => f.Device.ParameterValues["R"] = "1"),
                ("communication object number", () => facts.Object.Number = 2, () => facts.Object.Number = 1),
                ("object flags", () => facts.Metadata.Flags = "CW", () => facts.Metadata.Flags = "CRWT"),
                ("object DPT", () => facts.Metadata.Dpt = "1.002", () => facts.Metadata.Dpt = "1.001"),
                ("object bit width", () => facts.Metadata.Bits = 2, () => facts.Metadata.Bits = 1),
                ("declared object activity fact", () => facts.Metadata.Active = false, () => facts.Metadata.Active = true),
                ("native object direction fact", () => facts.Object.Device = "false", () => facts.Object.Device = "true"),
                ("native channel pin", () => facts.Object.ChannelId = "C-build", () => facts.Object.ChannelId = null),
                ("group link", () => facts.Object.Links.Add("GA-second"), () => facts.Object.Links.Remove("GA-second")),
                ("sending connector", () => facts.Connector.Send = true, () => facts.Connector.Send = false),
                ("acknowledging connector", () => facts.Connector.Acknowledge = true, () => facts.Connector.Acknowledge = false),
                ("connector priority", () => facts.Connector.Priority = 3, () => facts.Connector.Priority = null),
                ("connector slot", () => facts.Connector.Slot = 2, () => facts.Connector.Slot = null),
                ("legacy sending group", () => facts.Metadata.SendingGroupId = facts.Group.Id, () => facts.Metadata.SendingGroupId = null),
                ("native group address", () => facts.Group.Address = new(2306), () => facts.Group.Address = new(2305)),
                ("native group DPT", () => facts.Group.DatapointType = "1.002", () => facts.Group.DatapointType = "1.001"),
                ("native group PUID", () => facts.Group.Puid = 3, () => facts.Group.Puid = 1),
                ("group bit width", () => facts.GroupMetadata.Bits = 2, () => facts.GroupMetadata.Bits = 1),
                ("group security fact", () => facts.GroupMetadata.Secure = true, () => facts.GroupMetadata.Secure = false),
                ("binary source content", () => f.Device.BinaryData["config"][0] = 4, () => f.Device.BinaryData["config"][0] = 1),
                ("another named binary source", () => f.Device.BinaryData.Add("second", [5]), () => f.Device.BinaryData.Remove("second")),
            };
            foreach (var mutation in mutations)
            {
                mutation.Change();
                try
                {
                    var changed = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters);
                    Check(mutation.Name + " stales build inputs without staling archive sources", changed.IsAvailable
                        && changed.Binding?.ProjectInputHash != binding.ProjectInputHash && changed.Binding?.Fingerprint != binding.Fingerprint
                        && changed.Binding?.SourceFingerprint == binding.SourceFingerprint && source.IsCurrent(f.Project, f.Device)
                        && !binding.IsCurrent(f.Project, f.Device));
                }
                finally { mutation.Restore(); }
            }
            Check("restoring exact declared inputs revalidates the original build binding", binding.IsCurrent(f.Project, f.Device));
            var foreign = new DeviceInstance { Id = f.Device.Id, ProductRefId = f.Device.ProductRefId, Hardware2ProgramRefId = f.Device.Hardware2ProgramRefId, Address = f.Device.Address };
            var detached = OfflineApplicationBuildPlan.Preview(f.Project, foreign, Parameters);
            Check("detached matching device cannot reuse live authority", !binding.IsCurrent(f.Project, foreign) && !detached.IsAvailable
                && detached.Binding is null && detached.Procedure is null && detached.Sources.Status == ArchivedImageSourceStatus.InvalidRequest);
            var reloaded = Knxproj.Load(f.Path); var reloadedDevice = ProjectQuery.DeviceList(reloaded).Single();
            Check("reloaded source needs a fresh live binding", !binding.IsCurrent(reloaded, reloadedDevice)
                && OfflineApplicationBuildPlan.Preview(reloaded, reloadedDevice, Parameters).Binding?.IsCurrent(reloaded, reloadedDevice) == true);

            facts.Range.Ranges.Add(facts.Range);
            try
            {
                var cyclic = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters);
                Check("cyclic range input is bounded and publishes no build artifacts", !cyclic.IsAvailable && cyclic.Binding is null && cyclic.Procedure is null
                    && cyclic.Issues.Single().Code == LoadProcedureIssueCode.LimitExceeded && source.IsCurrent(f.Project, f.Device));
            }
            finally { facts.Range.Ranges.Remove(facts.Range); }
            var deep = new GroupRange { Id = "Deep", Name = "Deep" }; facts.Range.Ranges.Add(deep);
            try
            {
                var bottom = deep;
                for (var i = 0; i < 129; i++) { var child = new GroupRange { Id = "Deep-" + i, Name = "Depth" }; bottom.Ranges.Add(child); bottom = child; }
                var excessive = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, Parameters);
                Check("range depth budget is an atomic refusal", !excessive.IsAvailable && excessive.Binding is null && excessive.Procedure is null
                    && excessive.Issues.Single().Code == LoadProcedureIssueCode.LimitExceeded);
            }
            finally { facts.Range.Ranges.Remove(deep); }
            Check("rejected graph inputs leave the restored model unchanged", binding.IsCurrent(f.Project, f.Device)
                && Knxproj.ToXml(f.Project).ToString(SaveOptions.DisableFormatting) == model && history.Index == undoIndex && history.Steps == 0);
        }
        else Check("valid binding fixture is available for input tests", false);

        foreach (var request in new LoadProcedureRequest?[] { null, new("unknown", LoadProcedureAccess.Remote, LoadParts.Parameters),
            new("all", (LoadProcedureAccess)99, LoadParts.Full), new("all", LoadProcedureAccess.Remote, (LoadParts)128) })
        {
            var bad = OfflineApplicationBuildPlan.Preview(f.Project, f.Device, request!);
            Check("invalid request publishes no procedure artifacts or binding", !bad.IsAvailable && bad.Binding is null
                && bad.Issues.Single().Code == LoadProcedureIssueCode.InvalidRequest
                && (bad.Procedure is null || bad.Procedure.Controls.Count == 0 && bad.Procedure.ResourceReferences.Count == 0 && bad.Procedure.TableRequirements.Count == 0));
        }
        var unsupported = Application(); unsupported.Descendants(Ns + "LoadProcedure").Single().Add(new XElement(Ns + "LdCtrlUnknown"));
        var unsupportedFixture = ArchivedImageSourceChecks.Load(directory, app: unsupported);
        var unsupportedPlan = OfflineApplicationBuildPlan.Preview(unsupportedFixture.Project, unsupportedFixture.Device, Parameters);
        Check("unsupported control retains source facts without partial build artifacts", !unsupportedPlan.IsAvailable && unsupportedPlan.Binding is null
            && unsupportedPlan.Sources.Status == ArchivedImageSourceStatus.Inspected && unsupportedPlan.Issues.Single().Code == LoadProcedureIssueCode.UnsupportedControl
            && unsupportedPlan.Procedure is { Controls.Count: 0, ResourceReferences.Count: 0, TableRequirements.Count: 0 });

        var mismatched = Application(); mismatched.Descendants(Ns + "ApplicationProgram").Single().SetAttributeValue("Id", "M-0001_A-other");
        var mismatchFixture = ArchivedImageSourceChecks.Load(directory, app: mismatched);
        var mismatch = OfflineApplicationBuildPlan.Preview(mismatchFixture.Project, mismatchFixture.Device, Parameters);
        Check("source entry and app identity mismatch prevents build authority", !mismatch.IsAvailable && mismatch.Binding is null && mismatch.Procedure is null
            && mismatch.Sources.Status == ArchivedImageSourceStatus.InvalidDeclaration);
        var absentApp = ArchivedImageSourceChecks.Load(directory, app: Application(), omitApp: true);
        var missingApp = OfflineApplicationBuildPlan.Preview(absentApp.Project, absentApp.Device, Parameters);
        Check("missing application source cannot become an empty successful plan", !missingApp.IsAvailable && missingApp.Binding is null && missingApp.Procedure is null
            && missingApp.Issues.Single().Code == LoadProcedureIssueCode.InvalidApplication);
        var absentHardware = ArchivedImageSourceChecks.Load(directory, app: Application(), omitHardware: true);
        var missingHardware = OfflineApplicationBuildPlan.Preview(absentHardware.Project, absentHardware.Device, Parameters);
        Check("declared hardware link with missing exact source refuses the build", !missingHardware.IsAvailable && missingHardware.Binding is null && missingHardware.Procedure is null
            && missingHardware.Sources.Hardware?.Status == ArchivedHardwareSourceStatus.PartMissing);
        var direct = ArchivedImageSourceChecks.Load(directory, app: Application()); direct.Device.Hardware2ProgramRefId = null;
        var directPlan = OfflineApplicationBuildPlan.Preview(direct.Project, direct.Device, Parameters);
        Check("explicit direct application keeps missing hardware separate from procedure declarations", directPlan.IsAvailable
            && directPlan.Sources.Hardware?.Status == ArchivedHardwareSourceStatus.ReferenceMissing && !directPlan.CompleteApplicationImage && !directPlan.ExecutionEligible);

        var undoFixture = Setup(directory); var undo = new UndoStack(); undo.Reset(undoFixture.Fixture.Project);
        var originalBinding = OfflineApplicationBuildPlan.Preview(undoFixture.Fixture.Project, undoFixture.Fixture.Device, Parameters).Binding;
        undoFixture.Fixture.Device.ParameterValues["R"] = "2"; undo.Push(undoFixture.Fixture.Project);
        var undone = undo.Undo()!; var undoneDevice = ProjectQuery.DeviceList(undone).Single();
        var undoPlan = OfflineApplicationBuildPlan.Preview(undone, undoneDevice, Parameters);
        Check("undo retains inputs and sources but requires a new live build binding", originalBinding is not null && !originalBinding.IsCurrent(undone, undoneDevice)
            && undoPlan.IsAvailable && undoPlan.Binding?.IsCurrent(undone, undoneDevice) == true
            && undoPlan.Sources.Hardware?.FactoryImage.Status == ArchivedFactoryImageStatus.Decoded);
        var redone = undo.Redo()!; var redoneDevice = ProjectQuery.DeviceList(redone).Single(); var redoPlan = OfflineApplicationBuildPlan.Preview(redone, redoneDevice, Parameters);
        Check("redo captures its own changed declared input hash", redoPlan.IsAvailable && !originalBinding!.IsCurrent(redone, redoneDevice)
            && redoPlan.Binding?.ProjectInputHash != originalBinding.ProjectInputHash && redoPlan.Binding?.IsCurrent(redone, redoneDevice) == true);
        var archive = typeof(Project).GetProperty("ArchiveState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var priorRevision = undoFixture.Fixture.Project.ApplicationDataRevision;
        var beforeReplacement = OfflineApplicationBuildPlan.Preview(undoFixture.Fixture.Project, undoFixture.Fixture.Device, Parameters).Binding;
        var wasCurrent = beforeReplacement?.IsCurrent(undoFixture.Fixture.Project, undoFixture.Fixture.Device) == true;
        archive.SetValue(undoFixture.Fixture.Project, archive.GetValue(Knxproj.Load(undoFixture.Fixture.Path)));
        var afterReplacement = OfflineApplicationBuildPlan.Preview(undoFixture.Fixture.Project, undoFixture.Fixture.Device, Parameters);
        Check("archive replacement stales build binding even for matching source bytes", wasCurrent && undoFixture.Fixture.Project.ApplicationDataRevision > priorRevision
            && beforeReplacement!.IsCurrent(undoFixture.Fixture.Project, undoFixture.Fixture.Device) == false
            && afterReplacement.Binding?.ProjectInputHash == beforeReplacement.ProjectInputHash);
        Check("all plans leave original archive bytes intact", SHA256.HashData(File.ReadAllBytes(f.Path)).SequenceEqual(sourceHash));
        Console.WriteLine($"ApplicationBuild: {checks - failures}/{checks} passed");
        return failures;
    }
}

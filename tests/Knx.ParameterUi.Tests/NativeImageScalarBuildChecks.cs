using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class NativeImageScalarBuildChecks
{
    private static readonly XNamespace Ns = PropertySourceChecks.Ns;
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Core(string name) => typeof(Project).Assembly.GetType("Knx." + name, true)!;
    private static object Call(string type, string name, params object?[] arguments) => Core(type).GetMethod(name, Hidden)!.Invoke(null, arguments)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name, Any)!.GetValue(value)!;
    private static XElement Stat(XDocument app) => NativeGeneralPopulationChecks.Stat(app);
    private static XElement Program(XDocument app) => NativeGeneralPopulationChecks.Program(app);
    private static XElement Parameter(XDocument app, int index) => Stat(app).Element(Ns + "Parameters")!.Elements().ElementAt(index);
    private static XElement Shape(XDocument app, string id) => Stat(app).Element(Ns + "ParameterTypes")!.Elements().Single(t => (string?)t.Attribute("Id") == id).Elements().Single();
    private static XElement Single() => new(Ns + "TypeFloat", new XAttribute("Encoding", "IEEE-754 Single"),
        new XAttribute("minInclusive", "-1e300"), new XAttribute("maxInclusive", "1e300"));
    private static XElement Dense(int bits = 16, string unit = "Seconds", long min = -32768, long max = 32767)
        => new(Ns + "TypeTime", new XAttribute("SizeInBit", bits), new XAttribute("Unit", unit),
            new XAttribute("minInclusive", min), new XAttribute("maxInclusive", max), new XAttribute("UIHint", "Duration_hhmmss"));
    private static XDocument Application(bool little = false)
    {
        var app = NativeGeneralPopulationChecks.App(); var types = Stat(app).Element(Ns + "ParameterTypes")!;
        types.Add(new XElement(Ns + "ParameterType", new XAttribute("Id", "Single"), Single()),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "Packed"), new XElement(Ns + "TypeTime",
                new XAttribute("SizeInBit", 24), new XAttribute("Unit", "PackedDaysHoursMinutesAndSeconds"),
                new XAttribute("minInclusive", 0), new XAttribute("maxInclusive", 691199))),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "Dense"), Dense()),
            new XElement(Ns + "ParameterType", new XAttribute("Id", "Enum"), new XElement(Ns + "TypeRestriction",
                new XAttribute("Base", "Value"), new XAttribute("SizeInBit", 8), new[] { 0, 3, 255 }.Select(i => new XElement(Ns + "Enumeration",
                    new XAttribute("Id", "Choice" + i), new XAttribute("Value", i))))));
        foreach (var (index, type, value) in new[] { (0,"Single","0.25"), (1,"Single","1.0000000596046448"), (2,"Single","-0"),
            (3,"Packed","90061"), (4,"Dense","-2"), (5,"Enum","3"), (128,"Single","1.5"), (129,"Dense","258") })
        { Parameter(app,index).SetAttributeValue("ParameterType",type); Parameter(app,index).SetAttributeValue("Value",value); }
        if (little)
        {
            Stat(app).Element(Ns + "Options")!.SetAttributeValue("ParameterByteOrder", "LittleEndian");
            Parameter(app,3).SetAttributeValue("ParameterType", "T");
        }
        return app;
    }
    private static ArchivedImageSourceChecks.Fixture Load(string directory, XDocument? app = null)
        => NativeGeneralImageBuildChecks.Mixed(directory, app ?? Application());
    private static NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture fixture, uint[]? words = null)
        => OfflineNativeImageBuild.Preview(fixture.Project, fixture.Device, new(words ?? [1u]));
    private static NativeImageBuildChunk Memory(NativeImageBuildPlan plan) => plan.Variants.Single().Chunks.Single(c => c.Target.Space == NativeImageBuildSpace.StandardMemory);
    private static NativeImageBuildChunk Property(NativeImageBuildPlan plan) => plan.Variants.Single().Chunks.Single(c => c.Target.Property?.PropertyId == 10);
    private static bool Bytes(ImmutablePatchBytes? bytes, byte[] expected) => bytes is not null && bytes.ToArray().SequenceEqual(expected);
    private static bool Empty(NativeImageBuildPlan plan, NativeImageBuildIssueCode? code = null)
        => !plan.IsAvailable && plan.HasGlobalFailure && plan.Binding is null && plan.Variants.Count == 0 && plan.Issues.Count == 1
            && (code is null || plan.Issues[0].Code == code) && !plan.ActualOptionsKnown && !plan.CompleteApplicationImage && !plan.DownloadEligible && !plan.ExecutionEligible;
    private static object Inventory(ArchivedImageSourceChecks.Fixture fixture)
        => Call("NativeRootImageInventory", "Read", fixture.Project, fixture.Device, Call("ArchivedImageSources", "ReadInputs", fixture.Project, fixture.Device));
    private static object Facts(ArchivedImageSourceChecks.Fixture fixture) => Get<object>(Inventory(fixture), "CodecFacts");
    private static string Xml(Project project) => Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Unplace(XDocument app, string id) => Program(app).Element(Ns + "Dynamic")!.Elements(Ns + "ParameterRefRef")
        .Single(e => (string?)e.Attribute("RefId") == id).Remove();

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok) { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageScalar " + name); if (!ok) failures++; }
        void Refused(string name, Action<XDocument> change, NativeImageBuildIssueCode? code = null)
        {
            var app = Application(); change(app); var fixture = Load(directory, app);
            var xml = Xml(fixture.Project); var hash = Hash(File.ReadAllBytes(fixture.Path)); var history = new UndoStack(); history.Reset(fixture.Project);
            Check(name, Empty(Preview(fixture), code) && xml == Xml(fixture.Project) && hash == Hash(File.ReadAllBytes(fixture.Path))
                && history.Steps == 0 && !history.IsDirty);
        }
        var full = Load(directory); var before = Xml(full.Project); var archive = Hash(File.ReadAllBytes(full.Path)); var revision = full.Project.ApplicationDataRevision;
        var undo = new UndoStack(); undo.Reset(full.Project); var result = Preview(full); var facts = Facts(full);
        Check("complete mixed131ref population admits all existing fixed-word families", result.IsAvailable && result.DeclaredReferenceCount == 131
            && result.DeclaredOwnerCount == 130 && result.Variants.Single().Population is { ReferenceCount: 131, PhysicalRecordCount: 130, NoStorageCount: 1 });
        var expected = new byte[512];
        Convert.FromHexString("3E8000003F8000008000000001012100FFFE000003000000").CopyTo(expected, 0);
        for (var i = 6; i < 128; i++) BinaryPrimitives.WriteUInt32BigEndian(expected.AsSpan(i * 4, 4), (uint)i + 1);
        var validity = Enumerable.Repeat((byte)255,512).ToArray();
        foreach (var i in new[] { 15, 18, 19, 21, 22, 23 }) validity[i] = 0;
        Check("all512 memory bytes match independent constants and BCL integer serialization", result.IsAvailable && Bytes(Memory(result).WorkingData, expected));
        Check("all512 validity and field-write bits preserve every narrow-field gap", result.IsAvailable && Bytes(Memory(result).DesiredValidity, validity)
            && Bytes(Memory(result).ParameterWriteBits, validity) && Bytes(Memory(result).CanonicalDesired, expected));
        var initial = new byte[512]; initial[0] = 0xA0;
        Check("initial512 source-merge bytes remain independent from scalar formatting", result.IsAvailable && Bytes(Memory(result).InitialData, initial)
            && Bytes(Memory(result).InitialValidity,new byte[512]));
        Check("whole property bytes use the same Single and signedTime codecs while tail stays unknown", result.IsAvailable
            && Property(result).InitialData.ToHexString() == "DEADBEEF10203040" && Bytes(Property(result).InitialValidity,new byte[8])
            && Property(result).WorkingData.ToHexString() == "3FC0000001023040" && Property(result).DesiredValidity?.ToHexString() == "FFFFFFFFFFFF0000"
            && Property(result).CanonicalDesired?.ToHexString() == "3FC0000001020000" && Property(result).ParameterWriteBits.ToHexString() == "FFFFFFFFFFFF0000");
        Check("codec facts count all130 physical fields rather than type definitions or references", Get<int>(facts,"CodecProfileVersion") == 3
            && Get<int>(facts,"PhysicalFieldCount") == 130 && Get<int>(facts,"SingleFieldCount") == 4
            && Get<int>(facts,"PackedTimeFieldCount") == 1 && Get<int>(facts,"DenseTimeFieldCount") == 2 && Get<bool>(facts,"HasExtendedCodecs"));
        Check("profile literals distinguish Single parsing packed bytes and signed rawTime", Get<string>(facts,"NumericParseProfile") == "ExactIntegerAndDotNet10XmlDoubleToSingle"
            && Get<string>(facts,"PackedTimeEncodingProfile") == "PackedDaysHoursMinutesAndSeconds24BigEndianOnly"
            && Get<string>(facts,"DenseTimeEncodingProfile") == "SignedRawOrdinaryTime8To32" && Get<string>(facts,"Fingerprint").Length == 64);
        Check("count observations and authority remain separate from scalar fields", result.IsAvailable && result.Variants.Single().ObservedCounts.Single().EncodedCount == 0x1234
            && result.Variants.Single().ObservedCounts.Single().NotAdopted && !result.CompleteApplicationImage && !result.DownloadEligible && !result.ExecutionEligible);
        if (result.IsAvailable)
        {
            Check("mixed binding freshly replays the complete source and codec branch", result.Binding!.IsCurrent(full.Project,full.Device));
            full.Device.ParameterValues["R0"] = "0.250";
            Check("equal encoded Single with changed raw presence stales the binding", !result.Binding!.IsCurrent(full.Project,full.Device));
            full.Device.ParameterValues.Clear();
            Check("exact restored raw presence restores the codec binding", result.Binding.IsCurrent(full.Project,full.Device));
            var copy = Memory(result).WorkingData.ToArray(); copy[0] = 0;
            Check("scalar result copy-out cannot change payload or binding", Memory(result).WorkingData[0] == 0x3E && result.Binding.IsCurrent(full.Project,full.Device));
        }
        Check("whole mixed source model revision archive and undo remain unchanged", before == Xml(full.Project) && archive == Hash(File.ReadAllBytes(full.Path))
            && revision == full.Project.ApplicationDataRevision && undo.Steps == 0 && !undo.IsDirty);
        var skipped = Preview(full,[128]);
        Check("task skipping does not erase the admitted source fields", skipped.IsAvailable && skipped.Variants.Single().Population is { ParameterTaskScheduled: false, ReferenceCount: 0 }
            && Get<string>(Facts(full),"Fingerprint") == Get<string>(facts,"Fingerprint"));

        var little = Load(directory,Application(little:true)); var littleResult = Preview(little); var littleExpected = new byte[512];
        Convert.FromHexString("0000803E0000803F00000080CD5F0100FEFF000003000000").CopyTo(littleExpected,0);
        for (var i = 6; i < 128; i++) BinaryPrimitives.WriteUInt32LittleEndian(littleExpected.AsSpan(i*4,4),(uint)i+1);
        Check("complete LittleEndian memory uses source order and Double-to-Single bits", littleResult.IsAvailable && Bytes(Memory(littleResult).WorkingData,littleExpected));
        Check("complete LittleEndian property uses unchanged raw ordinaryTime counts", littleResult.IsAvailable && Property(littleResult).WorkingData.ToHexString() == "0000C03F02013040");
        Check("unused packed type definition does not falsely mark a physical codec", Get<int>(Facts(little),"PackedTimeFieldCount") == 0
            && Get<string?>(Facts(little),"PackedTimeEncodingProfile") is null);

        var unused = NativeGeneralPopulationChecks.App();
        Stat(unused).Element(Ns + "ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","UnusedSingle"),Single()));
        var unusedFacts = Facts(Load(directory,unused));
        Check("unused valid Single metadata does not change physical codec facts", !Get<bool>(unusedFacts,"HasExtendedCodecs")
            && Get<string>(unusedFacts,"NumericParseProfile") == "ExactInteger" && Get<int>(unusedFacts,"SingleFieldCount") == 0);
        var latentApp = Application();
        Stat(latentApp).Element(Ns+"ParameterRefs")!.Elements().Single(e => (string?)e.Attribute("Id")=="R2").Remove(); Unplace(latentApp,"R2");
        var latent = Load(directory,latentApp); var latentResult = Preview(latent);
        Check("latent unreferenced physical Single still contributes to codec and owner inventory", latentResult.IsAvailable && latentResult.DeclaredReferenceCount == 130
            && Get<int>(Facts(latent),"SingleFieldCount") == 4 && Get<int>(Facts(latent),"PhysicalFieldCount") == 130
            && Memory(latentResult).WorkingData.ToArray().AsSpan(8,4).SequenceEqual(new byte[4]));

        foreach (var (raw,hex) in new[] { ("0.1","3DCCCCCD"),("1.0000000596046448","3F800000"),("-0","80000000"),("1e-45","00000001") })
        {
            var fixture = Load(directory); fixture.Device.ParameterValues["R0"] = raw; var plan = Preview(fixture);
            Check("Single exact literal "+raw,plan.IsAvailable && Convert.ToHexString(Memory(plan).WorkingData.ToArray().AsSpan(0,4)) == hex);
        }
        foreach (var (raw,hex) in new[] { ("0","000000"),("691199","3B3BF7") })
        {
            var fixture = Load(directory); fixture.Device.ParameterValues["R3"] = raw; var plan = Preview(fixture);
            Check("packed boundary "+raw,plan.IsAvailable && Convert.ToHexString(Memory(plan).WorkingData.ToArray().AsSpan(12,3)) == hex);
        }
        foreach (var unit in new[] { "Hours","Minutes","Seconds","HundredMilliseconds","TenMilliseconds","Milliseconds" })
        {
            var app = Application(); Shape(app,"Dense").SetAttributeValue("Unit",unit); Parameter(app,4).SetAttributeValue("Value",258); var plan = Preview(Load(directory,app));
            Check("ordinary unit remains raw counts "+unit,plan.IsAvailable && Convert.ToHexString(Memory(plan).WorkingData.ToArray().AsSpan(16,2)) == "0102");
        }
        foreach (var (raw,hex) in new[] { (-128,"80"),(127,"7F") })
        {
            var app = Application(); Shape(app,"Dense").ReplaceWith(Dense(8,min:-128,max:127)); Parameter(app,4).SetAttributeValue("Value",raw); Parameter(app,129).SetAttributeValue("Value",1);
            var plan = Preview(Load(directory,app)); Check("signed8 endpoint "+raw,plan.IsAvailable && Memory(plan).WorkingData[16].ToString("X2") == hex);
        }
        var subbyte = Application(); Shape(subbyte,"Dense").ReplaceWith(Dense(11,min:-1024,max:1023)); Parameter(subbyte,4).SetAttributeValue("Value",-1);
        Parameter(subbyte,4).Element(Ns+"Memory")!.SetAttributeValue("BitOffset",2);
        var code = Stat(subbyte).Descendants(Ns+"AbsoluteSegment").Single(); var seed = new byte[18];seed[16]=0xD3;seed[17]=0x5A;
        code.Element(Ns+"Data")!.Value=Convert.ToBase64String(seed);code.Element(Ns+"Mask")!.Value=Convert.ToBase64String(Enumerable.Repeat((byte)255,18).ToArray());
        var subbyteResult=Preview(Load(directory,subbyte));
        Check("signed11 atbit2 replaces only its field but validates both whole bytes",subbyteResult.IsAvailable
            && Convert.ToHexString(Memory(subbyteResult).WorkingData.ToArray().AsSpan(16,2))=="FFFA"
            && Convert.ToHexString(Memory(subbyteResult).ParameterWriteBits.ToArray().AsSpan(16,2))=="3FF8"
            && Convert.ToHexString(Memory(subbyteResult).DesiredValidity!.ToArray().AsSpan(16,2))=="FFFF");

        foreach (var behavior in new[] { "DefaultValue","Background","None" })
        {
            var app=Application();Unplace(app,"R0");Stat(app).Element(Ns+"Options")!.SetAttributeValue("DownloadInvisibleParameters",behavior);
            Stat(app).Element(Ns+"ParameterRefs")!.Elements().First().SetAttributeValue("Value","0.5");
            var fixture=Load(directory,app);fixture.Device.ParameterValues["R0"]="0.75";var plan=Preview(fixture);
            Check("inactive Single policy keeps current override separate: "+behavior,plan.IsAvailable
                && plan.Variants.Single().Population!.Records.Single(r=>r.RefId=="R0") is { Active:false,NormalReferenceCount:0 } record
                && record.CurrentValueHash!=record.RecordValueHash
                && Convert.ToHexString(Memory(plan).WorkingData.ToArray().AsSpan(0,4))==(behavior=="DefaultValue"?"3E800000":"A0000000")
                && Convert.ToHexString(Memory(plan).DesiredValidity!.ToArray().AsSpan(0,4))==(behavior=="None"?"00000000":"FFFFFFFF"));
        }
        var alwaysApp=Application();Unplace(alwaysApp,"R0");Parameter(alwaysApp,0).SetAttributeValue("LegacyPatchAlways",true);
        var alwaysFixture=Load(directory,alwaysApp);alwaysFixture.Device.ParameterValues["R0"]="0.75";var always=Preview(alwaysFixture);
        Check("inactive existing Always Single uses hydrated saved current",always.IsAvailable && always.Variants.Single().Population!.Records.Single(r=>r.RefId=="R0") is
            { Disposition:RootParameterDisposition.AlwaysValue,Active:false,NormalReferenceCount:0 } ar && ar.CurrentValueHash==ar.RecordValueHash
            && Convert.ToHexString(Memory(always).WorkingData.ToArray().AsSpan(0,4))=="3F400000");
        foreach(var nullValue in new[]{false,true})
        {
            var app=Application();Parameter(app,0).SetAttributeValue("CustomerAdjustable",true);
            if(nullValue){Unplace(app,"R0");Stat(app).Element(Ns+"Options")!.SetAttributeValue("DownloadInvisibleParameters","Background");}
            var plan=Preview(Load(directory,app),[1025]);
            Check("excluded customer Single "+(nullValue?"null background":"nonnull"),plan.IsAvailable
                && plan.Variants.Single().Population!.Records.Single(r=>r.RefId=="R0") is { CustomerExcluded:true,Allocates:true,WritesData:false } record
                && (record.RecordValueHash is null)==nullValue && Convert.ToHexString(Memory(plan).WorkingData.ToArray().AsSpan(0,4))=="A0000000"
                && Convert.ToHexString(Memory(plan).DesiredValidity!.ToArray().AsSpan(0,4))=="00000000");
        }

        foreach(var raw in new[]{"NaN","INF","-INF","3.4028236e38","1e400","1,25"})
        {var fixture=Load(directory);fixture.Device.ParameterValues["R0"]=raw;Check("invalid Single raw "+raw,Empty(Preview(fixture),NativeImageBuildIssueCode.InvalidValue));}
        foreach(var attr in new[]{"Increment","DisplayFactor","DisplayOffset"})
            Refused("Single transform remains rejected "+attr,a=>Shape(a,"Single").SetAttributeValue(attr,"1"),NativeImageBuildIssueCode.UnsupportedType);
        foreach(var encoding in new[]{"IEEE-754 Double","DPT 9"})
            Refused("other Float encoding "+encoding,a=>Shape(a,"Single").SetAttributeValue("Encoding",encoding),NativeImageBuildIssueCode.UnsupportedType);
        Refused("Single width is intrinsic not an XML override",a=>Shape(a,"Single").SetAttributeValue("SizeInBit",32),NativeImageBuildIssueCode.UnsupportedType);
        Refused("Single finite ordered bounds are required",a=>Shape(a,"Single").Attribute("minInclusive")!.Remove(),NativeImageBuildIssueCode.UnsupportedType);
        Refused("Single bit offset must be zero",a=>Parameter(a,0).Element(Ns+"Memory")!.SetAttributeValue("BitOffset",1),NativeImageBuildIssueCode.InvalidValue);
        Refused("packed LittleEndian is unsupported",a=>Stat(a).Element(Ns+"Options")!.SetAttributeValue("ParameterByteOrder","LittleEndian"),NativeImageBuildIssueCode.InvalidValue);
        Refused("packed bit offset must be zero",a=>Parameter(a,3).Element(Ns+"Memory")!.SetAttributeValue("BitOffset",1),NativeImageBuildIssueCode.InvalidValue);
        Refused("packed width must be24",a=>Shape(a,"Packed").SetAttributeValue("SizeInBit",16),NativeImageBuildIssueCode.UnsupportedType);
        Refused("other packed units remain unsupported",a=>Shape(a,"Packed").SetAttributeValue("Unit","PackedSecondsAndMilliseconds"),NativeImageBuildIssueCode.UnsupportedType);
        Refused("entire packed bound must fit",a=>Shape(a,"Packed").SetAttributeValue("maxInclusive",691200),NativeImageBuildIssueCode.UnsupportedType);
        foreach(var bits in new[]{7,33}) Refused("ordinary width remains bounded "+bits,a=>Shape(a,"Dense").SetAttributeValue("SizeInBit",bits),NativeImageBuildIssueCode.UnsupportedType);
        Refused("whole declared ordinary interval must fit signed width",a=>Shape(a,"Dense").SetAttributeValue("maxInclusive",32768),NativeImageBuildIssueCode.UnsupportedType);
        Refused("nonbyte ordinary LittleEndian remains rejected",a=>{Shape(a,"Dense").ReplaceWith(Dense(11,min:-1024,max:1023));Stat(a).Element(Ns+"Options")!.SetAttributeValue("ParameterByteOrder","LittleEndian");Parameter(a,3).SetAttributeValue("ParameterType","T");},NativeImageBuildIssueCode.InvalidValue);
        foreach(var index in new[]{0,3,4}) Refused("new scalar NoStorage is not implicitly admitted "+index,a=>{Parameter(a,index).Element(Ns+"Memory")!.Remove();Parameter(a,index).Attribute("Value")!.Remove();},NativeImageBuildIssueCode.UnsupportedType);
        foreach(var index in new[]{0,3,4}) Refused("new scalar is not a controller "+index,a=>Program(a).Element(Ns+"Dynamic")!.Add(new XElement(Ns+"choose",new XAttribute("ParamRefId","R"+index),new XElement(Ns+"when",new XAttribute("test","1")))));
        Refused("unselected malformed new type is still preflighted",a=>Stat(a).Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","BadUnused"),new XElement(Ns+"TypeFloat",new XAttribute("Encoding","DPT 9")))),NativeImageBuildIssueCode.UnsupportedType);
        Refused("latent unsupported physical layout cannot disappear",a=>{Stat(a).Element(Ns+"ParameterRefs")!.Elements().First().Remove();Unplace(a,"R0");Parameter(a,0).Element(Ns+"Memory")!.SetAttributeValue("BitOffset",1);},NativeImageBuildIssueCode.InvalidValue);

        var table = NativeGeneralImageBuildChecks.TableFixture(directory);
        var tableInputs=Call("ArchivedImageSources","ReadInputs",table.Project,table.Device);
        // Rebuild an owned archive with a Single in the object-table's unused tail.
        var tableApp=new XDocument(Get<XDocument>(tableInputs,"Application"));
        Stat(tableApp).Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","TailSingle"),Single()));
        Stat(tableApp).Element(Ns+"Parameters")!.Add(new XElement(Ns+"Parameter",new XAttribute("Id","Tail"),new XAttribute("ParameterType","TailSingle"),new XAttribute("Value","1.5"),
            new XElement(Ns+"Memory",new XAttribute("CodeSegment","KO"),new XAttribute("Offset",24),new XAttribute("BitOffset",0))));
        Stat(tableApp).Element(Ns+"ParameterRefs")!.Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","TailRef"),new XAttribute("RefId","Tail")));
        Program(tableApp).Element(Ns+"Dynamic")!.Add(new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","TailRef")));
        var tableWithScalar=ArchivedImageSourceChecks.Load(directory,app:tableApp,master:GeneratedTableSerializerChecks.Master(GeneratedTableFormat.M112),hardware:PropertyInitializationChecks.Hardware());
        var sourceInstallation=table.Project.Installations.Single();var destination=tableWithScalar.Project.Installations.Single();
        destination.Areas.Single().Lines.Single().MediumTypeRefId="MT-0";
        destination.MainRanges.AddRange(sourceInstallation.MainRanges);
        foreach(var obj in table.Device.ComObjectRefs)tableWithScalar.Device.ComObjectRefs.Add(obj);
        foreach(var pair in table.Project.Workbench.Groups)tableWithScalar.Project.Workbench.Groups[pair.Key]=pair.Value;
        foreach(var pair in table.Project.Workbench.Objects)
        {
            var obj=table.Device.ComObjectRefs.Single(o=>pair.Key==new Knx.Workbench.ObjectKey(table.Device.Id,o.Id).ToString());
            tableWithScalar.Project.Workbench.Objects[new Knx.Workbench.ObjectKey(tableWithScalar.Device.Id,obj.Id).ToString()]=pair.Value;
        }
        var tableXml=Xml(tableWithScalar.Project);var tableArchive=Hash(File.ReadAllBytes(tableWithScalar.Path));
        var tableResult=OfflineNativeImageBuild.Preview(tableWithScalar.Project,tableWithScalar.Device,new(new uint[]{131},new(GeneratedTableFormat.M112)));
        Check("Single tail and all three tables use the same complete current working image",tableResult.IsAvailable && tableResult.Variants.Single().Tables is { Payloads.Count:3 } tables
            && tables.Payloads.Single(p=>p.Role==GeneratedTableRole.GroupAddress).Data.ToHexString()=="04CAFE01010A030B01"
            && tableResult.Variants.Single().Chunks.Single(c=>c.ByteOffset==0x4400).WorkingData.ToArray().AsSpan(24,4).SequenceEqual(Convert.FromHexString("3FC00000")));
        Check("table application preserves scalar field validity outside its used span",tableResult.IsAvailable
            && tableResult.Variants.Single().Chunks.Single(c=>c.ByteOffset==0x4400).DesiredValidity!.ToArray().AsSpan(23,9).SequenceEqual(Convert.FromHexString("00FFFFFFFF00000000"))
            && tableXml==Xml(tableWithScalar.Project)&&tableArchive==Hash(File.ReadAllBytes(tableWithScalar.Path)));

        var late=Load(directory);late.Device.ParameterValues["R0"]="0.25";
        var inputs=Call("ArchivedImageSources","ReadInputs",late.Project,late.Device);
        var inventory=Call("NativeRootImageInventory","Read",late.Project,late.Device,inputs);
        var preparation=Call("NativeImagePreparation","Prepare",late.Project,late.Device,inputs,inventory,new uint[]{1});
        late.Device.ParameterValues["R0"]="0.250";
        var context=Call("NativeApplicationEvaluationContext","Create",late.Project,late.Device,inputs,Array.Empty<string>(),null);
        var population=Call("NativeRootWriterPopulation","Create",inventory,preparation,context);
        Check("late equal-word raw capture cannot mint a population from older codec sources",!Get<bool>(population,"IsAvailable")
            && Get<string?>(population,"Fingerprint") is null
            && ((System.Collections.IEnumerable)Get<object>(population,"Issues")).Cast<object>().Any(i=>Get<object>(i,"Code").ToString()=="InputsChanged"));
        Console.WriteLine($"NativeImageScalar: {checks} checks, {failures} failures");
        return failures;
    }
}

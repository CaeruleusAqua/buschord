using System.Xml.Linq;
using Knx;

internal static class NativeModuleImageBuildChecks
{
    private static readonly XNamespace Ns=PropertySourceChecks.Ns;
    internal static XDocument Application()
    {
        var app=NativeGeneralPopulationChecks.App(2,8);var program=NativeGeneralPopulationChecks.Program(app);var stat=NativeGeneralPopulationChecks.Stat(app);
        stat.Element(Ns+"Code")!.Element(Ns+"AbsoluteSegment")!.SetAttributeValue("Size",512);
        XElement Parameter(string id,int offset,int value)=>new(Ns+"Parameter",new XAttribute("Id",id),new XAttribute("ParameterType","T"),new XAttribute("Value",value),
            new XElement(Ns+"Memory",new XAttribute("CodeSegment","S"),new XAttribute("Offset",offset),new XAttribute("BitOffset",0),new XAttribute("BaseOffset","MD_A")));
        program.Add(new XElement(Ns+"ModuleDefs",new XElement(Ns+"ModuleDef",new XAttribute("Id","MD"),
            new XElement(Ns+"Arguments",new XElement(Ns+"Argument",new XAttribute("Id","MD_A"),new XAttribute("Type","Numeric"))),
            new XElement(Ns+"Static",new XElement(Ns+"Parameters",Parameter("MD_P0",0,7),Parameter("MD_P1",1,11),Parameter("MD_PL",2,99)),
                new XElement(Ns+"ParameterRefs",new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_R0"),new XAttribute("RefId","MD_P0")),
                    new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_R1"),new XAttribute("RefId","MD_P1")))),
            new XElement(Ns+"Dynamic",new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","MD_R0")),
                new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),new XElement(Ns+"when",new XAttribute("test","1"),new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","MD_R1"))))))));
        foreach(var (id,offset) in new[]{("Call1",8),("Call2",16)}) program.Element(Ns+"Dynamic")!.Add(new XElement(Ns+"Module",new XAttribute("Id",id),new XAttribute("RefId","MD"),
            new XElement(Ns+"NumericArg",new XAttribute("RefId","MD_A"),new XAttribute("Value",offset))));
        return app;
    }
    private static NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture f,uint word=1)=>OfflineNativeImageBuild.Preview(f.Project,f.Device,new([word]));
    private static NativeImageBuildChunk Memory(NativeImageBuildPlan p)=>p.Variants.Single().Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory);
    private static bool Bytes(ImmutablePatchBytes? actual,byte[] expected)=>actual is not null&&actual.ToArray().SequenceEqual(expected);
    private static bool NoAuthority(NativeImageBuildPlan p)=>!p.CompleteApplicationImage&&!p.DownloadEligible&&!p.ExecutionEligible&&!p.ActualOptionsKnown;
    private static bool Empty(NativeImageBuildPlan p)=>!p.IsAvailable&&p.Binding is null&&p.Variants.All(v=>v.Chunks.Count==0)&&NoAuthority(p);
    private static XElement ModuleStatic(XDocument app)=>app.Descendants(Ns+"ModuleDef").Single().Element(Ns+"Static")!;
    internal static XDocument RepeatApplication(int count=3)
    {
        var app=Application();var stat=NativeGeneralPopulationChecks.Stat(app);var dynamic=NativeGeneralPopulationChecks.Program(app).Element(Ns+"Dynamic")!;
        dynamic.Elements(Ns+"Module").Remove();stat.Add(new XElement(Ns+"Allocators",new XElement(Ns+"Allocator",new XAttribute("Id","Pool"),new XAttribute("Start",9),new XAttribute("maxInclusive",22))));
        var argument=app.Descendants(Ns+"Argument").Single();argument.SetAttributeValue("Allocates",3);argument.SetAttributeValue("Alignment",4);
        dynamic.Add(new XElement(Ns+"Repeat",new XAttribute("Id","Repeat"),new XAttribute("Count",count),
            new XElement(Ns+"Module",new XAttribute("Id","RepeatCall"),new XAttribute("RefId","MD"),new XElement(Ns+"NumericArg",new XAttribute("RefId","MD_A"),new XAttribute("Value",0),new XAttribute("AllocatorRefId","Pool")))));
        return app;
    }
    internal static int Run(string directory)
    {
        var failures=0;var checks=0;
        void Check(string name,bool value){checks++;Console.WriteLine((value?"PASS ":"FAIL ")+"Module image "+name);if(!value)failures++;}
        ArchivedImageSourceChecks.Fixture Load(XDocument? app=null)=>NativeGeneralImageBuildChecks.Mixed(directory,app??Application());
        void Refuse(string name,Action<XDocument> change){var app=Application();change(app);var result=Preview(Load(app));Check(name,Empty(result));}
        var fixture=Load();var before=Knxproj.ToXml(fixture.Project).ToString();var revision=fixture.Project.ApplicationDataRevision;
        var plan=Preview(fixture);Check("literal modules use the common public pipeline",plan.IsAvailable&&plan.Variants.All(v=>v.IsAvailable)&&NoAuthority(plan));
        if(!plan.IsAvailable){Console.WriteLine(string.Join(",",plan.Issues.Concat(plan.Variants.SelectMany(v=>v.Issues))));return failures;}
        var memory=Memory(plan);var expected=new byte[512];expected[1]=1;expected[8]=expected[16]=7;expected[9]=expected[17]=11;
        var validity=new byte[512];foreach(var index in new[]{0,1,8,9,16,17})validity[index]=255;
        var initial=new byte[512];initial[0]=0xA0;
        Check("all512 desired bytes include independent per-call base offsets",Bytes(memory.WorkingData,expected)&&Bytes(memory.CanonicalDesired,expected));
        Check("all512 validity and mask bytes preserve latent holes",Bytes(memory.DesiredValidity,validity)&&Bytes(memory.ParameterWriteBits,validity));
        Check("all512 initialization bytes remain separate from writes",Bytes(memory.InitialData,initial)&&Bytes(memory.InitialValidity,new byte[512]));
        var population=plan.Variants.Single().Population!;
        Check("complete population includes both module references and latent owners",population.ReferenceCount==9&&population.OwnerCount==10&&population.ModuleOrigins.Count==10&&population.ModuleInstances.Count==2&&population.ModuleOriginsFingerprint?.Length==64);
        Check("module-internal inactive states remain explicit records",population.Records.Count(r=>r.RefId.Contains("_MI-",StringComparison.Ordinal)&&r.Active)==2&&population.Records.Count(r=>r.RefId.Contains("_MI-",StringComparison.Ordinal)&&!r.Active&&r.Disposition==RootParameterDisposition.InactiveDefault)==2);
        Check("concrete origins retain call definition original declaration and expanded identity",population.ModuleOrigins.Where(o=>o.ReferenceId is not null).All(o=>o.DefinitionId=="MD"&&o.CallId is "Call1" or "Call2"&&o.SourceKey>=0&&o.DeclarationHash.Length==64&&o.OriginalReferenceId!.StartsWith("MD_",StringComparison.Ordinal)));
        Check("active inactive and latent module addresses are explicit in the origin inventory",population.ModuleOrigins.Where(o=>o.ReferenceId is null).Select(o=>o.Field!.ByteOffset).Order().SequenceEqual(new uint[]{108,109,110,116,117,118}));
        Check("unchanged source replay is current",plan.Binding!.IsCurrent(fixture.Project,fixture.Device));
        fixture.Device.ParameterValues["Call1_MI-1_R0"]="13";
        Check("module value change stales the complete binding",!plan.Binding.IsCurrent(fixture.Project,fixture.Device));
        var changed=Preview(fixture);var changedBytes=(byte[])expected.Clone();changedBytes[8]=13;
        Check("scoped saved value changes only its concrete module field",changed.IsAvailable&&Bytes(Memory(changed).WorkingData,changedBytes)&&changed.Variants.Single().Population!.Records.Single(r=>r.RefId=="Call1_MI-1_R0").CurrentValueOrigin==RootParameterValueOrigin.Saved);
        fixture.Device.ParameterValues.Clear();Check("exact saved-state restoration restores binding",plan.Binding.IsCurrent(fixture.Project,fixture.Device));
        Check("model source revision and project serialization are unchanged",before==Knxproj.ToXml(fixture.Project).ToString()&&revision==fixture.Project.ApplicationDataRevision);
        var skipped=Preview(fixture,128);Check("task skipping preserves module origin inventory without writer records",skipped.IsAvailable&&skipped.Variants.Single().Population is {ParameterTaskScheduled:false,ReferenceCount:0,ModuleOrigins.Count:10});
        var active=Load();active.Device.ParameterValues["R0"]="1";var activePlan=Preview(active);
        Check("same shared evaluation controls root and module branches",activePlan.IsAvailable&&activePlan.Variants.Single().Population!.Records.Count(r=>r.RefId.Contains("_MI-",StringComparison.Ordinal)&&r.Active)==4);
        var repeated=Load(RepeatApplication());var repeatedPlan=Preview(repeated);
        Check("literal repeat and root allocator execute through the same combined kernel",repeatedPlan.IsAvailable&&NoAuthority(repeatedPlan));
        if(repeatedPlan.IsAvailable)
        {
            var repeatData=new byte[512];repeatData[1]=1;var repeatMask=new byte[512];repeatMask[0]=repeatMask[1]=255;
            foreach(var start in new[]{12,16,20}){repeatData[start]=7;repeatData[start+1]=11;repeatMask[start]=repeatMask[start+1]=255;}
            var chunk=Memory(repeatedPlan);var pop=repeatedPlan.Variants.Single().Population!;
            Check("all512 repeated data validity masks and canonical bytes match independent allocator arithmetic",Bytes(chunk.WorkingData,repeatData)&&Bytes(chunk.CanonicalDesired,repeatData)&&Bytes(chunk.DesiredValidity,repeatMask)&&Bytes(chunk.ParameterWriteBits,repeatMask));
            Check("first allocation alignment and inclusive final capacity are preserved",pop.ModuleAllocations.Select(a=>a.Value).SequenceEqual(new long[]{12,16,20})&&pop.ModuleAllocations.All(a=>a.Alignment==4&&a.Allocates==3&&a.PoolStart==9&&a.PoolMaximumInclusive==22&&a.Usage=="MemoryByteOffset"));
            Check("whole allocator reservations retain explicit targets and require initialization",pop.ModuleReservations.Select(r=>r.Field.ByteOffset).SequenceEqual(new uint[]{112,116,120})&&pop.ModuleReservations.All(r=>r.OwnerKey.Length==64&&r.Field.Target.Space==NativeImageBuildSpace.StandardMemory&&r.Field.BitOffset==0&&r.Field.BitLength==24));
            Check("repeat origins include every active inactive and latent physical owner",pop.ReferenceCount==11&&pop.OwnerCount==13&&pop.ModuleOrigins.Count==15&&pop.ModuleOrigins.Where(o=>o.ReferenceId is null).Select(o=>o.Field!.ByteOffset).Order().SequenceEqual(new uint[]{112,113,114,116,117,118,120,121,122}));
            Check("repeat binding freshly replays the full occurrence and allocator schedule",repeatedPlan.Binding!.IsCurrent(repeated.Project,repeated.Device));
        }
        else Console.WriteLine("Repeat diagnostic: "+string.Join(",",repeatedPlan.Issues.Concat(repeatedPlan.Variants.SelectMany(v=>v.Issues))));
        var zero=Preview(Load(RepeatApplication(0)));Check("zero-count literal repeats retain source inventory without fabricating occurrences",zero.IsAvailable&&zero.Variants.Single().Population is {ModuleOrigins.Count:0,ModuleAllocations.Count:0,ReferenceCount:5});
        var tight=RepeatApplication();tight.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",21);Check("one-byte allocator overflow exposes no partial alternative",Empty(Preview(Load(tight))));
        var shortSpan=RepeatApplication();shortSpan.Descendants(Ns+"Argument").Single().SetAttributeValue("Allocates",2);Check("allocator extent covers latent fields as well as emitted references",Empty(Preview(Load(shortSpan))));
        var gap=RepeatApplication();gap.Descendants(Ns+"Argument").Single().SetAttributeValue("Allocates",4);gap.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",23);
        NativeGeneralPopulationChecks.Stat(gap).Element(Ns+"Parameters")!.Elements().ElementAt(1).Element(Ns+"Memory")!.SetAttributeValue("Offset",15);
        var gapPlan=Preview(Load(gap));Check("foreign owner in unused reserved bytes is an alias",Empty(gapPlan)&&gapPlan.Issues.Any(i=>i.Code==NativeImageBuildIssueCode.AmbiguousOwnership));
        var tail=RepeatApplication(1);tail.Descendants(Ns+"Argument").Single().SetAttributeValue("Allocates",600);tail.Descendants(Ns+"Argument").Single().SetAttributeValue("Alignment",1);
        tail.Descendants(Ns+"Allocator").Single().SetAttributeValue("Start",8);tail.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",607);
        Check("unused allocation tail needs full initialized backing",Empty(Preview(Load(tail))));
        var nested=RepeatApplication(2);var outer=nested.Descendants(Ns+"Repeat").Single();var call=outer.Element(Ns+"Module")!;call.ReplaceWith(new XElement(Ns+"Repeat",new XAttribute("Id","Inner"),new XAttribute("Count",2),new XElement(call)));nested.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",26);
        var nestedPlan=Preview(Load(nested));Check("nested literal repeat dimensions retain complete forward identities",nestedPlan.IsAvailable&&nestedPlan.Variants.Single().Population!.ModuleAllocations.Select(a=>a.InstanceId).SequenceEqual(new[]{"RepeatCall_MI-1-1","RepeatCall_MI-1-2","RepeatCall_MI-2-1","RepeatCall_MI-2-2"}));
        var expandedBudget=RepeatApplication(256);expandedBudget.Descendants(Ns+"Argument").Single().SetAttributeValue("Allocates",512);expandedBudget.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",131083);
        var largeStatic=ModuleStatic(expandedBudget);largeStatic.Element(Ns+"Parameters")!.RemoveNodes();largeStatic.Element(Ns+"ParameterRefs")!.RemoveNodes();var largeDynamic=expandedBudget.Descendants(Ns+"ModuleDef").Single().Element(Ns+"Dynamic")!;largeDynamic.RemoveNodes();
        for(var i=0;i<512;i++){largeStatic.Element(Ns+"Parameters")!.Add(new XElement(Ns+"Parameter",new XAttribute("Id","MD_B"+i),new XAttribute("ParameterType","T"),new XAttribute("Value",i%256),new XElement(Ns+"Memory",new XAttribute("CodeSegment","S"),new XAttribute("Offset",i),new XAttribute("BitOffset",0),new XAttribute("BaseOffset","MD_A"))));largeStatic.Element(Ns+"ParameterRefs")!.Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_BR"+i),new XAttribute("RefId","MD_B"+i)));largeDynamic.Add(new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","MD_BR"+i)));}
        var budgetPlan=Preview(Load(expandedBudget));Check("aggregate expanded population budget precedes materialization",Empty(budgetPlan)&&budgetPlan.Issues.Any(i=>i.Code==NativeImageBuildIssueCode.LimitExceeded));
        foreach(var emptyCount in new[]{0,256})
        {
            var emptyRepeats=Application();
            var inner=new XElement(Ns+"Repeat",new XAttribute("Id","EmptyRepeat7"),new XAttribute("Count",emptyCount));
            for(var depth=6;depth>=0;depth--)inner=new XElement(Ns+"Repeat",new XAttribute("Id","EmptyRepeat"+depth),new XAttribute("Count",256),inner);
            NativeGeneralPopulationChecks.Program(emptyRepeats).Element(Ns+"Dynamic")!.Add(inner);
            var emptyFixture=Load(emptyRepeats);var elapsed=System.Diagnostics.Stopwatch.StartNew();var emptyPlan=Preview(emptyFixture);elapsed.Stop();
            Check("empty nested repeat traversal is bounded before expansion (inner="+emptyCount+")",Empty(emptyPlan)&&emptyPlan.Issues.Any(i=>i.ActivationIssue?.Code==GeneratedTableIssueCode.LimitExceeded)&&elapsed.Elapsed<TimeSpan.FromSeconds(5));
        }
        var quadratic=RepeatApplication(256);var qDefinition=quadratic.Descendants(Ns+"ModuleDef").Single();
        qDefinition.Element(Ns+"Arguments")!.RemoveNodes();var qStatic=qDefinition.Element(Ns+"Static")!;qStatic.Element(Ns+"Parameters")!.RemoveNodes();qStatic.Element(Ns+"ParameterRefs")!.RemoveNodes();qDefinition.Element(Ns+"Dynamic")!.RemoveNodes();
        var qCall=quadratic.Descendants(Ns+"Module").Single();qCall.RemoveNodes();var qPools=NativeGeneralPopulationChecks.Stat(quadratic).Element(Ns+"Allocators")!;qPools.RemoveNodes();
        for(var argument=0;argument<64;argument++)
        {
            var id="MD_A"+argument;var pool="Pool"+argument;
            qDefinition.Element(Ns+"Arguments")!.Add(new XElement(Ns+"Argument",new XAttribute("Id",id),new XAttribute("Type","Numeric"),new XAttribute("Allocates",6)));
            qCall.Add(new XElement(Ns+"NumericArg",new XAttribute("RefId",id),new XAttribute("Value",0),new XAttribute("AllocatorRefId",pool)));
            qPools.Add(new XElement(Ns+"Allocator",new XAttribute("Id",pool),new XAttribute("Start",argument*1536),new XAttribute("maxInclusive",argument*1536+1535)));
            for(var offset=0;offset<6;offset++)qStatic.Element(Ns+"Parameters")!.Add(new XElement(Ns+"Parameter",new XAttribute("Id","MD_Q"+argument+"_"+offset),new XAttribute("ParameterType","T"),new XAttribute("Value",7),new XElement(Ns+"Memory",new XAttribute("CodeSegment","S"),new XAttribute("Offset",offset),new XAttribute("BitOffset",0),new XAttribute("BaseOffset",id))));
        }
        var scopeType=typeof(Project).Assembly.GetType("Knx.NativeModuleImageScope",true)!;var scopeRead=scopeType.GetMethod("Read",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        var validationWatch=System.Diagnostics.Stopwatch.StartNew();var limited=false;
        try{_=scopeRead.Invoke(null,[quadratic.Root!]);}catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException?.GetType().Name=="NativeGeneralRejected")
        {limited=ex.InnerException.GetType().GetProperty("Issue",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(ex.InnerException) is {} issue&&issue.GetType().GetProperty("Code")!.GetValue(issue)?.ToString()=="LimitExceeded";}
        validationWatch.Stop();Check("combined allocation validation work is bounded before materialization",limited&&validationWatch.Elapsed<TimeSpan.FromSeconds(5));
        var rangeApp=RepeatApplication(256);rangeApp.Descendants(Ns+"Allocator").Single().SetAttributeValue("maxInclusive",1034);
        var rangeScope=scopeRead.Invoke(null,[rangeApp.Root!])!;var hidden=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        var physical=(IReadOnlyList<XElement>)scopeType.GetProperty("Parameters",hidden)!.GetValue(rangeScope)!;
        var ownerType=typeof(Project).Assembly.GetType("Knx.NativeRootOwner",true)!;var targetType=typeof(Project).Assembly.GetType("Knx.NativeImageTarget",true)!;
        var spaceType=typeof(Project).Assembly.GetType("Knx.NativeImageSpace",true)!;var target=Activator.CreateInstance(targetType,[Enum.Parse(spaceType,"StandardMemory"),null,null])!;
        var owners=Array.CreateInstance(ownerType,physical.Count+20000);
        for(var i=0;i<owners.Length;i++)
        {
            var id=i<physical.Count?(string)physical[i].Attribute("Id")!:"RootBudgetOwner"+i;
            var offset=i<physical.Count?(uint)physical[i].Element(Ns+"Memory")!.Attribute("Offset")!+100:(uint)(2000+i);
            owners.SetValue(Activator.CreateInstance(ownerType,[id,i,"hash",target,offset,(byte)0,8u,false,null,null,id,"hash"]),i);
        }
        var rangeWatch=System.Diagnostics.Stopwatch.StartNew();limited=false;
        try{scopeType.GetMethod("ValidateAllocationRanges",hidden)!.Invoke(rangeScope,[owners]);}catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException?.GetType().Name=="NativeGeneralRejected")
        {limited=ex.InnerException.GetType().GetProperty("Issue",hidden)!.GetValue(ex.InnerException) is {} issue&&issue.GetType().GetProperty("Code")!.GetValue(issue)?.ToString()=="LimitExceeded";}
        rangeWatch.Stop();Check("root owners also count toward the combined reservation validation budget",limited&&rangeWatch.Elapsed<TimeSpan.FromSeconds(5));
        var native=Load();var original=(XDocument)typeof(Project).GetProperty("OriginalXml",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(native.Project)!;var ns=original.Root!.Name.Namespace;var device=original.Descendants(ns+"DeviceInstance").Single(e=>(string?)e.Attribute("Id")==native.Device.Id);
        var instances=ApplicationSnapshot.Evaluate(Application(),new Dictionary<string,string?>()).ModuleInstances;
        device.Add(new XElement(ns+"ModuleInstances",instances.Select(i=>new XElement(ns+"ModuleInstance",new XAttribute("Id",i.Id),new XAttribute("RefId",i.RefId),new XAttribute("RepeatIndex",i.RepeatIndex),
            new XElement(ns+"Arguments",i.Arguments.Select(a=>new XElement(ns+"Argument",new XAttribute("RefId",a.RefId),new XAttribute("Value",a.Value))))))));
        var nativePlan=Preview(native);Check("complete exact native module carriers are validated",nativePlan.IsAvailable&&Bytes(Memory(nativePlan).WorkingData,expected));
        device.Descendants(ns+"Argument").First().SetAttributeValue("Value","900");
        Check("changed native module argument refuses output and stales prior binding",Empty(Preview(native))&&!nativePlan.Binding!.IsCurrent(native.Project,native.Device));
        device.Descendants(ns+"Argument").First().SetAttributeValue("Value",instances[0].Arguments[0].Value);
        device.Descendants(ns+"ModuleInstance").First().SetAttributeValue("UnknownPersistentState","1");Check("unknown native module metadata cannot disappear",Empty(Preview(native)));
        device.Descendants(ns+"ModuleInstance").First().Attribute("UnknownPersistentState")!.Remove();device.Descendants(ns+"ModuleInstance").Last().Remove();Check("partial native module carrier sets are refused",Empty(Preview(native)));
        Refuse("overlapping instances refuse the whole population",a=>a.Descendants(Ns+"Module").Last().Element(Ns+"NumericArg")!.SetAttributeValue("Value",8));
        Refuse("aliases in one module cannot be skipped",a=>ModuleStatic(a).Element(Ns+"ParameterRefs")!.Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_Alias"),new XAttribute("RefId","MD_P0"))));
        Refuse("uncovered latent module owner refuses the entire alternative",a=>ModuleStatic(a).Descendants(Ns+"Parameter").Single(p=>(string?)p.Attribute("Id")=="MD_PL").Element(Ns+"Memory")!.SetAttributeValue("Offset",600));
        Refuse("unaccounted local code remains unavailable",a=>ModuleStatic(a).Add(new XElement(Ns+"Code")));
        Refuse("unknown module-definition carriers cannot disappear",a=>a.Descendants(Ns+"ModuleDef").Single().Add(new XElement(Ns+"RelocationCarrier",new XAttribute("Offset",4))));
        Refuse("unaccounted property storage remains unavailable",a=>ModuleStatic(a).Descendants(Ns+"Memory").First().Name=Ns+"Property");
        Refuse("unused definitions remain a visible unsupported declaration",a=>{var d=new XElement(a.Descendants(Ns+"ModuleDef").Single());d.SetAttributeValue("Id","Unused");a.Descendants(Ns+"ModuleDefs").Single().Add(d);});
        Refuse("conditional module calls await conservative latent allocation proof",a=>{var call=a.Descendants(Ns+"Module").Last();call.ReplaceWith(new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),new XElement(Ns+"when",new XAttribute("test","0"),new XElement(call))));});
        Refuse("repeat declarations without a source identity remain refused",a=>{var call=a.Descendants(Ns+"Module").Last();call.ReplaceWith(new XElement(Ns+"Repeat",new XAttribute("Count",2),new XElement(call)));});
        Refuse("allocators outside their declared application container remain refused",a=>NativeGeneralPopulationChecks.Stat(a).Add(new XElement(Ns+"Allocator",new XAttribute("Id","AL"))));
        Refuse("257 module occurrences are rejected before payloads",a=>{var d=NativeGeneralPopulationChecks.Program(a).Element(Ns+"Dynamic")!;var call=d.Elements(Ns+"Module").First();d.Elements(Ns+"Module").Last().Remove();foreach(var i in Enumerable.Range(2,256)){var c=new XElement(call);c.SetAttributeValue("Id","Call"+i);d.Add(c);}});
        Console.WriteLine($"Module image: {checks} checks, {failures} failures.");return failures;
    }
}

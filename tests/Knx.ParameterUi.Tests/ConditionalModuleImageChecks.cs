using System.Xml.Linq;
using Knx;

internal static class ConditionalModuleImageChecks
{
    private static readonly XNamespace Ns=PropertySourceChecks.Ns;
    internal static XDocument App(string behavior="DefaultValue")
    {
        var app=NativeModuleImageBuildChecks.Application();var program=NativeGeneralPopulationChecks.Program(app);var stat=NativeGeneralPopulationChecks.Stat(app);
        stat.Element(Ns+"Options")!.SetAttributeValue("DownloadInvisibleParameters",behavior);
        stat.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","Selector"),
            new XElement(Ns+"TypeRestriction",new XAttribute("SizeInBit",8),new XAttribute("Base","Value"),
                Enumerable.Range(1,3).Select(i=>new XElement(Ns+"Enumeration",new XAttribute("Id","E"+i),new XAttribute("Value",i),new XAttribute("Text","Branch"+i))))));
        var parameters=stat.Element(Ns+"Parameters")!;parameters.Elements().Where(p=>(string?)p.Attribute("Id") is not ("P0" or "P1")).Remove();
        parameters.Elements().First().SetAttributeValue("ParameterType","Selector");parameters.Elements().First().SetAttributeValue("Value",1);
        stat.Element(Ns+"ParameterRefs")!.Elements().Where(r=>(string?)r.Attribute("Id") is not ("R0" or "R1")).Remove();
        var code=stat.Element(Ns+"Code")!.Element(Ns+"AbsoluteSegment")!;code.SetAttributeValue("Size",64);code.Element(Ns+"Data")!.Value=Convert.ToBase64String(new byte[64]);code.Element(Ns+"Mask")!.Value=Convert.ToBase64String(new byte[64]);
        var dynamic=program.Element(Ns+"Dynamic")!;dynamic.Elements(Ns+"ParameterRefRef").Where(r=>(string?)r.Attribute("RefId") is not ("R0" or "R1")).Remove();
        var calls=dynamic.Elements(Ns+"Module").ToArray();calls.Remove();
        dynamic.Add(new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),
            new XElement(Ns+"when",new XAttribute("test",1),calls[0]),new XElement(Ns+"when",new XAttribute("test",2),calls[1])));
        var moduleDynamic=program.Descendants(Ns+"ModuleDef").Single().Element(Ns+"Dynamic")!;
        moduleDynamic.Elements().Where(n=>n.Name!=Ns+"ParameterRefRef").Remove();
        return app;
    }
    private static ArchivedImageSourceChecks.Fixture Load(string directory,string behavior="DefaultValue",Action<XDocument>? edit=null)
    {
        var app=App(behavior);edit?.Invoke(app);var master=NativeGeneralImageBuildChecks.MixedMaster();master.Descendants(Ns+"MemorySegment").Single().SetAttributeValue("Length",64);
        return NativeGeneralImageBuildChecks.Mixed(directory,app,master);
    }
    private static bool Empty(NativeImageBuildPlan p)=>!p.IsAvailable&&p.Binding is null&&p.Variants.All(v=>v.Chunks.Count==0)
        &&!p.CompleteApplicationImage&&!p.ActualOptionsKnown&&!p.DownloadEligible&&!p.ExecutionEligible;
    internal static int Run(string directory,string? goldenOutput=null)
    {
        var failures=0;var checks=0;void Check(string name,bool ok){checks++;Console.WriteLine((ok?"PASS ":"FAIL ")+"Conditional modules "+name);if(!ok)failures++;}
        var goldenRows=new List<object>();
        NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture f,uint word=1)=>OfflineNativeImageBuild.Preview(f.Project,f.Device,new([word]));
        foreach(var behavior in new[]{"DefaultValue","Background","None"})foreach(var selector in new[]{1,2,3})foreach(var saved in new[]{false,true})
        {
            var f=Load(directory,behavior);f.Device.ParameterValues["R0"]=selector.ToString();
            if(saved){f.Device.ParameterValues["Call1_MI-1_R0"]="31";f.Device.ParameterValues["Call2_MI-1_R0"]="41";}
            var before=Knxproj.ToXml(f.Project).ToString();var revision=f.Project.ApplicationDataRevision;var p=Preview(f);
            var label=behavior+"/"+selector+"/saved="+saved;
            Check("one shared pipeline admits "+label,p.IsAvailable);
            if(!p.IsAvailable){Console.WriteLine("ISSUES "+string.Join(',',p.Issues.Concat(p.Variants.SelectMany(v=>v.Issues)).Select(i=>i.Code+":"+i.ActivationIssue?.Code+":"+i.RefId)));continue;}
            var variant=p.Variants.Single();var pop=variant.Population!;var memory=variant.Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory);
            if(goldenOutput is not null)goldenRows.Add(new{behavior,selector,saved,data=memory.WorkingData.ToHexString(),validity=memory.DesiredValidity!.ToHexString(),
                writes=memory.ParameterWriteBits.ToHexString(),canonical=memory.CanonicalDesired.ToHexString(),initialData=memory.InitialData.ToHexString(),initialValidity=memory.InitialValidity!.ToHexString()});
            var data=new byte[64];var mask=new byte[64];data[0]=(byte)selector;data[1]=1;mask[0]=mask[1]=255;
            if(selector is 1 or 2){var offset=selector==1?8:16;data[offset]=(byte)(saved?(selector==1?31:41):7);mask[offset]=255;if(behavior=="DefaultValue")data[offset+1]=11;if(behavior!="None")mask[offset+1]=255;}
            var writes=(byte[])mask.Clone();if(behavior=="Background"&&selector is 1 or 2)writes[(selector==1?8:16)+1]=0;
            Check("whole64 data/validity/mask/canonical goldens "+label,memory.WorkingData.ToArray().SequenceEqual(data)&&memory.CanonicalDesired.ToArray().SequenceEqual(data)
                &&memory.DesiredValidity!.ToArray().SequenceEqual(mask)&&memory.ParameterWriteBits.ToArray().SequenceEqual(writes)
                &&memory.InitialData.ToArray().SequenceEqual(new byte[64])&&memory.InitialValidity!.ToArray().SequenceEqual(new byte[64]));
            Check("potential geometry and instantiated subset are separate "+label,pop.ModuleOrigins.Count==10&&pop.PotentialModuleInstances.Count==2
                &&pop.ModuleInstances.Count==(selector==3?0:1)&&pop.ModuleNotInstantiatedCount==(selector==3?4:2)&&pop.ReferenceCount==6&&pop.OwnerCount==8);
            Check("dormant references emit no record/claim even with retained saved values "+label,pop.Records.Where(r=>r.Disposition==RootParameterDisposition.ModuleNotInstantiated)
                .All(r=>!r.Active&&!r.NativeRecordEmitted&&!r.Allocates&&!r.WritesData&&r.RecordValueHash is null));
            Check("model and authority remain unchanged "+label,before==Knxproj.ToXml(f.Project).ToString()&&revision==f.Project.ApplicationDataRevision
                &&!p.ActualOptionsKnown&&!p.CompleteApplicationImage&&!p.DownloadEligible&&!p.ExecutionEligible&&p.Binding!.IsCurrent(f.Project,f.Device));
        }
        foreach (var behavior in new[] { "DefaultValue", "Background", "None" })
        foreach (var selector in new[] { 1, 2, 3 })
        {
            var seed = Enumerable.Range(0, 64).Select(index => (byte)(0x80 + index)).ToArray();
            var preserved = Load(directory, behavior, app =>
            {
                var segment = NativeGeneralPopulationChecks.Stat(app).Element(Ns + "Code")!.Element(Ns + "AbsoluteSegment")!;
                segment.Element(Ns + "Data")!.Value = Convert.ToBase64String(seed);
                segment.Element(Ns + "Mask")!.Value = Convert.ToBase64String(Enumerable.Repeat((byte)255, 64).ToArray());
            });
            preserved.Device.ParameterValues["R0"] = selector.ToString();
            var plan = Preview(preserved);
            var chunk = plan.IsAvailable ? plan.Variants[0].Chunks.Single(item => item.Target.Space == NativeImageBuildSpace.StandardMemory) : null;
            var offsets = selector == 1 ? new[] { 16, 17, 18 } : selector == 2 ? new[] { 8, 9, 10 } : new[] { 8, 9, 10, 16, 17, 18 };
            // This profile starts parameter validity uncommitted; an archive Mask is not a trusted validity seed.
            Check("unselected scopes preserve nonzero backing/validity and emit no write bits " + behavior + "/" + selector,
                chunk is not null && offsets.All(offset => chunk.InitialData[offset] == seed[offset]
                    && chunk.InitialValidity![offset] == 0 && chunk.WorkingData[offset] == seed[offset]
                    && chunk.CanonicalDesired[offset] == 0 && chunk.DesiredValidity![offset] == 0
                    && chunk.ParameterWriteBits[offset] == 0));
        }
        var stale=Load(directory);stale.Device.ParameterValues["Call2_MI-1_R0"]="41";var first=Preview(stale);
        stale.Device.ParameterValues["R0"]="2";Check("changing the selected branch stales the old complete binding",first.IsAvailable&&!first.Binding!.IsCurrent(stale.Project,stale.Device));
        var reactivated=Preview(stale);Check("fresh activation uses the retained saved value without deleting other scopes",reactivated.IsAvailable
            &&reactivated.Variants[0].Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory).WorkingData[16]==41&&stale.Device.ParameterValues["Call2_MI-1_R0"]=="41");
        var skipped=Preview(stale,128);Check("skipped writers retain potential and actual instance facts",skipped.IsAvailable&&skipped.Variants[0].Population is{Records.Count:0,ModuleOrigins.Count:10,PotentialModuleInstances.Count:2,ModuleInstances.Count:1});
        var hidden=Load(directory);var hiddenPlan=Preview(hidden);hidden.Device.ParameterValues["Call2_MI-1_R0"]="44";
        var hiddenChanged=Preview(hidden);Check("changed dormant saved values stale the binding while leaving bytes unchanged",hiddenPlan.IsAvailable&&hiddenChanged.IsAvailable
            &&!hiddenPlan.Binding!.IsCurrent(hidden.Project,hidden.Device)&&hiddenPlan.Variants[0].Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory).WorkingData.ToArray()
                .SequenceEqual(hiddenChanged.Variants[0].Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory).WorkingData.ToArray()));
        var nested=Load(directory,edit:a=>
        {
            NativeGeneralPopulationChecks.Stat(a).Element(Ns+"Parameters")!.Elements().Single(p=>(string?)p.Attribute("Id")=="P1").SetAttributeValue("ParameterType","Selector");
            var call=a.Descendants(Ns+"Module").First();call.ReplaceWith(new XElement(Ns+"choose",new XAttribute("ParamRefId","R1"),new XElement(Ns+"when",new XAttribute("test",1),new XElement(call))));
        });
        var nestedPlan=Preview(nested);Check("two independently proven root selectors share one selected occurrence set",nestedPlan.IsAvailable&&nestedPlan.Variants[0].Population is{ModuleInstances.Count:1,PotentialModuleInstances.Count:2});
        nested.Device.ParameterValues["R1"]="3";var nestedNone=Preview(nested);
        Check("false inner selector removes only the instantiated subset",nestedNone.IsAvailable&&nestedNone.Variants[0].Population is{ModuleInstances.Count:0,PotentialModuleInstances.Count:2,ModuleNotInstantiatedCount:4}
            &&!nestedPlan.Binding!.IsCurrent(nested.Project,nested.Device));
        var empty=Load(directory);var emptyOriginal=(XDocument)typeof(Project).GetProperty("OriginalXml",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(empty.Project)!;
        emptyOriginal.Descendants().Single(n=>n.Name.LocalName=="DeviceInstance").Add(new XElement(emptyOriginal.Root!.Name.Namespace+"ModuleInstances"));
        Check("explicit empty native carrier uses the same fresh source geometry profile",Preview(empty).IsAvailable);
        var fallback=Load(directory,edit:a=>{var branch=NativeGeneralPopulationChecks.Program(a).Element(Ns+"Dynamic")!.Element(Ns+"choose")!.Elements(Ns+"when").Last();branch.Attribute("test")!.Remove();branch.SetAttributeValue("default",true);});
        fallback.Device.ParameterValues["R0"]="3";var fallbackPlan=Preview(fallback);
        Check("a separately proved default branch instantiates its exact source call",fallbackPlan.IsAvailable&&fallbackPlan.Variants[0].Population is{ModuleInstances.Count:1,PotentialModuleInstances.Count:2}
            &&fallbackPlan.Variants[0].Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory).WorkingData[16]==7);
        foreach(var (name,edit) in new(string,Action<XDocument>)[]
        {
            ("exclusive branches cannot hide physical aliases",a=>a.Descendants(Ns+"Module").Last().Element(Ns+"NumericArg")!.SetAttributeValue("Value",8)),
            ("latent unselected fields need complete backing",a=>a.Descendants(Ns+"Parameter").Single(p=>(string?)p.Attribute("Id")=="MD_PL").Element(Ns+"Memory")!.SetAttributeValue("Offset",100)),
            ("unproved module LegacyPatchAlways remains refused",a=>a.Descendants(Ns+"ModuleDef").Single().Descendants(Ns+"Parameter").First().SetAttributeValue("LegacyPatchAlways",true)),
            ("conditional repeats remain refused",a=>{var call=a.Descendants(Ns+"Module").First();call.ReplaceWith(new XElement(Ns+"Repeat",new XAttribute("Id","ConditionalRepeat"),new XAttribute("Count",1),new XElement(call)));}),
            ("conditional allocators remain refused",a=>{NativeGeneralPopulationChecks.Stat(a).Add(new XElement(Ns+"Allocators",new XElement(Ns+"Allocator",new XAttribute("Id","Pool"),new XAttribute("Start",8),new XAttribute("maxInclusive",31))));a.Descendants(Ns+"Argument").Single().SetAttributeValue("Allocates",3);a.Descendants(Ns+"NumericArg").First().SetAttributeValue("AllocatorRefId","Pool");}),
            ("a hidden root selector cannot fabricate module activation",a=>NativeGeneralPopulationChecks.Program(a).Element(Ns+"Dynamic")!.Elements(Ns+"ParameterRefRef").Single(r=>(string?)r.Attribute("RefId")=="R0").Remove()),
            ("three root choices exceed the proven conditional profile",a=>{var call=a.Descendants(Ns+"Module").First();call.ReplaceWith(new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),new XElement(Ns+"when",new XAttribute("test",1),new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),new XElement(Ns+"when",new XAttribute("test",1),new XElement(call))))));}),
            ("257 potential calls exceed the budget even when unselected",a=>{var choice=NativeGeneralPopulationChecks.Program(a).Element(Ns+"Dynamic")!.Element(Ns+"choose")!;var template=new XElement(choice.Descendants(Ns+"Module").First());choice.Elements().Remove();choice.Add(new XElement(Ns+"when",new XAttribute("test",2),Enumerable.Range(0,257).Select(i=>{var call=new XElement(template);call.SetAttributeValue("Id","Dormant"+i);return call;})));}),
        })Check(name,Empty(Preview(Load(directory,edit:edit))));
        var invalid=Load(directory);invalid.Device.ParameterValues["Call2_MI-1_R0"]="999";Check("invalid dormant saved values fail before partial output",Empty(Preview(invalid)));
        var carrier=Load(directory);var original=(XDocument)typeof(Project).GetProperty("OriginalXml",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(carrier.Project)!;
        var ns=original.Root!.Name.Namespace;original.Descendants(ns+"DeviceInstance").Single().Add(new XElement(ns+"ModuleInstances",new XElement(ns+"ModuleInstance",new XAttribute("Id","Call1_MI-1"),new XAttribute("RefId","Call1"))));
        Check("nonempty native carriers remain outside the first conditional profile",Empty(Preview(carrier)));
        if(goldenOutput is not null)File.WriteAllText(goldenOutput,System.Text.Json.JsonSerializer.Serialize(goldenRows));
        Console.WriteLine($"Conditional modules: {checks} checks, {failures} failures.");return failures;
    }
}

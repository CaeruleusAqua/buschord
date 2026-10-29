using System.Xml.Linq;
using Knx;

internal static class BinaryModuleControllerImageChecks
{
    private static readonly XNamespace Ns=PropertySourceChecks.Ns;
    internal static XDocument App(string behavior="DefaultValue")
    {
        var app=ConditionalModuleImageChecks.App(behavior);var stat=NativeGeneralPopulationChecks.Stat(app);
        stat.Element(Ns+"ParameterTypes")!.Elements().Single(t=>(string?)t.Attribute("Id")=="Selector").Elements().Single()
            .ReplaceWith(new XElement(Ns+"TypeNumber",new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit",1),
                new XAttribute("minInclusive",0),new XAttribute("maxInclusive",1),new XAttribute("UIHint","CheckBox")));
        NativeGeneralPopulationChecks.Program(app).Element(Ns+"Dynamic")!.Element(Ns+"choose")!.Elements(Ns+"when").Last().SetAttributeValue("test",0);
        return app;
    }
    internal static int Run(string directory,string? goldenOutput=null)
    {
        var checks=0;var failed=0;var goldens=new List<object>();
        void Check(string name,bool ok){checks++;Console.WriteLine((ok?"PASS ":"FAIL ")+"Binary module controller "+name);if(!ok)failed++;}
        ArchivedImageSourceChecks.Fixture Load(string behavior="DefaultValue",Action<XDocument>? edit=null)
        {var app=App(behavior);edit?.Invoke(app);var master=NativeGeneralImageBuildChecks.MixedMaster();master.Descendants(Ns+"MemorySegment").Single().SetAttributeValue("Length",64);return NativeGeneralImageBuildChecks.Mixed(directory,app,master);}
        NativeImageBuildPlan Preview(ArchivedImageSourceChecks.Fixture f)=>OfflineNativeImageBuild.Preview(f.Project,f.Device,new([1u]));
        bool Empty(NativeImageBuildPlan p)=>!p.IsAvailable&&p.Binding is null&&p.Variants.All(v=>v.Chunks.Count==0)&&!p.ActualOptionsKnown&&!p.CompleteApplicationImage&&!p.ExecutionEligible&&!p.DownloadEligible;
        foreach(var behavior in new[]{"DefaultValue","Background","None"})foreach(var selector in new[]{0,1})foreach(var saved in new[]{false,true})foreach(var nonzero in new[]{false,true})
        {
            var f=Load(behavior,nonzero?a=>{var segment=NativeGeneralPopulationChecks.Stat(a).Element(Ns+"Code")!.Element(Ns+"AbsoluteSegment")!;segment.Element(Ns+"Data")!.Value=Convert.ToBase64String(Enumerable.Repeat((byte)0xA5,64).ToArray());segment.Element(Ns+"Mask")!.Value=Convert.ToBase64String(Enumerable.Repeat((byte)255,64).ToArray());}:null);f.Device.ParameterValues["R0"]=selector.ToString();if(saved){f.Device.ParameterValues["Call1_MI-1_R0"]="31";f.Device.ParameterValues["Call2_MI-1_R0"]="41";}
            var xml=Knxproj.ToXml(f.Project).ToString();var revision=f.Project.ApplicationDataRevision;var plan=Preview(f);var label=behavior+"/"+selector+"/saved="+saved+"/nonzero="+nonzero;
            Check("shared image pipeline accepts declared 0/1 domain "+label,plan.IsAvailable);
            if(!plan.IsAvailable){Console.WriteLine(string.Join(',',plan.Issues.Concat(plan.Variants.SelectMany(v=>v.Issues)).Select(i=>i.Code+":"+i.ActivationIssue?.Code+":"+i.RefId)));continue;}
            var memory=plan.Variants.Single().Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory);var initialData=Enumerable.Repeat(nonzero?(byte)0xA5:(byte)0,64).ToArray();var initialValidity=new byte[64];var data=(byte[])initialData.Clone();var validity=new byte[64];var writes=new byte[64];
            var offset=selector==1?8:16;data[0]=(byte)((data[0]&0x7f)|(selector<<7));data[1]=1;data[offset]=(byte)(saved?(selector==1?31:41):7);validity[0]=validity[1]=validity[offset]=255;writes[0]=128;writes[1]=writes[offset]=255;
            if(behavior=="DefaultValue"){data[offset+1]=11;writes[offset+1]=255;}if(behavior!="None")validity[offset+1]=255;
            Check("whole64 data/canonical/validity/write-bit and initial arrays "+label,memory.WorkingData.ToArray().SequenceEqual(data)&&memory.CanonicalDesired.ToArray().SequenceEqual(data.Zip(validity,(value,mask)=>(byte)(value&mask)))
                &&memory.DesiredValidity!.ToArray().SequenceEqual(validity)&&memory.ParameterWriteBits.ToArray().SequenceEqual(writes)
                &&memory.InitialData.ToArray().SequenceEqual(initialData)&&memory.InitialValidity!.ToArray().SequenceEqual(initialValidity));
            Check("potential owners and selected subset remain complete "+label,plan.Variants[0].Population is{PotentialModuleInstances.Count:2,ModuleInstances.Count:1,ModuleNotInstantiatedCount:2,OwnerCount:8});
            Check("input model and all authority gates remain unchanged "+label,xml==Knxproj.ToXml(f.Project).ToString()&&revision==f.Project.ApplicationDataRevision&&plan.Binding!.IsCurrent(f.Project,f.Device)
                &&!plan.ActualOptionsKnown&&!plan.CompleteApplicationImage&&!plan.ExecutionEligible&&!plan.DownloadEligible);
            if(goldenOutput is not null)goldens.Add(new{behavior,selector,saved,nonzero,data=memory.WorkingData.ToHexString(),canonical=memory.CanonicalDesired.ToHexString(),validity=memory.DesiredValidity!.ToHexString(),writes=memory.ParameterWriteBits.ToHexString(),initialData=memory.InitialData.ToHexString(),initialValidity=memory.InitialValidity!.ToHexString()});
        }
        foreach(var sourceDefault in new[]{0,1}){var source=Load(edit:a=>NativeGeneralPopulationChecks.Stat(a).Element(Ns+"Parameters")!.Elements().Single(p=>(string?)p.Attribute("Id")=="P0").SetAttributeValue("Value",sourceDefault));var plan=Preview(source);Check("an ordinary uncalculated source default is selected without inventing saved state "+sourceDefault,plan.IsAvailable&&source.Device.ParameterValues.Count==0&&plan.Variants[0].Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory).WorkingData[sourceDefault==1?8:16]==7);}
        var stale=Load();var initial=Preview(stale);stale.Device.ParameterValues["R0"]="0";Check("selection changes invalidate the entire bound image",initial.IsAvailable&&!initial.Binding!.IsCurrent(stale.Project,stale.Device));
        var invalid=Load();invalid.Device.ParameterValues["R0"]="2";Check("a saved value outside the declared binary domain is refused",Empty(Preview(invalid)));
        foreach(var (name,edit) in new(string,Action<XElement>)[]{("two bits",s=>s.SetAttributeValue("SizeInBit",2)),("signed",s=>s.SetAttributeValue("Type","signedInt")),("missing bound",s=>s.Attribute("maxInclusive")!.Remove()),("nonbinary bound",s=>s.SetAttributeValue("maxInclusive",2)),("display transform",s=>s.SetAttributeValue("DisplayFactor",2)),("unknown attribute",s=>s.SetAttributeValue("Unknown",1))})
            Check(name+" stays outside this closed structural type profile",Empty(Preview(Load(edit:a=>edit(a.Descendants(Ns+"ParameterType").Single(t=>(string?)t.Attribute("Id")=="Selector").Elements().Single())))));
        var tainted=Load(edit:a=>NativeGeneralPopulationChecks.Stat(a).Add(new XElement(Ns+"ParameterCalculations",new XElement(Ns+"ParameterCalculation",new XAttribute("Id","PC"),new XAttribute("Language","JavaScript"),
            new XElement(Ns+"LRTransformation","o = 1;"),new XElement(Ns+"RLTransformation","i = 1;"),new XElement(Ns+"LParameters",new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","R1"),new XAttribute("AliasName","i"))),new XElement(Ns+"RParameters",new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","R0"),new XAttribute("AliasName","o")))))));
        var taintedPlan=Preview(tainted);Check("calculated binary selectors never use their default as a result",Empty(taintedPlan)&&taintedPlan.Issues.Any(i=>i.ActivationIssue?.Code==GeneratedTableIssueCode.UnsupportedActivationDependency));
        var typeGuard=typeof(Project).Assembly.GetType("Knx.NativeStructuralActivationProof")!.GetMethod("ModuleControllerType",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        XElement Shape(string kind,XNamespace ns)=>kind=="TypeNumber"?new XElement(ns+kind,new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit",1),new XAttribute("minInclusive",0),new XAttribute("maxInclusive",1))
            :new XElement(ns+kind,new XAttribute("Base","Value"),new XAttribute("SizeInBit",1),new XElement(ns+"Enumeration",new XAttribute("Id","Zero"),new XAttribute("Value",0)),new XElement(ns+"Enumeration",new XAttribute("Id","One"),new XAttribute("Value",1)));
        foreach(XNamespace expected in new XNamespace[]{"http://knx.org/xml/project/20","http://knx.org/xml/project/21","http://knx.org/xml/project/23"})foreach(var kind in new[]{"TypeNumber","TypeRestriction"})
            Check("declared element namespace is accepted only against its explicit project namespace "+kind+"/"+expected,(bool)typeGuard.Invoke(null,[Shape(kind,expected),expected])!);
        foreach(XNamespace foreign in new XNamespace[]{XNamespace.None,"urn:untrusted-type-shape","http://knx.org/xml/project/21"})foreach(var kind in new[]{"TypeNumber","TypeRestriction"})
        {
            var shape=Shape(kind,foreign);
            Check("declaration guard itself rejects foreign ELEMENT namespace "+kind+"/"+foreign,!(bool)typeGuard.Invoke(null,[shape,Ns])!);
            Check("whole image rejects foreign ELEMENT namespace before output "+kind+"/"+foreign,Empty(Preview(Load(edit:a=>a.Descendants(Ns+"ParameterType").Single(t=>(string?)t.Attribute("Id")=="Selector").Elements().Single().ReplaceWith(new XElement(shape))))));
        }
        if(goldenOutput is not null)File.WriteAllText(goldenOutput,System.Text.Json.JsonSerializer.Serialize(goldens));
        Console.WriteLine($"Binary module controller: {checks} checks, {failed} failures.");return failed;
    }
}

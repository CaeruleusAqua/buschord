using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml.Linq;
using Knx;

internal static class OfflineParameterPatchChecks
{
    private const string AppId = "M-0001_A-0100-10-0000";
    private static XDocument App(int bits, int bit, byte[]? data, byte[]? mask, bool signed = false, string extra = "", string dynamic = "", string? minimum = null, string? maximum = null)
    {
        minimum ??= signed ? (-(1L << (bits - 1))).ToString() : "0";
        maximum ??= (signed ? (1L << (bits - 1)) - 1 : (1L << bits) - 1).ToString();
        return XDocument.Parse($$"""
        <KNX xmlns="http://knx.org/xml/project/23"><ApplicationProgram Id="{{AppId}}"><Static><Code>
        <AbsoluteSegment Id="AS" Address="4096" Size="{{data?.Length ?? 8}}">{{(data is null ? "" : "<Data>" + Convert.ToBase64String(data) + "</Data>")}}{{(mask is null ? "" : "<Mask>" + Convert.ToBase64String(mask) + "</Mask>")}}</AbsoluteSegment>
        </Code><ParameterTypes><ParameterType Id="T"><TypeNumber Type="{{(signed ? "signedInt" : "unsignedInt")}}" SizeInBit="{{bits}}" minInclusive="{{minimum}}" maxInclusive="{{maximum}}"/></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P1" ParameterType="T" Value="{{minimum}}"><Memory CodeSegment="AS" Offset="0" BitOffset="{{bit}}"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R1" RefId="P1"/></ParameterRefs>{{extra}}</Static><Dynamic><ParameterRefRef RefId="R1"/>{{dynamic}}</Dynamic></ApplicationProgram></KNX>
        """);
    }
    private static (Project Project, DeviceInstance Device, string Path) Fixture(string directory, XDocument app)
    {
        var path = Path.Combine(directory, "patch-" + Guid.NewGuid().ToString("N") + ".knxproj");
        var project = ProjectWizard.Create(new() { Name = "Offline numeric subset", Areas = 1, LinesPerArea = 1 });
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new() { Id = "D1", ProductRefId = AppId, Address = new(1, 1, 1) });
        Knxproj.Save(project, path);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update)) using (var stream = zip.CreateEntry("M-0001/" + AppId + ".xml").Open()) app.Save(stream);
        var loaded = Knxproj.Load(path); return (loaded, ProjectQuery.DeviceList(loaded).Single(), path);
    }
    private static XDocument ManyResources(int count, int bytes)
    {
        var app=App(8,0,[0],null); var ns=app.Root!.Name.Namespace; var stat=app.Descendants(ns+"Static").Single();
        foreach(var name in new[]{"Code","Parameters","ParameterRefs"}) stat.Element(ns+name)!.RemoveNodes();
        app.Descendants(ns+"Dynamic").Single().RemoveNodes();
        var payload=Convert.ToBase64String(new byte[bytes]);
        for(var n=0;n<count;n++)
        {
            stat.Element(ns+"Code")!.Add(new XElement(ns+"AbsoluteSegment",new XAttribute("Id","S"+n),new XAttribute("Address",n*(bytes==1?16:400000)),new XAttribute("Size",Math.Min(bytes,1048575)),new XElement(ns+"Data",payload),new XElement(ns+"Mask",payload)));
            stat.Element(ns+"Parameters")!.Add(new XElement(ns+"Parameter",new XAttribute("Id","P"+n),new XAttribute("ParameterType","T"),new XAttribute("Value","0"),new XElement(ns+"Memory",new XAttribute("CodeSegment","S"+n),new XAttribute("Offset","0"),new XAttribute("BitOffset","0"))));
            stat.Element(ns+"ParameterRefs")!.Add(new XElement(ns+"ParameterRef",new XAttribute("Id","R"+n),new XAttribute("RefId","P"+n)));
        }
        return app;
    }
    internal static int Run(string directory)
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + "OfflinePatch " + name); if (!ok) failures++; }
        ParameterPatchPreview Preview(XDocument app, string value = "1", ParameterPatchByteOrder order = ParameterPatchByteOrder.BigEndian)
        { var f = Fixture(directory, app); return OfflineParameterPatch.Preview(f.Project, f.Device, [new("R1", value)], order); }
        var vectors = new (string Name, int Width, int Bit, bool Signed, string Value, byte[] Data, byte[]? Mask, ParameterPatchByteOrder Order, string After, string Field, string? AfterMask)[]
        {
            ("big16",16,0,false,"4660",[0xA5,0x5A],[1,0x80],ParameterPatchByteOrder.BigEndian,"1234","FFFF","FFFF"),
            ("little16",16,0,false,"4660",[0xA5,0x5A],[0,0],ParameterPatchByteOrder.LittleEndian,"3412","FFFF","FFFF"),
            ("msb-u3-offset2",3,2,false,"5",[0xD3],[0x80],ParameterPatchByteOrder.BigEndian,"EB","38","B8"),
            ("boolean-bit0",1,0,false,"1",[0x55],[0],ParameterPatchByteOrder.BigEndian,"D5","80","80"),
            ("boolean-bit7-clear",1,7,false,"0",[0xFF],[0x20],ParameterPatchByteOrder.BigEndian,"FE","01","21"),
            ("big12-preserves-low-nibble",12,0,false,"2748",[0x12,0x34],[1,2],ParameterPatchByteOrder.BigEndian,"ABC4","FFF0","FFF2"),
            ("cross-byte-u5",5,6,false,"21",[0xF0,0x0F],[0x80,1],ParameterPatchByteOrder.BigEndian,"F2AF","03E0","83E1"),
            ("signed8-negative",8,0,true,"-2",[0],[0],ParameterPatchByteOrder.BigEndian,"FE","FF","FF"),
            ("signed5-negative-offset1",5,1,true,"-3",[0x85],[0x40],ParameterPatchByteOrder.BigEndian,"F5","7C","7C"),
            ("signed12-minimum",12,0,true,"-2048",[0x55,0xAA],[0,0],ParameterPatchByteOrder.BigEndian,"800A","FFF0","FFF0"),
            ("unsigned32-max",32,0,false,"4294967295",[0,0,0,0],null,ParameterPatchByteOrder.BigEndian,"FFFFFFFF","FFFFFFFF",null),
            ("signed32-min",32,0,true,"-2147483648",[0,0,0,0],null,ParameterPatchByteOrder.BigEndian,"80000000","FFFFFFFF",null),
            ("big32-offset7-fivebytes",32,7,false,"305419896",[255,255,255,255,255],null,ParameterPatchByteOrder.BigEndian,"FE2468ACF1","01FFFFFFFE",null),
            ("little24",24,0,false,"1193046",[0,0,0],null,ParameterPatchByteOrder.LittleEndian,"563412","FFFFFF",null),
            ("little32",32,0,false,"2309737967",[0,0,0,0],null,ParameterPatchByteOrder.LittleEndian,"EFCDAB89","FFFFFFFF",null),
            ("mask-absent",1,7,false,"0",[255],null,ParameterPatchByteOrder.BigEndian,"FE","01",null),
            ("public-pilot-literal",16,0,false,"10",[0,0,0,0],null,ParameterPatchByteOrder.BigEndian,"000A","FFFF",null),
        };
        foreach (var v in vectors)
        {
            var result = Preview(App(v.Width,v.Bit,v.Data,v.Mask,v.Signed),v.Value,v.Order);
            Check("literal " + v.Name, result.IsAvailable && result.Ranges.Count == 1 && result.Ranges[0].AfterData.ToHexString() == v.After
                && result.Ranges[0].FieldMask.ToHexString() == v.Field && result.Ranges[0].AfterMask?.ToHexString() == v.AfterMask);
        }
        Check("signed and unsigned width overflow cannot truncate", !Preview(App(5,0,[0],null,true),"16").IsAvailable
            && !Preview(App(3,0,[0],null),"8").IsAvailable && !Preview(App(32,0,[0,0,0,0],null),"4294967296").IsAvailable);
        Check("missing data and short declared mask are different atomic failures", Preview(App(8,0,null,null)).Issues.Single().Code == ParameterPatchIssueCode.UnknownInitialData
            && Preview(App(8,0,[0,0],[0])).Issues.Single().Code == ParameterPatchIssueCode.InvalidInitialMask);
        Check("little unaligned layouts stay unsupported", Preview(App(12,0,[0,0],null),"1",ParameterPatchByteOrder.LittleEndian).Issues.Single().Code == ParameterPatchIssueCode.UnsupportedLayout
            && !Preview(App(16,1,[0,0,0],null),"1",ParameterPatchByteOrder.LittleEndian).IsAvailable);
        var declared = App(16,0,[0,0],null); var ns = declared.Root!.Name.Namespace;
        declared.Descendants(ns+"Static").Single().Add(new XElement(ns+"Options",new XAttribute("ParameterByteOrder","BigEndian")));
        Check("explicit byte order must match a declared option", !Preview(declared,"1",ParameterPatchByteOrder.LittleEndian).IsAvailable);
        var dependency = App(8,0,[0],null,dynamic:"<Assign TargetParamRefRef='R1' Value='2'/>");
        Check("assigned selection is not a literal value shortcut", Preview(dependency).Issues.Single().Code == ParameterPatchIssueCode.UnsupportedDependency);
        var calc = App(8,0,[0,0],null); var calcStatic=calc.Descendants(ns+"Static").Single();
        calcStatic.Element(ns+"Parameters")!.Add(new XElement(ns+"Parameter",new XAttribute("Id","P2"),new XAttribute("ParameterType","T"),new XAttribute("Value","0")));
        calcStatic.Element(ns+"ParameterRefs")!.Add(new XElement(ns+"ParameterRef",new XAttribute("Id","R2"),new XAttribute("RefId","P2")));
        calcStatic.Add(new XElement(ns+"ParameterCalculations",XElement.Parse("""
            <ParameterCalculation xmlns="http://knx.org/xml/project/23" Language="JavaScript"><LRTransformation>y=x;</LRTransformation>
            <LParameters><ParameterRefRef RefId="R1" AliasName="x"/></LParameters><RParameters><ParameterRefRef RefId="R2" AliasName="y"/></RParameters></ParameterCalculation>
            """)));
        Check("calculation participation is rejected without executing scripts", Preview(calc).Issues.Single().Code == ParameterPatchIssueCode.UnsupportedDependency);
        var dca = App(8,0,[0],null); dca.Descendants(ns+"Static").Single().Add(new XElement(ns+"Extension",new XElement(ns+"DCA")));
        Check("external software cannot be treated as a numeric codec", Preview(dca).Issues.Single().Code == ParameterPatchIssueCode.UnsupportedApplication);
        var disjoint = App(2,0,[0xA5],[0x80]); var stat = disjoint.Descendants(ns+"Static").Single();
        stat.Element(ns+"ParameterTypes")!.Add(XElement.Parse("<ParameterType xmlns='http://knx.org/xml/project/23' Id='T2'><TypeNumber Type='unsignedInt' SizeInBit='3' minInclusive='0' maxInclusive='7'/></ParameterType>"));
        stat.Element(ns+"Parameters")!.Add(XElement.Parse("<Parameter xmlns='http://knx.org/xml/project/23' Id='P2' ParameterType='T2' Value='0'><Memory CodeSegment='AS' Offset='0' BitOffset='4'/></Parameter>"));
        stat.Element(ns+"ParameterRefs")!.Add(new XElement(ns+"ParameterRef",new XAttribute("Id","R2"),new XAttribute("RefId","P2")));
        var pair = Fixture(directory,disjoint);
        var first = OfflineParameterPatch.Preview(pair.Project,pair.Device,[new("R1","1"),new("R2","6")],ParameterPatchByteOrder.BigEndian);
        var reversed = OfflineParameterPatch.Preview(pair.Project,pair.Device,[new("R2","6"),new("R1","1")],ParameterPatchByteOrder.BigEndian);
        Check("disjoint bit fields commute and preserve unrelated bits", first.IsAvailable && reversed.IsAvailable && first.Ranges.Single().AfterData.ToHexString()=="6D"
            && first.Ranges.Single().AfterMask?.ToHexString()=="CE" && reversed.Ranges.Single().AfterData.ToHexString()=="6D");
        var overlapping = new XDocument(disjoint); overlapping.Descendants(ns+"Parameter").Last().Element(ns+"Memory")!.SetAttributeValue("BitOffset","1");
        var blocked=Preview(overlapping); Check("unselected overlaps reject atomically without partial bytes", !blocked.IsAvailable && blocked.Binding is null && blocked.Fields.Count==0 && blocked.Ranges.Count==0
            && blocked.Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        var alias = App(8,0,[0],null); alias.Descendants(ns+"ParameterRefs").Single().Add(new XElement(ns+"ParameterRef",new XAttribute("Id","R2"),new XAttribute("RefId","P1")));
        Check("parameter aliases do not choose a winner by visibility", Preview(alias).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        var owner=App(8,0,[0],null); owner.Descendants(ns+"Code").Single().Add(new XElement(ns+"AbsoluteSegment",new XAttribute("Id","Other"),new XAttribute("Address","4096"),new XAttribute("Size","1")));
        Check("an overlapping resource without parameter refs is still ambiguous", Preview(owner).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        owner.Descendants(ns+"AbsoluteSegment").Last().Attribute("Size")!.Remove();
        Check("unknown resource extent cannot prove unique ownership", Preview(owner).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        var hidden=App(8,0,[0],null); hidden.Descendants(ns+"Parameter").Single().SetAttributeValue("IsVisible","false");
        Check("visibility does not define subset activation", Preview(hidden).IsAvailable);
        var fixture=Fixture(directory,App(8,0,[0x55],null)); var before=Knxproj.ToXml(fixture.Project).ToString(); var bytes=File.ReadAllBytes(fixture.Path);
        var history=new UndoStack(); history.Reset(fixture.Project);
        var preview=OfflineParameterPatch.Preview(fixture.Project,fixture.Device,[new("R1","2")],ParameterPatchByteOrder.BigEndian);
        Check("preview changes neither project archive saved values nor undo", preview.IsAvailable && !history.CanUndo && fixture.Device.ParameterValues.Count==0
            && before==Knxproj.ToXml(fixture.Project).ToString() && bytes.SequenceEqual(File.ReadAllBytes(fixture.Path)) && !preview.CompleteApplicationImage && !preview.DownloadEligible);
        var leaked=preview.Resources.Single().Data.ToArray(); leaked[0]=255; var output=preview.Ranges.Single().AfterData.ToArray(); output[0]=255;
        Check("all exposed byte arrays are defensive copies", preview.Resources.Single().Data[0]==0x55 && preview.Ranges.Single().AfterData[0]==2);
        Check("live binding rejects another project and device identity", preview.Binding!.IsCurrent(fixture.Project,fixture.Device)
            && !preview.Binding.IsCurrent(Knxproj.Load(fixture.Path),fixture.Device));
        fixture.Device.ParameterValues["unrelated"]="1";
        Check("saved value changes invalidate the live binding", !preview.Binding!.IsCurrent(fixture.Project,fixture.Device));
        var invalid=OfflineParameterPatch.Preview(pair.Project,pair.Device,[new("R1","1"),new("R1","1")],ParameterPatchByteOrder.BigEndian);
        Check("duplicate caller ids are bounded before evaluation", !invalid.IsAvailable && invalid.Issues.Single().Code==ParameterPatchIssueCode.InvalidRequest);
        Check("native Number33 and Number64 widths cannot acquire a patch codec", !Preview(App(33,0,[0,0,0,0,0],null),"1").IsAvailable
            && !Preview(App(64,0,new byte[8],null),"1").IsAvailable);
        var oversized=App(8,0,new byte[1_048_577],null);
        Check("oversized encoded resources are rejected before decode", !Preview(oversized).IsAvailable);
        var badMask=App(8,0,[0],[0]); badMask.Descendants(ns+"Mask").Single().Value="not-base64";
        Check("a malformed declared mask never becomes absent", Preview(badMask).Issues.Single().Code==ParameterPatchIssueCode.InvalidInitialMask);
        var request=Enumerable.Range(0,65).Select(i=>new ParameterPatchValue("R"+i,"1")).ToArray();
        Check("input count and identity bounds reject without raw source echoes", OfflineParameterPatch.Preview(pair.Project,pair.Device,request,ParameterPatchByteOrder.BigEndian).Issues.Single().Code==ParameterPatchIssueCode.InvalidRequest
            && OfflineParameterPatch.Preview(pair.Project,pair.Device,[new(new string('x',257),"1")],ParameterPatchByteOrder.BigEndian).Issues.Single().RefId is null);
        foreach(var local in new[]{"Parameter","ParameterType","TypeNumber","Memory","AbsoluteSegment"})
        {
            var foreign=App(8,0,[0],null); foreign.Descendants(ns+local).Single().Name=XNamespace.Get("urn:foreign")+local;
            Check("foreign namespace "+local+" cannot acquire a native codec", Preview(foreign).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        }
        var typeOverride=App(8,0,[0,0],null); typeOverride.Descendants(ns+"ParameterRef").Single().SetAttributeValue("ParameterType","T16");
        typeOverride.Descendants(ns+"ParameterTypes").Single().Add(new XElement(ns+"ParameterType",new XAttribute("Id","T16"),new XElement(ns+"TypeNumber",new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit","16"),new XAttribute("minInclusive","0"),new XAttribute("maxInclusive","65535"))));
        Check("reference type overrides cannot change the native parameter width", Preview(typeOverride).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        var nested=App(8,0,[0],null); nested.Descendants(ns+"TypeNumber").Single().Add(new XElement(ns+"VendorCodec"));
        var encoding=App(8,0,[0],null); encoding.Descendants(ns+"TypeNumber").Single().SetAttributeValue("Encoding","vendor");
        Check("nested or unknown semantic number declarations remain unsupported", Preview(nested).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration
            && Preview(encoding).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        foreach(var typed in new[]{true,false})
        {
            var unreferenced=App(8,0,[0],null); var p=new XElement(ns+"Parameter",new XAttribute("Id","Unreferenced"),new XAttribute("Value","0"),new XElement(ns+"Memory",new XAttribute("CodeSegment","AS"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0")));
            if(typed)p.SetAttributeValue("ParameterType","T"); unreferenced.Descendants(ns+"Parameters").Single().Add(p);
            Check("unreferenced "+(typed?"typed":"missing-type")+" memory cannot evade overlap checks", Preview(unreferenced).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        }
        var union=App(8,0,[0],null); union.Descendants(ns+"Parameters").Single().Add(new XElement(ns+"Union",new XAttribute("SizeInBit","8"),
            new XElement(ns+"Memory",new XAttribute("CodeSegment","AS"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0")),
            new XElement(ns+"Parameter",new XAttribute("Id","UnknownUnion"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0"),new XAttribute("Value","0"))));
        Check("untyped Union members cannot lose their parent memory ownership", Preview(union).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        var legacy=App(8,0,[0],null); legacy.Descendants(ns+"Parameters").Single().Add(new XElement(ns+"ParameterInstance",new XAttribute("Id","Legacy"),
            new XElement(ns+"Memory",new XAttribute("CodeSegment","AS"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0"))));
        Check("legacy ParameterInstance declarations stay outside the native codec", Preview(legacy).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        var many=Fixture(directory,ManyResources(17,1));
        var manyResult=OfflineParameterPatch.Preview(many.Project,many.Device,Enumerable.Range(0,17).Select(n=>new ParameterPatchValue("R"+n,"1")).ToArray(),ParameterPatchByteOrder.BigEndian);
        Check("seventeen selected resources exceed the exact resource gate", manyResult.Issues.Single().Code==ParameterPatchIssueCode.ResourceLimitExceeded && manyResult.Ranges.Count==0);
        var total=Fixture(directory,ManyResources(3,1048576));
        var totalResult=OfflineParameterPatch.Preview(total.Project,total.Device,Enumerable.Range(0,3).Select(n=>new ParameterPatchValue("R"+n,"1")).ToArray(),ParameterPatchByteOrder.BigEndian);
        Check("aggregate Data plus Mask budget precedes allocation and ownership resolution", totalResult.Issues.Single().Code==ParameterPatchIssueCode.ResourceLimitExceeded && totalResult.Resources.Count==0 && totalResult.Ranges.Count==0);
        foreach(var local in new[]{"Parameter","ParameterType","ParameterRef","AbsoluteSegment"})
        {
            var container=App(8,0,[0],null); var element=container.Descendants(ns+local).Single();
            if(local=="AbsoluteSegment") { var code=element.Parent!; code.Remove(); container.Descendants(ns+"Static").Single().Add(new XElement(ns+"Wrapper",code)); }
            else { element.Remove(); container.Descendants(ns+"Static").Single().Add(element); }
            Check("consumed "+local+" must use its native root Static container", Preview(container).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        }
        var unrelated=App(8,0,[0],null); var unrelatedStatic=unrelated.Descendants(ns+"Static").Single();
        for(var n=2;n<=3;n++)
        {
            unrelatedStatic.Element(ns+"Parameters")!.Add(new XElement(ns+"Parameter",new XAttribute("Id","P"+n),new XAttribute("ParameterType","T"),new XAttribute("Value","0")));
            unrelatedStatic.Element(ns+"ParameterRefs")!.Add(new XElement(ns+"ParameterRef",new XAttribute("Id","R"+n),new XAttribute("RefId","P"+n)));
        }
        unrelatedStatic.Add(new XElement(ns+"ParameterCalculations",XElement.Parse("""
            <ParameterCalculation xmlns="http://knx.org/xml/project/23" Language="JavaScript"><LRTransformation>y=x;</LRTransformation>
            <LParameters><ParameterRefRef RefId="R2" AliasName="x"/></LParameters><RParameters><ParameterRefRef RefId="R3" AliasName="y"/></RParameters></ParameterCalculation>
            """)));
        Check("known unrelated JS bindings need no execution for the explicit subset", Preview(unrelated).IsAvailable);
        unrelated.Descendants(ns+"ParameterCalculation").Single().SetAttributeValue("Language","VBScript");
        Check("unproved VBScript dependency semantics cannot be called isolated", Preview(unrelated).Issues.Single().Code==ParameterPatchIssueCode.UnsupportedDependency);
        return failures;
    }
}

using System.IO.Compression;
using System.Xml.Linq;
using Knx;

internal static class EnumParameterPatchChecks
{
    private const string AppId="M-0001_A-0100-10-0000";
    private static readonly XNamespace Ns="http://knx.org/xml/project/23";
    private static XDocument App(int width, string[] choices, byte[] data, byte[]? mask=null, int bit=0)
    {
        var shape=new XElement(Ns+"TypeRestriction",new XAttribute("Base","Value"),new XAttribute("SizeInBit",width),new XAttribute("UIHint","DropDown"));
        for(var n=0;n<choices.Length;n++) shape.Add(new XElement(Ns+"Enumeration",new XAttribute("Id","E"+n),new XAttribute("Value",choices[n]),new XAttribute("Text","Label "+n),new XAttribute("DisplayOrder",choices.Length-n)));
        var document=XDocument.Parse($$"""
        <KNX xmlns="{{Ns}}"><ApplicationProgram Id="{{AppId}}"><Static><Code><AbsoluteSegment Id="AS" Address="18408" Size="{{data.Length}}">
        <Data>{{Convert.ToBase64String(data)}}</Data>{{(mask is null?"":"<Mask>"+Convert.ToBase64String(mask)+"</Mask>")}}
        </AbsoluteSegment></Code><ParameterTypes><ParameterType Id="T"/></ParameterTypes><Parameters>
        <Parameter Id="P" ParameterType="T" Value="0"><Memory CodeSegment="AS" Offset="0" BitOffset="{{bit}}"/></Parameter></Parameters>
        <ParameterRefs><ParameterRef Id="R" RefId="P"/></ParameterRefs></Static><Dynamic><ParameterRefRef RefId="R"/></Dynamic></ApplicationProgram></KNX>
        """);
        document.Descendants(Ns+"ParameterType").Single().Add(shape);
        document.Descendants(Ns+"Parameter").Single().SetAttributeValue("Value",choices.FirstOrDefault()??"0");
        return document;
    }
    private static (Project Project,DeviceInstance Device) Fixture(string directory,XDocument application)
    {
        var path=Path.Combine(directory,"enum-patch-"+Guid.NewGuid().ToString("N")+".knxproj");
        var project=ProjectWizard.Create(new(){Name="Declared unsigned enum subset",Areas=1,LinesPerArea=1});
        project.Installations[0].Areas[0].Lines[0].Devices.Add(new(){Id="D",ProductRefId=AppId,Address=new(1,1,1)});
        Knxproj.Save(project,path);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))using(var stream=zip.CreateEntry("M-0001/"+AppId+".xml").Open())application.Save(stream);
        var loaded=Knxproj.Load(path);return(loaded,ProjectQuery.DeviceList(loaded).Single());
    }
    internal static int Run(string directory)
    {
        var failures=0;
        void Check(string name,bool ok){Console.WriteLine((ok?"PASS ":"FAIL ")+"EnumPatch "+name);if(!ok)failures++;}
        ParameterPatchPreview Preview(XDocument app,string value="1",ParameterPatchByteOrder order=ParameterPatchByteOrder.BigEndian)
        {var f=Fixture(directory,app);return OfflineParameterPatch.Preview(f.Project,f.Device,[new("R",value)],order);}
        var be=Preview(App(16,["0","30","300"],[0xA5,0x5A]),"300");
        Check("sparse selected Value300 is012C rather than ordinal2",be.IsAvailable&&be.Ranges.Single().AfterData.ToHexString()=="012C"&&be.Ranges.Single().FieldMask.ToHexString()=="FFFF"&&be.Ranges.Single().AfterMask is null);
        var le=Preview(App(16,["0","30","300"],[0,0]),"300",ParameterPatchByteOrder.LittleEndian);
        Check("sparse little endian Value300 is2C01",le.IsAvailable&&le.Ranges.Single().AfterData.ToHexString()=="2C01");
        var reordered=App(16,["300","0","30"],[0,0]);reordered.Descendants(Ns+"Enumeration").First().SetAttributeValue("Text","different label");
        Check("labels Ids and DisplayOrder do not encode an ordinal",Preview(reordered,"300").Ranges.Single().AfterData.ToHexString()=="012C");
        foreach(var (value,order,expected) in new[]{("2147483648",ParameterPatchByteOrder.BigEndian,"80000000"),("2147483648",ParameterPatchByteOrder.LittleEndian,"00000080"),("4294967295",ParameterPatchByteOrder.BigEndian,"FFFFFFFF")})
        {var r=Preview(App(32,["0","2147483648","4294967295"],new byte[4]),value,order);Check("UInt32 selector "+value+" "+order,r.IsAvailable&&r.Ranges.Single().AfterData.ToHexString()==expected);}
        var packed=Preview(App(3,["0","5"],[0xD3],[0x80],2),"5");
        Check("MSB packed enum preserves unrelated data and mask bits",packed.IsAvailable&&packed.Ranges.Single().AfterData.ToHexString()=="EB"&&packed.Ranges.Single().FieldMask.ToHexString()=="38"&&packed.Ranges.Single().AfterMask?.ToHexString()=="B8");
        foreach(var invalid in new[]{"2","Label 2","E2","-1","0300","+300"," 300"})
            Check("nonmember or noncanonical request "+invalid,Preview(App(16,["0","30","300"],[0,0]),invalid).Issues.Single().Code==ParameterPatchIssueCode.InvalidValue);
        foreach(var invalid in new[]{"-1","01","+1"," 1","4294967296","label"})
            Check("all declarations must be canonical UInt32 "+invalid,Preview(App(32,["0",invalid],new byte[4]),"0").Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        Check("an unselected out-of-width choice invalidates the shape",Preview(App(3,["0","8"],[0]),"0").Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        Check("duplicate selectors are ambiguous even with distinct enum Ids",Preview(App(8,["1","1"],[0])).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        var ids=App(8,["0","1"],[0]);ids.Descendants(Ns+"Enumeration").Last().SetAttributeValue("Id","E0");
        Check("duplicate required Ids are rejected",Preview(ids).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        foreach(var id in new string?[]{null,"",new string('x',257),"bad name","bad\nname"})
        {var doc=App(8,["0","1"],[0]);doc.Descendants(Ns+"Enumeration").Last().SetAttributeValue("Id",id);Check("required bounded native Id "+(id?.Length.ToString()??"missing"),Preview(doc).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);}
        Check("empty choice lists are not free-form numbers",Preview(App(8,[],[0])).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        foreach(var value in new[]{"","AA=="})
        {var doc=App(8,["0","1"],[0]);doc.Descendants(Ns+"Enumeration").First().SetAttributeValue("BinaryValue",value);Check("any BinaryValue attribute excludes the numeric profile",Preview(doc).Issues.Single().Code==ParameterPatchIssueCode.UnsupportedParameter);}
        var binary=App(48,["11"],new byte[6]);binary.Descendants(Ns+"TypeRestriction").Single().SetAttributeValue("Base","BinaryValue");
        binary.Descendants(Ns+"Enumeration").Single().SetAttributeValue("BinaryValue","ESIzRFVm");
        var binaryResult=Preview(binary,"11");Check("binary selector11 cannot encode as a48-bit numeric value",binaryResult.Issues.Single().Code==ParameterPatchIssueCode.UnsupportedParameter&&binaryResult.Ranges.Count==0&&binaryResult.Binding is null);
        foreach(var width in new[]{33,64})Check("wide numeric restriction remains diagnostic-only "+width,Preview(App(width,["1"],new byte[(width+7)/8])).Issues.Single().Code==ParameterPatchIssueCode.UnsupportedParameter);
        var unknown=App(8,["0","1"],[0]);unknown.Descendants(Ns+"TypeRestriction").Single().SetAttributeValue("Encoding","signedInt");
        var child=App(8,["0","1"],[0]);child.Descendants(Ns+"Enumeration").Last().Add(new XElement(Ns+"VendorData"));
        Check("unknown semantic attrs and member children cannot acquire a codec",Preview(unknown).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration&&Preview(child).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        var foreign=App(8,["0","1"],[0]);foreign.Descendants(Ns+"Enumeration").Last().Name=XNamespace.Get("urn:foreign")+"Enumeration";
        Check("member namespaces remain bound to the native document",Preview(foreign).Issues.Single().Code==ParameterPatchIssueCode.InvalidDeclaration);
        Check("unaligned little enum layout remains unsupported",Preview(App(3,["0","1"],[0]),"1",ParameterPatchByteOrder.LittleEndian).Issues.Single().Code==ParameterPatchIssueCode.UnsupportedLayout);
        var cap=Enumerable.Range(0,4096).Select(n=>n.ToString()).ToArray();
        Check("exact4096 member bound remains usable",Preview(App(16,cap,[0,0]),"4095").IsAvailable);
        Check("4097 selected members reject before snapshot choice construction",Preview(App(16,cap.Concat(["4096"]).ToArray(),[0,0])).Issues.Single().Code==ParameterPatchIssueCode.ResourceLimitExceeded);
        var numberOnly=App(16,cap.Concat(["4096"]).ToArray(),[0,0]);var bigType=numberOnly.Descendants(Ns+"ParameterType").Single();bigType.SetAttributeValue("Id","UnrelatedEnum");
        numberOnly.Descendants(Ns+"ParameterTypes").Single().Add(new XElement(Ns+"ParameterType",new XAttribute("Id","T"),new XElement(Ns+"TypeNumber",new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit","16"),new XAttribute("minInclusive","0"),new XAttribute("maxInclusive","65535"))));
        Check("Number-only requests do not acquire a global unrelated enum gate",Preview(numberOnly).IsAvailable);
        var wide=App(64,["1"],new byte[8]);var row=Ksxml.Parse(wide).Instances.Single();
        Check("normal enum editing and64-bit geometry remain unchanged",ParameterValidation.TryValidate(row,"1",out _)&&row.Storage.Footprint is{Status:ParameterFootprintStatus.Known,BitLength:64});
        var f=Fixture(directory,App(16,["0","300"],[0,0]));var before=Knxproj.ToXml(f.Project).ToString();
        var available=OfflineParameterPatch.Preview(f.Project,f.Device,[new("R","300")],ParameterPatchByteOrder.BigEndian);
        Check("enum preview preserves project and immutable source binding",available.IsAvailable&&available.Binding!.IsCurrent(f.Project,f.Device)&&f.Device.ParameterValues.Count==0&&Knxproj.ToXml(f.Project).ToString()==before&&!available.DownloadEligible&&!available.CompleteApplicationImage);
        var atomic=OfflineParameterPatch.Preview(f.Project,f.Device,[new("R","300"),new("missing","1")],ParameterPatchByteOrder.BigEndian);
        Check("a mixed invalid subset never publishes partial enum bytes",!atomic.IsAvailable&&atomic.Binding is null&&atomic.Resources.Count==0&&atomic.Fields.Count==0&&atomic.Ranges.Count==0);
        var mixed=App(3,["0","6"],[0xA5],[0x80],4);var mixedStatic=mixed.Descendants(Ns+"Static").Single();
        mixedStatic.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","TN"),new XElement(Ns+"TypeNumber",new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit","2"),new XAttribute("minInclusive","0"),new XAttribute("maxInclusive","3"))));
        mixedStatic.Element(Ns+"Parameters")!.Add(new XElement(Ns+"Parameter",new XAttribute("Id","PN"),new XAttribute("ParameterType","TN"),new XAttribute("Value","0"),new XElement(Ns+"Memory",new XAttribute("CodeSegment","AS"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0"))));
        mixedStatic.Element(Ns+"ParameterRefs")!.Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","RN"),new XAttribute("RefId","PN")));
        var pair=Fixture(directory,mixed);
        var enumFirst=OfflineParameterPatch.Preview(pair.Project,pair.Device,[new("R","6"),new("RN","1")],ParameterPatchByteOrder.BigEndian);
        var numberFirst=OfflineParameterPatch.Preview(pair.Project,pair.Device,[new("RN","1"),new("R","6")],ParameterPatchByteOrder.BigEndian);
        Check("mixed Number2 and Enum3 fields commute with shared masks",enumFirst.IsAvailable&&numberFirst.IsAvailable
            &&enumFirst.Ranges.Single().AfterData.ToHexString()=="6D"&&enumFirst.Ranges.Single().AfterMask?.ToHexString()=="CE"
            &&numberFirst.Ranges.Single().AfterData.ToHexString()=="6D"&&numberFirst.Ranges.Single().AfterMask?.ToHexString()=="CE"
            &&enumFirst.Binding!.SubsetHash==numberFirst.Binding!.SubsetHash);
        var assign=App(8,["0","1"],[0]);assign.Descendants(Ns+"Dynamic").Single().Add(new XElement(Ns+"Assign",new XAttribute("TargetParamRefRef","R"),new XAttribute("Value","1")));
        Check("selected enum Assign participation remains rejected",Preview(assign).Issues.Single().Code==ParameterPatchIssueCode.UnsupportedDependency);
        var alias=App(8,["0","1"],[0]);alias.Descendants(Ns+"ParameterRefs").Single().Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","Alias"),new XAttribute("RefId","P")));
        Check("selected enum aliases cannot acquire different payload winners",Preview(alias).Issues.Single().Code==ParameterPatchIssueCode.AmbiguousOverlap);
        var mutations=new(string Name,Action<XDocument> Mutate,ParameterPatchIssueCode Code)[]
        {
            ("distinct unreferenced bit owner",d=>d.Descendants(Ns+"Parameters").Single().Add(new XElement(Ns+"Parameter",new XAttribute("Id","Other"),new XAttribute("ParameterType","T"),new XAttribute("Value","0"),
                new XElement(Ns+"Memory",new XAttribute("CodeSegment","AS"),new XAttribute("Offset","0"),new XAttribute("BitOffset","0")))),ParameterPatchIssueCode.AmbiguousOverlap),
            ("affected static calculation",d=>
            {
                d.Descendants(Ns+"Parameters").Single().Add(new XElement(Ns+"Parameter",new XAttribute("Id","Other"),new XAttribute("ParameterType","T"),new XAttribute("Value","0")));
                d.Descendants(Ns+"ParameterRefs").Single().Add(new XElement(Ns+"ParameterRef",new XAttribute("Id","OtherRef"),new XAttribute("RefId","Other")));
                d.Descendants(Ns+"Static").Single().Add(new XElement(Ns+"ParameterCalculations",XElement.Parse("""
                <ParameterCalculation xmlns="http://knx.org/xml/project/23" Language="JavaScript"><LRTransformation>y=x;</LRTransformation>
                <LParameters><ParameterRefRef RefId="R" AliasName="x"/></LParameters><RParameters><ParameterRefRef RefId="OtherRef" AliasName="y"/></RParameters></ParameterCalculation>
                """)));
            },ParameterPatchIssueCode.UnsupportedDependency),
            ("short declared mask",d=>d.Descendants(Ns+"AbsoluteSegment").Single().Add(new XElement(Ns+"Mask","AA==")),ParameterPatchIssueCode.InvalidInitialMask),
            ("malformed declared mask",d=>d.Descendants(Ns+"AbsoluteSegment").Single().Add(new XElement(Ns+"Mask","invalid??")),ParameterPatchIssueCode.InvalidInitialMask),
        };
        foreach(var mutation in mutations)
        {
            var doc=App(8,["0","1"],[0,0]);mutation.Mutate(doc);var rejected=Preview(doc);
            Check("shared atomic gate "+mutation.Name,!rejected.IsAvailable&&rejected.Issues.Single().Code==mutation.Code
                &&rejected.Binding is null&&rejected.Resources.Count==0&&rejected.Fields.Count==0&&rejected.Ranges.Count==0);
        }
        return failures;
    }
}

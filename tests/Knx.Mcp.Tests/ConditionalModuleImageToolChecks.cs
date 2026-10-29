using System.IO.Compression;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Bus;
using Knx.Mcp;
using F=RootPropertyBuildMcpFixture;

internal static class ConditionalModuleImageToolChecks
{
    private static readonly XNamespace Ns=F.Ns;
    private static bool Error(JsonNode n)=>n["error"] is not null||n["result"]?["isError"]?.GetValue<bool>()==true;
    private static JsonNode Build(JsonNode n)=>n["result"]!["structuredContent"]!["offlineNativeImageBuild"]!;
    private static JsonNode Population(JsonNode n)=>Build(n)["variants"]!.AsArray().Single()!["population"]!;
    private static string Hash<T>(T value)=>Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string Fixture(string directory)
    {
        // Specialize the existing synthetic combined-pipeline fixture, not a manufacturer payload.
        var path=(string)typeof(NativeModuleImageToolChecks).GetMethod("Fixture",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[directory,2,false])!;
        using var zip=ZipFile.Open(path,ZipArchiveMode.Update);
        XDocument Read(string name){using var input=zip.GetEntry(name)!.Open();return XDocument.Load(input);}
        void Write(string name,XDocument doc){zip.GetEntry(name)!.Delete();using var output=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));output.Write(doc.ToString(SaveOptions.DisableFormatting));}
        var app=Read(F.ManufacturerId+"/"+F.AppId+".xml");var stat=F.Static(app);var program=F.Program(app);
        stat.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","Selector"),
            new XElement(Ns+"TypeRestriction",new XAttribute("SizeInBit",32),new XAttribute("Base","Value"),Enumerable.Range(1,3).Select(i=>new XElement(Ns+"Enumeration",new XAttribute("Id","E"+i),new XAttribute("Value",i),new XAttribute("Text","Branch"+i))))));
        stat.Element(Ns+"Parameters")!.Elements().First().SetAttributeValue("ParameterType","Selector");stat.Element(Ns+"Parameters")!.Elements().First().SetAttributeValue("Value",1);
        stat.Element(Ns+"Allocators")!.Remove();var argument=app.Descendants(Ns+"Argument").First();argument.Attribute("Allocates")!.Remove();argument.Attribute("Alignment")!.Remove();
        var repeat=program.Descendants(Ns+"Repeat").Single();var template=repeat.Element(Ns+"Module")!;
        XElement Call(string id,int offset){var call=new XElement(template);call.SetAttributeValue("Id",id);var numeric=call.Element(Ns+"NumericArg")!;numeric.Attribute("AllocatorRefId")!.Remove();numeric.SetAttributeValue("Value",offset);return call;}
        repeat.ReplaceWith(new XElement(Ns+"choose",new XAttribute("ParamRefId","R0"),new XElement(Ns+"when",new XAttribute("test",1),Call("CallA",8)),new XElement(Ns+"when",new XAttribute("test",2),Call("CallB",16))));
        Write(F.ManufacturerId+"/"+F.AppId+".xml",app);
        var project=Read("0.xml");foreach(var value in project.Descendants().Where(n=>n.Name.LocalName=="ParameterInstanceRef"))value.SetAttributeValue("Value",1);Write("0.xml",project);
        return path;
    }
    internal static async Task Run(Action<string,bool,string?> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"buschord-conditional-mcp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            await using var server=new McpServer();var sequence=0;var descriptorCalls=0;
            var field=typeof(McpServer).GetField("_downloadDescriptorReader",BindingFlags.Instance|BindingFlags.NonPublic)!;
            Func<Task<BusScan>> blocked=()=>{descriptorCalls++;throw new InvalidOperationException("BlockedSyntheticConditionalProbe");};
            var parameters=field.FieldType.GetMethod("Invoke")!.GetParameters().Select(p=>Expression.Parameter(p.ParameterType,p.Name)).ToArray();
            field.SetValue(server,Expression.Lambda(field.FieldType,Expression.Invoke(Expression.Constant(blocked)),parameters).Compile());
            async Task<JsonNode> Tool(string name,object arguments)=>JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new{jsonrpc="2.0",id=++sequence,method="tools/call",@params=new{name,arguments}})))!)!;
            Task<JsonNode> Preview()=>Tool("download_plan",new{device_address="1.1.1",gateway="blocked-conditional-preview",native_image_build_preview=new{option_words=new[]{1u}}});
            var opened=await Tool("project_open",new{path=Fixture(directory)});check("conditional module MCP opens own source",!Error(opened),opened.ToJsonString());
            if(Error(opened)||server.Project is not {} project)return;
            var device=ProjectQuery.DeviceList(project).Single();
            foreach(var selector in new[]{1,2,3})
            {
                device.ParameterValues["R0"]=selector.ToString();var response=await Preview();var core=OfflineNativeImageBuild.Preview(project,device,new([1u]));
                check("conditional module MCP shares one valid offline Core plan selector="+selector,!Error(response)&&core.IsAvailable&&Build(response)["available"]?.GetValue<bool>()==true&&descriptorCalls==0,response.ToJsonString());
                if(Error(response)||!core.IsAvailable||Build(response)["available"]?.GetValue<bool>()!=true)continue;
                var pop=Population(response);var actual=core.Variants[0].Population!;
                check("conditional module MCP separates potential and selected instances selector="+selector,pop["potentialModuleInstancesTotal"]?.GetValue<int>()==2
                    &&pop["moduleInstancesTotal"]?.GetValue<int>()==(selector==3?0:1)&&pop["moduleNotInstantiatedCount"]?.GetValue<int>()==(selector==3?4:2)
                    &&pop["moduleOriginsTotal"]?.GetValue<int>()==10,null);
                check("conditional module MCP complete potential/selected hashes agree selector="+selector,(string?)pop["potentialModuleInstancesHash"]==Hash(actual.PotentialModuleInstances)
                    &&(string?)pop["moduleInstancesHash"]==Hash(actual.ModuleInstances)&&(string?)Build(response)["binding"]?["fingerprint"]==core.Binding?.Fingerprint,null);
                var dormant=pop["records"]!.AsArray().Where(r=>r?["disposition"]?.GetValue<string>()=="module_not_instantiated").ToArray();
                check("conditional module MCP dormant records do not advertise native writes selector="+selector,dormant.Length==(selector==3?4:2)
                    &&dormant.All(r=>r!["nativeRecordEmitted"]?.GetValue<bool>()==false&&r["writesData"]?.GetValue<bool>()==false&&r["allocates"]?.GetValue<bool>()==false),null);
                check("conditional module MCP authority and argument-redaction gates remain closed selector="+selector,Build(response)["actualOptionsKnown"]?.GetValue<bool>()==false
                    &&Build(response)["completeApplicationImage"]?.GetValue<bool>()==false&&Build(response)["executionEligible"]?.GetValue<bool>()==false&&Build(response)["downloadEligible"]?.GetValue<bool>()==false
                    &&!response.ToJsonString().Contains("PrivateModuleArgumentCanary",StringComparison.Ordinal),null);
            }
            check("conditional module MCP never called descriptor I/O",descriptorCalls==0,null);
        }
        finally{Directory.Delete(directory,true);}
    }
}

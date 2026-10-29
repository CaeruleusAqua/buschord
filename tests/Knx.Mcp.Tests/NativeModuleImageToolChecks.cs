using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Bus;
using Knx.Mcp;
using F = RootPropertyBuildMcpFixture;

internal static class NativeModuleImageToolChecks
{
    private static readonly XNamespace Ns = F.Ns;
    private const string Canary = "PrivateModuleArgumentCanary";
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static string HashBytes(ImmutablePatchBytes bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    private static bool Error(JsonNode result) => result["error"] is not null || result["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonNode Build(JsonNode result) => result["result"]!["structuredContent"]!["offlineNativeImageBuild"]!;
    private static JsonNode Population(JsonNode result) => Build(result)["variants"]!.AsArray().First(v=>v?["available"]?.GetValue<bool>()==true)!["population"]!;
    private static string Fixture(string directory, int count, bool unsupported = false)
        => F.CreateArchive(directory, "modules-" + count + (unsupported ? "-unsupported" : ""), count: 1,
            editApplication: app =>
            {
                var program=F.Program(app);var stat=F.Static(app);
                stat.Element(Ns+"ParameterTypes")!.Add(new XElement(Ns+"ParameterType",new XAttribute("Id","Byte"),
                    new XElement(Ns+"TypeNumber",new XAttribute("Type","unsignedInt"),new XAttribute("SizeInBit",8),new XAttribute("minInclusive",0),new XAttribute("maxInclusive",255))));
                stat.Add(new XElement(Ns+"Code",new XElement(Ns+"AbsoluteSegment",new XAttribute("Id","CM"),new XAttribute("Address",100),new XAttribute("Size",2048),
                    new XElement(Ns+"Data","AQ=="),new XElement(Ns+"Mask","AA=="))),
                    new XElement(Ns+"Allocators",new XElement(Ns+"Allocator",new XAttribute("Id","Pool"),new XAttribute("Start",9),new XAttribute("maxInclusive",2047))));
                XElement Parameter(string id,int offset,int value)=>new(Ns+"Parameter",new XAttribute("Id",id),new XAttribute("ParameterType","Byte"),new XAttribute("Value",value),
                    new XElement(Ns+"Memory",new XAttribute("CodeSegment","CM"),new XAttribute("Offset",offset),new XAttribute("BitOffset",0),new XAttribute("BaseOffset","MD_A")));
                program.Add(new XElement(Ns+"ModuleDefs",new XElement(Ns+"ModuleDef",new XAttribute("Id","MD"),
                    new XElement(Ns+"Arguments",new XElement(Ns+"Argument",new XAttribute("Id","MD_A"),new XAttribute("Type","Numeric"),new XAttribute("Allocates",3),new XAttribute("Alignment",4)),
                        new XElement(Ns+"Argument",new XAttribute("Id","MD_Label"),new XAttribute("Type","Text"))),
                    new XElement(Ns+"Static",new XElement(Ns+"Parameters",Parameter("MD_P0",0,7),Parameter("MD_P1",1,11),Parameter("MD_Latent",2,99)),
                        new XElement(Ns+"ParameterRefs",new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_R0"),new XAttribute("RefId","MD_P0")),
                            new XElement(Ns+"ParameterRef",new XAttribute("Id","MD_R1"),new XAttribute("RefId","MD_P1")))),
                    new XElement(Ns+"Dynamic",new XElement(Ns+"ParameterRefRef",new XAttribute("RefId","MD_R0"))))));
                program.Element(Ns+"Dynamic")!.Add(new XElement(Ns+"Repeat",new XAttribute("Id","Repeat"),new XAttribute("Count",count),
                    new XElement(Ns+"Module",new XAttribute("Id","Call"),new XAttribute("RefId","MD"),
                        new XElement(Ns+"NumericArg",new XAttribute("RefId","MD_A"),new XAttribute("Value",0),new XAttribute("AllocatorRefId","Pool")),
                        new XElement(Ns+"TextArg",new XAttribute("RefId","MD_Label"),new XAttribute("Value",Canary)))));
                if(unsupported)app.Descendants(Ns+"ModuleDef").Single().Add(new XElement(Ns+"UnaccountedCarrier"));
            },
            editMaster: master => master.Descendants(Ns+"HawkConfigurationData").Single().Add(new XElement(Ns+"MemorySegments",
                new XElement(Ns+"MemorySegment",new XAttribute("MemoryType","EEPROM"),new XAttribute("Length",2048),new XAttribute("Optional",false),
                    new XElement(Ns+"Location",new XAttribute("AddressSpace","StandardMemory"),new XAttribute("StartAddress",100)),
                    new XElement(Ns+"AccessRights",new XAttribute("Read","Runtime"),new XAttribute("Write","Configuration")))),
                new XElement(Ns+"Resources",new[]{"GroupAddressTablePtr","GroupAssociationTablePtr","GroupObjectTablePtr","ApplicationDataPtr","PeiprogDataPtr"}
                    .Select((name,index)=>new XElement(Ns+"Resource",new XAttribute("Name",name),new XAttribute("Access","remote"),
                        new XElement(Ns+"Location",new XAttribute("AddressSpace","SystemProperty"),new XAttribute("StartAddress",0),new XAttribute("InterfaceObjectRef",index+1),new XAttribute("PropertyID",7)),
                        new XElement(Ns+"ResourceType",new XAttribute("Length",4)))))));

    internal static async Task Run(Action<string,bool,string?> check)
    {
        var directory=Path.Combine(Path.GetTempPath(),"buschord-module-mcp-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            await using var server=new McpServer();var sequence=0;
            var descriptorCalls=0;
            typeof(McpServer).GetField("_downloadDescriptorReader",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(server,
                (Func<JsonElement,int,IndividualAddress,ScanOptions,Task<BusScan>>)((_,_,_,_)=>{descriptorCalls++;throw new InvalidOperationException("BlockedSyntheticDescriptorProbe");}));
            async Task<JsonNode> Tool(string name,object arguments)=>JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            {jsonrpc="2.0",id=++sequence,method="tools/call",@params=new{name,arguments}})))!)!;
            Task<JsonNode> Preview(params uint[] words)=>Tool("download_plan",new{device_address="1.1.1",gateway="invalid-offline-preview-gateway",native_image_build_preview=new{option_words=words}});
            bool Safe(JsonNode response)=>!Error(response)&&response["result"]!["structuredContent"]!["writesEnabled"]?.GetValue<bool>()==false
                &&Build(response)["actualOptionsKnown"]?.GetValue<bool>()==false&&Build(response)["completeApplicationImage"]?.GetValue<bool>()==false
                &&Build(response)["executionEligible"]?.GetValue<bool>()==false&&Build(response)["downloadEligible"]?.GetValue<bool>()==false;
            var opened=await Tool("project_open",new{path=Fixture(directory,3)});check("module image MCP opens own synthetic source",!Error(opened),opened.ToJsonString());
            if(Error(opened)||server.Project is not {} project)return;
            var device=ProjectQuery.DeviceList(project).Single();var before=Knxproj.ToXml(project).ToString();var revision=project.ApplicationDataRevision;
            var response=await Preview(1);var core=OfflineNativeImageBuild.Preview(project,device,new([1u]));
            check("module image MCP uses the shared public plan and stays offline",Safe(response)&&core.IsAvailable&&Build(response)["available"]?.GetValue<bool>()==true
                &&(string?)Build(response)["binding"]?["fingerprint"]==core.Binding?.Fingerprint&&descriptorCalls==0,response.ToJsonString());
            if(Error(response)||!core.IsAvailable||Build(response)["available"]?.GetValue<bool>()!=true)return;
            var pop=Population(response);var publicPop=core.Variants.First(v=>v.IsAvailable).Population!;
            check("module image MCP complete role hashes match immutable Core inventories",(string?)pop["moduleOriginsHash"]==Hash(publicPop.ModuleOrigins)
                &&(string?)pop["moduleInstancesHash"]==Hash(publicPop.ModuleInstances)&&(string?)pop["moduleAllocationsHash"]==Hash(publicPop.ModuleAllocations)
                &&(string?)pop["moduleReservationsHash"]==Hash(publicPop.ModuleReservations)&&(string?)pop["moduleOriginsFingerprint"]==publicPop.ModuleOriginsFingerprint,null);
            check("module image MCP exposes complete instance origin allocation and reservation totals",pop["moduleOriginsTotal"]?.GetValue<int>()==15
                &&pop["moduleInstancesTotal"]?.GetValue<int>()==3&&pop["moduleAllocationsTotal"]?.GetValue<int>()==3&&pop["moduleReservationsTotal"]?.GetValue<int>()==3,null);
            check("module image MCP includes latent physical origins and concrete addresses",pop["moduleOrigins"]!.AsArray().Count(o=>o?["referenceId"] is null&&o?["field"] is not null)==9
                &&pop["moduleOrigins"]!.AsArray().Where(o=>o?["referenceId"] is null).Select(o=>o!["field"]!["byteOffset"]!.GetValue<uint>()).Order().SequenceEqual(new uint[]{112,113,114,116,117,118,120,121,122}),null);
            check("module image MCP reports allocator geometry and complete reservations",pop["moduleAllocations"]!.AsArray().Select(a=>a!["allocatedValue"]!.GetValue<long>()).SequenceEqual(new long[]{12,16,20})
                &&pop["moduleAllocations"]!.AsArray().All(a=>a!["alignment"]!.GetValue<long>()==4&&a["allocates"]!.GetValue<long>()==3&&a["usage"]!.GetValue<string>()=="MemoryByteOffset")
                &&pop["moduleReservations"]!.AsArray().All(r=>r!["field"]!["bitLength"]!.GetValue<uint>()==24),null);
            check("module image MCP arguments are hashed and never returned in clear",!response.ToJsonString().Contains(Canary,StringComparison.Ordinal)
                &&pop["moduleInstances"]!.AsArray().All(i=>i!["arguments"] is null&&i["argumentsTotal"]?.GetValue<int>()==2&&i["argumentsHash"]?.GetValue<string>().Length==64),null);
            var memory=core.Variants.First(v=>v.IsAvailable).Chunks.Single(c=>c.Target.Space==NativeImageBuildSpace.StandardMemory);var projected=Build(response)["variants"]!.AsArray().First(v=>v?["available"]?.GetValue<bool>()==true)!["chunks"]!.AsArray().Single(c=>c?["target"]?["space"]?.GetValue<string>()=="standard_memory")!;
            check("module image MCP hashes cover the whole shared data validity and write masks",(string?)projected["workingDataHash"]==HashBytes(memory.WorkingData)
                &&(string?)projected["desiredValidityHash"]==HashBytes(memory.DesiredValidity!)&&(string?)projected["parameterWriteBitsHash"]==HashBytes(memory.ParameterWriteBits),null);
            var skipped=await Preview(128);check("module image MCP skipped writers retain source origins",Safe(skipped)&&Build(skipped)["available"]?.GetValue<bool>()==true
                &&Population(skipped)["moduleOriginsTotal"]?.GetValue<int>()==15&&Population(skipped)["recordsTotal"]?.GetValue<int>()==0,null);
            check("module image MCP preview preserves model values and source revision",before==Knxproj.ToXml(project).ToString()&&revision==project.ApplicationDataRevision,null);
            const string procedure="{\"subtype\":\"all\",\"access\":\"remote\",\"full\":true,\"parameters\":false,\"groups\":false}";
            foreach(var (name,options) in new (string,string)[]
            {
                ("subset","\"parameter_subset\":[{\"refId\":\"R0\",\"rawValue\":\"1\"}],\"byte_order\":\"big_endian\""),
                ("resource subset","\"parameter_subset\":[{\"refId\":\"R0\",\"rawValue\":\"1\"}],\"byte_order\":\"big_endian\",\"resource_preview\":true"),
                ("property subset","\"parameter_subset\":[{\"refId\":\"R0\",\"rawValue\":\"1\"}],\"byte_order\":\"big_endian\",\"property_preview\":true"),
                ("procedure","\"load_procedure_preview\":"+procedure),
                ("composition","\"image_composition_preview\":{\"mask_policy\":\"source_defined_bits\"}"),
                ("root image","\"root_image_build_preview\":{\"mask_policy\":\"source_defined_bits\"},\"load_procedure_preview\":"+procedure),
                ("resolved root image","\"root_resolved_image_build_preview\":{\"mask_policy\":\"source_defined_bits\",\"procedure\":"+procedure+"}"),
                ("root property","\"root_property_image_build_preview\":{\"procedure\":"+procedure+"}"),
            })
            {
                var arguments=JsonNode.Parse("{\"device_address\":\"1.1.1\",\"gateway\":\"blocked-synthetic-gateway\","+options+"}")!;
                var offline=await Tool("download_plan",arguments);
                check("explicit "+name+" preview never invokes discovery/connect descriptor I/O",!Error(offline)&&descriptorCalls==0
                    &&offline["result"]!["structuredContent"]!["descriptor"] is null,Error(offline)?offline.ToJsonString():null);
            }
            var manyOpened=await Tool("project_open",new{path=Fixture(directory,130)});check("module image MCP opens the bounded detail fixture",!Error(manyOpened),manyOpened.ToJsonString());
            var many=await Preview(1,129);check("module image MCP truncates details without widening authority",Safe(many)&&Build(many)["available"]?.GetValue<bool>()==true,many.ToJsonString());
            if(!Error(many)&&Build(many)["available"]?.GetValue<bool>()==true)
            {
                foreach(var role in new[]{"moduleOrigins","moduleInstances","moduleAllocations","moduleReservations"})
                {
                    var rows=Build(many)["variants"]!.AsArray().Where(v=>v?["population"] is not null).Select(v=>v!["population"]!).ToArray();
                    check("module image MCP shares the "+role+" budget across alternatives",rows.Sum(p=>p[role+"Shown"]!.GetValue<int>())==128
                        &&rows.All(p=>p[role+"Total"]!.GetValue<int>()>128&&p[role+"Truncated"]!.GetValue<bool>()&&p[role]!.AsArray().Count==p[role+"Shown"]!.GetValue<int>()),null);
                }
                var manyCore=OfflineNativeImageBuild.Preview(server.Project!,ProjectQuery.DeviceList(server.Project!).Single(),new([1u,129u]));
                check("module image MCP complete hashes include undisplayed instances and arguments",(string?)Population(many)["moduleInstancesHash"]==Hash(manyCore.Variants.First(v=>v.IsAvailable).Population!.ModuleInstances)
                    &&!many.ToJsonString().Contains(Canary,StringComparison.Ordinal),null);
            }
            var badOpened=await Tool("project_open",new{path=Fixture(directory,3,true)});var bad=await Preview(1);
            check("module image MCP refuses unknown carriers without partial alternatives",!Error(badOpened)&&Safe(bad)&&Build(bad)["available"]?.GetValue<bool>()==false
                &&Build(bad)["binding"] is null&&Build(bad)["variantsTotal"]?.GetValue<int>()==0,bad.ToJsonString());
            check("all module preview paths keep the descriptor transport blocked",descriptorCalls==0,null);
            var ordinary=await Tool("download_plan",new{device_address="1.1.1",gateway="blocked-synthetic-gateway"});
            check("ordinary preflight still invokes only the injected descriptor reader",Error(ordinary)&&descriptorCalls==1
                &&ordinary.ToJsonString().Contains("ConnectionFailed",StringComparison.Ordinal)
                &&!ordinary.ToJsonString().Contains("BlockedSyntheticDescriptorProbe",StringComparison.Ordinal),null);
        }
        finally{Directory.Delete(directory,true);}
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;

namespace Knx;

/// <summary>Original-source proof for literal repeats and application-owned allocator pools.</summary>
internal static class NativeModuleImageDeclarations
{
    private sealed record Pool(long Start, long Maximum);
    private sealed record Call(XElement Definition, IReadOnlyDictionary<string,XElement> Arguments);
    internal static NativeStructuralActivationProof.SourcePlan Read(NativeCommunicationOriginCatalog catalog)
    {
        var nodes=catalog.Nodes; var root=nodes[0]; var ns=root.Name.Namespace;
        if(nodes.Count>500000||nodes.Any(n=>n.Name.Namespace!=ns))Fail();
        var keys=nodes.Select((node,key)=>(node,key)).ToDictionary(p=>p.node,p=>p.key);
        var apps=nodes.Where(n=>n.Name==ns+"ApplicationProgram").Take(2).ToArray();if(apps.Length!=1)Fail();var app=apps[0];
        var stat=One(app,"Static");var dynamic=One(app,"Dynamic");var appId=Id(app);
        string Local(string id)=>id.StartsWith(appId+"_",StringComparison.Ordinal)?id[(appId.Length+1)..]:id;
        if(nodes.Any(n=>n.Name.LocalName is "SubModuleDefs" or "Assign" or "ParameterCalculation" || n.Attributes("BaseValue").Any()))Fail();
        var definitions=new Dictionary<string,XElement>(StringComparer.Ordinal);
        foreach(var definition in nodes.Where(n=>n.Name==ns+"ModuleDef"))
        {
            Shape(definition,"Id","Name","InternalDescription");
            if(definition.Parent?.Name!=ns+"ModuleDefs"||!ReferenceEquals(definition.Parent.Parent,app)||!definitions.TryAdd(SafeId(definition),definition))Fail();
            Shape(One(definition,"Static"));Shape(One(definition,"Dynamic"));
        }
        if(app.Elements(ns+"ModuleDefs").Count()!=1||definitions.Count is <1 or >256)Fail();
        var pools=new Dictionary<string,Pool>(StringComparer.Ordinal);
        foreach(var allocator in nodes.Where(n=>n.Name==ns+"Allocator"))
        {
            if(allocator.Parent?.Name!=ns+"Allocators"||!ReferenceEquals(allocator.Parent.Parent,stat)||allocator.HasElements)Fail();
            Shape(allocator,"Id","Start","maxInclusive","ErrorMessageRef","InternalDescription");
            var start=Number(allocator,"Start");var maximum=Number(allocator,"maxInclusive");
            if(start<0||maximum<start||maximum>1048575||!pools.TryAdd(SafeId(allocator),new(start,maximum)))Fail();
        }
        if(stat.Elements(ns+"Allocators").Count()>1||stat.Elements(ns+"Allocators").Any(c=>c.Attributes().Any(a=>!a.IsNamespaceDeclaration)||c.Elements().Any(e=>e.Name!=ns+"Allocator")))Fail();
        var calls=new Dictionary<XElement,Call>();var callIds=new HashSet<string>(StringComparer.Ordinal);var usedDefinitions=new HashSet<string>(StringComparer.Ordinal);
        foreach(var call in nodes.Where(n=>n.Name==ns+"Module"))
        {
            var ancestors=call.Ancestors().TakeWhile(n=>!ReferenceEquals(n,app)).ToArray();
            if(ancestors.Length==0||!ReferenceEquals(ancestors[^1],dynamic)||ancestors.Any(n=>n.Name.LocalName is not ("Dynamic" or "Channel" or "ChannelIndependentBlock" or "ParameterBlock" or "Repeat")))Fail();
            Shape(call,"Id","RefId","Name","InternalDescription");if(!callIds.Add(SafeId(call)))Fail();
            if(!definitions.TryGetValue((string?)call.Attribute("RefId")??"",out var definition))Fail();usedDefinitions.Add(Id(definition!));
            var containers=definition!.Elements(ns+"Arguments").ToArray();if(containers.Length>1)Fail();
            foreach(var container in containers)Shape(container);
            var declarations=new Dictionary<string,XElement>(StringComparer.Ordinal);
            foreach(var argument in containers.SelectMany(c=>c.Elements()))
            {
                Shape(argument,"Id","Name","Type","Allocates","Alignment","InternalDescription");
                if(argument.Name!=ns+"Argument"||argument.HasElements||declarations.Count>=64||!declarations.TryAdd(SafeId(argument),argument)
                    || (string?)argument.Attribute("Type") is not (null or "Numeric" or "Text"))Fail();
                if(argument.Attribute("Alignment") is not null && Number(argument,"Alignment") is not (1 or 2 or 4 or 8))Fail();
            }
            var supplied=new HashSet<string>(StringComparer.Ordinal);var allocators=new HashSet<string>(StringComparer.Ordinal);
            foreach(var argument in call.Elements())
            {
                var reference=(string?)argument.Attribute("RefId")??"";
                if(!declarations.TryGetValue(reference,out var declaration)||!supplied.Add(reference)||argument.HasElements)Fail();
                Shape(argument,"RefId","Value","AllocatorRefId");
                var numeric=((string?)declaration!.Attribute("Type")??"Numeric")=="Numeric";
                if(argument.Name!=ns+(numeric?"NumericArg":"TextArg"))Fail();
                if(numeric)
                {
                    _=Number(argument,"Value",0);
                    if(argument.Attribute("AllocatorRefId") is {} allocation)
                    {
                        if(!pools.ContainsKey(allocation.Value)||!allocators.Add(allocation.Value)||Number(argument,"Value",0)!=0
                            ||Number(declaration,"Allocates") is <1 or >1048576)Fail();
                        if(!definition.Descendants(ns+"Memory").Any(m=>(string?)m.Attribute("BaseOffset")==reference)
                            ||definition.Descendants(ns+"ComObject").Any(o=>(string?)o.Attribute("BaseNumber")==reference))Fail();
                    }
                }
                else if(argument.Attribute("AllocatorRefId") is not null||(string?)argument.Attribute("Value") is not {Length:<=255})Fail();
            }
            if(supplied.Count!=declarations.Count)Fail();calls.Add(call,new(definition,declarations));
        }
        if(calls.Count==0||usedDefinitions.Count!=definitions.Count)Fail();
        foreach(var repeat in nodes.Where(n=>n.Name==ns+"Repeat"))
        {
            Shape(repeat,"Id","Count");_=SafeId(repeat);var count=Number(repeat,"Count");
            if(count is <0 or >256||!repeat.Ancestors().Contains(dynamic)||repeat.Ancestors().Any(n=>n.Name==ns+"ModuleDef")
                ||repeat.Elements().Any(n=>n.Name.LocalName is not ("Module" or "Repeat")))Fail();
        }
        NativeStructuralActivationProof.ValidateImageDynamicSources(stat,dynamic,definitions.Values);
        // Bound repeat traversal independently of emitted modules: empty or zero-count inner branches still cost work.
        const long maximumTraversalWork=500000;
        long MeasureWork(IEnumerable<XElement> elements,int depth)
        {
            if(depth>8)Fail(GeneratedTableIssueCode.LimitExceeded);
            long total=0;
            foreach(var node in elements)
            {
                long work=1;
                if(node.Name==ns+"Repeat")
                {
                    var childWork=MeasureWork(node.Elements(),depth+1);
                    var count=Number(node,"Count");
                    if(count>0&&childWork+1>(maximumTraversalWork-1)/count)Fail(GeneratedTableIssueCode.LimitExceeded);
                    work+=count*(childWork+1);
                }
                else if(node.Name.LocalName is "Channel" or "ChannelIndependentBlock" or "ParameterBlock")work+=MeasureWork(node.Elements(),depth);
                if(work>maximumTraversalWork-total)Fail(GeneratedTableIssueCode.LimitExceeded);
                total+=work;
            }
            return total;
        }
        _=MeasureWork(dynamic.Elements(),0);
        var order=dynamic.DescendantsAndSelf().Select((node,key)=>(node,key)).ToDictionary(p=>p.node,p=>p.key);
        var next=new Dictionary<string,long>(StringComparer.Ordinal);var expected=new List<NativeCommunicationOccurrence>();var instanceIds=new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<XElement> elements,IReadOnlyList<int> counters,IReadOnlyList<string> dimensions,int depth)
        {
            if(depth>8)Fail(GeneratedTableIssueCode.LimitExceeded);
            foreach(var node in elements)
            {
                if(node.Name==ns+"Repeat")
                {
                    var count=Number(node,"Count");for(var i=1;i<=count;i++)Walk(node.Elements(),counters.Append(i).ToArray(),dimensions.Append(order[node].ToString(CultureInfo.InvariantCulture)+"x"+i.ToString(CultureInfo.InvariantCulture)).ToArray(),depth+1);
                }
                else if(node.Name==ns+"Module")
                {
                    if(depth>=8)Fail(GeneratedTableIssueCode.LimitExceeded);
                    if(expected.Count>=256)Fail(GeneratedTableIssueCode.LimitExceeded);var call=calls[node];var numeric=new List<NativeCommunicationArgument>();var native=new List<ApplicationModuleArgument>();
                    foreach(var argument in node.Elements())
                    {
                        var id=(string)argument.Attribute("RefId")!;var declaration=call.Arguments[id];
                        native.Add(new(Local(id),argument.Attribute("AllocatorRefId") is {} poolRef?Local(poolRef.Value):(string?)argument.Attribute("Value")??"0"));
                        if(argument.Name==ns+"TextArg")continue;var value=Number(argument,"Value",0);
                        if(argument.Attribute("AllocatorRefId") is {} allocated)
                        {
                            var pool=pools[allocated.Value];var alignment=Number(declaration,"Alignment",1);var position=next.GetValueOrDefault(allocated.Value,pool.Start);
                            value=checked(position+(alignment-position%alignment)%alignment);var end=checked(value+Number(declaration,"Allocates"));
                            if(end>pool.Maximum+1)Fail();next[allocated.Value]=end;
                        }
                        numeric.Add(new(id,value));
                    }
                    var instance=ApplicationModules.InstanceId(Id(node),counters);if(!NativeCommunicationGuard.Safe(instance)||!instanceIds.Add(instance))Fail();
                    var frame=new NativeCommunicationModuleFrame(keys[node],keys[call.Definition],Id(call.Definition),instance,Local(instance),Local(Id(node)),
                        string.Join(' ',dimensions.Append(order[node].ToString(CultureInfo.InvariantCulture)+"x1")),catalog.Hash(keys[node]),catalog.Hash(keys[call.Definition]),Array.AsReadOnly(numeric.OrderBy(a=>a.Id,StringComparer.Ordinal).ToArray()));
                    expected.Add(new(frame,Array.AsReadOnly(native.ToArray())));
                }
                else if(node.Name.LocalName is "Channel" or "ChannelIndependentBlock" or "ParameterBlock")Walk(node.Elements(),counters,dimensions,depth);
                else if(node.Descendants().Any(n=>n.Name==ns+"Module"||n.Name==ns+"Repeat"))Fail();
            }
        }
        Walk(dynamic.Elements(),[],[],0);
        var owners=new Dictionary<int,int>();foreach(var node in nodes){var definition=node.AncestorsAndSelf().FirstOrDefault(n=>n.Name==ns+"ModuleDef");owners.Add(keys[node],definition is null?-1:keys[definition]);}
        return new(app,catalog.Hash(0),Array.AsReadOnly(nodes.ToArray()),Array.AsReadOnly(expected.ToArray()),new ReadOnlyDictionary<int,int>(owners),
            new ReadOnlyDictionary<int,IReadOnlyList<int>>(new Dictionary<int,IReadOnlyList<int>>()),Array.Empty<string>(),true);
    }
    private static string Id(XElement node)=>(string?)node.Attribute("Id")??"";
    private static string SafeId(XElement node){var id=Id(node);if(!NativeCommunicationGuard.Safe(id))Fail();return id;}
    private static XElement One(XElement parent,string name){var nodes=parent.Elements(parent.Name.Namespace+name).Take(2).ToArray();if(nodes.Length!=1)Fail();return nodes[0];}
    private static long Number(XElement node,string name,long? fallback=null)
    {
        var raw=(string?)node.Attribute(name);if(raw is null&&fallback is {} value)return value;
        if(raw is not {Length:>0 and <=32}||!long.TryParse(raw,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var parsed))Fail();
        return long.Parse(raw!,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);
    }
    private static void Shape(XElement node,params string[] attributes)
    {if(node.Attributes().Any(a=>!a.IsNamespaceDeclaration&&(a.Name.Namespace!=XNamespace.None||!attributes.Contains(a.Name.LocalName)))||node.Nodes().OfType<XText>().Any(t=>!string.IsNullOrWhiteSpace(t.Value)))Fail();}
    private static void Fail(GeneratedTableIssueCode code=GeneratedTableIssueCode.UnsupportedStructureActivation)=>NativeCommunicationGuard.Fail(code);
}

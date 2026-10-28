using System.Xml.Linq;
using Knx;
using Knx.Scripting;

internal static class SemanticsChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        var doc = XDocument.Parse("""
            <KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id="T"><TypeNumber SizeInBit="8" Type="unsignedInt" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P" ParameterType="T" Text="Original" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="R" RefId="P" /></ParameterRefs></Static>
            <Dynamic><ParameterBlock Id="B" Text="Original block"><ParameterRefRef RefId="R" />
            <choose ParamRefId="R"><when test="1"><Rename RefId="B" Text="Enabled block" /><Rename RefId="R" Text="Enabled value" /></when></choose>
            </ParameterBlock></Dynamic></ApplicationProgram></KNX>
            """);
        var parsed = Ksxml.Parse(doc, out _, new Dictionary<string, string?> { ["R"] = "1" });
        Check("Selected Rename changes block and row labels", parsed.Blocks.Single().Name == "Enabled block"
            && parsed.Instances.Single().Name == "Enabled value");
        DynamicParameters.Apply(DynamicParameters.Evaluate(doc.Root!, new Dictionary<string, string?> { ["R"] = "0" }), parsed.Instances, parsed.Blocks);
        Check("Leaving the renamed branch restores declared labels", parsed.Blocks.Single().Name == "Original block"
            && parsed.Instances.Single().Name == "Original");
        using var runtime = new IsolatedScriptRuntime();
        var bindings = new Dictionary<string, object?> { ["input"] = 3d, ["output"] = 0d };
        Check("JavaScript executes in a separate worker", Equals(runtime.Execute("output = input * 2;", bindings, ["output"])["output"], 6d));
        bool Rejected(string source)
        {
            try { runtime.Execute(source, bindings, ["output"]); return false; }
            catch (InvalidDataException) { return true; }
        }
        Check("JavaScript cannot access CLR objects or compile string code", Rejected("output = input.GetType();")
            && Rejected("output = Function('return 1')();"));
        Check("JavaScript has no filesystem network or process APIs", Equals(runtime.Execute(
            "output = [typeof System, typeof require, typeof process, typeof fetch].join(',');", bindings, ["output"])["output"],
            "undefined,undefined,undefined,undefined"));
        Check("JavaScript infinite loops are bounded", Rejected("while (true) {}"));
        Check("JavaScript rejects recursion oversized allocations and nonprimitive results", Rejected("function f(){ return f()+1; } output=f();")
            && Rejected("output='x'.repeat(100000000);") && Rejected("output={ value: 1 };") && Rejected("output=1/0;"));
        runtime.Execute("globalThis.previousInput = input; output = 1;", bindings, ["output"]);
        Check("Script requests never share a JavaScript realm", Equals(runtime.Execute("output = typeof previousInput;", bindings, ["output"])["output"], "undefined"));
        var hostInputRejected = false;
        try { runtime.Execute("output=1;", new Dictionary<string, object?> { ["input"] = new object() }, ["output"]); }
        catch (InvalidDataException) { hostInputRejected = true; }
        Check("Host objects are rejected before script transport", hostInputRejected);
        var calculationDoc = XDocument.Parse("""
            <KNX><ApplicationProgram><Static><ParameterTypes><ParameterType Id="T"><TypeNumber SizeInBit="8" Type="unsignedInt" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-L" ParameterType="T" Text="Input" Value="3" /><Parameter Id="P-R" ParameterType="T" Text="Output" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="L" RefId="P-L" /><ParameterRef Id="R" RefId="P-R" /></ParameterRefs>
            <ParameterCalculations><ParameterCalculation Language="JavaScript"><LParameters><ParameterRefRef RefId="L" AliasName="left" /></LParameters>
            <RParameters><ParameterRefRef RefId="R" AliasName="right" /></RParameters><LRTransformation>right = left * 2;</LRTransformation><RLTransformation>left = right / 2;</RLTransformation></ParameterCalculation></ParameterCalculations></Static>
            <Dynamic><ParameterBlock Id="B" Text="Original"><ParameterRefRef RefId="L" /><ParameterRefRef RefId="R" />
            <choose ParamRefId="R"><when test="6"><Rename RefId="B" Text="Calculated" /></when></choose></ParameterBlock></Dynamic>
            </ApplicationProgram></KNX>
            """);
        var calculated = Ksxml.Parse(calculationDoc, out var calculationState, scripts: runtime);
        Check("LR calculations feed branch selection and Rename", calculated.Instances.Single(p => p.Id == "R").Value == "6"
            && calculated.Blocks.Single().Name == "Calculated" && calculationState.ScriptFailures == 0 && calculationState.Converged);
        var reverse = DynamicParameters.Evaluate(calculationDoc.Root!, new Dictionary<string, string?> { ["L"] = "3", ["R"] = "10" }, runtime, "R");
        DynamicParameters.Apply(reverse, calculated.Instances);
        Check("Editing a right-hand parameter selects RL only", calculated.Instances.Single(p => p.Id == "L").Value == "5"
            && calculated.Instances.Single(p => p.Id == "R").Value == "10" && reverse.ScriptFailures == 0);
        var chained = new XDocument(calculationDoc);
        chained.Descendants("Parameters").Single().Add(new XElement("Parameter", new XAttribute("Id", "P-H"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "0")));
        chained.Descendants("ParameterRefs").Single().Add(new XElement("ParameterRef", new XAttribute("Id", "H"), new XAttribute("RefId", "P-H")));
        chained.Descendants("ParameterCalculations").Single().Add(XElement.Parse("""
            <ParameterCalculation Language="JavaScript"><LParameters><ParameterRefRef RefId="R" AliasName="middle" /></LParameters>
            <RParameters><ParameterRefRef RefId="H" AliasName="last" /></RParameters><LRTransformation>last=middle*2;</LRTransformation><RLTransformation>middle=last/2;</RLTransformation></ParameterCalculation>
            """));
        var chain = DynamicParameters.Evaluate(chained.Root!, new Dictionary<string, string?> { ["L"] = "3", ["R"] = "6", ["H"] = "20" }, runtime, "H");
        Check("Reverse edits propagate through calculation dependencies", chain.Parameters["L"].Value == "5" && chain.Parameters["R"].Value == "10"
            && chain.Parameters["H"].Value == "20" && chain.Converged && chain.ScriptFailures == 0);
        var invalidResult = new XDocument(calculationDoc);
        invalidResult.Descendants("LRTransformation").Single().Value = "right=1000;";
        var invalid = DynamicParameters.Evaluate(invalidResult.Root!, scripts: runtime);
        Check("Invalid calculation results never enter state or enable dependent outputs", invalid.Parameters["R"].Value == "0"
            && !invalid.Parameters["R"].IsEnabled && invalid.ScriptFailures > 0);
        var noRuntime = Ksxml.Parse(calculationDoc, out var noRuntimeState);
        Check("Without script capability calculations stay explicit and unevaluated", noRuntime.Instances.Single(p => p.Id == "R").Value == "0"
            && noRuntimeState.Unsupported.Count > 0);
        var nestedDoc = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber SizeInBit="8" Type="unsignedInt" /></ParameterType></ParameterTypes>
            <ModuleDefs><ModuleDef Id="APP_MD-1"><Static><ModuleDefs><ModuleDef Id="APP_MD-1_MD-2"><Static>
            <Parameters><Parameter Id="APP_MD-1_MD-2_P-1" ParameterType="T" Value="2" /></Parameters><ParameterRefs><ParameterRef Id="APP_MD-1_MD-2_P-1_R-1" RefId="APP_MD-1_MD-2_P-1" /></ParameterRefs>
            </Static><Dynamic><ParameterRefRef RefId="APP_MD-1_MD-2_P-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Module Id="APP_MD-1_MD-2_M-2" RefId="APP_MD-1_MD-2" /></Dynamic></ModuleDef></ModuleDefs></Static>
            <Dynamic><Module Id="APP_MD-1_M-1" RefId="APP_MD-1" /></Dynamic></ApplicationProgram></KNX>
            """);
        var nested = Ksxml.Parse(nestedDoc);
        Check("Nested literal modules preserve their complete instance identity", nested.Instances.Count == 1
            && nested.Instances[0].Id == "APP_MD-1_M-1_MI-1_MD-2_M-2_MI-1_P-1_R-1");
        var recursiveDoc = new XDocument(nestedDoc);
        recursiveDoc.Descendants("Module").First().SetAttributeValue("RefId", "APP_MD-1");
        var recursive = Ksxml.Parse(recursiveDoc, out var recursionState);
        Check("Recursive module definitions remain bounded and never expose partial instances", recursive.Instances.Count == 0 && recursionState.Unsupported.Count > 0);
        return failures;
    }
}

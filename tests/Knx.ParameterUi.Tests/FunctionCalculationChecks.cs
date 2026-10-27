using System.Xml.Linq;
using Knx;
using Knx.Scripting;

internal static class FunctionCalculationChecks
{
    private static XDocument Application(string script, string context = "{factor:2}")
    {
        var document = XDocument.Parse("""
            <KNX><ApplicationProgram Id="APP"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
            <Parameters><Parameter Id="P-L" ParameterType="T" Value="3" /><Parameter Id="P-R" ParameterType="T" Value="0" /></Parameters>
            <ParameterRefs><ParameterRef Id="L" RefId="P-L" /><ParameterRef Id="R" RefId="P-R" /></ParameterRefs><Script />
            <ParameterCalculations><ParameterCalculation Language="JavaScript" LRTransformationFunc="forward" RLTransformationFunc="reverse">
            <LParameters><ParameterRefRef RefId="L" AliasName="left" /></LParameters><RParameters><ParameterRefRef RefId="R" AliasName="right" /></RParameters>
            </ParameterCalculation></ParameterCalculations></Static><Dynamic><ParameterRefRef RefId="L" /><ParameterRefRef RefId="R" /></Dynamic></ApplicationProgram></KNX>
            """);
        document.Descendants("Script").Single().Value = script;
        document.Descendants("ParameterCalculation").Single().SetAttributeValue("LRTransformationParameters", context);
        document.Descendants("ParameterCalculation").Single().SetAttributeValue("RLTransformationParameters", context);
        return document;
    }

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) failures++; }
        const string functions = "function forward(input,output,context){output.right=input.left*context.factor;} function reverse(input,output,context){output.left=input.right/context.factor;}";
        using var runtime = new IsolatedScriptRuntime();
        DynamicEvaluation Evaluate(XDocument document, IReadOnlyDictionary<string, string?>? values = null, string? edited = null)
            => DynamicParameters.Evaluate(document.Root!, values, runtime, edited);
        bool Rejected(XDocument document) { var state = Evaluate(document); return state.ScriptFailures > 0 && state.Parameters["R"].Value == "0" && !state.Parameters["R"].IsEnabled; }
        var original = Application(functions);
        var before = original.ToString();
        var forward = Evaluate(original);
        Check("Static function calculations receive input output and data context", forward.ScriptFailures == 0 && forward.Converged && forward.Parameters["R"].Value == "6");
        var reverse = Evaluate(original, new Dictionary<string, string?> { ["L"] = "3", ["R"] = "10" }, "R");
        Check("Function calculation reverse edits use RL function context and sides", reverse.ScriptFailures == 0 && reverse.Parameters["L"].Value == "5" && reverse.Parameters["R"].Value == "10");
        var namedLikeGlobal = Application("function forward(input,output,context){output.Object=input.JSON*context.factor;}");
        namedLikeGlobal.Descendants("LParameters").Single().Elements().Single().SetAttributeValue("AliasName", "JSON");
        namedLikeGlobal.Descendants("RParameters").Single().Elements().Single().SetAttributeValue("AliasName", "Object");
        var namedState = Evaluate(namedLikeGlobal);
        Check("Function field aliases cannot replace worker parser or object globals", namedState.ScriptFailures == 0 && namedState.Parameters["R"].Value == "6");
        var data = Application("function forward(input,output,context){output.right=input.left+context.nested.add+context.flags[0];}", "{nested:{add:2},flags:[true,false],text:\"},unsafe:42\"}");
        Check("Context accepts bounded nested data and preserves quoted punctuation", Evaluate(data).Parameters["R"].Value == "6" && Evaluate(data).ScriptFailures == 0);
        var emptyContext = Application("function forward(input,output,context){output.right=input.left+Object.keys(context).length;}", "");
        var absentContext = new XDocument(emptyContext); absentContext.Descendants("ParameterCalculation").Single().Attribute("LRTransformationParameters")!.Remove();
        Check("Absent and empty function context both supply an empty object", Evaluate(emptyContext).Parameters["R"].Value == "3"
            && Evaluate(absentContext).Parameters["R"].Value == "3" && Evaluate(absentContext).ScriptFailures == 0);
        var noOp = Application("function forward(input,output,context){return {right:99};}");
        noOp.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", "7");
        var noOpState = Evaluate(noOp);
        Check("Function outputs start with current values and returned objects are ignored", noOpState.ScriptFailures == 0 && noOpState.Parameters["R"].Value == "7");
        var noRuntime = DynamicParameters.Evaluate(original.Root!);
        Check("Function declarations never execute without an explicit capability", noRuntime.ScriptExecutions == 0 && noRuntime.Parameters["R"].Value == "0"
            && noRuntime.Unsupported.Any(s => s.Contains("JavaScript", StringComparison.Ordinal)) && original.ToString() == before);
        Check("Missing function and malformed primitive output fail without output publication", Rejected(Application("function other(){}"))
            && Rejected(Application("function forward(input,output,context){output.right={value:1};}"))
            && Rejected(Application("function forward(input,output,context){delete output.right;}")));
        Check("Function results pass central numeric type validation", Rejected(Application("function forward(input,output,context){output.right=256;}")));
        var invalidNumber = Application("function forward(input,output,context){output.right=1;}");
        invalidNumber.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", "999");
        var restrictedNumber = new XDocument(invalidNumber);
        restrictedNumber.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", "6");
        restrictedNumber.Descendants("TypeNumber").Single().SetAttributeValue("maxInclusive", "5");
        var invalidReverse = Evaluate(original, new Dictionary<string, string?> { ["R"] = "999" }, "R");
        Check("Consumed numeric inputs obey storage bounds restrictions and reverse direction", Rejected(invalidNumber) && Rejected(restrictedNumber)
            && invalidReverse.ScriptFailures > 0 && invalidReverse.Parameters["L"].Value == "3");
        var repairableOutput = Application(functions);
        repairableOutput.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", "999");
        var repaired = Evaluate(repairableOutput);
        var unrepairedOutput = new XDocument(repairableOutput); unrepairedOutput.Descendants("Script").Single().Value = "function forward(input,output,context){}";
        var unrepaired = Evaluate(unrepairedOutput);
        Check("Preinitialized invalid outputs require a valid repair before publication", repaired.ScriptFailures == 0 && repaired.Parameters["R"].Value == "6"
            && unrepaired.ScriptFailures > 0 && unrepaired.Parameters["R"].Value == "999" && !unrepaired.Parameters["R"].IsEnabled);
        XDocument TypedInput(XElement type, string value)
        {
            var document = Application("function forward(input,output,context){output.right=1;}");
            type.SetAttributeValue("Id", "Input"); document.Descendants("ParameterTypes").Single().Add(type);
            var parameter = document.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L");
            parameter.SetAttributeValue("ParameterType", "Input"); parameter.SetAttributeValue("Value", value);
            return document;
        }
        var invalidFloat = TypedInput(XElement.Parse("<ParameterType><TypeFloat Encoding='IEEE-754 Single' minInclusive='0' maxInclusive='10' /></ParameterType>"), "11");
        Check("Consumed float inputs cannot bypass declared restrictions", Rejected(invalidFloat));
        var enumeration = Application("function forward(input,output,context){output.right=2;}");
        enumeration.Descendants("TypeNumber").Single().ReplaceWith(XElement.Parse("<TypeRestriction Base='Value' SizeInBit='8'><Enumeration Value='1' Text='One'/><Enumeration Value='2' Text='Two'/></TypeRestriction>"));
        enumeration.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", "1");
        var invalidEnum = Evaluate(enumeration);
        enumeration.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", "1");
        enumeration.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", "3");
        var repairedEnum = Evaluate(enumeration);
        var unchangedEnum = new XDocument(enumeration);
        unchangedEnum.Descendants("Enumeration").First().SetAttributeValue("Value", "01");
        unchangedEnum.Descendants("Parameter").ToList().ForEach(p => p.SetAttributeValue("Value", "01"));
        unchangedEnum.Descendants("Script").Single().Value = "function forward(input,output,context){}";
        var unchangedEnumState = Evaluate(unchangedEnum);
        Check("Consumed enums require a declared value while output values remain repairable", invalidEnum.ScriptFailures > 0 && invalidEnum.Parameters["R"].Value == "1"
            && repairedEnum.ScriptFailures == 0 && repairedEnum.Parameters["R"].Value == "2"
            && unchangedEnumState.ScriptFailures == 0 && unchangedEnumState.Parameters["R"].Value == "01");
        Check("Consumed text values obey their declared UTF8 storage bound", Rejected(TypedInput(XElement.Parse("<ParameterType><TypeText SizeInBit='8'/></ParameterType>"), "ä")));
        var boolean = Application("function forward(input,output,context){output.right=!input.left;}");
        boolean.Descendants("ParameterType").Single().Elements().Remove(); boolean.Descendants("ParameterType").Single().SetAttributeValue("DataType", "Boolean");
        boolean.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", "1");
        var validBoolean = Evaluate(boolean);
        boolean.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", "true");
        Check("Consumed Boolean inputs use canonical storage zero or one", validBoolean.ScriptFailures == 0 && validBoolean.Parameters["R"].Value == "0" && Rejected(boolean));
        foreach (var type in new[]
        {
            XElement.Parse("<ParameterType Name='Color'><TypeColor Space='RGB'/></ParameterType>"),
            XElement.Parse("<ParameterType Name='RawData'><TypeRawData MaxSize='1'/></ParameterType>"),
            XElement.Parse("<ParameterType Name='Date' DataType='Date' />"),
            XElement.Parse("<ParameterType Name='Time' DataType='Time' />"),
        })
            Check("Unproven calculation input encoding is explicit: " + (string?)type.Attribute("Name"), Rejected(TypedInput(type, "1")));
        var missingScript = Application(functions); missingScript.Descendants("Script").Remove();
        var ambiguousScript = Application(functions); ambiguousScript.Descendants("Script").Single().AddAfterSelf(new XElement("Script", functions));
        var hiddenScript = Application(functions); hiddenScript.Descendants("Script").Single().Remove();
        hiddenScript.Descendants("Static").Single().Add(new XElement("ModuleDefs", new XElement("ModuleDef", new XAttribute("Id", "Unused"), new XElement("Static", new XElement("Script", functions)))));
        Check("Function calculations require one unambiguous instantiated static script", Rejected(missingScript) && Rejected(ambiguousScript) && Rejected(hiddenScript));
        var invalidName = Application(functions); invalidName.Descendants("ParameterCalculation").Single().SetAttributeValue("LRTransformationFunc", "forward);right=7;//");
        var mixed = Application(functions); mixed.Descendants("ParameterCalculation").Single().Add(new XElement("LRTransformation", "right=7;"));
        Check("Function names and mixed inline function declarations are validated", Rejected(invalidName) && Rejected(mixed));
        Check("Context expressions duplicate keys and excess nesting are rejected", Rejected(Application(functions, "{factor:(globalThis.bad=2)}"))
            && Rejected(Application(functions, "{factor:2,factor:3}")) && Rejected(Application(functions, "{a:{a:{a:{a:{a:{a:{a:{a:{a:1}}}}}}}}}")));
        Check("Function source and context have independent size limits", Rejected(Application(new string(' ', 1_048_576) + functions))
            && Rejected(Application(functions, "{text:\"" + new string('x', 4096) + "\"}")));
        Check("Function execution remains bounded in the isolated worker", Rejected(Application("function forward(){while(true){}}"))
            && Evaluate(original).Parameters["R"].Value == "6");
        var atomic = Application("function forward(input,output,context){output.right=6;output.extra=256;}");
        atomic.Descendants("Parameters").Single().Add(new XElement("Parameter", new XAttribute("Id", "P-H"), new XAttribute("ParameterType", "T"), new XAttribute("Value", "9")));
        atomic.Descendants("ParameterRefs").Single().Add(new XElement("ParameterRef", new XAttribute("Id", "H"), new XAttribute("RefId", "P-H")));
        atomic.Descendants("RParameters").Single().Add(new XElement("ParameterRefRef", new XAttribute("RefId", "H"), new XAttribute("AliasName", "extra")));
        var atomicState = Evaluate(atomic);
        Check("A bad function output cannot partially publish another output", atomicState.ScriptFailures > 0 && atomicState.Parameters["R"].Value == "0" && atomicState.Parameters["H"].Value == "9");

        XDocument Ip(string value, string version = "IPv4")
        {
            var document = Application("function forward(input,output,context){output.right=input.left;} function reverse(input,output,context){output.left=input.right;}");
            document.Descendants("TypeNumber").Single().ReplaceWith(new XElement("TypeIPAddress", new XAttribute("AddressType", "HostAddress"), new XAttribute("Version", version)));
            document.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-L").SetAttributeValue("Value", value);
            document.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", version == "IPv4" ? "192.168.1.101" : "2001:db8::2");
            return document;
        }
        var ip = Ip("192.168.1.100"); var ipState = Evaluate(ip);
        var repairableIp = new XDocument(ip); repairableIp.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-R").SetAttributeValue("Value", "invalid");
        var repairedIp = Evaluate(repairableIp);
        Check("IPv4 inputs stay typed and outputs are validated even when repairing old values", ipState.ScriptFailures == 0 && ipState.Parameters["R"].Value == "192.168.1.100"
            && repairedIp.ScriptFailures == 0 && repairedIp.Parameters["R"].Value == "192.168.1.100");
        var badIp = Ip("192.168.1.100"); badIp.Descendants("Script").Single().Value = "function forward(input,output,context){output.right='999.1.1.1';}";
        var badIpState = Evaluate(badIp);
        var badIpInput = Evaluate(Ip("999.1.1.1"));
        Check("Invalid function IP input or output retains the original address", badIpState.ScriptFailures > 0 && badIpState.Parameters["R"].Value == "192.168.1.101"
            && badIpInput.ScriptFailures > 0 && badIpInput.Parameters["R"].Value == "192.168.1.101");
        var v6 = Evaluate(Ip("2001:db8::1", "IPv6"));
        var familyMismatch = Ip("192.168.1.100"); familyMismatch.Descendants("Script").Single().Value = "function forward(input,output,context){output.right='2001:db8::1';}";
        Check("Function IP outputs respect their declared address family", v6.ScriptFailures == 0 && v6.Parameters["R"].Value == "2001:db8::1"
            && Evaluate(familyMismatch).ScriptFailures > 0);
        var ipReverse = Evaluate(ip, new Dictionary<string, string?> { ["R"] = "192.168.1.200" }, "R");
        Check("Reverse function IP outputs use the same central validator", ipReverse.ScriptFailures == 0 && ipReverse.Parameters["L"].Value == "192.168.1.200");
        var inlineIp = Ip("192.168.1.100");
        var inlineCalculation = inlineIp.Descendants("ParameterCalculation").Single();
        inlineCalculation.Attributes().Where(a => a.Name.LocalName.Contains("Transformation", StringComparison.Ordinal)).Remove();
        inlineCalculation.Add(new XElement("LRTransformation", "right=left;"), new XElement("RLTransformation", "left=right;"));
        Check("Inline calculations retain their profile with typed IP strings", Evaluate(inlineIp).ScriptFailures == 0 && Evaluate(inlineIp).Parameters["R"].Value == "192.168.1.100");
        return failures;
    }
}

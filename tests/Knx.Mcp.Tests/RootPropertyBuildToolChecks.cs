using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx;
using Knx.Mcp;
using F = RootPropertyBuildMcpFixture;

internal static class RootPropertyBuildToolChecks
{
    private static readonly RootPropertyBuildRequest Request = new(new("all", LoadProcedureAccess.Remote, LoadParts.Full));
    private static bool Error(JsonNode node) => node["error"] is not null || node["result"]?["isError"]?.GetValue<bool>() == true;
    private static JsonNode Data(JsonNode node) => node["result"]!["structuredContent"]!;
    private static JsonNode Build(JsonNode node) => Data(node)[F.ProjectionName]!;
    private static bool Available(JsonNode node) => !Error(node) && Build(node)?["available"]?.GetValue<bool>() == true;
    private static string Model(Project project) => Knxproj.ToXml(project).ToString(SaveOptions.DisableFormatting);
    private static string? Text(JsonNode? node, string name) => (string?)node?[name];
    private static bool Gates(JsonNode response)
    {
        var data = Data(response); var build = Build(response);
        return data["writesEnabled"]?.GetValue<bool>() == false && data["realDownloadAvailable"]?.GetValue<bool>() == false
            && data["plan"]?["ready"]?.GetValue<bool>() == false && data["plan"]?["program"] is null
            && data["plan"]?["imageBytes"]?.GetValue<int>() == 0 && data["plan"]?["chunks"]?.AsArray().Count == 0
            && build["readOnly"]?.GetValue<bool>() == true && build["completeApplicationImage"]?.GetValue<bool>() == false
            && build["executionEligible"]?.GetValue<bool>() == false && build["downloadEligible"]?.GetValue<bool>() == false;
    }
    private static bool Empty(JsonNode response)
    {
        if (Error(response)) return false;
        var build = Build(response);
        return build["available"]?.GetValue<bool>() == false && build["binding"] is null && build["population"] is null
            && build["procedure"] is null && build["procedureProof"] is null && build["policy"] is null
            && build["completeFixedRootPopulation"]?.GetValue<bool>() == false && build["desiredBitCount"]?.GetValue<ulong>() == 0
            && build["unspecifiedBitCount"]?.GetValue<ulong>() == 0 && build["issues"]?.AsArray().Count == 1
            && new[] { "claims", "chunks", "allocations", "observedCounts", "unspecifiedSpans", "unresolvedRequirements" }
                .All(name => build[name]?.AsArray().Count == 0) && Gates(response);
    }
    private static bool HashOnly(JsonNode root)
    {
        var forbidden = new HashSet<string>(StringComparer.Ordinal)
        {
            "initialData", "initialValidity", "workingData", "desiredValidity", "canonicalDesired", "beforeData", "afterData",
            "inlineData", "inlineMask", "rawValue", "recordValue", "encodedWord", "controllerValue", "activeValue", "data", "mask",
        };
        var stack = new Stack<JsonNode>(); stack.Push(root);
        while (stack.Count != 0)
        {
            var node = stack.Pop();
            if (node is JsonObject obj)
                foreach (var pair in obj)
                {
                    if (forbidden.Contains(pair.Key)) return false;
                    if (pair.Value is not null) stack.Push(pair.Value);
                }
            else if (node is JsonArray array)
                foreach (var child in array) if (child is not null) stack.Push(child);
        }
        return true;
    }

    internal static async Task Run(Action<string, bool, string?> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "buschord-root-property-tools-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var oldCatalog = Environment.GetEnvironmentVariable("KNX_CATALOG_DIR");
        Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", Path.Combine(directory, "catalog"));
        try
        {
            await using var server = new McpServer(); var sequence = 191000;
            async Task<JsonNode> Tool(string name, object arguments) => JsonNode.Parse((await server.HandleAsync(JsonSerializer.Serialize(new
            { jsonrpc = "2.0", id = ++sequence, method = "tools/call", @params = new { name, arguments } })))!)!;
            async Task<JsonNode> Raw(string arguments) => JsonNode.Parse((await server.HandleAsync("{\"jsonrpc\":\"2.0\",\"id\":" + ++sequence
                + ",\"method\":\"tools/call\",\"params\":{\"name\":\"download_plan\",\"arguments\":" + arguments + "}}"))!)!;
            async Task<JsonNode> Preview(string? option = null) => await Raw(F.Arguments(option));
            static bool ParserError(JsonNode node) => Error(node) && node.ToJsonString().Contains(F.OptionName, StringComparison.Ordinal)
                && !node.ToJsonString().Contains("descriptor read failed", StringComparison.OrdinalIgnoreCase);

            // No project is open: malformed requests must be rejected before Current, source reads or gateway discovery.
            foreach (var (name, option) in F.StrictInvalidOptions())
            {
                var rejected = await Raw(F.Arguments(option, invalidGatewaySentinel: true));
                check("root property malformed " + name + " precedes Current and gateway", ParserError(rejected), rejected.ToJsonString());
            }
            foreach (var backend in new[] { "resource_preview", "property_preview" })
            {
                foreach (var literal in new[] { "null", "\"false\"", "0" })
                {
                    var rejected = await Raw(F.Arguments(F.OptionJson + ",\"" + backend + "\":" + literal, invalidGatewaySentinel: true));
                    check("root property validates backend flag " + backend + " " + literal + " before Current", ParserError(rejected), rejected.ToJsonString());
                }
                var duplicate = await Raw(F.Arguments(F.OptionJson + ",\"" + backend + "\":false,\"" + backend + "\":false", invalidGatewaySentinel: true));
                check("root property rejects duplicate false " + backend + " before Current", ParserError(duplicate), duplicate.ToJsonString());
            }
            foreach (var other in new[]
            {
                "\"load_procedure_preview\":" + F.ProcedureJson,
                "\"image_composition_preview\":{}", "\"root_image_build_preview\":{}", "\"root_resolved_image_build_preview\":{}",
                "\"parameter_subset\":[{\"refId\":\"R0\",\"rawValue\":\"2\"}]", "\"byte_order\":\"big_endian\"",
                "\"resource_preview\":true", "\"property_preview\":true",
                "\"load_procedure_preview\":null", "\"parameter_subset\":null", "\"byte_order\":null",
                "\"image_composition_preview\":false", "\"root_image_build_preview\":false", "\"root_resolved_image_build_preview\":false",
            })
            {
                var rejected = await Raw(F.Arguments(F.OptionJson + "," + other, invalidGatewaySentinel: true));
                check("root property excludes other preview presence " + other, ParserError(rejected)
                    && rejected.ToJsonString().Contains("cannot be combined", StringComparison.Ordinal), rejected.ToJsonString());
            }

            var path = F.CreateArchive(directory); var sourceHash = F.Hash(File.ReadAllBytes(path));
            var opened = await Tool("project_open", new { path });
            check("root property own complete fixture opens", !Error(opened), opened.ToJsonString());
            if (Error(opened) || server.Project is not { } project) return;
            var device = ProjectQuery.DeviceList(project).Single();
            var xml = Model(project); var revision = project.ApplicationDataRevision;
            var info = Data(await Tool("project_info", new { })).ToJsonString();
            var ordinary = Data(await Tool("download_plan", new { device_address = F.DeviceAddress })).ToJsonString();
            var schema = server.Tools.Single(t => t.Name == "download_plan").InputSchema;
            var optionSchema = schema["properties"]?[F.OptionName];
            check("root property closed option uses the current tool contract", server.Tools.Count == McpToolContract.ExpectedToolCount
                && optionSchema?["additionalProperties"]?.GetValue<bool>() == false
                && optionSchema?["required"]?.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["procedure"]) == true
                && optionSchema?["properties"]?["procedure"]?["additionalProperties"]?.GetValue<bool>() == false
                && optionSchema?["properties"]?["procedure"]?["required"]?.AsArray().Count == 5, optionSchema?.ToJsonString());
            check("absent root property option preserves ordinary projection", !ordinary.Contains(F.ProjectionName, StringComparison.Ordinal), ordinary);

            var response = await Preview(); var core = OfflineRootPropertyBuild.Preview(project, device, Request);
            check("root property entire 130-reference working stage is available", Available(response) && core.IsAvailable, response.ToJsonString());
            if (Available(response) && core.IsAvailable)
            {
                var coreBinding = core.Binding!;
                var build = Build(response); var chunk = build["chunks"]!.AsArray().Single()!; var allocation = build["allocations"]!.AsArray().Single()!;
                var expected = F.ExpectedWords(); var mask = F.ExpectedValidity(); var initial = F.ExpectedZeroInitial();
                check("root property computes the full independent BCL 520-byte array", core.Chunks.Single().WorkingData.ToArray().SequenceEqual(expected)
                    && Text(chunk, "workingDataHash") == F.Hash(expected) && Text(chunk, "canonicalDesiredHash") == F.Hash(expected)
                    && chunk["byteLength"]?.GetValue<int>() == F.ByteLength, chunk.ToJsonString());
                check("root property missing factory gives zero Data and zero validity before formatting", core.Chunks[0].InitialData.ToArray().SequenceEqual(initial)
                    && core.Chunks[0].InitialValidity.ToArray().SequenceEqual(initial) && Text(chunk, "initialDataHash") == F.Hash(initial)
                    && Text(chunk, "initialValidityHash") == F.Hash(initial), chunk.ToJsonString());
                check("root property full fixed validity is distinct from application completeness", core.Chunks[0].DesiredValidity.ToArray().SequenceEqual(mask)
                    && Text(chunk, "desiredValidityHash") == F.Hash(mask) && build["desiredBitCount"]?.GetValue<ulong>() == 4160
                    && build["unspecifiedBitCount"]?.GetValue<ulong>() == 0 && build["completeFixedRootPopulation"]?.GetValue<bool>() == true
                    && (string?)build["scope"] == "RootFixedPropertyWorkingSet" && build["profileVersion"]?.GetValue<int>() == 1 && Gates(response), build.ToJsonString());
                check("root property PDT2 and actual Count260 supply a 520-byte allocation", allocation["requestedLength"]?.GetValue<uint>() == 520
                    && allocation["byteLength"]?.GetValue<int>() == 520 && allocation["pdtLayout"]?["writeElementBytes"]?.GetValue<int>() == 2
                    && (string?)allocation["pdtLayout"]?["declaredPdt"] == "PDT_UNSIGNED_INT"
                    && (string?)allocation["nativeSelector"]?["kind"] == "system_property" && allocation["nativeSelector"]?["nativeObjectIndex"]?.GetValue<int>() == 3
                    && allocation["nativeSelector"]?["propertyId"]?.GetValue<int>() == 10 && allocation["nativeSelector"]?["applicationObjectIndex"] is null, allocation.ToJsonString());
                var write = build["procedure"]!["controls"]!.AsArray().Single(c => (string?)c?["kind"] == "write_property")!;
                check("root property actual CC inline action is hashed and does not seed working bytes", Text(write, "inlineDataHash") == F.Hash(F.DeclaredInlineAction())
                    && write["inlineDataLength"]?.GetValue<int>() == 520 && Text(write, "inlineDataHash") != Text(chunk, "initialDataHash")
                    && Text(write, "inlineDataHash") != Text(chunk, "workingDataHash") && core.Procedure?.ProfileVersion == 1, write.ToJsonString());
                check("root property record and claim presentation caps retain true 130 totals", build["population"]?["referenceCount"]?.GetValue<int>() == 130
                    && build["population"]?["ownerCount"]?.GetValue<int>() == 130 && build["population"]?["activeCount"]?.GetValue<int>() == 130
                    && build["population"]?["recordsTotal"]?.GetValue<int>() == 130 && build["population"]?["recordsShown"]?.GetValue<int>() == 128
                    && build["population"]?["records"]?.AsArray().Count == 128 && build["population"]?["recordsTruncated"]?.GetValue<bool>() == true
                    && build["claimsTotal"]?.GetValue<int>() == 130 && build["claimsShown"]?.GetValue<int>() == 128
                    && build["claims"]?.AsArray().Count == 128 && build["claimsTruncated"]?.GetValue<bool>() == true, build.ToJsonString());
                check("root property absent count stays absent instead of becoming capacity", build["observedCountsTotal"]?.GetValue<int>() == 0
                    && build["observedCounts"]?.AsArray().Count == 0 && core.ObservedCounts.Count == 0, build.ToJsonString());
                check("root property output exposes hash-only buffers and values", HashOnly(build), build.ToJsonString());
                check("root property binding links complete source population and shared procedure", Text(build["binding"], "fingerprint") == coreBinding.Fingerprint
                    && Text(build["binding"], "contextFingerprint") == Text(build["procedureProof"], "applicationContextFingerprint")
                    && Text(build["binding"], "sharedProcedureProofHash") == Text(build["procedureProof"], "fingerprint")
                    && Text(build["binding"], "procedureWitnessHash") == Text(build["procedure"], "witnessHash")
                    && Text(build["binding"], "numericParseProfile") == "ExactInteger", build.ToJsonString());
                check("root property source policy includes the precise mask policy", Text(build["policy"], "maskPolicy") == JsonNamingPolicy.SnakeCaseLower.ConvertName(core.Policy!.MaskPolicy.ToString())
                    && Text(build["policy"], "byteOrder") == "big_endian" && build["policy"]?["optionsDeclared"]?.GetValue<bool>() == true, build["policy"]?.ToJsonString());
                check("root property all declared binding hashes use Core values", new (string Json, string Value)[]
                {
                    ("definitionHash", coreBinding.DefinitionHash), ("savedValuesHash", coreBinding.SavedValuesHash), ("sourceFingerprint", coreBinding.SourceFingerprint), ("propertyProfileHash", coreBinding.PropertyProfileHash),
                    ("carrierHash", coreBinding.CarrierHash), ("projectInputHash", coreBinding.ProjectInputHash), ("sourceHash", coreBinding.SourceHash),
                    ("contextFingerprint", coreBinding.ContextFingerprint), ("sharedProcedureProofHash", coreBinding.SharedProcedureProofHash), ("procedureWitnessHash", coreBinding.ProcedureWitnessHash),
                    ("populationHash", coreBinding.PopulationHash), ("populationValuesHash", coreBinding.PopulationValuesHash), ("policyHash", coreBinding.PolicyHash),
                    ("geometryHash", coreBinding.GeometryHash), ("initializationHash", coreBinding.InitializationHash), ("allocationHash", coreBinding.AllocationHash),
                    ("initialDataHash", coreBinding.InitialDataHash), ("observedCountsHash", coreBinding.ObservedCountsHash), ("requirementsHash", coreBinding.RequirementsHash),
                    ("claimsHash", coreBinding.ClaimsHash), ("workingHash", coreBinding.WorkingHash), ("validityHash", coreBinding.ValidityHash),
                    ("canonicalHash", coreBinding.CanonicalHash), ("procedureGraphHash", coreBinding.ProcedureGraphHash), ("controllerSetHash", coreBinding.ControllerSetHash),
                    ("sharedPlacementInventoryHash", coreBinding.SharedPlacementInventoryHash),
                }.All(pair => pair.Value.Length == 64 && Text(build["binding"], pair.Json) == pair.Value)
                    && Text(build["binding"], "selectedProcedureProfileHash") == coreBinding.SelectedProcedureProfileHash, build["binding"]?.ToJsonString());
                var copy = core.Chunks[0].WorkingData.ToArray(); copy[0] ^= 0xFF; var copyMask = core.Chunks[0].DesiredValidity.ToArray(); copyMask[0] = 0;
                var copyInitial = core.Chunks[0].InitialData.ToArray(); copyInitial[0] = 255;
                check("root property copy-out cannot mutate owned initial working or validity bytes", core.Chunks[0].WorkingData.ToArray().SequenceEqual(expected)
                    && core.Chunks[0].InitialData[0] == 0 && core.Chunks[0].DesiredValidity[0] == 255 && coreBinding.IsCurrent(project, device)
                    && build.ToJsonString() == Build(await Preview()).ToJsonString(), null);
                var falseFlags = await Raw(F.Arguments(F.OptionJson + ",\"resource_preview\":false,\"property_preview\":false"));
                check("root property allows legacy false backend flags without changing results", Available(falseFlags)
                    && Build(falseFlags).ToJsonString() == build.ToJsonString(), falseFlags.ToJsonString());
                var parts = await Preview(F.OptionJson.Replace("\"full\":true", "\"full\":false", StringComparison.Ordinal)
                    .Replace("\"parameters\":false", "\"parameters\":true", StringComparison.Ordinal));
                check("root property parameters-only explicit selection is admitted without assuming a Full image", Available(parts) && Gates(parts)
                    && Text(Build(parts)["chunks"]?[0], "workingDataHash") == F.Hash(expected), parts.ToJsonString());
                check("root property stage does not invoke an old working subset or table result", new[]
                { "offlineApplicationBuild", "offlineRootImageBuild", "offlineRootResolvedImageBuild", "offlineParameterPatch", "offlineResourcePatch", "offlinePropertyPatch", "offlineImageComposition" }
                    .All(name => Data(response)[name] is null), response.ToJsonString());
            }
            check("root property preview preserves XML source revision history and ordinary JSON", xml == Model(project)
                && revision == project.ApplicationDataRevision && sourceHash == F.Hash(File.ReadAllBytes(path))
                && info == Data(await Tool("project_info", new { })).ToJsonString()
                && ordinary == Data(await Tool("download_plan", new { device_address = F.DeviceAddress })).ToJsonString(), null);

            var conditionalPath = F.CreateArchive(directory, "conditional", conditionalProcedure: true);
            var conditionalSourceHash = F.Hash(File.ReadAllBytes(conditionalPath));
            await Tool("project_open", new { path = conditionalPath }); var conditionalProject = server.Project!;
            var conditionalDevice = ProjectQuery.DeviceList(conditionalProject).Single();
            var firstResponse = await Preview(); var firstCore = OfflineRootPropertyBuild.Preview(conditionalProject, conditionalDevice, Request);
            check("root property selected branch shares one fresh context with complete writers", Available(firstResponse) && firstCore.IsAvailable
                && Build(firstResponse)["procedure"]?["profileVersion"]?.GetValue<int>() == 2
                && Build(firstResponse)["procedureProof"]?["controllersTotal"]?.GetValue<int>() == 1, firstResponse.ToJsonString());
            if (Available(firstResponse) && firstCore.Binding is { } oldBinding)
            {
                var conditionalXml = Model(conditionalProject); var conditionalRevision = conditionalProject.ApplicationDataRevision;
                var conditionalInfo = Data(await Tool("project_info", new { })).ToJsonString();
                conditionalDevice.ParameterValues["R0"] = "2"; var changedXml = Model(conditionalProject);
                var changedResponse = await Preview(); var changed = !Error(changedResponse) ? Build(changedResponse) : null;
                check("root property current controller selects the default DD action and independently encoded values", Available(changedResponse)
                    && changed?["procedure"]?["conditionalSelection"]?["decisions"]?[0]?["usedDefault"]?.GetValue<bool>() == true
                    && Text(changed?["chunks"]?[0], "workingDataHash") == F.Hash(F.ExpectedWords(2))
                    && Text(changed?["chunks"]?[0], "initialDataHash") == F.Hash(F.ExpectedZeroInitial())
                    && changed?["procedure"]?["controls"]?.AsArray().Any(c => Text(c, "inlineDataHash") == F.Hash(F.DeclaredInlineAction(0xDD))) == true, changedResponse.ToJsonString());
                check("root property changed controller invalidates owned binding without source mutation", !oldBinding.IsCurrent(conditionalProject, conditionalDevice)
                    && Text(changed?["binding"], "fingerprint") != oldBinding.Fingerprint && Model(conditionalProject) == changedXml
                    && conditionalProject.ApplicationDataRevision == conditionalRevision && sourceHash == F.Hash(File.ReadAllBytes(path))
                    && conditionalSourceHash == F.Hash(File.ReadAllBytes(conditionalPath)), changedResponse.ToJsonString());
                conditionalDevice.ParameterValues["R0"] = F.SavedFirstLexeme;
                check("root property exact same-project restoration permits exact replay", Model(conditionalProject) == conditionalXml && oldBinding.IsCurrent(conditionalProject, conditionalDevice)
                    && conditionalInfo == Data(await Tool("project_info", new { })).ToJsonString(), null);
                var replacement = Knxproj.Load(conditionalPath); var replacementDevice = ProjectQuery.DeviceList(replacement).Single();
                check("root property same-ID detached project cannot reuse the old binding", !oldBinding.IsCurrent(replacement, replacementDevice), null);
                var undo = new UndoStack(); undo.Reset(conditionalProject); conditionalProject.Name = "Synthetic rename"; undo.Push(conditionalProject);
                var restored = undo.Undo()!; var restoredDevice = ProjectQuery.DeviceList(restored).Single();
                check("root property Undo replacement needs its own fresh complete preview", !oldBinding.IsCurrent(restored, restoredDevice)
                    && OfflineRootPropertyBuild.Preview(restored, restoredDevice, Request).IsAvailable, null);
            }

            var countedPath = F.CreateArchive(directory, "observed-count", editHardware: F.SetFactoryWithObservedCount);
            await Tool("project_open", new { path = countedPath }); var countedResponse = await Preview();
            check("root property archived Count is observed but not adopted as bytes or capacity", Available(countedResponse)
                && Build(countedResponse)["observedCountsTotal"]?.GetValue<int>() == 1
                && (string?)Build(countedResponse)["observedCounts"]?[0]?["disposition"] == "ObservedNotAdopted"
                && Build(countedResponse)["observedCounts"]?[0]?["encodedCount"]?.GetValue<int>() == 2
                && Text(Build(countedResponse)["observedCounts"]?[0], "dataHash") == F.Hash([0, 2])
                && Text(Build(countedResponse)["observedCounts"]?[0], "maskHash") == F.Hash([255, 255])
                && Text(Build(countedResponse)["chunks"]?[0], "workingDataHash") == F.Hash(F.ExpectedWords())
                && Text(Build(countedResponse)["chunks"]?[0], "initialDataHash") == F.Hash(Enumerable.Repeat((byte)0x7F, 520).ToArray())
                && Text(Build(countedResponse)["chunks"]?[0], "initialValidityHash") == F.Hash(F.ExpectedZeroInitial())
                && HashOnly(Build(countedResponse)), countedResponse.ToJsonString());

            var manyPath = F.CreateArchive(directory, "513-controllers", count: 513, editApplication: app => F.AddControllerChoices(app, 513));
            await Tool("project_open", new { path = manyPath }); var manyResponse = await Preview();
            check("root property actual 513 controller choices produce an admitted complete stage", Available(manyResponse), manyResponse.ToJsonString());
            if (Available(manyResponse))
            {
                var many = Build(manyResponse);
                check("root property 513 actual controllers and decisions retain totals behind cap512", many["procedureProof"]?["controllersTotal"]?.GetValue<int>() == 513
                    && many["procedureProof"]?["controllersShown"]?.GetValue<int>() == 512 && many["procedureProof"]?["controllers"]?.AsArray().Count == 512
                    && many["procedureProof"]?["controllersTruncated"]?.GetValue<bool>() == true
                    && many["procedure"]?["conditionalSelection"]?["decisionsTotal"]?.GetValue<int>() == 513
                    && many["procedure"]?["conditionalSelection"]?["decisionsShown"]?.GetValue<int>() == 512
                    && many["procedure"]?["conditionalSelection"]?["decisions"]?.AsArray().Count == 512
                    && many["procedure"]?["conditionalSelection"]?["decisionsTruncated"]?.GetValue<bool>() == true, many.ToJsonString());
                check("root property 516 actual controls retain a full procedure witness behind cap512", many["procedure"]?["controlsTotal"]?.GetValue<int>() == 516
                    && many["procedure"]?["controlsShown"]?.GetValue<int>() == 512 && many["procedure"]?["controls"]?.AsArray().Count == 512
                    && many["procedure"]?["controlsTruncated"]?.GetValue<bool>() == true
                    && Text(many["binding"], "procedureWitnessHash") == Text(many["procedure"], "witnessHash")
                    && Text(many["chunks"]?[0], "workingDataHash") == F.Hash(F.ExpectedWords(count: 513)) && HashOnly(many) && Gates(manyResponse), many.ToJsonString());
                var manyDevice = ProjectQuery.DeviceList(server.Project!).Single();
                manyDevice.ParameterValues["R512"] = "1";
                var tailChanged = await Preview(); var tailBytes = F.ExpectedWords(count: 513);
                BinaryPrimitives.WriteUInt32BigEndian(tailBytes.AsSpan(512 * sizeof(uint), sizeof(uint)), 1);
                check("root property hidden 513th decision changes content witnesses while retaining the controller set", Available(tailChanged)
                    && Text(Build(tailChanged)["binding"], "fingerprint") != Text(many["binding"], "fingerprint")
                    && Text(Build(tailChanged)["binding"], "controllerSetHash") == Text(many["binding"], "controllerSetHash")
                    && Text(Build(tailChanged)["binding"], "sharedProcedureProofHash") != Text(many["binding"], "sharedProcedureProofHash")
                    && Text(Build(tailChanged)["procedure"], "witnessHash") != Text(many["procedure"], "witnessHash")
                    && Text(Build(tailChanged)["chunks"]?[0], "workingDataHash") == F.Hash(tailBytes), tailChanged.ToJsonString());
                manyDevice.ParameterValues.Remove("R512");
                manyDevice.ParameterValues["R99"] = "1";
                var hiddenChanged = await Preview(); var hiddenBytes = F.ExpectedWords(count: 513);
                BinaryPrimitives.WriteUInt32BigEndian(hiddenBytes.AsSpan(99 * sizeof(uint), sizeof(uint)), 1);
                check("root property controller omitted by ordinal ID sorting still changes complete content proofs", Available(hiddenChanged)
                    && many["procedureProof"]!["controllers"]!.AsArray().All(controller => Text(controller, "refId") != "R99")
                    && Text(Build(hiddenChanged)["binding"], "controllerSetHash") == Text(many["binding"], "controllerSetHash")
                    && Text(Build(hiddenChanged)["binding"], "sharedProcedureProofHash") != Text(many["binding"], "sharedProcedureProofHash")
                    && Text(Build(hiddenChanged)["binding"], "fingerprint") != Text(many["binding"], "fingerprint")
                    && Text(Build(hiddenChanged)["procedure"], "witnessHash") != Text(many["procedure"], "witnessHash")
                    && Text(Build(hiddenChanged)["chunks"]?[0], "workingDataHash") == F.Hash(hiddenBytes), hiddenChanged.ToJsonString());
            }

            var propertiesPath = F.CreateArchive(directory, "128-properties", editApplication: app => F.AddPropertyAllocations(app, 128),
                editMaster: master => F.AddPropertyDeclarations(master, 128));
            await Tool("project_open", new { path = propertiesPath }); var propertiesResponse = await Preview();
            check("root property exact 128 actual property allocations remain complete", Available(propertiesResponse)
                && Build(propertiesResponse)["allocationsTotal"]?.GetValue<int>() == 128 && Build(propertiesResponse)["allocationsShown"]?.GetValue<int>() == 128
                && Build(propertiesResponse)["allocations"]?.AsArray().Count == 128 && Build(propertiesResponse)["allocationsTruncated"]?.GetValue<bool>() == false
                && Build(propertiesResponse)["chunksTotal"]?.GetValue<int>() == 128 && Build(propertiesResponse)["chunksShown"]?.GetValue<int>() == 128
                && Build(propertiesResponse)["chunks"]?.AsArray().Count == 128 && Build(propertiesResponse)["chunksTruncated"]?.GetValue<bool>() == false
                && Build(propertiesResponse)["chunks"]?.AsArray().Count(chunk => chunk?["byteLength"]?.GetValue<int>() == 2) == 127
                && Build(propertiesResponse)["unspecifiedSpansTotal"]?.GetValue<int>() == 127
                && Build(propertiesResponse)["requirementsShown"]?.GetValue<int>() <= 256 && Gates(propertiesResponse), propertiesResponse.ToJsonString());

            async Task Refuse(string name, Action<XDocument>? app = null, Action<XDocument>? master = null, Action<XDocument>? hardware = null)
            {
                var negativePath = F.CreateArchive(directory, "negative-" + Guid.NewGuid().ToString("N"), editApplication: app, editMaster: master, editHardware: hardware);
                var open = await Tool("project_open", new { path = negativePath });
                if (Error(open)) { check("root property source " + name + " opens before typed rejection", false, open.ToJsonString()); return; }
                var negativeProject = server.Project!; var negativeXml = Model(negativeProject); var negativeRevision = negativeProject.ApplicationDataRevision;
                var negativeInfo = Data(await Tool("project_info", new { })).ToJsonString(); var negativeHash = F.Hash(File.ReadAllBytes(negativePath));
                var rejected = await Preview();
                check("root property source " + name + " refuses atomically", Empty(rejected) && Model(negativeProject) == negativeXml
                    && negativeRevision == negativeProject.ApplicationDataRevision && negativeHash == F.Hash(File.ReadAllBytes(negativePath))
                    && negativeInfo == Data(await Tool("project_info", new { })).ToJsonString(), rejected.ToJsonString());
            }
            await Refuse("alias", app: app => F.Static(app).Element(F.Ns + "ParameterRefs")!.Add(new XElement(F.Ns + "ParameterRef", new XAttribute("Id", "alias"), new XAttribute("RefId", "P0"))));
            await Refuse("hidden equal-data overlap", app: app => { var hidden = new XElement(F.Static(app).Element(F.Ns + "Parameters")!.Elements().First()); hidden.SetAttributeValue("Id", "hidden"); F.Static(app).Element(F.Ns + "Parameters")!.Add(hidden); });
            await Refuse("hidden memory owner", app: app => F.Static(app).Element(F.Ns + "Parameters")!.Add(new XElement(F.Ns + "Parameter", new XAttribute("Id", "memory"), new XAttribute("ParameterType", "T"), new XAttribute("Value", 0), new XElement(F.Ns + "Memory"))));
            await Refuse("Code even without writers", app: app => F.Static(app).Add(new XElement(F.Ns + "Code", new XElement(F.Ns + "AbsoluteSegment", new XAttribute("Id", "S"), new XAttribute("Address", 0), new XAttribute("Size", 0)))));
            await Refuse("selected mask memory region", master: master => master.Descendants(F.Ns + "HawkConfigurationData").Single().Add(
                new XElement(F.Ns + "MemorySegments", new XElement(F.Ns + "MemorySegment", new XAttribute("MemoryType", "EEPROM"), new XAttribute("Length", 16),
                    new XElement(F.Ns + "Location", new XAttribute("AddressSpace", "StandardMemory"), new XAttribute("StartAddress", 100)),
                    new XElement(F.Ns + "AccessRights", new XAttribute("Read", "Configuration"), new XAttribute("Write", "Configuration"))))));
            await Refuse("missing PDT", master: master => master.Descendants(F.Ns + "Property").Single().Attribute("PropertyDataType")!.Remove());
            await Refuse("asymmetric PDT", master: master => master.Descendants(F.Ns + "Property").Single().SetAttributeValue("PropertyDataType", "PDT_CONTROL"));
            await Refuse("missing current profile", master: master => master.Descendants(F.Ns + "HawkConfigurationData").Single().SetAttributeValue("LegacyVersion", 1));
            await Refuse("missing FirstApp", master: master => master.Descendants(F.Ns + "Feature").Single().Remove());
            await Refuse("external configuration", app: app => F.Program(app).SetAttributeValue("RequiresExternalSoftware", true));
            await Refuse("DefaultProcedure cannot supply the assumed property helper", app: app => F.Program(app).SetAttributeValue("LoadProcedureStyle", "DefaultProcedure"));
            await Refuse("missing allocation", app: app => app.Descendants(F.Ns + "LdCtrlWriteProp").Single().Remove());
            await Refuse("short allocation", app: app => app.Descendants(F.Ns + "LdCtrlWriteProp").Single().SetAttributeValue("Count", 259));
            await Refuse("duplicate actual tuple", app: app => { var write = app.Descendants(F.Ns + "LdCtrlWriteProp").Single(); write.AddAfterSelf(new XElement(write)); });
            await Refuse("PID27", app: app => app.Descendants(F.Ns + "LdCtrlWriteProp").Single().SetAttributeValue("PropId", 27));
            await Refuse("zero start", app: app => app.Descendants(F.Ns + "LdCtrlWriteProp").Single().SetAttributeValue("StartElement", 0));
            await Refuse("zero count", app: app => app.Descendants(F.Ns + "LdCtrlWriteProp").Single().SetAttributeValue("Count", 0));
            await Refuse("malformed factory", hardware: hardware => hardware.Descendants(F.Ns + "Hardware2Program").Single().SetAttributeValue("LoadedImage", "invalid!"));
            await Refuse("declared decoded-empty factory", hardware: hardware => hardware.Descendants(F.Ns + "Hardware2Program").Single()
                .SetAttributeValue("LoadedImage", Convert.ToBase64String(Convert.FromHexString("010800F7FF0100010000000000"))));
            await Refuse("129 actual allocations exceed the full-stage property limit", app: app => F.AddPropertyAllocations(app, 129), master: master => F.AddPropertyDeclarations(master, 129));
            foreach (var pointer in new[] { "GroupAddressTablePtr", "GroupAssociationTablePtr", "GroupObjectTablePtr", "ApplicationDataPtr", "PeiprogDataPtr" })
                await Refuse("contributing pointer " + pointer, master: master => master.Descendants(F.Ns + "HawkConfigurationData").Single().Add(new XElement(F.Ns + "Resources",
                    new XElement(F.Ns + "Resource", new XAttribute("Name", pointer), new XAttribute("Access", "remote"),
                        new XElement(F.Ns + "Location", new XAttribute("AddressSpace", "SystemProperty"), new XAttribute("StartAddress", 0), new XAttribute("InterfaceObjectRef", 3), new XAttribute("PropertyID", 11)),
                        new XElement(F.Ns + "ResourceType", new XAttribute("Length", 4))))));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", oldCatalog);
            Directory.Delete(directory, true);
        }
    }
}

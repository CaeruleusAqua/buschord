using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Knx.Mcp;

// In-process checks for the MCP dispatcher: protocol handshake, tool catalog and the
// project_new -> group_address_add -> group_addresses_list round trip including undo.
// Run: dotnet run --project tests/Knx.Mcp.Tests
var failures = new List<string>();
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine((ok ? "  PASS " : "  FAIL ") + name + (ok || detail is null ? "" : " -> " + detail));
    if (!ok) failures.Add(name + (detail is null ? "" : ": " + detail));
}

// Catalogue tools must not touch the user's library: point them at a temp directory.
var catalogDir = Path.Combine(Path.GetTempPath(), "knx_mcp_catalog_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(catalogDir);
Environment.SetEnvironmentVariable("KNX_CATALOG_DIR", catalogDir);

await ManagementChangeCliChecks.RunAsync(Check);
if (args.Contains("--management-cli")) return failures.Count == 0 ? 0 : 1;
await SecureConnectionMcpChecks.RunAsync(Check);
if (args.Contains("--secure-connections")) return failures.Count == 0 ? 0 : 1;

await using var server = new McpServer();

// ---------------------------------------------------------------- initialize

var initialize = await Call(1, "initialize", new JsonObject
{
    ["protocolVersion"] = "2025-06-18",
    ["capabilities"] = new JsonObject(),
    ["clientInfo"] = new JsonObject { ["name"] = "Knx.Mcp.Tests", ["version"] = "1.0" },
});
var initResult = initialize["result"];
Check("initialize returns 2025-06-18", (string?)initResult?["protocolVersion"] == "2025-06-18",
    initialize.ToJsonString());
Check("initialize names the server", (string?)initResult?["serverInfo"]?["name"] == "buschord");
Check("initialize reports a version", (string?)initResult?["serverInfo"]?["version"] is { Length: > 0 });
Check("initialize advertises tools", initResult?["capabilities"]?["tools"] is not null);
Check("initialize carries instructions", (string?)initResult?["instructions"] is { Length: > 30 });

var initialized = await server.HandleAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
Check("notifications/initialized has no reply", initialized is null);

var ping = await Call(2, "ping");
Check("ping answers with an object", ping["result"] is JsonObject, ping.ToJsonString());

// ---------------------------------------------------------------- tools/list

var listed = await Call(3, "tools/list");
var tools = listed["result"]?["tools"]?.AsArray() ?? new JsonArray();
Check($"tools/list returns {McpToolContract.ExpectedToolCount} tools", tools.Count == McpToolContract.ExpectedToolCount, tools.Count.ToString());
Check("every tool has a snake_case name",
    tools.All(t => (string?)t?["name"] is { Length: > 0 } name &&
                   name.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_')));
Check("every tool has an actionable description",
    tools.All(t => (string?)t?["description"] is { Length: > 20 }));
Check("every input schema is a typed object with properties", tools.All(ValidSchema));
Check("required arguments are declared as properties", tools.All(RequiredDeclared));

static bool ValidSchema(JsonNode? tool)
    => (string?)tool?["inputSchema"]?["type"] == "object"
       && tool?["inputSchema"]?["properties"] is JsonObject;

static bool RequiredDeclared(JsonNode? tool)
{
    var schema = tool?["inputSchema"];
    if (schema?["properties"] is not JsonObject properties) return false;
    return schema["required"] is not JsonArray required
           || required.All(name => properties.ContainsKey((string)name!));
}

// ---------------------------------------------------------------- project + group addresses

var created = await Tool(4, "project_new", new { name = "MCP Testprojekt", style = "three_level", areas = 1, lines = 2 });
Check("project_new succeeds", created["result"]?["isError"] is null, created.ToJsonString());
Check("project_new returns structured project",
    (string?)created["result"]?["structuredContent"]?["project"]?["name"] == "MCP Testprojekt");
Check("project_new returns a text block",
    (string?)created["result"]?["content"]?[0]?["text"] is { Length: > 0 });

var added = await Tool(5, "group_address_add", new { address = "1/1/1", name = "Wohnzimmer Decke", datapoint_type = "1.001" });
Check("group_address_add succeeds", added["result"]?["isError"] is null, added.ToJsonString());
Check("group_address_add normalises the datapoint type",
    (string?)added["result"]?["structuredContent"]?["groupAddress"]?["datapointType"] == "DPST-1-1");

var duplicate = await Tool(6, "group_address_add", new { address = "1/1/1", name = "Doppelt" });
Check("duplicate group address is a tool error", duplicate["result"]?["isError"]?.GetValue<bool>() == true,
    duplicate.ToJsonString());

var filtered = await Tool(7, "group_addresses_list", new { filter = "wohnzimmer" });
Check("filtered list finds the address", filtered["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);
Check("round trip keeps the name",
    (string?)filtered["result"]?["structuredContent"]?["groupAddresses"]?[0]?["name"] == "Wohnzimmer Decke");

var all = await Tool(8, "group_addresses_list");
Check("unfiltered list returns the address",
    (string?)all["result"]?["structuredContent"]?["groupAddresses"]?[0]?["address"] == "1/1/1");

var invalidAddress = await Tool(9, "group_address_add", new { address = "not-an-address", name = "X" });
Check("invalid group address is a tool error", invalidAddress["result"]?["isError"]?.GetValue<bool>() == true);

// ---------------------------------------------------------------- error handling

var unknownTool = await Tool(10, "no_such_tool");
Check("unknown tool is a tool error", unknownTool["result"]?["isError"]?.GetValue<bool>() == true);
Check("unknown tool still returns a text block",
    (string?)unknownTool["result"]?["content"]?[0]?["text"] is { Length: > 0 });

var malformed = await server.HandleAsync("{this is not json");
var malformedJson = JsonNode.Parse(malformed ?? "null");
Check("malformed JSON answers -32700", malformedJson?["error"]?["code"]?.GetValue<int>() == -32700,
    malformed);
Check("malformed JSON answers with null id", malformedJson?["id"] is null);

var unknownMethod = await Call(11, "tools/does-not-exist");
Check("unknown method answers -32601", unknownMethod["error"]?["code"]?.GetValue<int>() == -32601);

var missingName = await Call(12, "tools/call", new JsonObject());
Check("tools/call without name answers -32602", missingName["error"]?["code"]?.GetValue<int>() == -32602);

// ---------------------------------------------------------------- undo / redo

var undo = await Tool(13, "undo");
Check("undo succeeds", undo["result"]?["isError"] is null, undo.ToJsonString());
var afterUndo = await Tool(14, "group_addresses_list");
Check("undo restores the state before the last mutation",
    afterUndo["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 0);
Check("undo reports that redo is possible",
    undo["result"]?["structuredContent"]?["undo"]?["canRedo"]?.GetValue<bool>() == true);

var redo = await Tool(15, "redo");
Check("redo succeeds", redo["result"]?["isError"] is null, redo.ToJsonString());
var afterRedo = await Tool(16, "group_addresses_list");
Check("redo re-applies the group address",
    afterRedo["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);

// ---------------------------------------------------------------- devices

var device = await Tool(17, "device_add", new { line_area = 0, line_address = 0, name = "Schaltaktor" });
Check("device_add succeeds", device["result"]?["isError"] is null, device.ToJsonString());
Check("device_add assigns the next free address",
    (string?)device["result"]?["structuredContent"]?["device"]?["address"] == "0.0.1");

var devicesOnLine0 = await Tool(18, "devices_list", new { line = "0.0" });
Check("devices_list filters by line",
    devicesOnLine0["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);

var moved = await Tool(19, "device_move", new { address = "0.0.1", target_area = 0, target_line = 1 });
Check("device_move succeeds", moved["result"]?["isError"] is null, moved.ToJsonString());
var devicesOnLine1 = await Tool(20, "devices_list", new { line = "0.1" });
Check("device_move lands on the target line",
    devicesOnLine1["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1);

var updated = await Tool(21, "device_update", new { address = "0.0.1", name = "Schaltaktor 8-fach" });
Check("device_update succeeds", updated["result"]?["isError"] is null, updated.ToJsonString());
Check("device_update keeps the address",
    (string?)updated["result"]?["structuredContent"]?["device"]?["address"] == "0.0.1");

var deleted = await Tool(22, "device_delete", new { address = "0.0.1" });
Check("device_delete succeeds", deleted["result"]?["isError"] is null, deleted.ToJsonString());
var afterDelete = await Tool(23, "devices_list");
Check("device_delete removes the device",
    afterDelete["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 0);

// ---------------------------------------------------------------- bus connect/disconnect

var busStatusBefore = await Tool(30, "bus_status");
Check("bus_status meldet getrennt",
    busStatusBefore["result"]?["structuredContent"]?["connected"]?.GetValue<bool>() == false,
    busStatusBefore.ToJsonString());

// Never discover/connect real hardware from the automated test suite.
var busConnect = await Tool(31, "bus_connect", new { mode = "tunnel", own_address = "not-an-address" });
Check("bus_connect rejects malformed input without network access",
    busConnect["result"]?["isError"]?.GetValue<bool>() == true, busConnect.ToJsonString());
Check("bus_connect nennt den Grund",
    (busConnect["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
        .Contains("InvalidProfile", StringComparison.OrdinalIgnoreCase), busConnect.ToJsonString());

var badMode = await Tool(32, "bus_connect", new { mode = "carrier-pigeon" });
Check("bus_connect lehnt unbekannten Modus ab",
    badMode["result"]?["isError"]?.GetValue<bool>() == true, badMode.ToJsonString());

var badAddress = await Tool(33, "bus_connect", new { mode = "tunnel", own_address = "kaputt" });
Check("bus_connect validiert die eigene Adresse",
    badAddress["result"]?["isError"]?.GetValue<bool>() == true, badAddress.ToJsonString());

var readTool = tools.FirstOrDefault(t => (string?)t?["name"] == "bus_read");
Check("bus_read wartet auf eine GroupValueResponse mit Timeout",
    (string?)readTool?["description"] is { } readDescription
    && readDescription.Contains("GroupValueResponse", StringComparison.Ordinal)
    && readTool?["inputSchema"]?["properties"]?["timeout_ms"] is not null,
    readTool?.ToJsonString() ?? "bus_read fehlt");
var busWriteRefused = await Tool(35, "bus_write", new { group_address = "1/1/1", value = "On" });
Check("bus_write wird von der Read-only-Policy abgelehnt",
    busWriteRefused["result"]?["isError"]?.GetValue<bool>() == true
    && (busWriteRefused["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
        .Contains("read-only", StringComparison.Ordinal), busWriteRefused.ToJsonString());

var busDisconnect = await Tool(34, "bus_disconnect");
Check("bus_disconnect ist idempotent",
    busDisconnect["result"]?["structuredContent"]?["connected"]?.GetValue<bool>() == false,
    busDisconnect.ToJsonString());

// ---------------------------------------------------------------- catalogue (.knxprod)

var catalogFixture = """
<?xml version="1.0" encoding="utf-8"?>
<KNX xmlns="http://knx.org/xml/prod/20">
  <ManufacturerData>
    <Manufacturer RefId="M-00FA">
      <Catalog>
        <CatalogSection Id="CS-1" Name="Lighting" Number="1">
          <CatalogItem Id="CI-1" Name="Actuator 4" Number="1"
                       ProductRefId="M-00FA_H-1_P-1" Hardware2ProgramRefId="M-00FA_H-1_HP-1"/>
        </CatalogSection>
      </Catalog>
      <Hardware>
        <Hardware Id="M-00FA_H-1" Name="Actuator" SerialNumber="1" VersionNumber="1"
                  HasIndividualAddress="true" HasApplicationProgram="true">
          <Products>
            <Product Id="M-00FA_H-1_P-1" Text="Actuator 4" OrderNumber="AX-4"
                     IsRailMounted="true" VisibleDescription="Fourfold switch actuator"/>
            <Product Id="M-00FA_H-1_P-2" Text="Actuator 8" OrderNumber="AX-8" IsRailMounted="true"/>
          </Products>
          <Hardware2Programs>
            <Hardware2Program Id="M-00FA_H-1_HP-1">
              <ApplicationProgramRef RefId="M-00FA_A-1"/>
            </Hardware2Program>
          </Hardware2Programs>
        </Hardware>
      </Hardware>
      <ApplicationPrograms>
        <ApplicationProgram Id="M-00FA_A-1" Name="Switch actuator" ApplicationNumber="008A"
                            ApplicationVersion="25">
          <Static>
            <ComObjectTable>
              <ComObject Id="CO-1" Name="Switch" Text="Switch" FunctionText="Switching"
                         Number="1" ObjectSize="1 Bit"/>
              <ComObject Id="CO-2" Name="Status" Text="Status" FunctionText="Status"
                         Number="2" ObjectSize="1 Bit"/>
            </ComObjectTable>
          </Static>
        </ApplicationProgram>
      </ApplicationPrograms>
    </Manufacturer>
  </ManufacturerData>
</KNX>
""";

var knxprodPath = Path.Combine(Path.GetTempPath(), "knx_mcp_fixture_" + Guid.NewGuid().ToString("N") + ".knxprod");
using (var zip = ZipFile.Open(knxprodPath, ZipArchiveMode.Create))
{
    using var writer = new StreamWriter(zip.CreateEntry("knxprod.xml").Open(), new UTF8Encoding(false));
    writer.Write(catalogFixture);
}

var catalogImport = await Tool(40, "catalog_import", new { path = knxprodPath });
Check("catalog_import liest die knxprod",
    catalogImport["result"]?["structuredContent"]?["products"]?.GetValue<int>() == 2,
    catalogImport.ToJsonString());

var catalogLibrary = await Tool(41, "catalog_library");
Check("catalog_library listet den Katalog",
    catalogLibrary["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1,
    catalogLibrary.ToJsonString());

var catalogProducts = await Tool(42, "catalog_products", new { filter = "Actuator" });
Check("catalog_products findet beide Produkte",
    catalogProducts["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 2,
    catalogProducts.ToJsonString());

var inserted = await Tool(43, "catalog_import_device", new
{
    product_id = "M-00FA_H-1_P-1",
    line_area = 0,
    line_address = 0,
    name = "Katalogaktor",
});
Check("catalog_import_device setzt Kommunikationsobjekte",
    inserted["result"]?["structuredContent"]?["comObjects"]?.GetValue<int>() == 2,
    inserted.ToJsonString());

var devicesAfterCatalog = await Tool(44, "devices_list");
Check("Kataloggerät liegt im Projekt",
    devicesAfterCatalog["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 1,
    devicesAfterCatalog.ToJsonString());

var undoCatalog = await Tool(45, "undo");
Check("undo entfernt das Kataloggerät", undoCatalog["result"]?["isError"] is null,
    undoCatalog.ToJsonString());
Check("Projekt ist nach undo wieder leer",
    (await Tool(46, "devices_list"))["result"]?["structuredContent"]?["count"]?.GetValue<int>() == 0);

var missingProduct = await Tool(47, "catalog_import_device", new
{
    product_id = "gibt-es-nicht",
    line_area = 0,
    line_address = 0,
});
Check("unbekanntes Produkt ist ein Tool-Fehler",
    missingProduct["result"]?["isError"]?.GetValue<bool>() == true, missingProduct.ToJsonString());

// ---------------------------------------------------------------- manual links (offline, synthetic catalogue fixture)

await using (var identityServer = new McpServer())
{
    async Task<JsonNode> IdentityTool(string name, object arguments)
    {
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 900, method = "tools/call",
            @params = new { name, arguments }
        });
        return JsonNode.Parse(await identityServer.HandleAsync(request) ?? "null")!;
    }
    await IdentityTool("project_new", new { name = "Identity", style = "three_level", areas = 1, lines = 2 });
    await IdentityTool("catalog_import", new { path = knxprodPath });
    await IdentityTool("catalog_import_device", new { product_id = "M-00FA_H-1_P-1", line_area = 0, line_address = 0, address = "0.0.1" });
    var original = Knx.ProjectQuery.DeviceList(identityServer.Project!).Single();
    original.Address = new Knx.IndividualAddress(0, 0, 2);
    var second = await IdentityTool("catalog_import_device", new { product_id = "M-00FA_H-1_P-1", line_area = 0, line_address = 0, address = "0.0.1" });
    var identityDevices = Knx.ProjectQuery.DeviceList(identityServer.Project!).ToList();
    Check("catalog MCP insertion keeps stable identities unique after readdressing",
        second["result"]?["isError"] is null && identityDevices.Count == 2
        && identityDevices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() == 2, second.ToJsonString());
    var outside = await IdentityTool("catalog_import_device", new { product_id = "M-00FA_H-1_P-1", line_area = 0, line_address = 0, address = "1.1.3" });
    Check("catalog MCP rejects an address outside the requested line before mutation",
        outside["result"]?["isError"]?.GetValue<bool>() == true
        && Knx.ProjectQuery.DeviceList(identityServer.Project!).Count() == 2, outside.ToJsonString());
    await IdentityTool("group_address_add", new { address = "1/1/1", name = "Size", datapoint_type = "1.001" });
    var sized = Knx.ProjectQuery.GroupAddresses(identityServer.Project!).Single();
    identityServer.Project!.Workbench.Groups[sized.Id] = new Knx.Workbench.GroupMetadata { Bits = 1 };
    await IdentityTool("group_address_update", new { address = "1/1/1", comment = "before DPT edit" });
    var resized = await IdentityTool("group_address_update", new { address = "1/1/1", datapoint_type = "9.001" });
    Check("MCP DPT edit updates retained group bit size through the shared operation",
        resized["result"]?["isError"] is null && identityServer.Project.Workbench.Groups[sized.Id].Bits == 16,
        resized.ToJsonString());
    await IdentityTool("undo", new { });
    Check("MCP DPT edit restores bit size on undo", identityServer.Project!.Workbench.Groups[sized.Id].Bits == 1);
    var importedName = Knx.ProjectQuery.GroupAddresses(identityServer.Project).Single();
    importedName.Name = "";
    var emptyNameEdit = await IdentityTool("group_address_update", new { address = "1/1/1", datapoint_type = "9.001" });
    Check("MCP DPT-only update accepts and preserves an imported empty name",
        emptyNameEdit["result"]?["isError"] is null && importedName.Name == ""
        && identityServer.Project.Workbench.Groups[importedName.Id].Bits == 16);
    importedName.Name = " Licht ";
    var paddedNameEdit = await IdentityTool("group_address_update", new { address = "1/1/1", datapoint_type = "1.001" });
    Check("MCP DPT-only update does not trim an omitted name",
        paddedNameEdit["result"]?["isError"] is null && importedName.Name == " Licht ");
    var clearedName = await IdentityTool("group_address_update", new { address = "1/1/1", name = (string?)null });
    Check("MCP partial update still supports explicit null to clear a name",
        clearedName["result"]?["isError"] is null && importedName.Name == "");
}

await Tool(50, "redo");
var objects = await Tool(51, "communication_objects_list", new { device_address = "0.0.1" });
var objectRows = objects["result"]?["structuredContent"]?["communicationObjects"]?.AsArray();
Check("communication_objects_list exposes reference ids and text", objectRows?.Count == 2 &&
    (string?)objectRows[0]?["id"] is { Length: > 0 } && (string?)objectRows[0]?["text"] == "Switch",
    objects.ToJsonString());
var objectId = (string?)objectRows?[0]?["id"] ?? "missing";
Check("communication_objects_list rejects a missing device",
    (await Tool(51, "communication_objects_list", new { device_address = "0.0.99" }))["result"]?["isError"]?.GetValue<bool>() == true);
await using (var emptyServer = new McpServer())
{
    foreach (var name in new[] { "communication_objects_list", "group_link_add", "group_link_remove", "download_plan" })
    {
        var response = await emptyServer.HandleAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = 1, method = "tools/call",
            @params = new { name, arguments = new { device_address = "0.0.1", object_id = objectId, group_address = "1/1/1" } },
        }));
        Check(name + " requires an open project", JsonNode.Parse(response!)?["result"]?["isError"]?.GetValue<bool>() == true);
    }
}
var linkArgs = new { device_address = "0.0.1", object_id = objectId, group_address = "1/1/1" };
var linked = await Tool(52, "group_link_add", linkArgs);
Check("manual link adds without name matching", linked["result"]?["structuredContent"]?["changed"]?.GetValue<bool>() == true, linked.ToJsonString());
var repeatedLink = await Tool(53, "group_link_add", linkArgs);
Check("manual link duplicate does not add history", repeatedLink["result"]?["structuredContent"]?["changed"]?.GetValue<bool>() == false &&
    repeatedLink["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() == linked["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>());
var linkedObjects = await Tool(54, "communication_objects_list", new { device_address = "0.0.1" });
Check("manual link list resolves GA address/name", (string?)linkedObjects["result"]?["structuredContent"]?["communicationObjects"]?[0]?["links"]?[0]?["address"] == "1/1/1" &&
    (string?)linkedObjects["result"]?["structuredContent"]?["communicationObjects"]?[0]?["links"]?[0]?["name"] == "Wohnzimmer Decke");
foreach (var name in new[] { "group_link_add", "group_link_remove" })
{
    var beforeError = Knx.Knxproj.ToXml(server.Project!).ToString();
    foreach (var invalid in new object[]
    {
        new { device_address = "0.0.1", object_id = "missing", group_address = "1/1/1" },
        new { device_address = "0.0.1", object_id = objectId, group_address = "1/1/99" },
        new { device_address = "0.0.99", object_id = objectId, group_address = "1/1/1" },
        new { device_address = "invalid", object_id = objectId, group_address = "1/1/1" },
        new { device_address = "0.0.1", object_id = objectId, group_address = "invalid" },
        new { device_address = "0.0.1", object_id = " ", group_address = "1/1/1" },
        new { device_address = "0.0.1", object_id = 1, group_address = "1/1/1" },
        new { device_address = "0.0.1", group_address = "1/1/1" },
    })
        Check(name + " rejects invalid input without mutation", (await Tool(55, name, invalid))["result"]?["isError"]?.GetValue<bool>() == true &&
            Knx.Knxproj.ToXml(server.Project!).ToString() == beforeError);
}
await Tool(56, "undo");
Check("undo removes manual link despite duplicate and errors", Knx.ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs[0].Links.Count == 0);
await Tool(56, "group_link_remove", linkArgs);
await Tool(57, "redo");
Check("redo restores manual link", Knx.ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs[0].Links.Count == 1);
var unlinked = await Tool(58, "group_link_remove", linkArgs);
Check("manual unlink removes link", unlinked["result"]?["structuredContent"]?["changed"]?.GetValue<bool>() == true);
var repeatedUnlink = await Tool(59, "group_link_remove", linkArgs);
Check("manual unlink is idempotent without history", repeatedUnlink["result"]?["structuredContent"]?["changed"]?.GetValue<bool>() == false &&
    repeatedUnlink["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() == unlinked["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>());
await Tool(60, "undo");
Check("undo restores removed manual link", Knx.ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs[0].Links.Count == 1);
await Tool(61, "redo");
Check("redo removes manual link", Knx.ProjectQuery.DeviceList(server.Project!).Single().ComObjectRefs[0].Links.Count == 0);
Check("manual tools leave bus disconnected", (await Tool(62, "bus_status"))["result"]?["structuredContent"]?["connected"]?.GetValue<bool>() == false);

// ---------------------------------------------------------------- download preflight (read-only)

var downloadPlanTool = tools.FirstOrDefault(t => (string?)t?["name"] == "download_plan");
Check("download_plan ist als read-only beschrieben",
    (string?)downloadPlanTool?["description"] is { } planDescription
    && planDescription.Contains("read-only", StringComparison.OrdinalIgnoreCase)
    && planDescription.Contains("dry run", StringComparison.OrdinalIgnoreCase),
    downloadPlanTool?.ToJsonString() ?? "download_plan fehlt");

var downloadPlan = await Tool(90, "download_plan", new { device_address = "0.0.1" });
var planContent = downloadPlan["result"]?["structuredContent"];
var planBlockers = planContent?["plan"]?["blockers"]?.AsArray() ?? new JsonArray();
Check("download_plan liefert einen Read-only-Status",
    downloadPlan["result"]?["isError"] is null
    && planContent?["readOnly"]?.GetValue<bool>() == true
    && planContent?["writesEnabled"]?.GetValue<bool>() == false
    && planContent?["realDownloadAvailable"]?.GetValue<bool>() == false,
    downloadPlan.ToJsonString());
Check("download_plan verweigert ohne Descriptor, Programm und Bestätigungen",
    planContent?["plan"]?["ready"]?.GetValue<bool>() == false
    && planBlockers.Count(b => (string?)b?["code"] == "descriptor_missing") == 1
    && planBlockers.Count(b => (string?)b?["code"] == "program_missing") == 1
    && planContent?["confirmations"]?["programmingMode"]?.GetValue<bool>() == false
    && planContent?["confirmations"]?["address"]?.GetValue<bool>() == false
    && planContent?["confirmations"]?["dryRun"]?.GetValue<bool>() == false,
    downloadPlan.ToJsonString());
Check("download_plan druckt den Dry Run",
    (string?)planContent?["plan"]?["dryRun"] is { Length: > 0 } downloadDryRun
    && downloadDryRun.Contains("0.0.1") && downloadDryRun.Contains("no program"),
    downloadPlan.ToJsonString());
Check("download_plan nennt Gerät und Zeile aus dem Projekt",
    (string?)planContent?["device"]?["address"] == "0.0.1" && planContent?["device"]?["line"] is not null,
    downloadPlan.ToJsonString());

var downloadPlanUnknown = await Tool(91, "download_plan", new { device_address = "0.0.99" });
Check("download_plan lehnt ein unbekanntes Gerät ab",
    downloadPlanUnknown["result"]?["isError"]?.GetValue<bool>() == true, downloadPlanUnknown.ToJsonString());

// ---------------------------------------------------------------- report export

var reportPath = Path.Combine(Path.GetTempPath(), "knx_mcp_report_" + Guid.NewGuid().ToString("N") + ".html");
var reportExport = await Tool(63, "report_export", new { path = reportPath });
var reportContent = File.Exists(reportPath) ? File.ReadAllText(reportPath) : "";
Check("report_export writes the report", reportExport["result"]?["isError"] is null && reportContent.Length > 0,
    reportExport.ToJsonString());
Check("report_export reports project counts",
    reportExport["result"]?["structuredContent"]?["groupAddresses"]?.GetValue<int>() == 1 &&
    reportExport["result"]?["structuredContent"]?["devices"]?.GetValue<int>() == 1, reportExport.ToJsonString());
Check("report_export output is self-contained HTML", reportContent.StartsWith("<!DOCTYPE html>") &&
    reportContent.Contains("Projektdokumentation") && !reportContent.Contains("<script") &&
    !reportContent.Contains("http://"));
var smallReportPath = Path.Combine(Path.GetTempPath(), "knx_mcp_report_small_" + Guid.NewGuid().ToString("N") + ".html");
var smallReport = await Tool(64, "report_export", new
{
    path = smallReportPath, include_title_page = false, include_topology = false,
    include_group_addresses = false, include_com_objects = false, include_link_matrix = false,
});
Check("report_export honours section switches",
    smallReport["result"]?["isError"] is null && File.Exists(smallReportPath) &&
    !File.ReadAllText(smallReportPath).Contains("Topologie und Geräte"));
foreach (var path in new[] { reportPath, smallReportPath })
    if (File.Exists(path)) File.Delete(path);

// ---------------------------------------------------------------- acceptance protocol export

await Tool(90, "group_link_add", linkArgs);
var acceptancePath = Path.Combine(Path.GetTempPath(), "knx_mcp_acceptance_" + Guid.NewGuid().ToString("N") + ".html");
var acceptanceBefore = Knx.Knxproj.ToXml(server.Project!).ToString();
var acceptance = await Tool(91, "acceptance_protocol_export", new
{
    path = acceptancePath,
    technician = "T. Prüfer",
    building_owner = "B. Eigentümer",
});
var acceptanceContent = File.Exists(acceptancePath) ? File.ReadAllText(acceptancePath) : "";
Check("acceptance_protocol_export writes HTML",
    acceptance["result"]?["isError"] is null && acceptanceContent.Length > 0, acceptance.ToJsonString());
Check("acceptance_protocol_export reports devices and open items",
    acceptance["result"]?["structuredContent"]?["devices"]?.GetValue<int>() == 1 &&
    acceptance["result"]?["structuredContent"]?["openItems"]?.GetValue<int>() == 1, acceptance.ToJsonString());
Check("acceptance protocol is honest without recorded states",
    acceptance["result"]?["structuredContent"]?["commissioning"]?.GetValue<string>() == "not_recorded" &&
    acceptanceContent.Contains("Kein Download aufgezeichnet") &&
    acceptanceContent.Contains("kein Download aufgezeichnet") &&
    acceptanceContent.Contains("nicht geprüft") &&
    acceptanceContent.Contains("T. Prüfer") && acceptanceContent.Contains("B. Eigentümer") &&
    !acceptanceContent.Contains("<script") && !acceptanceContent.Contains("http://"),
    acceptance.ToJsonString());
Check("acceptance protocol carries a document hash",
    (string?)acceptance["result"]?["structuredContent"]?["documentHash"] is { Length: 64 } hash &&
    acceptanceContent.Contains("sha256:" + hash), acceptance.ToJsonString());
Check("acceptance protocol names the device and group address",
    acceptanceContent.Contains("Katalogaktor") && acceptanceContent.Contains("Wohnzimmer Decke"));
Check("acceptance_protocol_export does not change the project",
    Knx.Knxproj.ToXml(server.Project!).ToString() == acceptanceBefore);
Check("acceptance_protocol_export leaves the bus disconnected",
    (await Tool(92, "bus_status"))["result"]?["structuredContent"]?["connected"]?.GetValue<bool>() == false);

// A recorded demo load proves simulation only; it must never read as a real download.
var acceptanceDeviceId = Knx.ProjectQuery.DeviceList(server.Project!).Single().Id;
var acceptanceSignature = Knx.Workbench.WorkbenchProject.ConfigurationSignature(server.Project!, acceptanceDeviceId);
var demoState = Knx.Workbench.DeviceProgrammingState.Empty(acceptanceDeviceId)
    .ApplySuccess(Knx.Workbench.DeviceLoadFlags.All, acceptanceSignature, DateTimeOffset.UnixEpoch, "iface-1",
        Knx.Workbench.ProgrammingOrigin.Demo);
var statesPath = Path.Combine(Path.GetTempPath(), "knx_mcp_states_" + Guid.NewGuid().ToString("N") + ".json");
File.WriteAllText(statesPath, Knx.Workbench.DeviceProgrammingStates.Serialize(server.Project!.Id, new[] { demoState }));
var simulatedPath = Path.Combine(Path.GetTempPath(), "knx_mcp_acceptance_sim_" + Guid.NewGuid().ToString("N") + ".html");
var simulated = await Tool(93, "acceptance_protocol_export", new
{
    path = simulatedPath,
    states_path = statesPath,
    language = "en",
});
var simulatedContent = File.Exists(simulatedPath) ? File.ReadAllText(simulatedPath) : "";
Check("acceptance protocol reflects a recorded demo load as simulation",
    simulated["result"]?["structuredContent"]?["commissioning"]?.GetValue<string>() == "simulation" &&
    simulated["result"]?["structuredContent"]?["openItems"]?.GetValue<int>() == 0 &&
    simulatedContent.Contains("Simulation – recorded demo loads") &&
    !simulatedContent.Contains("loaded for real"), simulated.ToJsonString());

var acceptanceCsvPath = Path.Combine(Path.GetTempPath(), "knx_mcp_acceptance_" + Guid.NewGuid().ToString("N") + ".csv");
var acceptanceCsv = await Tool(94, "acceptance_protocol_export", new
{
    path = acceptanceCsvPath,
    format = "csv",
    states_path = statesPath,
});
var acceptanceCsvContent = File.Exists(acceptanceCsvPath) ? File.ReadAllText(acceptanceCsvPath) : "";
Check("acceptance protocol CSV carries the device row",
    acceptanceCsv["result"]?["isError"] is null && acceptanceCsvContent.Contains("Adresse") &&
    acceptanceCsvContent.Contains("Katalogaktor") && acceptanceCsvContent.Contains("Simulation (Demo)"),
    acceptanceCsv.ToJsonString());

var wrongStatesPath = Path.Combine(Path.GetTempPath(), "knx_mcp_states_wrong_" + Guid.NewGuid().ToString("N") + ".json");
File.WriteAllText(wrongStatesPath, Knx.Workbench.DeviceProgrammingStates.Serialize("P-OTHER", new[] { demoState }));
var rejectedStates = await Tool(95, "acceptance_protocol_export", new { path = acceptancePath, states_path = wrongStatesPath });
Check("acceptance_protocol_export rejects a foreign state file",
    rejectedStates["result"]?["isError"]?.GetValue<bool>() == true &&
    (rejectedStates["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
        .Contains("another project", StringComparison.Ordinal), rejectedStates.ToJsonString());

foreach (var path in new[] { acceptancePath, simulatedPath, acceptanceCsvPath, statesPath, wrongStatesPath })
    if (File.Exists(path)) File.Delete(path);

// ---------------------------------------------------------------- handover package export

var handoverProjectPath = Path.Combine(Path.GetTempPath(),
    "knx_mcp_handover_project_" + Guid.NewGuid().ToString("N") + ".knxproj");
var handoverSaved = await Tool(96, "project_save", new { path = handoverProjectPath });
Check("handover test project saved", handoverSaved["result"]?["isError"] is null, handoverSaved.ToJsonString());
var handoverProjectId = server.Project!.Id;
var handoverProjectBytes = File.ReadAllBytes(handoverProjectPath);
var handoverXmlBefore = Knx.Knxproj.ToXml(server.Project!).ToString();
var handoverUndoBefore = (await Tool(97, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>();

var handoverPath = Path.Combine(Path.GetTempPath(), "knx_mcp_handover_" + Guid.NewGuid().ToString("N") + ".zip");
var handover = await Tool(98, "handover_package_export", new
{
    output = handoverPath,
    language = "de",
    generated_at = "2026-10-22T12:00:00+02:00",
    technician = "T. Prüfer",
    building_owner = "B. Eigentümer",
});
Check("handover_package_export writes a ZIP",
    handover["result"]?["isError"] is null && File.Exists(handoverPath), handover.ToJsonString());
var handoverContent = handover["result"]?["structuredContent"];
Check("handover_package_export reports manifest, files and status",
    handoverContent?["files"]?.GetValue<int>() == 17 &&
    (string?)handoverContent?["manifestHash"] is { Length: 64 } &&
    handoverContent?["commissioning"]?.GetValue<string>() == "not_recorded" &&
    handoverContent?["signature"]?.GetValue<string>() is { Length: > 0 } &&
    (string?)handoverContent?["root"] is { Length: > 0 }, handover.ToJsonString());
using (var handoverZip = ZipFile.OpenRead(handoverPath))
{
    var entries = handoverZip.Entries.Select(entry => entry.FullName).ToList();
    Check("handover package ZIP has the manifest first",
        entries.Count == 17 && entries[0].EndsWith("/00_MANIFEST.sha256", StringComparison.Ordinal));
    Check("handover package ZIP includes room book, README and protocol",
        entries.Any(entry => entry.EndsWith("/06_raum_buch.csv", StringComparison.Ordinal)) &&
        entries.Any(entry => entry.EndsWith("/00_README.txt", StringComparison.Ordinal)) &&
        entries.Any(entry => entry.EndsWith("/10_pruefprotokoll.html", StringComparison.Ordinal)));
    var manifest = ReadZipEntry(handoverZip, "00_MANIFEST.sha256");
    Check("handover manifest carries hashes and the project identity",
        manifest.Contains("# project: MCP Testprojekt") && manifest.Contains("# files: 16") &&
        manifest.Contains("# acceptance-protocol: 10_pruefprotokoll.html") &&
        manifest.Contains("# generated: 2026-10-22T12:00:00"));
}
Check("handover_package_export does not change the project",
    Knx.Knxproj.ToXml(server.Project!).ToString() == handoverXmlBefore);
Check("handover_package_export leaves the project file byte-identical",
    File.ReadAllBytes(handoverProjectPath).SequenceEqual(handoverProjectBytes));
Check("handover_package_export adds no undo step",
    (await Tool(99, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() ==
    handoverUndoBefore);
Check("handover_package_export leaves the bus disconnected",
    (await Tool(100, "bus_status"))["result"]?["structuredContent"]?["connected"]?.GetValue<bool>() == false);
Check("handover package contains no local source paths",
    ZipEntryTexts(handoverPath).All(text => !text.Contains(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)));

// Guard rails: never overwrite an input, reject foreign states and non-ZIP targets.
var handoverOverwrite = await Tool(101, "handover_package_export", new { output = handoverProjectPath });
Check("handover_package_export refuses to overwrite the project file",
    handoverOverwrite["result"]?["isError"]?.GetValue<bool>() == true &&
    Knx.Knxproj.ToXml(server.Project!).ToString() == handoverXmlBefore, handoverOverwrite.ToJsonString());
var handoverGuardPath = Path.Combine(Path.GetTempPath(), "knx_mcp_handover_guard_" + Guid.NewGuid().ToString("N") + ".zip");
var handoverForeignStatePath = Path.Combine(Path.GetTempPath(),
    "knx_mcp_handover_states_wrong_" + Guid.NewGuid().ToString("N") + ".json");
var handoverDemo = Knx.Workbench.DeviceProgrammingState.Empty(acceptanceDeviceId)
    .ApplySuccess(Knx.Workbench.DeviceLoadFlags.All, acceptanceSignature, DateTimeOffset.UnixEpoch, "iface-1",
        Knx.Workbench.ProgrammingOrigin.Demo);
File.WriteAllText(handoverForeignStatePath,
    Knx.Workbench.DeviceProgrammingStates.Serialize("P-OTHER", new[] { handoverDemo }));
var handoverForeign = await Tool(102, "handover_package_export", new
{
    output = handoverGuardPath,
    states_path = handoverForeignStatePath,
});
Check("handover_package_export rejects a foreign state file",
    handoverForeign["result"]?["isError"]?.GetValue<bool>() == true &&
    !File.Exists(handoverGuardPath) &&
    (handoverForeign["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
    .Contains("another project", StringComparison.Ordinal), handoverForeign.ToJsonString());
var handoverNotZip = await Tool(103, "handover_package_export", new
{
    output = Path.ChangeExtension(handoverGuardPath, ".txt"),
});
Check("handover_package_export requires a .zip output",
    handoverNotZip["result"]?["isError"]?.GetValue<bool>() == true &&
    (handoverNotZip["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
    .Contains("zip", StringComparison.Ordinal), handoverNotZip.ToJsonString());

var handoverStatesPath = Path.Combine(Path.GetTempPath(),
    "knx_mcp_handover_states_" + Guid.NewGuid().ToString("N") + ".json");
File.WriteAllText(handoverStatesPath,
    Knx.Workbench.DeviceProgrammingStates.Serialize(handoverProjectId, new[] { handoverDemo }));
var handoverSimulatedPath = Path.Combine(Path.GetTempPath(),
    "knx_mcp_handover_sim_" + Guid.NewGuid().ToString("N") + ".zip");
var handoverSimulated = await Tool(104, "handover_package_export", new
{
    output = handoverSimulatedPath,
    states_path = handoverStatesPath,
    language = "en",
});
Check("handover package labels a recorded demo load as simulation",
    handoverSimulated["result"]?["isError"] is null &&
    handoverSimulated["result"]?["structuredContent"]?["commissioning"]?.GetValue<string>() == "simulation" &&
    ZipEntryTexts(handoverSimulatedPath).Any(text => text.Contains("# commissioning: simulation")) &&
    ZipEntryTexts(handoverSimulatedPath).Any(text => text.Contains("simulation – demo loads only")),
    handoverSimulated.ToJsonString());

var handoverFromPath = Path.Combine(Path.GetTempPath(),
    "knx_mcp_handover_path_" + Guid.NewGuid().ToString("N") + ".zip");
var handoverReadPath = await Tool(105, "handover_package_export", new
{
    path = handoverProjectPath,
    output = handoverFromPath,
});
Check("handover_package_export reads a project path without replacing the open project",
    handoverReadPath["result"]?["isError"] is null && File.Exists(handoverFromPath) &&
    server.Project!.Id == handoverProjectId &&
    Knx.Knxproj.ToXml(server.Project!).ToString() == handoverXmlBefore);
Check("handover_package_export still adds no undo step after a path export",
    (await Tool(106, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() ==
    handoverUndoBefore);

foreach (var path in new[]
         {
             handoverProjectPath, handoverPath, handoverGuardPath, handoverForeignStatePath,
             handoverStatesPath, handoverSimulatedPath, handoverFromPath,
         })
    if (File.Exists(path)) File.Delete(path);

// ---------------------------------------------------------------- project compare (revision-aware)

static Knx.Project CompareProject()
{
    var project = new Knx.Project { Id = "P-CMP", Name = "Vergleich" };
    var installation = new Knx.Installation { Id = "I-CMP", Name = "Haus" };
    var area = new Knx.Area { Id = "A-CMP", Address = 1 };
    var line = new Knx.Line { Id = "L-CMP", Address = 1, Name = "Linie 1" };
    line.Devices.Add(new Knx.DeviceInstance
    {
        Id = "D-CMP",
        Address = new Knx.IndividualAddress(1, 1, 1),
        Name = "Aktor",
        ProductRefId = "M-1",
        Hardware2ProgramRefId = "M-1_H-1",
    });
    line.Segments.Add(new Knx.Segment { Id = "S-CMP", Address = 0, MediumTypeRefId = "MT-0" });
    area.Lines.Add(line);
    installation.Areas.Add(area);
    project.Installations.Add(installation);
    return project;
}

var compareDir = Path.Combine(Path.GetTempPath(), "knx_mcp_compare_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(compareDir);
var compareLeftPath = Path.Combine(compareDir, "links.knxproj");
var compareRightPath = Path.Combine(compareDir, "rechts.knxproj");
var compareLeft = CompareProject();
Knx.Knxproj.Save(compareLeft, compareLeftPath);
var compareRight = Knx.Knxproj.Load(compareLeftPath);
compareRight.Installations.Single().Areas.Single().Lines.Single().Segments.Single().MediumTypeRefId = "MT-2";
Knx.Knxproj.Save(compareRight, compareRightPath);
var compareTimestamp = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.FromHours(2));
Knx.ProjectRevisions.Capture(compareLeft, compareLeftPath, "links", compareTimestamp);
Knx.ProjectRevisions.Capture(compareRight, compareRightPath, "rechts", compareTimestamp.AddMinutes(1));
Knx.Knxproj.Save(compareLeft, compareLeftPath);
Knx.Knxproj.Save(compareRight, compareRightPath);

var compareArgs = new
{
    left_path = compareLeftPath,
    right_path = compareRightPath,
    generated_at = "2026-10-22T12:30:00+02:00",
};
var compared = await Tool(107, "project_compare", compareArgs);
var comparedContent = compared["result"]?["structuredContent"];
Check("project_compare keeps the legacy summary",
    (string?)comparedContent?["leftProject"] == "Vergleich"
    && (string?)comparedContent?["rightProject"] == "Vergleich"
    && comparedContent?["added"]?.GetValue<int>() is not null
    && comparedContent?["removed"]?.GetValue<int>() is not null
    && comparedContent?["changed"]?.GetValue<int>() is > 0
    && comparedContent?["unchanged"]?.GetValue<int>() is not null
    && comparedContent?["total"]?.GetValue<int>() is > 0,
    compared.ToJsonString());
Check("project_compare is revision-aware",
    comparedContent?["revisionAware"]?.GetValue<bool>() == true
    && comparedContent?["leftRevision"]?["number"]?.GetValue<int>() == 1
    && comparedContent?["rightRevision"]?["number"]?.GetValue<int>() == 1
    && (string?)comparedContent?["leftRevision"]?["id"] == "REV-0001"
    && (string?)comparedContent?["revisionContent"] == "changed"
    && (string?)comparedContent?["revisionSignature"] == "unchanged",
    compared.ToJsonString());
Check("project_compare detects a segment medium-only change",
    comparedContent?["items"] is JsonArray compareItems && compareItems.Any(item =>
        (string?)item?["path"] == "Segment/I-CMP/A-CMP/L-CMP/S-CMP/MediumTypeRefId"),
    compared.ToJsonString());
Check("project_compare renders the revision report",
    (string?)comparedContent?["reportText"] is { } rendered
    && rendered.Contains("revision-content=changed", StringComparison.Ordinal)
    && rendered.Contains("left-revision=number:1", StringComparison.Ordinal)
    && rendered.Contains("right-revision=number:1", StringComparison.Ordinal),
    (string?)comparedContent?["reportText"]);
Check("project_compare leaves the source directory out",
    !compared.ToJsonString().Contains(compareDir, StringComparison.OrdinalIgnoreCase));
var comparedAgain = await Tool(108, "project_compare", compareArgs);
Check("project_compare output is stable for a fixed timestamp",
    comparedAgain["result"]?.ToJsonString() == compared["result"]?.ToJsonString());

var plainLeftPath = Path.Combine(compareDir, "plain-links.knxproj");
var plainRightPath = Path.Combine(compareDir, "plain-rechts.knxproj");
Knx.Knxproj.Save(CompareProject(), plainLeftPath);
var plainRight = Knx.Knxproj.Load(plainLeftPath);
plainRight.Installations.Single().Areas.Single().Lines.Single().Segments.Single().MediumTypeRefId = "MT-2";
Knx.Knxproj.Save(plainRight, plainRightPath);
var plainCompared = await Tool(109, "project_compare", new
{
    left_path = plainLeftPath,
    right_path = plainRightPath,
    generated_at = "2026-10-22T12:30:00+02:00",
});
var plainContent = plainCompared["result"]?["structuredContent"];
Check("project_compare without revisions keeps the old shape",
    plainContent?["revisionAware"]?.GetValue<bool>() == false
    && plainContent?["reportText"] is null
    && plainContent?["leftRevision"] is null
    && plainContent?["rightRevision"] is null
    && plainContent?["items"] is JsonArray plainItems && plainItems.Count > 0,
    plainCompared.ToJsonString());
foreach (var path in new[] { compareLeftPath, compareRightPath, plainLeftPath, plainRightPath })
    if (File.Exists(path)) File.Delete(path);
Directory.Delete(compareDir, recursive: true);

// ---------------------------------------------------------------- project search / replace

var searchedDevice = await Tool(70, "project_search", new { query = "Katalogaktor", scope = "devices" });
Check("project_search finds a device by name",
    searchedDevice["result"]?["structuredContent"]?["results"]?[0]?["kind"]?.GetValue<string>() == "device" &&
    searchedDevice["result"]?["structuredContent"]?["results"]?[0]?["field"]?.GetValue<string>() == "name" &&
    searchedDevice["result"]?["structuredContent"]?["total"]?.GetValue<int>() == 1,
    searchedDevice.ToJsonString());

var searchedAddress = await Tool(71, "project_search", new { query = "Wohnzimmer", scope = "group_addresses" });
Check("project_search finds a group address by name",
    searchedAddress["result"]?["structuredContent"]?["results"]?[0]?["kind"]?.GetValue<string>() == "group_address" &&
    searchedAddress["result"]?["structuredContent"]?["results"]?[0]?["path"]?.GetValue<string>() == "1/1/1 / Wohnzimmer Decke",
    searchedAddress.ToJsonString());

var searchedRegex = await Tool(72, "project_search",
    new { query = "^Switch", regex = true, scope = "communication_objects" });
Check("project_search supports regular expressions across text and function text",
    searchedRegex["result"]?["structuredContent"]?["total"]?.GetValue<int>() == 2,
    searchedRegex.ToJsonString());

var searchedCase = await Tool(73, "project_search", new { query = "katalogaktor", case_sensitive = true });
Check("project_search honours case_sensitive",
    searchedCase["result"]?["structuredContent"]?["total"]?.GetValue<int>() == 0, searchedCase.ToJsonString());

var searchedLimited = await Tool(74, "project_search", new { query = "Switch", limit = 1 });
Check("project_search limit caps results but keeps the total",
    searchedLimited["result"]?["structuredContent"]?["results"]?.AsArray().Count == 1 &&
    searchedLimited["result"]?["structuredContent"]?["total"]?.GetValue<int>() == 2 &&
    searchedLimited["result"]?["structuredContent"]?["truncated"]?.GetValue<bool>() == true,
    searchedLimited.ToJsonString());

var searchedScope = await Tool(75, "project_search", new { query = "Wohnzimmer", scope = "devices" });
Check("project_search scope excludes other kinds",
    searchedScope["result"]?["structuredContent"]?["total"]?.GetValue<int>() == 0, searchedScope.ToJsonString());

var invalidRegexSearch = await Tool(76, "project_search", new { query = "([", regex = true });
Check("project_search rejects an invalid regular expression",
    invalidRegexSearch["result"]?["isError"]?.GetValue<bool>() == true &&
    (invalidRegexSearch["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
        .Contains("invalid regular expression", StringComparison.Ordinal), invalidRegexSearch.ToJsonString());

var unknownScopeSearch = await Tool(77, "project_search", new { query = "x", scope = "catalogue" });
Check("project_search rejects an unknown scope",
    unknownScopeSearch["result"]?["isError"]?.GetValue<bool>() == true &&
    (unknownScopeSearch["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? "")
        .Contains("unknown scope", StringComparison.Ordinal), unknownScopeSearch.ToJsonString());

var beforePreview = Knx.Knxproj.ToXml(server.Project!).ToString();
var stepsBeforePreview = (await Tool(78, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>();
var replacePreview = await Tool(79, "project_replace_preview",
    new { query = "Wohnzimmer", replacement = "Salon", scope = "group_addresses" });
Check("project_replace_preview reports per-item before/after",
    replacePreview["result"]?["structuredContent"]?["itemCount"]?.GetValue<int>() == 1 &&
    replacePreview["result"]?["structuredContent"]?["fieldCount"]?.GetValue<int>() == 1 &&
    replacePreview["result"]?["structuredContent"]?["changes"]?[0]?["before"]?.GetValue<string>() == "Wohnzimmer Decke" &&
    replacePreview["result"]?["structuredContent"]?["changes"]?[0]?["after"]?.GetValue<string>() == "Salon Decke",
    replacePreview.ToJsonString());
Check("project_replace_preview does not mutate the project", Knx.Knxproj.ToXml(server.Project!).ToString() == beforePreview);
Check("project_replace_preview adds no undo entry",
    (await Tool(80, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() == stepsBeforePreview);

var replaceApplied = await Tool(81, "project_replace_apply",
    new { query = "Wohnzimmer", replacement = "Salon", scope = "group_addresses" });
Check("project_replace_apply renames and reports changed ids",
    replaceApplied["result"]?["structuredContent"]?["applied"]?.GetValue<bool>() == true &&
    replaceApplied["result"]?["structuredContent"]?["changedIds"]?.AsArray().Count == 1,
    replaceApplied.ToJsonString());
Check("project_replace_apply mutates the project",
    Knx.ProjectQuery.FindGroupAddress(server.Project!, Knx.GroupAddress.Parse("1/1/1"))?.Name == "Salon Decke");

var replaceUndo = await Tool(82, "undo");
Check("undo reverts a project_replace_apply", replaceUndo["result"]?["isError"] is null,
    replaceUndo.ToJsonString());
Check("undo restores the replaced name",
    Knx.ProjectQuery.FindGroupAddress(server.Project!, Knx.GroupAddress.Parse("1/1/1"))?.Name == "Wohnzimmer Decke");

var stepsAfterUndo = (await Tool(83, "project_info"))["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>();
var noMatchReplace = await Tool(84, "project_replace_apply",
    new { query = "gibt-es-nicht", replacement = "x", scope = "group_addresses" });
Check("project_replace_apply without matches is a no-op without undo entry",
    noMatchReplace["result"]?["structuredContent"]?["applied"]?.GetValue<bool>() == false &&
    noMatchReplace["result"]?["structuredContent"]?["fieldCount"]?.GetValue<int>() == 0 &&
    noMatchReplace["result"]?["structuredContent"]?["undo"]?["steps"]?.GetValue<int>() == stepsAfterUndo,
    noMatchReplace.ToJsonString());

// ---------------------------------------------------------------- summary
await MigrationExportChecks.Run(Check);
await DcaManifestToolChecks.Run(Check);
await JungKspToolChecks.Run(Check);
await MaskProfileToolChecks.Run(Check);
await ParameterPatchToolChecks.Run(Check);
await EnumPatchToolChecks.Run(Check);
await FloatPatchToolChecks.Run(Check);
await TimePatchToolChecks.Run(Check);
await ModulePatchToolChecks.Run(Check);
await ModuleEnumPatchToolChecks.Run(Check);
await DenseTimePatchToolChecks.Run(Check);
await Native20ProjectToolChecks.Run(Check);
await Native22ProjectToolChecks.Run(Check);
await ResourcePatchToolChecks.Run(Check);
await PropertyPatchToolChecks.Run(Check);
await ImageCompositionToolChecks.Run(Check);
await RootImageBuildToolChecks.Run(Check);
await RootResolvedImageBuildToolChecks.Run(Check);
await RootPropertyBuildToolChecks.Run(Check);
var nativeImageChecks = 0;
var nativeImageFailures = 0;
await NativeImageBuildToolChecks.Run((name, ok, detail) =>
{
    nativeImageChecks++;
    if (!ok) nativeImageFailures++;
    Check(name, ok, detail);
});
Console.WriteLine($"NativeImageBuildMcp: {nativeImageChecks} checks, {nativeImageFailures} failures");
var nativeScalarChecks = 0;
var nativeScalarFailures = 0;
await NativeImageScalarToolChecks.Run((name, ok, detail) =>
{
    nativeScalarChecks++;
    if (!ok) nativeScalarFailures++;
    Check(name, ok, detail);
});
Console.WriteLine($"NativeImageScalarMcp: {nativeScalarChecks} checks, {nativeScalarFailures} failures");
var nativeFoldChecks = 0;
var nativeFoldFailures = 0;
await NativeImageFoldToolChecks.Run((name, ok, detail) =>
{
    nativeFoldChecks++;
    if (!ok) nativeFoldFailures++;
    Check(name, ok, detail);
});
Console.WriteLine($"NativeImageFoldMcp: {nativeFoldChecks} checks, {nativeFoldFailures} failures");
await ApplicationBuildToolChecks.Run(Check);
await ResolvedProcedureToolChecks.Run(Check);
await GeneratedTablesToolChecks.Run(Check);
await MassLinkToolChecks.Run((name, ok) => Check(name, ok));
await TopologyTemplateToolChecks.Run((name, ok) => Check(name, ok));

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("ALL TESTS PASSED");
    return 0;
}

Console.WriteLine($"{failures.Count} TEST(S) FAILED");
foreach (var failure in failures) Console.WriteLine("  - " + failure);
return 1;

async Task<JsonNode> Send(JsonObject request)
{
    var response = await server.HandleAsync(request.ToJsonString());
    return JsonNode.Parse(response ?? "null") ?? new JsonObject();
}

async Task<JsonNode> Call(int id, string method, JsonNode? parameters = null)
{
    var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
    if (parameters is not null) request["params"] = parameters;
    return await Send(request);
}

async Task<JsonNode> Tool(int id, string name, object? arguments = null)
{
    var parameters = new JsonObject { ["name"] = name };
    if (arguments is not null) parameters["arguments"] = JsonSerializer.SerializeToNode(arguments);
    return await Call(id, "tools/call", parameters);
}

static string ReadZipEntry(ZipArchive zip, string name)
{
    var entry = zip.Entries.Single(e => e.FullName.EndsWith("/" + name, StringComparison.Ordinal));
    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
    return reader.ReadToEnd();
}

static IEnumerable<string> ZipEntryTexts(string path)
{
    using var zip = ZipFile.OpenRead(path);
    foreach (var entry in zip.Entries)
    {
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        yield return reader.ReadToEnd();
    }
}

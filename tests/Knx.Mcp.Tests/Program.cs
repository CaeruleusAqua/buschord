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
Check("tools/list returns 49 tools", tools.Count == 49, tools.Count.ToString());
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
        .Contains("individual address", StringComparison.OrdinalIgnoreCase), busConnect.ToJsonString());

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

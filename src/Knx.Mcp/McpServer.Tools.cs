using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Knx.Bus;
using Knx.Catalog;
using Knx.ProjectCompare;
using Knx.Reports;
using Knx.Workbench;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    // ---------------------------------------------------------------- catalog

    private List<McpTool> BuildTools() =>
    [
        new("project_open",
            "Open an existing ETS project archive (*.knxproj) and make it the working project. " +
            "Replaces the current project and resets undo/redo history.",
            Schema(StrProp("path", "Path to the .knxproj file.", true)),
            Sync(ProjectOpen)),
        new("project_new",
            "Create a new empty project in memory with the given name, group address style, area count and " +
            "lines per area. save_path only remembers where project_save should write later.",
            Schema(
                StrProp("name", "Project name.", true),
                EnumProp("style", "Group address style.", ["three_level", "two_level", "free"]),
                IntProp("areas", "Number of areas to create (default 1)."),
                IntProp("lines", "Number of lines per area (default 1)."),
                StrProp("save_path", "Target .knxproj path used by project_save.")),
            Sync(ProjectNew)),
        new("project_save",
            "Write the in-memory project to disk as a .knxproj archive. Uses the path from project_open or " +
            "project_new unless path is given.",
            Schema(StrProp("path", "Target .knxproj path; overrides the remembered path.")),
            Sync(ProjectSave)),
        new("project_info",
            "Summarize the open project: ids, path, topology/device/group address counts and undo state.",
            Schema(),
            Sync(ProjectInfo)),
        new("project_dashboard",
            "Aggregated project figures: link and datapoint coverage, devices per line, top datapoint types " +
            "and group addresses per main range.",
            Schema(),
            Sync(ProjectDashboard)),
        new("project_search",
            "Search the whole project for plain text or a regular expression. Covers devices (name, address, " +
            "comment, product reference), group addresses (name, address, datapoint type, comment), topology " +
            "nodes, communication objects (text, function text, number) and buildings when present. Results " +
            "carry kind, id, display path, matched field and snippet, ordered deterministically.",
            Schema(
                StrProp("query", "Text or regular expression to find.", true),
                EnumProp("scope", "Restrict the search to one area of the project.",
                    ["all", "devices", "group_addresses", "topology", "communication_objects", "buildings"]),
                BoolProp("regex", "Treat query as a .NET regular expression (default false)."),
                BoolProp("case_sensitive", "Match case exactly (default false)."),
                IntProp("limit", "Maximum hits returned (1..1000, default 200).")),
            Sync(ProjectSearchTool)),
        new("project_replace_preview",
            "Preview a project-wide rename: returns the matched items and every before/after field value " +
            "without changing the project. Only names, comments and object texts are replaceable; addresses, " +
            "datapoint types and references stay untouched.",
            Schema(
                StrProp("query", "Text or regular expression to replace.", true),
                StrProp("replacement", "Literal replacement text; may be empty.", true),
                EnumProp("scope", "Restrict the replacement to one area of the project.",
                    ["all", "devices", "group_addresses", "topology", "communication_objects", "buildings"]),
                BoolProp("regex", "Treat query as a .NET regular expression (default false)."),
                BoolProp("case_sensitive", "Match case exactly (default false).")),
            Sync(ProjectReplacePreviewTool)),
        new("project_replace_apply",
            "Apply a project-wide rename and push one undo snapshot. Recomputes and validates the preview " +
            "before the first mutation, so a stale or invalid request changes nothing. Returns the changed ids; " +
            "a request without matches is a no-op without undo history.",
            Schema(
                StrProp("query", "Text or regular expression to replace.", true),
                StrProp("replacement", "Literal replacement text; may be empty.", true),
                EnumProp("scope", "Restrict the replacement to one area of the project.",
                    ["all", "devices", "group_addresses", "topology", "communication_objects", "buildings"]),
                BoolProp("regex", "Treat query as a .NET regular expression (default false)."),
                BoolProp("case_sensitive", "Match case exactly (default false).")),
            Sync(ProjectReplaceApplyTool)),

        new("group_addresses_list",
            "List group addresses with id, address, name, datapoint type, description and comment. " +
            "filter matches address, name, description or datapoint type (case-insensitive).",
            Schema(StrProp("filter", "Optional substring filter.")),
            Sync(GroupAddressesList)),
        new("group_address_add",
            "Add one group address. Missing main/middle ranges are created; fails when the address exists.",
            Schema(
                StrProp("address", "Group address, e.g. 1/1/1.", true),
                StrProp("name", "Group address name.", true),
                StrProp("datapoint_type", "Datapoint type as 1.001, DPT-1-1 or DPST-1-1.")),
            Sync(GroupAddressAdd)),
        new("group_address_update",
            "Change name, datapoint type, description and/or comment of one group address. Only the given " +
            "fields change; an explicit null clears the field.",
            Schema(
                StrProp("address", "Group address, e.g. 1/1/1.", true),
                StrProp("name", "New name."),
                StrProp("datapoint_type", "New datapoint type."),
                StrProp("description", "New description."),
                StrProp("comment", "New comment.")),
            Sync(GroupAddressUpdate)),
        new("group_address_delete",
            "Delete one group address and every communication-object link that points at it.",
            Schema(StrProp("address", "Group address, e.g. 1/1/1.", true)),
            Sync(GroupAddressDelete)),
        new("group_address_import_csv",
            "Import a group address list from CSV text (content) or a file (path). Accepts ETS exports and " +
            "plain 'address;name;datapointType' lines; duplicates are skipped and reported.",
            Schema(
                StrProp("content", "CSV text to import."),
                StrProp("path", "CSV file to import when content is not given.")),
            Sync(GroupAddressImport)),

        new("devices_list",
            "List devices with individual address, name, product reference, container and link count. " +
            "line filters by area.line (for example 1.1).",
            Schema(StrProp("line", "Optional area.line filter, e.g. 1.1.")),
            Sync(DevicesList)),
        new("device_update",
            "Change name, description and/or comment of one device. Only the given fields change; an explicit " +
            "null clears the field.",
            Schema(
                StrProp("address", "Individual address, e.g. 1.1.5.", true),
                StrProp("name", "New name."),
                StrProp("description", "New description."),
                StrProp("comment", "New comment.")),
            Sync(DeviceUpdate)),
        new("device_add",
            "Create a device on a line (line_area = area number, line_address = line number). The next free " +
            "individual address on that line is assigned.",
            Schema(
                IntProp("line_area", "Area number 0..15.", true),
                IntProp("line_address", "Line number 0..15.", true),
                StrProp("name", "Device name.", true),
                StrProp("product_ref", "Optional product reference id.")),
            Sync(DeviceAdd)),
        new("device_delete",
            "Delete the device with the given individual address.",
            Schema(StrProp("address", "Individual address, e.g. 1.1.5.", true)),
            Sync(DeviceDelete)),
        new("device_move",
            "Move a device to another area/line, keeping its individual address. Lines with segments receive " +
            "the device in the first segment.",
            Schema(
                StrProp("address", "Individual address, e.g. 1.1.5.", true),
                IntProp("target_area", "Target area number 0..15.", true),
                IntProp("target_line", "Target line number 0..15.", true)),
            Sync(DeviceMove)),

        new("communication_objects_list",
            "List communication object reference ids, text, number and existing group address links for one device. Works offline.",
            Schema(StrProp("device_address", "Individual address, e.g. 1.1.5.", true)),
            Sync(CommunicationObjectsList)),
        new("group_link_add",
            "Manually link a communication object reference to an existing group address. Idempotent, offline and undoable; no DPT compatibility is inferred.",
            GroupLinkSchema(),
            Sync(args => GroupLinkChange(args, true))),
        new("group_link_remove",
            "Remove a communication object reference's link to an existing group address. Idempotent, offline and undoable.",
            GroupLinkSchema(),
            Sync(args => GroupLinkChange(args, false))),

        new("project_check",
            "Run the structural ETS project check (duplicate addresses, dangling links, missing datapoint " +
            "types, topology limits) and return findings grouped by severity.",
            Schema(),
            Sync(ProjectCheckTool)),
        new("smart_link_suggest",
            "Suggest group addresses for unlinked communication objects, scored by name overlap and datapoint " +
            "type match (minimum score 40, up to 3 suggestions per object).",
            Schema(),
            Sync(SmartLinkSuggest)),
        new("smart_link_apply",
            "Apply the best suggestion (same scoring as smart_link_suggest) to every unlinked communication " +
            "object of the project.",
            Schema(),
            Sync(SmartLinkApply)),
        new("product_replace",
            "Repoint all devices of one product or application-program reference to another. dry_run reports " +
            "what would change without modifying the project.",
            Schema(
                StrProp("old_ref", "Product or hardware2program reference to replace.", true),
                StrProp("new_ref", "Replacement reference id.", true),
                StrProp("new_program", "Optional new hardware2program reference id."),
                BoolProp("dry_run", "Report matches without changing the project.")),
            Sync(ProductReplace)),

        new("export_csv",
            "Export group addresses, devices or topology as semicolon CSV and write it to path.",
            Schema(
                EnumProp("kind", "What to export.", ["group_addresses", "devices", "topology"], true),
                StrProp("path", "Output file path.", true)),
            Sync(ExportCsv)),
        new("export_semantic",
            "Export the project as semantic linked data (KNX ontology) and write it to path.",
            Schema(
                EnumProp("format", "Serialization format.", ["turtle", "jsonld"], true),
                StrProp("path", "Output file path.", true)),
            Sync(ExportSemantic)),
        new("export_labels",
            "Write printable labels for group addresses or devices: HTML sheet, CSV, or merge (group address " +
            "and device labels on one HTML sheet).",
            Schema(
                EnumProp("kind", "What to label.", ["group_addresses", "devices"], true),
                EnumProp("format", "Output format.", ["html", "csv", "merge"], true),
                StrProp("path", "Output file path.", true)),
            Sync(ExportLabels)),
        new("report_export",
            "Write printable, self-contained HTML project documentation: title page with counts, topology and " +
            "device list, group address table with datapoint types and links, communication objects, link matrix " +
            "summary and project check findings. Inline CSS only, no external assets or scripts.",
            Schema(
                StrProp("path", "Output .html file path.", true),
                StrProp("title", "Report title; defaults to the project name."),
                BoolProp("include_title_page", "Include the title page with counts (default true)."),
                BoolProp("include_topology", "Include the topology and device list (default true)."),
                BoolProp("include_group_addresses", "Include the group address table (default true)."),
                BoolProp("include_com_objects", "Include communication objects and their links (default true)."),
                BoolProp("include_link_matrix", "Include the link matrix summary per main range (default true)."),
                BoolProp("include_findings", "Include the project check findings (default true).")),
            Sync(ReportExport)),
        new("acceptance_protocol_export",
            "Write a printable KNX acceptance/test protocol (HTML or CSV) for the open project: project facts " +
            "with document version, document hash and master-data signature status, topology and device list, " +
            "group addresses with datapoint types, function matrix, per-device test rows with on-site check boxes " +
            "and fill-in measured-value/remark columns, open items with a blank defect list and a sign-off block. " +
            "Read-only: no bus access, no project change, no download. The check columns start open (not tested) " +
            "and the download status stays 'no download recorded' unless a device programming state file " +
            "(states_path, workbench export) proves a demo or real load.",
            Schema(
                StrProp("path", "Output .html or .csv file path.", true),
                EnumProp("format", "Document format.", ["html", "csv"]),
                EnumProp("language", "Document language.", ["de", "en"]),
                StrProp("title", "Protocol title; defaults to the project name."),
                StrProp("states_path", "Optional device programming state JSON (workbench export) used for the download status."),
                StrProp("technician", "Technician name printed in the sign-off block."),
                StrProp("building_owner", "Building owner name printed in the sign-off block."),
                IntProp("defect_lines", "Blank defect-list rows (0..50, default 5).")),
            Sync(AcceptanceProtocolExport)),
        new("project_compare",
            "Compare two .knxproj files and report added, removed and changed installations, devices and " +
            "group addresses.",
            Schema(
                StrProp("left_path", "Left/baseline .knxproj path.", true),
                StrProp("right_path", "Right/updated .knxproj path.", true)),
            Sync(ProjectCompareTool)),

        new("undo",
            "Revert the last mutating tool call (project shape, not disk state).",
            Schema(),
            Sync(UndoTool)),
        new("redo",
            "Re-apply the change that was last undone.",
            Schema(),
            Sync(RedoTool)),

        new("bus_discover",
            "Search the KNXnet/IP network for gateways and interfaces. Searches every local interface " +
            "with a real HPAI. Set host to probe one address directly when broadcasts are filtered.",
            Schema(
                IntProp("timeout_ms", "Search duration in ms (100..10000, default 1500)."),
                StrProp("host", "IP address of one gateway to query directly instead of broadcasting.")),
            BusDiscover),
        new("bus_connect",
            "Open a KNXnet/IP connection. mode=tunnel (default) searches for gateways and connects the first " +
            "one matching 'device' (name fragment or individual address); mode=routing joins 224.0.23.12 on " +
            "the KNX segment interface (bind routing port 3671). Required before bus_read.",
            Schema(
                EnumProp("mode", "Connection type.", ["tunnel", "routing"]),
                StrProp("device", "Gateway name fragment or individual address; defaults to the first gateway found."),
                StrProp("own_address", "Individual address used by the tunnel, default 1.1.250."),
                StrProp("interface", "Local IPv4 address of the NIC on the KNX segment (routing mode only)."),
                IntProp("timeout_ms", "Gateway search duration in ms (100..10000, default 1500).")),
            BusConnect),
        new("bus_disconnect",
            "Close the KNXnet/IP connection and release the socket.",
            Schema(),
            BusDisconnect),
        new("bus_read",
            "Send a group value read and wait up to timeout_ms for a GroupValueResponse from a real device " +
            "for the requested group address. Our own L_Data.con echo is ignored. Requires a connected tunnel.",
            Schema(
                StrProp("group_address", "Group address, e.g. 1/1/1.", true),
                StrProp("datapoint_type", "Datapoint type for decoding; defaults to the project type."),
                IntProp("timeout_ms", "Response timeout in ms (100..10000, default 3000).")),
            BusRead),
        new("bus_write",
            "Refused: this build is read-only. Group value writes are disabled by policy until the write " +
            "frame encoding and the safe programming sequence are verified on real hardware.",
            Schema(
                StrProp("group_address", "Group address, e.g. 1/1/1.", true),
                StrProp("value", "Value in display form (On/Off, 50%, 21.5) or hex payload.", true),
                StrProp("datapoint_type", "Datapoint type overriding the project type.")),
            BusWrite),
        new("bus_status",
            "Report bus connection state, connected device and telegram recorder status.",
            Schema(),
            Sync(BusStatus)),
        new("bus_scan",
            "Read-only bus or line scan: broadcast IndividualAddressRead, collect the answering individual " +
            "addresses and read each device descriptor (mask version). Opens its own short-lived tunnelling " +
            "connection, so bus_connect is not required, and writes nothing to the bus. target restricts the " +
            "scan to one line 'area.line'. Compared with the open project it reports duplicate, missing and " +
            "unexpected addresses.",
            Schema(
                StrProp("target", "Optional line filter 'area.line', e.g. 1.1; omit to scan the whole bus."),
                StrProp("gateway", "Gateway name fragment or IP; defaults to the first gateway found."),
                IntProp("timeout_ms", "Gateway search duration in ms (100..10000, default 1500)."),
                IntProp("response_timeout_ms", "Collect and probe timeout in ms (100..5000, default 1000).")),
            BusScanTool),
        new("bus_device_info",
            "Read the device descriptor of one individual address (mask version and descriptor type) over a " +
            "short-lived read-only tunnel. Sends only a well-formed DeviceDescriptorRead (descriptor type 0).",
            Schema(
                StrProp("address", "Individual address, e.g. 1.1.5.", true),
                StrProp("gateway", "Gateway name fragment or IP; defaults to the first gateway found."),
                IntProp("timeout_ms", "Gateway search duration in ms (100..10000, default 1500)."),
                IntProp("response_timeout_ms", "Response timeout in ms (100..5000, default 1000).")),
            BusDeviceInfoTool),
        new("bus_statistics",
            "Windowed telegram statistics of the bus session: telegram, ack and error counters, rate per " +
            "second and minute, telegrams per source and errors per kind. Counts from the moment a bus " +
            "connection delivered telegrams; works offline and returns zeros without a connection.",
            Schema(IntProp("window_ms", "Trailing window in ms (100..3600000, default 60000).")),
            Sync(BusStatisticsTool)),
        new("download_plan",
            "Read-only preflight and dry run for a future device download: reports the gate checks " +
            "(descriptor/mask, programming mode, confirmed address/line, program match, parameter/link " +
            "completeness, confirmations), the computed image chunks, the printable dry run and the " +
            "attempt-log status. Sends no programming frame and never downloads; with 'gateway' it reads " +
            "the descriptor over a short-lived read-only tunnel (like bus_device_info). " +
            "Real downloads stay disabled (writesEnabled false).",
            Schema(
                StrProp("device_address", "Individual address of a project device, e.g. 1.1.5.", true),
                StrProp("gateway", "Optional gateway to read the descriptor from; omit for a purely offline plan."),
                IntProp("timeout_ms", "Gateway search duration in ms (100..10000, default 1500)."),
                IntProp("response_timeout_ms", "Descriptor response timeout in ms (100..5000, default 1000).")),
            DownloadPlanTool),
        new("catalog_library",
            "List the product catalogues (.knxprod) in the local library with manufacturer, product " +
            "count and any files that failed to parse.",
            Schema(),
            Sync(CatalogLibrary)),
        new("catalog_import",
            "Copy a .knxprod file into the local catalogue library and return its products summary.",
            Schema(StrProp("path", "Absolute path of the .knxprod file.", true)),
            Sync(CatalogImport)),
        new("catalog_products",
            "List products of a library catalogue (first catalogue when 'catalog' is omitted). " +
            "'filter' matches product name or order number.",
            Schema(
                StrProp("catalog", "Catalogue name, manufacturer name or manufacturer id fragment."),
                StrProp("filter", "Product name or order number fragment.")),
            Sync(CatalogProducts)),
        new("catalog_import_device",
            "Insert a catalogue product as a device into a project line, creating one communication " +
            "object reference per com object. Address defaults to the next free one on that line.",
            Schema(
                StrProp("product_id", "Product id from catalog_products, e.g. M-00FA_H-1_P-1.", true),
                StrProp("catalog", "Catalogue selector when the library holds more than one."),
                IntProp("line_area", "Target area address (0..15).", true),
                IntProp("line_address", "Target line address (0..15).", true),
                StrProp("name", "Device name, default: product name."),
                StrProp("address", "Individual address to use, e.g. 1.1.5; default: next free.")),
            Sync(CatalogImportDevice)),
        new("recorder_start",
            "Start a new telegram recording; clears previously recorded telegrams.",
            Schema(),
            Sync(RecorderStart)),
        new("recorder_stop",
            "Stop recording and return the captured telegrams (first 200, with a truncation flag).",
            Schema(),
            Sync(RecorderStop)),
        new("recorder_save",
            "Write the current recording to path as XML, CSV or JSON Lines.",
            Schema(
                StrProp("path", "Output file path.", true),
                EnumProp("format", "File format.", ["xml", "csv", "jsonl"], true)),
            Sync(RecorderSave)),
    ];

    // ---------------------------------------------------------------- project

    private object ProjectOpen(JsonElement args)
    {
        var path = Path.GetFullPath(Required(args, "path"));
        if (!File.Exists(path)) throw new ToolException($"project file not found: {path}");
        var project = Knxproj.Load(path);
        _project = project;
        _projectPath = path;
        _undo.Reset(project);
        return new { opened = true, project = Summary(project), undo = History() };
    }

    private object ProjectNew(JsonElement args)
    {
        var options = new NewProjectOptions
        {
            Name = Required(args, "name"),
            GroupAddressStyle = ParseStyle(Str(args, "style")),
            Areas = Math.Max(1, Int(args, "areas") ?? 1),
            LinesPerArea = Math.Max(1, Int(args, "lines") ?? 1),
        };
        var project = ProjectWizard.Create(options);
        _project = project;
        _projectPath = Str(args, "save_path") is { Length: > 0 } savePath ? Path.GetFullPath(savePath) : null;
        _undo.Reset(project);
        return new { created = true, project = Summary(project), undo = History() };
    }

    private object ProjectSave(JsonElement args)
    {
        var project = Current;
        if (Str(args, "path") is { Length: > 0 } path) _projectPath = Path.GetFullPath(path);
        var target = _projectPath
                     ?? throw new ToolException("no save path: pass 'path' or set save_path when creating the project");
        EnsureDirectory(target);
        Knxproj.Save(project, target);
        return new
        {
            saved = true,
            path = target,
            bytes = new FileInfo(target).Length,
            project = project.Name,
            groupAddresses = ProjectQuery.GroupAddresses(project).Count(),
            devices = ProjectQuery.DeviceList(project).Count(),
        };
    }

    private object ProjectInfo(JsonElement args)
        => new { project = Summary(Current), undo = History() };

    private object ProjectDashboard(JsonElement args)
    {
        var stats = Dashboard.Build(Current);
        return new
        {
            projectName = stats.ProjectName,
            installations = stats.Installations,
            areas = stats.Areas,
            lines = stats.Lines,
            devices = stats.Devices,
            comObjects = stats.ComObjects,
            linkedComObjects = stats.LinkedComObjects,
            groupAddresses = stats.GroupAddresses,
            groupAddressesWithDpt = stats.GroupAddressesWithDpt,
            linkedGroupAddresses = stats.LinkedGroupAddresses,
            dptTypes = stats.DptTypes,
            linkCoverage = Math.Round(stats.LinkCoverage, 1),
            dptCoverage = Math.Round(stats.DptCoverage, 1),
            devicesPerLine = stats.DevicesPerLine.Select(n => new { name = n.Name, count = n.Count }).ToList(),
            topDatapointTypes = stats.TopDatapointTypes.Select(n => new { name = n.Name, count = n.Count }).ToList(),
            groupAddressesPerMainRange = stats.GroupAddressesPerMainRange
                .Select(n => new { name = n.Name, count = n.Count }).ToList(),
        };
    }

    // ---------------------------------------------------------------- group addresses

    private object GroupAddressesList(JsonElement args)
    {
        var project = Current;
        var filter = Str(args, "filter");
        var all = ProjectQuery.GroupAddresses(project).OrderBy(a => a.Address.Raw).ToList();
        var matches = string.IsNullOrWhiteSpace(filter)
            ? all
            : all.Where(a => Matches(a, filter)).ToList();
        return new { count = matches.Count, total = all.Count, groupAddresses = matches.Select(Address).ToList() };
    }

    private object GroupAddressAdd(JsonElement args)
    {
        var project = Current;
        var address = ParseAddress(Required(args, "address"));
        if (ProjectQuery.FindGroupAddress(project, address) is not null)
            throw new ToolException($"group address {address} already exists");

        // Reuse the wizard import so range creation and id numbering stay identical to the CSV import.
        var result = ProjectWizard.ImportGroupAddresses(project, FirstInstallation(project),
            CsvLine(address.ToString(), Required(args, "name"), Str(args, "datapoint_type")));
        if (result.Imported != 1)
            throw new ToolException("could not add group address: " +
                                    string.Join("; ", result.Errors.DefaultIfEmpty("unknown import error")));

        _undo.Push(project);
        return new { added = true, groupAddress = Address(ProjectQuery.FindGroupAddress(project, address)!) };
    }

    private object GroupAddressUpdate(JsonElement args)
    {
        var project = Current;
        var entry = FindAddressOrThrow(project, Required(args, "address"));
        var changed = false;

        if (TryStr(args, "name", out var name)) { entry.Name = name ?? ""; changed = true; }
        if (TryStr(args, "datapoint_type", out var datapointType))
        {
            entry.DatapointType = NormaliseDpt(datapointType);
            changed = true;
        }
        if (TryStr(args, "description", out var description)) { entry.Description = description; changed = true; }
        if (TryStr(args, "comment", out var comment)) { entry.Comment = comment; changed = true; }

        if (!changed) throw new ToolException("nothing to update: pass name, datapoint_type, description or comment");
        _undo.Push(project);
        return new { updated = true, groupAddress = Address(entry) };
    }

    private object GroupAddressDelete(JsonElement args)
    {
        var project = Current;
        var entry = FindAddressOrThrow(project, Required(args, "address"));
        var range = ProjectQuery.FindRangeOf(project, entry)
                    ?? throw new ToolException($"group address {entry.Address} is not attached to a group range");
        range.Addresses.Remove(entry);

        var linksRemoved = 0;
        foreach (var (_, comObject) in ProjectQuery.ComObjectRefs(project))
            linksRemoved += comObject.Links.RemoveAll(id => id == entry.Id);

        _undo.Push(project);
        return new { deleted = Address(entry), linksRemoved };
    }

    private object GroupAddressImport(JsonElement args)
    {
        var project = Current;
        string? content = Str(args, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            var path = Str(args, "path") is { Length: > 0 } file
                ? Path.GetFullPath(file)
                : throw new ToolException("pass 'content' or 'path' with the CSV data");
            if (!File.Exists(path)) throw new ToolException($"CSV file not found: {path}");
            content = File.ReadAllText(path);
        }

        var result = ProjectWizard.ImportGroupAddresses(project, FirstInstallation(project), content);
        if (result.Imported > 0) _undo.Push(project);
        return new { imported = result.Imported, skipped = result.Skipped, errors = result.Errors };
    }

    // ---------------------------------------------------------------- devices

    private object DevicesList(JsonElement args)
    {
        var project = Current;
        (byte Area, byte Line)? filter = Str(args, "line") is { Length: > 0 } line ? ParseLine(line) : null;

        var devices = ProjectQuery.Devices(project)
            .Where(d => filter is null || (d.Area.Address == filter.Value.Area && d.Line?.Address == filter.Value.Line))
            .OrderBy(d => d.Device.Address.Raw)
            .Select(d => new
            {
                address = d.Device.Address.ToString(),
                name = d.Device.Name,
                productRefId = d.Device.ProductRefId,
                description = d.Device.Description,
                comment = d.Device.Comment,
                area = d.Area.Address,
                line = d.Line?.Address,
                segment = d.Segment?.Address,
                comObjects = d.Device.ComObjectRefs.Count,
                links = d.Device.ComObjectRefs.Sum(r => r.Links.Count),
            })
            .ToList();

        return new { count = devices.Count, devices };
    }

    private object DeviceUpdate(JsonElement args)
    {
        var project = Current;
        var device = FindDeviceOrThrow(project, Required(args, "address"));
        var changed = false;

        if (TryStr(args, "name", out var name)) { device.Name = name; changed = true; }
        if (TryStr(args, "description", out var description)) { device.Description = description; changed = true; }
        if (TryStr(args, "comment", out var comment)) { device.Comment = comment; changed = true; }

        if (!changed) throw new ToolException("nothing to update: pass name, description or comment");
        _undo.Push(project);
        return new { updated = true, device = Device(device) };
    }

    private object DeviceAdd(JsonElement args)
    {
        var project = Current;
        var areaAddress = RequiredByte(args, "line_area");
        var lineAddress = RequiredByte(args, "line_address");
        var area = project.Installations.SelectMany(i => i.Areas).FirstOrDefault(a => a.Address == areaAddress)
                   ?? throw new ToolException($"area {areaAddress} not found");
        var line = area.Lines.FirstOrDefault(l => l.Address == lineAddress)
                   ?? throw new ToolException($"line {areaAddress}.{lineAddress} not found");

        var address = ProjectQuery.NextFreeDeviceAddress(project, areaAddress, lineAddress);
        var device = new DeviceInstance
        {
            Id = address.ToString(),
            Address = address,
            Name = Required(args, "name"),
            ProductRefId = Str(args, "product_ref") ?? "",
        };
        ProjectQuery.AddToLine(line, device);

        _undo.Push(project);
        return new { added = true, device = Device(device), line = $"{areaAddress}.{lineAddress}" };
    }

    private object DeviceDelete(JsonElement args)
    {
        var project = Current;
        var located = LocateDeviceOrThrow(project, Required(args, "address"));
        var deleted = new
        {
            address = located.Device.Address.ToString(),
            name = located.Device.Name,
            area = located.Area.Address,
            line = located.Line?.Address,
            comObjects = located.Device.ComObjectRefs.Count,
        };
        ProjectQuery.RemoveDevice(project, located.Device);
        _undo.Push(project);
        return new { deleted };
    }

    private object DeviceMove(JsonElement args)
    {
        var project = Current;
        var located = LocateDeviceOrThrow(project, Required(args, "address"));
        var targetAreaAddress = RequiredByte(args, "target_area");
        var targetLineAddress = RequiredByte(args, "target_line");
        var targetArea = project.Installations.SelectMany(i => i.Areas)
                             .FirstOrDefault(a => a.Address == targetAreaAddress)
                         ?? throw new ToolException($"area {targetAreaAddress} not found");
        var targetLine = targetArea.Lines.FirstOrDefault(l => l.Address == targetLineAddress)
                         ?? throw new ToolException($"line {targetAreaAddress}.{targetLineAddress} not found");

        var previous = new { area = located.Area.Address, line = located.Line?.Address };
        ProjectQuery.RemoveDevice(project, located.Device);
        ProjectQuery.AddToLine(targetLine, located.Device);

        _undo.Push(project);
        return new
        {
            moved = true,
            address = located.Device.Address.ToString(),
            from = previous,
            to = new { area = targetAreaAddress, line = targetLineAddress },
        };
    }

    // ---------------------------------------------------------------- analysis

    private object ProjectCheckTool(JsonElement args)
    {
        var findings = ProjectCheck.Run(Current);
        return new
        {
            errors = ProjectCheck.Count(findings, CheckSeverity.Error),
            warnings = ProjectCheck.Count(findings, CheckSeverity.Warning),
            infos = ProjectCheck.Count(findings, CheckSeverity.Info),
            total = findings.Count,
            findings = findings.Select(f => new
            {
                severity = f.Severity.ToString().ToLowerInvariant(),
                code = f.Code,
                path = f.Path,
                message = f.Message,
            }).ToList(),
        };
    }

    private object SmartLinkSuggest(JsonElement args)
    {
        var suggestions = SmartLinking.Suggest(Current);
        return new { count = suggestions.Count, suggestions = suggestions.Select(Suggestion).ToList() };
    }

    private object SmartLinkApply(JsonElement args)
    {
        var project = Current;
        // One link per unlinked com object: the highest scoring suggestion only.
        var best = SmartLinking.Suggest(project)
            .GroupBy(s => s.ComObject)
            .Select(group => group.OrderByDescending(s => s.Score).First())
            .ToList();
        var applied = SmartLinking.Apply(best);
        if (applied > 0) _undo.Push(project);
        return new { applied, suggestions = best.Select(Suggestion).ToList() };
    }

    private object ProductReplace(JsonElement args)
    {
        var project = Current;
        var oldRef = Required(args, "old_ref");
        var newRef = Required(args, "new_ref");
        var newProgram = Str(args, "new_program");
        var dryRun = Bool(args, "dry_run");

        var results = ProductReplacer.Replace(project, oldRef, newRef, newProgram, dryRun);
        if (!dryRun && results.Count > 0) _undo.Push(project);

        return new
        {
            dryRun,
            matched = results.Count,
            devices = results.Select(r => new
            {
                address = r.Device.Address.ToString(),
                name = r.Device.Name,
                oldRefId = r.OldRefId,
                newRefId = r.NewRefId,
                keptLinks = r.KeptLinks,
            }).ToList(),
        };
    }

    // ---------------------------------------------------------------- export

    private object ExportCsv(JsonElement args)
    {
        var project = Current;
        var kind = Required(args, "kind").ToLowerInvariant();
        var path = WritePath(Required(args, "path"));
        var content = kind switch
        {
            "group_addresses" or "groupaddresses" => ProjectCsvExporter.GroupAddresses(project),
            "devices" => ProjectCsvExporter.Devices(project),
            "topology" => ProjectCsvExporter.Topology(project),
            _ => throw new ToolException($"unknown export kind '{kind}' (expected group_addresses, devices or topology)"),
        };
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return new { path, kind, bytes = new FileInfo(path).Length, lines = CountLines(content) };
    }

    private object ExportSemantic(JsonElement args)
    {
        var project = Current;
        var requested = Required(args, "format").ToLowerInvariant();
        var path = WritePath(Required(args, "path"));
        var (format, content) = requested switch
        {
            "turtle" or "ttl" => ("turtle", SemanticExport.ToTurtle(project)),
            "jsonld" or "json-ld" => ("jsonld", SemanticExport.ToJsonLd(project)),
            _ => throw new ToolException($"unknown semantic format '{requested}' (expected turtle or jsonld)"),
        };
        File.WriteAllText(path, content, new UTF8Encoding(true));
        return new { path, format, bytes = new FileInfo(path).Length };
    }

    private object ExportLabels(JsonElement args)
    {
        var project = Current;
        var kind = Required(args, "kind").ToLowerInvariant();
        var requested = Required(args, "format").ToLowerInvariant();
        var path = WritePath(Required(args, "path"));
        var options = new LabelOptions { SheetTitle = project.Name };
        var merge = requested == "merge";

        IReadOnlyList<Label> labels = merge
            ? LabelCreator.GroupAddressLabels(project, options)
                .Concat(LabelCreator.DeviceLabels(project, options)).ToList()
            : kind switch
            {
                "group_addresses" or "groupaddresses" => LabelCreator.GroupAddressLabels(project, options),
                "devices" => LabelCreator.DeviceLabels(project, options),
                _ => throw new ToolException($"unknown label kind '{kind}' (expected group_addresses or devices)"),
            };

        var format = merge ? "html" : requested;
        var content = format switch
        {
            "html" => LabelCreator.ToHtml(labels, options),
            "csv" => LabelCreator.ToCsv(labels),
            _ => throw new ToolException($"unknown label format '{requested}' (expected html, csv or merge)"),
        };

        File.WriteAllText(path, content, new UTF8Encoding(true));
        return new
        {
            path,
            kind = merge ? "group_addresses+devices" : kind,
            format = requested,
            labels = labels.Count,
            bytes = new FileInfo(path).Length,
        };
    }

    private object ReportExport(JsonElement args)
    {
        var project = Current;
        var path = WritePath(Required(args, "path"));
        var options = new ProjectReportOptions
        {
            Title = Str(args, "title"),
            IncludeTitlePage = BoolOr(args, "include_title_page", true),
            IncludeTopology = BoolOr(args, "include_topology", true),
            IncludeGroupAddresses = BoolOr(args, "include_group_addresses", true),
            IncludeComObjects = BoolOr(args, "include_com_objects", true),
            IncludeLinkMatrix = BoolOr(args, "include_link_matrix", true),
            IncludeFindings = BoolOr(args, "include_findings", true),
        };
        var file = ProjectReport.Write(project, path, options);
        var dashboard = Dashboard.Build(project);
        var findings = ProjectCheck.Run(project);
        return new
        {
            path = file.Path,
            bytes = file.Bytes,
            devices = dashboard.Devices,
            groupAddresses = dashboard.GroupAddresses,
            comObjects = dashboard.ComObjects,
            findings = findings.Count,
            errors = ProjectCheck.Count(findings, CheckSeverity.Error),
            warnings = ProjectCheck.Count(findings, CheckSeverity.Warning),
            infos = ProjectCheck.Count(findings, CheckSeverity.Info),
        };
    }

    private object AcceptanceProtocolExport(JsonElement args)
    {
        var project = Current;
        var format = (Str(args, "format") ?? "html").ToLowerInvariant();
        if (format is not ("html" or "csv"))
            throw new ToolException($"unknown format '{format}' (expected html or csv)");
        var language = Str(args, "language")?.ToLowerInvariant();
        if (language is not null && language is not ("de" or "en"))
            throw new ToolException($"unknown language '{language}' (expected de or en)");
        var path = WritePath(Required(args, "path"));

        IReadOnlyDictionary<string, DeviceProgrammingState>? states = null;
        if (Str(args, "states_path") is { Length: > 0 } statesPath)
        {
            var full = Path.GetFullPath(statesPath);
            if (!File.Exists(full)) throw new ToolException($"device programming state file not found: {full}");
            try
            {
                states = DeviceProgrammingStates.Parse(File.ReadAllText(full), project.Id);
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                throw new ToolException($"device programming state file rejected: {ex.Message}");
            }
        }

        var options = new AcceptanceProtocolOptions
        {
            Title = Str(args, "title"),
            Language = language ?? Strings.Language,
            Technician = Str(args, "technician"),
            BuildingOwner = Str(args, "building_owner"),
            DefectLines = Math.Clamp(Int(args, "defect_lines") ?? 5, 0, 50),
            ProgrammingStates = states,
        };
        var file = format == "html"
            ? AcceptanceProtocol.Write(project, path, options)
            : AcceptanceProtocol.WriteCsv(project, path, options);
        var summary = AcceptanceProtocol.Summarize(project, options);

        return new
        {
            path = file.Path,
            format,
            bytes = file.Bytes,
            language = options.Language == "en" ? "en" : "de",
            devices = summary.Devices,
            openItems = summary.OpenItems,
            commissioning = summary.Commissioning switch
            {
                AcceptanceCommissioningMode.Real => "real",
                AcceptanceCommissioningMode.Simulation => "simulation",
                _ => "not_recorded",
            },
            documentHash = summary.DocumentHash,
        };
    }

    private object ProjectCompareTool(JsonElement args)
    {
        var leftPath = Path.GetFullPath(Required(args, "left_path"));
        var rightPath = Path.GetFullPath(Required(args, "right_path"));
        if (!File.Exists(leftPath)) throw new ToolException($"project file not found: {leftPath}");
        if (!File.Exists(rightPath)) throw new ToolException($"project file not found: {rightPath}");

        var report = ProjectComparer.Compare(Knxproj.Load(leftPath), Knxproj.Load(rightPath));
        const int limit = 500;
        return new
        {
            leftProject = report.LeftProjectName,
            rightProject = report.RightProjectName,
            generatedAt = report.GeneratedAt,
            added = report.AddedCount,
            removed = report.RemovedCount,
            changed = report.ChangedCount,
            unchanged = report.UnchangedCount,
            total = report.Items.Count,
            truncated = report.Items.Count > limit,
            items = report.Items.Take(limit).Select(item => new
            {
                path = item.Path,
                objectType = item.ObjectType,
                objectName = item.ObjectName,
                modification = item.Modification.ToString(),
                oldValue = item.OldValue,
                newValue = item.NewValue,
                details = item.Details,
            }).ToList(),
        };
    }

    // ---------------------------------------------------------------- undo / redo

    private object UndoTool(JsonElement args)
    {
        var restored = _undo.Undo() ?? throw new ToolException("nothing to undo");
        _project = restored;
        return new { undone = true, project = Summary(restored), undo = History() };
    }

    private object RedoTool(JsonElement args)
    {
        var restored = _undo.Redo() ?? throw new ToolException("nothing to redo");
        _project = restored;
        return new { redone = true, project = Summary(restored), undo = History() };
    }

    // ---------------------------------------------------------------- bus

    private async Task<object> BusDiscover(JsonElement args)
    {
        var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
        try
        {
            var host = Str(args, "host");
            var devices = await _bus.DiscoverAsync(TimeSpan.FromMilliseconds(timeoutMs), host)
                .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 3000));
            return new
            {
                count = devices.Count,
                timeoutMs,
                devices = devices.Select(d => new
                {
                    name = d.Name,
                    address = d.KnxAddress,
                    endpoint = d.ControlEndpoint.ToString(),
                    mac = d.MacAddress,
                    medium = d.KnxMedium,
                }).ToList(),
            };
        }
        catch (TimeoutException)
        {
            throw new ToolException($"bus discovery did not finish within {timeoutMs + 3000} ms");
        }
        catch (Exception ex)
        {
            throw new ToolException($"bus discovery failed: {ex.Message}");
        }
    }

    private async Task<object> BusConnect(JsonElement args)
    {
        if (_bus.State == BusConnectionState.Connected)
            return new
            {
                connected = true,
                alreadyConnected = true,
                device = _bus.DeviceName,
                state = _bus.State.ToString().ToLowerInvariant(),
            };

        var mode = (Str(args, "mode") ?? "tunnel").ToLowerInvariant();
        var ownAddress = Str(args, "own_address") ?? "1.1.250";
        var interfaceText = Str(args, "interface");
        IPAddress? localAddress = null;
        if (interfaceText is not null)
        {
            if (!IPAddress.TryParse(interfaceText, out localAddress))
                throw new ToolException($"'{interfaceText}' is not a local IPv4 address");
        }
        var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
        if (!IndividualAddress.TryParse(ownAddress, out var individualAddress))
            throw new ToolException($"'{ownAddress}' is not a valid individual address (x.y.z)");

        try
        {
            if (mode == "routing")
            {
                await _bus.ConnectRoutingAsync(localAddress);
            }
            else if (mode == "tunnel")
            {
                var devices = await _bus.DiscoverAsync(TimeSpan.FromMilliseconds(timeoutMs))
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs + 3000));
                if (devices.Count == 0)
                    throw new ToolException($"no KNXnet/IP gateway answered within {timeoutMs} ms");

                var wanted = Str(args, "device");
                var device = wanted is null
                    ? devices[0]
                    : devices.FirstOrDefault(d =>
                        d.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(d.KnxAddress, wanted, StringComparison.OrdinalIgnoreCase));
                if (device is null)
                    throw new ToolException(
                        $"no gateway matched '{wanted}'; found: " +
                        string.Join(", ", devices.Select(d => $"{d.Name} ({d.KnxAddress})")));

                await _bus.ConnectTunnelAsync(device, individualAddress);
            }
            else
            {
                throw new ToolException($"unknown mode '{mode}' (tunnel|routing)");
            }
        }
        catch (ToolException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new ToolException($"connecting did not finish within {timeoutMs + 3000} ms");
        }
        catch (Exception ex)
        {
            throw new ToolException($"connect failed: {ex.Message}");
        }

        return new
        {
            connected = _bus.State == BusConnectionState.Connected,
            mode,
            device = _bus.DeviceName,
            ownAddress,
            @interface = localAddress?.ToString(),
            state = _bus.State.ToString().ToLowerInvariant(),
        };
    }

    private async Task<object> BusDisconnect(JsonElement args)
    {
        await _bus.DisposeAsync();
        return new
        {
            connected = _bus.State == BusConnectionState.Connected,
            state = _bus.State.ToString().ToLowerInvariant(),
        };
    }

    private async Task<object> BusRead(JsonElement args)
    {
        RequireConnected();
        var address = ParseAddress(Required(args, "group_address"));
        var datapointType = Str(args, "datapoint_type") ?? ProjectDatapointType(address);
        var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 3000, 100, 10000);

        BusTelegram? received;
        try
        {
            received = await _bus.ReadGroupValueAsync(address, TimeSpan.FromMilliseconds(timeoutMs));
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            throw new ToolException($"group read failed: {ex.Message}");
        }

        if (received is null)
            throw new ToolException(
                $"no GroupValueResponse for {address} within {timeoutMs} ms; " +
                "our own L_Data.con confirmation is not a response");

        return new
        {
            groupAddress = address.ToString(),
            datapointType,
            value = Dpt.Decode(datapointType, received.Telegram.Data),
            payload = Convert.ToHexString(received.Telegram.Data),
            source = received.Telegram.Source.ToString(),
            transport = received.Source,
            apci = received.Telegram.Apci.ToString(),
            hopCount = received.Telegram.HopCount,
            timestamp = received.Timestamp,
        };
    }

    private Task<object> BusWrite(JsonElement args)
        => throw new ToolException(
            "bus_write is disabled: this build is read-only. Group value writes are blocked by policy " +
            "until the wire encoding and the safe programming sequence are verified on real hardware.");

    private object BusStatus(JsonElement args) => new    {
        connected = _bus.State == BusConnectionState.Connected,
        state = _bus.State.ToString().ToLowerInvariant(),
        device = _bus.DeviceName,
        recorder = new
        {
            recording = _recorder.IsRecording,
            count = _recorder.Count,
            startedAt = _recorder.StartedAt,
        },
        project = _project?.Name,
        hint = _bus.State == BusConnectionState.Connected
            ? null
            : "No KNXnet/IP connection is active. Connect a tunnel or routing interface before bus_read/bus_write.",
    };

    private async Task<object> BusScanTool(JsonElement args)
    {
        var target = Str(args, "target");
        (byte Area, byte Line)? line = string.IsNullOrWhiteSpace(target) ? null : ParseLine(target);
        var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
        var responseTimeoutMs = Math.Clamp(Int(args, "response_timeout_ms") ?? 1000, 100, 5000);
        var options = new ScanOptions
        {
            ResponseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMs),
            // A descriptor probe may be missed on a busy line; two attempts, never an open retry loop.
            Attempts = 2,
        };

        BusScan scan;
        try
        {
            scan = await BusDiagnostics.WithSessionAsync(Str(args, "gateway"), timeoutMs, session => line is null
                ? Diagnostics.ScanBusAsync(session, options)
                : Diagnostics.ScanLineAsync(session, line.Value.Area, line.Value.Line, options));
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            throw new ToolException($"bus scan failed: {ex.Message}");
        }

        var check = Diagnostics.CheckAddresses(scan, ExpectedAddresses());
        return new
        {
            target = line is null ? "bus" : $"{line.Value.Area}.{line.Value.Line}",
            addressCheck = new
            {
                clean = check.Clean,
                duplicates = check.Duplicates.Select(a => a.ToString()).ToList(),
                missing = check.Missing.Select(a => a.ToString()).ToList(),
                unexpected = check.Unexpected.Select(a => a.ToString()).ToList(),
                expectedFromProject = _project is not null,
            },
            found = scan.Devices.Count(d => d.Found),
            failed = scan.Failures.Count,
            responders = scan.Responders.Distinct().Count(),
            elapsedMs = (int)scan.Elapsed.TotalMilliseconds,
            devices = scan.Devices.Select(ScannedDeviceJson).ToList(),
        };
    }

    private async Task<object> BusDeviceInfoTool(JsonElement args)
    {
        var address = ParseIndividualAddress(Required(args, "address"));
        var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
        var responseTimeoutMs = Math.Clamp(Int(args, "response_timeout_ms") ?? 1000, 100, 5000);
        var options = new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMs) };

        BusScan scan;
        try
        {
            scan = await BusDiagnostics.WithSessionAsync(Str(args, "gateway"), timeoutMs,
                session => Diagnostics.ScanAsync(session, [address], options));
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            throw new ToolException($"device info failed: {ex.Message}");
        }

        var device = scan.Devices[0];
        if (!device.Found)
            throw new ToolException($"no device descriptor from {address}: {device.Error}");
        return new
        {
            address = address.ToString(),
            descriptorType = device.Descriptor!.Type,
            maskVersion = device.Descriptor.MaskVersion is { } mask ? $"0x{mask:X4}" : null,
            mask = device.Descriptor.MaskName,
            description = device.Descriptor.Describe(),
            bytes = Convert.ToHexString(device.Descriptor.Bytes),
            projectDevice = _project is null
                ? null
                : ProjectQuery.FindDevice(_project, address)?.Name,
            elapsedMs = (int)scan.Elapsed.TotalMilliseconds,
        };
    }

    private object BusStatisticsTool(JsonElement args)
    {
        var window = TimeSpan.FromMilliseconds(Math.Clamp(Int(args, "window_ms") ?? 60_000, 100, 3_600_000));
        return new
        {
            windowMs = (int)window.TotalMilliseconds,
            window = Statistics(_statistics.Snapshot(window)),
            lastSecond = _statistics.Snapshot(TimeSpan.FromSeconds(1)).Telegrams,
            lastMinute = _statistics.Snapshot(TimeSpan.FromMinutes(1)).Telegrams,
            stored = _statistics.Count,
            hint = _bus.State == BusConnectionState.Connected
                ? null
                : "No KNXnet/IP connection is active, so no telegrams are counted yet.",
        };
    }

    private async Task<object> DownloadPlanTool(JsonElement args)
    {
        var project = Current;
        var address = ParseIndividualAddress(Required(args, "device_address"));
        var located = ProjectQuery.LocateDevice(project, address)
            ?? throw new ToolException($"device {address} not found in the project");

        // A descriptor read is the only live step and it is read-only; without 'gateway' the plan
        // stays offline and reports the missing descriptor as a blocker.
        DeviceDescriptor? descriptor = null;
        if (Str(args, "gateway") is { Length: > 0 } gateway)
        {
            var timeoutMs = Math.Clamp(Int(args, "timeout_ms") ?? 1500, 100, 10000);
            var responseTimeoutMs = Math.Clamp(Int(args, "response_timeout_ms") ?? 1000, 100, 5000);
            var options = new ScanOptions { ResponseTimeout = TimeSpan.FromMilliseconds(responseTimeoutMs) };
            BusScan scan;
            try
            {
                scan = await BusDiagnostics.WithSessionAsync(gateway, timeoutMs,
                    session => Diagnostics.ScanAsync(session, [address], options));
            }
            catch (Exception ex) when (ex is not ToolException)
            {
                throw new ToolException($"descriptor read failed: {ex.Message}");
            }
            descriptor = scan.Devices[0].Descriptor;
        }

        var programRef = located.Device.Hardware2ProgramRefId is { Length: > 0 } reference ? reference : null;
        var plan = DownloadPreflight.Plan(new DownloadPreflightRequest
        {
            DeviceAddress = address,
            Descriptor = descriptor,
            // This build cannot query programming mode read-only, and no agent can confirm on site.
            ProgrammingMode = false,
            AddressConfirmed = false,
            DryRunConfirmed = false,
            ExpectedArea = located.Area.Address,
            ExpectedLine = located.Line?.Address,
            ExpectedApplicationId = programRef,
        });

        return new
        {
            readOnly = true,
            writesEnabled = KnxBus.WritesEnabled,
            realDownloadAvailable = false,
            device = new
            {
                address = address.ToString(),
                name = located.Device.Name,
                area = located.Area.Address,
                line = located.Line?.Address,
                productRefId = located.Device.ProductRefId,
                programRefId = programRef,
            },
            descriptor = descriptor is null ? null : new
            {
                type = descriptor.Type,
                maskVersion = descriptor.MaskVersion is { } mask ? $"0x{mask:X4}" : null,
                mask = descriptor.MaskName,
                description = descriptor.Describe(),
            },
            confirmations = new { programmingMode = false, address = false, dryRun = false },
            plan = new
            {
                ready = plan.Ready,
                target = plan.DeviceAddress.ToString(),
                program = plan.ProgramName,
                imageHash = plan.ImageHash,
                imageBytes = plan.ImageLength,
                startAddress = $"0x{plan.StartAddress:X4}",
                maskVersion = plan.MaskVersion is { } planMask ? $"0x{planMask:X4}" : null,
                chunks = plan.Chunks.Select(c => new { address = $"0x{c.Address:X4}", bytes = c.Length }).ToList(),
                blockers = plan.Blockers.Select(DownloadCheckJson).ToList(),
                warnings = plan.Warnings.Select(DownloadCheckJson).ToList(),
                dryRun = plan.Describe(),
            },
            attemptLog = new
            {
                writtenFor = "every DownloadSession.RunAsync (real or refused)",
                entries = 0,
                note = "no download ran: the real write path is disabled by policy",
            },
        };
    }

    private static object DownloadCheckJson(DownloadCheck check) => new
    {
        code = JsonNamingPolicy.SnakeCaseLower.ConvertName(check.Code.ToString()),
        severity = check.Severity.ToString().ToLowerInvariant(),
        message = check.Message,
    };

    private static object Statistics(BusStatisticsSnapshot snapshot) => new
    {
        from = snapshot.From,
        to = snapshot.To,
        telegrams = snapshot.Telegrams,
        acks = snapshot.Acks,
        errors = snapshot.Errors,
        perSecond = Math.Round(snapshot.PerSecond, 2),
        perMinute = Math.Round(snapshot.PerMinute, 1),
        sources = snapshot.Sources.Select(s => new { source = s.Source, telegrams = s.Telegrams }).ToList(),
        errorsByKind = snapshot.ErrorsByKind
            .Select(e => new { kind = e.Kind.ToString().ToLowerInvariant(), count = e.Count }).ToList(),
    };

    private static object ScannedDeviceJson(ScannedDevice device) => new
    {
        address = device.Address.ToString(),
        found = device.Found,
        maskVersion = device.Descriptor?.MaskVersion is { } mask ? $"0x{mask:X4}" : null,
        mask = device.Descriptor?.MaskName,
        descriptor = device.Descriptor?.Describe(),
        error = device.Error,
    };

    /// <summary>Project device addresses as the expected set for the scan comparison, if a project is open.</summary>
    private List<IndividualAddress>? ExpectedAddresses()
        => _project is null ? null : ProjectQuery.DeviceList(_project).Select(d => d.Address).ToList();

    // ---------------------------------------------------------------- catalog

    private List<KnxProductCatalog>? _catalogs;
    // KNX_CATALOG_DIR lets hosts/tests point the library somewhere else (default: %APPDATA%\KnxClone\catalogs).
    private readonly CatalogStore _catalogStore = new(Environment.GetEnvironmentVariable("KNX_CATALOG_DIR"));

    private List<KnxProductCatalog> Catalogs() => _catalogs ??= _catalogStore.LoadAll();

    private KnxProductCatalog PickCatalog(string? selector)
    {
        var catalogs = Catalogs();
        if (catalogs.Count == 0)
            throw new ToolException("catalogue library is empty; import a .knxprod with catalog_import first");
        if (string.IsNullOrWhiteSpace(selector)) return catalogs[0];
        return catalogs.FirstOrDefault(c =>
                   c.Name.Contains(selector, StringComparison.OrdinalIgnoreCase) ||
                   (c.Manufacturer?.Name.Contains(selector, StringComparison.OrdinalIgnoreCase) ?? false) ||
                   string.Equals(c.Manufacturer?.Id, selector, StringComparison.OrdinalIgnoreCase))
               ?? throw new ToolException(
                   $"no catalogue matched '{selector}'; available: " +
                   string.Join(", ", catalogs.Select(c => c.Name)));
    }

    private object CatalogLibrary(JsonElement args) => new
    {
        directory = _catalogStore.Directory,
        count = Catalogs().Count,
        catalogs = Catalogs().Select(c => new
        {
            name = c.Name,
            path = c.Path,
            manufacturer = c.Manufacturer?.Name,
            manufacturerId = c.Manufacturer?.Id,
            products = c.Products.Count,
            programs = c.Products.SelectMany(p => p.ProgramRefIds).Distinct().Count(),
            sections = c.CatalogSections.Count,
        }).ToList(),
        errors = _catalogStore.LoadErrors.Select(e => new { file = e.File, message = e.Message }).ToList(),
    };

    private object CatalogImport(JsonElement args)
    {
        var path = Required(args, "path");
        KnxProductCatalog catalog;
        try
        {
            catalog = _catalogStore.Import(path);
        }
        catch (Exception ex)
        {
            throw new ToolException($"catalogue not readable: {ex.Message}");
        }
        _catalogs = null;
        return new
        {
            imported = true,
            name = catalog.Name,
            path = catalog.Path,
            manufacturer = catalog.Manufacturer?.Name,
            manufacturerId = catalog.Manufacturer?.Id,
            products = catalog.Products.Count,
            programs = catalog.Products.SelectMany(p => p.ProgramRefIds).Distinct().Count(),
        };
    }

    private object CatalogProducts(JsonElement args)
    {
        var catalog = PickCatalog(Str(args, "catalog"));
        var filter = Str(args, "filter");
        var products = catalog.Products
            .Where(p => filter is null ||
                        p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                        (p.OrderNumber?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(p => new
            {
                id = p.Id,
                name = p.Name,
                orderNumber = p.OrderNumber,
                hardware = p.HardwareRefId,
                programs = p.ProgramRefIds,
                catalogPaths = p.CatalogPaths,
            }).ToList();

        return new
        {
            catalog = catalog.Name,
            manufacturer = catalog.Manufacturer?.Name,
            count = products.Count,
            products,
        };
    }

    private object CatalogImportDevice(JsonElement args)
    {
        var project = Current;
        var productId = Required(args, "product_id");
        var catalog = PickCatalog(Str(args, "catalog"));
        var product = catalog.FindProduct(productId)
                      ?? throw new ToolException($"product '{productId}' is not in catalogue '{catalog.Name}'");

        var areaAddress = RequiredByte(args, "line_area");
        var lineAddress = RequiredByte(args, "line_address");
        var area = project.Installations.SelectMany(i => i.Areas).FirstOrDefault(a => a.Address == areaAddress)
                   ?? throw new ToolException($"area {areaAddress} not found");
        var line = area.Lines.FirstOrDefault(l => l.Address == lineAddress)
                   ?? throw new ToolException($"line {areaAddress}.{lineAddress} not found");

        var address = ProjectQuery.NextFreeDeviceAddress(project, areaAddress, lineAddress);
        if (Str(args, "address") is { } requested &&
            !IndividualAddress.TryParse(requested, out address))
            throw new ToolException($"'{requested}' is not a valid individual address (x.y.z)");
        if (ProjectQuery.FindDevice(project, address) is not null)
            throw new ToolException($"individual address {address} is already in use");

        var program = catalog.FindProgramFor(product);
        var device = CatalogInstaller.CreateDevice(product, program,
            address, Str(args, "name") ?? product.Name);
        ProjectQuery.AddToLine(line, device);

        _undo.Push(project);
        return new
        {
            inserted = true,
            device = Device(device),
            line = $"{areaAddress}.{lineAddress}",
            product = new { id = product.Id, name = product.Name, orderNumber = product.OrderNumber },
            program = program?.Name,
            comObjects = device.ComObjectRefs.Count,
        };
    }

    private object RecorderStart(JsonElement args)    {
        _recorder.Start();
        return new { recording = true, startedAt = _recorder.StartedAt, count = _recorder.Count };
    }

    private object RecorderStop(JsonElement args)
    {
        var entries = _recorder.Stop();
        const int limit = 200;
        return new
        {
            recording = false,
            count = entries.Count,
            startedAt = _recorder.StartedAt,
            truncated = entries.Count > limit,
            telegrams = entries.Take(limit).Select(Telegram).ToList(),
        };
    }

    private object RecorderSave(JsonElement args)
    {
        var path = WritePath(Required(args, "path"));
        var format = Required(args, "format").ToLowerInvariant();
        switch (format)
        {
            case "csv":
                _recorder.SaveCsv(path);
                break;
            case "jsonl" or "json":
                _recorder.SaveJsonLines(path);
                break;
            case "xml":
                SaveRecorderXml(path);
                break;
            default:
                throw new ToolException($"unknown recorder format '{format}' (expected xml, csv or jsonl)");
        }

        return new { path, format, count = _recorder.Count, bytes = new FileInfo(path).Length };
    }

    private void SaveRecorderXml(string path)
    {
        var root = new XElement("Telegrams");
        foreach (var entry in _recorder.Entries)
            root.Add(new XElement("Telegram",
                new XAttribute("Timestamp", entry.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
                new XAttribute("Source", entry.Source),
                new XAttribute("Destination", entry.Destination),
                new XAttribute("IsGroupAddress", entry.IsGroupAddress),
                new XAttribute("Apci", entry.Apci),
                new XAttribute("Data", entry.Data)));
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
    }

    // ---------------------------------------------------------------- helpers

    private Project Current => _project
        ?? throw new ToolException("no project is open; call project_open or project_new first");

    private object History() => new { steps = _undo.Steps, canUndo = _undo.CanUndo, canRedo = _undo.CanRedo };

    private object Summary(Project project) => new
    {
        id = project.Id,
        name = project.Name,
        comment = project.Comment,
        path = _projectPath,
        saved = _projectPath is not null && File.Exists(_projectPath),
        installations = project.Installations.Count,
        areas = project.Installations.Sum(i => i.Areas.Count),
        lines = project.Installations.Sum(i => i.Areas.Sum(a => a.Lines.Count)),
        devices = ProjectQuery.DeviceList(project).Count(),
        groupAddresses = ProjectQuery.GroupAddresses(project).Count(),
        comObjects = ProjectQuery.ComObjectRefs(project).Count(),
    };

    private static object Address(GroupAddressEntry entry) => new
    {
        id = entry.Id,
        address = entry.Address.ToString(),
        name = entry.Name,
        datapointType = entry.DatapointType,
        description = entry.Description,
        comment = entry.Comment,
    };

    private static object Device(DeviceInstance device) => new
    {
        address = device.Address.ToString(),
        name = device.Name,
        productRefId = device.ProductRefId,
        hardware2ProgramRefId = device.Hardware2ProgramRefId,
        description = device.Description,
        comment = device.Comment,
        comObjects = device.ComObjectRefs.Count,
    };

    private static object Suggestion(LinkSuggestion suggestion) => new
    {
        device = suggestion.Device.Address.ToString(),
        comObject = suggestion.ComObject.Text ?? suggestion.ComObject.Id,
        groupAddress = suggestion.GroupAddress.Address.ToString(),
        groupAddressName = suggestion.GroupAddress.Name,
        score = suggestion.Score,
        reason = suggestion.Reason,
    };

    private static object Telegram(RecordedTelegram entry) => new
    {
        timestamp = entry.Timestamp,
        source = entry.Source,
        destination = entry.Destination,
        isGroupAddress = entry.IsGroupAddress,
        apci = entry.Apci,
        data = entry.Data,
    };

    private static bool Matches(GroupAddressEntry entry, string filter)
        => entry.Address.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)
           || (entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
           || (entry.Description?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
           || (entry.DatapointType?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    private static GroupAddressEntry FindAddressOrThrow(Project project, string value)
    {
        var address = ParseAddress(value);
        return ProjectQuery.FindGroupAddress(project, address)
               ?? throw new ToolException($"group address {address} not found");
    }

    private DeviceInstance FindDeviceOrThrow(Project project, string value)
    {
        var address = ParseIndividualAddress(value);
        return ProjectQuery.FindDevice(project, address)
               ?? throw new ToolException($"device {address} not found");
    }

    private static (DeviceInstance Device, Installation Installation, Area Area, Line? Line) LocateDeviceOrThrow(
        Project project, string value)
    {
        var address = ParseIndividualAddress(value);
        return ProjectQuery.LocateDevice(project, address)
               ?? throw new ToolException($"device {address} not found");
    }

    private static GroupAddress ParseAddress(string value)
    {
        try
        {
            return GroupAddress.Parse(value.Trim());
        }
        catch (Exception)
        {
            throw new ToolException($"'{value}' is not a group address (expected main/middle/sub, e.g. 1/1/1)");
        }
    }

    private static IndividualAddress ParseIndividualAddress(string value)
    {
        if (!IndividualAddress.TryParse(value.Trim(), out var address)
            || address.Area > 15 || address.Line > 15)
            throw new ToolException($"'{value}' is not an individual address (expected area.line.device, e.g. 1.1.5)");
        return address;
    }

    private static (byte Area, byte Line) ParseLine(string value)
    {
        var parts = value.Split('.', '/');
        if (parts.Length != 2 || !byte.TryParse(parts[0], out var area) || !byte.TryParse(parts[1], out var line)
            || area > 15 || line > 15)
            throw new ToolException($"'{value}' is not a line (expected area.line, e.g. 1.1)");
        return (area, line);
    }

    private static Installation FirstInstallation(Project project)
        => project.Installations.FirstOrDefault()
           ?? throw new ToolException("project has no installation");

    private static GroupAddressStyle ParseStyle(string? value)
    {
        var text = (value ?? "three_level").Replace("_", "").Replace("-", "").Trim();
        return text.ToLowerInvariant() switch
        {
            "threelevel" or "three" or "3" or "3level" => GroupAddressStyle.ThreeLevel,
            "twolevel" or "two" or "2" or "2level" => GroupAddressStyle.TwoLevel,
            "free" => GroupAddressStyle.Free,
            _ => throw new ToolException($"unknown group address style '{value}' (expected three_level, two_level or free)"),
        };
    }

    /// <summary>
    /// "1.001", "DPT-1-1" and "1" all become "DPST-1-1"; delegates to the one implementation
    /// in <see cref="ProjectWizard"/> so the CSV import and the MCP tools cannot drift apart.
    /// </summary>
    private static string? NormaliseDpt(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : ProjectWizard.NormaliseDpt(value);

    private static string CsvLine(params string?[] columns)
        => string.Join(';', columns.Select(column => column is null ? "" : Quote(column)));

    private static string Quote(string value)
        => value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static int CountLines(string content)
        => content.Split('\n').Count(line => line.Trim().Length > 0);

    private static string WritePath(string path)
    {
        var full = Path.GetFullPath(path);
        EnsureDirectory(full);
        return full;
    }

    private static void EnsureDirectory(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
    }

    private void RequireConnected()
    {
        if (_bus.State != BusConnectionState.Connected)
            throw new ToolException("not connected to a KNXnet/IP interface: no tunnel or routing connection " +
                                    "is active, so no telegram can be sent or received");
    }

    private string? ProjectDatapointType(GroupAddress address)
        => _project is null ? null : ProjectQuery.FindGroupAddress(_project, address)?.DatapointType;

    // ---------------------------------------------------------------- argument access

    private static string? Str(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static bool TryStr(JsonElement args, string name, out string? value)
    {
        value = null;
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var element)) return false;
        value = element.ValueKind == JsonValueKind.String ? element.GetString() : null;
        return true;
    }

    private static string Required(JsonElement args, string name)
        => Str(args, name) is { Length: > 0 } value
            ? value
            : throw new ToolException($"missing required argument '{name}'");

    private static int? Int(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static byte RequiredByte(JsonElement args, string name)
    {
        var value = Int(args, name) ?? throw new ToolException($"missing required argument '{name}'");
        return value is >= 0 and <= 15
            ? (byte)value
            : throw new ToolException($"'{name}' must be between 0 and 15");
    }

    private static bool Bool(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;

    /// <summary>Tri-state boolean: absent means the fallback default.</summary>
    private static bool BoolOr(JsonElement args, string name, bool fallback)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;

    // ---------------------------------------------------------------- schema building

    private sealed record Prop(string Name, string Type, string Description, bool Required = false, string[]? Values = null);

    private static JsonObject Schema(params Prop[] properties)
    {
        var propertiesObject = new JsonObject();
        var required = new JsonArray();
        foreach (var property in properties)
        {
            var node = new JsonObject
            {
                ["type"] = property.Type,
                ["description"] = property.Description,
            };
            if (property.Values is { Length: > 0 } values)
            {
                var allowed = new JsonArray();
                foreach (var value in values) allowed.Add(value);
                node["enum"] = allowed;
            }
            propertiesObject[property.Name] = node;
            if (property.Required) required.Add(property.Name);
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = propertiesObject };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static Prop StrProp(string name, string description, bool required = false)
        => new(name, "string", description, required);

    private static Prop IntProp(string name, string description, bool required = false)
        => new(name, "integer", description, required);

    private static Prop BoolProp(string name, string description, bool required = false)
        => new(name, "boolean", description, required);

    private static Prop EnumProp(string name, string description, string[] values, bool required = false)
        => new(name, "string", description, required, values);

    private static Func<JsonElement, Task<object>> Sync(Func<JsonElement, object> handler)
        => args => Task.FromResult(handler(args));
}

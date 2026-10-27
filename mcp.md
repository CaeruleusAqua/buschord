# buschord-mcp – MCP server for BusChord

Headless [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the
`Knx.Core`, `Knx.Bus`, `Knx.Reports` and `Knx.ProjectCompare` functionality as MCP tools.
It is a plain .NET 9 console app with no NuGet dependencies; JSON-RPC 2.0 runs over stdio,
one JSON message per line. stdout carries protocol messages only, all logging goes to stderr.

## Run

```bash
dotnet build
dotnet run --project src/Knx.Mcp --no-build              # stdio server (for MCP clients)
dotnet run --project src/Knx.Mcp -- --selftest           # end-to-end check, exit 0/1
dotnet run --project src/Knx.Mcp -- --list-tools         # tool names only
```

Use `--no-build` for clients: a `dotnet run` that builds writes MSBuild output to stdout and
corrupts the protocol stream. After one build the compiled entry point can also be used directly:
`src/Knx.Mcp/bin/Debug/net9.0/Knx.Mcp.dll`.

## Connect an agent

Register the server as a stdio MCP server, for example in an OpenCode/Claude-style config:

```json
{
  "mcpServers": {
    "knx-ets-clone": {
      "command": "dotnet",
      "args": ["<repo>/src/Knx.Mcp/bin/Debug/net9.0/Knx.Mcp.dll"]
    }
  }
}
```

The agent then calls `initialize`, reads `tools/list` and invokes tools with `tools/call`.
Server name `knx-ets-clone`, version `1.0.0`, protocol version `2025-06-18`
(`2025-03-26` is accepted for older clients).

Typical session: `project_open` or `project_new` → inspect with `project_info` /
`project_dashboard` → edit with `group_address_*`, `group_link_*`, `device_*`, `smart_link_apply`,
`product_replace` → verify with `project_check` → `project_save`.

## State model

- One open project per server process. `project_open` replaces it and resets history.
- Mutating tools change only the in-memory project and push an undo snapshot (100 steps).
  `undo`/`redo` move through that history across tool calls.
- The `.knxproj` on disk changes only when `project_save` is called.
- Results come back as a text block (compact JSON) plus `structuredContent`; expected failures
  are `isError: true` results with a readable message, never JSON-RPC errors.

## Tools

**Project** — `project_open(path)`, `project_new(name, style, areas, lines, save_path?)`,
`project_save(path?)`, `project_info`, `project_dashboard`

**Search** — `project_search(query, scope?, regex?, case_sensitive?, limit?)`,
`project_replace_preview(query, replacement, scope?, regex?, case_sensitive?)`,
`project_replace_apply(query, replacement, scope?, regex?, case_sensitive?)`.
`scope` is one of `all`, `devices`, `group_addresses`, `topology`, `communication_objects` or `buildings`.
Hits carry `kind`, `id`, display `path`, matched `field`, `value` and a `snippet`; `total` counts all matches
while `count` respects `limit`, and ordering is deterministic (kind, path, field, id).
Replacement only touches names, comments and communication-object text/function text — never addresses,
datapoint types or references. `project_replace_preview` reports every before/after field without changing
the project; `project_replace_apply` recomputes and validates that preview before the first mutation, pushes
one undo step, and returns `changedIds`. A request without matches changes nothing and adds no undo entry.
Building nodes are searched only when the optional building model provides `Project.Buildings`.

**Group addresses** — `group_addresses_list(filter?)`, `group_address_add(address, name, datapoint_type?)`,
`group_address_update(address, name?, datapoint_type?, description?, comment?)`,
`group_address_delete(address)`, `group_address_import_csv(content | path)`

**Devices** — `devices_list(line?)`, `device_update(address, name?, description?, comment?)`,
`device_add(line_area, line_address, name, product_ref?)`, `device_delete(address)`,
`device_move(address, target_area, target_line)`

**Manual links** — `communication_objects_list(device_address)`,
`group_link_add(device_address, object_id, group_address)`,
`group_link_remove(device_address, object_id, group_address)`.
Use the reference `id` from `communication_objects_list`, not its `comObjectId`.
List results include object text/number and links with GA id/address/name; dangling links retain their id.
Add/remove require an existing device, reference and GA; duplicate/absent links return `changed: false`
without adding undo history. Errors never mutate the project. These tools work offline and send no telegrams.
No DPT compatibility is inferred when reference metadata is unavailable.

**Analysis** — `project_check`, `smart_link_suggest`, `smart_link_apply`,
`product_replace(old_ref, new_ref, new_program?, dry_run)`

**Export** — `export_csv(kind, path)`, `export_semantic(format, path)`,
`export_labels(kind, format, path)`, `report_export(path, title?, include_*)`,
`project_compare(left_path, right_path)`

`report_export` writes one self-contained printable HTML project report (title page with counts,
topology and devices, group address table with datapoint types and links, communication objects,
link matrix summary, project check findings). Optional `include_title_page`, `include_topology`,
`include_group_addresses`, `include_com_objects`, `include_link_matrix` and `include_findings`
booleans default to `true`. There is no PDF output: a dependency-free PDF writer would have to
embed fonts, so the report is printed from the browser (page breaks, print header/footer and page
numbers come from the inline CSS).

**Editing** – `undo`, `redo`

**Catalog** – `catalog_library`, `catalog_import(path)`, `catalog_products(catalog?, filter?)`,
`catalog_import_device(product_id, line_area, line_address, catalog?, name?, address?)`.
Catalogues are read from the local library (`%APPDATA%\BusChord\catalogs`, override with the
`KNX_CATALOG_DIR` environment variable); `.knxprod` files are imported with `catalog_import`.

**Bus** – `bus_discover(timeout_ms?, host?)`, `bus_connect(mode?, device?, own_address?, timeout_ms?)`,
`bus_disconnect`, `bus_read(group_address)`, `bus_write(group_address, value, datapoint_type?)`,
`bus_status`, `recorder_start`, `recorder_stop`, `recorder_save(path, format)`

**Bus diagnostics (read-only)** – `bus_scan(target?, gateway?, timeout_ms?, response_timeout_ms?)`,
`bus_device_info(address, gateway?, timeout_ms?, response_timeout_ms?)`,
`bus_statistics(window_ms?)`.
`bus_scan` broadcasts `IndividualAddressRead`, collects the answering addresses, reads each device
descriptor (`DeviceDescriptorRead`, mask version) and compares the result with the open project's
devices: duplicate, missing and unexpected addresses. `target` restricts the scan to one line
(`1.1`). `bus_device_info` reads the descriptor of one address. Both tools open their own
short-lived tunnelling connection (own address 1.1.249) because `KnxBus` exposes no
management-frame send; they send no write/programming frames and never change a device.
`bus_statistics` counts the telegrams of the current bus session over a trailing window:
telegrams, acks, errors (by kind), rate per second/minute and telegrams per source. It works
offline and returns zeros until a connection delivers telegrams.

The diagnostic logic itself is in `src/Knx.Bus/Diagnostics.cs` and takes an
`IBusDiagnosticSession`, so it is usable and tested without hardware
(`tests/Knx.Diagnostics.Tests`). The wire behaviour of scan and descriptor frames is **unverified
live**: the frame layout follows the KNX spec but has not been validated against a real device.

## Bus behaviour

`bus_discover(host="192.0.2.10")` uses a targeted SEARCH/DESCRIPTION lookup.
Without `host`, discovery sends requests on each usable local IPv4 interface.
The standalone read-only diagnostic is `dotnet run --project src/Knx.Mcp -- --bus-probe=192.0.2.10`.
See router-discovery.md for the live validation
and remaining tunnel wire-format limitations; successful discovery is not a
commissioning validation.

`bus_connect` opens the interface: `mode=tunnel` (default) searches the gateway and connects the first
match for `device` (name fragment or individual address), `mode=routing` joins the multicast group
without a device. `bus_disconnect` releases the socket; both are idempotent.
`bus_read`/`bus_write` report `isError` with "not connected" instead of blocking when no connection
exists, and `bus_connect` reports `isError` when no gateway answers the search.

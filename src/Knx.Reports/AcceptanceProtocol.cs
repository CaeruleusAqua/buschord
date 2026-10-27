using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Knx;
using Knx.Workbench;

namespace Knx.Reports;

/// <summary>
/// Inputs of the acceptance protocol document. Everything is optional except the project itself;
/// a caller that needs a reproducible document passes a fixed <see cref="GeneratedAt"/>.
/// </summary>
public sealed class AcceptanceProtocolOptions
{
    /// <summary>Document title; defaults to the project name.</summary>
    public string? Title { get; set; }
    /// <summary>Generation timestamp printed in header and footer; defaults to now.</summary>
    public DateTimeOffset? GeneratedAt { get; set; }
    /// <summary>Document language ("de" or "en"); defaults to the active UI language.</summary>
    public string Language { get; set; } = Strings.Language;
    /// <summary>Technician printed in the sign-off block; empty leaves a fill-in line.</summary>
    public string? Technician { get; set; }
    /// <summary>Building owner printed in the sign-off block; empty leaves a fill-in line.</summary>
    public string? BuildingOwner { get; set; }
    /// <summary>Blank defect-list rows for on-site notes (0..50).</summary>
    public int DefectLines { get; set; } = 5;
    /// <summary>Paper size for the print stylesheet: A4, A3 or Letter; the protocol prints in landscape.</summary>
    public string PaperSize { get; set; } = "A4";
    /// <summary>
    /// Recorded device programming states (workbench side file). Without states every device
    /// reports "no download recorded"; a recorded state proves a demo or real load and nothing more.
    /// </summary>
    public IReadOnlyDictionary<string, DeviceProgrammingState>? ProgrammingStates { get; set; }
}

/// <summary>Why a device appears in the open-item list.</summary>
public enum AcceptanceOpenItemKind
{
    /// <summary>No programming state was recorded for the device.</summary>
    DownloadNotRecorded,
    /// <summary>A state exists but does not cover every load flag.</summary>
    ProgrammingOutstanding,
    /// <summary>The recorded state was produced from a different configuration signature.</summary>
    SignatureDrift,
    /// <summary>The last recorded attempt failed.</summary>
    LastError,
}

/// <summary>What the recorded programming states prove for the whole document.</summary>
public enum AcceptanceCommissioningMode
{
    /// <summary>No programming state was recorded at all.</summary>
    NotRecorded,
    /// <summary>Only demo-origin loads were recorded.</summary>
    Simulation,
    /// <summary>At least one load or attempt carries the real origin.</summary>
    Real,
}

/// <summary>One per-device acceptance row: topology placement, recorded state and derived status.</summary>
public sealed record AcceptanceDeviceRow(
    DeviceInstance Device,
    Installation Installation,
    Area Area,
    Line? Line,
    Segment? Segment,
    DeviceProgrammingState? State,
    DeviceProgrammingStatus Status);

/// <summary>One open item derived from the project data and the recorded programming states.</summary>
public sealed record AcceptanceOpenItem(
    AcceptanceOpenItemKind Kind,
    DeviceInstance Device,
    DeviceProgrammingStatus Status);

/// <summary>Counts, commissioning mode and document hash of one protocol build.</summary>
public sealed record AcceptanceProtocolSummary(
    int Devices,
    int OpenItems,
    AcceptanceCommissioningMode Commissioning,
    string DocumentHash);

/// <summary>
/// KNX acceptance/test protocol (Inbetriebnahme-/Abnahmeprotokoll). Produces one self-contained,
/// printable HTML document and a CSV variant from the same model: project facts, topology and
/// device list, group addresses, function matrix, per-device test rows, open items with a blank
/// defect list and a sign-off block.
///
/// Honesty contract: the visual/function/bus/parameter columns always start as an open check box
/// (legend: not tested) and the measured-value/remark columns are empty fill-in placeholders; the
/// download column reports only what a recorded <see cref="DeviceProgrammingState"/> proves, with
/// its origin (demo = simulation, real = real) and only as "loaded" when every load flag is set.
/// The document never claims a download happened by itself.
/// </summary>
public static class AcceptanceProtocol
{
    /// <summary>Document format version; changes when the rendered structure or the hash input changes.</summary>
    public const int Version = 1;

    public static string Render(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AcceptanceProtocolOptions();

        var lang = Language(options);
        var generated = options.GeneratedAt ?? DateTimeOffset.Now;
        var title = string.IsNullOrWhiteSpace(options.Title) ? project.Name : options.Title!;
        var date = generated.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        var built = Build(project, options);
        var hash = built.Hash;
        var rows = built.Rows;
        var items = built.Items;

        var html = new StringBuilder(48 * 1024);
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine($"<html lang=\"{lang}\">");
        html.AppendLine("<head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<meta name=\"generator\" content=\"BusChord\">");
        html.AppendLine($"<title>{Escape(title)} – {Escape(T("ProtocolTitle", lang))}</title>");
        html.AppendLine("<style>");
        AppendStyle(html, options.PaperSize, title, date, hash, lang);
        html.AppendLine("</style>");
        html.AppendLine("</head>");
        html.AppendLine("<body>");

        AppendHeader(html, project, options, title, date, hash, rows, items, lang);
        AppendSection(html, "topology", T("ProtocolTopology", lang), TopologyBody(rows, lang));
        AppendSection(html, "group-addresses", L("ReportGroupAddresses", lang), GroupAddressBody(project, lang));
        AppendSection(html, "function-matrix", T("ProtocolFunctionMatrix", lang), FunctionMatrixBody(project, lang));
        AppendSection(html, "device-tests", T("ProtocolDeviceTests", lang), DeviceTestBody(rows, lang));
        AppendSection(html, "open-items", T("ProtocolOpenItems", lang), OpenItemsBody(items, options, lang));
        AppendSection(html, "sign-off", T("ProtocolSignOff", lang), SignOffBody(options, lang));

        html.AppendLine($"<div class=\"page-footer\"><span>{Escape(title)}</span>" +
                        $"<span>sha256:{Escape(hash[..12])}…</span><span>{date}</span></div>");
        html.AppendLine("</body>");
        html.AppendLine("</html>");
        return html.ToString();
    }

    /// <summary>Writes the protocol as UTF-8 HTML without BOM, creating the target directory when needed.</summary>
    public static ReportFile Write(Project project, string path, AcceptanceProtocolOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var html = Render(project, options);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, html, new UTF8Encoding(false));
        return new ReportFile(full, new FileInfo(full).Length);
    }

    /// <summary>CSV of the per-device test rows (UTF-8 with BOM, semicolon-separated, formula-safe).</summary>
    public static string RenderCsv(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AcceptanceProtocolOptions();

        var lang = Language(options);
        var rows = DeviceRows(project, options);
        var sb = new StringBuilder();
        sb.Append(T("ProtocolCsvHeader", lang)).Append("\r\n");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var state = row.State;
            sb.Append(Csv.Row(';',
                (i + 1).ToString(CultureInfo.InvariantCulture),
                row.Device.Address.ToString(),
                row.Device.Name,
                row.Device.ProductRefId,
                row.Device.Hardware2ProgramRefId,
                Container(row),
                T("ProtocolNotTested", lang),
                T("ProtocolNotTested", lang),
                T("ProtocolNotTested", lang),
                T("ProtocolNotTested", lang),
                LoadState(state, lang),
                LoadFlags(row.Status.LoadedFlags),
                state?.LastAttemptAt is { } attempt ? Stamp(attempt) : "",
                state?.LastSuccessAt is { } success ? Stamp(success) : "",
                state?.LastError ?? "",
                "",
                "")).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Writes the CSV protocol (UTF-8 with BOM for spreadsheets).</summary>
    public static ReportFile WriteCsv(Project project, string path, AcceptanceProtocolOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var csv = RenderCsv(project, options);
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(full, csv, new UTF8Encoding(true));
        return new ReportFile(full, new FileInfo(full).Length);
    }

    /// <summary>Per-device rows ordered by individual address; the check columns stay open by design.</summary>
    public static IReadOnlyList<AcceptanceDeviceRow> DeviceRows(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AcceptanceProtocolOptions();

        var states = options.ProgrammingStates ?? new Dictionary<string, DeviceProgrammingState>();
        var statuses = DeviceProgrammingReport.Check(project, states)
            .ToDictionary(s => s.DeviceId, StringComparer.Ordinal);
        return ProjectQuery.Devices(project)
            .OrderBy(d => d.Device.Address.Raw)
            .ThenBy(d => d.Device.Id, StringComparer.Ordinal)
            .Select(d => new AcceptanceDeviceRow(d.Device, d.Installation, d.Area, d.Line, d.Segment,
                states.GetValueOrDefault(d.Device.Id), statuses[d.Device.Id]))
            .ToList();
    }

    /// <summary>
    /// Devices that are not proven loaded: no state, incomplete flags, signature drift or a failed
    /// last attempt. Empty when every device carries a complete, matching recorded state.
    /// </summary>
    public static IReadOnlyList<AcceptanceOpenItem> OpenItems(Project project, AcceptanceProtocolOptions? options = null)
    {
        options ??= new AcceptanceProtocolOptions();
        return OpenItems(DeviceRows(project, options));
    }

    /// <summary>Open items of an already built row list, in row order.</summary>
    private static List<AcceptanceOpenItem> OpenItems(IReadOnlyList<AcceptanceDeviceRow> rows)
    {
        var items = new List<AcceptanceOpenItem>();
        foreach (var row in rows)
        {
            var status = row.Status;
            if (status.LastError is not null)
                items.Add(new(AcceptanceOpenItemKind.LastError, row.Device, status));
            else if (status.SignatureDrift)
                items.Add(new(AcceptanceOpenItemKind.SignatureDrift, row.Device, status));
            else if (status.LoadedFlags != DeviceLoadFlags.All)
                items.Add(new(row.State is null
                    ? AcceptanceOpenItemKind.DownloadNotRecorded
                    : AcceptanceOpenItemKind.ProgrammingOutstanding, row.Device, status));
        }
        return items;
    }

    /// <summary>Counts, commissioning mode and document hash of the document these options produce.</summary>
    public static AcceptanceProtocolSummary Summarize(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AcceptanceProtocolOptions();
        var built = Build(project, options);
        return new AcceptanceProtocolSummary(built.Rows.Count, built.Items.Count,
            CommissioningMode(project, options), built.Hash);
    }

    /// <summary>
    /// What the recorded programming states prove: no successful load (not recorded), demo origin only
    /// (simulation) or at least one real successful load (real). Failed attempts never promote the
    /// document to a load. The document badge and the MCP result both use this classification.
    /// </summary>
    public static AcceptanceCommissioningMode CommissioningMode(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        options ??= new AcceptanceProtocolOptions();
        var successes = options.ProgrammingStates?.Values
            .Where(s => s.LastSuccessAt is not null)
            .ToList();
        if (successes is null || successes.Count == 0) return AcceptanceCommissioningMode.NotRecorded;
        if (successes.Any(s => s.Origin == ProgrammingOrigin.Real)) return AcceptanceCommissioningMode.Real;
        return successes.Any(s => s.Origin == ProgrammingOrigin.Demo)
            ? AcceptanceCommissioningMode.Simulation
            : AcceptanceCommissioningMode.NotRecorded;
    }

    /// <summary>
    /// Lowercase SHA-256 hex over the canonical document content: project identity, master-data
    /// signature, topology, devices, group addresses, links, recorded states and the sign-off
    /// inputs. Identical inputs produce the same hash on every run and machine.
    /// </summary>
    public static string ComputeHash(Project project, AcceptanceProtocolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Build(project, options ?? new AcceptanceProtocolOptions()).Hash;
    }

    private sealed record Built(
        IReadOnlyList<AcceptanceDeviceRow> Rows,
        IReadOnlyList<AcceptanceOpenItem> Items,
        string Hash);

    private static Built Build(Project project, AcceptanceProtocolOptions options)
    {
        var rows = DeviceRows(project, options);
        var items = OpenItems(rows);
        return new Built(rows, items, Hash(project, options, rows, items));
    }

    private static string Hash(Project project, AcceptanceProtocolOptions options,
        IReadOnlyList<AcceptanceDeviceRow> rows, IReadOnlyList<AcceptanceOpenItem> items)
    {
        var sb = new StringBuilder(16 * 1024);
        sb.Append("BusChord.AcceptanceProtocol/").Append(Version).Append('\n');
        sb.Append("language=").Append(Language(options)).Append('\n');
        sb.Append("title=").Append(Flat(options.Title)).Append('\n');
        sb.Append("generated=").Append(Format(options.GeneratedAt)).Append('\n');
        sb.Append("technician=").Append(Flat(options.Technician)).Append('\n');
        sb.Append("buildingOwner=").Append(Flat(options.BuildingOwner)).Append('\n');
        sb.Append("defectLines=").Append(options.DefectLines.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("project.id=").Append(Flat(project.Id)).Append('\n');
        sb.Append("project.name=").Append(Flat(project.Name)).Append('\n');
        sb.Append("project.comment=").Append(Flat(project.Comment)).Append('\n');
        sb.Append("project.start=").Append(Format(project.ProjectStart)).Append('\n');
        sb.Append("project.guid=").Append(Flat(project.Guid)).Append('\n');
        sb.Append("masterdata=").Append(Flat(project.MasterData.Id)).Append('/')
            .Append(project.MasterData.Version.ToString(CultureInfo.InvariantCulture)).Append('/')
            .Append(Flat(project.MasterData.Signature)).Append('\n');

        foreach (var row in rows)
        {
            sb.Append("device=").Append(Flat(row.Device.Id)).Append('|')
                .Append(row.Device.Address.ToString()).Append('|')
                .Append(Flat(row.Device.Name)).Append('|')
                .Append(Flat(row.Device.ProductRefId)).Append('|')
                .Append(Flat(row.Device.Hardware2ProgramRefId)).Append('|')
                .Append(Flat(Container(row))).Append('|')
                .Append(Flat(row.Device.Description)).Append('|')
                .Append(Flat(row.Device.Comment)).Append('\n');
            var state = row.State;
            sb.Append("state=").Append(Flat(state?.DeviceId)).Append('|')
                .Append((state?.Flags ?? DeviceLoadFlags.None).ToString()).Append('|')
                .Append(Flat(state?.ImageHash)).Append('|')
                .Append(Format(state?.LastAttemptAt)).Append('|')
                .Append(Format(state?.LastSuccessAt)).Append('|')
                .Append((state?.Attempts ?? 0).ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(Flat(state?.LastError)).Append('|')
                .Append(state?.Origin?.ToString() ?? "").Append('|')
                .Append(state?.LastAttemptOrigin?.ToString() ?? "").Append('\n');
            foreach (var comObject in row.Device.ComObjectRefs.OrderBy(o => o.Number).ThenBy(o => o.Id, StringComparer.Ordinal))
                sb.Append("object=").Append(Flat(row.Device.Id)).Append('|')
                    .Append(Flat(comObject.Id)).Append('|')
                    .Append(comObject.Number.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(Flat(comObject.Text)).Append('|')
                    .Append(Flat(comObject.FunctionText)).Append('|')
                    .Append(string.Join(' ', comObject.Links)).Append('\n');
        }

        foreach (var installation in project.Installations)
            foreach (var range in ProjectQuery.AllRanges(installation))
                foreach (var address in range.Addresses.OrderBy(a => a.Address.Raw))
                    sb.Append("ga=").Append(address.Address.ToString()).Append('|')
                        .Append(Flat(address.Name)).Append('|')
                        .Append(Flat(address.DatapointType)).Append('|')
                        .Append(Flat(address.Description)).Append('|')
                        .Append(Flat(address.Comment)).Append('|')
                        .Append(Flat(RangeLabel(range))).Append('\n');

        foreach (var item in items)
            sb.Append("open=").Append(item.Kind).Append('|')
                .Append(item.Device.Address.ToString()).Append('\n');

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- header and sections

    private static void AppendHeader(StringBuilder html, Project project, AcceptanceProtocolOptions options,
        string title, string date, string hash, IReadOnlyList<AcceptanceDeviceRow> rows,
        IReadOnlyList<AcceptanceOpenItem> items, string lang)
    {
        html.AppendLine("<header class=\"protocol-header\">");
        html.AppendLine($"<p class=\"kicker\">{Escape(T("ProtocolKicker", lang))}</p>");
        html.AppendLine($"<h1>{Escape(title)}</h1>");
        if (!string.IsNullOrWhiteSpace(project.Comment))
            html.AppendLine($"<p class=\"lead\">{Escape(project.Comment)}</p>");

        var mode = CommissioningMode(project, options);
        html.AppendLine($"<p class=\"mode {ModeClass(mode)}\">{Escape(ModeText(mode, lang))}</p>");

        html.AppendLine("<table class=\"facts\">");
        AppendFact(html, L("ReportFactId", lang), project.Id);
        AppendFact(html, L("ReportFactCreated", lang), date);
        AppendFact(html, T("ProtocolFactVersion", lang), Version.ToString(CultureInfo.InvariantCulture));
        if (project.ProjectStart is { } start)
            AppendFact(html, L("ReportFactStart", lang), start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(project.Guid)) AppendFact(html, L("ReportFactGuid", lang), project.Guid!);
        AppendFact(html, T("ProtocolFactMasterData", lang),
            $"{project.MasterData.Id} v{project.MasterData.Version.ToString(CultureInfo.InvariantCulture)}");
        AppendFact(html, T("ProtocolFactSignature", lang),
            string.IsNullOrWhiteSpace(project.MasterData.Signature)
                ? T("ProtocolSignatureMissing", lang)
                : $"{T("ProtocolSignaturePresent", lang)} ({project.MasterData.Signature})");
        AppendFact(html, T("ProtocolFactHash", lang), $"sha256:{hash}");
        html.AppendLine("</table>");

        html.AppendLine($"<p class=\"notice\">{Escape(T("ProtocolNotice", lang))}</p>");
        html.AppendLine($"<p class=\"counts\">{Escape(string.Format(CultureInfo.InvariantCulture,
            T("ProtocolCounts", lang), rows.Count, items.Count))}</p>");
        html.AppendLine("</header>");
    }

    private static string TopologyBody(IReadOnlyList<AcceptanceDeviceRow> rows, string lang)
    {
        var html = new StringBuilder();
        if (rows.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(L("ReportNoDevices", lang))}</p>");
            return html.ToString();
        }

        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(L("ReportColAddress", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColDevice", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColProductRef", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColProgram", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColContainer", lang))}</th></tr></thead><tbody>");
        foreach (var row in rows)
            html.AppendLine("<tr>" +
                            $"<td class=\"mono\">{row.Device.Address}</td>" +
                            $"<td>{Show(row.Device.Name)}</td>" +
                            $"<td class=\"mono\">{Show(row.Device.ProductRefId)}</td>" +
                            $"<td class=\"mono\">{Show(row.Device.Hardware2ProgramRefId)}</td>" +
                            $"<td>{Show(Container(row))}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string GroupAddressBody(Project project, string lang)
    {
        var html = new StringBuilder();
        var addresses = ProjectQuery.GroupAddresses(project)
            .OrderBy(a => a.Address.Raw).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
        if (addresses.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(L("ReportNoGroupAddresses", lang))}</p>");
            return html.ToString();
        }

        var rangeNames = new Dictionary<GroupAddressEntry, string>();
        foreach (var installation in project.Installations)
            foreach (var range in ProjectQuery.AllRanges(installation))
                foreach (var entry in range.Addresses)
                    rangeNames[entry] = RangeLabel(range);

        var links = ProjectQuery.ComObjectRefs(project)
            .SelectMany(r => r.Ref.Links)
            .GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(L("ReportColAddress", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColName", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColDpt", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColRange", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColLinks", lang))}</th></tr></thead><tbody>");
        foreach (var entry in addresses)
            html.AppendLine("<tr>" +
                            $"<td class=\"mono\">{entry.Address}</td>" +
                            $"<td>{Show(entry.Name)}</td>" +
                            $"<td class=\"mono\">{Show(entry.DatapointType)}</td>" +
                            $"<td>{Show(rangeNames.GetValueOrDefault(entry))}</td>" +
                            $"<td class=\"num\">{links.GetValueOrDefault(entry.Id)}</td>" +
                            "</tr>");
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string FunctionMatrixBody(Project project, string lang)
    {
        var html = new StringBuilder();
        var refs = ProjectQuery.ComObjectRefs(project)
            .OrderBy(r => r.Device.Address.Raw)
            .ThenBy(r => r.Ref.Number)
            .ThenBy(r => r.Ref.Id, StringComparer.Ordinal)
            .ToList();
        if (refs.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(L("ReportNoComObjects", lang))}</p>");
            return html.ToString();
        }

        var addresses = ProjectQuery.GroupAddresses(project)
            .ToDictionary(a => a.Id, a => $"{a.Address} {a.Name}".Trim(), StringComparer.Ordinal);
        html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(L("ReportColDevice", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColAddress", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColNumber", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColObject", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColFunctionText", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColLinkedGroupAddresses", lang))}</th></tr></thead><tbody>");
        foreach (var (device, comObject) in refs)
        {
            var links = comObject.Links.Count == 0
                ? Dash
                : string.Join(", ", comObject.Links.Select(id =>
                    addresses.TryGetValue(id, out var address)
                        ? address
                        : string.Format(CultureInfo.InvariantCulture, L("ReportUnknownReference", lang), id)));
            html.AppendLine("<tr>" +
                            $"<td>{Show(device.Name)}</td>" +
                            $"<td class=\"mono\">{device.Address}</td>" +
                            $"<td class=\"num\">{comObject.Number}</td>" +
                            $"<td>{Show(comObject.Text ?? comObject.ComObjectId)}</td>" +
                            $"<td>{Show(comObject.FunctionText)}</td>" +
                            $"<td>{Escape(links)}</td>" +
                            "</tr>");
        }
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string DeviceTestBody(IReadOnlyList<AcceptanceDeviceRow> rows, string lang)
    {
        var html = new StringBuilder();
        if (rows.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(L("ReportNoDevices", lang))}</p>");
            return html.ToString();
        }

        var box = CheckBox;
        html.AppendLine($"<p class=\"legend\">{Escape(T("ProtocolTestsLegend", lang))}</p>");
        html.AppendLine($"<table class=\"tests\"><thead><tr><th scope=\"col\">{Escape(L("ReportColNumber", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColAddress", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColDevice", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(L("ReportColProductRef", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColVisual", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColFunction", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColBus", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColParameters", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColDownload", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColMeasured", lang))}</th>" +
                        $"<th scope=\"col\">{Escape(T("ProtocolColRemark", lang))}</th></tr></thead><tbody>");
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            html.AppendLine("<tr>" +
                            $"<td class=\"num\">{(i + 1).ToString(CultureInfo.InvariantCulture)}</td>" +
                            $"<td class=\"mono\">{row.Device.Address}</td>" +
                            $"<td>{Show(row.Device.Name)}</td>" +
                            $"<td class=\"mono\">{Show(row.Device.ProductRefId)}</td>" +
                            $"<td class=\"check\">{box}</td>" +
                            $"<td class=\"check\">{box}</td>" +
                            $"<td class=\"check\">{box}</td>" +
                            $"<td class=\"check\">{box}</td>" +
                            $"<td class=\"load\">{LoadCell(row, lang)}</td>" +
                            "<td class=\"fill\"></td>" +
                            "<td class=\"fill\"></td>" +
                            "</tr>");
        }
        html.AppendLine("</tbody></table>");
        return html.ToString();
    }

    private static string OpenItemsBody(IReadOnlyList<AcceptanceOpenItem> items,
        AcceptanceProtocolOptions options, string lang)
    {
        var html = new StringBuilder();
        html.AppendLine($"<p>{Escape(T("ProtocolOpenItemsIntro", lang))}</p>");
        if (items.Count == 0)
        {
            html.AppendLine($"<p class=\"empty\">{Escape(T("ProtocolNoOpenItems", lang))}</p>");
        }
        else
        {
            html.AppendLine($"<table><thead><tr><th scope=\"col\">{Escape(L("ReportColNumber", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("ProtocolColKind", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(L("ReportColAddress", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(L("ReportColDevice", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("ProtocolColDetail", lang))}</th></tr></thead><tbody>");
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                html.AppendLine("<tr>" +
                                $"<td class=\"num\">{(i + 1).ToString(CultureInfo.InvariantCulture)}</td>" +
                                $"<td>{Escape(KindText(item.Kind, lang))}</td>" +
                                $"<td class=\"mono\">{item.Device.Address}</td>" +
                                $"<td>{Show(item.Device.Name)}</td>" +
                                $"<td>{Escape(DetailText(item, lang))}</td>" +
                                "</tr>");
            }
            html.AppendLine("</tbody></table>");
        }

        var lines = Math.Clamp(options.DefectLines, 0, 50);
        if (lines > 0)
        {
            html.AppendLine($"<h3>{Escape(T("ProtocolDefectList", lang))}</h3>");
            html.AppendLine($"<table class=\"defects\"><thead><tr><th scope=\"col\">{Escape(L("ReportColNumber", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("ProtocolColDefect", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("ProtocolColResolved", lang))}</th>" +
                            $"<th scope=\"col\">{Escape(T("ProtocolColRemark", lang))}</th></tr></thead><tbody>");
            for (var i = 1; i <= lines; i++)
                html.AppendLine("<tr>" +
                                $"<td class=\"num\">{i.ToString(CultureInfo.InvariantCulture)}</td>" +
                                "<td class=\"fill\"></td><td class=\"fill\"></td><td class=\"fill\"></td>" +
                                "</tr>");
            html.AppendLine("</tbody></table>");
        }
        return html.ToString();
    }

    private static string SignOffBody(AcceptanceProtocolOptions options, string lang)
    {
        var html = new StringBuilder();
        html.AppendLine($"<p>{Escape(T("ProtocolSignOffStatement", lang))}</p>");
        html.AppendLine("<table class=\"signoff\">");
        AppendSignRow(html, T("ProtocolSignPlaceDate", lang), null);
        AppendSignRow(html, T("ProtocolSignTechnician", lang), options.Technician);
        AppendSignRow(html, T("ProtocolSignOwner", lang), options.BuildingOwner);
        AppendSignRow(html, T("ProtocolSignSignature", lang), null);
        html.AppendLine("</table>");
        return html.ToString();
    }

    private static void AppendSignRow(StringBuilder html, string label, string? value)
        => html.AppendLine($"<tr><th scope=\"row\">{Escape(label)}</th>" +
                           (string.IsNullOrWhiteSpace(value)
                               ? "<td class=\"line\"></td>"
                               : $"<td class=\"line\">{Escape(value)}</td>") + "</tr>");

    // ---------------------------------------------------------------- download status

    private static string LoadCell(AcceptanceDeviceRow row, string lang)
    {
        var html = new StringBuilder();
        html.Append(Escape(LoadState(row.State, lang)));
        html.Append($"<br><span class=\"flags\">{Escape(T("ProtocolLoadFlags", lang))}: {Escape(LoadFlags(row.Status.LoadedFlags))}</span>");
        if (row.State?.LastSuccessAt is { } success)
            html.Append($"<br><span class=\"muted\">{Escape(T("ProtocolLastSuccess", lang))}: {Stamp(success)}</span>");
        if (row.State?.LastAttemptAt is { } attempt)
            html.Append($"<br><span class=\"muted\">{Escape(T("ProtocolLastAttempt", lang))}: {Stamp(attempt)}" +
                        $" ({row.State.Attempts.ToString(CultureInfo.InvariantCulture)})</span>");
        if (row.State?.LastError is { } error)
            html.Append($"<br><span class=\"muted\">{Escape(T("ProtocolLastError", lang))}: {Escape(error)}</span>");
        if (row.Status.SignatureDrift)
            html.Append($"<br><span class=\"muted\">{Escape(T("ProtocolDrift", lang))}</span>");
        return html.ToString();
    }

    private static string LoadState(DeviceProgrammingState? state, string lang)
    {
        if (state is null || (state.LastSuccessAt is null && state.LastError is null))
            return T("ProtocolLoadNotRecorded", lang);
        if (state.LastSuccessAt is null) return T("ProtocolLoadFailed", lang);
        var complete = (state.Flags & DeviceLoadFlags.All) == DeviceLoadFlags.All;
        return state.Origin == ProgrammingOrigin.Real
            ? T(complete ? "ProtocolLoadReal" : "ProtocolLoadPartialReal", lang)
            : T(complete ? "ProtocolLoadSimulation" : "ProtocolLoadPartialSimulation", lang);
    }

    /// <summary>Adr/Prg/Par/Grp/Cfg load flags with ✓/–.</summary>
    private static string LoadFlags(DeviceLoadFlags flags)
        => string.Join(" ", LoadFlagLabels.Select(f => f.Label + (flags.HasFlag(f.Flag) ? "✓" : "–")));

    /// <summary>Plain comma-separated labels of the flags a state does not cover.</summary>
    private static string MissingFlagList(DeviceLoadFlags flags)
        => string.Join(", ", LoadFlagLabels.Where(f => flags.HasFlag(f.Flag)).Select(f => f.Label));

    private static readonly (string Label, DeviceLoadFlags Flag)[] LoadFlagLabels =
    [
        ("Adr", DeviceLoadFlags.IndividualAddress),
        ("Prg", DeviceLoadFlags.ApplicationProgram),
        ("Par", DeviceLoadFlags.Parameters),
        ("Grp", DeviceLoadFlags.GroupAddresses),
        ("Cfg", DeviceLoadFlags.Configuration),
    ];

    private static string DetailText(AcceptanceOpenItem item, string lang) => item.Kind switch
    {
        AcceptanceOpenItemKind.DownloadNotRecorded => T("ProtocolDetailNotRecorded", lang),
        AcceptanceOpenItemKind.ProgrammingOutstanding => string.Format(CultureInfo.InvariantCulture,
            T("ProtocolMissingFlags", lang), MissingFlagList(item.Status.MissingFlags)),
        AcceptanceOpenItemKind.SignatureDrift => T("ProtocolDetailDrift", lang),
        _ => $"{T("ProtocolLastError", lang)}: {item.Status.LastError}",
    };

    private static string KindText(AcceptanceOpenItemKind kind, string lang) => kind switch
    {
        AcceptanceOpenItemKind.DownloadNotRecorded => T("ProtocolKindNotRecorded", lang),
        AcceptanceOpenItemKind.ProgrammingOutstanding => T("ProtocolKindOutstanding", lang),
        AcceptanceOpenItemKind.SignatureDrift => T("ProtocolKindDrift", lang),
        _ => L("ReportSeverityError", lang),
    };

    private static string ModeText(AcceptanceCommissioningMode mode, string lang) => mode switch
    {
        AcceptanceCommissioningMode.Real => T("ProtocolModeReal", lang),
        AcceptanceCommissioningMode.Simulation => T("ProtocolModeSimulation", lang),
        _ => T("ProtocolModeNone", lang),
    };

    private static string ModeClass(AcceptanceCommissioningMode mode) => mode switch
    {
        AcceptanceCommissioningMode.Real => "mode-real",
        AcceptanceCommissioningMode.Simulation => "mode-sim",
        _ => "mode-none",
    };

    // ---------------------------------------------------------------- layout

    private static void AppendStyle(StringBuilder html, string paperSize, string title, string date, string hash, string lang)
    {
        var paper = paperSize.ToUpperInvariant() switch
        {
            "A3" => "A3",
            "LETTER" => "Letter",
            _ => "A4",
        };

        html.AppendLine($"  @page {{ size: {paper} landscape; margin: 16mm 12mm 18mm;");
        html.AppendLine($"    @top-left {{ content: \"{Css(title)}\"; font-size: 8pt; color: #555; }}");
        html.AppendLine($"    @top-right {{ content: \"sha256:{Css(hash[..12])}…\"; font-size: 8pt; color: #555; }}");
        html.AppendLine($"    @bottom-left {{ content: \"{Css(date)}\"; font-size: 8pt; color: #555; }}");
        html.AppendLine($"    @bottom-right {{ content: \"{Css(L("ReportPageOf", lang))}\" counter(page) \" / \" counter(pages); font-size: 8pt; color: #555; }}");
        html.AppendLine("  }");
        html.AppendLine(StyleSheet);
    }

    private const string StyleSheet = """
          html { background: #f4f4f4; }
          body { margin: 0 auto; padding: 14mm; max-width: 297mm; background: #fff; color: #111;
                 font-family: "Segoe UI", Arial, sans-serif; font-size: 9.5pt; line-height: 1.4; }
          h1 { font-size: 22pt; margin: 0 0 3mm 0; line-height: 1.15; }
          h2 { font-size: 15pt; margin: 0 0 4mm 0; padding-bottom: 1.5mm; border-bottom: 0.4mm solid #333; }
          h3 { font-size: 11.5pt; margin: 6mm 0 2mm 0; }
          p { margin: 0 0 3mm 0; }
          .kicker { font-size: 9pt; text-transform: uppercase; letter-spacing: 0.15em; color: #666; margin: 0 0 2mm 0; }
          .lead { font-size: 11pt; color: #333; }
          .mode { display: inline-block; padding: 1.5mm 3mm; border-radius: 1.5mm; font-weight: 600;
                  border: 0.3mm solid #666; margin: 0 0 4mm 0; }
          .mode-sim { background: #fff3d6; }
          .mode-real { background: #e2f3e2; }
          .mode-none { background: #f0f0f0; color: #555; }
          .notice { font-size: 8.5pt; color: #444; border-left: 0.8mm solid #bbb; padding: 1mm 0 1mm 3mm; }
          .legend { font-size: 8.5pt; color: #444; }
          .counts { font-size: 9pt; color: #333; }
          .protocol-section { page-break-before: always; break-before: page; }
          table { width: 100%; border-collapse: collapse; font-size: 8.5pt; margin: 0 0 4mm 0; }
          th, td { text-align: left; padding: 1.2mm 2mm; border-bottom: 0.2mm solid #ccc; vertical-align: top; }
          thead { display: table-header-group; }
          tr { page-break-inside: avoid; break-inside: avoid; }
          th { background: #f2f2f2; color: #222; font-weight: 600; }
          .mono { font-family: Consolas, "Courier New", monospace; white-space: nowrap; }
          .num { text-align: right; font-variant-numeric: tabular-nums; }
          .check { color: #555; text-align: center; white-space: nowrap; font-size: 13pt; line-height: 1; }
          .load { min-width: 52mm; }
          .fill { height: 9mm; }
          .flags { font-family: Consolas, "Courier New", monospace; font-size: 7.5pt; color: #444; white-space: nowrap; }
          .muted { color: #555; font-size: 7.5pt; }
          .empty { color: #555; font-style: italic; }
          .facts { width: auto; min-width: 70%; }
          .facts th { width: 45mm; background: none; font-weight: 600; }
          .defects th:first-child, .defects td:first-child { width: 12mm; }
          .defects th:nth-child(2) { width: 50%; }
          .signoff { width: 80%; }
          .signoff th { width: 45mm; background: none; font-weight: 600; border-bottom: none; vertical-align: bottom; }
          .signoff td { height: 14mm; border-bottom: 0.3mm solid #333; vertical-align: bottom; }
          .page-footer { display: none; }
          @media print {
            html, body { background: #fff; }
            body { margin: 0; padding: 0; max-width: none; }
            .page-footer { display: block; position: fixed; left: 0; right: 0; bottom: 0;
                           font-size: 8pt; color: #555; border-top: 0.2mm solid #bbb; padding-top: 1mm; }
            .page-footer span:last-child { float: right; }
          }
        """;

    // ---------------------------------------------------------------- helpers

    private const string Dash = "—";

    /// <summary>Open check box of the on-site test columns; the legend explains ✓/✗.</summary>
    private const string CheckBox = "☐";

    private static string Language(AcceptanceProtocolOptions options) => options.Language == "en" ? "en" : "de";

    private static string Stamp(DateTimeOffset value)
        => value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Format(DateTimeOffset? value)
        => value is { } time ? time.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) : "";

    /// <summary>One canonical line value: newlines/tabs collapse so a value cannot forge a line.</summary>
    private static string Flat(string? value)
        => value is null ? "" : value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static string Show(string? value)
        => string.IsNullOrWhiteSpace(value) ? Dash : Escape(value);

    private static string RangeLabel(GroupRange range)
        => string.IsNullOrWhiteSpace(range.Name)
            ? $"{range.RangeStart}..{range.RangeEnd}"
            : $"{range.Name} ({range.RangeStart}..{range.RangeEnd})";

    private static string Container(AcceptanceDeviceRow row)
        => string.Join(" · ", new[] { row.Installation.Name, row.Area.Name, row.Line?.Name, row.Segment?.Name }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    private static void AppendFact(StringBuilder html, string label, string value)
        => html.AppendLine($"<tr><th scope=\"row\">{Escape(label)}</th><td>{Escape(value)}</td></tr>");

    private static void AppendSection(StringBuilder html, string id, string heading, string body)
    {
        html.AppendLine($"<section class=\"protocol-section\" id=\"{id}\">");
        html.AppendLine($"<h2>{Escape(heading)}</h2>");
        html.Append(body);
        html.AppendLine("</section>");
    }

    /// <summary>Escapes every user value for HTML text and attribute context.</summary>
    private static string Escape(string? value)
        => value is null
            ? ""
            : value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&#39;");

    /// <summary>Escapes a value for use inside a CSS string literal in the inline stylesheet.</summary>
    private static string Css(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r':
                case '\n': sb.Append(' '); break;
                case '<': sb.Append("\\3C "); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Protocol text; de/en pairs live here because the core dictionary is not in this feature's scope.</summary>
    private static string T(string key, string lang)
    {
        var entry = Text[key];
        return lang == "en" ? entry.En : entry.De;
    }

    /// <summary>Shared report label from the core dictionary, resolved for the document language.</summary>
    private static string L(string key, string lang)
        => Strings.Catalogue.TryGetValue(key, out var entry)
            ? lang == "en" ? entry.En : entry.De
            : key;

    private static readonly Dictionary<string, (string De, string En)> Text = new(StringComparer.Ordinal)
    {
        ["ProtocolTitle"] = ("Abnahmeprotokoll", "Acceptance protocol"),
        ["ProtocolKicker"] = ("Inbetriebnahme · Abnahme", "Commissioning · acceptance"),
        ["ProtocolCounts"] = ("{0} Geräte · {1} offene Punkte", "{0} devices · {1} open items"),
        ["ProtocolFactMasterData"] = ("Stammdaten", "Master data"),
        ["ProtocolFactVersion"] = ("Dokumentversion", "Document version"),
        ["ProtocolFactSignature"] = ("Signatur", "Signature"),
        ["ProtocolSignaturePresent"] = ("vorhanden, unverändert übernommen", "present, preserved unchanged"),
        ["ProtocolSignatureMissing"] = ("fehlt", "missing"),
        ["ProtocolFactHash"] = ("Dokument-Hash", "Document hash"),
        ["ProtocolModeNone"] = ("Kein Download aufgezeichnet", "No download recorded"),
        ["ProtocolModeSimulation"] = ("Simulation – aufgezeichnete Demo-Ladungen", "Simulation – recorded demo loads"),
        ["ProtocolModeReal"] = ("Real – aufgezeichnete Ladungen", "Real – recorded loads"),
        ["ProtocolNotice"] = (
            "Dieses Dokument gibt den Projektstand wieder. Prüfspalten stehen auf „nicht geprüft“, solange kein Nachweis vorliegt." + " " +
            "Ein Download gilt nur dann als belegt, wenn ein aufgezeichneter Ladezustand mit Herkunft „real“ vorliegt; Demo-Ladungen sind Simulation." + " " +
            "Das Protokoll ersetzt keine Messung vor Ort.",
            "This document reflects the project data. Test columns stay \"not tested\" unless evidence exists. " +
            "A download counts as proven only when a recorded load state with origin \"real\" exists; demo loads are simulation. " +
            "The protocol does not replace on-site measurement."),
        ["ProtocolTopology"] = ("Anlage und Topologie", "Installation and topology"),
        ["ProtocolColContainer"] = ("Anlage/Bereich/Linie/Segment", "Installation/area/line/segment"),
        ["ProtocolColProgram"] = ("Programm-Ref", "Program reference"),
        ["ProtocolFunctionMatrix"] = ("Funktionsmatrix", "Function matrix"),
        ["ProtocolColFunctionText"] = ("Funktionstext", "Function text"),
        ["ProtocolDeviceTests"] = ("Geräteprüfung", "Device tests"),
        ["ProtocolTestsLegend"] = (
            "Je Gerät eine Zeile. Prüfspalten vor Ort ausfüllen: ☐ nicht geprüft, ✓ in Ordnung, ✗ Mangel; Messwert und Bemerkung frei eintragen.",
            "One row per device. Fill the check columns on site: ☐ not tested, ✓ ok, ✗ defect; enter measured value and remark freely."),
        ["ProtocolColVisual"] = ("Sichtprüfung", "Visual check"),
        ["ProtocolColFunction"] = ("Funktion", "Function"),
        ["ProtocolColBus"] = ("Bus", "Bus"),
        ["ProtocolColParameters"] = ("Parameter", "Parameters"),
        ["ProtocolColDownload"] = ("Download", "Download"),
        ["ProtocolColMeasured"] = ("Messwert", "Measured value"),
        ["ProtocolColRemark"] = ("Bemerkung", "Remark"),
        ["ProtocolNotTested"] = ("nicht geprüft", "not tested"),
        ["ProtocolLoadNotRecorded"] = ("kein Download aufgezeichnet", "no download recorded"),
        ["ProtocolLoadFailed"] = ("Download fehlgeschlagen", "download failed"),
        ["ProtocolLoadSimulation"] = ("Simulation (Demo)", "simulation (demo)"),
        ["ProtocolLoadPartialSimulation"] = ("Simulation (Demo), teilgeladen", "simulation (demo), partially loaded"),
        ["ProtocolLoadReal"] = ("real geladen", "loaded for real"),
        ["ProtocolLoadPartialReal"] = ("real teilgeladen", "partially loaded (real)"),
        ["ProtocolLoadFlags"] = ("Ladeflags", "Load flags"),
        ["ProtocolLastAttempt"] = ("Letzter Versuch", "Last attempt"),
        ["ProtocolLastSuccess"] = ("Letzter Erfolg", "Last success"),
        ["ProtocolLastError"] = ("Letzter Fehler", "Last error"),
        ["ProtocolDrift"] = ("Konfiguration seit der Ladung geändert", "configuration changed since the load"),
        ["ProtocolOpenItems"] = ("Offene Punkte und Mängelliste", "Open items and defect list"),
        ["ProtocolOpenItemsIntro"] = (
            "Punkte aus dem Projektstand und den aufgezeichneten Ladungen; die Mängelliste darunter ist für die Vor-Ort-Aufnahme.",
            "Items derived from the project data and the recorded loads; the defect list below is for on-site notes."),
        ["ProtocolNoOpenItems"] = ("Keine offenen Punkte aus dem Projektstand.", "No open items from the project data."),
        ["ProtocolColKind"] = ("Art", "Kind"),
        ["ProtocolColDetail"] = ("Detail", "Detail"),
        ["ProtocolKindNotRecorded"] = ("Download fehlt", "Download missing"),
        ["ProtocolKindOutstanding"] = ("Programmierung offen", "Programming outstanding"),
        ["ProtocolKindDrift"] = ("Konfiguration geändert", "Configuration changed"),
        ["ProtocolMissingFlags"] = ("Fehlende Ladeflags: {0}", "Missing load flags: {0}"),
        ["ProtocolDetailNotRecorded"] = ("Kein Ladezustand für dieses Gerät aufgezeichnet.", "No load state recorded for this device."),
        ["ProtocolDetailDrift"] = ("Gespeicherter Stand weicht vom Projektstand ab.", "Recorded state differs from the project state."),
        ["ProtocolDefectList"] = ("Mängelliste", "Defect list"),
        ["ProtocolColDefect"] = ("Mangel", "Defect"),
        ["ProtocolColResolved"] = ("Behoben am", "Resolved on"),
        ["ProtocolSignOff"] = ("Abnahme und Unterschriften", "Acceptance and signatures"),
        ["ProtocolSignOffStatement"] = ("Mit der Unterschrift wird die Abnahme der aufgeführten Leistungen bestätigt.",
            "By signing, the acceptance of the listed works is confirmed."),
        ["ProtocolSignPlaceDate"] = ("Ort, Datum", "Place, date"),
        ["ProtocolSignTechnician"] = ("Techniker/in", "Technician"),
        ["ProtocolSignOwner"] = ("Bauherr/Bauherrin", "Building owner"),
        ["ProtocolSignSignature"] = ("Unterschrift", "Signature"),
        ["ProtocolCsvHeader"] = (
            "Nr.;Adresse;Gerät;Produkt-Ref;Programm-Ref;Anlage/Bereich/Linie/Segment;Sichtprüfung;Funktion;Bus;" +
            "Parameter;Download;Ladeflags;Letzter Versuch;Letzter Erfolg;Fehler;Messwert;Bemerkung",
            "No.;Address;Device;Product reference;Program reference;Installation/area/line/segment;Visual check;Function;Bus;" +
            "Parameters;Download;Load flags;Last attempt;Last success;Error;Measured value;Remark"),
    };
}

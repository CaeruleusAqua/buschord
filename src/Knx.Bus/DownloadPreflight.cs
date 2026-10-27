using System.Security.Cryptography;
using System.Text;
using Knx;

namespace Knx.Bus;

/// <summary>Severity of one preflight finding.</summary>
public enum DownloadCheckSeverity { Blocker, Warning, Info }

/// <summary>Stable codes of the download preflight checks; MCP output uses their snake_case form.</summary>
public enum DownloadCheckCode
{
    DescriptorMissing,
    DescriptorTypeNotZero,
    MaskUnknown,
    MaskKnown,
    ProgrammingModeMissing,
    ProgrammingModeOn,
    AddressInvalid,
    AddressLineMismatch,
    AddressNotConfirmed,
    LineNotVerified,
    ProgramMissing,
    ProgramMismatch,
    ImageEmpty,
    ChunkSizeInvalid,
    ImageRangeInvalid,
    ParametersIncomplete,
    LinksIncomplete,
    DryRunNotConfirmed,
    WritesDisabled,
    Ready,
}

/// <summary>One preflight finding: stable code, severity and a human message.</summary>
public sealed record DownloadCheck(DownloadCheckCode Code, DownloadCheckSeverity Severity, string Message);

/// <summary>One planned memory-write chunk: target address and payload length.</summary>
public sealed record DownloadPlanChunk(ushort Address, int Length);

/// <summary>
/// Everything the planner may look at. Every field is provided by the caller; the planner
/// performs no I/O, so a plan is deterministic and testable without a bus.
/// </summary>
public sealed record DownloadPreflightRequest
{
    public required IndividualAddress DeviceAddress { get; init; }

    /// <summary>The application program image a future download would flash; null when none is built.</summary>
    public IApplicationProgram? Program { get; init; }

    /// <summary>Descriptor read from the device before planning (type 0 carries the mask version).</summary>
    public DeviceDescriptor? Descriptor { get; init; }

    /// <summary>Device is in programming mode (observed or confirmed on site).</summary>
    public bool ProgrammingMode { get; init; }

    /// <summary>The operator confirmed the target line and individual address.</summary>
    public bool AddressConfirmed { get; init; }

    /// <summary>The operator reviewed the printed dry run (image and target addresses).</summary>
    public bool DryRunConfirmed { get; init; }

    /// <summary>Line the project expects for the device; used for the address/line check.</summary>
    public byte? ExpectedArea { get; init; }
    public byte? ExpectedLine { get; init; }

    /// <summary>Application/program reference the project expects for this device.</summary>
    public string? ExpectedApplicationId { get; init; }

    /// <summary>Application/program reference the image was built for.</summary>
    public string? ApplicationId { get; init; }

    /// <summary>Parameter names that are not configured; empty when complete or not verifiable.</summary>
    public IReadOnlyList<string> MissingParameters { get; init; } = [];

    /// <summary>Link descriptions that are missing; empty when complete or not verifiable.</summary>
    public IReadOnlyList<string> MissingLinks { get; init; } = [];
}

/// <summary>
/// Deterministic result of <see cref="DownloadPreflight.Plan"/>: the computed image chunks, the
/// findings and a printable dry run. Only the planner creates a plan, so a download session can
/// treat a ready plan as the product of the checks below.
/// </summary>
public sealed record DownloadPlan
{
    internal DownloadPlan(IndividualAddress deviceAddress, string? programName, string? applicationId,
        string? imageHash, int imageLength, ushort startAddress, int chunkSize, ushort? maskVersion,
        IReadOnlyList<DownloadPlanChunk> chunks, IReadOnlyList<DownloadCheck> checks)
    {
        DeviceAddress = deviceAddress;
        ProgramName = programName;
        ApplicationId = applicationId;
        ImageHash = imageHash;
        ImageLength = imageLength;
        StartAddress = startAddress;
        ChunkSize = chunkSize;
        MaskVersion = maskVersion;
        Chunks = chunks;
        Checks = checks;
    }

    public IndividualAddress DeviceAddress { get; }
    public string? ProgramName { get; }
    public string? ApplicationId { get; }

    /// <summary>SHA-256 of the image as uppercase hex, or null when no image was planned.</summary>
    public string? ImageHash { get; }
    public int ImageLength { get; }
    public ushort StartAddress { get; }
    public int ChunkSize { get; }

    /// <summary>Mask version from the descriptor read, or null when it was not available.</summary>
    public ushort? MaskVersion { get; }

    public IReadOnlyList<DownloadPlanChunk> Chunks { get; }
    public IReadOnlyList<DownloadCheck> Checks { get; }

    public bool Ready => Checks.All(check => check.Severity != DownloadCheckSeverity.Blocker);
    public IReadOnlyList<DownloadCheck> Blockers => Checks.Where(c => c.Severity == DownloadCheckSeverity.Blocker).ToList();
    public IReadOnlyList<DownloadCheck> Warnings => Checks.Where(c => c.Severity == DownloadCheckSeverity.Warning).ToList();

    /// <summary>The dry run: image hash, every target address and the findings, in a fixed order.</summary>
    public string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine($"download dry run for {DeviceAddress} ({ProgramName ?? "no program"})");
        if (ImageHash is not null)
        {
            text.AppendLine($"  image: {ImageLength} bytes, sha256 {ImageHash}, start 0x{StartAddress:X4}, chunk size {ChunkSize}");
            foreach (var chunk in Chunks)
                text.AppendLine($"  write 0x{chunk.Address:X4}: {chunk.Length} byte(s)");
        }

        text.AppendLine($"  mask: {(MaskVersion is { } mask ? $"0x{mask:X4} ({MaskVersions.Describe(mask)})" : "unknown")}");
        text.AppendLine($"  checks: {Blockers.Count} blocker(s), {Warnings.Count} warning(s)");
        foreach (var check in Checks)
            text.AppendLine($"    {check.Severity.ToString().ToUpperInvariant()} {check.Code}: {check.Message}");
        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// Read-only preflight and dry-run planner for a future device download. It never touches a bus;
/// callers supply the descriptor read, the programming-mode state and the confirmations. The real
/// download path (<see cref="KnxBus.DownloadAsync"/>, <see cref="DownloadSession"/>) stays disabled
/// and refuses to run without a ready plan.
/// </summary>
public static class DownloadPreflight
{
    /// <summary>SHA-256 of an image as uppercase hex; the plan and the attempt log use it.</summary>
    public static string ImageHash(byte[] image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Convert.ToHexString(SHA256.HashData(image));
    }

    /// <summary>Runs every check in a fixed order and returns the plan; pure, no I/O.</summary>
    public static DownloadPlan Plan(DownloadPreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checks = new List<DownloadCheck>();
        var address = request.DeviceAddress;

        // 1. Descriptor, mask version and management model (the mask names the memory model).
        ushort? maskVersion = null;
        if (request.Descriptor is not { } descriptor)
        {
            checks.Add(Blocker(DownloadCheckCode.DescriptorMissing,
                "no descriptor was read from the device; the mask and memory model are unknown"));
        }
        else if (descriptor.Type != 0)
        {
            checks.Add(Blocker(DownloadCheckCode.DescriptorTypeNotZero,
                $"descriptor type {descriptor.Type} was read; the mask version requires descriptor type 0"));
        }
        else if (descriptor.MaskVersion is not { } mask)
        {
            checks.Add(Blocker(DownloadCheckCode.MaskUnknown, "descriptor type 0 carried no mask version"));
        }
        else
        {
            maskVersion = mask;
            checks.Add(MaskVersions.IsKnown(mask)
                ? Info(DownloadCheckCode.MaskKnown, $"mask 0x{mask:X4} ({MaskVersions.Describe(mask)}) is known")
                : Blocker(DownloadCheckCode.MaskUnknown,
                    $"mask 0x{mask:X4} is not a known mask version; a download must not guess a memory map"));
        }

        // 2. Programming mode is a hard prerequisite; it cannot be queried read-only in this build.
        checks.Add(request.ProgrammingMode
            ? Info(DownloadCheckCode.ProgrammingModeOn, "device is in programming mode")
            : Blocker(DownloadCheckCode.ProgrammingModeMissing,
                "device is not in programming mode; a download must not start without it"));

        // 3. Address and line validity plus the operator confirmation.
        if (address.Device is 0 or 255)
            checks.Add(Blocker(DownloadCheckCode.AddressInvalid,
                $"{address} is not a device address (device part must be 1..254)"));
        if (request.ExpectedArea is { } area && request.ExpectedLine is { } line)
        {
            if (address.Area != area || address.Line != line)
                checks.Add(Blocker(DownloadCheckCode.AddressLineMismatch,
                    $"{address} is not on the line {area}.{line} the project expects"));
        }
        else
        {
            checks.Add(new DownloadCheck(DownloadCheckCode.LineNotVerified, DownloadCheckSeverity.Warning,
                "no expected line was provided; the address/line was not verified against the project"));
        }
        if (!request.AddressConfirmed)
            checks.Add(Blocker(DownloadCheckCode.AddressNotConfirmed,
                "the target line and individual address were not confirmed"));

        // 4. Application/program match and the image the download would write.
        string? imageHash = null;
        var imageLength = 0;
        var startAddress = 0;
        var chunkSize = 0;
        var chunks = new List<DownloadPlanChunk>();
        if (request.Program is not { } program)
        {
            checks.Add(Blocker(DownloadCheckCode.ProgramMissing,
                "no application program image is available; the image build is not implemented"));
        }
        else
        {
            imageLength = program.Image.Length;
            startAddress = program.StartAddress;
            chunkSize = program.ChunkSize;
            if (imageLength == 0)
                checks.Add(Blocker(DownloadCheckCode.ImageEmpty, $"program \"{program.Name}\" has an empty image"));
            else
                imageHash = ImageHash(program.Image);

            var rangeFits = imageLength > 0 && (long)program.StartAddress + imageLength <= 0x10000;
            if (chunkSize is < 1 or > 12)
                checks.Add(Blocker(DownloadCheckCode.ChunkSizeInvalid,
                    $"chunk size {chunkSize} does not fit a memory-write frame (1..12 bytes)"));
            else if (imageLength > 0 && !rangeFits)
                checks.Add(Blocker(DownloadCheckCode.ImageRangeInvalid,
                    $"image range 0x{program.StartAddress:X4}+{imageLength} exceeds the 16-bit address space"));

            if (imageLength > 0 && chunkSize is >= 1 and <= 12 && rangeFits)
                chunks = PlanChunks(program);

            if (request.ExpectedApplicationId is { Length: > 0 } expected
                && request.ApplicationId is { Length: > 0 } actual
                && !string.Equals(expected, actual, StringComparison.Ordinal))
                checks.Add(Blocker(DownloadCheckCode.ProgramMismatch,
                    $"the image is for \"{actual}\" but the project device expects \"{expected}\""));
        }

        // 5. Parameter and link completeness as reported by the caller.
        if (request.MissingParameters.Count > 0)
            checks.Add(Blocker(DownloadCheckCode.ParametersIncomplete,
                "incomplete parameters: " + string.Join(", ", request.MissingParameters.OrderBy(n => n, StringComparer.Ordinal))));
        if (request.MissingLinks.Count > 0)
            checks.Add(Blocker(DownloadCheckCode.LinksIncomplete,
                "incomplete links: " + string.Join(", ", request.MissingLinks.OrderBy(n => n, StringComparer.Ordinal))));

        // 6. Required confirmations and the read-only policy note.
        if (!request.DryRunConfirmed)
            checks.Add(Blocker(DownloadCheckCode.DryRunNotConfirmed,
                "the computed image and target addresses were not reviewed (dry run not confirmed)"));
        checks.Add(Info(DownloadCheckCode.WritesDisabled,
            "real downloads are disabled by policy (KnxBus.WritesEnabled is false); this plan is a dry run only"));

        if (checks.All(check => check.Severity != DownloadCheckSeverity.Blocker))
            checks.Add(Info(DownloadCheckCode.Ready, "all preflight checks passed; the plan is ready for a future gated download"));

        return new DownloadPlan(address, request.Program?.Name, request.ApplicationId, imageHash, imageLength,
            (ushort)startAddress, chunkSize, maskVersion, chunks, checks);
    }

    private static List<DownloadPlanChunk> PlanChunks(IApplicationProgram program)
    {
        var chunks = new List<DownloadPlanChunk>();
        for (var offset = 0; offset < program.Image.Length; offset += program.ChunkSize)
            chunks.Add(new DownloadPlanChunk((ushort)(program.StartAddress + offset),
                Math.Min(program.ChunkSize, program.Image.Length - offset)));
        return chunks;
    }

    private static DownloadCheck Blocker(DownloadCheckCode code, string message)
        => new(code, DownloadCheckSeverity.Blocker, message);

    private static DownloadCheck Info(DownloadCheckCode code, string message)
        => new(code, DownloadCheckSeverity.Info, message);
}

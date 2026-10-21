using System.Text.Json;
using System.Text.Json.Serialization;

namespace Knx.Workbench;

/// <summary>
/// Load flags of one device. Mirrors the ETS programming-state letters Adr/Prg/Par/Grp/Cfg;
/// the letter mapping is inferred from the analysed ETS member names (analysis/ets/commissioning.md §2).
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceLoadFlags
{
    None = 0,
    /// <summary>Adr: individual address / network address is loaded.</summary>
    IndividualAddress = 1 << 0,
    /// <summary>Prg: application program is loaded.</summary>
    ApplicationProgram = 1 << 1,
    /// <summary>Par: parameters are loaded.</summary>
    Parameters = 1 << 2,
    /// <summary>Grp: group communication tables / links are loaded (ETS: CommunicationPartLoaded).</summary>
    GroupAddresses = 1 << 3,
    /// <summary>Cfg: medium/network configuration is loaded (ETS: MediumConfigLoaded).</summary>
    Configuration = 1 << 4,
    All = IndividualAddress | ApplicationProgram | Parameters | GroupAddresses | Configuration,
}

/// <summary>Bus-relevant change classification; maps to the load flags a change invalidates.</summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeviceChangeKind
{
    None = 0,
    /// <summary>Labels only; never invalidates a load flag.</summary>
    Name = 1 << 0,
    /// <summary>Building/room placement only; never invalidates a load flag.</summary>
    Room = 1 << 1,
    IndividualAddress = 1 << 2,
    /// <summary>Move between area/line/segment; the individual address becomes stale.</summary>
    Topology = 1 << 3,
    /// <summary>Product or application-program reference; app, parameters and links become stale.</summary>
    Product = 1 << 4,
    Parameters = 1 << 5,
    GroupAddressLink = 1 << 6,
    /// <summary>Medium/network configuration only.</summary>
    Configuration = 1 << 7,
}

/// <summary>Explicit provenance of a recorded load. Real execution is not implemented.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProgrammingOrigin { Demo, Real }

/// <summary>
/// What is known to be loaded into one device, independent of project undo and project dirty state.
/// <see cref="ImageHash"/> is the frozen configuration signature used as an image/program hash until a
/// real image builder exists; it is not a downloadable device image.
/// </summary>
public sealed record DeviceProgrammingState(
    string DeviceId,
    DeviceLoadFlags Flags,
    string? ImageHash,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    int Attempts,
    string? LastError,
    string? InterfaceId,
    ProgrammingOrigin? Origin,
    ProgrammingOrigin? LastAttemptOrigin)
{
    public static DeviceProgrammingState Empty(string deviceId) => new(deviceId,
        DeviceLoadFlags.None, null, null, null, 0, null, null, null, null);

    /// <summary>True only when a success was recorded and every load flag is set.</summary>
    [JsonIgnore] public bool IsFullyLoaded => LastSuccessAt is not null && Flags == DeviceLoadFlags.All;

    /// <summary>Conservative drift check: an incomplete load also needs programming.</summary>
    public bool NeedsProgramming(string currentSignature) => !IsFullyLoaded || ImageHash != currentSignature;

    /// <summary>Successful attempt. Partial loads keep the previously loaded flags.</summary>
    public DeviceProgrammingState ApplySuccess(DeviceLoadFlags flags, string imageHash, DateTimeOffset at,
        string interfaceId, ProgrammingOrigin origin)
    {
        if (flags == DeviceLoadFlags.None || (flags & ~DeviceLoadFlags.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(flags), "A successful load must name at least one known load flag.");
        ArgumentException.ThrowIfNullOrWhiteSpace(imageHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        return this with
        {
            Flags = Flags | flags,
            ImageHash = imageHash,
            LastAttemptAt = at,
            LastSuccessAt = at,
            Attempts = Attempts + 1,
            LastError = null,
            InterfaceId = interfaceId,
            Origin = origin,
            LastAttemptOrigin = origin,
        };
    }

    /// <summary>Failed attempt: records time/error/interface and never touches the success state.</summary>
    public DeviceProgrammingState ApplyFailure(string error, DateTimeOffset at, string interfaceId,
        ProgrammingOrigin origin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        return this with
        {
            LastAttemptAt = at,
            Attempts = Attempts + 1,
            LastError = error,
            InterfaceId = interfaceId,
            LastAttemptOrigin = origin,
        };
    }

    /// <summary>Clears exactly the flags invalidated by the classified change; keeps success history.</summary>
    public DeviceProgrammingState ResetForChange(DeviceChangeKind change)
    {
        var affected = AffectedFlags(change);
        return affected == DeviceLoadFlags.None ? this : this with { Flags = Flags & ~affected };
    }

    /// <summary>Deterministic change-to-flag mapping. Name/room edits never invalidate a flag.</summary>
    public static DeviceLoadFlags AffectedFlags(DeviceChangeKind change)
    {
        const DeviceChangeKind known = DeviceChangeKind.Name | DeviceChangeKind.Room |
            DeviceChangeKind.IndividualAddress | DeviceChangeKind.Topology | DeviceChangeKind.Product |
            DeviceChangeKind.Parameters | DeviceChangeKind.GroupAddressLink | DeviceChangeKind.Configuration;
        if ((change & ~known) != 0) throw new ArgumentOutOfRangeException(nameof(change));
        var flags = DeviceLoadFlags.None;
        if ((change & (DeviceChangeKind.IndividualAddress | DeviceChangeKind.Topology)) != 0)
            flags |= DeviceLoadFlags.IndividualAddress;
        if ((change & DeviceChangeKind.Product) != 0)
            flags |= DeviceLoadFlags.ApplicationProgram | DeviceLoadFlags.Parameters |
                DeviceLoadFlags.GroupAddresses;
        if ((change & DeviceChangeKind.Parameters) != 0) flags |= DeviceLoadFlags.Parameters;
        if ((change & DeviceChangeKind.GroupAddressLink) != 0) flags |= DeviceLoadFlags.GroupAddresses;
        if ((change & DeviceChangeKind.Configuration) != 0) flags |= DeviceLoadFlags.Configuration;
        return flags;
    }

    /// <summary>Rejects malformed persisted/imported state before it reaches session state.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceId))
            throw new FormatException("Device programming state has no device id.");
        if ((Flags & ~DeviceLoadFlags.All) != 0)
            throw new FormatException($"Device '{DeviceId}' has unknown load flags.");
        if (Attempts < 0)
            throw new FormatException($"Device '{DeviceId}' has a negative attempt count.");
        if (InterfaceId is { Length: 0 })
            throw new FormatException($"Device '{DeviceId}' has an empty interface id.");
        if (LastAttemptAt is null != LastAttemptOrigin is null)
            throw new FormatException($"Device '{DeviceId}' has inconsistent attempt origin data.");
        if (LastAttemptAt is null != (Attempts == 0))
            throw new FormatException($"Device '{DeviceId}' has inconsistent attempt data.");
        if (LastAttemptAt is { } attempt && LastSuccessAt is { } success && attempt < success)
            throw new FormatException($"Device '{DeviceId}' has a success after its last attempt.");
        if (LastSuccessAt is not null && LastAttemptAt is null)
            throw new FormatException($"Device '{DeviceId}' has a success without an attempt.");
        if (LastAttemptOrigin is { } attemptOrigin && !Enum.IsDefined(attemptOrigin))
            throw new FormatException($"Device '{DeviceId}' has an unknown attempt origin.");
        if (LastError is { Length: 0 } || LastError is not null && LastAttemptAt is null)
            throw new FormatException($"Device '{DeviceId}' has inconsistent error data.");
        if (LastSuccessAt is { } lastSuccess)
        {
            if (lastSuccess == default) throw new FormatException($"Device '{DeviceId}' has an empty success time.");
            if (string.IsNullOrWhiteSpace(ImageHash))
                throw new FormatException($"Device '{DeviceId}' has a success without an image hash.");
            if (Origin is not { } origin || !Enum.IsDefined(origin))
                throw new FormatException($"Device '{DeviceId}' has a success without a known origin.");
        }
        else if (ImageHash is not null || Origin is not null || Flags != DeviceLoadFlags.None)
        {
            throw new FormatException($"Device '{DeviceId}' claims loaded state without a success time.");
        }
    }
}

/// <summary>
/// Versioned side file for device programming state. It lives outside the project and its undo stack
/// and stores signatures and counters only, never configuration payloads, keys or other secrets.
/// </summary>
public static class DeviceProgrammingStates
{
    public const int Version = 2;

    public static string Serialize(string? projectId, IEnumerable<DeviceProgrammingState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var byId = new Dictionary<string, DeviceProgrammingState>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            state.Validate();
            if (!byId.TryAdd(state.DeviceId, state))
                throw new InvalidDataException($"Device programming state repeats '{state.DeviceId}'.");
        }
        var ordered = byId.Values.OrderBy(s => s.DeviceId, StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(new Envelope(Version, projectId, ordered));
    }

    /// <summary>Parses completely before returning; the caller swaps state only after this succeeds.</summary>
    public static IReadOnlyDictionary<string, DeviceProgrammingState> Parse(string json, string? projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        Envelope file;
        try
        {
            file = JsonSerializer.Deserialize<Envelope>(json) ??
                throw new FormatException("Missing device programming state.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid device programming state file.", ex);
        }
        if (file.Version != Version)
            throw new FormatException($"Unsupported device programming state version {file.Version}.");
        if (file.ProjectId != projectId)
            throw new FormatException("Device programming state belongs to another project.");
        if (file.Devices is null)
            throw new FormatException("Device programming state list is null.");
        var result = new Dictionary<string, DeviceProgrammingState>(StringComparer.Ordinal);
        foreach (var state in file.Devices)
        {
            if (state is null) throw new FormatException("Device programming state contains a null entry.");
            state.Validate();
            if (!result.TryAdd(state.DeviceId, state))
                throw new FormatException($"Device programming state repeats '{state.DeviceId}'.");
        }
        return result;
    }

    private sealed record Envelope(int Version, string? ProjectId, DeviceProgrammingState[] Devices);
}

/// <summary>Read-only per-device programming status for project check / diagnostics consumers.</summary>
public sealed record DeviceProgrammingStatus(string DeviceId, string Name, string Address,
    bool NeedsProgramming, DeviceLoadFlags LoadedFlags, DeviceLoadFlags MissingFlags,
    bool SignatureDrift, ProgrammingOrigin? Origin, DateTimeOffset? LastSuccessAt, string? LastError);

/// <summary>Pure diagnostics helper; pass the same signature provider that produced the states.</summary>
public static class DeviceProgrammingReport
{
    public static IReadOnlyList<DeviceProgrammingStatus> Check(Project project,
        IReadOnlyDictionary<string, DeviceProgrammingState> states,
        Func<Project, DeviceInstance, string>? signature = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(states);
        signature ??= static (p, d) => WorkbenchProject.ConfigurationSignature(p, d.Id);
        return ProjectQuery.DeviceList(project)
            .OrderBy(d => d.Address.Raw)
            .Select(device =>
            {
                states.TryGetValue(device.Id, out var state);
                var current = signature(project, device);
                var drift = state is { LastSuccessAt: not null } && state.ImageHash != current;
                return new DeviceProgrammingStatus(device.Id, device.Name ?? device.Id, device.Address.ToString(),
                    state is null || state.NeedsProgramming(current), state?.Flags ?? DeviceLoadFlags.None,
                    DeviceLoadFlags.All & ~(state?.Flags ?? DeviceLoadFlags.None), drift,
                    state?.Origin, state?.LastSuccessAt, state?.LastError);
            })
            .ToList();
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using Knx;

namespace Knx.Bus;

/// <summary>Application program image that gets flashed to a device.</summary>
public interface IApplicationProgram
{
    string Name { get; }
    byte[] Image { get; }
    ushort StartAddress { get; }
    int ChunkSize { get; }
}

/// <summary>Download lifecycle stages.</summary>
public enum DownloadStage { Idle, ReadingDescriptor, Programming, Flashing, Verifying, Restarting, Completed, Failed }

public sealed record DownloadProgress(DownloadStage Stage, int Percent, string Message);

public sealed record DownloadResult(bool Success, string Message);

/// <summary>One download attempt as the per-attempt log records it: interface, target, image, options, duration, cause.</summary>
public sealed record DownloadAttempt(
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string? Interface,
    string Device,
    string? ProgramName,
    string? ImageHash,
    int ImageLength,
    ushort StartAddress,
    int ChunkSize,
    bool Success,
    string? FailureCause);

/// <summary>Bounded in-memory attempt log; <see cref="DownloadSession"/> writes one entry per run.</summary>
public sealed class DownloadAttemptLog
{
    private readonly object _gate = new();
    private readonly Queue<DownloadAttempt> _entries = new();
    private readonly int _capacity;

    public DownloadAttemptLog(int capacity = 100) => _capacity = Math.Max(1, capacity);

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public IReadOnlyList<DownloadAttempt> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public void Add(DownloadAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        lock (_gate)
        {
            _entries.Enqueue(attempt);
            while (_entries.Count > _capacity) _entries.Dequeue();
        }
    }
}

/// <summary>A programmable device on a tunnel connection.</summary>
public sealed class DownloadSession
{
    private readonly KnxTunnel _tunnel;
    private readonly IApplicationProgram _program;
    private readonly IndividualAddress _device;
    private readonly string? _interfaceName;
    private readonly DownloadAttemptLog? _log;
    private readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(3);

    public DownloadSession(KnxTunnel tunnel, IndividualAddress device, IApplicationProgram program,
        string? interfaceName = null, DownloadAttemptLog? log = null)
    {
        _tunnel = tunnel;
        _device = device;
        _program = program;
        _interfaceName = interfaceName;
        _log = log;
    }

    public event Action<DownloadProgress>? Progress;

    /// <summary>
    /// Runs the download only with a ready preflight plan (<see cref="DownloadPreflight.Plan"/>).
    /// A plan that is not ready, targets another device or was built for another image is refused
    /// before the first frame leaves the process; a live descriptor read must still match the
    /// planned mask. Every invocation writes one <see cref="DownloadAttempt"/>.
    /// </summary>
    public async Task<DownloadResult> RunAsync(DownloadPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var startedAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        try
        {
            if (!plan.Ready)
                return Finish(false, "download refused by preflight: " +
                                     string.Join("; ", plan.Blockers.Select(b => b.Message)));
            if (plan.DeviceAddress != _device)
                return Finish(false, $"download refused: the preflight plan targets {plan.DeviceAddress} " +
                                     $"but this session targets {_device}");
            if (plan.ImageHash is { } plannedHash && plannedHash != DownloadPreflight.ImageHash(_program.Image))
                return Finish(false, "download refused: the preflight plan was built for a different image");

            Progress?.Invoke(new DownloadProgress(DownloadStage.ReadingDescriptor, 0, "descriptor lesen"));
            var response = await _tunnel.RequestAsync(
                Management.DeviceDescriptorRead(_device), Apci.DeviceDescriptorResponse,
                _device, _responseTimeout, ct);
            if (response is null)
                return Finish(false, "keine descriptor response");
            var descriptor = DeviceDescriptor.Parse(0x00, response.Data);
            if (plan.MaskVersion is { } mask && descriptor.MaskVersion != mask)
                return Finish(false, "download refused: the device reports mask " +
                                     (descriptor.MaskVersion is { } actual ? $"0x{actual:X4}" : "unknown") +
                                     $" but the preflight plan is for 0x{mask:X4}");
            Progress?.Invoke(new DownloadProgress(DownloadStage.ReadingDescriptor, 5, "descriptor ok"));
            Report(5, "flash");

            var image = _program.Image;
            var total = image.Length;
            var written = 0;
            var offset = 0;
            while (offset < total)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = image.Skip(offset).Take(_program.ChunkSize).ToArray();
                var writeReq = Management.MemoryWrite(_device, (ushort)(_program.StartAddress + offset), chunk);
                var memResp = await _tunnel.RequestAsync(writeReq, Apci.MemoryResponse, _device, _responseTimeout, ct);
                if (memResp is null)
                    return Finish(false, $"memory write nicht bestätigt bei Offset {offset}");

                // verify: read back and compare
                var readReq = Management.MemoryRead(_device, (ushort)(_program.StartAddress + offset), (byte)chunk.Length);
                var readResp = await _tunnel.RequestAsync(readReq, Apci.MemoryResponse, _device, _responseTimeout, ct);
                if (readResp is null)
                    return Finish(false, $"verify read ohne Antwort bei Offset {offset}");
                var echo = readResp.Data.Skip(readResp.Data.Length - chunk.Length).ToArray();
                if (!echo.SequenceEqual(chunk))
                    return Finish(false, $"verify mismatch bei Offset {offset}: [{Convert.ToHexString(echo)}] != [{Convert.ToHexString(chunk)}]");

                offset += chunk.Length;
                written += chunk.Length;
                Progress?.Invoke(new DownloadProgress(DownloadStage.Flashing, 5 + 85 * written / total,
                    $"geschrieben {written}/{total}"));
            }

            Progress?.Invoke(new DownloadProgress(DownloadStage.Restarting, 95, "Neustart"));
            var restartReq = Management.Restart(_device, erase: false);
            var restartResp = await _tunnel.RequestAsync(restartReq, Apci.MemoryResponse, _device, _responseTimeout, ct);
            if (restartResp is null)
                return Finish(false, "restart nicht bestätigt");

            return Finish(true, $"{_program.Name}: {total} Bytes geschrieben (Offset 0x{_program.StartAddress:X4})");
        }
        catch (Exception ex)
        {
            return Finish(false, ex.Message);
        }

        DownloadResult Finish(bool success, string message)
        {
            _log?.Add(new DownloadAttempt(startedAt, Stopwatch.GetElapsedTime(started), _interfaceName,
                _device.ToString(), _program.Name, plan.ImageHash, _program.Image.Length, _program.StartAddress,
                _program.ChunkSize, success, success ? null : message));
            Progress?.Invoke(new DownloadProgress(success ? DownloadStage.Completed : DownloadStage.Failed,
                success ? 100 : -1, message));
            return new DownloadResult(success, message);
        }

        void Report(int percent, string message)
            => Progress?.Invoke(new DownloadProgress(DownloadStage.Programming, percent, message));
    }
}

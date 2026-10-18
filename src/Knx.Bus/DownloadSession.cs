using System.Buffers.Binary;
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

/// <summary>A programmable device on a tunnel connection.</summary>
public sealed class DownloadSession
{
    private readonly KnxTunnel _tunnel;
    private readonly IApplicationProgram _program;
    private readonly IndividualAddress _device;
    private readonly TimeSpan _responseTimeout = TimeSpan.FromSeconds(3);

    public DownloadSession(KnxTunnel tunnel, IndividualAddress device, IApplicationProgram program)
    {
        _tunnel = tunnel;
        _device = device;
        _program = program;
    }

    public event Action<DownloadProgress>? Progress;

    public async Task<DownloadResult> RunAsync(CancellationToken ct = default)
    {
        try
        {
            Progress?.Invoke(new DownloadProgress(DownloadStage.ReadingDescriptor, 0, "descriptor lesen"));
            var descriptor = await _tunnel.RequestAsync(
                Management.DeviceDescriptorRead(_device), Apci.DeviceDescriptorResponse,
                _device, _responseTimeout, ct);
            if (descriptor is null)
                return Fail("keine descriptor response");
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
                    return Fail($"memory write nicht bestätigt bei Offset {offset}");

                // verify: read back and compare
                var readReq = Management.MemoryRead(_device, (ushort)(_program.StartAddress + offset), (byte)chunk.Length);
                var readResp = await _tunnel.RequestAsync(readReq, Apci.MemoryResponse, _device, _responseTimeout, ct);
                if (readResp is null)
                    return Fail($"verify read ohne Antwort bei Offset {offset}");
                var echo = readResp.Data.Skip(readResp.Data.Length - chunk.Length).ToArray();
                if (!echo.SequenceEqual(chunk))
                    return Fail($"verify mismatch bei Offset {offset}: [{Convert.ToHexString(echo)}] != [{Convert.ToHexString(chunk)}]");

                offset += chunk.Length;
                written += chunk.Length;
                Progress?.Invoke(new DownloadProgress(DownloadStage.Flashing, 5 + 85 * written / total,
                    $"geschrieben {written}/{total}"));
            }

            Progress?.Invoke(new DownloadProgress(DownloadStage.Restarting, 95, "Neustart"));
            var restartReq = Management.Restart(_device, erase: false);
            var restartResp = await _tunnel.RequestAsync(restartReq, Apci.MemoryResponse, _device, _responseTimeout, ct);
            if (restartResp is null)
                return Fail("restart nicht bestätigt");

            Progress?.Invoke(new DownloadProgress(DownloadStage.Completed, 100, "Download abgeschlossen"));
            return new DownloadResult(true, $"{_program.Name}: {total} Bytes geschrieben (Offset 0x{_program.StartAddress:X4})");
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        DownloadResult Fail(string message)
        {
            Progress?.Invoke(new DownloadProgress(DownloadStage.Failed, -1, message));
            return new DownloadResult(false, message);
        }

        void Report(int percent, string message)
            => Progress?.Invoke(new DownloadProgress(DownloadStage.Programming, percent, message));
    }
}

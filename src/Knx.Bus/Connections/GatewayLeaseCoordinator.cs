using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Connections;

/// <summary>One canonical lock per gateway across modes, windows and local App/MCP processes.</summary>
public sealed class GatewayLeaseCoordinator
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Active = new(StringComparer.Ordinal);
    public static string DefaultDirectory
    {
        get
        {
            var userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (!Path.IsPathFullyQualified(userData)) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            return Path.Combine(userData, "BusChord", "network-leases");
        }
    }
    private readonly string _directory;
    public GatewayLeaseCoordinator(string? directory = null) => _directory = Path.GetFullPath(directory ?? DefaultDirectory);
    public static IDisposable? TryAcquireProcessOnly(IPEndPoint gateway)
    {
        var key = "process:" + gateway.Address + ":" + gateway.Port;
        lock (Gate) return Active.Add(key) ? new ProcessLease(key) : null;
    }
    public IDisposable Acquire(IPEndPoint gateway)
    {
        var identity = gateway.Address + ":" + gateway.Port;
        var path = Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".lock");
        lock (Gate)
        {
            var processKey = "process:" + identity;
            if (Active.Contains(path) || Active.Contains(processKey)) throw new KnxConnectionException(KnxConnectionFailure.Occupied);
            FileStream? file = null;
            try
            {
                Directory.CreateDirectory(_directory); SecureLocalFiles.NoLinks(_directory);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (File.Exists(path)) SecureLocalFiles.NoLinks(path);
                file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                Active.Add(path); Active.Add(processKey); var result = new Lease(path, processKey, file); file = null; return result;
            }
            catch (KnxConnectionException) { throw; }
            catch (IOException) { throw new KnxConnectionException(KnxConnectionFailure.Occupied); }
            catch (UnauthorizedAccessException) { throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable); }
            finally { file?.Dispose(); }
        }
    }
    private sealed class ProcessLease(string key) : IDisposable
    { private bool _disposed; public void Dispose() { lock (Gate) { if (_disposed) return; _disposed = true; Active.Remove(key); } } }
    private sealed class Lease(string path, string processKey, FileStream file) : IDisposable
    {
        private bool _disposed;
        public void Dispose() { lock (Gate) { if (_disposed) return; _disposed = true; file.Dispose(); Active.Remove(path); Active.Remove(processKey); } }
    }
}

internal static class SecureLocalFiles
{
    internal static void NoLinks(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
        for (var entry = Path.GetFullPath(path); entry is not null; entry = Path.GetDirectoryName(entry))
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
    }
    internal static async Task<byte[]> ReadPrivateAsync(string path, int maximum, CancellationToken ct)
    {
        NoLinks(path);
        for (var entry = Path.GetDirectoryName(path); entry is not null; entry = Path.GetDirectoryName(entry))
            if (Directory.Exists(Path.Combine(entry, ".git")) || File.Exists(Path.Combine(entry, ".git"))) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is < 1 || file.Length > maximum) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
        using var bytes = new MemoryStream(); var buffer = new byte[65536];
        try
        {
            for (;;)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - bytes.Length)), ct);
                if (read == 0) return bytes.ToArray();
                bytes.Write(buffer, 0, read);
                if (bytes.Length > maximum) throw new KnxConnectionException(KnxConnectionFailure.CredentialSourceUnavailable);
            }
        }
        finally { CryptographicOperations.ZeroMemory(buffer); if (bytes.TryGetBuffer(out var owned)) CryptographicOperations.ZeroMemory(owned.AsSpan()); }
    }
}

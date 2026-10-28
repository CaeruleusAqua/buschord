using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Knx.Updates;

/// <summary>Readers cover all workbenches; intent prevents new starts during a complete native swap.</summary>
public sealed class UpdateProcessLease : IDisposable
{
    private readonly LeaseFile _running;
    private readonly LeaseFile? _intent;
    public IReadOnlyList<SafeFileHandle> Handles => _intent is null ? [_running.Handle] : [_intent.Handle, _running.Handle];
    private UpdateProcessLease(LeaseFile running, LeaseFile? intent = null) { _running = running; _intent = intent; }
    private static string LeasePath(string directory) { Directory.CreateDirectory(directory); return Path.Combine(directory, "running.lock"); }
    public static UpdateProcessLease EnterApplication(string directory)
    {
        var path = LeasePath(directory);
        using var intent = LeaseFile.Open(Path.Combine(directory, "intent.lock"), false);
        return new(LeaseFile.Open(path, false));
    }
    public static UpdateProcessLease EnterPackageManagedApplication() => new(LeaseFile.Open("/var/lib/buschord-updater/running.lock", false, create: false));
    public static UpdateProcessLease? TryEnterInstaller(string directory)
    {
        LeaseFile? intent = null;
        try
        {
            var path = LeasePath(directory); intent = LeaseFile.Open(Path.Combine(directory, "intent.lock"), true);
            return new(LeaseFile.Open(path, true), intent);
        }
        catch (IOException) { intent?.Dispose(); return null; }
    }
    public void Dispose() { _running.Dispose(); _intent?.Dispose(); }

    private sealed class LeaseFile : IDisposable
    {
        public SafeFileHandle Handle { get; }
        private readonly FileStream? _stream;
        private LeaseFile(SafeFileHandle handle, FileStream? stream = null) { Handle = handle; _stream = stream; }
        public static LeaseFile Open(string path, bool exclusive, bool create = true)
        {
            if (OperatingSystem.IsWindows())
            {
                var stream = new FileStream(path, create ? FileMode.OpenOrCreate : FileMode.Open,
                    exclusive ? FileAccess.ReadWrite : FileAccess.Read, exclusive ? FileShare.None : FileShare.Read);
                return new(stream.SafeFileHandle, stream);
            }
            var fd = open(path, (exclusive ? 2 : 0) | (create ? 0x40 : 0) | 0x80000, 0x180); // Linux O_CREAT/O_CLOEXEC, mode 0600.
            if (fd == -1) throw new IOException("Cannot open update lease.", new Win32Exception(Marshal.GetLastWin32Error()));
            var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            if (flock(fd, (exclusive ? 2 : 1) | 4) == -1)
            { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("An update or workbench already holds this lease.", new Win32Exception(error)); }
            // This handle was not opened through FileStream. Dispose closes without LOCK_UN,
            // so inherited descriptors keep the flock alive until the final updater exits.
            return new(handle);
        }
        public void Dispose() { if (_stream is not null) _stream.Dispose(); else Handle.Dispose(); }
    }
    [DllImport("libc", SetLastError = true)] private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport("libc", SetLastError = true)] private static extern int flock(int fd, int operation);
}

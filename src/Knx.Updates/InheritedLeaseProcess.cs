using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Knx.Updates;

/// <summary>Starts a native updater with inherited kernel leases. Those survive a broker crash until the updater exits.</summary>
public sealed class InheritedLeaseProcess : IDisposable
{
    private readonly IntPtr _handle;
    private readonly int _pid;
    private InheritedLeaseProcess(IntPtr handle, int pid) { _handle = handle; _pid = pid; }
    public static InheritedLeaseProcess Start(string executable, IReadOnlyList<string> args, string workingDirectory,
        IReadOnlyList<SafeFileHandle> leases)
    {
        var references = new List<SafeFileHandle>();
        try
        {
            foreach (var lease in leases)
            {
                var acquired = false; lease.DangerousAddRef(ref acquired); if (acquired) references.Add(lease);
                if (OperatingSystem.IsWindows())
                {
                    if (!SetHandleInformation(lease.DangerousGetHandle(), 1, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    var fd = lease.DangerousGetHandle().ToInt32(); var flags = fcntl(fd, 1, 0);
                    if (flags == -1 || fcntl(fd, 2, flags & ~1) == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            if (OperatingSystem.IsWindows())
            {
                var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
                var command = new System.Text.StringBuilder(string.Join(' ', new[] { executable }.Concat(args).Select(QuoteWindows)));
                if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08000000, IntPtr.Zero, workingDirectory, ref startup, out var process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                CloseHandle(process.Thread); return new(process.Process, process.Id);
            }
            // posix_spawn inherits non-CLOEXEC descriptors. Do not use Process.Start, which closes unrelated descriptors.
            using var argv = new NativeStrings(new[] { executable }.Concat(args));
            using var env = new NativeStrings(Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(e => e.Key + "=" + e.Value));
            var result = posix_spawn(out var pid, executable, IntPtr.Zero, IntPtr.Zero, argv.Pointer, env.Pointer);
            if (result != 0) throw new Win32Exception(result);
            return new(IntPtr.Zero, pid);
        }
        finally
        {
            foreach (var lease in references)
            {
                if (OperatingSystem.IsWindows()) SetHandleInformation(lease.DangerousGetHandle(), 1, 0);
                else { var fd = lease.DangerousGetHandle().ToInt32(); var flags = fcntl(fd, 1, 0); if (flags != -1) fcntl(fd, 2, flags | 1); }
                lease.DangerousRelease();
            }
        }
    }
    public async Task<int> WaitAsync(CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                var state = WaitForSingleObject(_handle, 0);
                if (state == 0) { if (!GetExitCodeProcess(_handle, out var code)) throw new Win32Exception(Marshal.GetLastWin32Error()); return unchecked((int)code); }
                if (state != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            else
            {
                var result = waitpid(_pid, out var status, 1);
                if (result == _pid) return (status & 0x7f) == 0 ? (status >> 8) & 255 : 128 + (status & 0x7f);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            await Task.Delay(30, token).ConfigureAwait(false);
        }
    }
    public void Dispose() { if (_handle != IntPtr.Zero) CloseHandle(_handle); }
    private static string QuoteWindows(string value)
    {
        var b = new System.Text.StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') b.Append('\\', slashes * 2 + 1).Append(c);
            else b.Append('\\', slashes).Append(c);
            slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed class NativeStrings : IDisposable
    {
        private readonly IntPtr[] _strings;
        public IntPtr Pointer { get; }
        public NativeStrings(IEnumerable<string> strings)
        {
            _strings = strings.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = Marshal.AllocHGlobal((_strings.Length + 1) * IntPtr.Size);
            for (var i = 0; i < _strings.Length; i++) Marshal.WriteIntPtr(Pointer, i * IntPtr.Size, _strings[i]);
            Marshal.WriteIntPtr(Pointer, _strings.Length * IntPtr.Size, IntPtr.Zero);
        }
        public void Dispose() { foreach (var text in _strings) Marshal.FreeCoTaskMem(text); Marshal.FreeHGlobal(Pointer); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public int Id, ThreadId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, System.Text.StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr handle, out uint code);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", SetLastError = true)] private static extern int fcntl(int fd, int command, int argument);
    [DllImport("libc", SetLastError = true)] private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr fileActions, IntPtr attributes, IntPtr argv, IntPtr envp);
    [DllImport("libc", SetLastError = true)] private static extern int waitpid(int pid, out int status, int options);
}

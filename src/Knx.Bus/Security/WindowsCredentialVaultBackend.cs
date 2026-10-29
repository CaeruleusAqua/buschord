using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>
/// Windows Generic Credentials, CRED_PERSIST_LOCAL_MACHINE: current user on this computer, not machine-wide credentials.
/// No CredEnumerate API exists in this adapter. Native error text and private target names never enter exceptions.
/// https://learn.microsoft.com/windows/win32/api/wincred/ns-wincred-credentialw
/// </summary>
internal sealed class WindowsCredentialVaultBackend : ISecureCredentialVaultBackend
{
    private const string Marker = "BusChord protected keyring password v1", User = "BusChord";
    private readonly IWindowsCredentialApi _api;
    internal WindowsCredentialVaultBackend(IWindowsCredentialApi? api = null) => _api = api ?? new WindowsCredentialApi();
    public SecureCredentialCapability Probe(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (!_api.Available) return SecureCredentialCapability.UnsupportedPlatform;
        var persistence = new uint[7];
        return _api.GetSessionTypes(persistence, out _) && persistence[1] >= 2 ? SecureCredentialCapability.Ready : SecureCredentialCapability.Unavailable;
    }
    public byte[] ReadOwned(SecureCredentialBinding binding, CancellationToken ct)
    { using var lease = Enter(binding, ct); return Read(binding) ?? throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound); }
    public void Write(SecureCredentialBinding binding, byte[] borrowedBlob, SecureCredentialWriteMode mode, CancellationToken ct)
    {
        using var lease = Enter(binding, ct); SecureCredentialEnvelope.Validate(binding, borrowedBlob); var existing = Read(binding);
        try
        {
            if (existing is not null) { if (mode == SecureCredentialWriteMode.CreateNew) throw new SecureCredentialVaultException(SecureCredentialFailure.AlreadyExists); SecureCredentialEnvelope.Validate(binding, existing); }
            else if (mode == SecureCredentialWriteMode.ReplaceMatching) throw new SecureCredentialVaultException(SecureCredentialFailure.NotFound);
            ct.ThrowIfCancellationRequested();
            using var target = new NativeText(binding.TargetName); using var comment = new NativeText(Marker); using var user = new NativeText(User);
            var secret = Marshal.AllocHGlobal(borrowedBlob.Length);
            try
            {
                Marshal.Copy(borrowedBlob, 0, secret, borrowedBlob.Length);
                var credential = new WindowsCredentialRecord { Type = 1, TargetName = target.Pointer, Comment = comment.Pointer,
                    CredentialBlobSize = (uint)borrowedBlob.Length, CredentialBlob = secret, Persist = 2, UserName = user.Pointer };
                if (!_api.Write(ref credential, out var error)) throw Failure(error);
            }
            finally { ClearNative(secret, borrowedBlob.Length); Marshal.FreeHGlobal(secret); }
        }
        finally { if (existing is not null) CryptographicOperations.ZeroMemory(existing); }
    }
    public bool DeleteMatching(SecureCredentialBinding binding, CancellationToken ct)
    {
        using var lease = Enter(binding, ct); var existing = Read(binding); if (existing is null) return false;
        try
        {
            SecureCredentialEnvelope.Validate(binding, existing); ct.ThrowIfCancellationRequested();
            if (_api.Delete(binding.TargetName, out var error)) return true;
            if (error == 1168) return false; throw Failure(error);
        }
        finally { CryptographicOperations.ZeroMemory(existing); }
    }
    private byte[]? Read(SecureCredentialBinding binding)
    {
        if (!_api.Read(binding.TargetName, out var pointer, out var error))
        { if (error == 1168) return null; throw Failure(error); }
        WindowsCredentialRecord record = default; byte[]? result = null;
        try
        {
            try
            {
                if (pointer == IntPtr.Zero) throw new SecureCredentialVaultException(SecureCredentialFailure.NativeFailure);
                record = Marshal.PtrToStructure<WindowsCredentialRecord>(pointer);
                if (record.Type != 1 || record.Flags != 0 || record.Persist != 2 || record.AttributeCount != 0 || !TextEquals(record.TargetName, binding.TargetName, StringComparison.OrdinalIgnoreCase)
                    || !TextEquals(record.Comment, Marker) || !TextEquals(record.UserName, User)
                    || record.CredentialBlobSize is <= SecureCredentialEnvelope.HeaderLength or > SecureCredentialEnvelope.MaximumBlobBytes || record.CredentialBlob == IntPtr.Zero)
                    throw new SecureCredentialVaultException(SecureCredentialFailure.BindingMismatch);
                result = new byte[record.CredentialBlobSize]; Marshal.Copy(record.CredentialBlob, result, 0, result.Length);
                SecureCredentialEnvelope.Validate(binding, result);
            }
            finally
            {
                try { if (record.CredentialBlob != IntPtr.Zero && record.CredentialBlobSize <= 2560) ClearNative(record.CredentialBlob, (int)record.CredentialBlobSize); }
                finally { if (pointer != IntPtr.Zero) _api.Free(pointer); }
            }
            return result;
        }
        catch { if (result is not null) CryptographicOperations.ZeroMemory(result); throw; }
    }
    private IDisposable Enter(SecureCredentialBinding binding, CancellationToken ct)
    {
        if (!_api.Available) throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
        var user = _api.CurrentUserId();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user + ":" + binding.TargetName)));
        var mutex = new Mutex(false, (OperatingSystem.IsWindows() ? "Global\\" : "") + "BusChord.Credential." + identity); var owned = false;
        try
        {
            var clock = Stopwatch.StartNew();
            while (!owned)
            {
                ct.ThrowIfCancellationRequested(); if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new SecureCredentialVaultException(SecureCredentialFailure.Unavailable);
                try { owned = mutex.WaitOne(50); } catch (AbandonedMutexException) { owned = true; }
            }
            return new MutexLease(mutex);
        }
        catch { if (owned) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable { public void Dispose() { try { mutex.ReleaseMutex(); } finally { mutex.Dispose(); } } }
    private static bool TextEquals(IntPtr pointer, string value, StringComparison comparison = StringComparison.Ordinal)
    {
        if (pointer == IntPtr.Zero) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var actual = (char)Marshal.ReadInt16(pointer, checked(i * 2)); if (actual == '\0') return false;
            if (comparison == StringComparison.OrdinalIgnoreCase ? char.ToUpperInvariant(actual) != char.ToUpperInvariant(value[i]) : actual != value[i]) return false;
        }
        return Marshal.ReadInt16(pointer, checked(value.Length * 2)) == 0;
    }
    private static void ClearNative(IntPtr pointer, int length) { if (length > 0) Marshal.Copy(new byte[length], 0, pointer, length); }
    private static SecureCredentialVaultException Failure(int error) => new(error switch
    { 1168 => SecureCredentialFailure.NotFound, 5 => SecureCredentialFailure.AccessDenied, 1312 => SecureCredentialFailure.Unavailable, _ => SecureCredentialFailure.NativeFailure });
    private sealed class NativeText : IDisposable
    { public IntPtr Pointer { get; } public NativeText(string value) => Pointer = Marshal.StringToCoTaskMemUni(value); public void Dispose() => Marshal.FreeCoTaskMem(Pointer); }
}

[StructLayout(LayoutKind.Sequential)]
internal struct WindowsCredentialRecord
{
    public uint Flags, Type;
    public IntPtr TargetName, Comment;
    public uint LastWrittenLow, LastWrittenHigh, CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist, AttributeCount;
    public IntPtr Attributes, TargetAlias, UserName;
}
internal interface IWindowsCredentialApi
{
    bool Available { get; }
    string CurrentUserId();
    bool GetSessionTypes(uint[] persistence, out int error);
    bool Read(string exactTarget, out IntPtr credential, out int error);
    bool Write(ref WindowsCredentialRecord credential, out int error);
    bool Delete(string exactTarget, out int error);
    void Free(IntPtr credential);
}
internal sealed class WindowsCredentialApi : IWindowsCredentialApi
{
    public bool Available => OperatingSystem.IsWindows();
    public string CurrentUserId()
    {
        if (!OperatingSystem.IsWindows()) throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
        using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value ?? throw new SecureCredentialVaultException(SecureCredentialFailure.Unavailable);
    }
    public bool GetSessionTypes(uint[] persistence, out int error) { var ok = CredGetSessionTypes((uint)persistence.Length, persistence); error = Marshal.GetLastPInvokeError(); return ok; }
    public bool Read(string target, out IntPtr credential, out int error) { var ok = CredReadW(target, 1, 0, out credential); error = Marshal.GetLastPInvokeError(); return ok; }
    public bool Write(ref WindowsCredentialRecord credential, out int error) { var ok = CredWriteW(ref credential, 0); error = Marshal.GetLastPInvokeError(); return ok; }
    public bool Delete(string target, out int error) { var ok = CredDeleteW(target, 1, 0); error = Marshal.GetLastPInvokeError(); return ok; }
    public void Free(IntPtr credential) => CredFree(credential);
    [DllImport("Advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredGetSessionTypes(uint count, [Out] uint[] persistence);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("Advapi32.dll", SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWriteW(ref WindowsCredentialRecord credential, uint flags);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("Advapi32.dll", ExactSpelling = true)] private static extern void CredFree(IntPtr credential);
}

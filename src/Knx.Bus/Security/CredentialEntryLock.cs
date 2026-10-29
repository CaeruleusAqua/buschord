using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>Cooperating same-user callers serialize exact-entry mutations. No secret is used as a kernel-object name.</summary>
internal sealed class CredentialEntryLock : IDisposable
{
    private readonly Mutex _mutex;
    private CredentialEntryLock(Mutex mutex) => _mutex = mutex;
    internal static CredentialEntryLock Enter(SecureCredentialBinding binding, CancellationToken ct)
    {
        var name = "BusChord.Credential." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName + ":" + binding.TargetName)));
        var mutex = new Mutex(false, name); var owned = false;
        try
        {
            var watch = Stopwatch.StartNew();
            while (!owned)
            {
                ct.ThrowIfCancellationRequested(); if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new SecureCredentialVaultException(SecureCredentialFailure.Unavailable);
                try { owned = mutex.WaitOne(50); } catch (AbandonedMutexException) { owned = true; }
            }
            return new(mutex);
        }
        catch { if (owned) mutex.ReleaseMutex(); mutex.Dispose(); throw; }
    }
    public void Dispose() { try { _mutex.ReleaseMutex(); } finally { _mutex.Dispose(); } }
}

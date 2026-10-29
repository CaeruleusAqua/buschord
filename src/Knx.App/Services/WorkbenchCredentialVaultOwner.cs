using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Knx.Bus.Security;

namespace Knx.App.Services;

/// <summary>One window's lazy OS-vault owner. Construction and project binding never access an OS provider.</summary>
public sealed class WorkbenchCredentialVaultOwner : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<SecureCredentialVault> _create;
    private SecureCredentialVault? _vault;
    private Task? _disposal;
    public WorkbenchCredentialVaultOwner(Func<SecureCredentialVault>? create = null) => _create = create ?? SecureCredentialVault.CreateCurrentUser;
    [JsonIgnore] public SecureCredentialVault Vault
    {
        get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposal is not null, this); return _vault ??= _create(); } }
    }
    /// <summary>Saved projects only; stable identity and full canonical path, never a display name or per-dialog GUID.</summary>
    public static string? ProjectScope(Project? project, string? projectPath)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.Id) || string.IsNullOrWhiteSpace(projectPath) || !Path.IsPathFullyQualified(projectPath)) return null;
        try
        {
            var path = Path.GetFullPath(projectPath); if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> length = stackalloc byte[4];
            foreach (var field in new[] { "BusChord.Workbench.ProjectScope.1", project.Id, project.Guid ?? "", path })
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(field);
                BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
            }
            return "workbench-project-v1:" + Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }
    /// <summary>The application must first join every connection, scan, commissioning and dialog operation using this owner.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? completion = null; SecureCredentialVault? vault = null; Task result;
        lock (_gate)
        {
            if (_disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = completion.Task; vault = _vault; }
            result = _disposal;
        }
        if (completion is not null) _ = FinishDisposeAsync(vault, completion); return new(result);
    }
    private static async Task FinishDisposeAsync(SecureCredentialVault? vault, TaskCompletionSource completion)
    {
        try { if (vault is not null) await vault.DisposeAsync().ConfigureAwait(false); completion.TrySetResult(); }
        catch { completion.TrySetException(new IOException("Protected credential cleanup failed.")); }
    }
    public override string ToString() => "[workbench protected credential owner]";
}

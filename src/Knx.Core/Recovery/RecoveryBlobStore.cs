using System.Security.Cryptography;

namespace Knx.Recovery;

/// <summary>Bounded immutable blob storage. Manifest closure and protection checks belong to the caller.</summary>
public sealed class RecoveryBlobStore
{
    public const long MaximumBlobBytes = 128L * 1024 * 1024;
    private readonly string _root;
    private readonly string _blobs;

    public RecoveryBlobStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        _blobs = Path.Combine(_root, "blobs");
        RecoveryStoragePermissions.Directory(_root);
        RejectLink(_root);
        RecoveryStoragePermissions.ManagedDirectory(_blobs);
        RejectLink(_blobs);
    }
    internal bool Exists(RecoveryBlob blob) => File.Exists(BlobPath(blob));

    public async Task<RecoveryBlob> AddAsync(Stream input, long expectedLength, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateLength(expectedLength);
        RejectLink(_root); RejectLink(_blobs);
        var temporary = Path.Combine(_blobs, ".blob-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            using (var output = RecoveryStoragePermissions.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[65536];
                long copied = 0;
                while (copied < expectedLength)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, expectedLength - copied)), cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw Corrupt("Recovery blob input ended early.");
                    digest.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                }
                if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
                    throw Corrupt("Recovery blob input exceeds its declared length.");
                cancellationToken.ThrowIfCancellationRequested();
                output.Flush(true);
                hash = Convert.ToHexString(digest.GetHashAndReset());
            }
            var blob = new RecoveryBlob(hash, expectedLength);
            var destination = BlobPath(blob);
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, destination, false); }
            catch (IOException) when (File.Exists(destination))
            {
                // Concurrent identical content is acceptable only after complete verification.
                await using var verified = await OpenVerifiedAsync(blob, cancellationToken).ConfigureAwait(false);
            }
            return blob;
        }
        finally { DeleteOwnedTemporary(temporary); }
    }

    /// <summary>Returns a read-only stream positioned at zero after exact size/hash verification. Caller disposes it.</summary>
    public async Task<FileStream> OpenVerifiedAsync(RecoveryBlob blob, CancellationToken cancellationToken = default)
    {
        var path = BlobPath(blob);
        RejectLink(_root); RejectLink(_blobs); RejectLink(path);
        FileStream stream;
        try { stream = RecoveryStoragePermissions.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { throw Corrupt("Recovery.BlobMissing"); }
        catch (DirectoryNotFoundException) { throw Corrupt("Recovery.BlobMissing"); }
        try
        {
            if (stream.Length != blob.Length) throw Corrupt("Recovery blob size does not match its manifest.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long total = 0;
            while (total < blob.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, blob.Length - total)), cancellationToken).ConfigureAwait(false);
                if (read == 0) throw Corrupt("Recovery blob ended early.");
                digest.AppendData(buffer.AsSpan(0, read)); total += read;
            }
            if (stream.Length != blob.Length || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(blob.Sha256)))
                throw Corrupt("Recovery blob content does not match its manifest.");
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private string BlobPath(RecoveryBlob blob)
    {
        ArgumentNullException.ThrowIfNull(blob);
        ValidateLength(blob.Length);
        if (blob.Sha256 is not { Length: 64 } || blob.Sha256.Any(character => !char.IsAsciiHexDigit(character))
            || blob.Sha256 != blob.Sha256.ToUpperInvariant())
            throw Corrupt("Recovery blob identity is not a canonical SHA-256 value.");
        return Path.Combine(_blobs, blob.Sha256 + ".blob");
    }

    private static void ValidateLength(long length)
    {
        if (length is < 0 or > MaximumBlobBytes)
            throw new RecoveryRejectedException(RecoveryFailure.LimitExceeded, "Recovery blob exceeds its size limit.");
    }
    private static RecoveryRejectedException Corrupt(string message) => new(RecoveryFailure.Corrupt, message);
    private static void RejectLink(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Corrupt("Recovery storage links are not supported.");
    }
    private static void DeleteOwnedTemporary(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

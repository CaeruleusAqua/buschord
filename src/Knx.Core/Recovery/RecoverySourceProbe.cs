using System.Security.Cryptography;

namespace Knx.Recovery;

/// <summary>Reads only the identity/hash of an explicitly selected local saved archive. Never opens a manifest-provided path automatically.</summary>
public static class RecoverySourceProbe
{
    public static Task<RecoverySource> ReadSelectedAsync(string absolutePath, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
            if (!Path.IsPathFullyQualified(absolutePath)) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.AbsoluteSourceRequired");
            var path = Path.GetFullPath(absolutePath);
            cancellationToken.ThrowIfCancellationRequested(); RecoveryWire.NoLink(path);
            FileStream input;
            try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (FileNotFoundException) { throw RecoveryWire.Reject(RecoveryFailure.SourceMissing, "Recovery.SourceMissing"); }
            catch (DirectoryNotFoundException) { throw RecoveryWire.Reject(RecoveryFailure.SourceMissing, "Recovery.SourceMissing"); }
            await using (input)
            {
                var length = input.Length;
                if (length <= 0) throw RecoveryWire.Reject(RecoveryFailure.Corrupt, "Recovery.InvalidArchive");
                if (length > RecoveryBlobStore.MaximumBlobBytes) throw RecoveryWire.Reject(RecoveryFailure.LimitExceeded, "Recovery.SourceTooLarge");
                var bytes = new byte[checked((int)length)];
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var offset = 0;
                while (offset < bytes.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = await input.ReadAsync(bytes.AsMemory(offset, Math.Min(65536, bytes.Length - offset)), cancellationToken).ConfigureAwait(false);
                    if (count == 0) throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceChanged");
                    hash.AppendData(bytes.AsSpan(offset, count)); offset += count;
                }
                if (input.Length != length || await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                    throw RecoveryWire.Reject(RecoveryFailure.SourceChanged, "Recovery.SourceChanged");
                cancellationToken.ThrowIfCancellationRequested();
                // Inspect checks all local/central protection flags before parsing bounded
                // primary project XML. Optional manufacturer/application payloads stay opaque.
                var identity = RecoveryArchives.Inspect(bytes, null, inferNested: true);
                cancellationToken.ThrowIfCancellationRequested();
                var source = new RecoverySource(RecoveryBindingKind.SavedArchive, identity.ProjectId ?? "", identity.ProjectGuid,
                    path, RecoveryContractValidation.PathIdentity(path), new(Convert.ToHexString(hash.GetHashAndReset()), length), null);
                RecoveryContractValidation.ValidateSavedBinding(source);
                return source;
            }
        }, cancellationToken);
}

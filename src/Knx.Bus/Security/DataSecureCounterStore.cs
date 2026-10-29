using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>
/// A private, fixed-size counter file. Each counter has two separate authenticated 4-KiB pages.
/// A new value is flushed before release; optional independent anchors detect rollback across restarts.
/// Without an external anchor, restoring a complete earlier valid file cannot be detected after a process restart.
/// </summary>
public sealed partial class DataSecureCounterStore : IAsyncDisposable
{
    private const int PageSize = 4096, MacOffset = PageSize - 32, MaximumSenders = 65534;
    private static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("BCDSC002"), PageMagic = Encoding.ASCII.GetBytes("BCDSCNT2");
    private readonly string _path;
    private readonly DataSecureStateIdentity _identity;
    private readonly byte[] _integrityKey;
    private readonly IDataSecureMonotonicAnchor? _anchor;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<int, DataSecureCounterCheckpoint> _minimum = [];
    private Manifest _manifest = null!;
    private bool _disposed;
    private Task? _disposal;
    public string IdentityFingerprint => _identity.Fingerprint;
    public IndividualAddress LocalAddress => _identity.LocalAddress;
    public bool HasExternalRollbackAnchor => _anchor is not null;
    public IReadOnlyList<IndividualAddress> KnownSenders => Array.AsReadOnly(_manifest.Senders.Select(s => new IndividualAddress(s)).ToArray());

    private DataSecureCounterStore(string path, DataSecureStateIdentity identity, ReadOnlyMemory<byte> integrityKey, IDataSecureMonotonicAnchor? anchor)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (integrityKey.Length != 32) throw new ArgumentException("A counter-state integrity key is 32 bytes.");
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Counter files require Windows or Linux in this build.");
        _path = PrivatePath(path); _identity = identity; _integrityKey = integrityKey.ToArray(); _anchor = anchor;
    }

    public static async Task<DataSecureCounterStore> InitializeAsync(string path, DataSecureStateIdentity identity, ReadOnlyMemory<byte> integrityKey,
        DataSecureCounterBootstrap trusted, IDataSecureMonotonicAnchor? anchor = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        var store = new DataSecureCounterStore(path, identity, integrityKey, anchor);
        try { await store.InitializeCoreAsync(trusted, ct).ConfigureAwait(false); return store; }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public static async Task<DataSecureCounterStore> OpenAsync(string path, DataSecureStateIdentity identity, ReadOnlyMemory<byte> integrityKey,
        IDataSecureMonotonicAnchor? anchor = null, CancellationToken ct = default)
    {
        var store = new DataSecureCounterStore(path, identity, integrityKey, anchor);
        try
        {
            using var source = CancellationTokenSource.CreateLinkedTokenSource(ct, store._lifetime.Token);
            await store._serial.WaitAsync(source.Token).ConfigureAwait(false);
            try
            {
                using var lease = await store.LockFileAsync(source.Token).ConfigureAwait(false);
                using var file = store.OpenState(); store._manifest = store.ReadManifest(file);
                await store.ReadCounterAsync(file, 0, source.Token).ConfigureAwait(false);
            }
            finally { store._serial.Release(); }
            return store;
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async Task InitializeCoreAsync(DataSecureCounterBootstrap trusted, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var lease = await LockFileAsync(ct).ConfigureAwait(false);
            PrivatePath(_path);
            var senders = trusted.TrustedReceiveFloors.Keys.Select(a => a.Raw).Order().ToArray();
            var headerLength = checked((68 + senders.Length * 2 + 32 + PageSize - 1) / PageSize * PageSize);
            _manifest = new(Guid.NewGuid(), headerLength, senders, _anchor is not null);
            using var file = new FileStream(_path, Options(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read));
            await file.WriteAsync(new byte[headerLength], ct).ConfigureAwait(false);
            var sendHighwater = trusted.TrustedNextSendSequence - 1;
            if (trusted.TrustedReceiveFloors.TryGetValue(_identity.LocalAddress, out var ownFloor)) sendHighwater = Math.Max(sendHighwater, ownFloor);
            for (var index = 0; index < senders.Length + 2; index++)
            {
                var value = index == 0 ? 1UL : index == 1 ? sendHighwater
                    : trusted.TrustedReceiveFloors[new IndividualAddress(senders[index - 2])];
                var page = BuildPage(index, 0, value, new byte[32]);
                await file.WriteAsync(page, ct).ConfigureAwait(false); await file.WriteAsync(page, ct).ConfigureAwait(false);
            }
            file.Flush(flushToDisk: true);
            var header = BuildManifest(); file.Position = 0; await file.WriteAsync(header, ct).ConfigureAwait(false); file.Flush(flushToDisk: true);
            if (_anchor is not null)
            {
                // The readiness anchor is last, so interrupted initialization cannot release counters.
                foreach (var index in Enumerable.Range(1, senders.Length + 1).Append(0))
                {
                    var pages = ReadPages(file, index); var checkpoint = Checkpoint(pages.Current);
                    var context = AnchorContext(index);
                    if (await _anchor.ReadAsync(context, ct).ConfigureAwait(false) is not null
                        || !await _anchor.TryAdvanceAsync(context, null, checkpoint, ct).ConfigureAwait(false))
                        throw Failure(DataSecureCounterFailure.UncertainAnchor, "Counter anchor initialization was not confirmed.");
                    _minimum[index] = checkpoint;
                }
            }
        }
        finally { _serial.Release(); }
    }

    public Task<DataSecureCounterRange> ReserveSendCountersAsync(int count = 1, CancellationToken ct = default)
    {
        if (count is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(count));
        return TransactionAsync(async (file, token) =>
        {
            await RequireReadyAsync(file, token).ConfigureAwait(false); var current = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
            if (current.Value > KnxDataSecureCodec.MaximumSequence - (ulong)count) throw Failure(DataSecureCounterFailure.Exhausted, "The send counter is exhausted; no sequence was allocated.");
            var next = current.Value + (ulong)count; await WriteCounterAsync(file, current, next, token).ConfigureAwait(false);
            return new DataSecureCounterRange(current.Value + 1, next);
        }, ct);
    }

    /// <summary>Commits a cryptographically verified sender sequence before any associated application data may be released.</summary>
    public Task<bool> TryAcceptReceiveAsync(IndividualAddress source, ulong sequence, CancellationToken ct = default)
    {
        if (sequence is 0 or > KnxDataSecureCodec.MaximumSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        var index = SenderIndex(source);
        return TransactionAsync(async (file, token) =>
        {
            await RequireReadyAsync(file, token).ConfigureAwait(false); var current = await ReadCounterAsync(file, index, token).ConfigureAwait(false);
            if (sequence <= current.Value) return false;
            if (source == _identity.LocalAddress)
            {
                var local = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
                if (sequence > local.Value) await WriteCounterAsync(file, local, sequence, token).ConfigureAwait(false);
            }
            await WriteCounterAsync(file, current, sequence, token).ConfigureAwait(false); return true;
        }, ct);
    }
    public Task<ulong> ReadReceiveFloorAsync(IndividualAddress source, CancellationToken ct = default)
    {
        var index = SenderIndex(source);
        return TransactionAsync(async (file, token) => (await ReadCounterAsync(file, index, token).ConfigureAwait(false)).Value, ct);
    }
    /// <summary>
    /// Commits authenticated peer Sync facts without clearing any existing restore gate. The caller must first verify
    /// the exact peer, key kind/key epoch, open challenge, frame authentication and timeout. Both values are next-valid
    /// sequences, not last-used values. This does not recover a lost trusted local highwater or create replacement state.
    /// </summary>
    public Task MergeAuthenticatedSyncAsync(IndividualAddress peer, ulong remoteNextSequence, ulong localNextSequence, CancellationToken ct = default)
    {
        if (peer == _identity.LocalAddress || remoteNextSequence is 0 or > KnxDataSecureCodec.MaximumSequence
            || localNextSequence is 0 or > KnxDataSecureCodec.MaximumSequence) throw new ArgumentOutOfRangeException(nameof(peer));
        var index = SenderIndex(peer);
        return TransactionAsync(async (file, token) =>
        {
            await RequireReadyAsync(file, token).ConfigureAwait(false);
            var local = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
            var remote = await ReadCounterAsync(file, index, token).ConfigureAwait(false);
            // Persist local nonce highwater first. Interrupted merges release no session readiness or payload.
            if (localNextSequence - 1 > local.Value) await WriteCounterAsync(file, local, localNextSequence - 1, token).ConfigureAwait(false);
            if (remoteNextSequence - 1 > remote.Value) await WriteCounterAsync(file, remote, remoteNextSequence - 1, token).ConfigureAwait(false);
            return true;
        }, ct);
    }
    public Task<bool> IsSynchronizationRequiredAsync(CancellationToken ct = default)
        => TransactionAsync(async (file, token) => (await ReadCounterAsync(file, 0, token).ConfigureAwait(false)).Value == 0, ct);
    public Task MarkSynchronizationRequiredAsync(CancellationToken ct = default)
        => TransactionAsync(async (file, token) =>
        {
            var ready = await ReadCounterAsync(file, 0, token).ConfigureAwait(false);
            if (ready.Value != 0) await WriteCounterAsync(file, ready, 0, token).ConfigureAwait(false); return true;
        }, ct);

    /// <summary>Resumes a marked restore only after trusted synchronization/provisioning supplies every known sender floor. Values never decrease.</summary>
    public Task ConfirmTrustedCountersAsync(DataSecureCounterBootstrap trusted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        if (!trusted.TrustedReceiveFloors.Keys.Select(a => a.Raw).Order().SequenceEqual(_manifest.Senders))
            throw new ArgumentException("Trusted synchronization must cover exactly the configured senders.", nameof(trusted));
        return TransactionAsync(async (file, token) =>
        {
            var ready = await ReadCounterAsync(file, 0, token).ConfigureAwait(false);
            if (ready.Value != 0) throw Failure(DataSecureCounterFailure.SynchronizationRequired, "Mark the restored state before confirming trusted counters.");
            var local = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
            var sendHighwater = trusted.TrustedNextSendSequence - 1;
            if (trusted.TrustedReceiveFloors.TryGetValue(_identity.LocalAddress, out var ownFloor))
            {
                var currentOwnFloor = await ReadCounterAsync(file, SenderIndex(_identity.LocalAddress), token).ConfigureAwait(false);
                sendHighwater = Math.Max(sendHighwater, Math.Max(ownFloor, currentOwnFloor.Value));
            }
            if (sendHighwater > local.Value) await WriteCounterAsync(file, local, sendHighwater, token).ConfigureAwait(false);
            foreach (var sender in _manifest.Senders)
            {
                var current = await ReadCounterAsync(file, SenderIndex(new(sender)), token).ConfigureAwait(false);
                var floor = trusted.TrustedReceiveFloors[new(sender)];
                if (floor > current.Value) await WriteCounterAsync(file, current, floor, token).ConfigureAwait(false);
            }
            // A crash before this durable readiness update leaves every ordinary send/receive blocked.
            await WriteCounterAsync(file, ready, 1, token).ConfigureAwait(false); return true;
        }, ct);
    }

    private async Task<T> TransactionAsync<T>(Func<FileStream, CancellationToken, Task<T>> action, CancellationToken ct)
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _serial.WaitAsync(source.Token).ConfigureAwait(false);
        try
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            using var lease = await LockFileAsync(source.Token).ConfigureAwait(false); using var file = OpenState();
            var manifest = ReadManifest(file);
            if (manifest.StoreId != _manifest.StoreId || manifest.HeaderLength != _manifest.HeaderLength || !manifest.Senders.SequenceEqual(_manifest.Senders))
                throw Failure(DataSecureCounterFailure.ContextMismatch, "The counter file was replaced with another store.");
            return await action(file, source.Token).ConfigureAwait(false);
        }
        finally { _serial.Release(); }
    }
    private async Task RequireReadyAsync(FileStream file, CancellationToken ct)
    {
        if ((await ReadCounterAsync(file, 0, ct).ConfigureAwait(false)).Value != 1)
            throw Failure(DataSecureCounterFailure.SynchronizationRequired, "Trusted synchronization is required before sending or releasing received data.");
    }
    private int SenderIndex(IndividualAddress source)
    {
        var index = Array.BinarySearch(_manifest.Senders, source.Raw);
        if (index < 0) throw Failure(DataSecureCounterFailure.ContextMismatch, "The sender is not registered in this counter context.");
        return index + 2;
    }

    private async Task<Page> ReadCounterAsync(FileStream file, int index, CancellationToken ct)
    {
        var pages = ReadPages(file, index); var page = pages.Current; var checkpoint = Checkpoint(page);
        if (_minimum.TryGetValue(index, out var minimum) && (checkpoint.Revision < minimum.Revision
            || checkpoint.Revision == minimum.Revision && checkpoint.Digest != minimum.Digest))
            throw Failure(DataSecureCounterFailure.SynchronizationRequired, "A previously observed counter checkpoint was rolled back.");
        if (_anchor is null)
        {
            if (pages.ValidCount != 2) throw Failure(DataSecureCounterFailure.SynchronizationRequired, "A counter page is incomplete; trusted synchronization is required.");
        }
        else
        {
            var context = AnchorContext(index); var anchor = await _anchor.ReadAsync(context, ct).ConfigureAwait(false);
            if (anchor is null || anchor.StoreId != _manifest.StoreId || !CanonicalDigest(anchor.Digest))
                throw Failure(DataSecureCounterFailure.UncertainAnchor, "The independent counter anchor is missing or inconsistent.");
            if (anchor.Revision == checkpoint.Revision && anchor.Digest == checkpoint.Digest) { }
            else if (anchor.Revision != ulong.MaxValue && checkpoint.Revision == anchor.Revision + 1 && Convert.ToHexString(page.PreviousDigest) == anchor.Digest)
            {
                if (!await _anchor.TryAdvanceAsync(context, anchor, checkpoint, ct).ConfigureAwait(false))
                    throw Failure(DataSecureCounterFailure.UncertainAnchor, "A pending counter checkpoint could not be confirmed.");
            }
            else throw Failure(DataSecureCounterFailure.UncertainAnchor, "The counter file and its independent anchor disagree.");
        }
        _minimum[index] = checkpoint; return page;
    }
    private async Task WriteCounterAsync(FileStream file, Page current, ulong value, CancellationToken ct)
    {
        if (current.Revision == ulong.MaxValue) throw Failure(DataSecureCounterFailure.Exhausted, "The state revision is exhausted.");
        var bytes = BuildPage(current.Index, current.Revision + 1, value, current.Digest); var bank = 1 - current.Bank;
        file.Position = PageOffset(current.Index, bank); await file.WriteAsync(bytes, ct).ConfigureAwait(false); file.Flush(flushToDisk: true);
        var updated = ParsePage(bytes, current.Index, bank)!; var checkpoint = Checkpoint(updated); _minimum[current.Index] = checkpoint;
        if (_anchor is not null && !await _anchor.TryAdvanceAsync(AnchorContext(current.Index), Checkpoint(current), checkpoint, ct).ConfigureAwait(false))
            throw Failure(DataSecureCounterFailure.UncertainAnchor, "The durable counter update has not been confirmed by its independent anchor.");
        ct.ThrowIfCancellationRequested();
    }

    private byte[] BuildManifest()
    {
        var result = new byte[_manifest.HeaderLength]; HeaderMagic.CopyTo(result, 0);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(8), result.Length); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12), _identity.LocalAddress.Raw);
        result[14] = _manifest.Anchored ? (byte)1 : (byte)0; _manifest.StoreId.TryWriteBytes(result.AsSpan(16, 16)); Convert.FromHexString(_identity.Fingerprint).CopyTo(result, 32);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(64), _manifest.Senders.Length);
        for (var index = 0; index < _manifest.Senders.Length; index++) BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(68 + index * 2), _manifest.Senders[index]);
        HMACSHA256.HashData(_integrityKey, result.AsSpan(0, result.Length - 32), result.AsSpan(result.Length - 32)); return result;
    }
    private Manifest ReadManifest(FileStream file)
    {
        Span<byte> prefix = stackalloc byte[68]; file.Position = 0;
        try { file.ReadExactly(prefix); } catch (EndOfStreamException) { throw Failure(DataSecureCounterFailure.CorruptState, "The counter-state manifest is incomplete."); }
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix[8..]); var count = BinaryPrimitives.ReadInt32BigEndian(prefix[64..]);
        if (!prefix[..8].SequenceEqual(HeaderMagic) || count is < 0 or > MaximumSenders || length != (68 + count * 2 + 32 + PageSize - 1) / PageSize * PageSize
            || file.Length != length + checked((long)(count + 2) * PageSize * 2)) throw Failure(DataSecureCounterFailure.CorruptState, "The counter-state file has invalid geometry.");
        var bytes = new byte[length]; file.Position = 0; file.ReadExactly(bytes); Span<byte> expected = stackalloc byte[32]; HMACSHA256.HashData(_integrityKey, bytes.AsSpan(0, length - 32), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(length - 32)) || bytes[15] != 0 || bytes[14] > 1)
            throw Failure(DataSecureCounterFailure.CorruptState, "The counter-state manifest failed authentication.");
        if (BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(12)) != _identity.LocalAddress.Raw
            || !CryptographicOperations.FixedTimeEquals(bytes.AsSpan(32, 32), Convert.FromHexString(_identity.Fingerprint)) || (bytes[14] == 1) != (_anchor is not null))
            throw Failure(DataSecureCounterFailure.ContextMismatch, "The counter file belongs to another sender, project, epoch or anchor policy.");
        var senders = new ushort[count];
        for (var index = 0; index < count; index++)
        {
            senders[index] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(68 + index * 2));
            if (senders[index] == 0 || (senders[index] & 255) == 255 || index > 0 && senders[index] <= senders[index - 1])
                throw Failure(DataSecureCounterFailure.CorruptState, "The sender manifest is invalid.");
        }
        return new(new Guid(bytes.AsSpan(16, 16)), length, senders, bytes[14] == 1);
    }
    private byte[] BuildPage(int index, ulong revision, ulong value, byte[] previousDigest)
    {
        if (index == 0 ? value > 1 : value > KnxDataSecureCodec.MaximumSequence) throw Failure(DataSecureCounterFailure.CorruptState, "Invalid counter value.");
        var result = new byte[PageSize]; PageMagic.CopyTo(result, 0); _manifest.StoreId.TryWriteBytes(result.AsSpan(8, 16)); Convert.FromHexString(_identity.Fingerprint).CopyTo(result, 24);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(56), index); BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(60), PageSender(index)); result[62] = PageKind(index);
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(64), revision); BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(72), value); previousDigest.CopyTo(result, 80);
        HMACSHA256.HashData(_integrityKey, result.AsSpan(0, MacOffset), result.AsSpan(MacOffset)); return result;
    }
    private PageSet ReadPages(FileStream file, int index)
    {
        var pages = new Page?[2];
        for (var bank = 0; bank < 2; bank++) { var bytes = new byte[PageSize]; file.Position = PageOffset(index, bank); file.ReadExactly(bytes); pages[bank] = ParsePage(bytes, index, bank); }
        var valid = pages.OfType<Page>().OrderBy(p => p.Revision).ToArray();
        if (valid.Length == 0) throw Failure(DataSecureCounterFailure.CorruptState, "Both counter pages failed authentication.");
        if (valid.Length == 2)
        {
            if (valid[0].Revision == valid[1].Revision && !valid[0].Digest.SequenceEqual(valid[1].Digest)
                || valid[0].Revision != valid[1].Revision && (valid[0].Revision == ulong.MaxValue || valid[1].Revision != valid[0].Revision + 1 || !valid[1].PreviousDigest.SequenceEqual(valid[0].Digest)))
                throw Failure(DataSecureCounterFailure.CorruptState, "The two counter revisions are inconsistent.");
        }
        return new(valid[^1], valid.Length);
    }
    private Page? ParsePage(byte[] bytes, int index, int bank)
    {
        Span<byte> expected = stackalloc byte[32]; HMACSHA256.HashData(_integrityKey, bytes.AsSpan(0, MacOffset), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, bytes.AsSpan(MacOffset)) || !bytes.AsSpan(0, 8).SequenceEqual(PageMagic)
            || new Guid(bytes.AsSpan(8, 16)) != _manifest.StoreId || !bytes.AsSpan(24, 32).SequenceEqual(Convert.FromHexString(_identity.Fingerprint))
            || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(56)) != index || BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(60)) != PageSender(index)
            || bytes[62] != PageKind(index) || bytes[63] != 0) return null;
        var value = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(72)); if (index == 0 ? value > 1 : value > KnxDataSecureCodec.MaximumSequence) return null;
        return new(index, bank, BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(64)), value, bytes.AsSpan(80, 32).ToArray(), bytes.AsSpan(MacOffset, 32).ToArray());
    }
    private ushort PageSender(int index) => index == 0 ? (ushort)0 : index == 1 ? _identity.LocalAddress.Raw : _manifest.Senders[index - 2];
    private static byte PageKind(int index) => index == 0 ? (byte)0 : index == 1 ? (byte)1 : (byte)2;
    private long PageOffset(int index, int bank) => _manifest.HeaderLength + ((long)index * 2 + bank) * PageSize;
    private DataSecureCounterCheckpoint Checkpoint(Page page) => new(_manifest.StoreId, page.Revision, Convert.ToHexString(page.Digest));
    private string AnchorContext(int index) => $"BusChord.DataSecureCounter2:{_identity.Fingerprint}:{index}";
    private static bool CanonicalDigest(string value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private sealed record Manifest(Guid StoreId, int HeaderLength, ushort[] Senders, bool Anchored);
    private sealed record Page(int Index, int Bank, ulong Revision, ulong Value, byte[] PreviousDigest, byte[] Digest);
    private sealed record PageSet(Page Current, int ValidCount);

    private FileStream OpenState()
    {
        PrivatePath(_path);
        try { return new(_path, Options(FileMode.Open, FileAccess.ReadWrite, FileShare.Read)); }
        catch (FileNotFoundException) { throw Failure(DataSecureCounterFailure.MissingState, "Counter state is missing. Trusted provisioning or synchronization is required; no state was created."); }
    }
    private async Task<FileStream> LockFileAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew(); var lockPath = _path + ".lock";
        for (;;)
        {
            ct.ThrowIfCancellationRequested(); PrivatePath(lockPath);
            try { return new(lockPath, Options(FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(30)) { await Task.Delay(10, ct).ConfigureAwait(false); }
            catch (IOException) { throw Failure(DataSecureCounterFailure.Occupied, "Counter state remains occupied by another process."); }
        }
    }
    private static FileStreamOptions Options(FileMode mode, FileAccess access, FileShare share)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = share, Options = FileOptions.WriteThrough, BufferSize = PageSize };
        if (OperatingSystem.IsLinux() && mode is FileMode.CreateNew or FileMode.OpenOrCreate) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }
    private static string PrivatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw Failure(DataSecureCounterFailure.UnsupportedPath, "Counter state requires an absolute path outside a Git checkout.");
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) throw Failure(DataSecureCounterFailure.UnsupportedPath, "Counter state must name a private file.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure(DataSecureCounterFailure.UnsupportedPath, "Counter paths cannot use filesystem links.");
        var parent = Directory.GetParent(path);
        if (parent is null || !parent.Exists) throw Failure(DataSecureCounterFailure.UnsupportedPath, "Create the private counter directory explicitly before provisioning.");
        for (var directory = parent; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0 || File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
                throw Failure(DataSecureCounterFailure.UnsupportedPath, "Counter paths cannot use filesystem links or Git checkouts.");
        }
        return path;
    }
    private static DataSecureCounterStateException Failure(DataSecureCounterFailure failure, string message) => new(failure, message);
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? owner = null; Task task;
        lock (_gate) { if (_disposal is null) { _disposed = true; owner = new(TaskCreationOptions.RunContinuationsAsynchronously); _disposal = owner.Task; } task = _disposal; }
        if (owner is not null)
        {
            Exception? cancellationFailure = null; try { _lifetime.Cancel(); } catch (Exception ex) { cancellationFailure = ex; }
            _ = FinishDisposeAsync(owner, cancellationFailure);
        }
        return new(task);
    }
    private async Task FinishDisposeAsync(TaskCompletionSource completion, Exception? cancellationFailure)
    {
        try
        {
            await _serial.WaitAsync().ConfigureAwait(false);
            try { CryptographicOperations.ZeroMemory(_integrityKey); } finally { _serial.Release(); _lifetime.Dispose(); }
            if (cancellationFailure is not null) completion.TrySetException(cancellationFailure); else completion.TrySetResult();
        }
        catch (Exception ex) { completion.TrySetException(ex); }
    }
}

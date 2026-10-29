using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Knx.Bus.Security;

/// <summary>
/// No-unlock Secret Service client. libsecret owns negotiated DH/AES transfer sessions; direct GIO mutations deliberately
/// inspect returned prompt paths instead of using libsecret's automatically prompting store/delete convenience methods.
/// Public protocol: https://specifications.freedesktop.org/secret-service/latest/ .
/// Only an exact app/binding attribute search is issued; collection contents are never enumerated.
/// </summary>
internal sealed class LibsecretCredentialSession : ILinuxSecretServiceSession
{
    private const string ServiceName = "org.freedesktop.secrets", ItemInterface = "org.freedesktop.Secret.Item", CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string Schema = "org.buschord.KeyringPassword.v1", BinaryContent = "application/octet-stream", Transfer = "dh-ietf1024-sha256-aes128-cbc-pkcs7";
    private readonly LibsecretNative _n;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(10));
    private readonly CancellationTokenSource _bounded;
    private CancellationTokenRegistration _registration;
    private IntPtr _cancel, _service;
    private string _collection = "";
    public bool DefaultCollectionLocked
    {
        get { using var proxy = Proxy(_collection, CollectionInterface); return Locked(proxy.Pointer, CollectionInterface); }
    }
    internal LibsecretCredentialSession(CancellationToken ct)
    {
        _caller = ct; _bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, _deadline.Token);
        try
        {
            _n = LibsecretNative.Shared.Value; CheckCancellation(); _cancel = _n.CancellableNew();
            if (_cancel == IntPtr.Zero) throw Failure(SecureCredentialFailure.Unavailable);
            _registration = _bounded.Token.Register(() => _n.CancellableCancel(_cancel));
            // A private owned proxy/session; do not retain secrets/session keys in libsecret's default-service cache.
            _service = _n.Service(_n.ServiceType(), ServiceName, 0, _cancel, out var error); CheckError(error);
            if (_service == IntPtr.Zero) throw Failure(SecureCredentialFailure.Unavailable);
            if (_n.Ensure(_service, _cancel, out error) == 0) { CheckError(error); throw Failure(SecureCredentialFailure.Unavailable); }
            CheckError(error);
            if (ReadText(_n.Algorithms(_service), 128) != Transfer) throw Failure(SecureCredentialFailure.Unavailable);
            var alias = _n.Alias(_service, "default", _cancel, out error);
            try { CheckError(error); _collection = ReadText(alias, 4096); if (_collection == "/" || !ObjectPath(_collection)) throw Failure(SecureCredentialFailure.Unavailable); }
            finally { if (alias != IntPtr.Zero) _n.Free(alias); }
        }
        catch { Dispose(); throw; }
    }
    public IReadOnlyList<LinuxSecretMatch> FindExact(SecureCredentialBinding binding)
    {
        CheckCancellation(); using var attrs = Attributes(binding); IntPtr unlocked = IntPtr.Zero, locked = IntPtr.Zero;
        try
        {
            var ok = _n.Search(_service, IntPtr.Zero, attrs.Pointer, _cancel, out unlocked, out locked, out var error); CheckError(error);
            if (ok == 0) throw Failure(SecureCredentialFailure.NativeFailure);
            var result = new List<LinuxSecretMatch>();
            void Add(IntPtr paths, bool isLocked)
            {
                if (paths == IntPtr.Zero) return;
                for (var i = 0; i < 3; i++)
                {
                    var pointer = Marshal.ReadIntPtr(paths, i * IntPtr.Size); if (pointer == IntPtr.Zero) return;
                    var path = ReadText(pointer, 4096); if (!ObjectPath(path)) throw Failure(SecureCredentialFailure.NativeFailure);
                    result.Add(new(path, isLocked)); if (result.Count > 1) throw Failure(SecureCredentialFailure.Ambiguous);
                }
                throw Failure(SecureCredentialFailure.Ambiguous);
            }
            Add(unlocked, false); Add(locked, true); return result.AsReadOnly();
        }
        finally { if (unlocked != IntPtr.Zero) _n.StringVectorFree(unlocked); if (locked != IntPtr.Zero) _n.StringVectorFree(locked); }
    }
    public void VerifyExactAttributes(string itemPath, SecureCredentialBinding binding)
    {
        using var proxy = Proxy(itemPath, ItemInterface);
        if (Locked(proxy.Pointer, ItemInterface)) throw Failure(SecureCredentialFailure.Locked);
        using var attributes = Property(proxy.Pointer, ItemInterface, "Attributes"); RequireType(attributes.Pointer, "a{ss}");
        if (_n.Count(attributes.Pointer) != 3) throw Failure(SecureCredentialFailure.BindingMismatch);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        for (nuint index = 0; index < 3; index++)
        {
            using var pair = Owned(_n.Child(attributes.Pointer, index), _n.VariantUnref); RequireType(pair.Pointer, "{ss}");
            using var key = Owned(_n.Child(pair.Pointer, 0), _n.VariantUnref); using var value = Owned(_n.Child(pair.Pointer, 1), _n.VariantUnref);
            if (!found.TryAdd(VariantString(key.Pointer), VariantString(value.Pointer))) throw Failure(SecureCredentialFailure.BindingMismatch);
        }
        if (found.GetValueOrDefault("xdg:schema") != Schema || found.GetValueOrDefault("app") != binding.NamespaceName || found.GetValueOrDefault("binding") != binding.Id)
            throw Failure(SecureCredentialFailure.BindingMismatch);
    }
    public byte[] ReadOwned(string itemPath)
    {
        CheckCancellation(); var value = _n.Get(_service, itemPath, _cancel, out var error);
        try
        {
            CheckError(error); if (value == IntPtr.Zero) throw Failure(SecureCredentialFailure.Locked);
            if (!SupportedContentType(ReadText(_n.ContentType(value), 128))) throw Failure(SecureCredentialFailure.BindingMismatch);
            var bytes = _n.Value(value, out var length);
            if (bytes == IntPtr.Zero || length is <= SecureCredentialEnvelope.HeaderLength or > SecureCredentialEnvelope.MaximumBlobBytes) throw Failure(SecureCredentialFailure.BindingMismatch);
            var copy = new byte[(int)length]; Marshal.Copy(bytes, copy, 0, copy.Length); return copy;
        }
        finally { if (value != IntPtr.Zero) _n.ValueUnref(value); }
    }
    // GNOME Keyring preserves the value bytes but always returns text/plain in gkd_secret_secret_append().
    // MIME metadata is not the ownership boundary: the exact attributes and bound binary envelope remain mandatory.
    // https://github.com/GNOME/gnome-keyring/blob/main/daemon/dbus/gkd-secret-secret.c
    internal static bool SupportedContentType(string value) => value is BinaryContent or "text/plain";
    public void Create(SecureCredentialBinding binding, byte[] borrowedBlob)
    {
        using var proxy = Proxy(_collection, CollectionInterface); if (Locked(proxy.Pointer, CollectionInterface)) throw Failure(SecureCredentialFailure.Locked);
        using var properties = ItemProperties(binding); using var secret = EncodedSecret(borrowedBlob); using var replace = Sink(_n.Boolean(0));
        using var args = Tuple(properties, secret, replace); using var result = Call(proxy.Pointer, "CreateItem", args, true); RequireType(result.Pointer, "(oo)", true);
        using var item = Owned(_n.Child(result.Pointer, 0), _n.VariantUnref); using var prompt = Owned(_n.Child(result.Pointer, 1), _n.VariantUnref);
        var promptPath = VariantString(prompt.Pointer, "o");
        if (promptPath != "/") { DismissOwnPrompt(promptPath); throw Failure(SecureCredentialFailure.Locked); }
        if (!ObjectPath(VariantString(item.Pointer, "o")) || VariantString(item.Pointer, "o") == "/") throw Failure(SecureCredentialFailure.MutationOutcomeUnknown);
    }
    public void Replace(string itemPath, byte[] borrowedBlob)
    {
        using var proxy = Proxy(itemPath, ItemInterface); if (Locked(proxy.Pointer, ItemInterface)) throw Failure(SecureCredentialFailure.Locked);
        using var secret = EncodedSecret(borrowedBlob); using var args = Tuple(secret); using var result = Call(proxy.Pointer, "SetSecret", args, true); RequireType(result.Pointer, "()", true);
    }
    public void Delete(string itemPath)
    {
        using var proxy = Proxy(itemPath, ItemInterface); if (Locked(proxy.Pointer, ItemInterface)) throw Failure(SecureCredentialFailure.Locked);
        using var args = Tuple(); using var result = Call(proxy.Pointer, "Delete", args, true); RequireType(result.Pointer, "(o)", true);
        using var prompt = Owned(_n.Child(result.Pointer, 0), _n.VariantUnref); var promptPath = VariantString(prompt.Pointer, "o");
        if (promptPath != "/") { DismissOwnPrompt(promptPath); throw Failure(SecureCredentialFailure.Locked); }
    }
    private void DismissOwnPrompt(string path)
    {
        if (!ObjectPath(path) || path == "/") return;
        try { using var proxy = Proxy(path, "org.freedesktop.Secret.Prompt"); using var args = Tuple(); using var ignored = Call(proxy.Pointer, "Dismiss", args, false); }
        catch { /* No Prompt() or Unlock() was called; failure to dismiss cannot authorize the pending mutation. */ }
    }
    private Lease Attributes(SecureCredentialBinding binding)
    {
        var table = _n.HashNew(_n.StringHash, _n.StringEqual, _n.FreePointer, _n.FreePointer); var owner = Owned(table, _n.HashUnref);
        try { foreach (var (key, value) in new[] { ("xdg:schema", Schema), ("app", binding.NamespaceName), ("binding", binding.Id) }) _n.Insert(table, _n.Duplicate(key), _n.Duplicate(value)); return owner; }
        catch { owner.Dispose(); throw; }
    }
    private Lease ItemProperties(SecureCredentialBinding binding)
    {
        var entries = new List<Lease>(); var owned = new List<Lease>();
        try
        {
            var attributes = new List<Lease>();
            foreach (var (key, value) in new[] { ("xdg:schema", Schema), ("app", binding.NamespaceName), ("binding", binding.Id) })
            {
                var keyValue = Sink(_n.String(key)); var valueValue = Sink(_n.String(value)); owned.Add(keyValue); owned.Add(valueValue);
                var entry = Sink(_n.Pair(keyValue.Pointer, valueValue.Pointer)); attributes.Add(entry); owned.Add(entry);
            }
            var attributeMap = Array("{ss}", attributes); owned.Add(attributeMap);
            var label = Sink(_n.String("BusChord protected keyring password")); owned.Add(label);
            foreach (var (name, value) in new[] { (ItemInterface + ".Label", label), (ItemInterface + ".Attributes", attributeMap) })
            {
                var key = Sink(_n.String(name)); var variant = Sink(_n.VariantVariant(value.Pointer)); owned.Add(key); owned.Add(variant);
                var entry = Sink(_n.Pair(key.Pointer, variant.Pointer)); entries.Add(entry); owned.Add(entry);
            }
            return Array("{sv}", entries);
        }
        finally { for (var i = owned.Count - 1; i >= 0; i--) owned[i].Dispose(); }
    }
    private Lease EncodedSecret(byte[] borrowedBlob)
    {
        var pin = GCHandle.Alloc(borrowedBlob, GCHandleType.Pinned); IntPtr value = IntPtr.Zero;
        try
        {
            value = _n.NewValue(pin.AddrOfPinnedObject(), borrowedBlob.Length, BinaryContent);
            if (value == IntPtr.Zero) throw Failure(SecureCredentialFailure.NativeFailure);
            var encoded = _n.Encode(_service, value); if (encoded == IntPtr.Zero) throw Failure(SecureCredentialFailure.NativeFailure);
            return Sink(encoded);
        }
        finally { if (value != IntPtr.Zero) _n.ValueUnref(value); pin.Free(); }
    }
    private Lease Property(IntPtr proxy, string iface, string name)
    {
        using var interfaceValue = Sink(_n.String(iface)); using var nameValue = Sink(_n.String(name)); using var args = Tuple(interfaceValue, nameValue);
        using var result = Call(proxy, "org.freedesktop.DBus.Properties.Get", args, false); RequireType(result.Pointer, "(v)");
        using var variant = Owned(_n.Child(result.Pointer, 0), _n.VariantUnref); return Owned(_n.GetVariant(variant.Pointer), _n.VariantUnref);
    }
    private bool Locked(IntPtr proxy, string iface) { using var value = Property(proxy, iface, "Locked"); RequireType(value.Pointer, "b"); return _n.Bool(value.Pointer) != 0; }
    private Lease Proxy(string path, string iface)
    {
        CheckCancellation(); if (!ObjectPath(path)) throw Failure(SecureCredentialFailure.NativeFailure);
        // DO_NOT_LOAD_PROPERTIES | DO_NOT_CONNECT_SIGNALS | DO_NOT_AUTO_START.
        // In particular, never fetch a collection's Items property as a side effect of proxy construction.
        var proxy = _n.Proxy(_n.ProxyConnection(_service), 7, IntPtr.Zero, ServiceName, path, iface, _cancel, out var error);
        try { CheckError(error); return Owned(proxy, _n.ObjectUnref); }
        catch { if (proxy != IntPtr.Zero) _n.ObjectUnref(proxy); throw; }
    }
    private Lease Call(IntPtr proxy, string method, Lease args, bool mutation)
    {
        CheckCancellation(); var reply = _n.Call(proxy, method, args.Pointer, 0, 5000, _cancel, out var error);
        try { CheckError(error, mutation); return Owned(reply, _n.VariantUnref); }
        catch { if (reply != IntPtr.Zero) _n.VariantUnref(reply); throw; }
    }
    private Lease Tuple(params Lease[] values) => Sink(_n.Tuple(values.Select(value => value.Pointer).ToArray(), (nuint)values.Length));
    private Lease Array(string elementSignature, IReadOnlyCollection<Lease> values)
    { var type = _n.Type(elementSignature); try { return Sink(_n.Array(type, values.Select(value => value.Pointer).ToArray(), (nuint)values.Count)); } finally { _n.TypeFree(type); } }
    private Lease Sink(IntPtr pointer) => pointer != IntPtr.Zero ? Owned(_n.VariantRefSink(pointer), _n.VariantUnref) : throw Failure(SecureCredentialFailure.NativeFailure);
    private static Lease Owned(IntPtr pointer, LibsecretNative.Release release)
        => pointer != IntPtr.Zero ? new(pointer, release) : throw Failure(SecureCredentialFailure.NativeFailure);
    private sealed class Lease(IntPtr pointer, LibsecretNative.Release release) : IDisposable
    { private IntPtr _pointer = pointer; public IntPtr Pointer => _pointer; public void Dispose() { var value = Interlocked.Exchange(ref _pointer, IntPtr.Zero); if (value != IntPtr.Zero) release(value); } }
    private void RequireType(IntPtr value, string expected, bool mutation = false)
    { if (ReadText(_n.VariantType(value), 128) != expected) throw Failure(mutation ? SecureCredentialFailure.MutationOutcomeUnknown : SecureCredentialFailure.NativeFailure); }
    private string VariantString(IntPtr value, string expected = "s")
    {
        RequireType(value, expected); var pointer = _n.Text(value, out var length);
        if (length > 4096) throw Failure(SecureCredentialFailure.NativeFailure); return ReadText(pointer, checked((int)length + 1));
    }
    private static string ReadText(IntPtr value, int maximum)
    {
        if (value == IntPtr.Zero) return ""; var bytes = new List<byte>();
        for (var i = 0; i < maximum; i++) { var next = Marshal.ReadByte(value, i); if (next == 0) return new UTF8Encoding(false, true).GetString(bytes.ToArray()); bytes.Add(next); }
        throw Failure(SecureCredentialFailure.NativeFailure);
    }
    private static bool ObjectPath(string path) => path.Length is > 0 and <= 4096 && path[0] == '/' && (path == "/" || path[1..].Split('/').All(part => part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')));
    [StructLayout(LayoutKind.Sequential)] private struct GError { public uint Domain; public int Code; public IntPtr Message; }
    private void CheckError(IntPtr error, bool mutation = false)
    {
        if (error == IntPtr.Zero) return;
        try
        {
            var value = Marshal.PtrToStructure<GError>(error); var remote = _n.RemoteErrorName(error); string name;
            try { name = ReadText(remote, 256); } finally { if (remote != IntPtr.Zero) _n.Free(remote); }
            if (value.Domain == _n.SecretErrorQuark() && value.Code == 2 || name == "org.freedesktop.Secret.Error.IsLocked") throw Failure(SecureCredentialFailure.Locked);
            if (value.Domain == _n.SecretErrorQuark() && value.Code == 3 || name == "org.freedesktop.Secret.Error.NoSuchObject") throw Failure(SecureCredentialFailure.NotFound);
            if (_caller.IsCancellationRequested) { if (mutation) throw Failure(SecureCredentialFailure.MutationOutcomeUnknown); throw new OperationCanceledException(_caller); }
            throw Failure(mutation ? SecureCredentialFailure.MutationOutcomeUnknown : SecureCredentialFailure.Unavailable);
        }
        finally { _n.ErrorFree(error); }
    }
    private void CheckCancellation()
    { _caller.ThrowIfCancellationRequested(); if (_deadline.IsCancellationRequested) throw Failure(SecureCredentialFailure.Unavailable); }
    private static SecureCredentialVaultException Failure(SecureCredentialFailure failure) => new(failure);
    private void CloseOwnTransferSession()
    {
        // libsecret frees the local session key on unref, but does not issue the protocol Session.Close itself.
        // Close only the session created by this private proxy. Cleanup is bounded and independent of a cancelled request.
        try
        {
            var path = ReadText(_n.SessionPath(_service), 4096); if (path == "/" || !ObjectPath(path)) return;
            using var args = Tuple(); var reply = _n.DirectCall(_n.ProxyConnection(_service), ServiceName, path, "org.freedesktop.Secret.Session", "Close", args.Pointer, IntPtr.Zero, 1, 1000, IntPtr.Zero, out var error);
            if (reply != IntPtr.Zero) _n.VariantUnref(reply); if (error != IntPtr.Zero) _n.ErrorFree(error);
        }
        catch { /* Remote cleanup must never bypass release/zeroing of the locally owned session and secret values. */ }
    }
    public void Dispose()
    {
        _registration.Dispose();
        if (_service != IntPtr.Zero) { CloseOwnTransferSession(); _n.ObjectUnref(_service); _service = IntPtr.Zero; }
        if (_cancel != IntPtr.Zero) { _n.ObjectUnref(_cancel); _cancel = IntPtr.Zero; }
        _bounded.Dispose(); _deadline.Dispose();
    }
}

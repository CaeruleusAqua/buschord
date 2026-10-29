using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Knx.Bus.Security;

/// <summary>Ephemeral RFC 7748 X25519 using the operating system provider, never managed scalar arithmetic.</summary>
public sealed class OsX25519KeyAgreement : IDisposable
{
    private readonly object _gate = new();
    private ECDiffieHellman? _windows;
    private OpenSslKey? _opensslKey;
    private byte[] _publicKey = [];
    private bool _disposed;
    public byte[] PublicKey { get { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _publicKey.ToArray(); } } }

    private OsX25519KeyAgreement() { }

    public static OsX25519KeyAgreement Create()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = new OsX25519KeyAgreement();
            try
            {
                result._windows = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("curve25519"));
                var parameters = result._windows.ExportParameters(false);
                if (parameters.Q.X is not { Length: 32 } || parameters.Q.Y is not { Length: 32 }
                    || parameters.Q.Y.Any(b => b != 0)) throw new PlatformNotSupportedException("The Windows provider cannot export RFC 7748 keys.");
                result._publicKey = parameters.Q.X;
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        var seed = RandomNumberGenerator.GetBytes(32);
        try { return ImportPrivateKey(seed); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>Imports a 32-byte RFC 7748 private scalar. Input is copied and never retained as a managed field.</summary>
    public static OsX25519KeyAgreement ImportPrivateKey(ReadOnlySpan<byte> scalar)
    {
        if (scalar.Length != 32) throw new ArgumentException("An X25519 private scalar is 32 bytes.", nameof(scalar));
        var seed = scalar.ToArray(); seed[0] &= 248; seed[31] = (byte)((seed[31] & 127) | 64);
        var result = new OsX25519KeyAgreement();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                result._windows = ECDiffieHellman.Create(new ECParameters
                {
                    Curve = ECCurve.CreateFromFriendlyName("curve25519"), D = seed,
                    Q = new ECPoint { X = new byte[32], Y = new byte[32] }
                });
                result._publicKey = result._windows.ExportParameters(false).Q.X!;
            }
            else if (OperatingSystem.IsLinux())
            {
                var native = OpenSsl.Instance;
                var key = native.NewPrivate(IntPtr.Zero, "X25519", IntPtr.Zero, seed, 32);
                if (key == IntPtr.Zero) throw new CryptographicException("The OpenSSL X25519 private key was refused.");
                result._opensslKey = new(key, native.FreeKey);
                result._publicKey = new byte[32]; nuint size = 32;
                if (native.GetPublic(key, result._publicKey, ref size) != 1 || size != 32)
                    throw new CryptographicException("The OpenSSL X25519 public key could not be exported.");
            }
            else throw new PlatformNotSupportedException("X25519 requires Windows CNG or Linux OpenSSL 3 in this build.");
            if (result._publicKey.Length != 32) throw new CryptographicException("The X25519 provider returned an invalid public key.");
            return result;
        }
        catch { result.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>Returns the raw 32-byte little-endian shared secret; the caller must clear it after deriving its session key.</summary>
    public byte[] DeriveRawSecret(ReadOnlySpan<byte> peerPublicKey)
    {
        if (peerPublicKey.Length != 32) throw new ArgumentException("An X25519 public key is 32 bytes.", nameof(peerPublicKey));
        var peer = peerPublicKey.ToArray(); peer[31] &= 127;
        // The peer coordinate is public. Reduce the at most 19 noncanonical encodings without secret arithmetic.
        var noncanonical = peer[31] == 127 && peer.Skip(1).Take(30).All(b => b == 255) && peer[0] >= 237;
        if (noncanonical) { var low = peer[0] - 237; Array.Clear(peer); peer[0] = (byte)low; }
        if (peer.All(b => b == 0)) throw new CryptographicException("Invalid X25519 peer key.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] secret;
            if (_windows is not null)
            {
                try
                {
                    using var remote = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.CreateFromFriendlyName("curve25519"), Q = new ECPoint { X = peer, Y = new byte[32] } });
                    secret = _windows.DeriveRawSecretAgreement(remote.PublicKey);
                }
                catch (PlatformNotSupportedException ex) when (ex.InnerException is CryptographicException)
                { throw new CryptographicException("The X25519 peer key was refused.", ex); }
            }
            else
            {
                var native = OpenSsl.Instance;
                var remote = native.NewPublic(IntPtr.Zero, "X25519", IntPtr.Zero, peer, 32);
                if (remote == IntPtr.Zero) throw new CryptographicException("The OpenSSL X25519 peer key was refused.");
                var context = IntPtr.Zero; secret = new byte[32]; var keyReference = false;
                try
                {
                    _opensslKey!.DangerousAddRef(ref keyReference);
                    context = native.NewContext(IntPtr.Zero, _opensslKey!.DangerousGetHandle(), IntPtr.Zero); nuint size = 32;
                    if (context == IntPtr.Zero || native.DeriveInit(context) != 1 || native.SetPeer(context, remote) != 1
                        || native.Derive(context, secret, ref size) != 1 || size != 32)
                        throw new CryptographicException("The OpenSSL X25519 agreement failed.");
                }
                catch { CryptographicOperations.ZeroMemory(secret); throw; }
                finally
                {
                    if (context != IntPtr.Zero) native.FreeContext(context); native.FreeKey(remote);
                    if (keyReference) _opensslKey!.DangerousRelease();
                }
            }
            if (secret.Length != 32 || CryptographicOperations.FixedTimeEquals(secret, new byte[secret.Length]))
            { CryptographicOperations.ZeroMemory(secret); throw new CryptographicException("Invalid X25519 shared secret."); }
            return secret;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            _windows?.Dispose(); _windows = null;
            _opensslKey?.Dispose(); _opensslKey = null;
            Array.Clear(_publicKey); _publicKey = [];
        }
    }

    private sealed class OpenSslKey : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly OpenSsl.Free _free;
        internal OpenSslKey(IntPtr key, OpenSsl.Free free) : base(true) { _free = free; SetHandle(key); }
        protected override bool ReleaseHandle() { _free(handle); return true; }
    }

    private sealed class OpenSsl
    {
        // The process-wide binding owns the library for process lifetime; no delegate can outlive its native module.
        private readonly IntPtr _library;
        private static readonly Lazy<OpenSsl> Binding = new(() => new OpenSsl());
        internal static OpenSsl Instance => Binding.Value;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] internal delegate IntPtr NewRaw(IntPtr context, string type, IntPtr properties, byte[] key, nuint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Export(IntPtr key, byte[] output, ref nuint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Context(IntPtr context, IntPtr key, IntPtr properties);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Init(IntPtr context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Peer(IntPtr context, IntPtr peer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Agreement(IntPtr context, byte[] output, ref nuint length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Free(IntPtr value);
        internal readonly NewRaw NewPrivate, NewPublic;
        internal readonly Export GetPublic;
        internal readonly Context NewContext;
        internal readonly Init DeriveInit;
        internal readonly Peer SetPeer;
        internal readonly Agreement Derive;
        internal readonly Free FreeKey, FreeContext;
        private OpenSsl()
        {
            if (!OperatingSystem.IsLinux() || !NativeLibrary.TryLoad("libcrypto.so.3", typeof(OsX25519KeyAgreement).Assembly,
                DllImportSearchPath.SafeDirectories, out _library)) throw new PlatformNotSupportedException("Linux X25519 requires OpenSSL 3.");
            try
            {
                T Bind<T>(string symbol) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, symbol));
                NewPrivate = Bind<NewRaw>("EVP_PKEY_new_raw_private_key_ex"); NewPublic = Bind<NewRaw>("EVP_PKEY_new_raw_public_key_ex");
                GetPublic = Bind<Export>("EVP_PKEY_get_raw_public_key"); NewContext = Bind<Context>("EVP_PKEY_CTX_new_from_pkey");
                DeriveInit = Bind<Init>("EVP_PKEY_derive_init"); SetPeer = Bind<Peer>("EVP_PKEY_derive_set_peer"); Derive = Bind<Agreement>("EVP_PKEY_derive");
                FreeKey = Bind<Free>("EVP_PKEY_free"); FreeContext = Bind<Free>("EVP_PKEY_CTX_free");
            }
            catch (EntryPointNotFoundException ex) { NativeLibrary.Free(_library); throw new PlatformNotSupportedException("The installed OpenSSL does not expose X25519 raw-key operations.", ex); }
        }
    }
}

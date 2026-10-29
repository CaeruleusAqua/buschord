using System.Runtime.InteropServices;

namespace Knx.Bus.Security;

/// <summary>
/// ABI bindings to installed libsecret/GIO/GLib. Modules stay loaded for process lifetime so callbacks/handles never outlive
/// their symbols. No password CLI or library-install fallback is present. Public declarations: GNOME/libsecret headers,
/// https://gnome.pages.gitlab.gnome.org/libsecret/ and https://docs.gtk.org/gio/.
/// </summary>
internal sealed class LibsecretNative
{
    internal static readonly Lazy<LibsecretNative> Shared = new(() => new());
    private readonly IntPtr[] _modules;
    internal readonly ServiceOpen Service; internal readonly TypeGet ServiceType; internal readonly EnsureSession Ensure; internal readonly StringPointer Algorithms, SessionPath;
    internal readonly AliasRead Alias; internal readonly SearchPaths Search; internal readonly GetSecret Get; internal readonly EncodeSecret Encode;
    internal readonly ValueNew NewValue; internal readonly ValueGet Value; internal readonly StringPointer ContentType;
    internal readonly Release ValueUnref, ObjectUnref, Free, ErrorFree, StringVectorFree, VariantUnref, TypeFree, HashUnref;
    internal readonly NewPointer CancellableNew; internal readonly Release CancellableCancel;
    internal readonly NewHash HashNew; internal readonly HashInsert Insert; internal readonly DupString Duplicate;
    internal readonly IntPtr StringHash, StringEqual, FreePointer;
    internal readonly ProxyNew Proxy; internal readonly StringPointer ProxyConnection;
    internal readonly ProxyCall Call; internal readonly ConnectionCall DirectCall; internal readonly RemoteError RemoteErrorName; internal readonly Quark SecretErrorQuark;
    internal readonly VariantString String, ObjectPath; internal readonly VariantBool Boolean;
    internal readonly VariantTuple Tuple; internal readonly VariantArray Array; internal readonly VariantPair Pair;
    internal readonly RefPointer VariantRefSink, VariantVariant, GetVariant; internal readonly VariantChild Child;
    internal readonly VariantCount Count; internal readonly VariantGetBool Bool; internal readonly VariantGetString Text;
    internal readonly StringPointer VariantType; internal readonly NewType Type;
    private LibsecretNative()
    {
        if (!OperatingSystem.IsLinux()) throw new SecureCredentialVaultException(SecureCredentialFailure.UnsupportedPlatform);
        var loaded = new List<IntPtr>();
        try
        {
            IntPtr Load(string name) { var handle = NativeLibrary.Load(name); loaded.Add(handle); return handle; }
            var secret = Load("libsecret-1.so.0"); var gio = Load("libgio-2.0.so.0"); var obj = Load("libgobject-2.0.so.0"); var glib = Load("libglib-2.0.so.0");
            T Symbol<T>(IntPtr library, string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
            Service = Symbol<ServiceOpen>(secret, "secret_service_open_sync"); ServiceType = Symbol<TypeGet>(secret, "secret_service_get_type"); Ensure = Symbol<EnsureSession>(secret, "secret_service_ensure_session_sync");
            Algorithms = Symbol<StringPointer>(secret, "secret_service_get_session_algorithms"); SessionPath = Symbol<StringPointer>(secret, "secret_service_get_session_dbus_path"); Alias = Symbol<AliasRead>(secret, "secret_service_read_alias_dbus_path_sync");
            Search = Symbol<SearchPaths>(secret, "secret_service_search_for_dbus_paths_sync"); Get = Symbol<GetSecret>(secret, "secret_service_get_secret_for_dbus_path_sync");
            Encode = Symbol<EncodeSecret>(secret, "secret_service_encode_dbus_secret"); NewValue = Symbol<ValueNew>(secret, "secret_value_new"); Value = Symbol<ValueGet>(secret, "secret_value_get");
            ContentType = Symbol<StringPointer>(secret, "secret_value_get_content_type"); ValueUnref = Symbol<Release>(secret, "secret_value_unref"); SecretErrorQuark = Symbol<Quark>(secret, "secret_error_get_quark");
            ObjectUnref = Symbol<Release>(obj, "g_object_unref"); CancellableNew = Symbol<NewPointer>(gio, "g_cancellable_new"); CancellableCancel = Symbol<Release>(gio, "g_cancellable_cancel");
            Proxy = Symbol<ProxyNew>(gio, "g_dbus_proxy_new_sync"); ProxyConnection = Symbol<StringPointer>(gio, "g_dbus_proxy_get_connection"); Call = Symbol<ProxyCall>(gio, "g_dbus_proxy_call_sync"); DirectCall = Symbol<ConnectionCall>(gio, "g_dbus_connection_call_sync");
            RemoteErrorName = Symbol<RemoteError>(gio, "g_dbus_error_get_remote_error");
            Free = Symbol<Release>(glib, "g_free"); ErrorFree = Symbol<Release>(glib, "g_error_free"); StringVectorFree = Symbol<Release>(glib, "g_strfreev");
            HashNew = Symbol<NewHash>(glib, "g_hash_table_new_full"); Insert = Symbol<HashInsert>(glib, "g_hash_table_insert"); Duplicate = Symbol<DupString>(glib, "g_strdup"); HashUnref = Symbol<Release>(glib, "g_hash_table_unref");
            StringHash = NativeLibrary.GetExport(glib, "g_str_hash"); StringEqual = NativeLibrary.GetExport(glib, "g_str_equal"); FreePointer = NativeLibrary.GetExport(glib, "g_free");
            String = Symbol<VariantString>(glib, "g_variant_new_string"); ObjectPath = Symbol<VariantString>(glib, "g_variant_new_object_path"); Boolean = Symbol<VariantBool>(glib, "g_variant_new_boolean");
            Tuple = Symbol<VariantTuple>(glib, "g_variant_new_tuple"); Array = Symbol<VariantArray>(glib, "g_variant_new_array"); Pair = Symbol<VariantPair>(glib, "g_variant_new_dict_entry");
            VariantRefSink = Symbol<RefPointer>(glib, "g_variant_ref_sink"); VariantVariant = Symbol<RefPointer>(glib, "g_variant_new_variant"); GetVariant = Symbol<RefPointer>(glib, "g_variant_get_variant");
            Child = Symbol<VariantChild>(glib, "g_variant_get_child_value"); Count = Symbol<VariantCount>(glib, "g_variant_n_children"); Bool = Symbol<VariantGetBool>(glib, "g_variant_get_boolean");
            Text = Symbol<VariantGetString>(glib, "g_variant_get_string"); VariantType = Symbol<StringPointer>(glib, "g_variant_get_type_string"); Type = Symbol<NewType>(glib, "g_variant_type_new");
            TypeFree = Symbol<Release>(glib, "g_variant_type_free"); VariantUnref = Symbol<Release>(glib, "g_variant_unref"); _modules = loaded.ToArray();
        }
        catch { foreach (var module in loaded) NativeLibrary.Free(module); throw new SecureCredentialVaultException(SecureCredentialFailure.Unavailable); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Release(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nuint TypeGet();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ServiceOpen(nuint serviceType, [MarshalAs(UnmanagedType.LPUTF8Str)] string serviceName, int flags, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int EnsureSession(IntPtr service, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr StringPointer(IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr AliasRead(IntPtr service, [MarshalAs(UnmanagedType.LPUTF8Str)] string alias, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SearchPaths(IntPtr service, IntPtr schema, IntPtr attrs, IntPtr cancel, out IntPtr unlocked, out IntPtr locked, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr GetSecret(IntPtr service, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr EncodeSecret(IntPtr service, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ValueNew(IntPtr bytes, nint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ValueGet(IntPtr value, out nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewPointer();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint Quark();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewHash(IntPtr hash, IntPtr equals, IntPtr freeKey, IntPtr freeValue);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int HashInsert(IntPtr table, IntPtr key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr DupString([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ProxyNew(IntPtr connection, int flags, IntPtr info, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ProxyCall(IntPtr proxy, [MarshalAs(UnmanagedType.LPUTF8Str)] string method, IntPtr args, int flags, int timeoutMilliseconds, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr ConnectionCall(IntPtr connection, [MarshalAs(UnmanagedType.LPUTF8Str)] string busName, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string iface, [MarshalAs(UnmanagedType.LPUTF8Str)] string method, IntPtr args, IntPtr replyType, int flags, int timeoutMilliseconds, IntPtr cancel, out IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr RemoteError(IntPtr error);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantString([MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantBool(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantTuple([In] IntPtr[] children, nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantArray(IntPtr childType, [In] IntPtr[] children, nuint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantPair(IntPtr key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr RefPointer(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantChild(IntPtr value, nuint index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate nuint VariantCount(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int VariantGetBool(IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr VariantGetString(IntPtr value, out nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr NewType([MarshalAs(UnmanagedType.LPUTF8Str)] string signature);
}

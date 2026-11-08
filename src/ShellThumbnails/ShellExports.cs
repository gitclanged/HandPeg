using System.Runtime.InteropServices;

namespace HandPegApp.ShellThumbnails;

// ----- The handler's COM, by hand (Pass 5.3-Stage2: NativeAOT, so Explorer needs no .NET to load it) -----
// Explorer's thumbnail surrogate makes a handler as it makes any in-process COM class: it loads the DLL its class's InprocServer32 names,
// asks the DLL's DllGetClassObject for the class factory, asks the factory for an object (IInitializeWithStream), hands it the file as a
// stream, asks it for IThumbnailProvider and the picture, then releases it. A NativeAOT library has no built-in COM, so the objects are
// written by hand, as the compositor's COM calls are: a COM pointer's first field is its table of methods, each a function pointer
// (IUnknown's three first, then the interface's own in their declaration order), here [UnmanagedCallersOnly] methods of this library. No
// ComWrappers, no reflection. An exception leaving a method Explorer calls would end its surrogate, so none does: every failure is an HRESULT.

/// <summary>The interfaces the handler answers to (unknwn.h, shobjidl.h, thumbcache.h), and its class.</summary>
internal static class ComIds
{
    public static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    public static readonly Guid ClassFactory = new("00000001-0000-0000-c000-000000000046");
    public static readonly Guid InitializeWithStream = new("b824b49d-22ac-4161-ac8a-9916e8fa3f7f");
    public static readonly Guid ThumbnailProvider = new("e357fccd-a995-4576-b01f-234630154e96");

    /// <summary>The handler's CLSID (<see cref="ProjectThumbnailProvider.ClassId"/>; ExplorerThumbnails registers the same).</summary>
    public static readonly Guid Provider = new(ProjectThumbnailProvider.ClassId);
}

/// <summary>The HRESULTs the handler answers with.</summary>
internal static class HResult
{
    public const int Ok = 0, False = 1;
    public const int NoInterface = unchecked((int)0x80004002), Pointer = unchecked((int)0x80004003), Fail = unchecked((int)0x80004005);
    public const int NoAggregation = unchecked((int)0x80040110), ClassNotAvailable = unchecked((int)0x80040111);

    // E_INVALIDARG, E_OUTOFMEMORY; HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED), for a second Initialize.
    public const int InvalidArgument = unchecked((int)0x80070057), OutOfMemory = unchecked((int)0x8007000E), AlreadyInitialized = unchecked((int)0x800704DF);
}

/// <summary>
/// The library's two exports, what Explorer's surrogate calls by name. No DllRegisterServer: HandPeg writes the handler's keys itself
/// (ExplorerThumbnails), for the user alone.
/// </summary>
public static unsafe class ShellExports
{
    /// <summary>The class factory of the handler's class (IClassFactory, or IUnknown); CLASS_E_CLASSNOTAVAILABLE for any other class.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, void** result)
    {
        if (result is null)
            return HResult.Pointer;
        *result = null;
        if (clsid is null || iid is null)
            return HResult.InvalidArgument;
        if (*clsid != ComIds.Provider)
            return HResult.ClassNotAvailable;
        if (*iid != ComIds.ClassFactory && *iid != ComIds.Unknown)
            return HResult.NoInterface;

        *result = ClassFactory.Instance;
        return HResult.Ok;
    }

    /// <summary>Never (S_FALSE): a NativeAOT library cannot be unloaded (its runtime stays), so the surrogate keeps it.</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => HResult.False;
}

/// <summary>
/// The class factory (IClassFactory: QueryInterface, AddRef, Release, CreateInstance, LockServer): one for the process, made on first use
/// and never freed, so its counts mean nothing.
/// </summary>
internal static unsafe class ClassFactory
{
    public static readonly void* Instance = Create();

    private static void* Create()
    {
        var methods = (void**)NativeMemory.Alloc((nuint)(5 * sizeof(void*)));
        methods[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
        methods[1] = (delegate* unmanaged<void*, uint>)&AddRef;
        methods[2] = (delegate* unmanaged<void*, uint>)&Release;
        methods[3] = (delegate* unmanaged<void*, void*, Guid*, void**, int>)&CreateInstance;
        methods[4] = (delegate* unmanaged<void*, int, int>)&LockServer;
        var instance = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        *instance = methods;
        return instance;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result is null)
            return HResult.Pointer;
        if (iid is not null && (*iid == ComIds.Unknown || *iid == ComIds.ClassFactory))
        {
            *result = self;
            return HResult.Ok;
        }

        *result = null;
        return HResult.NoInterface;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(void* self) => 2;

    [UnmanagedCallersOnly]
    private static uint Release(void* self) => 1;

    /// <summary>A new handler object for one file, as <paramref name="iid"/> (IInitializeWithStream, IThumbnailProvider or IUnknown); never aggregated.</summary>
    [UnmanagedCallersOnly]
    private static int CreateInstance(void* self, void* outer, Guid* iid, void** result)
    {
        if (result is null)
            return HResult.Pointer;
        *result = null;
        if (outer is not null)
            return HResult.NoAggregation;
        if (iid is null)
            return HResult.InvalidArgument;

        try
        {
            // Made with one reference, which the interface asked for takes over (or which frees it when the interface is not one of its).
            var created = ProviderObject.Create();
            var answer = ProviderObject.Query(created, iid, result);
            ProviderObject.Drop(created);
            return answer;
        }
        catch (Exception)
        {
            return HResult.OutOfMemory;
        }
    }

    [UnmanagedCallersOnly]
    private static int LockServer(void* self, int locking) => HResult.Ok;
}

/// <summary>
/// One handler object in native memory: its two interface pointers (IInitializeWithStream, which is also its IUnknown, then
/// IThumbnailProvider one pointer later), its reference count, and a GCHandle of its managed half (<see cref="ProjectThumbnailProvider"/>).
/// Each interface's IUnknown methods find the object from their own pointer and share the rest.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ProviderObject
{
    public void** InitializeMethods;
    public void** ThumbnailMethods;
    public int References;
    public nint State;

    private static readonly void** InitializeTable = MakeInitializeTable();
    private static readonly void** ThumbnailTable = MakeThumbnailTable();

    // IInitializeWithStream: IUnknown, then Initialize(IStream* stream, DWORD mode).
    private static void** MakeInitializeTable()
    {
        var table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
        table[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&InitializeQuery;
        table[1] = (delegate* unmanaged<void*, uint>)&InitializeAddRef;
        table[2] = (delegate* unmanaged<void*, uint>)&InitializeRelease;
        table[3] = (delegate* unmanaged<void*, void*, uint, int>)&Initialize;
        return table;
    }

    // IThumbnailProvider: IUnknown, then GetThumbnail(UINT cx, HBITMAP* bitmap, WTS_ALPHATYPE* alpha).
    private static void** MakeThumbnailTable()
    {
        var table = (void**)NativeMemory.Alloc((nuint)(4 * sizeof(void*)));
        table[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&ThumbnailQuery;
        table[1] = (delegate* unmanaged<void*, uint>)&ThumbnailAddRef;
        table[2] = (delegate* unmanaged<void*, uint>)&ThumbnailRelease;
        table[3] = (delegate* unmanaged<void*, uint, nint*, int*, int>)&GetThumbnail;
        return table;
    }

    /// <summary>A new object with one reference.</summary>
    public static ProviderObject* Create()
    {
        var state = GCHandle.ToIntPtr(GCHandle.Alloc(new ProjectThumbnailProvider()));
        var made = (ProviderObject*)NativeMemory.AllocZeroed((nuint)sizeof(ProviderObject));
        made->InitializeMethods = InitializeTable;
        made->ThumbnailMethods = ThumbnailTable;
        made->References = 1;
        made->State = state;
        return made;
    }

    /// <summary>QueryInterface: the object's pointer for <paramref name="iid"/>, one more reference; E_NOINTERFACE for an interface it is not.</summary>
    public static int Query(ProviderObject* self, Guid* iid, void** result)
    {
        if (result is null)
            return HResult.Pointer;
        if (iid is not null && (*iid == ComIds.Unknown || *iid == ComIds.InitializeWithStream))
        {
            *result = &self->InitializeMethods;
        }
        else if (iid is not null && *iid == ComIds.ThumbnailProvider)
        {
            *result = &self->ThumbnailMethods;
        }
        else
        {
            *result = null;
            return HResult.NoInterface;
        }

        Interlocked.Increment(ref self->References);
        return HResult.Ok;
    }

    /// <summary>Release: one reference fewer; at none the stream it still holds is let go of, then its managed half and its memory.</summary>
    public static uint Drop(ProviderObject* self)
    {
        var left = Interlocked.Decrement(ref self->References);
        if (left != 0)
            return (uint)left;

        var handle = GCHandle.FromIntPtr(self->State);
        try
        {
            (handle.Target as ProjectThumbnailProvider)?.ReleaseStream();
        }
        catch (Exception)
        {
            // Released from Explorer's call: nothing may leave it.
        }

        handle.Free();
        NativeMemory.Free(self);
        return 0;
    }

    private static ProviderObject* FromInitialize(void* self) => (ProviderObject*)self;

    private static ProviderObject* FromThumbnail(void* self) => (ProviderObject*)((void**)self - 1);

    private static ProjectThumbnailProvider Managed(ProviderObject* self) => (ProjectThumbnailProvider)GCHandle.FromIntPtr(self->State).Target!;

    [UnmanagedCallersOnly]
    private static int InitializeQuery(void* self, Guid* iid, void** result) => Query(FromInitialize(self), iid, result);

    [UnmanagedCallersOnly]
    private static uint InitializeAddRef(void* self) => (uint)Interlocked.Increment(ref FromInitialize(self)->References);

    [UnmanagedCallersOnly]
    private static uint InitializeRelease(void* self) => Drop(FromInitialize(self));

    [UnmanagedCallersOnly]
    private static int ThumbnailQuery(void* self, Guid* iid, void** result) => Query(FromThumbnail(self), iid, result);

    [UnmanagedCallersOnly]
    private static uint ThumbnailAddRef(void* self) => (uint)Interlocked.Increment(ref FromThumbnail(self)->References);

    [UnmanagedCallersOnly]
    private static uint ThumbnailRelease(void* self) => Drop(FromThumbnail(self));

    [UnmanagedCallersOnly]
    private static int Initialize(void* self, void* stream, uint mode)
    {
        try
        {
            return Managed(FromInitialize(self)).Initialize((nint)stream, mode);
        }
        catch (Exception)
        {
            return HResult.Fail;
        }
    }

    [UnmanagedCallersOnly]
    private static int GetThumbnail(void* self, uint cx, nint* bitmap, int* alpha)
    {
        if (bitmap is null || alpha is null)
            return HResult.Pointer;
        *bitmap = 0;
        *alpha = 0;
        try
        {
            var answer = Managed(FromThumbnail(self)).GetThumbnail(cx, out var drawn, out var type);
            (*bitmap, *alpha) = (drawn, (int)type);
            return answer;
        }
        catch (Exception)
        {
            return HResult.Fail;
        }
    }
}

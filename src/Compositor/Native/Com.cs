using System.Runtime.InteropServices;

namespace HandPegApp.Compositor.Native;

// ----- COM by hand (Pass 5.0-Core) -----
//
// DXGI, Direct3D 11 and Media Foundation are called through their method tables directly: a COM pointer's first field
// is its table, and each method is a function pointer at a fixed place in it. The places are the interfaces' declaration
// order, checked against Windows.Win32.winmd (the Win32 metadata) for this pass: IUnknown is 0-2, then each base
// interface's methods, then the interface's own. No NuGet, no generated wrappers, no reflection; nothing allocates.

/// <summary>A failed HRESULT from the graphics or media stack, with what was being done.</summary>
public sealed class GpuException(int hresult, string what) : Exception(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{what} failed (0x{hresult:X8})"))
{
    public int Code { get; } = hresult;

    /// <summary>The card was removed, reset, hung or its driver fell over: a TDR. The device is gone; the worker starts again.</summary>
    public bool IsDeviceLost => Com.IsDeviceLost(Code);
}

internal static unsafe partial class Com
{
    public const int DeviceRemoved = unchecked((int)0x887A0005);
    public const int DeviceHung = unchecked((int)0x887A0006);
    public const int DeviceReset = unchecked((int)0x887A0007);
    public const int DriverInternalError = unchecked((int)0x887A0020);
    public const int WasStillDrawing = unchecked((int)0x887A000A);
    public const int WaitTimeout = 0x102;
    public const int SFalse = 1;

    public static bool IsDeviceLost(int hresult) => hresult is DeviceRemoved or DeviceHung or DeviceReset or DriverInternalError;

    public static void Check(int hresult, string what)
    {
        if (hresult < 0)
            throw new GpuException(hresult, what);
    }

    /// <summary>The method table of a COM pointer.</summary>
    public static void** Table(nint instance) => *(void***)instance;

    public static void Release(ref nint instance)
    {
        if (instance != 0)
        {
            ((delegate* unmanaged<nint, uint>)Table(instance)[2])(instance);
            instance = 0;
        }
    }

    public static void AddRef(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged<nint, uint>)Table(instance)[1])(instance);
    }

    /// <summary>QueryInterface; 0 when the object does not have that interface.</summary>
    public static nint Query(nint instance, in Guid interfaceId)
    {
        if (instance == 0)
            return 0;
        nint result = 0;
        fixed (Guid* id = &interfaceId)
        {
            var hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)Table(instance)[0])(instance, id, &result);
            return hr < 0 ? 0 : result;
        }
    }

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint memory);

    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint coInit);

    public const uint CoInitMultithreaded = 0;
}

/// <summary>The interface ids used (Windows.Win32.winmd).</summary>
internal static class Iid
{
    public static readonly Guid DxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid DxgiFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
    public static readonly Guid DxgiAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");
    public static readonly Guid DxgiAdapter3 = new("645967a4-1392-4310-a798-8053ce3e93fd");
    public static readonly Guid DxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    public static readonly Guid DxgiSwapChain1 = new("790a45f7-0d42-4876-983a-0a55cfe6f4aa");
    public static readonly Guid DxgiSwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");
    public static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    public static readonly Guid DxgiResource1 = new("30961379-4609-4a41-998e-54fe567ee0c1");
    public static readonly Guid DxgiKeyedMutex = new("9d8e1289-d7b3-465f-8126-250e349af85d");
    public static readonly Guid D3D11Device1 = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
    public static readonly Guid D3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    public static readonly Guid D3D11Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    public static readonly Guid D3D11VideoDevice = new("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
    public static readonly Guid MfDxgiBuffer = new("e7174cfa-1c9e-48b1-8866-626226bfc258");
    public static readonly Guid Mf2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");
}

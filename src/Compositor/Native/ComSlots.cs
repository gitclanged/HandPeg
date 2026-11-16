namespace HandPegApp.Compositor.Native;

// ----- COM vtable slots, by name -----
// COM methods are called through their vtable slot (Com.Table(p)[slot]). A wrong slot calls another method with the wrong arguments:
// no exception, just undefined behaviour inside Media Foundation or a driver. So each interface's slots are written once here, derived
// from its base interface's End (the number of methods up to and including the base), never as bare numbers at call sites; a mistake
// in a base count then shows in one place, which the hot-path goldens exercise. Order: the SDK headers' declaration order (checked
// against Windows.Win32.winmd).

/// <summary>IUnknown: QueryInterface 0, AddRef 1, Release 2.</summary>
internal static class IUnknownSlot
{
    public const int End = 3;
}

/// <summary>IMFAttributes (mfobjects.h), after IUnknown.</summary>
internal static class IMFAttributesSlot
{
    public const int GetUINT32 = IUnknownSlot.End + 4, GetUINT64 = IUnknownSlot.End + 5, GetGUID = IUnknownSlot.End + 7,
        GetAllocatedString = IUnknownSlot.End + 10, GetBlobSize = IUnknownSlot.End + 11, GetBlob = IUnknownSlot.End + 12,
        SetUINT32 = IUnknownSlot.End + 18, SetUINT64 = IUnknownSlot.End + 19, SetGUID = IUnknownSlot.End + 21, SetBlob = IUnknownSlot.End + 23,
        SetUnknown = IUnknownSlot.End + 24, GetCount = IUnknownSlot.End + 27;

    /// <summary>GetItem ... CopyAllItems: 30 methods.</summary>
    public const int End = IUnknownSlot.End + 30;
}

/// <summary>IMFSample (mfobjects.h), after IMFAttributes.</summary>
internal static class IMFSampleSlot
{
    public const int GetSampleTime = IMFAttributesSlot.End + 2, SetSampleTime = IMFAttributesSlot.End + 3, GetSampleDuration = IMFAttributesSlot.End + 4,
        SetSampleDuration = IMFAttributesSlot.End + 5, GetBufferCount = IMFAttributesSlot.End + 6, GetBufferByIndex = IMFAttributesSlot.End + 7,
        ConvertToContiguousBuffer = IMFAttributesSlot.End + 8, AddBuffer = IMFAttributesSlot.End + 9;
}

/// <summary>IMFActivate (mfobjects.h), after IMFAttributes.</summary>
internal static class IMFActivateSlot
{
    public const int ActivateObject = IMFAttributesSlot.End, ShutdownObject = IMFAttributesSlot.End + 1;
}

/// <summary>IMFMediaEvent (mfobjects.h), after IMFAttributes.</summary>
internal static class IMFMediaEventSlot
{
    public const int GetEventType = IMFAttributesSlot.End, GetStatus = IMFAttributesSlot.End + 2;
}

/// <summary>IMFMediaBuffer (mfobjects.h): Lock, Unlock, GetCurrentLength, SetCurrentLength, GetMaxLength.</summary>
internal static class IMFMediaBufferSlot
{
    public const int Lock = IUnknownSlot.End, Unlock = IUnknownSlot.End + 1, SetCurrentLength = IUnknownSlot.End + 3, GetMaxLength = IUnknownSlot.End + 4;
}

/// <summary>IMF2DBuffer (mfobjects.h): Lock2D, Unlock2D, ...</summary>
internal static class IMF2DBufferSlot
{
    public const int Lock2D = IUnknownSlot.End, Unlock2D = IUnknownSlot.End + 1;
}

/// <summary>IMFDXGIBuffer (mfobjects.h): GetResource, GetSubresourceIndex, ...</summary>
internal static class IMFDXGIBufferSlot
{
    public const int GetResource = IUnknownSlot.End, GetSubresourceIndex = IUnknownSlot.End + 1;
}

/// <summary>IMFDXGIDeviceManager (mfobjects.h): CloseDeviceHandle, GetVideoService, LockDevice, OpenDeviceHandle, ResetDevice, ...</summary>
internal static class IMFDXGIDeviceManagerSlot
{
    public const int ResetDevice = IUnknownSlot.End + 4;
}

/// <summary>IMFSourceReader (mfreadwrite.h).</summary>
internal static class IMFSourceReaderSlot
{
    public const int SetStreamSelection = IUnknownSlot.End + 1, GetNativeMediaType = IUnknownSlot.End + 2, GetCurrentMediaType = IUnknownSlot.End + 3,
        SetCurrentMediaType = IUnknownSlot.End + 4, SetCurrentPosition = IUnknownSlot.End + 5, ReadSample = IUnknownSlot.End + 6, Flush = IUnknownSlot.End + 7,
        GetPresentationAttribute = IUnknownSlot.End + 9;
}

/// <summary>IMFTransform (mftransform.h).</summary>
internal static class IMFTransformSlot
{
    public const int GetOutputStreamInfo = IUnknownSlot.End + 4, GetAttributes = IUnknownSlot.End + 5, GetInputAvailableType = IUnknownSlot.End + 10,
        GetOutputAvailableType = IUnknownSlot.End + 11, SetInputType = IUnknownSlot.End + 12, SetOutputType = IUnknownSlot.End + 13,
        GetOutputCurrentType = IUnknownSlot.End + 15, ProcessMessage = IUnknownSlot.End + 20, ProcessInput = IUnknownSlot.End + 21,
        ProcessOutput = IUnknownSlot.End + 22;
}

/// <summary>IMFMediaEventGenerator (mfobjects.h): GetEvent, ...</summary>
internal static class IMFMediaEventGeneratorSlot
{
    public const int GetEvent = IUnknownSlot.End;
}

/// <summary>ICodecAPI (strmif.h): IsSupported, IsModifiable, GetParameterRange, GetParameterValues, GetDefaultValue, GetValue, SetValue, ...</summary>
internal static class ICodecAPISlot
{
    public const int IsModifiable = IUnknownSlot.End + 1, SetValue = IUnknownSlot.End + 6;
}

// ----- DXGI (dxgi.h, dxgi1_2.h to dxgi1_6.h) -----

/// <summary>IDXGIObject: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent.</summary>
internal static class IDXGIObjectSlot
{
    public const int GetParent = IUnknownSlot.End + 3, End = IUnknownSlot.End + 4;
}

/// <summary>IDXGIDeviceSubObject: GetDevice.</summary>
internal static class IDXGIDeviceSubObjectSlot
{
    public const int End = IDXGIObjectSlot.End + 1;
}

/// <summary>IDXGIFactory: EnumAdapters, MakeWindowAssociation, GetWindowAssociation, CreateSwapChain, CreateSoftwareAdapter.</summary>
internal static class IDXGIFactorySlot
{
    public const int EnumAdapters = IDXGIObjectSlot.End, MakeWindowAssociation = IDXGIObjectSlot.End + 1, End = IDXGIObjectSlot.End + 5;
}

/// <summary>IDXGIFactory1: EnumAdapters1, IsCurrent.</summary>
internal static class IDXGIFactory1Slot
{
    public const int EnumAdapters1 = IDXGIFactorySlot.End, End = IDXGIFactorySlot.End + 2;
}

/// <summary>IDXGIFactory2: IsWindowedStereoEnabled, CreateSwapChainForHwnd, ... CreateSwapChainForComposition (11 methods).</summary>
internal static class IDXGIFactory2Slot
{
    public const int CreateSwapChainForHwnd = IDXGIFactory1Slot.End + 1, End = IDXGIFactory1Slot.End + 11;
}

/// <summary>IDXGIFactory6: EnumAdapterByGpuPreference, after IDXGIFactory3 (1 method), IDXGIFactory4 (2) and IDXGIFactory5 (1).</summary>
internal static class IDXGIFactory6Slot
{
    public const int EnumAdapterByGpuPreference = IDXGIFactory2Slot.End + 4;
}

/// <summary>IDXGIAdapter: EnumOutputs, GetDesc, CheckInterfaceSupport.</summary>
internal static class IDXGIAdapterSlot
{
    public const int GetDesc = IDXGIObjectSlot.End + 1, CheckInterfaceSupport = IDXGIObjectSlot.End + 2, End = IDXGIObjectSlot.End + 3;
}

/// <summary>IDXGIAdapter1: GetDesc1.</summary>
internal static class IDXGIAdapter1Slot
{
    public const int GetDesc1 = IDXGIAdapterSlot.End, End = IDXGIAdapterSlot.End + 1;
}

/// <summary>IDXGIAdapter3: QueryVideoMemoryInfo, after IDXGIAdapter2's GetDesc2 and the two content-protection methods.</summary>
internal static class IDXGIAdapter3Slot
{
    public const int QueryVideoMemoryInfo = IDXGIAdapter1Slot.End + 3;
}

/// <summary>IDXGIDevice: GetAdapter, CreateSurface, QueryResourceResidency, SetGPUThreadPriority, GetGPUThreadPriority.</summary>
internal static class IDXGIDeviceSlot
{
    public const int GetAdapter = IDXGIObjectSlot.End, End = IDXGIObjectSlot.End + 5;
}

/// <summary>IDXGIDevice3: Trim, after IDXGIDevice1 (2 methods) and IDXGIDevice2 (3).</summary>
internal static class IDXGIDevice3Slot
{
    public const int Trim = IDXGIDeviceSlot.End + 5;
}

/// <summary>IDXGIResource (4 methods) then IDXGIResource1: CreateSubresourceSurface, CreateSharedHandle.</summary>
internal static class IDXGIResource1Slot
{
    public const int CreateSharedHandle = IDXGIDeviceSubObjectSlot.End + 5;
}

/// <summary>IDXGIKeyedMutex: AcquireSync, ReleaseSync.</summary>
internal static class IDXGIKeyedMutexSlot
{
    public const int AcquireSync = IDXGIDeviceSubObjectSlot.End, ReleaseSync = IDXGIDeviceSubObjectSlot.End + 1;
}

/// <summary>IDXGISwapChain: Present, GetBuffer, SetFullscreenState, GetFullscreenState, GetDesc, ResizeBuffers, ... GetLastPresentCount.</summary>
internal static class IDXGISwapChainSlot
{
    public const int Present = IDXGIDeviceSubObjectSlot.End, GetBuffer = IDXGIDeviceSubObjectSlot.End + 1, ResizeBuffers = IDXGIDeviceSubObjectSlot.End + 5,
        GetFrameStatistics = IDXGIDeviceSubObjectSlot.End + 8, GetLastPresentCount = IDXGIDeviceSubObjectSlot.End + 9, End = IDXGIDeviceSubObjectSlot.End + 10;
}

/// <summary>IDXGISwapChain2: SetSourceSize, GetSourceSize, SetMaximumFrameLatency, GetMaximumFrameLatency, GetFrameLatencyWaitableObject, after IDXGISwapChain1 (11).</summary>
internal static class IDXGISwapChain2Slot
{
    public const int SetMaximumFrameLatency = IDXGISwapChainSlot.End + 13, GetFrameLatencyWaitableObject = IDXGISwapChainSlot.End + 15;
}

// ----- Direct3D 11 (d3d11.h, d3d11_1.h) -----

/// <summary>ID3D11Device, in its declaration order (CreateBuffer first, GetExceptionMode last).</summary>
internal static class ID3D11DeviceSlot
{
    public const int CreateBuffer = IUnknownSlot.End, CreateTexture2D = IUnknownSlot.End + 2, CreateTexture3D = IUnknownSlot.End + 3,
        CreateShaderResourceView = IUnknownSlot.End + 4, CreateRenderTargetView = IUnknownSlot.End + 6, CreateVertexShader = IUnknownSlot.End + 9,
        CreatePixelShader = IUnknownSlot.End + 12, CreateBlendState = IUnknownSlot.End + 17, CreateRasterizerState = IUnknownSlot.End + 19,
        CreateSamplerState = IUnknownSlot.End + 20, CreateQuery = IUnknownSlot.End + 21, GetDeviceRemovedReason = IUnknownSlot.End + 36,
        GetImmediateContext = IUnknownSlot.End + 37, End = IUnknownSlot.End + 40;
}

/// <summary>ID3D11Device1: OpenSharedResourceByName (its seventh method).</summary>
internal static class ID3D11Device1Slot
{
    public const int OpenSharedResourceByName = ID3D11DeviceSlot.End + 6;
}

/// <summary>ID3D11DeviceChild: GetDevice, GetPrivateData, SetPrivateData, SetPrivateDataInterface.</summary>
internal static class ID3D11DeviceChildSlot
{
    public const int End = IUnknownSlot.End + 4;
}

/// <summary>ID3D11Resource: GetType, SetEvictionPriority, GetEvictionPriority.</summary>
internal static class ID3D11ResourceSlot
{
    public const int End = ID3D11DeviceChildSlot.End + 3;
}

/// <summary>ID3D11Texture2D: GetDesc.</summary>
internal static class ID3D11Texture2DSlot
{
    public const int GetDesc = ID3D11ResourceSlot.End;
}

/// <summary>ID3D11DeviceContext, in its declaration order (VSSetConstantBuffers first). <see cref="EndQuery"/> is the method named End.</summary>
internal static class ID3D11DeviceContextSlot
{
    private const int First = ID3D11DeviceChildSlot.End;

    public const int VSSetConstantBuffers = First, PSSetShaderResources = First + 1, PSSetShader = First + 2, PSSetSamplers = First + 3,
        VSSetShader = First + 4, Draw = First + 6, Map = First + 7, Unmap = First + 8, PSSetConstantBuffers = First + 9, IASetInputLayout = First + 10,
        IASetPrimitiveTopology = First + 17, Begin = First + 20, EndQuery = First + 21, GetData = First + 22, OMSetRenderTargets = First + 26,
        OMSetBlendState = First + 28, RSSetState = First + 36, RSSetViewports = First + 37, RSSetScissorRects = First + 38,
        CopySubresourceRegion = First + 39, CopyResource = First + 40, UpdateSubresource = First + 41, ClearRenderTargetView = First + 43,
        ClearState = First + 103, Flush = First + 104;
}

/// <summary>ID3D11VideoDevice: ... GetVideoDecoderProfileCount, GetVideoDecoderProfile (its ninth and tenth methods).</summary>
internal static class ID3D11VideoDeviceSlot
{
    public const int GetVideoDecoderProfileCount = IUnknownSlot.End + 8, GetVideoDecoderProfile = IUnknownSlot.End + 9;
}

/// <summary>ID3D11Multithread: Enter, Leave, SetMultithreadProtected, GetMultithreadProtected.</summary>
internal static class ID3D11MultithreadSlot
{
    public const int SetMultithreadProtected = IUnknownSlot.End + 2;
}

// ----- DXCore (dxcore_interface.h) -----

/// <summary>IDXCoreAdapterFactory: CreateAdapterList, GetAdapterByLuid, ...</summary>
internal static class IDXCoreAdapterFactorySlot
{
    public const int CreateAdapterList = IUnknownSlot.End, GetAdapterByLuid = IUnknownSlot.End + 1;
}

/// <summary>IDXCoreAdapterList: GetAdapter, GetAdapterCount, ...</summary>
internal static class IDXCoreAdapterListSlot
{
    public const int GetAdapter = IUnknownSlot.End, GetAdapterCount = IUnknownSlot.End + 1;
}

/// <summary>IDXCoreAdapter: IsValid, IsAttributeSupported, IsPropertySupported, GetProperty, GetPropertySize, ...</summary>
internal static class IDXCoreAdapterSlot
{
    public const int IsAttributeSupported = IUnknownSlot.End + 1, IsPropertySupported = IUnknownSlot.End + 2, GetProperty = IUnknownSlot.End + 3,
        GetPropertySize = IUnknownSlot.End + 4;
}

// ----- WASAPI and the device enumerator (mmdeviceapi.h, audioclient.h, propsys.h) -----

/// <summary>IMMDeviceEnumerator: EnumAudioEndpoints, GetDefaultAudioEndpoint, GetDevice, RegisterEndpointNotificationCallback, Unregister....</summary>
internal static class IMMDeviceEnumeratorSlot
{
    public const int EnumAudioEndpoints = IUnknownSlot.End, GetDefaultAudioEndpoint = IUnknownSlot.End + 1, GetDevice = IUnknownSlot.End + 2,
        RegisterEndpointNotificationCallback = IUnknownSlot.End + 3, UnregisterEndpointNotificationCallback = IUnknownSlot.End + 4;
}

/// <summary>IMMDeviceCollection: GetCount, Item.</summary>
internal static class IMMDeviceCollectionSlot
{
    public const int GetCount = IUnknownSlot.End, Item = IUnknownSlot.End + 1;
}

/// <summary>IMMDevice: Activate, OpenPropertyStore, GetId, GetState.</summary>
internal static class IMMDeviceSlot
{
    public const int Activate = IUnknownSlot.End, OpenPropertyStore = IUnknownSlot.End + 1, GetId = IUnknownSlot.End + 2;
}

/// <summary>IPropertyStore: GetCount, GetAt, GetValue, SetValue, Commit.</summary>
internal static class IPropertyStoreSlot
{
    public const int GetValue = IUnknownSlot.End + 2;
}

/// <summary>IAudioClient: Initialize, GetBufferSize, GetStreamLatency, GetCurrentPadding, IsFormatSupported, GetMixFormat, ... GetService.</summary>
internal static class IAudioClientSlot
{
    public const int Initialize = IUnknownSlot.End, GetBufferSize = IUnknownSlot.End + 1, GetStreamLatency = IUnknownSlot.End + 2,
        GetCurrentPadding = IUnknownSlot.End + 3, GetMixFormat = IUnknownSlot.End + 5, GetDevicePeriod = IUnknownSlot.End + 6, Start = IUnknownSlot.End + 7,
        Stop = IUnknownSlot.End + 8, Reset = IUnknownSlot.End + 9, SetEventHandle = IUnknownSlot.End + 10, GetService = IUnknownSlot.End + 11,
        End = IUnknownSlot.End + 12;
}

/// <summary>IAudioClient3: GetSharedModeEnginePeriod, GetCurrentSharedModeEnginePeriod, InitializeSharedAudioStream, after IAudioClient2 (3).</summary>
internal static class IAudioClient3Slot
{
    public const int GetSharedModeEnginePeriod = IAudioClientSlot.End + 3, InitializeSharedAudioStream = IAudioClientSlot.End + 5;
}

/// <summary>IAudioRenderClient: GetBuffer, ReleaseBuffer.</summary>
internal static class IAudioRenderClientSlot
{
    public const int GetBuffer = IUnknownSlot.End, ReleaseBuffer = IUnknownSlot.End + 1;
}

/// <summary>IAudioClock: GetFrequency, GetPosition, GetCharacteristics.</summary>
internal static class IAudioClockSlot
{
    public const int GetFrequency = IUnknownSlot.End, GetPosition = IUnknownSlot.End + 1;
}

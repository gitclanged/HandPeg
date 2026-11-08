namespace HandPegApp.ShellThumbnails;

// ----- Explorer's preview of a HandPeg project (Pass 5.2D; NativeAOT since Pass 5.3-Stage2) -----
// Explorer asks a file type's thumbnail handler (HKCU\Software\Classes\.hproj\ShellEx\{e357fccd-...}, written by HandPeg's
// ExplorerThumbnails) for its picture: it hands the handler the file as a stream (IInitializeWithStream), then asks for a bitmap that
// fits a square of cx pixels (IThumbnailProvider). This one finds the thumbnail a save baked into the file (a .hproj's thumbnailJpeg, a
// .hpb's thumbnail.jpg: ThumbnailReader), draws it at that size into a top-down 32-bit DIB section (Gdip) and hands that over. A project
// without one gets E_FAIL, and Explorer shows its icon. The COM object Explorer holds is ShellExports' ProviderObject; this is its managed
// half. Nothing leaves as an exception: every failure is an HRESULT.

/// <summary>
/// The thumbnail handler of .hproj and .hpb files: the project's baked thumbnail, drawn to fit the size Explorer asks for. One for each
/// file, the managed half of the COM object Explorer's thumbnail surrogate makes through the library's DllGetClassObject.
/// </summary>
public sealed class ProjectThumbnailProvider
{
    /// <summary>The class's CLSID, as HandPeg registers it (ExplorerThumbnails.HandlerClsid: the same, in braces).</summary>
    public const string ClassId = "de4a57e8-2ca1-4db5-a7e2-af1299207a9e";

    /// <summary>S_OK, and E_FAIL: what every failure answers (no thumbnail, a file that cannot be read, a picture that cannot be drawn).</summary>
    public const int Ok = 0, Fail = unchecked((int)0x80004005);

    /// <summary>WTSAT_RGB: the bitmap's fourth byte means nothing.</summary>
    public const uint AlphaRgb = 1;

    // The largest side drawn, whatever is asked (Explorer asks for 1024 at most today).
    private const int LargestSide = 2560;

    // The IStream Explorer handed over (one reference of the handler's own), until the thumbnail is read.
    private nint _stream;
    private byte[]? _jpeg;
    private bool _read;

    /// <summary>IInitializeWithStream: the file as a stream, kept (one reference more) until its thumbnail is read; once only.</summary>
    public int Initialize(nint stream, uint mode)
    {
        if (_stream != 0 || _read)
            return HResult.AlreadyInitialized;
        if (stream == 0)
            return HResult.InvalidArgument;

        Com.AddRef(stream);
        _stream = stream;
        return Ok;
    }

    /// <summary>IThumbnailProvider: the thumbnail fitted into a square of <paramref name="cx"/> pixels, as an HBITMAP Explorer takes over.</summary>
    public int GetThumbnail(uint cx, out nint bitmap, out uint alpha)
    {
        bitmap = 0;
        alpha = 0;
        try
        {
            if (TakeThumbnail() is not { } jpeg || cx == 0)
                return Fail;

            var drawn = Gdip.Draw(jpeg, (int)Math.Min(cx, LargestSide));
            if (drawn == 0)
                return Fail;

            (bitmap, alpha) = (drawn, AlphaRgb);
            return Ok;
        }
        catch (Exception)
        {
            // A call from Explorer: nothing may leave it as an exception (a file that cannot be read, among others).
            return Fail;
        }
    }

    /// <summary>The stream let go of, when the object goes before its thumbnail was read.</summary>
    public void ReleaseStream()
    {
        var stream = _stream;
        _stream = 0;
        if (stream != 0)
            Com.Release(stream);
    }

    /// <summary>The thumbnail the file carries, read once; the stream let go of at once after, so the file is not held open.</summary>
    private byte[]? TakeThumbnail()
    {
        if (_read)
            return _jpeg;

        _read = true;
        var stream = _stream;
        _stream = 0;
        if (stream == 0)
            return null;

        try
        {
            using var file = new NativeStream(stream);
            _jpeg = ThumbnailReader.Read(file);
        }
        finally
        {
            Com.Release(stream);
        }

        return _jpeg;
    }

    /// <summary>A picture's size fitted into a square of <paramref name="cx"/> pixels, its shape kept: its long side <paramref name="cx"/>.</summary>
    internal static (int Width, int Height) FitInto(int width, int height, int cx)
    {
        var longest = Math.Max(Math.Max(width, height), 1);
        return (Math.Max((int)Math.Round((double)width * cx / longest), 1), Math.Max((int)Math.Round((double)height * cx / longest), 1));
    }
}

/// <summary>IUnknown's AddRef and Release (slots 1 and 2) on a COM pointer the handler was handed or made.</summary>
internal static unsafe class Com
{
    public static uint AddRef(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(void***)unknown)[1])(unknown);

    public static uint Release(nint unknown) => ((delegate* unmanaged<nint, uint>)(*(void***)unknown)[2])(unknown);
}

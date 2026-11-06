using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace HandPegApp.ShellThumbnails;

// ----- Explorer's preview of a HandPeg project (Pass 5.2D) -----
// Explorer asks a file type's thumbnail handler (HKCU\Software\Classes\.hproj\ShellEx\{e357fccd-...}, written by HandPeg's
// ExplorerThumbnails) for its picture: it hands the handler the file as a stream (IInitializeWithStream), then asks for a bitmap that
// fits a square of cx pixels (IThumbnailProvider). This one finds the thumbnail a save baked into the file (a .hproj's thumbnailJpeg, a
// .hpb's thumbnail.jpg: ThumbnailReader), draws it at that size into a top-down 32-bit DIB section and hands that over. A project
// without one gets E_FAIL, and Explorer shows its icon. Nothing leaves as an exception: every failure is an HRESULT.

/// <summary>IInitializeWithStream (shobjidl): the file a handler works on, as a stream.</summary>
[ComImport]
[ComVisible(true)]
[Guid("b824b49d-22ac-4161-ac8a-9916e8fa3f7f")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IInitializeWithStream
{
    [PreserveSig]
    int Initialize([MarshalAs(UnmanagedType.Interface)] IStream stream, uint mode);
}

/// <summary>IThumbnailProvider (thumbcache.h): a file's picture, an HBITMAP that fits a square of cx pixels, and what its alpha means.</summary>
[ComImport]
[ComVisible(true)]
[Guid("e357fccd-a995-4576-b01f-234630154e96")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IThumbnailProvider
{
    [PreserveSig]
    int GetThumbnail(uint cx, out nint bitmap, out uint alpha);
}

/// <summary>
/// The thumbnail handler of .hproj and .hpb files: the project's baked thumbnail, drawn to fit the size Explorer asks for. Created by
/// Explorer's thumbnail surrogate through HandPeg.ShellThumbnails.comhost.dll, one for each file.
/// </summary>
[ComVisible(true)]
[Guid(ClassId)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ProjectThumbnailProvider : IInitializeWithStream, IThumbnailProvider
{
    /// <summary>The class's CLSID, as HandPeg registers it (ExplorerThumbnails.HandlerClsid: the same, in braces).</summary>
    public const string ClassId = "de4a57e8-2ca1-4db5-a7e2-af1299207a9e";

    /// <summary>S_OK, and E_FAIL: what every failure answers (no thumbnail, a file that cannot be read, a picture that cannot be drawn).</summary>
    public const int Ok = 0, Fail = unchecked((int)0x80004005);

    /// <summary>WTSAT_RGB: the bitmap's fourth byte means nothing.</summary>
    public const uint AlphaRgb = 1;

    // E_INVALIDARG; HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED), for a second Initialize.
    private const int InvalidArgument = unchecked((int)0x80070057), AlreadyInitialized = unchecked((int)0x800704DF);

    // The largest side drawn, whatever is asked (Explorer asks for 1024 at most today).
    private const int LargestSide = 2560;

    private IStream? _stream;
    private byte[]? _jpeg;
    private bool _read;

    public int Initialize(IStream stream, uint mode)
    {
        if (_stream is not null || _read)
            return AlreadyInitialized;
        if (stream is null)
            return InvalidArgument;

        _stream = stream;
        return Ok;
    }

    public int GetThumbnail(uint cx, out nint bitmap, out uint alpha)
    {
        bitmap = 0;
        alpha = 0;
        try
        {
            if (TakeThumbnail() is not { } jpeg || cx == 0)
                return Fail;

            var drawn = Draw(jpeg, (int)Math.Min(cx, LargestSide));
            if (drawn == 0)
                return Fail;

            (bitmap, alpha) = (drawn, AlphaRgb);
            return Ok;
        }
        catch (Exception)
        {
            // A call from Explorer: nothing may leave it as an exception (GDI+ says a damaged picture is "out of memory", among others).
            return Fail;
        }
    }

    /// <summary>The thumbnail the file carries, read once; the stream let go of at once after, so the file is not held open.</summary>
    private byte[]? TakeThumbnail()
    {
        if (_read)
            return _jpeg;

        _read = true;
        var stream = _stream;
        _stream = null;
        if (stream is null)
            return null;

        try
        {
            using var file = new ComStream(stream);
            _jpeg = ThumbnailReader.Read(file);
        }
        finally
        {
            if (Marshal.IsComObject(stream))
                Marshal.ReleaseComObject(stream);
        }

        return _jpeg;
    }

    /// <summary>A JPEG drawn to fit a square of <paramref name="cx"/> pixels (its shape kept) into a top-down 32-bit DIB section; 0 when none could be made.</summary>
    private static nint Draw(byte[] jpeg, int cx)
    {
        using var source = new MemoryStream(jpeg, writable: false);
        using var picture = Image.FromStream(source, useEmbeddedColorManagement: false, validateImageData: true);
        var (width, height) = FitInto(picture.Width, picture.Height, cx);
        var section = Native.CreateTopDownDib(width, height, out var bits);
        if (section == 0 || bits == 0)
        {
            if (section != 0)
                Native.DeleteObject(section);
            return 0;
        }

        try
        {
            // GDI+ draws straight into the section's pixels (its rows top first, as the section is). Its edges sampled as if the
            // picture were mirrored beyond them, so the border pixels are not darkened by the scaling.
            using var target = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, bits);
            using var graphics = Graphics.FromImage(target);
            using var edges = new ImageAttributes();
            edges.SetWrapMode(WrapMode.TileFlipXY);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(picture, new Rectangle(0, 0, width, height), 0, 0, picture.Width, picture.Height, GraphicsUnit.Pixel, edges);
            return section;
        }
        catch
        {
            Native.DeleteObject(section);
            throw;
        }
    }

    /// <summary>A picture's size fitted into a square of <paramref name="cx"/> pixels, its shape kept: its long side <paramref name="cx"/>.</summary>
    internal static (int Width, int Height) FitInto(int width, int height, int cx)
    {
        var longest = Math.Max(Math.Max(width, height), 1);
        return (Math.Max((int)Math.Round((double)width * cx / longest), 1), Math.Max((int)Math.Round((double)height * cx / longest), 1));
    }
}

/// <summary>The GDI calls the handler makes: a DIB section to draw into, and letting it go when it is not handed over.</summary>
internal static partial class Native
{
    /// <summary>BITMAPINFO: its header (BITMAPINFOHEADER), and the one colour a 32-bit BI_RGB bitmap does not use.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
        public uint Color0;
    }

    /// <summary>A 32-bit BI_RGB DIB section, its rows top first (a negative height); its pixels in <paramref name="bits"/>. 0 when it could not be made.</summary>
    internal static nint CreateTopDownDib(int width, int height, out nint bits)
    {
        // biSize is the header's alone (40 bytes), without the colour after it.
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0 };
        return CreateDIBSection(0, in info, 0, out bits, 0, 0);
    }

    // DIB_RGB_COLORS (0), no file mapping.
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint deviceContext, in BitmapInfo info, uint usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);
}

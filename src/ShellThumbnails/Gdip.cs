using System.Runtime.InteropServices;

namespace HandPegApp.ShellThumbnails;

/// <summary>
/// A JPEG drawn into a DIB section through GDI+'s flat API (Pass 5.3-Stage2): what System.Drawing did for the 5.2D handler (Image from the
/// bytes, then DrawImage with HighQualityBicubic, a HighQuality pixel offset, SourceCopy and mirrored edges), without System.Drawing, which
/// NativeAOT cannot carry. GDI+ is started once, at the first picture (never while the library loads), and never shut down: the library
/// stays loaded for the surrogate's life.
/// </summary>
internal static unsafe partial class Gdip
{
    // PixelFormat32bppRGB, UnitPixel; CompositingModeSourceCopy, InterpolationModeHighQualityBicubic, PixelOffsetModeHighQuality, WrapModeTileFlipXY.
    private const int PixelFormat32bppRgb = 0x00022009, UnitPixel = 2;
    private const int SourceCopy = 1, HighQualityBicubic = 7, HighQualityOffset = 2, TileFlipXY = 3;

    private static readonly Lock Gate = new();
    private static nint _token;

    /// <summary>GdiplusStartupInput: version 1, no debug callback, GDI+'s own background thread, its external codecs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint GdiplusVersion;
        public nint DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    /// <summary>BITMAPINFO: its header (BITMAPINFOHEADER), and the one colour a 32-bit BI_RGB bitmap does not use.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
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

    private static bool Started()
    {
        lock (Gate)
        {
            if (_token != 0)
                return true;

            var input = new StartupInput { GdiplusVersion = 1 };
            nint token;
            if (GdiplusStartup(&token, &input, null) != 0 || token == 0)
                return false;

            _token = token;
            return true;
        }
    }

    /// <summary>
    /// A JPEG drawn to fit a square of <paramref name="cx"/> pixels (its shape kept) into a new top-down 32-bit DIB section, which the
    /// caller owns; 0 when it could not be decoded or drawn.
    /// </summary>
    public static nint Draw(ReadOnlySpan<byte> jpeg, int cx)
    {
        if (jpeg.IsEmpty || !Started())
            return 0;

        nint memory = 0, picture = 0, target = 0, graphics = 0, edges = 0, section = 0;
        try
        {
            // An IStream over a copy of the bytes, which GDI+ decodes from (and reads again while it draws).
            fixed (byte* bytes = jpeg)
                memory = SHCreateMemStream(bytes, (uint)jpeg.Length);
            if (memory == 0 || GdipCreateBitmapFromStream(memory, &picture) != 0)
                return 0;

            uint width, height;
            if (GdipGetImageWidth(picture, &width) != 0 || GdipGetImageHeight(picture, &height) != 0 || width == 0 || height == 0)
                return 0;

            var (w, h) = ProjectThumbnailProvider.FitInto((int)width, (int)height, cx);
            section = CreateTopDownDib(w, h, out var bits);
            if (section == 0 || bits == 0)
                return 0;

            // GDI+ draws straight into the section's pixels (its rows top first, as the section is). Its edges sampled as if the picture
            // were mirrored beyond them, so the border pixels are not darkened by the scaling.
            if (GdipCreateBitmapFromScan0(w, h, w * 4, PixelFormat32bppRgb, (byte*)bits, &target) != 0
                || GdipGetImageGraphicsContext(target, &graphics) != 0
                || GdipCreateImageAttributes(&edges) != 0
                || GdipSetImageAttributesWrapMode(edges, TileFlipXY, 0, 0) != 0
                || GdipSetCompositingMode(graphics, SourceCopy) != 0
                || GdipSetInterpolationMode(graphics, HighQualityBicubic) != 0
                || GdipSetPixelOffsetMode(graphics, HighQualityOffset) != 0
                || GdipDrawImageRectRectI(graphics, picture, 0, 0, w, h, 0, 0, (int)width, (int)height, UnitPixel, edges, 0, 0) != 0)
                return 0;

            var drawn = section;
            section = 0;
            return drawn;
        }
        finally
        {
            // In reverse, the stream last: GDI+ reads it until the picture is disposed. The section only when it is not handed over.
            if (graphics != 0)
                GdipDeleteGraphics(graphics);
            if (target != 0)
                GdipDisposeImage(target);
            if (edges != 0)
                GdipDisposeImageAttributes(edges);
            if (picture != 0)
                GdipDisposeImage(picture);
            if (memory != 0)
                Com.Release(memory);
            if (section != 0)
                DeleteObject(section);
        }
    }

    /// <summary>A 32-bit BI_RGB DIB section, its rows top first (a negative height); its pixels in <paramref name="bits"/>. 0 when it could not be made.</summary>
    private static nint CreateTopDownDib(int width, int height, out nint bits)
    {
        // biSize is the header's alone (40 bytes), without the colour after it.
        var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32, Compression = 0 };
        nint pixels;
        var section = CreateDIBSection(0, &info, 0, &pixels, 0, 0);
        bits = pixels;
        return section;
    }

    // DIB_RGB_COLORS (0), no file mapping.
    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint deviceContext, BitmapInfo* info, uint usage, nint* bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial int DeleteObject(nint handle);

    [LibraryImport("shlwapi.dll")]
    private static partial nint SHCreateMemStream(byte* initial, uint size);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdiplusStartup(nint* token, StartupInput* input, void* output);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateBitmapFromStream(nint stream, nint* bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageWidth(nint image, uint* width);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageHeight(nint image, uint* height);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, byte* scan0, nint* bitmap);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipGetImageGraphicsContext(nint image, nint* graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipCreateImageAttributes(nint* attributes);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetImageAttributesWrapMode(nint attributes, int wrap, uint argb, int clamp);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetCompositingMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetInterpolationMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipSetPixelOffsetMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDrawImageRectRectI(nint graphics, nint image, int dstX, int dstY, int dstWidth, int dstHeight,
        int srcX, int srcY, int srcWidth, int srcHeight, int srcUnit, nint attributes, nint callback, nint callbackData);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImage(nint image);

    [LibraryImport("gdiplus.dll")]
    private static partial int GdipDisposeImageAttributes(nint attributes);
}

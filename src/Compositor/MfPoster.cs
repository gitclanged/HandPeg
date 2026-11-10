using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HandPegApp.Compositor.Native;
using HandPegApp.Services;

namespace HandPegApp.Compositor;

/// <summary>
/// A Media Browser poster decoded in this process by Media Foundation (5.4.2A): when the thumbnail compositor does not draw it (busy, or
/// declined), the key frame nearest the moment (the file's own index, NativeKeyFrames) is read by a Source Reader on the processor, made
/// small with a box filter as it is turned from YUV into BGRA, and written as a JPEG, with no FFmpeg started. One at a time: the decoder
/// and its pictures go with each poster, so nothing of it stays in memory between posters.
/// </summary>
internal static class MfPoster
{
    // One poster at a time: a software HEVC decoder of a 1440p file holds some tens of MB while it works.
    private static readonly SemaphoreSlim One = new(1, 1);

    // How many samples are read past a seek for the first picture (a decoder may first answer with a format change).
    private const int MostReads = 16;

    private const int JpegQuality = 88;

    /// <summary>
    /// The frame at <paramref name="atSeconds"/> (the container's clock; the key frame nearest it) written to <paramref name="imagePath"/> as a JPEG
    /// <paramref name="height"/> pixels tall. False, and nothing left written, when Media Foundation cannot decode it (FFmpeg then makes it).
    /// </summary>
    public static async Task<bool> TryWriteAsync(string videoPath, string imagePath, double atSeconds, int height, CancellationToken token)
    {
        await One.WaitAsync(token);
        try
        {
            return await Task.Run(() => Write(videoPath, imagePath, atSeconds, height), token);
        }
        finally
        {
            One.Release();
        }
    }

    private static bool Write(string videoPath, string imagePath, double atSeconds, int height)
    {
        try
        {
            if (Decode(videoPath, atSeconds, height) is not { } picture)
                return false;

            var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
            encoder.Frames.Add(BitmapFrame.Create(picture));
            Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
            using (var file = new FileStream(imagePath, FileMode.Create, FileAccess.Write, FileShare.None))
                encoder.Save(file);
            return true;
        }
        catch (Exception ex) when (ex is GpuException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Write($"A poster of {Path.GetFileName(videoPath)} could not be decoded by Media Foundation ({ex.Message}); FFmpeg makes it.");
            try
            {
                File.Delete(imagePath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }

    /// <summary>
    /// The key frame nearest <paramref name="atSeconds"/> as a frozen BGR32 picture <paramref name="height"/> pixels tall (the video's shape, its
    /// width even); null when the file has no video Media Foundation decodes. Throws <see cref="GpuException"/> on a reader that fails midway.
    /// </summary>
    internal static unsafe BitmapSource? Decode(string videoPath, double atSeconds, int height)
    {
        MediaFoundation.Start();
        nint attributes = 0, reader = 0, sample = 0, type = 0;
        try
        {
            Com.Check(Mf.MFCreateAttributes(&attributes, 2), "MFCreateAttributes");
            Mf.SetUInt32(attributes, MfGuids.DisableDxva, 1);
            Mf.SetUInt32(attributes, MfGuids.EnableHardwareTransforms, 0);
            if (Mf.MFCreateSourceReaderFromURL(videoPath, attributes, &reader) < 0)
                return null;

            _ = Mf.SetStreamSelection(reader, Mf.AllStreams, false);
            if (Mf.SetStreamSelection(reader, Mf.FirstVideoStream, true) < 0 || !SetOutput(reader, MfGuids.Nv12) && !SetOutput(reader, MfGuids.P010))
                return null;

            // To the key frame nearest the moment, from the file's own index when it has one: a seek lands on the key frame at or before
            // where it is sent, and decodes nothing more for the first picture. Sent half way to the key frame after it (a second at most),
            // so it lands on that very one whatever small difference there is between the index's clock and the reader's (a stream's
            // start: B-frames' delay, an edit list).
            var at = Math.Max(atSeconds, 0);
            if (NativeKeyFrames.Read(videoPath) is { Length: > 0 } keys)
                at = SeekFor(keys, at);
            if (at > 0)
                _ = Mf.SetCurrentPosition(reader, (long)(at * 1e7));

            for (var read = 0; read < MostReads && sample == 0; read++)
            {
                Com.Check(Mf.ReadSample(reader, Mf.FirstVideoStream, out var flags, out _, out sample), "ReadSample");
                if ((flags & (Mf.ReaderError | Mf.ReaderEndOfStream)) != 0)
                    return null;
            }

            if (sample == 0)
                return null;

            Com.Check(Mf.GetCurrentMediaType(reader, Mf.FirstVideoStream, out type), "GetCurrentMediaType");
            return PictureOf(sample, type, height);
        }
        finally
        {
            Com.Release(ref type);
            Com.Release(ref sample);
            Com.Release(ref reader);
            Com.Release(ref attributes);
        }
    }

    /// <summary>
    /// Where to send a seek for the key frame nearest <paramref name="at"/> (<paramref name="keys"/> ascending, seconds): half way from it to
    /// the next (at most a second on, a tenth after the last), the earlier of two as near (pure: the goldens' gpu-filmstrip).
    /// </summary>
    internal static double SeekFor(double[] keys, double at)
    {
        var index = Array.BinarySearch(keys, at);
        if (index < 0)
        {
            index = ~index;
            if (index >= keys.Length || index > 0 && at - keys[index - 1] <= keys[index] - at)
                index--;
        }

        index = Math.Max(index, 0);
        return keys[index] + (index + 1 < keys.Length ? Math.Min((keys[index + 1] - keys[index]) / 2, 1) : 0.1);
    }

    private static unsafe bool SetOutput(nint reader, in Guid subtype)
    {
        nint type;
        Com.Check(Mf.MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            Mf.SetGuid(type, MfGuids.MajorType, MfGuids.Video);
            Mf.SetGuid(type, MfGuids.Subtype, subtype);
            return Mf.SetCurrentMediaType(reader, Mf.FirstVideoStream, type) >= 0;
        }
        finally
        {
            Com.Release(ref type);
        }
    }

    // The sample's picture (NV12 or P010, rows of pitch bytes, the chroma after the padded luma), boxed down to the height asked.
    private static unsafe BitmapSource? PictureOf(nint sample, nint type, int height)
    {
        _ = Mf.GetGuid(type, MfGuids.Subtype, out var subtype);
        var bytesPerSample = subtype == MfGuids.P010 ? 2 : 1;
        Com.Check(Mf.GetUInt64(type, MfGuids.FrameSize, out var size), "MF_MT_FRAME_SIZE");
        var (paddedWidth, paddedHeight) = ((int)(size >> 32), (int)(size & 0xFFFFFFFF));
        var (width, height0) = (paddedWidth, paddedHeight);
        MfVideoArea area;
        if (Mf.GetBlob(type, MfGuids.MinimumDisplayAperture, &area, (uint)sizeof(MfVideoArea)) >= 0 && area.Width > 0 && area.Height > 0)
            (width, height0) = (Math.Min(area.Width, width), Math.Min(area.Height, height0));
        (width, height0) = (width & ~1, height0 & ~1);
        if (width < 2 || height0 < 2)
            return null;

        // BT.709 unless the type says otherwise (2 BT.601, 4 BT.2020), as the compositor reads it (MfVideoDecoder); limited range unless it says full (1).
        var matrix = Mf.GetUInt32(type, MfGuids.YuvMatrix, out var m) >= 0 ? (int)m : 1;
        var fullRange = Mf.GetUInt32(type, MfGuids.NominalRange, out var range) >= 0 && range == 1;

        var outHeight = Math.Clamp(height, 2, height0) & ~1;
        var outWidth = Math.Max((int)Math.Round((double)width * outHeight / height0 / 2) * 2, 2);
        var bgra = new byte[outWidth * outHeight * 4];

        Com.Check(Mf.GetBufferByIndex(sample, 0, out var buffer), "GetBufferByIndex");
        var buffer2D = Com.Query(buffer, Iid.Mf2DBuffer);
        byte* data = null;
        var pitch = 0;
        var locked2D = buffer2D != 0 && Mf.Lock2D(buffer2D, out data, out pitch) >= 0;
        try
        {
            if (!locked2D)
            {
                Com.Check(Mf.Lock(buffer, out data, out var length), "IMFMediaBuffer::Lock");
                pitch = Mf.GetUInt32(type, MfGuids.DefaultStride, out var stride) >= 0 && (int)stride > 0 ? (int)stride : paddedWidth * bytesPerSample;
                if ((long)pitch * paddedHeight * 3 / 2 > length)
                    throw new GpuException(unchecked((int)0x80004005), "A picture smaller than its size");
            }

            if (pitch <= 0)
                throw new GpuException(unchecked((int)0x80004005), "A bottom-up picture");
            Convert(data, data + (long)pitch * paddedHeight, pitch, bytesPerSample, width, height0, outWidth, outHeight, matrix, fullRange, bgra);
        }
        finally
        {
            if (locked2D)
                _ = Mf.Unlock2D(buffer2D);
            else
                _ = Mf.Unlock(buffer);
            Com.Release(ref buffer2D);
            Com.Release(ref buffer);
        }

        var picture = BitmapSource.Create(outWidth, outHeight, 96, 96, PixelFormats.Bgr32, null, bgra, outWidth * 4);
        picture.Freeze();
        return picture;
    }

    /// <summary>
    /// YUV 4:2:0 (one or two bytes a sample: P010's top byte is taken) to BGRA, each output pixel the mean of the source pixels it covers
    /// (luma) and of the chroma samples under them: a box filter, as the picture is made a tenth of its size or less.
    /// </summary>
    internal static unsafe void Convert(byte* luma, byte* chroma, int pitch, int bytesPerSample, int width, int height, int outWidth, int outHeight, int matrix,
        bool fullRange, byte[] bgra)
    {
        // The matrix's coefficients (Kr, Kb), and the range's scale and offsets.
        var (kr, kb) = matrix switch { 2 => (0.299, 0.114), 4 => (0.2627, 0.0593), _ => (0.2126, 0.0722) };
        var kg = 1 - kr - kb;
        var (yScale, cScale, yOffset) = fullRange ? (1.0, 1.0, 0.0) : (255.0 / 219, 255.0 / 224, 16.0);
        var high = bytesPerSample - 1;

        for (var oy = 0; oy < outHeight; oy++)
        {
            var (y0, y1) = (oy * height / outHeight, Math.Max((oy + 1) * height / outHeight, oy * height / outHeight + 1));
            for (var ox = 0; ox < outWidth; ox++)
            {
                var (x0, x1) = (ox * width / outWidth, Math.Max((ox + 1) * width / outWidth, ox * width / outWidth + 1));
                long ySum = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = luma + (long)y * pitch + high;
                    for (var x = x0; x < x1; x++)
                        ySum += row[x * bytesPerSample];
                }

                var (cx0, cx1, cy0, cy1) = (x0 / 2, Math.Max(x1 / 2, x0 / 2 + 1), y0 / 2, Math.Max(y1 / 2, y0 / 2 + 1));
                long uSum = 0, vSum = 0;
                for (var y = cy0; y < cy1; y++)
                {
                    var row = chroma + (long)y * pitch + high;
                    for (var x = cx0; x < cx1; x++)
                    {
                        uSum += row[2 * x * bytesPerSample];
                        vSum += row[(2 * x + 1) * bytesPerSample];
                    }
                }

                var yValue = ((double)ySum / ((y1 - y0) * (x1 - x0)) - yOffset) * yScale;
                var chromaCount = (double)((cy1 - cy0) * (cx1 - cx0));
                var u = (uSum / chromaCount - 128) * cScale;
                var v = (vSum / chromaCount - 128) * cScale;
                var r = yValue + 2 * (1 - kr) * v;
                var b = yValue + 2 * (1 - kb) * u;
                var g = (yValue - kr * r - kb * b) / kg;
                var at = (oy * outWidth + ox) * 4;
                bgra[at] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
                bgra[at + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                bgra[at + 2] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                bgra[at + 3] = 255;
            }
        }
    }
}

using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;

namespace HandPegApp.ShellThumbnails;

/// <summary>
/// Finds the thumbnail a HandPeg save baked into a file, reading as little of it as it can: a project bundle's stored thumbnail.jpg
/// (through the archive's directory at its end, never the media), or a project's thumbnailJpeg (right after its header, in the first
/// kilobytes). Its own copy of HandPeg's ProjectThumbnail.ReadFromProject: this library carries nothing of HandPeg's.
/// </summary>
internal static class ThumbnailReader
{
    /// <summary>A bundle's thumbnail entry (ProjectThumbnail.BundleEntry).</summary>
    internal const string BundleEntry = "thumbnail.jpg";

    // The project file's property and the header property it follows (ProjectFile: ThumbnailJpeg right after WrittenBy, camel case).
    private static ReadOnlySpan<byte> PropertyName => "thumbnailJpeg"u8;
    private static ReadOnlySpan<byte> WriterProperty => "writtenBy"u8;

    // How much of a project is read at most for its thumbnail, and the first step of that (read again four times as far while the
    // thumbnail runs past what was read).
    private const int MostBytes = 4 << 20;
    private const int FirstRead = 128 << 10;

    /// <summary>The thumbnail in a project or a bundle (told apart by their first bytes: a bundle is a ZIP archive); null when it has none.</summary>
    internal static byte[]? Read(Stream file)
    {
        Span<byte> start = stackalloc byte[4];
        file.Seek(0, SeekOrigin.Begin);
        var got = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        file.Seek(0, SeekOrigin.Begin);
        var zip = got == start.Length && start[0] == (byte)'P' && start[1] == (byte)'K' && start[2] == 3 && start[3] == 4;
        return zip ? FromBundle(file) : FromProject(file);
    }

    private static byte[]? FromBundle(Stream file)
    {
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.GetEntry(BundleEntry) is not { } entry || entry.Length is <= 0 or > MostBytes)
            return null;

        var jpeg = new byte[entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(jpeg);
        return jpeg;
    }

    private static byte[]? FromProject(Stream file)
    {
        var length = file.Length;
        if (length <= 0)
            return null;

        var buffer = new byte[(int)Math.Min(FirstRead, Math.Min(length, MostBytes))];
        var read = 0;
        while (true)
        {
            read += file.ReadAtLeast(buffer.AsSpan(read), buffer.Length - read, throwOnEndOfStream: false);

            // The end of the file reached (or the file shorter than it said): the text is whole.
            var whole = read < buffer.Length || read >= length;
            var jpeg = FromJson(buffer.AsSpan(0, read), whole, out var cutOff);
            if (jpeg is not null || !cutOff || whole || buffer.Length >= MostBytes)
                return jpeg;

            Array.Resize(ref buffer, (int)Math.Min(Math.Min((long)buffer.Length * 4, MostBytes), length));
        }
    }

    /// <summary>
    /// The thumbnail in a project's text (or the start of it): its top level walked in order, objects stepped over, until
    /// "thumbnailJpeg" (its Base64 decoded) or the first property after "writtenBy" that is not it. <paramref name="cutOff"/> says the
    /// search ran past <paramref name="bytes"/> before it could tell.
    /// </summary>
    internal static byte[]? FromJson(ReadOnlySpan<byte> bytes, bool isFinalBlock, out bool cutOff)
    {
        cutOff = false;
        if (bytes is [0xEF, 0xBB, 0xBF, ..])
            bytes = bytes[3..];

        try
        {
            var reader = new Utf8JsonReader(bytes, isFinalBlock, state: default);
            if (!reader.Read())
            {
                cutOff = !isFinalBlock;
                return null;
            }

            if (reader.TokenType != JsonTokenType.StartObject)
                return null;

            var pastWriter = false;
            while (true)
            {
                if (!reader.Read())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    return null;

                var isThumbnail = reader.ValueTextEquals(PropertyName);
                if (pastWriter && !isThumbnail)
                    return null;

                var isWriter = reader.ValueTextEquals(WriterProperty);
                if (!reader.Read())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                if (isThumbnail)
                    return reader.TokenType == JsonTokenType.String && reader.TryGetBytesFromBase64(out var jpeg) && jpeg.Length > 0 ? jpeg : null;

                if ((reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) && !reader.TrySkip())
                {
                    cutOff = !isFinalBlock;
                    return null;
                }

                pastWriter |= isWriter;
            }
        }
        catch (JsonException)
        {
            // Not JSON, or cut off in the middle of a value in a file read whole: no thumbnail.
            return null;
        }
    }
}

/// <summary>A read-only, seekable Stream over the IStream Explorer hands a handler: what ZipArchive and the reader read through.</summary>
internal sealed class ComStream(IStream source) : Stream
{
    // STATFLAG_NONAME: the stream's size, without its name.
    private const int StatWithoutName = 1;

    // Where a read at an offset into a caller's array lands first (IStream reads into the start of an array).
    private byte[]? _scratch;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            try
            {
                source.Stat(out var stat, StatWithoutName);
                return stat.cbSize;
            }
            catch (Exception ex) when (ex is COMException or NotImplementedException or NotSupportedException)
            {
                // A stream that does not say: its end found by seeking there and back.
                var at = Seek(0, SeekOrigin.Current);
                var end = Seek(0, SeekOrigin.End);
                Seek(at, SeekOrigin.Begin);
                return end;
            }
        }
    }

    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override unsafe int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        if (count == 0)
            return 0;

        var into = buffer;
        if (offset != 0)
        {
            if (_scratch is null || _scratch.Length < count)
                _scratch = new byte[count];
            into = _scratch;
        }

        var read = 0;
        source.Read(into, count, (nint)(&read));
        if (!ReferenceEquals(into, buffer))
            Buffer.BlockCopy(into, 0, buffer, offset, read);
        return read;
    }

    public override unsafe long Seek(long offset, SeekOrigin origin)
    {
        // STREAM_SEEK_SET, _CUR and _END are SeekOrigin's Begin, Current and End.
        long position = 0;
        source.Seek(offset, (int)origin, (nint)(&position));
        return position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

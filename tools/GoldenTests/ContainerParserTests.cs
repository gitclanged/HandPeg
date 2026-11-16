using System.Buffers.Binary;
using System.IO;
using System.Text;
using HandPegApp.Services;
using HandPegApp.Services.Audio;

namespace HandPegGoldenTests;

/// <summary>
/// container-parsers-malformed: HandPeg's own container walkers (MatroskaTiming, Mp4Timing, NativeKeyFrames) read untrusted media,
/// so each crafted file here must come back quickly (within 2 s, on a thread of its own) with nothing or a bounded answer: elements
/// whose sizes wrap 32 bits, children larger than their parents, boxes that claim more than the file, counts far beyond their
/// payload. And a valid file with a version-0 ctts whose first offset is negative reads that offset signed, as FFmpeg does.
/// Every file is built here as bytes; nothing binary is committed.
/// </summary>
public static class ContainerParserTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("container-parsers-malformed", "# Crafted Matroska and MP4 files through HandPeg's own parsers: each ends in time with nothing or a bounded answer.", Malformed);
    }

    private static void Malformed(Action<bool, string> expect, StringBuilder text)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HandPeg.GoldenTests.Parsers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var n = 0;
            string File(byte[] bytes, string extension)
            {
                var path = Path.Combine(folder, $"case{++n}{extension}");
                System.IO.File.WriteAllBytes(path, bytes);
                return path;
            }

            // Runs a parse on its own thread: whether it ended within 2 s, what it returned, and how much it allocated.
            (bool Ended, T? Result, long Allocated) Bounded<T>(Func<T?> parse)
            {
                long allocated = 0;
                var task = Task.Run(() =>
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var result = parse();
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    return result;
                });
                return task.Wait(2000) ? (true, task.Result, allocated) : (false, default, 0);
            }

            // ----- Matroska -----
            byte[] ebml = [0x1A, 0x45, 0xDF, 0xA3, 0x80];
            byte[] unknownSegment = [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
            byte[] wrappingCluster = [0x1F, 0x43, 0xB6, 0x75, 0x08, 0xFF, 0xFF, 0xFF, 0xF7];

            var clusterInTracks = Bounded(() => MatroskaTiming.Read(File([.. ebml, .. unknownSegment, 0x16, 0x54, 0xAE, 0x6B, 0x8E, 0xAE, 0x83, 0x83, 0x81, 0x02, .. wrappingCluster], ".mkv")));
            expect(clusterInTracks.Ended && clusterInTracks.Result is [{ IsAudio: true }],
                "Matroska: a Cluster whose size wraps 32 bits, inside Tracks after a real audio TrackEntry, ends the walk; the entry before it is kept");
            var nestedSegment = Bounded(() => MatroskaTiming.Read(File([.. ebml, .. unknownSegment, 0x18, 0x53, 0x80, 0x67, 0x08, 0xFF, 0xFF, 0xFF, 0xF7], ".mkv")));
            expect(nestedSegment.Ended && nestedSegment.Result is null, "Matroska: a nested Segment whose size wraps 32 bits at the top level ends the walk (no tracks)");
            var oversizeEntry = Bounded(() => MatroskaTiming.Read(File([.. ebml, .. unknownSegment, 0x16, 0x54, 0xAE, 0x6B, 0x8E, 0xAE, 0x8E, 0x83, 0x81, 0x02, .. wrappingCluster], ".mkv")));
            expect(oversizeEntry.Ended && oversizeEntry.Result is { Length: 0 }, "Matroska: a TrackEntry larger than its Tracks is refused, and the walk ends");
            var truncated = Bounded(() => MatroskaTiming.Read(File([0x1A, 0x45, 0xDF], ".mkv")));
            expect(truncated.Ended && truncated.Result is null, "Matroska: a truncated EBML header reads as no tracks");
            var emptySegment = Bounded(() => MatroskaTiming.Read(File([.. ebml, 0x18, 0x53, 0x80, 0x67, 0x80], ".mkv")));
            expect(emptySegment.Ended && emptySegment.Result is null, "Matroska: a Segment of size 0 reads as no tracks");
            var keysOfWrapped = Bounded(() => NativeKeyFrames.Read(File([.. ebml, .. unknownSegment, .. wrappingCluster], ".mkv")));
            expect(keysOfWrapped.Ended && keysOfWrapped.Result is null, "Matroska: the key-frame reader ends on the same wrapping element (no key frames)");

            // ----- MP4 -----
            var truncatedMp4 = Bounded(() => Mp4Timing.Read(File([0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p'], ".mp4")));
            expect(truncatedMp4.Ended && truncatedMp4.Result is null, "MP4: a box header with no body reads as no tracks");
            var huge64 = Bounded(() => Mp4Timing.Read(File([.. Box("ftyp", new byte[8]), 0x00, 0x00, 0x00, 0x01, (byte)'m', (byte)'o', (byte)'o', (byte)'v', 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xF0], ".mp4")));
            expect(huge64.Ended && huge64.Result is null, "MP4: a moov of 64-bit size near 2^63 is refused");

            // A moov claiming 1 MB in a file of a few dozen bytes: refused before a byte is allocated for it.
            var claims = Bounded(() => Mp4Timing.Read(File([.. Box("ftyp", new byte[8]), 0x00, 0x10, 0x00, 0x00, (byte)'m', (byte)'o', (byte)'o', (byte)'v', .. new byte[16]], ".mp4")));
            expect(claims.Ended && claims.Result is null && claims.Allocated < 256 << 10, "MP4: a moov that claims more than the file holds is refused without allocating its claimed size");
            var biggerChild = Bounded(() => Mp4Timing.Read(File([.. Box("ftyp", new byte[8]), .. Box("moov", [0x00, 0x00, 0x10, 0x00, (byte)'t', (byte)'r', (byte)'a', (byte)'k', .. new byte[8]])], ".mp4")));
            expect(biggerChild.Ended && biggerChild.Result is { Length: 0 }, "MP4: a trak larger than its moov ends the walk (no tracks)");

            // A valid movie, then the same with stss and stts counts far beyond their payload.
            var negative = Movie(cttsFirst: unchecked((int)0xFFFFF448), sttsCount: 1, stssCount: 1);
            var timing = Bounded(() => Mp4Timing.Read(File(negative, ".mp4")));
            var keys = Bounded(() => NativeKeyFrames.Read(File(negative, ".mp4")));
            expect(timing.Ended && timing.Result is [{ Handler: "vide" } video] && Math.Abs(video.FirstPts - -3000.0 / 90000) < 1e-9,
                "MP4: a version-0 ctts whose first offset has its top bit set is read signed (-3000 at 90 kHz: the first frame at -0.0333 s), as FFmpeg reads it");
            expect(keys.Ended && keys.Result is { Length: > 0 } listed && timing.Result is [{ } same] && Math.Abs(listed[0] - same.FirstPts) < 1e-6,
                "MP4: the key-frame reader puts the first key frame at the same moment");
            var counts = Bounded(() => NativeKeyFrames.Read(File(Movie(cttsFirst: 0, sttsCount: uint.MaxValue, stssCount: uint.MaxValue), ".mp4")));
            expect(counts.Ended && (counts.Result is null || counts.Result.Length <= 2), "MP4: stts and stss counts far beyond their payload give at most the entries present");

            // A file of a megabyte of padding boxes: walked in bounded memory.
            var padding = new List<byte>(Box("ftyp", new byte[8]));
            for (var i = 0; i < 4096; i++)
                padding.AddRange(Box("free", new byte[248]));
            var big = Bounded(() => Mp4Timing.Read(File([.. padding], ".mp4")));
            expect(big.Ended && big.Result is null && big.Allocated < 8 << 20, "MP4: a 1 MB file of padding boxes and no moov is walked in under 8 MB of allocations");
            text.Append(n).Append(" crafted files\n");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // An ISO BMFF box: its 32-bit size, its type, its body.
    private static byte[] Box(string type, byte[] body)
    {
        var box = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        body.CopyTo(box, 8);
        return box;
    }

    private static byte[] Words(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), words[i]);
        return bytes;
    }

    // A minimal movie: one video track at 90 kHz, two samples of 3000 each, the first a key frame, and a version-0 ctts whose
    // first offset is cttsFirst. The stts and stss entry counts can claim more than their one entry.
    private static byte[] Movie(int cttsFirst, uint sttsCount, uint stssCount)
    {
        var mvhd = new byte[100];
        BinaryPrimitives.WriteUInt32BigEndian(mvhd.AsSpan(12), 1000);
        var mdhd = Words(0, 0, 0, 90000, 6000, 0);
        byte[] hdlr = [.. Words(0, 0), (byte)'v', (byte)'i', (byte)'d', (byte)'e', .. new byte[13]];
        var stts = Words(0, sttsCount, 2, 3000);
        var stss = Words(0, stssCount, 1);
        var ctts = Words(0, 1, 2, unchecked((uint)cttsFirst));
        var stbl = Box("stbl", [.. Box("stts", stts), .. Box("stss", stss), .. Box("ctts", ctts)]);
        var trak = Box("trak", Box("mdia", [.. Box("mdhd", mdhd), .. Box("hdlr", hdlr), .. Box("minf", stbl)]));
        return [.. Box("ftyp", [(byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 1]), .. Box("moov", [.. Box("mvhd", mvhd), .. trak])];
    }
}

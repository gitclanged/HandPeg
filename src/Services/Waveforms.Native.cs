using System.IO;
using HandPegApp.Compositor.Native;
using HandPegApp.Services.Audio;

namespace HandPegApp.Services;

// Peaks without FFmpeg (5.1-CleanupStudioAndFfmpegPurge, the Great FFmpeg Purge): Media Foundation's Source Reader over every stream
// wanted in one pass, in this process. One reading of the file whatever the number of its tracks, as the FFmpeg graph it takes over
// from was built to be: a recording with six tracks must not read its gigabytes six times. FFmpeg's pipe is what is left for a file
// Windows cannot decode.
public static partial class Waveforms
{
    /// <summary>
    /// A file's tracks read as peaks, each by its own reader, in one pass. False (the readers then hold nothing to use) when Media
    /// Foundation cannot read it: FFmpeg's pipe does then. An MP4 with several sound tracks is FFmpeg's too: Windows lists them in an
    /// order of its own (AudioDecoder.Open).
    /// </summary>
    private static unsafe bool ReadPeaksNatively(string path, IReadOnlyList<int> tracks, PeakReader[] readers, CancellationToken token)
    {
        if (tracks.Count == 0 || !MfAudio.Start() || Mp4Timing.Read(path) is { } mp4 && mp4.Count(t => t.Handler == "soun") > 1)
            return false;

        nint reader = 0;
        try
        {
            nint made;
            if (Mf.MFCreateSourceReaderFromURL(path, 0, &made) < 0)
                return false;
            reader = made;

            // Which of the reader's streams is each track asked for.
            var streams = new Dictionary<uint, StreamPeaks>();
            var seen = -1;
            _ = Mf.SetStreamSelection(reader, Mf.AllStreams, false);
            for (uint i = 0; i < 64; i++)
            {
                if (Mf.GetNativeMediaType(reader, i, 0, out var type) < 0)
                    break;
                try
                {
                    if (Mf.GetGuid(type, MfGuids.MajorType, out var major) >= 0 && major == MfAudio.Audio && ++seen >= 0 && IndexOf(tracks, seen) is >= 0 and var at)
                        streams[i] = new StreamPeaks(readers[at]);
                }
                finally
                {
                    Com.Release(ref type);
                }
            }

            if (streams.Count != tracks.Count)
                return false;

            foreach (var (stream, peaks) in streams)
            {
                if (Mf.SetStreamSelection(reader, stream, true) < 0 || !AsFloat(reader, stream, peaks))
                    return false;
            }

            var open = streams.Count;
            while (open > 0)
            {
                token.ThrowIfCancellationRequested();
                var hr = Mf.ReadSampleAny(reader, out var stream, out var flags, out var time, out var sample);
                try
                {
                    if (hr < 0 || (flags & Mf.ReaderError) != 0 || !streams.TryGetValue(stream, out var peaks))
                        return false;

                    if ((flags & Mf.ReaderCurrentMediaTypeChanged) != 0 && !ReadShape(reader, stream, peaks))
                        return false;
                    if (sample != 0 && !peaks.Take(sample, time))
                        return false;
                    if ((flags & Mf.ReaderEndOfStream) != 0)
                    {
                        open--;
                        _ = Mf.SetStreamSelection(reader, stream, false);
                    }
                }
                finally
                {
                    Com.Release(ref sample);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is GpuException or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            AppLog.Write($"Waveforms: Media Foundation could not read {Path.GetFileName(path)}", ex);
            return false;
        }
        finally
        {
            Com.Release(ref reader);
        }

        static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] == value)
                    return i;
            }

            return -1;
        }
    }

    private static unsafe bool AsFloat(nint reader, uint stream, StreamPeaks peaks)
    {
        nint output;
        if (Mf.MFCreateMediaType(&output) < 0)
            return false;
        try
        {
            Mf.SetGuid(output, MfGuids.MajorType, MfAudio.Audio);
            Mf.SetGuid(output, MfGuids.Subtype, MfAudio.Float);
            return Mf.SetCurrentMediaType(reader, stream, output) >= 0 && ReadShape(reader, stream, peaks);
        }
        finally
        {
            Com.Release(ref output);
        }
    }

    private static bool ReadShape(nint reader, uint stream, StreamPeaks peaks)
    {
        if (Mf.GetCurrentMediaType(reader, stream, out var current) < 0)
            return false;
        try
        {
            if (Mf.GetUInt32(current, MfAudio.SamplesPerSecond, out var rate) < 0 || Mf.GetUInt32(current, MfAudio.NumChannels, out var channels) < 0
                || rate == 0 || channels is 0 or > 32)
            {
                return false;
            }

            (peaks.Rate, peaks.Channels) = ((int)rate, (int)channels);
            return true;
        }
        finally
        {
            Com.Release(ref current);
        }
    }

    /// <summary>
    /// One stream on its way to peaks: made mono, then brought down to the rate peaks are counted at by averaging what falls in each
    /// step (what the graph's aresample did, near enough for an outline). A gap in the stream is silence, so that a peak's place is its time.
    /// </summary>
    private sealed class StreamPeaks(PeakReader peaks)
    {
        // How far a sample's time may stray from the count before it is taken for a gap, seconds.
        private const double LargestDrift = 0.05;

        private double _sum, _first = double.NaN;
        private long _frames, _phase;
        private int _held;

        public int Rate { get; set; }
        public int Channels { get; set; }

        public unsafe bool Take(nint sample, long time)
        {
            if (MfAudio.ContiguousBuffer(sample, out var buffer) < 0)
                return false;
            try
            {
                if (Mf.Lock(buffer, out var data, out var length) < 0)
                    return false;
                try
                {
                    var frames = (int)(length / (uint)(4 * Channels));
                    var seconds = time / 1e7;
                    if (double.IsNaN(_first))
                        _first = seconds;
                    var late = (long)Math.Round((seconds - _first) * Rate) - _frames;
                    if (late > LargestDrift * Rate)
                    {
                        for (var i = 0L; i < late; i++)
                            Step(0);
                    }

                    var floats = new ReadOnlySpan<float>(data, frames * Channels);
                    for (var frame = 0; frame < frames; frame++)
                    {
                        var mono = 0f;
                        for (var channel = 0; channel < Channels; channel++)
                            mono += floats[frame * Channels + channel];
                        Step(mono / Channels);
                    }

                    return true;
                }
                finally
                {
                    _ = Mf.Unlock(buffer);
                }
            }
            finally
            {
                Com.Release(ref buffer);
            }
        }

        private void Step(float value)
        {
            _sum += value;
            _held++;
            _frames++;
            _phase += SampleRate;
            while (_phase >= Rate)
            {
                _phase -= Rate;
                peaks.Add((short)Math.Clamp(_sum / Math.Max(_held, 1) * 32768, short.MinValue, short.MaxValue));
                (_sum, _held) = (0, 0);
            }
        }
    }

    /// <summary>
    /// One track's peaks, from the cache or read now, for what looks at them instead of drawing them (Remove Dead Air). Null when the
    /// sound cannot be read.
    /// </summary>
    public static async Task<WaveformData?> PeaksOfAsync(string mediaPath, int track, CancellationToken cancellationToken) =>
        DataOf((await RenderManyAsync(mediaPath, [new WaveformRequest([track], 16, 16, "gray")], cancellationToken, WorkLane.Interactive))[0]);
}

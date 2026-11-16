using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using HandPegApp.Compositor.Native;
using HandPegApp.Native;

namespace HandPegApp.Services.Audio;

/// <summary>
/// Microphone recorder over WASAPI shared-mode capture (no FFmpeg): one capture thread per stretch writes a 16-bit PCM WAV segment,
/// converted by the audio engine to 48 kHz stereo where it can, else kept at the endpoint's own rate. Pause ends a segment; Stop
/// joins the segments into one file.
/// </summary>
/// <remarks>Driven from one thread at a time (the Voiceover pane's commands); the capture thread owns every COM object it opens.</remarks>
public sealed class VoiceRecorder
{
    /// <summary>Longest wait for the endpoint to open before the recording counts as failed (ms).</summary>
    internal const int OpenWaitMs = 4000;

    private readonly List<string> _parts = [];
    private Take? _take;

    // Display name to endpoint id from the last listing; names shared by two endpoints are numbered.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal);

    /// <summary>A stretch is being recorded right now.</summary>
    public bool IsRecording => _take is not null;

    /// <summary>A recording has been started and not yet stopped: it is running, or paused.</summary>
    public bool IsSessionOpen => IsRecording || _parts.Count > 0;

    /// <summary>Names of the active capture endpoints, the default one first.</summary>
    public static Task<List<string>> ListMicrophonesAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var endpoints = AudioDevices.Microphones().OrderByDescending(d => d.IsDefault).ToList();
        var names = new List<string>(endpoints.Count);
        lock (Known)
        {
            Known.Clear();
            foreach (var endpoint in endpoints)
            {
                var name = endpoint.Name;
                for (var n = 2; Known.ContainsKey(name); n++)
                    name = string.Create(CultureInfo.InvariantCulture, $"{endpoint.Name} ({n})");
                Known[name] = endpoint.Id;
                names.Add(name);
            }
        }

        return names;
    }, cancellationToken);

    private static string? IdOf(string microphone)
    {
        lock (Known)
            return Known.TryGetValue(microphone, out var id) ? id : null;
    }

    /// <summary>
    /// Starts or resumes recording from the named microphone; throws <see cref="InvalidOperationException"/> with the reason when it
    /// cannot open (unplugged, blocked by Windows privacy settings, held in exclusive mode by another program).
    /// </summary>
    /// <param name="folder">Segment storage until Stop.</param>
    public async Task StartAsync(string microphone, string folder)
    {
        Opening = true;
        try
        {
            await OpenAsync(microphone, folder);
        }
        finally
        {
            Opening = false;
        }
    }

    /// <summary>True from the call of <see cref="StartAsync"/> until it finishes (set before its first await).</summary>
    public bool Opening { get; private set; }

    private async Task OpenAsync(string microphone, string folder)
    {
        if (_take is not null)
            throw new InvalidOperationException("A recording is already running.");
        if (string.IsNullOrWhiteSpace(microphone))
            throw new InvalidOperationException("Choose a microphone first.");

        if (IdOf(microphone) is null)
            await ListMicrophonesAsync(CancellationToken.None);
        var id = IdOf(microphone) ?? throw new InvalidOperationException($"\"{microphone}\" is not connected.");

        Directory.CreateDirectory(folder);
        var part = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"voiceover_part{_parts.Count}.wav"));
        var take = new Take(id, part);
        if (await Task.WhenAny(take.Opened, Task.Delay(OpenWaitMs)) != take.Opened)
        {
            await Task.Run(take.Stop);
            throw new InvalidOperationException("The microphone did not answer.");
        }

        try
        {
            await take.Opened;
        }
        catch (InvalidOperationException)
        {
            await Task.Run(take.Stop);
            WavFile.Delete(part);
            throw;
        }

        _take = take;
        _parts.Add(part);
    }

    /// <summary>Ends the stretch being recorded, keeping the recording open to be carried on.</summary>
    public async Task PauseAsync()
    {
        if (_take is not { } take)
            return;

        _take = null;
        await Task.Run(take.Stop);
    }

    /// <summary>Ends the recording and throws away everything recorded so far.</summary>
    public async Task DiscardAsync()
    {
        await PauseAsync();
        foreach (var part in _parts)
            WavFile.Delete(part);
        _parts.Clear();
    }

    /// <summary>
    /// Stops and writes one file (a single segment as it is, else the segments joined). Returns false when nothing usable was recorded.
    /// </summary>
    public async Task<bool> StopAsync(string outputPath, CancellationToken cancellationToken)
    {
        await PauseAsync();

        var parts = _parts.Where(p => File.Exists(p) && new FileInfo(p).Length > WavFile.HeaderBytes + 960).ToList();
        if (parts.Count == 0)
        {
            DeleteParts();
            return false;
        }

        // The segments stay (and _parts with them) until the take is written: a failed join or move keeps it for another Stop.
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        if (parts.Count == 1)
        {
            File.Move(parts[0], outputPath, overwrite: true);
        }
        else
        {
            var joining = outputPath + ".joining";
            try
            {
                await Task.Run(() => WavFile.Join(parts, joining), cancellationToken);
                File.Move(joining, outputPath, overwrite: true);
            }
            catch
            {
                WavFile.Delete(joining);
                throw;
            }
        }

        DeleteParts();
        return true;
    }

    // Deletes every segment file of the take and forgets them.
    private void DeleteParts()
    {
        foreach (var part in _parts)
            WavFile.Delete(part);
        _parts.Clear();
    }

    // ----- One stretch of capture -----

    /// <summary>
    /// A capture thread for one segment: opens the endpoint, signals <see cref="Opened"/> (or fails it with the reason), then drains
    /// packets into the WAV file until <see cref="Stop"/> or the endpoint goes away (what was captured is kept).
    /// </summary>
    private sealed unsafe class Take
    {
        // Engine buffer: generous, as this thread also writes to disk; a recording has no latency to keep low.
        private const long BufferHns = 2_000_000;
        private const int StopWaitMs = 3000, PollMs = 100;

        private readonly string _deviceId, _path;
        private readonly nint _stop;
        private readonly Thread _thread;
        private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Take(string deviceId, string path)
        {
            (_deviceId, _path) = (deviceId, path);
            _stop = Kernel32.CreateEventW(0, 1, 0, 0);
            _thread = new Thread(Run) { IsBackground = true, Name = "HandPeg voiceover capture", Priority = ThreadPriority.AboveNormal };
            _thread.Start();
        }

        /// <summary>Completes once capture runs; faults with an <see cref="InvalidOperationException"/> naming why it could not.</summary>
        public Task Opened => _opened.Task;

        /// <summary>Ends capture and finishes the file. A thread that does not stop in time keeps its stop event (it may still wait on it).</summary>
        public void Stop()
        {
            Kernel32.SetEvent(_stop);
            if (ThreadStop.Join(_thread, StopWaitMs, "Voiceover capture"))
                Kernel32.CloseHandle(_stop);
        }

        private void Run()
        {
            _ = Com.CoInitializeEx(0, Com.CoInitMultithreaded);
            nint enumerator = 0, device = 0, client = 0, capture = 0, ready = 0;
            WavFile? file = null;
            var started = false;
            try
            {
                var classId = AudioNative.DeviceEnumeratorClass;
                var interfaceId = AudioNative.DeviceEnumeratorId;
                Check(AudioNative.CoCreateInstance(&classId, 0, AudioNative.ClassContextAll, &interfaceId, &enumerator), "the device list");
                Check(AudioNative.GetDevice(enumerator, _deviceId, out device), "the microphone");
                Check(AudioNative.Activate(device, AudioNative.AudioClientId, out client), "the microphone");

                // 48 kHz stereo 16-bit through the engine's converter; else the mix format, converted here.
                var wanted = new WaveFormat { FormatTag = WaveFormat.Pcm, Channels = 2, SamplesPerSecond = 48000, BitsPerSample = 16, BlockAlign = 4, AverageBytesPerSecond = 192000 };
                var flags = AudioNative.StreamFlagsEventCallback | AudioNative.StreamFlagsAutoConvertPcm | AudioNative.StreamFlagsSrcDefaultQuality;
                var source = wanted;
                var engineConverts = AudioNative.Initialize(client, flags, BufferHns, &wanted) >= 0;
                if (!engineConverts)
                {
                    Com.Release(ref client);
                    Check(AudioNative.Activate(device, AudioNative.AudioClientId, out client), "the microphone");
                    source = MixFormat(client);
                    Check(AudioNative.Initialize(client, AudioNative.StreamFlagsEventCallback, BufferHns, &source), "the microphone's format");
                }

                ready = Kernel32.CreateEventW(0, 0, 0, 0);
                Check(AudioNative.SetEventHandle(client, ready), "the microphone's events");
                Check(AudioNative.GetService(client, AudioNative.CaptureClientId, out capture), "the microphone's capture");
                var converter = new Converter(source);
                file = new WavFile(_path, (int)source.SamplesPerSecond, converter.Channels);
                Check(AudioNative.Start(client), "the microphone");
                started = true;
                AppLog.Write(string.Create(CultureInfo.InvariantCulture,
                    $"Voiceover: recording at {source.SamplesPerSecond} Hz, {source.Channels} channel(s) in, {converter.Channels} written, {(engineConverts ? "converted by Windows" : "converted here")}"));
                _opened.TrySetResult();

                // Stop event first; the ready event or the poll timeout drains (a driver that never signals still records).
                var handles = stackalloc nint[2] { _stop, ready };
                while (Kernel32.WaitForMultipleObjects(2, handles, 0, PollMs) is not (AudioNative.WaitObject0 or AudioNative.Infinite))
                {
                    if (Drain(capture, converter, file) is var hr and < 0)
                    {
                        AppLog.Write(string.Create(CultureInfo.InvariantCulture, $"Voiceover: the microphone stopped (0x{hr:X8}); what was recorded is kept"));
                        break;
                    }
                }

                Drain(capture, converter, file);
            }
            catch (InvalidOperationException ex)
            {
                AppLog.Write("Voiceover: the microphone could not be opened", ex);
                _opened.TrySetException(ex);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nothing may escape a thread of its own: it would end the process.
                AppLog.Write("Voiceover: the recording could not be written", ex);
                _opened.TrySetException(new InvalidOperationException($"The recording could not be written: {ex.Message}", ex));
            }
            finally
            {
                if (started)
                    AudioNative.Stop(client);

                // A header flush failing (disk full) must neither end the process nor skip the releases below.
                try
                {
                    file?.Dispose();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Write("Voiceover: the recording's header could not be written", ex);
                }

                Com.Release(ref capture);
                Com.Release(ref client);
                Com.Release(ref device);
                Com.Release(ref enumerator);
                if (ready != 0)
                    Kernel32.CloseHandle(ready);
                _opened.TrySetException(new InvalidOperationException("The microphone could not be opened."));
            }
        }

        // Every queued packet into the file; the first failing HRESULT (device gone), else 0.
        private static int Drain(nint capture, Converter converter, WavFile file)
        {
            int hr;
            while ((hr = AudioNative.NextPacketFrames(capture, out var waiting)) >= 0 && waiting > 0)
            {
                if ((hr = AudioNative.GetCaptured(capture, out var data, out var frames, out var flags)) < 0)
                    return hr;
                try
                {
                    file.Write(converter.Convert(data, (int)frames, (flags & AudioNative.BufferFlagsSilent) != 0));
                }
                finally
                {
                    AudioNative.ReleaseCaptured(capture, frames);
                }
            }

            return hr < 0 ? hr : 0;
        }

        private static WaveFormat MixFormat(nint client)
        {
            Check(AudioNative.GetMixFormat(client, out var mix), "the microphone's format");
            WaveFormat format = default;
            try
            {
                // A plain WAVEFORMATEX is 18 bytes: read only what the device returned.
                var size = mix->FormatTag == WaveFormat.Extensible && mix->ExtraSize >= 22 ? 40 : 18;
                Buffer.MemoryCopy(mix, &format, sizeof(WaveFormat), size);
                if (size == 18)
                    format.ExtraSize = 0;
            }
            finally
            {
                Com.CoTaskMemFree((nint)mix);
            }

            return format;
        }

        private static void Check(int hr, string what)
        {
            if (hr >= 0)
                return;

            throw new InvalidOperationException(hr switch
            {
                AudioNative.AccessDenied => "Windows blocks microphone access: allow desktop apps under Privacy & security > Microphone.",
                AudioNative.DeviceInUse => "another program holds the microphone in exclusive mode.",
                _ when AudioNative.IsDeviceGone(hr) => "the microphone was unplugged or turned off.",
                _ => string.Create(CultureInfo.InvariantCulture, $"{what} could not be opened (0x{hr:X8})."),
            });
        }
    }

    /// <summary>
    /// Captured packets to 16-bit PCM: the engine-converted format as it is; a mix format (float, 16/24/32-bit integer) converted here,
    /// keeping at most two channels (the first two of a microphone array).
    /// </summary>
    private sealed unsafe class Converter
    {
        private readonly int _inChannels, _bits;
        private readonly bool _float;
        private short[] _out = [];

        public Converter(WaveFormat format)
        {
            _inChannels = Math.Max((int)format.Channels, 1);
            _bits = format.BitsPerSample;
            _float = format.FormatTag == WaveFormat.IeeeFloat || (format.FormatTag == WaveFormat.Extensible && format.SubFormat == AudioNative.SubtypeFloat);
            Channels = Math.Min(_inChannels, 2);
            if (!_float && _bits is not (16 or 24 or 32))
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"the microphone's {_bits}-bit format is not one HandPeg records."));
        }

        /// <summary>Channels written to the file.</summary>
        public int Channels { get; }

        public ReadOnlySpan<short> Convert(byte* data, int frames, bool silent)
        {
            var count = frames * Channels;
            if (_out.Length < count)
                _out = new short[Math.Max(count, 4096)];
            var output = _out.AsSpan(0, count);
            if (silent || data == null)
            {
                output.Clear();
                return output;
            }

            var bytes = _bits / 8;
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < Channels; c++)
                {
                    var at = data + ((long)f * _inChannels + c) * bytes;
                    output[f * Channels + c] = _float ? FromFloat(*(float*)at)
                        : _bits == 16 ? *(short*)at
                        : _bits == 24 ? (short)((at[1]) | (at[2] << 8))
                        : (short)(*(int*)at >> 16);
                }
            }

            return output;
        }

        private static short FromFloat(float sample) => (short)Math.Clamp(MathF.Round(sample * 32767f), -32768f, 32767f);
    }
}

/// <summary>
/// A 16-bit PCM WAV written as it is recorded: the header's sizes are brought up to date every two seconds, so a crash leaves a
/// playable file. Also joins finished segments (rate and channels converted to the first one's when they differ).
/// </summary>
internal sealed class WavFile : IDisposable
{
    public const int HeaderBytes = 44;

    // RIFF sizes are 32-bit: stop adding audio before the data chunk would overflow (about six hours at 48 kHz stereo).
    private const long MostData = uint.MaxValue - HeaderBytes;

    private readonly FileStream _stream;
    private readonly int _rate, _channels;
    private long _data, _sinceHeader;

    public WavFile(string path, int rate, int channels)
    {
        (_rate, _channels) = (rate, channels);
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        _stream.Write(Header(rate, channels, 0));
    }

    public void Write(ReadOnlySpan<short> samples)
    {
        var bytes = MemoryMarshal.AsBytes(samples);
        if (_data + bytes.Length > MostData)
            return;

        _stream.Write(bytes);
        (_data, _sinceHeader) = (_data + bytes.Length, _sinceHeader + bytes.Length);
        if (_sinceHeader >= 2L * _rate * _channels * 2)
        {
            _sinceHeader = 0;
            UpdateHeader();
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        try
        {
            UpdateHeader();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void UpdateHeader()
    {
        var end = _stream.Position;
        _stream.Position = 0;
        _stream.Write(Header(_rate, _channels, _data));
        _stream.Position = end;
    }

    /// <summary>The 44-byte header of a 16-bit PCM WAV with <paramref name="data"/> bytes of samples.</summary>
    public static byte[] Header(int rate, int channels, long data)
    {
        var header = new byte[HeaderBytes];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(36 + data));
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)rate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)(rate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), (uint)data);
        return header;
    }

    /// <summary>Rate, channels and samples of one of these files (header as <see cref="Header"/> writes it); null if not one.</summary>
    public static (int Rate, int Channels, short[] Samples)? Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < HeaderBytes || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(36, 4).SequenceEqual("data"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)) != 16)
        {
            return null;
        }

        var (channels, rate) = (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)), (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        if (channels is < 1 or > 2 || rate <= 0)
            return null;

        // Whole frames only, so a torn last frame cannot swap the channels of the segment after it.
        var data = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)), (uint)(bytes.Length - HeaderBytes)) / (2 * channels) * (2 * channels);

        return (rate, channels, MemoryMarshal.Cast<byte, short>(bytes.AsSpan(HeaderBytes, data)).ToArray());
    }

    /// <summary>
    /// Writes the segments one after another into <paramref name="output"/>, in the first one's rate and channels (others converted:
    /// mono and stereo mapped, the rate resampled linearly). Throws <see cref="InvalidOperationException"/> when a segment is unreadable.
    /// </summary>
    public static void Join(IReadOnlyList<string> parts, string output)
    {
        var first = Read(parts[0]) ?? throw new InvalidOperationException("A part of the recording could not be read.");
        using var file = new WavFile(output, first.Rate, first.Channels);
        file.Write(first.Samples);
        foreach (var part in parts.Skip(1))
        {
            var next = Read(part) ?? throw new InvalidOperationException("A part of the recording could not be read.");
            file.Write(next.Rate == first.Rate && next.Channels == first.Channels ? next.Samples : Converted(next, first.Rate, first.Channels));
        }
    }

    private static short[] Converted((int Rate, int Channels, short[] Samples) part, int rate, int channels)
    {
        var frames = part.Samples.Length / part.Channels;
        var outFrames = (int)((long)frames * rate / part.Rate);
        var result = new short[outFrames * channels];
        for (var f = 0; f < outFrames; f++)
        {
            var at = (double)f * part.Rate / rate;
            var (low, mix) = ((int)at, at - (int)at);
            var high = Math.Min(low + 1, frames - 1);
            for (var c = 0; c < channels; c++)
            {
                double Sample(int frame) => part.Channels == channels ? part.Samples[frame * channels + c]
                    : part.Channels == 1 ? part.Samples[frame]
                    : (part.Samples[frame * 2] + part.Samples[frame * 2 + 1]) / 2.0;
                result[f * channels + c] = (short)Math.Round(Sample(low) * (1 - mix) + Sample(high) * mix);
            }
        }

        return result;
    }

    /// <summary>Deletes a segment; a locked one is left for the session folder's clean-up.</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

using System.Globalization;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// Shared Timeline block images by key: per-file filmstrips and per-stream waveforms (<see cref="SoundKey"/>). One image serves every
/// clip of that file across tabs; blocks read here at draw time and <see cref="Changed"/> triggers redraw, so clips hold no images
/// and nothing polls.
/// </summary>
/// <remarks>
/// UI thread only (<see cref="ThreadOwner"/>). Images for files used by an open Sequence are always kept; of the rest, the
/// <c>kept</c> most recent stay so undo-restored clips find theirs. <see cref="Release"/> drops all unused-file images at once.
/// </remarks>
public sealed class ClipPictures(int kept, Func<IEnumerable<string>> filesInUse)
{
    private readonly record struct Entry(ImageSource Picture, string File, long Stamp);

    private readonly Dictionary<string, Entry> _film = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _sound = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThreadOwner _owner = new();
    private long _stamp;

    /// <summary>Files used by background tabs; window-injected (a VM only knows its own Sequence).</summary>
    public Func<IEnumerable<string>> OtherFilesInUse { get; set; } = static () => [];

    /// <summary>Raised on store/release so the Timeline redraws blocks.</summary>
    public event Action? Changed;

    /// <summary>Waveform key for <paramref name="file"/> stream <paramref name="stream"/> (the file path itself for stream 0).</summary>
    public static string SoundKey(string file, int stream) =>
        stream <= 0 ? file : string.Create(CultureInfo.InvariantCulture, $"{file}|a{stream}");

    /// <summary>Filmstrip of a video file; null until generated.</summary>
    public ImageSource? Film(string file) => _film.TryGetValue(file, out var entry) ? entry.Picture : null;

    /// <summary>Waveform for a <see cref="SoundKey"/>; null until generated.</summary>
    public ImageSource? Sound(string key) => _sound.TryGetValue(key, out var entry) ? entry.Picture : null;

    /// <summary>All images for <paramref name="file"/> (filmstrip and stream waveforms), for tab memory estimates.</summary>
    public IEnumerable<ImageSource> OfFile(string file) =>
        _film.Values.Concat(_sound.Values).Where(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)).Select(e => e.Picture);

    /// <summary>Number of cached images.</summary>
    public int Count => _film.Count + _sound.Count;

    /// <summary>Stores the filmstrip for <paramref name="file"/>.</summary>
    public void PutFilm(string file, ImageSource picture) => Put(_film, file, file, picture);

    /// <summary>Stores the waveform for <paramref name="file"/> stream <paramref name="stream"/>.</summary>
    public void PutSound(string file, int stream, ImageSource picture) => Put(_sound, SoundKey(file, stream), file, picture);

    /// <summary>Drops images of files no open Sequence uses; returns the file count.</summary>
    public int Release()
    {
        _owner.Check();
        var used = Used();
        var gone = _film.Values.Concat(_sound.Values).Select(e => e.File).Where(f => !used.Contains(f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (gone.Count == 0)
            return 0;

        foreach (var slots in new[] { _film, _sound })
        {
            foreach (var key in slots.Where(p => gone.Contains(p.Value.File)).Select(p => p.Key).ToList())
                slots.Remove(key);
        }

        Changed?.Invoke();
        return gone.Count;
    }

    private void Put(Dictionary<string, Entry> slots, string key, string file, ImageSource picture)
    {
        _owner.Check();
        slots[key] = new Entry(picture, file, ++_stamp);
        if (Count > kept)
            Trim();
        Changed?.Invoke();
    }

    // Over budget: evict least-recently-stored unused-file images until `kept` remain.
    private void Trim()
    {
        var used = Used();
        var unused = _film.Select(p => (Slots: _film, p.Key, p.Value)).Concat(_sound.Select(p => (Slots: _sound, p.Key, p.Value)))
            .Where(e => !used.Contains(e.Value.File)).OrderBy(e => e.Value.Stamp).ToList();
        for (var i = 0; i < unused.Count - kept; i++)
            unused[i].Slots.Remove(unused[i].Key);
    }

    private HashSet<string> Used() => filesInUse().Concat(OtherFilesInUse()).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

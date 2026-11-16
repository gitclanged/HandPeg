using System.Globalization;
using System.Windows.Media;
using HandPegApp.Services;

namespace HandPegApp.ViewModels;

/// <summary>
/// The Timeline blocks' pictures by key: a video file's strip of frames (keyed by the file) and the waveform of one audio stream of a
/// file (<see cref="SoundKey"/>). One picture serves every clip of that file in every open tab; a block reads its picture here when it
/// is drawn and <see cref="Changed"/> tells the Timeline to draw again, so no clip carries a picture and nothing polls for one.
/// </summary>
/// <remarks>
/// The window's thread only (<see cref="ThreadOwner"/>). Pictures of files an open Sequence uses are always kept; of the rest, the
/// <c>kept</c> most recently stored stay, so an undo that brings a clip back finds its pictures. <see cref="Release"/> drops all
/// pictures of unused files at once.
/// </remarks>
public sealed class ClipPictures(int kept, Func<IEnumerable<string>> filesInUse)
{
    private readonly record struct Entry(ImageSource Picture, string File, long Stamp);

    private readonly Dictionary<string, Entry> _film = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Entry> _sound = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThreadOwner _owner = new();
    private long _stamp;

    /// <summary>The files background tabs use; the window sets it (a view model alone has only its own Sequence).</summary>
    public Func<IEnumerable<string>> OtherFilesInUse { get; set; } = static () => [];

    /// <summary>A picture was stored or released: the Timeline draws its blocks again.</summary>
    public event Action? Changed;

    /// <summary>The key of the waveform of audio stream <paramref name="stream"/> of <paramref name="file"/> (the file itself for stream 0).</summary>
    public static string SoundKey(string file, int stream) =>
        stream <= 0 ? file : string.Create(CultureInfo.InvariantCulture, $"{file}|a{stream}");

    /// <summary>The strip of frames of a video file; null while none is made.</summary>
    public ImageSource? Film(string file) => _film.TryGetValue(file, out var entry) ? entry.Picture : null;

    /// <summary>The waveform of a sound key (<see cref="SoundKey"/>); null while none is made.</summary>
    public ImageSource? Sound(string key) => _sound.TryGetValue(key, out var entry) ? entry.Picture : null;

    /// <summary>Every picture made of <paramref name="file"/>: its strip and its streams' waveforms (a tab's memory estimate).</summary>
    public IEnumerable<ImageSource> OfFile(string file) =>
        _film.Values.Concat(_sound.Values).Where(e => string.Equals(e.File, file, StringComparison.OrdinalIgnoreCase)).Select(e => e.Picture);

    /// <summary>How many pictures are held.</summary>
    public int Count => _film.Count + _sound.Count;

    /// <summary>Stores the strip of frames of <paramref name="file"/>.</summary>
    public void PutFilm(string file, ImageSource picture) => Put(_film, file, file, picture);

    /// <summary>Stores the waveform of audio stream <paramref name="stream"/> of <paramref name="file"/>.</summary>
    public void PutSound(string file, int stream, ImageSource picture) => Put(_sound, SoundKey(file, stream), file, picture);

    /// <summary>Drops every picture of a file no open Sequence uses; returns how many files that was.</summary>
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

    // Over the bound: the least recently stored pictures of unused files go until `kept` of them are left.
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

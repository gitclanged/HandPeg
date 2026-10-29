using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// How surely a track's title names a voice, the surest first (<see cref="VoiceTrackScanner.Rank"/>). A title that names
/// "chat" ranks after every other voice word: a chat track is more often the other players' voices than the recorder's own.
/// </summary>
public enum VoiceTitle
{
    /// <summary>Voice words alone, with track numbers and the like: "Mic", "Track 2: Microphone", "Voice", "Host Mic".</summary>
    Only,

    /// <summary>A voice word among other words, none of them naming game sound, music or the desktop: "Shure SM7B Mic".</summary>
    AmongWords,

    /// <summary>"Chat" with nothing but voice words: "Chat", "Voice Chat".</summary>
    OnlyChat,

    /// <summary>"Chat" among other words, none of them disqualifying: "Party Chat".</summary>
    ChatAmongWords,

    /// <summary>Names no voice, or names one beside game sound, music or the desktop ("Game + Mic" is a mix, not a voice).</summary>
    None,
}

/// <summary>
/// Finds the voice among a file's audio tracks by their titles (4.3E). A recording with separate tracks names them (OBS
/// writes each track's name as its title): "Game", "Mic", "Discord". The track named only for a microphone or a voice is
/// the voice; failing that, one whose name has such a word among others, unless one of those others names game sound,
/// music, the desktop or another program's sound. Words are whole words, in any case and with any punctuation, plurals
/// included: "vox" is not found in "voxel", nor "host" in "ghost", and "Mics" is "Mic". Of two titles that rank the same,
/// the first track's wins. Nothing is changed here: the view model marks the track this picks.
/// </summary>
public static class VoiceTrackScanner
{
    /// <summary>Words that name a voice: a microphone, someone speaking.</summary>
    public static IReadOnlySet<string> VoiceWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "mic", "microphone", "voice", "vocal", "vox", "commentary", "dialogue", "dialog", "host", "voiceover", "narration", "narrator",
    };

    /// <summary>"Chat": a voice, but more often the other players' than the recorder's own (it ranks after the rest).</summary>
    public static IReadOnlySet<string> ChatWords { get; } = new HashSet<string>(StringComparer.Ordinal) { "chat" };

    /// <summary>
    /// Words that say a track is not one voice, alone: game sound, music, the desktop, another program's sound, a mix.
    /// A title with one of these is never taken for the voice, whatever voice word it has besides ("Game + Mic").
    /// </summary>
    public static IReadOnlySet<string> DisqualifyingWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "game", "gameplay", "music", "song", "soundtrack", "ost", "bgm", "sfx", "desktop", "system", "discord", "spotify", "browser",
        "media", "alert", "tts", "ambient", "ambience", "background", "instrumental", "karaoke", "backing", "mix", "mixed", "master",
        "loopback", "speaker", "headphone",
    };

    /// <summary>
    /// Words that say nothing of what a track is: the words around a track's number ("Track 2", "Stream #1", "Input 3"),
    /// its channels, and words that only say it is the voice alone ("Mic (Raw)"). Numbers and single letters count as these.
    /// </summary>
    public static IReadOnlySet<string> FillerWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "track", "trk", "audio", "stream", "channel", "ch", "input", "source", "src", "no", "nr", "number",
        "mono", "stereo", "left", "right", "only", "raw", "clean", "iso", "isolated", "dry",
    };

    /// <summary>The track whose title names a voice most surely (the first of equals), as its index; null when none does.</summary>
    /// <param name="tracks">Each track's index (as used in "0:a:N") and its title as the file has it; blank for none.</param>
    public static int? Pick(IEnumerable<(int Index, string Title)> tracks)
    {
        (int Index, VoiceTitle Rank)? best = null;
        foreach (var (index, title) in tracks)
        {
            var rank = Rank(title);
            if (rank != VoiceTitle.None && (best is not { } held || rank < held.Rank || rank == held.Rank && index < held.Index))
                best = (index, rank);
        }

        return best?.Index;
    }

    /// <summary>How surely one title names a voice.</summary>
    public static VoiceTitle Rank(string? title)
    {
        var (voice, chat, other) = (false, false, false);
        foreach (var word in Words(title ?? ""))
        {
            if (IsOneOf(word, VoiceWords))
                voice = true;
            else if (IsOneOf(word, ChatWords))
                chat = true;
            else if (IsOneOf(word, DisqualifyingWords))
                return VoiceTitle.None;
            else if (!IsFiller(word))
                other = true;
        }

        return chat ? (other ? VoiceTitle.ChatAmongWords : VoiceTitle.OnlyChat)
            : voice ? (other ? VoiceTitle.AmongWords : VoiceTitle.Only)
            : VoiceTitle.None;
    }

    /// <summary>
    /// A title's words, in lower case: letters and digits, apart wherever anything else comes between them, where letters
    /// turn to digits or back ("Mic2"), and where a small letter is followed by a capital ("MicTrack").
    /// </summary>
    private static List<string> Words(string title)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var previous = '\0';
        foreach (var c in title)
        {
            var part = char.IsLetterOrDigit(c);
            var turns = word.Length > 0 && (char.IsDigit(c) != char.IsDigit(previous) || char.IsUpper(c) && char.IsLower(previous));
            if (word.Length > 0 && (!part || turns))
            {
                words.Add(word.ToString().ToLowerInvariant());
                word.Clear();
            }

            if (part)
                word.Append(c);
            previous = c;
        }

        if (word.Length > 0)
            words.Add(word.ToString().ToLowerInvariant());
        return words;
    }

    /// <summary>Whether a word is one of a set, or the plural of one: "mics", "voices", "voxes", "commentaries".</summary>
    private static bool IsOneOf(string word, IReadOnlySet<string> set) =>
        set.Contains(word)
        || word.Length > 3 && word.EndsWith('s')
           && (set.Contains(word[..^1])
               || word.EndsWith("es", StringComparison.Ordinal) && set.Contains(word[..^2])
               || word.EndsWith("ies", StringComparison.Ordinal) && set.Contains(word[..^3] + "y"));

    private static bool IsFiller(string word) => word.Length == 1 || word.All(char.IsDigit) || IsOneOf(word, FillerWords);
}

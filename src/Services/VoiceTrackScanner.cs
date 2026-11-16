using System.Text;

namespace HandPegApp.Services;

/// <summary>
/// Voice-title confidence, strongest first (<see cref="VoiceTrackScanner.Rank"/>). "Chat" ranks last among voice words (usually other
/// players, not the recorder).
/// </summary>
public enum VoiceTitle
{
    /// <summary>Only voice words plus neutral words: "Mic", "Track 2: Microphone", "Voice", "Host Mic".</summary>
    Only,

    /// <summary>Voice word among other non-excluding words: "Shure SM7B Mic".</summary>
    AmongWords,

    /// <summary>"Chat" with nothing but voice words: "Chat", "Voice Chat".</summary>
    OnlyChat,

    /// <summary>"Chat" among other words, none of them disqualifying: "Party Chat".</summary>
    ChatAmongWords,

    /// <summary>No voice word, or one alongside game/music/desktop words ("Game + Mic" is a mix).</summary>
    None,
}

/// <summary>
/// Identifies the voice track from audio track titles (e.g. OBS: "Game", "Mic", "Discord"). Prefers titles with only voice words, then
/// voice words among others unless any excluding word (game, music, desktop, other apps) appears. Whole-word, case- and
/// punctuation-insensitive with plurals ("voxel" does not match "vox"; "Mics" matches "Mic"). Ties go to the first track. Pure; the
/// view model applies the result.
/// </summary>
public static class VoiceTrackScanner
{
    /// <summary>Words that name a voice: a microphone, someone speaking.</summary>
    public static IReadOnlySet<string> VoiceWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "mic", "microphone", "voice", "vocal", "vox", "commentary", "dialogue", "dialog", "host", "voiceover", "narration", "narrator",
    };

    /// <summary>"Chat": voice, but usually other players (ranked last).</summary>
    public static IReadOnlySet<string> ChatWords { get; } = new HashSet<string>(StringComparer.Ordinal) { "chat" };

    /// <summary>
    /// Excluding words (game, music, desktop, other apps, mix): titles containing any are never the voice ("Game + Mic").
    /// </summary>
    public static IReadOnlySet<string> DisqualifyingWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "game", "gameplay", "music", "song", "soundtrack", "ost", "bgm", "sfx", "desktop", "system", "discord", "spotify", "browser",
        "media", "alert", "tts", "ambient", "ambience", "background", "instrumental", "karaoke", "backing", "mix", "mixed", "master",
        "loopback", "speaker", "headphone",
    };

    /// <summary>
    /// Neutral words (track numbering, channel layout, qualifiers like "Raw"); numbers and single letters are neutral too.
    /// </summary>
    public static IReadOnlySet<string> FillerWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "track", "trk", "audio", "stream", "channel", "ch", "input", "source", "src", "no", "nr", "number",
        "mono", "stereo", "left", "right", "only", "raw", "clean", "iso", "isolated", "dry",
    };

    /// <summary>Index of the best voice track (first among ties); null when none.</summary>
    /// <param name="tracks">Track index ("0:a:N") and title (blank if none).</param>
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
    /// Lower-case tokens split on non-alphanumerics, letter/digit transitions ("Mic2") and camel case ("MicTrack").
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

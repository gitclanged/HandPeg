using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HandPegApp.Services;

/// <summary>
/// Makes a piece of text fit for a crash report: nothing in it may tell who made it or what they were working on.
/// Paths keep only their file's extension ("&lt;file&gt;.mkv"), addresses become "&lt;url&gt;", e-mail addresses
/// "&lt;email&gt;", the Windows user name and the computer's name "&lt;user&gt;" and "&lt;machine&gt;" wherever they
/// appear (inside other words too), environment expansions "&lt;env&gt;", and quoted strings (a title, a subtitle line,
/// the words of a text layer, a path in quotes) "&lt;text&gt;". What is left is the code's own words and numbers.
///
/// One static function, with no state but the names it strips (the user's, the computer's, and those of the files in
/// use: <see cref="KeepOutFileName"/>), so that it can be tested on its own with names of the test's own (the table in
/// the golden tests). It errs on the side of taking too much: a sentence cut short is a small loss, a name kept is not.
/// </summary>
public static partial class CrashSanitizer
{
    /// <summary>The most a sanitized text keeps, in characters: a message, a breadcrumb or a context value is never an essay.</summary>
    public const int DefaultMaxLength = 2000;

    // The placeholders are kept as private-use characters while the text is worked on, so that a later rule (the user
    // name, say, when it is "file" or "text") can never match inside one; they become "<file>" and so on at the end.
    private const char Open = '\uE000';
    private const char Close = '\uE001';
    private const char Numbers = '\uE010';

    private static readonly string[] Placeholders = ["file", "path", "url", "email", "env", "text", "user", "machine", "removed"];

    /// <summary>Sanitizes <paramref name="text"/> with the names of this computer and its user, and of the files in use.</summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength) => Sanitize(text, CurrentNames(), maxLength);

    /// <summary>
    /// Sanitizes <paramref name="text"/>, stripping <paramref name="names"/> wherever they appear, each with the
    /// placeholder it becomes: "user" and "machine" for the user's and the computer's names, "file" for the name of a
    /// file in use (without its folder or extension). Longest first. For tests, which pass names of their own.
    /// </summary>
    public static string Sanitize(string? text, IReadOnlyList<(string Name, string Placeholder)> names, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // Control characters (a NUL from a fixed-size field, an escape sequence from a tool's output) go first; line
        // breaks and tabs stay, as the rules below read lines.
        var work = ControlCharacters().Replace(text, " ");

        // The two .NET wordings that name a parameter: the name is the code's, and worth keeping.
        work = ParameterName().Replace(work, m => $"(Parameter {Mark("§" + m.Groups["name"].Value)})");

        // A stack frame's " in C:\...\Foo.cs:line 42": the source file's name and the line are the code's; the folder
        // it was built in (a developer's own) goes.
        work = FrameLocation().Replace(work, m => $") in {m.Groups["file"].Value}:line {m.Groups["line"].Value}");

        // Messages that end in what could not be found: everything after the prefix goes, quoted or not.
        work = NotFoundPrefix().Replace(work, m => m.Groups["prefix"].Value + " " + Mark("removed"));

        // A file:// address is a path, and may hold spaces as one does: it goes to the end of its file name.
        work = StripRuns(work, FileUrlStart(), _ => Mark("url"));

        // Addresses of any kind (http(s)://, rtmp://...) and bare www. ones, with their queries.
        work = Url().Replace(work, _ => Mark("url"));
        work = WwwAddress().Replace(work, _ => Mark("url"));
        work = Email().Replace(work, _ => Mark("email"));

        // %USERPROFILE%, %APPDATA%, $env:TEMP: what they stand for is a path on this computer.
        work = EnvironmentExpansion().Replace(work, _ => Mark("env"));

        // An ASS subtitle event: everything after its name is the line's words and timing. An SRT or WebVTT cue: the
        // lines after its timing, to the blank line that ends it, are its words.
        work = AssDialogue().Replace(work, m => m.Groups["head"].Value + " " + Mark("text"));
        work = CueTiming().Replace(work, m => m.Groups["timing"].Value + " " + Mark("text"));

        // A filter option or a metadata field whose value is words from the media (drawtext's text, a title).
        work = TextOption().Replace(work, m => m.Groups["key"].Value + "=" + Mark("text"));

        // FFmpeg writes a quote inside a quoted value as '\'' (close, an escaped quote, open again): taken as part of
        // the value, so that the words after it are not left outside the quotes.
        work = work.Replace(@"'\''", "", StringComparison.Ordinal);

        // Quoted strings: a title, a subtitle line, a text layer's words, a path in quotes. An opening quote with no
        // closing one (a field cut short) runs to the end of the line.
        work = DoubleQuoted().Replace(work, m => Quoted(m.Value[1..].TrimEnd('"')));
        work = TypographicQuoted().Replace(work, m => Quoted(m.Value[1..].TrimEnd('”', '»', '’')));
        work = SingleQuoted().Replace(work, m => m.Groups["before"].Value + Quoted(m.Groups["inside"].Value));

        // Paths: from a drive letter, a UNC start or an environment expansion ("%USERPROFILE%\") to the end of the file
        // name, which keeps its extension; then a relative one (a word with a backslash in it), over spaces the same way.
        work = StripRuns(work, PathStart(), PathPlaceholder);
        work = StripRuns(work, RelativePathStart(), PathPlaceholder);

        // A name with forward slashes that ends in an extension ("Videos/clip.mp4"; not "and/or" or "1920/1080").
        work = SlashedFileToken().Replace(work, m => FileToken(m.Value));

        // The names of the files in use (see KeepOutFileName), wherever they are left: a message that names a file
        // without its folder ("writing: Holiday 2024.mp4") keeps no word of it.
        foreach (var (name, placeholder) in names)
        {
            if (placeholder == "file")
                work = StripName(work, name, placeholder);
        }

        // A file name after a colon, spaces and all ("did not finish writing: Holiday 2024.mp4"), then a bare one of
        // the kinds people keep (a video, a picture, subtitles, a project).
        work = ColonFileName().Replace(work, m => Mark("file") + "." + m.Groups["ext"].Value.ToLowerInvariant());
        work = BareFileName().Replace(work, m => Mark("file") + "." + m.Groups["ext"].Value.ToLowerInvariant());

        // Last, the user's and the computer's names: wherever they are left, also inside other words.
        foreach (var (name, placeholder) in names)
        {
            if (placeholder != "file")
                work = StripName(work, name, placeholder);
        }

        // The placeholders are written out (and an FFmpeg quote that was not inside a quoted string is a quote again),
        // and whitespace runs are folded so a cut leaves no ragged gaps.
        var result = Unmark(work).Replace("", @"'\''", StringComparison.Ordinal);
        result = SpaceRuns().Replace(result, " ").Trim();
        return result.Length <= maxLength ? result : result[..maxLength] + "...";
    }

    // ----- The names of this computer and its user -----

    // Read once: the user name, the name of the profile folder (which can differ from it), the computer's name and the
    // domain the user signed in to (the computer's own name, or a work domain).
    private static readonly Lazy<IReadOnlyList<(string Name, string Placeholder)>> LocalNames = new(() =>
    {
        var names = new List<(string, string)>();
        void Add(string? name, string placeholder)
        {
            name = name?.Trim();
            if (!string.IsNullOrEmpty(name) && !names.Exists(n => string.Equals(n.Item1, name, StringComparison.OrdinalIgnoreCase)))
                names.Add((name, placeholder));
        }

        try
        {
            Add(Environment.UserName, "user");
            Add(System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))), "user");
            Add(Environment.MachineName, "machine");

            // The full host name: the one above is cut to 15 characters, which would leave the rest of a longer one. And
            // with its domain, for a computer in one ("studio-pc.corp.example.com"); the domain alone is not taken, as a
            // short one ("lan", "home") would be taken out of every word that holds it.
            Add(DnsName(ComputerNameDnsHostname), "machine");
            Add(DnsName(ComputerNameDnsFullyQualified), "machine");
            Add(Environment.UserDomainName, "machine");
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or ArgumentException)
        {
            // Whatever could be read is used.
        }

        // Longest first, so that a name inside another is not taken out of it first and leaves the rest behind.
        names.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return names;
    });

    // ----- The names of the files in use -----
    //
    // A path is taken out wherever it is written whole; a file's name written on its own ("writing: Holiday 2024.mp4")
    // cannot be told from other words, except by knowing it. So HandPeg tells this class the names of the files it has
    // open (CrashReporter.KeepOut: the main video, the layers', the project's, the output's), without their folders or
    // extensions, and they are taken out like the user's name. A ring of the newest few, in memory only (the sentinel
    // has the same names from the shared memory): never written anywhere.

    /// <summary>How many file names are kept: the newest this many.</summary>
    public const int KeptFileNames = CrashSharedMemory.FileNameCount;

    /// <summary>A name shorter than this identifies nothing ("01", "a") and would only blur the report: it is not kept.</summary>
    public const int ShortestFileName = 3;

    private static readonly string?[] FileNames = new string?[KeptFileNames];
    private static int _fileNamesKept;

    /// <summary>
    /// Keeps a file's name (without its folder or extension) out of every text from now on. Returns the slot it went
    /// into (so the caller can copy it into the shared memory), or -1 when it was kept already or is too short.
    /// Lock-free; allocates nothing.
    /// </summary>
    public static int KeepOutFileName(string name)
    {
        name = name.Trim();
        if (name.Length < ShortestFileName)
            return -1;

        for (var held = 0; held < KeptFileNames; held++)
        {
            if (string.Equals(Volatile.Read(ref FileNames[held]), name, StringComparison.OrdinalIgnoreCase))
                return -1;
        }

        var slot = (int)((uint)(Interlocked.Increment(ref _fileNamesKept) - 1) % KeptFileNames);
        Volatile.Write(ref FileNames[slot], name);
        return slot;
    }

    /// <summary>The file name in a slot, or null (for copying them all into the shared memory when it is made).</summary>
    public static string? FileNameAt(int slot) => slot >= 0 && slot < KeptFileNames ? Volatile.Read(ref FileNames[slot]) : null;

    /// <summary>This computer's and its user's names and the names of the files in use, longest first.</summary>
    private static List<(string Name, string Placeholder)> CurrentNames()
    {
        var names = new List<(string Name, string Placeholder)>(LocalNames.Value);
        for (var slot = 0; slot < KeptFileNames; slot++)
        {
            if (Volatile.Read(ref FileNames[slot]) is { } name)
                names.Add((name, "file"));
        }

        names.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length));
        return names;
    }

    private const int ComputerNameDnsHostname = 1;
    private const int ComputerNameDnsFullyQualified = 3;

    /// <summary>One of the computer's DNS names, or null when Windows will not say.</summary>
    private static unsafe string? DnsName(int nameType)
    {
        var buffer = stackalloc char[256];
        uint size = 256;
        return GetComputerNameExW(nameType, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetComputerNameExW(int nameType, char* buffer, ref uint size);

    /// <summary>
    /// Takes a name out wherever it is: inside other words too when it is long enough not to be an everyday syllable
    /// (three letters or more); a shorter one only where it stands as a word of its own. The one place it stays is
    /// inside a name of the code's own (Microsoft.Win32.SafeHandles, System.IO, HandPegApp.Services...): a user called
    /// "Sys" did not put "System" into a message, and taking it out would only blur the report.
    /// </summary>
    private static string StripName(string text, string name, string placeholder)
    {
        if (name.Length == 0)
            return text;

        var pattern = name.Length >= 3 ? Regex.Escape(name) : $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
        return Regex.Replace(text, pattern, m => IsInCodeName(text, m.Index, m.Length) ? m.Value : Mark(placeholder),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Whether the text at <paramref name="index"/> is part of a dotted name from one of the code's own namespaces.</summary>
    private static bool IsInCodeName(string text, int index, int length)
    {
        static bool InName(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '`';

        var start = index;
        while (start > 0 && InName(text[start - 1]))
            start--;
        var end = index + length;
        while (end < text.Length && InName(text[end]))
            end++;

        return CodeName().IsMatch(text.AsSpan(start, end - start).TrimEnd('.'));
    }

    // ----- Paths -----

    /// <summary>
    /// Every path that begins where <paramref name="starts"/> matches (a drive letter, a UNC start, the long-path
    /// prefix, an environment expansion and a separator, a relative path's first word, a file:// address): it runs over
    /// spaces to the end of its file name, which is the first "name.ext" after its last separator that is followed by
    /// the end, a space or punctuation. With no such name, the whole run goes. <paramref name="replace"/> is given the
    /// extension (null when there is none) and says what the path becomes.
    /// </summary>
    private static string StripRuns(string text, Regex starts, Func<string?, string> replace)
    {
        StringBuilder? builder = null;
        var at = 0;
        foreach (Match start in starts.Matches(text))
        {
            if (start.Index < at)
                continue;

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, at, start.Index - at);

            // The run: as far as a character no path can hold (a quote, a colon after the drive, a line break...).
            var end = start.Index + start.Length;
            while (end < text.Length && !EndsPathRun(text[end]))
                end++;

            var run = text[start.Index..end];
            var (length, extension) = FileNameEnd(run);
            builder.Append(replace(extension));
            at = start.Index + Math.Max(length, start.Length);
        }

        return builder is null ? text : builder.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>What a path becomes: "&lt;file&gt;.ext" when it ends in a file name with an extension, "&lt;path&gt;" otherwise.</summary>
    private static string PathPlaceholder(string? extension) => extension is null ? Mark("path") : Mark("file") + "." + extension;

    private static bool EndsPathRun(char c) => c is '"' or '\'' or ':' or '\r' or '\n' or '\t' or '<' or '>' or '|' or '*' or '?'
        or '\u201C' or '\u201D' or '\u2018' or '\u2019' or Open or Close or '\uE002';

    /// <summary>
    /// Where the file name in a run ends (the run's length when it has none), and its extension (null when none). The
    /// name is looked for after the last separator, so a folder with a dot in its name does not end the path early.
    /// </summary>
    private static (int Length, string? Extension) FileNameEnd(string run)
    {
        var lastSeparator = run.LastIndexOfAny(['\\', '/']);
        var match = ExtensionAtEnd().Match(run, lastSeparator + 1);
        return match.Success
            ? (match.Index + match.Length, match.Groups["ext"].Value.ToLowerInvariant())
            : (run.Length, null);
    }

    /// <summary>A token that is a path without a drive: "&lt;file&gt;.ext" when it ends in an extension, "&lt;path&gt;" otherwise.</summary>
    private static string FileToken(string token)
    {
        // Placeholders already in the token (a name taken out of it) stay out of the extension test.
        var (length, extension) = FileNameEnd(token);
        return (extension is null ? Mark("path") : Mark("file") + "." + extension) + token[length..];
    }

    /// <summary>
    /// What a quoted string becomes: a path in quotes keeps its extension ("&lt;file&gt;.mkv"), a token of the code's
    /// (lower case, with a digit or an underscore: 'libx265', 'h264_nvenc') stays as it is, and anything else, which could
    /// be words from the media, is "&lt;text&gt;".
    /// </summary>
    private static string Quoted(string inside)
    {
        if (PathStart().Match(inside) is { Success: true, Index: 0 })
        {
            var (_, extension) = FileNameEnd(inside);
            return extension is null ? Mark("path") : Mark("file") + "." + extension;
        }

        // A library or a program by its file name alone ('libmpv-2.dll', 'ffmpeg.exe') is the code's; a path to one went above.
        return CodeToken().IsMatch(inside) || ModuleFileName().IsMatch(inside) ? "'" + inside + "'" : Mark("text");
    }

    // ----- Placeholders -----

    private static string Mark(string placeholder)
    {
        // A kept parameter name travels as "§name" between the markers: it is put back as it was.
        if (placeholder.StartsWith('§'))
            return Open + placeholder + Close;

        // The placeholder's number as a private-use character too: no name, however short, can match inside it.
        var index = Array.IndexOf(Placeholders, placeholder);
        return string.Concat(Open, ((char)(Numbers + index)).ToString(), Close);
    }

    private static string Unmark(string text) => MarkedPlaceholder().Replace(text, m =>
    {
        var inside = m.Groups[1].Value;
        if (inside.StartsWith('§'))
            return "'" + inside[1..] + "'";
        var index = inside.Length == 1 ? inside[0] - Numbers : -1;
        return index >= 0 && index < Placeholders.Length ? "<" + Placeholders[index] + ">" : "";
    });

    // ----- The patterns -----

    // Every C0 control but tab, line feed and carriage return, and DEL.
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharacters();

    // "(Parameter 'path')", as ArgumentException writes it: an identifier, the code's own name for something.
    [GeneratedRegex(@"\(Parameter '(?<name>[A-Za-z_][A-Za-z0-9_]{0,60})'\)")]
    private static partial Regex ParameterName();

    // The wordings that are followed by a path or a name: what comes after them, to the end of the line, goes.
    // ("Unable to find an entry point named 'x' in DLL '...'" is not one of them: the entry point is the code's, and the
    // DLL's path is taken out as any quoted path is.)
    [GeneratedRegex(@"(?<prefix>Could not find (?:file|a part of the path)|File not found:|Source not found:|Not an https address:|Could not load file or assembly|Unable to find(?! an entry point)|No such file or directory:?)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex NotFoundPrefix();

    // A frame's location, after the closing parenthesis of its parameters: the folders (any, or none) and a source file.
    [GeneratedRegex(@"\) in (?:[^\r\n]*?[\\/])?(?<file>[\p{L}\p{N}_.\-]{1,80}\.(?:cs|xaml|vb|fs)):line (?<line>\d{1,7})")]
    private static partial Regex FrameLocation();

    // An SRT or WebVTT cue's timing line, the rest of that line, and the lines after it up to a blank one.
    [GeneratedRegex(@"(?<timing>\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3})[^\r\n]*(?:\r?\n(?![ \t]*(?:\r?\n|$))[^\r\n]*)*")]
    private static partial Regex CueTiming();

    // A dotted name from the code's own namespaces (or one of those words alone).
    [GeneratedRegex(@"^(?:Microsoft|System|MS|Windows|HandPegApp)(?:\.[\p{L}_][\p{L}\p{N}_`]*)*$")]
    private static partial Regex CodeName();

    // A library's or a program's file name, without a folder.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,60}\.(?:dll|exe|sys|drv)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleFileName();

    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]{1,15}://[^\s""'<>\u201C\u201D]*")]
    private static partial Regex Url();

    [GeneratedRegex(@"\bwww\.[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex WwwAddress();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"%[A-Za-z_][A-Za-z0-9_()]*%|\$env:[A-Za-z_][A-Za-z0-9_]*", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentExpansion();

    [GeneratedRegex(@"(?<head>Dialogue:)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex AssDialogue();

    // drawtext's text= and textfile=, metadata fields, a file named in an option: the value runs to the option's end
    // (an unquoted value may hold spaces: "text=Happy Birthday:x=10").
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<key>text|textfile|title|comment|artist|album|album_artist|author|composer|description|lyrics|show|episode_id|filename|file|fontfile|language)=(?![\uE000'""\u201C\u2018])(?:\\.|[^:,;\]\r\n\\'""])+", RegexOptions.IgnoreCase)]
    private static partial Regex TextOption();

    [GeneratedRegex(@"""[^""\r\n]*(?:""|$)", RegexOptions.Multiline)]
    private static partial Regex DoubleQuoted();

    [GeneratedRegex(@"[\u201C\u00AB\u2018][^\u201D\u00BB\u2019\r\n]*(?:[\u201D\u00BB\u2019]|$)", RegexOptions.Multiline)]
    private static partial Regex TypographicQuoted();

    // A single quote opens a quoted string only where a word does not run into it (so "can't" and "the user's" are
    // left alone), and closes one only where no word follows it.
    [GeneratedRegex(@"(?<before>^|[\s=(\[{:,;])'(?<inside>[^\r\n]*?)(?:'(?![\p{L}\p{N}])|$)", RegexOptions.Multiline)]
    private static partial Regex SingleQuoted();

    // Where a path starts: a drive letter and a separator (not inside a word, so "Foo:\" is not one), a UNC start, the
    // long-path prefix, or an environment expansion already marked (EnvMark) with a separator after it.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\\\\[?.]\\(?:[A-Za-z]:\\|UNC\\)?|[A-Za-z]:[\\/]|\\\\[^\s\\/]|//[A-Za-z0-9][^\s/]*/)|" + EnvMark + @"[\\/]")]
    private static partial Regex PathStart();

    // "<env>" as it is marked while the text is worked on (Mark("env"): the open marker, Numbers + its index in
    // Placeholders, the close marker), for PathStart. Keep it in step with Placeholders.
    private const string EnvMark = "";

    // Where a relative path starts: the beginning of a word that has a backslash in it before any character a path
    // cannot hold ("Videos\clip one.mp4", "subs\episode 01.srt").
    [GeneratedRegex(@"(?<![^\s""'<>(\[{=,;:])(?=[^\s""'<>|*?:]*\\)")]
    private static partial Regex RelativePathStart();

    // A file:// address, with its drive when it has one (whose colon would otherwise end the run).
    [GeneratedRegex(@"\bfile:(?:/{1,3}|\\\\)(?:[A-Za-z]:)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileUrlStart();

    // A file name's extension where it ends: one to six letters or digits, then the end, a space, closing punctuation,
    // or a full stop or comma at the end of a sentence.
    [GeneratedRegex(@"\.(?<ext>[A-Za-z0-9]{1,6})(?=$|[\s)\]},;!]|[.,](?:\s|$))")]
    private static partial Regex ExtensionAtEnd();

    [GeneratedRegex(@"(?<![\p{L}\p{N}/])[^\s/""'<>\uE000\uE001]+(?:/[^\s/""'<>\uE000\uE001]+)+\.[A-Za-z][A-Za-z0-9]{0,5}(?![\p{L}\p{N}])")]
    private static partial Regex SlashedFileToken();

    // The extensions of the files people keep: videos, sound, pictures, subtitles, documents, projects.
    private const string KeptExtensions = "mkv|mp4|m4v|mov|avi|wmv|flv|webm|ts|mts|m2ts|mpg|mpeg|vob|ogv|3gp|mxf|gif|mp3|m4a|aac|wav|flac|ogg|opus|wma|ac3|eac3|png|jpg|jpeg|bmp|tif|tiff|webp|heic|srt|ass|ssa|vtt|sub|sup|idx|lrc|txt|hproj|hpstyle|hpfx|hpcrash|cube|csv|pdf|docx?|xlsx?|zip|7z|rar";

    // A name with one of those extensions, without spaces (and not the rest of one already taken out: "<file>_HandPeg.mp4").
    [GeneratedRegex(@"(?<![\p{L}\p{N}_\-.])[\p{L}\p{N}_\-.()\[\]+&#!~,]+\.(?<ext>" + KeptExtensions + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex BareFileName();

    // A name with one of those extensions right after a colon and a space, spaces and all: what a message names last.
    [GeneratedRegex(@"(?<=:[ \t]{1,4})[^\r\n:""'<>\uE000\uE001]{1,160}?\.(?<ext>" + KeptExtensions + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex ColonFileName();

    // A token of the code's in quotes: lower-case letters, digits and underscores, with at least one digit or underscore.
    [GeneratedRegex(@"^(?=[a-z0-9_]*[0-9_])[a-z][a-z0-9_]{1,40}$")]
    private static partial Regex CodeToken();

    [GeneratedRegex("\uE000([^\uE000\uE001]*)\uE001")]
    private static partial Regex MarkedPlaceholder();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex SpaceRuns();
}

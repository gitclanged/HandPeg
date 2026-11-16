using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace HandPegApp.Services;

/// <summary>
/// Scrubs PII from crash report text: paths keep only the extension ("&lt;file&gt;.mkv"), URLs become "&lt;url&gt;", e-mails
/// "&lt;email&gt;", Windows user and machine names "&lt;user&gt;"/"&lt;machine&gt;" (also inside words), environment expansions
/// "&lt;env&gt;", and quoted strings (titles, subtitle lines, text layers, quoted paths) "&lt;text&gt;". Only code identifiers and numbers remain.
///
/// Stateless apart from the stripped names (user, machine, open files via <see cref="KeepOutFileName"/>), so goldens can test it with
/// their own names. Errs toward over-redaction.
/// </summary>
public static partial class CrashSanitizer
{
    /// <summary>Maximum sanitized length in characters.</summary>
    public const int DefaultMaxLength = 2000;

    // Placeholders are private-use markers during processing so later rules (e.g. a user named "file") cannot match inside them;
    // expanded to "<file>" etc. at the end.
    private const char Open = '\uE000';
    private const char Close = '\uE001';
    private const char Numbers = '\uE010';

    private static readonly string[] Placeholders = ["file", "path", "url", "email", "env", "text", "user", "machine", "removed"];

    /// <summary>Sanitizes <paramref name="text"/> using this machine's user, machine and open-file names.</summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength) => Sanitize(text, CurrentNames(), maxLength);

    /// <summary>
    /// Sanitizes <paramref name="text"/>, replacing each of <paramref name="names"/> with its placeholder ("user", "machine", or "file" for
    /// an open file's base name), longest first. Test entry point.
    /// </summary>
    public static string Sanitize(string? text, IReadOnlyList<(string Name, string Placeholder)> names, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        // Strip control characters first (NULs, escape sequences), keeping line breaks and tabs for the line-based rules.
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

        // ASS events: redact everything after the event name. SRT/WebVTT cues: redact lines after the timing up to the blank line.
        work = AssDialogue().Replace(work, m => m.Groups["head"].Value + " " + Mark("text"));
        work = CueTiming().Replace(work, m => m.Groups["timing"].Value + " " + Mark("text"));

        // A filter option or a metadata field whose value is words from the media (drawtext's text, a title).
        work = TextOption().Replace(work, m => m.Groups["key"].Value + "=" + Mark("text"));

        // FFmpeg writes a quote inside a quoted value as '\'' (close, an escaped quote, open again): taken as part of
        // the value, so that the words after it are not left outside the quotes.
        work = work.Replace(@"'\''", "", StringComparison.Ordinal);

        // Quoted strings (titles, subtitle lines, text layers, quoted paths); an unterminated quote runs to end of line.
        work = DoubleQuoted().Replace(work, m => Quoted(m.Value[1..].TrimEnd('"')));
        work = TypographicQuoted().Replace(work, m => Quoted(m.Value[1..].TrimEnd('”', '»', '’')));
        work = SingleQuoted().Replace(work, m => m.Groups["before"].Value + Quoted(m.Groups["inside"].Value));

        // Paths: from a drive letter, a UNC start or an environment expansion ("%USERPROFILE%\") to the end of the file
        // name, which keeps its extension; then a relative one (a word with a backslash in it), over spaces the same way.
        work = StripRuns(work, PathStart(), PathPlaceholder);
        work = StripRuns(work, RelativePathStart(), PathPlaceholder);

        // A name with forward slashes that ends in an extension ("Videos/clip.mp4"; not "and/or" or "1920/1080").
        work = SlashedFileToken().Replace(work, m => FileToken(m.Value));

        // Open-file base names (KeepOutFileName) anywhere, covering folder-less mentions ("writing: Holiday 2024.mp4").
        foreach (var (name, placeholder) in names)
        {
            if (placeholder == "file")
                work = StripName(work, name, placeholder);
        }

        // File names after a colon (spaces included), then bare media/project file names.
        work = ColonFileName().Replace(work, m => Mark("file") + "." + m.Groups["ext"].Value.ToLowerInvariant());
        work = BareFileName().Replace(work, m => Mark("file") + "." + m.Groups["ext"].Value.ToLowerInvariant());

        // Finally, user and machine names anywhere, including inside other words.
        foreach (var (name, placeholder) in names)
        {
            if (placeholder != "file")
                work = StripName(work, name, placeholder);
        }

        // Expand placeholders, restore stray FFmpeg quote markers, and collapse whitespace runs.
        var result = Unmark(work).Replace("", @"'\''", StringComparison.Ordinal);
        result = SpaceRuns().Replace(result, " ").Trim();
        return result.Length <= maxLength ? result : result[..maxLength] + "...";
    }

    // ----- The names of this computer and its user -----

    // Read once: user name, profile folder name (may differ), machine name, and logon domain (machine name or work domain).
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

            // Full DNS host name (NetBIOS name is truncated to 15 chars) and FQDN; the bare domain is not stripped since short domains
            // ("lan", "home") would corrupt unrelated words.
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

    // ----- Open file names -----
    //
    // Full paths are detectable, but bare file names are not; callers register open files (CrashReporter.KeepOut: sources, project,
    // output) by base name and they are stripped like the user name. In-memory ring of recent names (the sentinel mirrors them via
    // shared memory); never persisted.

    /// <summary>How many file names are kept: the newest this many.</summary>
    public const int KeptFileNames = CrashSharedMemory.FileNameCount;

    /// <summary>Minimum registered name length; shorter names ("01", "a") are ignored.</summary>
    public const int ShortestFileName = 3;

    private static readonly string?[] FileNames = new string?[KeptFileNames];
    private static int _fileNamesKept;

    /// <summary>
    /// Registers a file base name for redaction. Returns its ring slot (for mirroring to shared memory) or -1 if duplicate or too short.
    /// Lock-free and allocation-free.
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
    /// Redacts a name: as a substring when 3+ letters, else whole-word only. Occurrences inside known code namespaces
    /// (Microsoft.Win32, System.IO, HandPegApp.Services, ...) are kept.
    /// </summary>
    private static string StripName(string text, string name, string placeholder)
    {
        if (name.Length == 0)
            return text;

        var pattern = name.Length >= 3 ? Regex.Escape(name) : $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])";
        return Regex.Replace(text, pattern, m => IsInCodeName(text, m.Index, m.Length) ? m.Value : Mark(placeholder),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>True when <paramref name="index"/> lies inside a dotted identifier from a known code namespace.</summary>
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
    /// Replaces every path starting at a <paramref name="starts"/> match (drive, UNC, long-path prefix, env expansion, relative word,
    /// file:// URL), extending across spaces to the first "name.ext" after the last separator followed by end, space or punctuation (else
    /// the whole run). <paramref name="replace"/> maps the extension (null if none) to the replacement.
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

    /// <summary>Path replacement: "&lt;file&gt;.ext" with an extension, else "&lt;path&gt;".</summary>
    private static string PathPlaceholder(string? extension) => extension is null ? Mark("path") : Mark("file") + "." + extension;

    private static bool EndsPathRun(char c) => c is '"' or '\'' or ':' or '\r' or '\n' or '\t' or '<' or '>' or '|' or '*' or '?'
        or '\u201C' or '\u201D' or '\u2018' or '\u2019' or Open or Close or '\uE002';

    /// <summary>
    /// End index of the file name in a run (run length if none) and its extension (null if none), searched after the last separator so
    /// dotted folder names do not end the path early.
    /// </summary>
    private static (int Length, string? Extension) FileNameEnd(string run)
    {
        var lastSeparator = run.LastIndexOfAny(['\\', '/']);
        var match = ExtensionAtEnd().Match(run, lastSeparator + 1);
        return match.Success
            ? (match.Index + match.Length, match.Groups["ext"].Value.ToLowerInvariant())
            : (run.Length, null);
    }

    /// <summary>Drive-less path token replacement: "&lt;file&gt;.ext" or "&lt;path&gt;".</summary>
    private static string FileToken(string token)
    {
        // Placeholders already in the token (a name taken out of it) stay out of the extension test.
        var (length, extension) = FileNameEnd(token);
        return (extension is null ? Mark("path") : Mark("file") + "." + extension) + token[length..];
    }

    /// <summary>
    /// Quoted-string replacement: quoted paths become "&lt;file&gt;.ext"; code tokens (lower case with a digit or underscore: 'libx265',
    /// 'h264_nvenc') are kept; anything else becomes "&lt;text&gt;".
    /// </summary>
    private static string Quoted(string inside)
    {
        if (PathStart().Match(inside) is { Success: true, Index: 0 })
        {
            var (_, extension) = FileNameEnd(inside);
            return extension is null ? Mark("path") : Mark("file") + "." + extension;
        }

        // A library or a program by its file name alone ('d3d11.dll', 'ffmpeg.exe') is the code's; a path to one went above.
        return CodeToken().IsMatch(inside) || ModuleFileName().IsMatch(inside) ? "'" + inside + "'" : Mark("text");
    }

    // ----- Placeholders -----

    private static string Mark(string placeholder)
    {
        // Kept parameter names are carried between markers and restored verbatim.
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

    // Prefixes followed by a path or name; the rest of the line is redacted. (Entry-point-not-found messages are excluded: the entry
    // point is code, and the DLL path is handled as a quoted path.)
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

    // Option values (drawtext text=/textfile=, metadata, file options) run to the option end; unquoted values may contain spaces.
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<key>text|textfile|title|comment|artist|album|album_artist|author|composer|description|lyrics|show|episode_id|filename|file|fontfile|language)=(?![\uE000'""\u201C\u2018])(?:\\.|[^:,;\]\r\n\\'""])+", RegexOptions.IgnoreCase)]
    private static partial Regex TextOption();

    [GeneratedRegex(@"""[^""\r\n]*(?:""|$)", RegexOptions.Multiline)]
    private static partial Regex DoubleQuoted();

    [GeneratedRegex(@"[\u201C\u00AB\u2018][^\u201D\u00BB\u2019\r\n]*(?:[\u201D\u00BB\u2019]|$)", RegexOptions.Multiline)]
    private static partial Regex TypographicQuoted();

    // A single quote opens only when not preceded by a word character (so "can't" and "it's" are untouched) and closes only when not
    // followed by one.
    [GeneratedRegex(@"(?<before>^|[\s=(\[{:,;])'(?<inside>[^\r\n]*?)(?:'(?![\p{L}\p{N}])|$)", RegexOptions.Multiline)]
    private static partial Regex SingleQuoted();

    // Where a path starts: a drive letter and a separator (not inside a word, so "Foo:\" is not one), a UNC start, the
    // long-path prefix, or an environment expansion already marked (EnvMark) with a separator after it.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:\\\\[?.]\\(?:[A-Za-z]:\\|UNC\\)?|[A-Za-z]:[\\/]|\\\\[^\s\\/]|//[A-Za-z0-9][^\s/]*/)|" + EnvMark + @"[\\/]")]
    private static partial Regex PathStart();

    // Marked form of "<env>" (Mark("env"): open marker, Numbers + index in Placeholders, close marker) used by PathStart; keep in sync
    // with Placeholders.
    private const string EnvMark = "";

    // Where a relative path starts: the beginning of a word that has a backslash in it before any character a path
    // cannot hold ("Videos\clip one.mp4", "subs\episode 01.srt").
    [GeneratedRegex(@"(?<![^\s""'<>(\[{=,;:])(?=[^\s""'<>|*?:]*\\)")]
    private static partial Regex RelativePathStart();

    // A file:// address, with its drive when it has one (whose colon would otherwise end the run).
    [GeneratedRegex(@"\bfile:(?:/{1,3}|\\\\)(?:[A-Za-z]:)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileUrlStart();

    // Extension: 1-6 alphanumerics followed by end, space, closing punctuation, or sentence-ending period/comma.
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

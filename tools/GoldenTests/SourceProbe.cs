using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace HandPegGoldenTests;

/// <summary>
/// Reads HandPeg's own source for the goldens that check source text, in one place: line endings normalized to "\n",
/// a member found by its signature and cut at its true end (the matching brace of a block body, or the semicolon that
/// ends an expression body), a missing member reported by <see cref="MissingMemberException"/> so a negative check can
/// never pass on an empty span, and token probes that skip comments in the C# files read here.
/// </summary>
public static partial class SourceProbe
{
    private static readonly Lazy<string> RootFolder = new(FindRoot);

    // Comment ranges of the C# texts read here (and of the members cut from them), by string identity: a probe skips
    // a token that only appears in a comment. Text read by other means (and XAML) is probed as plain text.
    private static readonly ConditionalWeakTable<string, int[]> CommentRanges = new();

    /// <summary>The repository root (the folder holding HandPegApp.csproj).</summary>
    public static string Root => RootFolder.Value;

    /// <summary>A file under the repository root, line endings normalized to "\n".</summary>
    public static string Repo(params string[] parts)
    {
        var path = Path.Combine([Root, .. parts]);
        var text = File.ReadAllText(path).ReplaceLineEndings("\n");
        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            CommentRanges.AddOrUpdate(text, Comments(text));
        return text;
    }

    /// <summary>A file under src\, line endings normalized to "\n".</summary>
    public static string Src(params string[] parts) => Repo(["src", .. parts]);

    /// <summary>
    /// A member's text, from <paramref name="signature"/> (its first occurrence outside a comment) to the end of the
    /// member. Throws <see cref="MissingMemberException"/> when the signature is not in <paramref name="source"/>.
    /// </summary>
    public static string Member(string source, string signature)
    {
        var start = IndexOf(source, signature);
        if (start < 0)
            throw new MissingMemberException($"'{signature}' is not in the source probed (renamed, moved to another file, or removed)");
        var end = MemberEnd(source, start);
        var member = source[start..end];
        if (CommentRanges.TryGetValue(source, out var ranges))
            CommentRanges.AddOrUpdate(member, Slice(ranges, start, end));
        return member;
    }

    /// <summary>
    /// A member of the type named <paramref name="typeName"/>, searched across every file under src\ that declares the type
    /// (all its partial files), so a member moved between partial files is still found.
    /// </summary>
    public static string MemberOfType(string typeName, string signature)
    {
        var declaration = new Regex(@"\b(class|struct|record|interface)\s+" + Regex.Escape(typeName) + @"\b");
        foreach (var path in TypeFiles(typeName))
        {
            var text = Repo(Path.GetRelativePath(Root, path));
            if (declaration.IsMatch(text) && IndexOf(text, signature) >= 0)
                return Member(text, signature);
        }

        throw new MissingMemberException($"'{signature}' is in none of the files that declare {typeName}");
    }

    /// <summary>The index of <paramref name="token"/> in <paramref name="text"/> from <paramref name="from"/>, skipping occurrences inside comments of C# text read here.</summary>
    public static int IndexOf(string text, string token, int from = 0)
    {
        CommentRanges.TryGetValue(text, out var ranges);
        for (var at = text.IndexOf(token, from, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            if (ranges is null || !InRanges(ranges, at))
                return at;
        }

        return -1;
    }

    /// <summary>Whether every one of <paramref name="parts"/> is in <paramref name="text"/>, each after the one before (comments skipped).</summary>
    public static bool InOrder(string text, params string[] parts)
    {
        var at = 0;
        foreach (var part in parts)
        {
            var found = IndexOf(text, part, at);
            if (found < 0)
                return false;
            at = found + part.Length;
        }

        return true;
    }

    // ----- Scanning -----

    private static IEnumerable<string> TypeFiles(string typeName)
    {
        var src = Path.Combine(Root, "src");
        // The usual file names first (Type.cs, Type.*.cs, Type.xaml.cs), then every other file.
        var named = Directory.EnumerateFiles(src, typeName + "*.cs", SearchOption.AllDirectories).ToList();
        return named.Concat(Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Except(named, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The end (exclusive) of the member whose signature starts at <paramref name="start"/>.</summary>
    private static int MemberEnd(string s, int start)
    {
        var depth = 0;
        for (var i = start; i < s.Length;)
        {
            var skipped = SkipLiteralOrComment(s, i);
            if (skipped > i)
            {
                i = skipped;
                continue;
            }

            var c = s[i];
            if (c is '(' or '[')
            {
                depth++;
            }
            else if (c is ')' or ']')
            {
                depth--;
            }
            else if (depth == 0 && c == ';')
            {
                return i + 1; // a declaration with no body
            }
            else if (depth == 0 && c == '=' && i + 1 < s.Length && s[i + 1] == '>')
            {
                return ExpressionEnd(s, i + 2);
            }
            else if (depth == 0 && c == '{')
            {
                return BlockEnd(s, i);
            }

            i++;
        }

        return s.Length;
    }

    // The ';' at depth 0 that ends an expression body (lambdas and initializers inside it are nested).
    private static int ExpressionEnd(string s, int from)
    {
        var depth = 0;
        for (var i = from; i < s.Length;)
        {
            var skipped = SkipLiteralOrComment(s, i);
            if (skipped > i)
            {
                i = skipped;
                continue;
            }

            var c = s[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ';' && depth == 0)
                return i + 1;
            i++;
        }

        return s.Length;
    }

    // The '}' matching the '{' at open.
    private static int BlockEnd(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var skipped = SkipLiteralOrComment(s, i);
            if (skipped > i)
            {
                i = skipped;
                continue;
            }

            if (s[i] == '{')
            {
                depth++;
            }
            else if (s[i] == '}' && --depth == 0)
            {
                return i + 1;
            }

            i++;
        }

        return s.Length;
    }

    /// <summary>Sorted [start, end) pairs of every comment in C# text.</summary>
    private static int[] Comments(string s)
    {
        var ranges = new List<int>();
        for (var i = 0; i < s.Length;)
        {
            var skipped = SkipLiteralOrComment(s, i);
            if (skipped > i)
            {
                if (s[i] == '/')
                    ranges.AddRange([i, skipped]);
                i = skipped;
                continue;
            }

            i++;
        }

        return [.. ranges];
    }

    private static int[] Slice(int[] ranges, int start, int end)
    {
        var sliced = new List<int>();
        for (var r = 0; r < ranges.Length; r += 2)
        {
            var (a, b) = (Math.Max(ranges[r], start), Math.Min(ranges[r + 1], end));
            if (a < b)
                sliced.AddRange([a - start, b - start]);
        }

        return [.. sliced];
    }

    private static bool InRanges(int[] ranges, int at)
    {
        var (lo, hi) = (0, ranges.Length / 2 - 1);
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (at < ranges[mid * 2])
                hi = mid - 1;
            else if (at >= ranges[mid * 2 + 1])
                lo = mid + 1;
            else
                return true;
        }

        return false;
    }

    /// <summary>
    /// The index just past the comment, string or char literal that starts at <paramref name="i"/> (regular, verbatim,
    /// interpolated and raw strings, with interpolation holes scanned as code), or <paramref name="i"/> when none starts there.
    /// </summary>
    private static int SkipLiteralOrComment(string s, int i)
    {
        var c = s[i];
        if (c == '/' && i + 1 < s.Length)
        {
            if (s[i + 1] == '/')
            {
                var newline = s.IndexOf('\n', i);
                return newline < 0 ? s.Length : newline;
            }

            if (s[i + 1] == '*')
            {
                var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                return close < 0 ? s.Length : close + 2;
            }

            return i;
        }

        if (c == '\'')
        {
            for (var k = i + 1; k < s.Length; k++)
            {
                if (s[k] == '\\')
                    k++;
                else if (s[k] == '\'')
                    return k + 1;
                else if (s[k] == '\n')
                    return k;
            }

            return s.Length;
        }

        var j = i;
        var (interpolated, verbatim) = (false, false);
        while (j < s.Length && s[j] is '$' or '@')
        {
            interpolated |= s[j] == '$';
            verbatim |= s[j] == '@';
            j++;
        }

        if (j >= s.Length || s[j] != '"')
            return i;

        if (verbatim)
        {
            for (var k = j + 1; k < s.Length; k++)
            {
                if (s[k] == '"')
                {
                    if (k + 1 < s.Length && s[k + 1] == '"')
                        k++;
                    else
                        return k + 1;
                }
                else if (interpolated && s[k] == '{')
                {
                    if (k + 1 < s.Length && s[k + 1] == '{')
                        k++;
                    else
                        k = Hole(s, k) - 1;
                }
            }

            return s.Length;
        }

        var quotes = 0;
        while (j + quotes < s.Length && s[j + quotes] == '"')
            quotes++;
        if (quotes >= 3)
        {
            var close = s.IndexOf(new string('"', quotes), j + quotes, StringComparison.Ordinal);
            return close < 0 ? s.Length : close + quotes;
        }

        if (quotes == 2)
            return j + 2; // ""

        for (var k = j + 1; k < s.Length; k++)
        {
            if (s[k] == '\\')
            {
                k++;
            }
            else if (s[k] == '"')
            {
                return k + 1;
            }
            else if (s[k] == '\n')
            {
                return k;
            }
            else if (interpolated && s[k] == '{')
            {
                if (k + 1 < s.Length && s[k + 1] == '{')
                    k++;
                else
                    k = Hole(s, k) - 1;
            }
        }

        return s.Length;
    }

    // Past the '}' that closes the interpolation hole opening at open (its code may hold strings of its own).
    private static int Hole(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length;)
        {
            var skipped = SkipLiteralOrComment(s, i);
            if (skipped > i)
            {
                i = skipped;
                continue;
            }

            if (s[i] == '{')
            {
                depth++;
            }
            else if (s[i] == '}' && --depth == 0)
            {
                return i + 1;
            }

            i++;
        }

        return s.Length;
    }

    private static string FindRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HandPegApp.csproj")))
                return folder.FullName;
        }

        throw new DirectoryNotFoundException("HandPegApp.csproj was not found above the golden tests' program.");
    }
}

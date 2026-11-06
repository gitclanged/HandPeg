using System.Text;

namespace HandPegGoldenTests;

/// <summary>
/// A filter graph's text cut into its chains and filters, for the golden files to show a hand-edited command's graph one filter to
/// a line (5.2-Audit: HandPeg writes no graph any more; the parser and the timing walk that followed the export's graphs went with
/// it). Brackets (labels) and quotes are kept whole.
/// </summary>
public static class FilterGraph
{
    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var (quoted, inLabel) = (false, false);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                current.Append(c);
                if (c == '\'')
                    quoted = false;
                continue;
            }

            if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(c).Append(text[++i]);
                continue;
            }

            if (c == '\'' && !inLabel)
                quoted = true;
            else if (c == '[')
                inLabel = true;
            else if (c == ']')
                inLabel = false;
            else if (c == separator && !inLabel)
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        parts.Add(current.ToString());
        return parts;
    }
}

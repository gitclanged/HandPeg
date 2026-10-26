using System.Text;

namespace HandPegGoldenTests;

/// <summary>
/// A filter graph taken apart the way FFmpeg's graph parser takes it apart: chains separated by ";", filters by ",",
/// and the link labels around each filter. A filter's inputs are its labels in order, then the filter before it in its
/// chain (when that one has no labels of its own on its output); its outputs are its labels, or else the filter after it.
/// </summary>
public sealed class FilterGraph
{
    public IReadOnlyList<GraphNode> Nodes => _nodes;

    private readonly List<GraphNode> _nodes = [];

    // A label written inside the graph is read once; one from outside it (an input stream) may be read by several filters.
    private readonly Dictionary<string, List<(GraphNode Node, int Pad)>> _consumers = new(StringComparer.Ordinal);

    public static FilterGraph Parse(string text)
    {
        var graph = new FilterGraph();
        foreach (var (chain, index) in SplitTopLevel(text, ';').Select((c, i) => (c, i)))
        {
            GraphNode? previous = null;
            foreach (var piece in SplitTopLevel(chain, ','))
            {
                if (piece.Trim().Length == 0)
                    continue;

                var node = ParseFilter(piece, graph._nodes.Count, index);
                if (previous is not null && previous.OutputLabels.Count == 0)
                {
                    (previous.Next, node.Previous) = (node, previous);
                }

                graph._nodes.Add(node);
                previous = node;
            }
        }

        foreach (var node in graph._nodes)
        {
            for (var pad = 0; pad < node.InputLabels.Count; pad++)
            {
                if (!graph._consumers.TryGetValue(node.InputLabels[pad], out var readers))
                    graph._consumers[node.InputLabels[pad]] = readers = [];
                readers.Add((node, pad));
            }
        }

        return graph;
    }

    /// <summary>Where an output of a node goes: the node and input pad that take it, or null for an output of the whole graph.</summary>
    public IEnumerable<(GraphNode Node, int Pad)?> Consumers(GraphNode node)
    {
        if (node.OutputLabels.Count > 0)
        {
            foreach (var label in node.OutputLabels)
                yield return _consumers.TryGetValue(label, out var readers) ? readers[0] : null;
        }
        else if (node.Next is { } next)
        {
            yield return (next, next.InputLabels.Count);
        }
        else
        {
            yield return null;
        }
    }

    /// <summary>The nodes and pads that read a label ("[vid1]", "[0:a:0]", or one written inside the graph); none when nothing does.</summary>
    public IReadOnlyList<(GraphNode Node, int Pad)> ConsumersOf(string label) => _consumers.TryGetValue(label, out var readers) ? readers : [];

    /// <summary>The labels the graph reads from outside: those no filter writes.</summary>
    public IEnumerable<string> ExternalInputs()
    {
        var written = _nodes.SelectMany(n => n.OutputLabels).ToHashSet(StringComparer.Ordinal);
        return _nodes.SelectMany(n => n.InputLabels).Where(l => !written.Contains(l)).Distinct(StringComparer.Ordinal);
    }

    private static GraphNode ParseFilter(string text, int id, int chain)
    {
        var inputs = new List<string>();
        var outputs = new List<string>();
        var body = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                body.Append(c);
                if (c == '\'')
                    quoted = false;
                continue;
            }

            switch (c)
            {
                case '\\':
                    body.Append(c);
                    if (i + 1 < text.Length)
                        body.Append(text[++i]);
                    break;
                case '\'':
                    quoted = true;
                    body.Append(c);
                    break;
                case '[':
                    var end = text.IndexOf(']', i + 1);
                    if (end < 0)
                        throw new FormatException($"A link label is not closed: {text}");
                    (body.Length == 0 && outputs.Count == 0 ? inputs : outputs).Add(text[(i + 1)..end]);
                    i = end;
                    break;
                default:
                    if (!(char.IsWhiteSpace(c) && body.Length == 0))
                        body.Append(c);
                    break;
            }
        }

        var whole = body.ToString().Trim();
        var equals = whole.IndexOf('=');
        var name = equals < 0 ? whole : whole[..equals];
        var args = equals < 0 ? "" : whole[(equals + 1)..];
        var at = name.IndexOf('@');
        return new GraphNode(id, chain, at < 0 ? name : name[..at], args, inputs, outputs);
    }

    /// <summary>Splits text at a separator that is outside quotes, link labels and escapes.</summary>
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

/// <summary>One filter of a graph: its name (without an "@instance"), its arguments as written, and its labels.</summary>
public sealed class GraphNode(int id, int chain, string name, string args, List<string> inputs, List<string> outputs)
{
    public int Id { get; } = id;
    public int Chain { get; } = chain;
    public string Name { get; } = name;
    public string Args { get; } = args;
    public IReadOnlyList<string> InputLabels { get; } = inputs;
    public IReadOnlyList<string> OutputLabels { get; } = outputs;
    public GraphNode? Previous { get; set; }
    public GraphNode? Next { get; set; }

    /// <summary>How many inputs it has: its labels, and the filter before it in its chain.</summary>
    public int InputCount => InputLabels.Count + (Previous is not null ? 1 : 0);

    /// <summary>A source: nothing flows into it.</summary>
    public bool IsSource => InputCount == 0;

    private FilterOptions? _options;

    public FilterOptions Options => _options ??= FilterOptions.Parse(Args);

    public override string ToString() => $"#{Id} {Name}={Args}";
}

/// <summary>
/// A filter's options as FFmpeg reads them: the graph's quoting and escapes taken off the whole (av_get_token), then the
/// list split at ":" and each value unquoted again. Values without a key are positional, in order.
/// </summary>
public sealed class FilterOptions
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Named { get; } = new(StringComparer.Ordinal);

    public static FilterOptions Parse(string args)
    {
        var options = new FilterOptions();
        var text = Unescape(args);
        foreach (var token in SplitOptions(text))
        {
            var equals = IndexOfUnescaped(token, '=');
            if (equals < 0)
                options.Positional.Add(Unescape(token));
            else
                options.Named[token[..equals]] = Unescape(token[(equals + 1)..]);
        }

        return options;
    }

    /// <summary>A named option, or the positional one at its place in the filter's option list, or null.</summary>
    public string? Get(string name, int position = -1, params string[] aliases)
    {
        if (Named.TryGetValue(name, out var value))
            return value;
        foreach (var alias in aliases)
        {
            if (Named.TryGetValue(alias, out value))
                return value;
        }

        return position >= 0 && position < Positional.Count ? Positional[position] : null;
    }

    /// <summary>av_get_token's reading of a value: a backslash keeps the next character, single quotes keep what is between them.</summary>
    public static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\'')
                    quoted = false;
                else
                    result.Append(c);
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                result.Append(text[++i]);
            }
            else if (c == '\'')
            {
                quoted = true;
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static List<string> SplitOptions(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                current.Append(c);
                if (c == '\'')
                    quoted = false;
            }
            else if (c == '\\' && i + 1 < text.Length)
            {
                current.Append(c).Append(text[++i]);
            }
            else if (c == '\'')
            {
                quoted = true;
                current.Append(c);
            }
            else if (c == ':')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0 || parts.Count > 0)
            parts.Add(current.ToString());
        return parts;
    }

    private static int IndexOfUnescaped(string text, char wanted)
    {
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '\'')
                    quoted = false;
            }
            else if (c == '\\')
            {
                i++;
            }
            else if (c == '\'')
            {
                quoted = true;
            }
            else if (c == wanted)
            {
                return i;
            }
        }

        return -1;
    }
}

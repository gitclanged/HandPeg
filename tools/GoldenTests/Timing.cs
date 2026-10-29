using System.Globalization;

namespace HandPegGoldenTests;

/// <summary>
/// Follows one moment of a file through a filter graph, by the time stamps the filters give it, to where it is seen or
/// heard. What each filter does to time stamps is modelled on what FFmpeg does (checked against it: tools\README.md,
/// "the timing model"):
///
///   movie / amovie   a frame or sample keeps the time stamp it has in its file (sp / seek_point only choose where
///                    reading starts, counted from the container's start);
///   trim / atrim     drop what is outside start..end, on those same time stamps; the stream's first moment moves up;
///   setpts / asetpts an expression of PTS, STARTPTS (the stream's first time stamp), N, TB, FRAME_RATE, SR, T, STARTT;
///   adelay           pushes everything later by its delay (counted in samples: its own time stamps are not usable);
///   aresample        with first_pts, the stream starts exactly there (filled with silence or cut), time stamps kept;
///   atempo           time from the stream's first moment divided by the tempo;
///   amix             adds its inputs up sample for sample from each one's first sample, whatever their time stamps (a
///                    moment followed through one is its distance from its input's first sample: where the mix counts it);
///   overlay          shows the frame of its upper input whose time stamp is the time of the picture under it.
///
/// Everything else leaves time stamps alone. Times are in seconds (TB is taken as 1).
/// </summary>
public static class Timing
{
    /// <summary>Where a moment is in a stream: its time stamp, and the time stamp of the stream's first frame or sample.</summary>
    public readonly record struct Clock(double T, double First);

    /// <summary>How a moment came out at the end of a walk: the filter it reached, on which input, and its clock there.</summary>
    public readonly record struct Arrival(GraphNode Node, int Pad, Clock Clock);

    /// <summary>A moment that a filter drops (cut by a trim, before a stream's start): it is never seen or heard.</summary>
    public sealed class Dropped(string why) : Exception(why);

    /// <summary>A filter whose effect on time stamps is not modelled: the walk cannot say where the moment goes.</summary>
    public sealed class Unsupported(string why) : Exception(why);

    /// <summary>
    /// Follows a moment out of <paramref name="start"/> (a source, or the filter that reads an outside label) through every
    /// branch, until <paramref name="stop"/> says a filter is where it ends. Branches that end anywhere else (a side chain,
    /// an output of the graph) are left; what reached a stop is returned.
    /// </summary>
    /// <param name="startPad">The input the moment enters <paramref name="start"/> by; -1 for a source.</param>
    /// <param name="stop">Given a filter and the input the moment arrives by: true to end there, false to go through it, null to give the branch up.</param>
    public static List<Arrival> Walk(FilterGraph graph, GraphNode start, int startPad, Clock clock, double rate, Func<GraphNode, int, bool?> stop)
    {
        var arrivals = new List<Arrival>();
        Visit(start, startPad, clock, 0);
        return arrivals;

        void Visit(GraphNode node, int pad, Clock at, int depth)
        {
            if (depth > 4000)
                throw new Unsupported("The graph is too deep to follow.");
            if (pad >= 0)
            {
                switch (stop(node, pad))
                {
                    case true:
                        arrivals.Add(new Arrival(node, pad, at));
                        return;
                    case null:
                        return;
                }
            }

            Clock after;
            try
            {
                after = pad < 0 ? at : Apply(node, at, rate);
            }
            catch (Dropped)
            {
                return;
            }

            foreach (var consumer in graph.Consumers(node))
            {
                if (consumer is { } next)
                    Visit(next.Node, next.Pad, after, depth + 1);
            }
        }
    }

    /// <summary>What one filter does to a moment's clock. Sources are not applied: the walk starts after them.</summary>
    public static Clock Apply(GraphNode node, Clock clock, double rate)
    {
        var o = node.Options;
        switch (node.Name)
        {
            case "trim":
            case "atrim":
            {
                var start = Seconds(o.Get("start", 0)) ?? (o.Get("start_pts") is { } sp ? Seconds(sp) : null);
                var end = Seconds(o.Get("end", 1)) ?? (o.Get("end_pts") is { } ep ? Seconds(ep) : null);
                if (o.Get("duration", 4) is { } duration && Seconds(duration) is { } length)
                    end = Math.Min(end ?? double.PositiveInfinity, (start ?? clock.First) + length);
                if (o.Get("end_frame") is { } endFrame && double.Parse(endFrame, CultureInfo.InvariantCulture) <= 1 && clock.T > clock.First + 1e-6)
                    throw new Dropped($"{node.Name} keeps only the first frame");
                if (start is { } from && clock.T < from - 1e-6)
                    throw new Dropped($"{node.Name} starts at {from}");
                if (end is { } to && clock.T >= to - 1e-6)
                    throw new Dropped($"{node.Name} ends at {to}");
                return clock with { First = Math.Max(clock.First, start ?? clock.First) };
            }

            case "setpts":
            case "asetpts":
            {
                var expression = o.Get("expr", 0) ?? "PTS";
                var audio = node.Name == "asetpts";
                var value = Expression.Evaluate(expression, clock.T, clock.First, rate, audio);
                var first = Expression.Evaluate(expression, clock.First, clock.First, rate, audio);
                return new Clock(value, first);
            }

            case "adelay":
            {
                var delays = o.Get("delays", 0) ?? "0";
                var first = delays.Split('|')[0].Trim();
                var seconds = first.EndsWith('S') ? double.Parse(first[..^1], CultureInfo.InvariantCulture) / 48000
                    : first.EndsWith('s') ? double.Parse(first[..^1], CultureInfo.InvariantCulture)
                    : double.Parse(first, CultureInfo.InvariantCulture) / 1000;
                return clock with { T = clock.T + seconds };
            }

            case "aresample":
            {
                if (o.Get("first_pts") is not { } firstPts)
                    return clock;
                var rateText = o.Get("osr", 0, "out_sample_rate") ?? "48000";
                var start = double.Parse(firstPts, CultureInfo.InvariantCulture) / double.Parse(rateText, CultureInfo.InvariantCulture);
                if (clock.T < start - 1e-6)
                    throw new Dropped($"aresample starts the stream at {start}");
                return clock with { First = start };
            }

            case "atempo":
            {
                var tempo = double.Parse(o.Get("tempo", 0) ?? "1", CultureInfo.InvariantCulture);
                return clock with { T = clock.First + (clock.T - clock.First) / tempo };
            }

            // Followed through (not stopped at): its output counted from its first sample, which is each input's first.
            case "amix":
                return new Clock(clock.T - clock.First, 0);

            case "tpad":
            case "apad" when o.Get("pad_dur") is null:
                if (node.Name == "tpad" && Seconds(o.Get("start_duration", 1)) is { } padding)
                    return new Clock(clock.T + padding, clock.First);
                return clock;

            case "concat":
            case "asetrate":
            case "setrate":
            case "areverse":
            case "reverse":
            case "loop" when !IsStillLoop(o):
            case "aloop":
                throw new Unsupported($"{node.Name} changes time stamps in a way the timing model does not follow");

            default:
                return clock;
        }
    }

    // A one-frame picture made into a stream (loop=loop=-1:size=1): a still, which has no moments of a file.
    private static bool IsStillLoop(FilterOptions o) => o.Get("size", 1) == "1";

    /// <summary>A time as FFmpeg reads one: seconds, or [-][HH:]MM:SS[.m...].</summary>
    private static double? Seconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        if (!text.Contains(':'))
            return double.Parse(text.TrimEnd('s'), CultureInfo.InvariantCulture);

        var negative = text.StartsWith('-');
        var parts = text.TrimStart('-').Split(':');
        var seconds = parts.Aggregate(0.0, (total, part) => total * 60 + double.Parse(part, CultureInfo.InvariantCulture));
        return negative ? -seconds : seconds;
    }

    /// <summary>
    /// setpts's arithmetic: numbers, + - * /, brackets, and the variables it offers, with time stamps in seconds (TB = 1).
    /// N counts frames (or samples) from the stream's first, which assumes a stream without gaps.
    /// </summary>
    private sealed class Expression
    {
        private readonly string _text;
        private readonly Dictionary<string, double> _values;
        private int _at;

        private Expression(string text, Dictionary<string, double> values) => (_text, _values) = (text, values);

        public static double Evaluate(string text, double pts, double startPts, double rate, bool audio)
        {
            var count = (pts - startPts) * (audio ? 48000 : rate);
            var values = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["PTS"] = pts, ["STARTPTS"] = startPts, ["TB"] = 1, ["N"] = count, ["T"] = pts, ["STARTT"] = startPts,
                ["FRAME_RATE"] = rate, ["FR"] = rate, ["SR"] = 48000, ["SAMPLE_RATE"] = 48000, ["NB_CONSUMED_SAMPLES"] = count,
                ["PI"] = Math.PI, ["E"] = Math.E,
            };

            var expression = new Expression(text, values);
            var value = expression.Sum();
            expression.Skip();
            if (expression._at != text.Length)
                throw new Unsupported($"setpts expression not understood: {text}");
            return value;
        }

        private double Sum()
        {
            var value = Product();
            while (true)
            {
                Skip();
                if (Take('+'))
                    value += Product();
                else if (Take('-'))
                    value -= Product();
                else
                    return value;
            }
        }

        private double Product()
        {
            var value = Unary();
            while (true)
            {
                Skip();
                if (Take('*'))
                    value *= Unary();
                else if (Take('/'))
                    value /= Unary();
                else
                    return value;
            }
        }

        private double Unary()
        {
            Skip();
            if (Take('-'))
                return -Unary();
            if (Take('+'))
                return Unary();
            if (Take('('))
            {
                var inner = Sum();
                Skip();
                if (!Take(')'))
                    throw new Unsupported($"setpts expression not understood: {_text}");
                return inner;
            }

            var start = _at;
            if (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.'))
            {
                while (_at < _text.Length && (char.IsDigit(_text[_at]) || _text[_at] == '.'))
                    _at++;
                if (_at < _text.Length && _text[_at] is 'e' or 'E' && _at + 1 < _text.Length && (char.IsDigit(_text[_at + 1]) || _text[_at + 1] is '-' or '+'))
                {
                    _at += 2;
                    while (_at < _text.Length && char.IsDigit(_text[_at]))
                        _at++;
                }

                return double.Parse(_text[start.._at], CultureInfo.InvariantCulture);
            }

            while (_at < _text.Length && (char.IsLetterOrDigit(_text[_at]) || _text[_at] == '_'))
                _at++;
            var name = _text[start.._at];
            Skip();
            if (name.Length == 0 || (_at < _text.Length && _text[_at] == '('))
                throw new Unsupported($"setpts expression not understood (a function or a stray character at {start}): {_text}");
            return _values.TryGetValue(name, out var value) ? value : throw new Unsupported($"setpts variable {name} is not modelled: {_text}");
        }

        private bool Take(char c)
        {
            if (_at < _text.Length && _text[_at] == c)
            {
                _at++;
                return true;
            }

            return false;
        }

        private void Skip()
        {
            while (_at < _text.Length && char.IsWhiteSpace(_text[_at]))
                _at++;
        }
    }
}

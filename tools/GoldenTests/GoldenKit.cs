global using static HandPegGoldenTests.GoldenKit;

using System.Text;
using System.Windows;

namespace HandPegGoldenTests;

/// <summary>
/// Which goldens a run makes: every golden, or only those whose name contains at least one term of
/// <c>--filter "a|b|c"</c> (case-insensitive). Every case helper asks <see cref="Wanted"/> before it runs, so a filtered run
/// skips the work of the cases it does not report instead of running them and discarding the result.
/// </summary>
public static class GoldenFilter
{
    private static string[] _terms = [];

    /// <summary>The filter's terms, in the order given; empty when every golden runs.</summary>
    public static IReadOnlyList<string> Terms => _terms;

    /// <summary>Whether a filter was given.</summary>
    public static bool Active => _terms.Length > 0;

    /// <summary>Sets the filter from the <c>--filter</c> value (terms split on <c>|</c>); null or blank selects every golden.</summary>
    public static void Configure(string? filter) =>
        _terms = string.IsNullOrWhiteSpace(filter) ? [] : filter.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Whether the golden named <paramref name="golden"/> is part of this run.</summary>
    public static bool Wanted(string golden)
    {
        if (_terms.Length == 0)
            return true;
        foreach (var term in _terms)
        {
            if (golden.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether any of <paramref name="goldens"/> is part of this run (for a suite that makes several goldens in one pass).</summary>
    public static bool WantedAny(params string[] goldens) => goldens.Any(Wanted);

    /// <summary>The terms that matched none of <paramref name="produced"/> (a typo, or a golden that was renamed or removed).</summary>
    public static IEnumerable<string> Unmatched(IEnumerable<string> produced)
    {
        var names = produced.ToList();
        return _terms.Where(term => !names.Any(name => name.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Runs <paramref name="make"/> with the filter lifted, for a suite whose wanted golden consumes another suite's results.</summary>
    public static T Unfiltered<T>(Func<T> make)
    {
        var saved = _terms;
        _terms = [];
        try
        {
            return make();
        }
        finally
        {
            _terms = saved;
        }
    }
}

/// <summary>
/// The shared scaffolding of a golden case (imported into every test file by the <c>global using static</c> above): the
/// three case shapes the suites use, the skip sentinel for a case the filter leaves out, and the WPF application that
/// cases after the project-settings suite expect to exist.
/// </summary>
public static class GoldenKit
{
    /// <summary>The result of a case the filter left out: it never reaches the golden comparison.</summary>
    public static GraphResult Skipped(string golden) => new(golden, "", [], 0) { Skipped = true };

    /// <summary>The result of <paramref name="make"/> when the filter wants <paramref name="golden"/>; otherwise it never runs.</summary>
    public static GraphResult Lazy(string golden, Func<GraphResult> make) => GoldenFilter.Wanted(golden) ? make() : Skipped(golden);

    /// <summary>A case of expectations: each <c>expect(holds, what)</c> writes "ok: what" or "NOT: what" under the title.</summary>
    public static GraphResult Case(string golden, string title, Action<Action<bool, string>> run) =>
        Make(golden, title, (expect, _, _) => run(expect));

    /// <summary>A case of expectations that also writes its own lines into the golden text.</summary>
    public static GraphResult Case(string golden, string title, Action<Action<bool, string>, StringBuilder> run) =>
        Make(golden, title, (expect, text, _) => run(expect, text));

    /// <summary>A case that writes its golden text and reports its problems itself.</summary>
    public static GraphResult Case(string golden, string header, Action<StringBuilder, List<string>> body) =>
        Make(golden, header, (_, text, problems) => body(text, problems));

    /// <summary>The problem line for an exception a case threw: its type, message and stack trace.</summary>
    public static string Threw(Exception ex) => $"threw {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";

    /// <summary>
    /// The WPF application. The runner creates it before the project-settings suite, filtered or not, so every suite from
    /// there on runs with it and its dispatcher present and a case sees the same environment alone as in the full run.
    /// </summary>
    public static Application EnsureApplication() =>
        Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

    private static GraphResult Make(string golden, string title, Action<Action<bool, string>, StringBuilder, List<string>> body)
    {
        if (!GoldenFilter.Wanted(golden))
            return Skipped(golden);

        var text = new StringBuilder(title).Append('\n');
        var problems = new List<string>();
        void Expect(bool holds, string what)
        {
            text.Append(holds ? "ok: " : "NOT: ").Append(what).Append('\n');
            if (!holds)
                problems.Add(what);
        }

        try
        {
            body(Expect, text, problems);
        }
        catch (Exception ex)
        {
            problems.Add(Threw(ex));
        }

        return new GraphResult(golden, text.ToString(), problems, 0);
    }
}

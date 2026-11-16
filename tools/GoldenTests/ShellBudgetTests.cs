using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HandPegApp;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// shell-budget: MainWindow's and MainViewModel's size only shrinks. Per class: its partial files, its fields (instance and static, by
/// reflection; constants and MainWindow's named XAML elements aside) and its field declaration lines in source (<see cref="FieldLine"/>,
/// the pattern local\AutoTest.ps1's static check counts with). The recorded numbers are this golden's own text: a count above them
/// fails (feature state belongs in a controller under src/Shell or a state object), and a count below them shows as a diff until the
/// golden is written again with the lower number (-Update), so the ratchet tightens.
/// </summary>
public static partial class ShellBudgetTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("shell-budget", "# MainWindow and MainViewModel: partial files, fields and field lines only shrink (recorded below; a lower count is written with -Update).", Budget);
    }

    /// <summary>
    /// A field declaration at class-member depth (four spaces): access modifiers, then a type (a tuple too), a name and '=', ';' or ','. Not a
    /// constant, event, property, method or nested type. local\AutoTest.ps1 uses this same pattern for its static check.
    /// </summary>
    [GeneratedRegex(@"^    (?:\[[^\]]*\]\s*)*(?:private|protected|internal|public)(?:\s+(?:static|readonly|volatile))*\s+(?!const\b|class\b|sealed\b|record\b|struct\b|enum\b|interface\b|delegate\b|event\b|partial\b|override\b|virtual\b|abstract\b|async\b|extern\b|unsafe\b|void\b)(?:\([^)]*\)\??|[\w.<>\[\],? ]+?)\s+_?[A-Za-z]\w*\s*(?:=(?!>)|;|,)", RegexOptions.Multiline)]
    internal static partial Regex FieldLine();

    private sealed record Measure(string Name, int Files, int Fields, int FieldLines);

    private static void Budget(Action<bool, string> expect, StringBuilder text)
    {
        var src = Path.Combine(SourceProbe.Root, "src");
        var named = XDocument.Load(Path.Combine(src, "MainWindow.xaml")).Descendants()
            .Select(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))).OfType<string>().ToHashSet(StringComparer.Ordinal);
        named.Add("_contentLoaded");

        var measures = new[]
        {
            Of("MainWindow", typeof(MainWindow), Directory.GetFiles(src, "MainWindow*.cs"), named),
            Of("MainViewModel", typeof(MainViewModel), Directory.GetFiles(Path.Combine(src, "ViewModels"), "MainViewModel*.cs"), []),
        };

        var recorded = Recorded();
        foreach (var m in measures)
        {
            text.Append($"{m.Name}: {m.Files} partial files, {m.Fields} fields, {m.FieldLines} field lines\n");

            // A first run records what is there; the messages carry numbers only when they fail, so the text stays the same run to run.
            var was = recorded.GetValueOrDefault(m.Name) ?? m;
            expect(m.Files <= was.Files, $"{m.Name}: no new partial file" + (m.Files <= was.Files ? "" : $" ({m.Files}, recorded {was.Files})"));
            expect(m.Fields <= was.Fields, $"{m.Name}: no new field" + (m.Fields <= was.Fields ? "" : $" ({m.Fields}, recorded {was.Fields}): put feature state in a controller (src/Shell) or a state object"));
            expect(m.FieldLines <= was.FieldLines, $"{m.Name}: no new field line" + (m.FieldLines <= was.FieldLines ? "" : $" ({m.FieldLines}, recorded {was.FieldLines})"));
        }

        expect(measures.All(m => m.Files > 0 && m.Fields > 0 && m.FieldLines > 0), "both classes are measured (files, fields and field lines found)");
    }

    private static Measure Of(string name, Type type, string[] files, HashSet<string> skip)
    {
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Count(f => !f.IsLiteral && !skip.Contains(f.Name));
        var lines = files.Sum(file => FieldLine().Matches(File.ReadAllText(file).ReplaceLineEndings("\n")).Count);
        return new Measure(name, files.Length, fields, lines);
    }

    // The numbers this golden recorded last (its own text); none on a first run.
    private static Dictionary<string, Measure> Recorded()
    {
        var path = Path.Combine(SourceProbe.Root, "tools", "GoldenTests", "golden", "shell-budget.txt");
        var found = new Dictionary<string, Measure>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return found;

        foreach (Match match in Regex.Matches(File.ReadAllText(path), @"(\w+): (\d+) partial files, (\d+) fields, (\d+) field lines"))
            found[match.Groups[1].Value] = new Measure(match.Groups[1].Value, int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value), int.Parse(match.Groups[4].Value));
        return found;
    }
}

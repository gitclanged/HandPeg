using System.IO;
using System.Text.RegularExpressions;

namespace HandPegGoldenTests;

/// <summary>
/// golden-kit-ratchet: the goldens' own scaffolding stays consolidated. The case shapes live in GoldenKit, the source probes in
/// SourceProbe and the view-model seams in ViewModelFixture/Reflect; no test file grows a private copy again, and string-named
/// reflection into HandPeg (which breaks only at run time on a rename) never rises above its recorded ceiling. Lower a ceiling
/// whenever a change removes lookups.
/// </summary>
public static partial class GoldenKitTests
{
    // The ceilings: today's counts. A count above its ceiling fails; a count below it is reported so the ceiling can come down.
    private const int ReflectionCeiling = 168, CaseWrapperCeiling = 2, RootCopyCeiling = 1, HiddenCopyCeiling = 2;

    private static readonly string[] Kit = ["GoldenKit.cs", "SourceProbe.cs", "ViewModelFixture.cs", "GoldenKitTests.cs"];

    public static IEnumerable<GraphResult> All()
    {
        yield return Case("golden-kit-ratchet", "# The goldens' scaffolding: shared case shapes, source probes and seams, and a ceiling on string-named reflection.", Ratchet);
    }

    private static void Ratchet(Action<bool, string> expect)
    {
        var files = Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "tools", "GoldenTests"), "*.cs")
            .Where(f => !Kit.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => (Name: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .ToList();
        int Count(Regex pattern) => files.Sum(f => pattern.Matches(f.Text).Count);
        string[] Holders(Regex pattern) => [.. files.Where(f => pattern.IsMatch(f.Text)).Select(f => f.Name).Order(StringComparer.Ordinal)];

        void Ceiling(string what, int count, int ceiling)
        {
            if (count < ceiling)
                Console.WriteLine($"        {what}: {count}, below its ceiling of {ceiling} (lower it in GoldenKitTests)");
            expect(count <= ceiling, $"{what}: at most {ceiling}" + (count > ceiling ? $" (now {count})" : ""));
        }

        expect(Holders(SourceHelper()).Length == 0,
            "no test file keeps a private Method/MemberOf/MethodText/InOrder/Ordered copy (SourceProbe has the one true-end, comment-aware version)");
        Ceiling("case wrappers (a shared Case with setup round it)", Holders(CaseHelper()).Length, CaseWrapperCeiling);
        Ceiling("repository-root helpers outside SourceProbe", Count(RootHelper()), RootCopyCeiling);
        Ceiling("BindingFlags Hidden copies outside Reflect", Count(HiddenCopy()), HiddenCopyCeiling);
        Ceiling("string-named reflection lookups (GetField/GetMethod/GetProperty)", Count(StringLookup()), ReflectionCeiling);
    }

    [GeneratedRegex(@"static (?:string (?:Method|MemberOf|MethodText)|bool (?:InOrder|Ordered))\(")]
    private static partial Regex SourceHelper();

    [GeneratedRegex(@"static GraphResult Case\(")]
    private static partial Regex CaseHelper();

    [GeneratedRegex(@"static string\?? (?:RepositoryRoot|Root|RepoRoot)\(\)")]
    private static partial Regex RootHelper();

    [GeneratedRegex(@"const BindingFlags Hidden")]
    private static partial Regex HiddenCopy();

    [GeneratedRegex(@"\.Get(?:Field|Method|Property)\(""")]
    private static partial Regex StringLookup();
}

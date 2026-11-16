using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace HandPegGoldenTests;

/// <summary>
/// shader-names: the compiled shader entry points embedded in HandPeg (HandPegApp.Shaders.&lt;entry&gt;.cso) are exactly the VS*/PS*
/// entry points defined in src\Compositor\Shaders, each defined once, and every "VS*"/"PS*" name the C# code asks ShaderLibrary for is
/// one of them. The build enforces the same (HLSL0004..0007 in HandPegApp.csproj); this case keeps the list itself under review.
/// </summary>
public static partial class ShaderNamesTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("shader-names", "# The shader entry points compiled into HandPeg, against the HLSL that defines them and the C# that names them.", Names);
    }

    private static void Names(Action<bool, string> expect, StringBuilder text)
    {
        const string prefix = "HandPegApp.Shaders.";
        var embedded = typeof(HandPegApp.Compositor.CompositorWorker).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".cso", StringComparison.Ordinal))
            .Select(n => n[prefix.Length..^4]).Order(StringComparer.Ordinal).ToList();

        var defined = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src", "Compositor", "Shaders"), "*.hlsl").Order(StringComparer.Ordinal))
            defined.AddRange(EntryPoint().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value));

        var named = Directory.EnumerateFiles(Path.Combine(SourceProbe.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => NameLiteral().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        text.Append(embedded.Count).Append(" entry points:\n");
        foreach (var line in embedded.Chunk(6))
            text.Append("  ").Append(string.Join(" ", line)).Append('\n');

        expect(embedded.Count > 0 && embedded.SequenceEqual(defined.Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "the embedded entry points are exactly the ones the shader files define");
        var duplicates = defined.GroupBy(d => d, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        expect(duplicates.Count == 0, "each entry point is defined in one shader file only" + (duplicates.Count > 0 ? ": " + string.Join(", ", duplicates) : ""));
        var unknown = named.Where(n => !embedded.Contains(n, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        expect(unknown.Count == 0, "every shader name in the C# code is an embedded entry point" + (unknown.Count > 0 ? ": " + string.Join(", ", unknown) : ""));
        var unused = embedded.Where(e => !named.Contains(e)).ToList();
        expect(unused.Count == 0, "every embedded entry point is named somewhere in the C# code" + (unused.Count > 0 ? ": " + string.Join(", ", unused) : ""));
    }

    [GeneratedRegex(@"^\s*(?:float4|VsOut)\s+((?:VS|PS)\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex EntryPoint();

    [GeneratedRegex("\"((?:VS|PS)[A-Z]\\w*)\"")]
    private static partial Regex NameLiteral();
}

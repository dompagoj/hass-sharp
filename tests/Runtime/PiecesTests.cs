using HassSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using System.Runtime.Loader;
using static VisualExecutionTests;

/// <summary>
/// <see cref="VisualGenerator.Pieces"/> as the compiler reads it: each text, compiled into a property, evaluates to itself
/// (escapes either side of a piece boundary, the boundary lengths, a surrogate pair across a boundary, more than 100 KiB),
/// and the expression for a long text stays one flat array, not nested.
/// </summary>
static class PiecesTests
{
    [RuntimeSuite("--pieces", Order = 33)]
    public static void Run()
    {
        const int n = VisualGenerator.PieceLength;
        var escapes = "\"\\\n\r\t\0\u0001</script>&'+";
        var cases = new List<(string Name, string Value)>
        {
            ("empty", ""), ("one piece", new string('a', n)), ("one piece and one character", new string('a', n + 1)),
            ("two pieces", new string('b', 2 * n)),
        };
        // Each escape at the last position of a piece and at the first position of the next.
        foreach (var c in escapes)
        {
            cases.Add(($"U+{(int)c:X4} at {n - 1}", new string('x', n - 1) + c + new string('y', n)));
            cases.Add(($"U+{(int)c:X4} at {n}", new string('x', n) + c + new string('y', n)));
        }
        // A surrogate pair across the boundary (its high half at the last position), and non-ASCII text.
        cases.Add(("a pair across a boundary", new string('x', n - 1) + "\U0001F600" + new string('y', n)));
        cases.Add(("a pair after a boundary", new string('x', n) + "\U0001F600" + new string('y', n)));
        cases.Add(("non-ASCII", string.Concat(Enumerable.Repeat("Grüße, 你好 \U0001F600 ", 40))));
        // More than 100 KiB of JSON-like text with escapes at every distance from a boundary.
        var random = new Random(29);
        var large = new System.Text.StringBuilder();
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789{}[]:,\"\\\n é€";
        while (large.Length < 110 * 1024) large.Append(alphabet[random.Next(alphabet.Length)]);
        cases.Add(("110 KiB", large.ToString()));

        var properties = string.Join("\n", cases.Select((c, i) => $"public static string P{i} => {VisualGenerator.Pieces(c.Value)};"));
        var tree = CSharpSyntaxTree.ParseText("public static class Texts {\n" + properties + "\n}");
        var compilation = CSharpCompilation.Create("Pieces_" + Guid.NewGuid().ToString("N"), [tree], NodeLibraryCompiler.FrameworkReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Check(emitted.Success, "the pieces do not compile: " + string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(3)));
        var context = new AssemblyLoadContext("pieces", isCollectible: true);
        try
        {
            image.Position = 0;
            var texts = context.LoadFromStream(image).GetType("Texts")!;
            for (var i = 0; i < cases.Count; i++)
            {
                var read = (string)texts.GetProperty("P" + i)!.GetValue(null)!;
                Check(read == cases[i].Value, $"{cases[i].Name}: the compiled text differs (length {read.Length}, expected {cases[i].Value.Length})");
            }
        }
        finally { context.Unload(); }

        // One flat array: the deepest node of a long text's expression is only a few levels down, however long it is.
        var expression = SyntaxFactory.ParseExpression(VisualGenerator.Pieces(large.ToString()));
        var depth = expression.DescendantNodes().Max(node => node.Ancestors().Count());
        Check(depth <= 8, $"the expression for 110 KiB nests {depth} levels deep");
        Console.WriteLine($"PASS pieces: {cases.Count} texts (escapes at both sides of a boundary, 0, {n} and {n + 1} characters, surrogate pairs, 110 KiB) compile to themselves; the expression nests {depth} levels");
    }
}

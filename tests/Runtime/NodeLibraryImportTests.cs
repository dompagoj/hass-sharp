using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// The node library <c>import</c> operation: several library files saved in one checked commit, as a repository's
/// manifest lists them. A library that uses another's types compiles when the other is imported beside it or stored.
/// </summary>
static class NodeLibraryImportTests
{
    static void Check(bool ok, string message) => VisualExecutionTests.Check(ok, message);
    static async Task<JsonObject> Request(HassSharpManager manager, object request) =>
        JsonNode.Parse(await manager.NodeLibraryRequest(JsonSerializer.Serialize(request)))!.AsObject();
    static JsonNode Result(JsonObject response) { Check(response["ok"]!.GetValue<bool>(), response.ToJsonString()); return response["result"]!; }
    static async Task<JsonObject> Visual(HassSharpManager m, object r) => JsonNode.Parse(await m.VisualRequest(JsonSerializer.Serialize(r)))!.AsObject();
    static string ErrorCode(JsonObject response) => response["ok"]!.GetValue<bool>() ? "ok" : response["error"]!["code"]!.GetValue<string>();

    // The dependent pair, shaped as hass-sharp-vision's Vision.cs and PeopleDetector.cs: B.cs uses A.cs's types.
    const string SourceA = """
        public record Reading(int Value);
        public static class Meter { public static Reading Read() => new(1); }
        """;
    const string SourceB = """
        public abstract class MeterDevice : SharpDevice { protected Reading Last() => Meter.Read(); }
        public static class ReadingNodes { [Node("Use reading")] public static int Use(Reading reading) => reading.Value; }
        """;
    const string SharedSource = "public static class Nodes { [Node(\"Use\")] public static int Use(int value=3) => value; }";
    static object Library(string id, string name, string source, long expectedRevision) => new { id, name, source, expectedRevision };
    static object Import(params object[] libraries) => new { operation = "import", libraries };

    [RuntimeSuite("--node-library-import", Order = 24)]
    public static async Task Run()
    {
        await TwoDependentLibrariesImportTogether();
        await ADependentImportsAfterItsDependencyIsStored();
        await OneInvalidLibraryWritesNothing();
        await AStaleRevisionWritesNothing();
        await ImportRebuildsGraphsOnce();
        await BadRequests();
        Console.WriteLine("PASS node-library import: one checked commit, request order, dependencies, refusals write nothing, one rebuild");
    }

    /// <summary>A manager on its own temporary configuration, removed afterwards.</summary>
    static async Task Fresh(Func<HassSharpManager, string, Task> body)
    {
        var previous = HassPath.HassConfiguration;
        var path = Path.Combine(Path.GetTempPath(), "library-import-" + Guid.NewGuid());
        var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
        var manager = new HassSharpManager(path);
        try { await manager.Init(); await body(manager, Path.Combine(path, ".storage", "hass_sharp")); }
        finally
        {
            await manager.UnloadAsync(); PyInterop.Log = log; HassPath.HassConfiguration = previous;
            try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The library <c>shared</c> at revision 1 and the graph <c>dependent</c> using its node, at revision 1.</summary>
    static async Task SharedAndDependent(HassSharpManager manager)
    {
        Result(await Request(manager, new { operation = "save", id = "shared", name = "Shared.cs", source = SharedSource, expectedRevision = 0 }));
        var catalog = Result(await Request(manager, new { operation = "catalog" }));
        var kind = catalog["catalog"]!["nodes"]!.AsObject().First(p => p.Key.StartsWith("library:shared:", StringComparison.Ordinal)).Key;
        var graph = G(N("use", kind, new { arguments = new { value = Lit("global::System.Int32", 9) } }));
        var saved = Result(await Visual(manager, new { operation = "save", id = "dependent", graph, expectedRevision = 0 }));
        Check(saved["active"]!.GetValue<bool>() && saved["document"]!["revision"]!.GetValue<long>() == 1, "fixture graph saved and active at revision 1");
    }

    static async Task<JsonArray> Listed(HassSharpManager manager) => Result(await Request(manager, new { operation = "list" }))["documents"]!.AsArray();
    static async Task<JsonNode> Graph(HassSharpManager manager, string id) => Result(await Visual(manager, new { operation = "read", id }));

    static async Task TwoDependentLibrariesImportTogether()
    {
        await Fresh(async (manager, _) =>
        {
            // In request order, not the ids' order: B.cs first.
            var imported = Result(await Request(manager, Import(Library("b", "B.cs", SourceB, 0), Library("a", "A.cs", SourceA, 0))));
            Check(imported["saved"]!.GetValue<bool>(), "import answers saved");
            var documents = imported["documents"]!.AsArray();
            Check(documents.Count == 2 && documents[0]!["id"]!.GetValue<string>() == "b" && documents[1]!["id"]!.GetValue<string>() == "a", "documents follow the request's order: " + documents.ToJsonString());
            Check(documents.All(d => d!["revision"]!.GetValue<long>() == 1), "each imported library is at revision 1");
            Check(documents[0]!["name"]!.GetValue<string>() == "B.cs" && documents[0]!["source"]!.GetValue<string>() == SourceB, "a document keeps its name and source");
            foreach (var member in new[] { "generation", "catalog", "automations", "invalid", "definitions", "invalidDefinitions" })
                Check(imported[member] != null, "import answers " + member + " as save does");
            Check(imported["document"] == null && imported["deleted"] == null, "import answers documents, not save's document or deleted");
            var listed = await Listed(manager);
            Check(listed.Select(d => d!["id"]!.GetValue<string>()).Order(StringComparer.Ordinal).SequenceEqual(["a", "b"]), "list shows both imported libraries");
            var catalog = Result(await Request(manager, new { operation = "catalog" }));
            Check(catalog["valid"]!.GetValue<bool>() && catalog["catalog"]!["nodes"]!.AsObject().Any(p => p.Key.StartsWith("library:b:", StringComparison.Ordinal)), "the dependent's node is in the saved catalog");
            Check(catalog["generation"]!.GetValue<string>() == imported["generation"]!.GetValue<string>(), "import answers the saved generation");
        });
        await Fresh(async (manager, _) =>
        {
            var alone = await Request(manager, new { operation = "save", id = "b", name = "B.cs", source = SourceB, expectedRevision = 0 });
            Check(ErrorCode(alone) == "validation", "saving the dependent alone is refused, so the pair compiled as one: " + alone.ToJsonString());
        });
    }

    static async Task ADependentImportsAfterItsDependencyIsStored()
    {
        await Fresh(async (manager, _) =>
        {
            Result(await Request(manager, new { operation = "save", id = "a", name = "A.cs", source = SourceA, expectedRevision = 0 }));
            var imported = Result(await Request(manager, Import(Library("b", "B.cs", SourceB, 0))));
            Check(imported["documents"]!.AsArray().Single()!["id"]!.GetValue<string>() == "b", "the dependent imports beside its stored dependency");
            Check((await Listed(manager)).Count == 2, "both libraries stored");
        });
        await Fresh(async (manager, root) =>
        {
            var refused = await Request(manager, Import(Library("b", "B.cs", SourceB, 0)));
            Check(ErrorCode(refused) == "validation", "the dependent alone is refused: " + refused.ToJsonString());
            var diagnostics = refused["error"]!["detail"]!["diagnostics"]!.AsArray();
            Check(diagnostics.Any(d => d!["sourceId"]!.GetValue<string>() == "b" && d["severity"]!.GetValue<int>() == 3), "an error diagnostic is in B.cs: " + diagnostics.ToJsonString());
            Check((await Listed(manager)).Count == 0, "nothing listed after a refused import");
            Check(!File.Exists(Path.Combine(root, "libraries", "b.json")), "nothing written after a refused import");
        });
    }

    static async Task OneInvalidLibraryWritesNothing()
    {
        await Fresh(async (manager, root) =>
        {
            await SharedAndDependent(manager);
            var before = (await Listed(manager)).ToJsonString();
            var refused = await Request(manager, Import(Library("a", "A.cs", SourceA, 0), Library("c", "C.cs", "public class C {", 0)));
            Check(ErrorCode(refused) == "validation", "one invalid library refuses the import: " + refused.ToJsonString());
            var detail = refused["error"]!["detail"]!;
            foreach (var member in new[] { "diagnostics", "automations", "invalid", "definitions" })
                Check(detail[member] != null, "the refusal carries " + member + " as save's does");
            Check(detail["diagnostics"]!.AsArray().Any(d => d!["sourceId"]!.GetValue<string>() == "c"), "the syntax error is reported in C.cs");
            Check((await Listed(manager)).ToJsonString() == before, "list unchanged");
            Check(!File.Exists(Path.Combine(root, "libraries", "a.json")) && !File.Exists(Path.Combine(root, "libraries", "c.json")), "neither library file written");
            var graph = await Graph(manager, "dependent");
            Check(graph["revision"]!.GetValue<long>() == 1 && graph["currentRevisionActive"]!.GetValue<bool>(), "the dependent graph stays at its revision");
        });
    }

    static async Task AStaleRevisionWritesNothing()
    {
        await Fresh(async (manager, root) =>
        {
            await SharedAndDependent(manager);
            var refused = await Request(manager, Import(Library("shared", "Shared.cs", SharedSource.Replace("=3", "=4"), 0), Library("extra", "Extra.cs", "public class Extra {}", 0)));
            Check(ErrorCode(refused) == "conflict", "a stale revision is a conflict: " + refused.ToJsonString());
            var shared = Result(await Request(manager, new { operation = "read", id = "shared" }));
            Check(shared["revision"]!.GetValue<long>() == 1 && shared["source"]!.GetValue<string>() == SharedSource, "the stored library is unchanged");
            Check((await Listed(manager)).Count == 1 && !File.Exists(Path.Combine(root, "libraries", "extra.json")), "the new library beside it is not written");
            Check((await Graph(manager, "dependent"))["revision"]!.GetValue<long>() == 1, "the dependent graph is not rebuilt");
        });
    }

    static async Task ImportRebuildsGraphsOnce()
    {
        await Fresh(async (manager, _) =>
        {
            await SharedAndDependent(manager);
            var changed = SharedSource.Replace("=3", "=4");
            var imported = Result(await Request(manager, Import(Library("shared", "Shared.cs", changed, 1), Library("extra", "Extra.cs", "public class Extra {}", 0))));
            var documents = imported["documents"]!.AsArray();
            Check(documents[0]!["id"]!.GetValue<string>() == "shared" && documents[0]!["revision"]!.GetValue<long>() == 2, "the overwritten library advances one revision");
            Check(documents[1]!["id"]!.GetValue<string>() == "extra" && documents[1]!["revision"]!.GetValue<long>() == 1, "the new library starts at revision 1");
            var automations = imported["automations"]!.AsArray();
            Check(automations.Count(a => a!["id"]!.GetValue<string>() == "dependent") == 1, "the dependent graph is rebuilt once: " + automations.ToJsonString());
            Check(automations.Single(a => a!["id"]!.GetValue<string>() == "dependent")!["active"]!.GetValue<bool>(), "the rebuilt graph activates");
            var graph = await Graph(manager, "dependent");
            Check(graph["revision"]!.GetValue<long>() == 2 && graph["activeRevision"]!.GetValue<long>() == 2 && graph["currentRevisionActive"]!.GetValue<bool>(), "the graph's revision advances by exactly 1 and it runs");
            Check(graph["source"]!.GetValue<string>().Contains(changed), "the rebuilt graph embeds the imported source");
        });
    }

    static async Task BadRequests()
    {
        await Fresh(async (manager, root) =>
        {
            async Task Refused(object request, string message)
            {
                var response = await Request(manager, request);
                Check(ErrorCode(response) == "invalid_request" && response["error"]!["detail"]!.GetValue<string>() == message, $"expected invalid_request '{message}': {response.ToJsonString()}");
            }
            await Refused(new { operation = "import", libraries = Array.Empty<object>() }, "libraries must list at least one library");
            await Refused(Import(Library("a", "A.cs", SourceA, 0), Library("b", "B.cs", SourceB, 0), Library("a", "A2.cs", SourceA, 0)), "Each library may be imported once: a");
            await Refused(Import(Library("a", "A.cs", SourceA, 0), Library("Bad Id", "B.cs", SourceB, 0)), "Invalid library ID");
            await Refused(Import(Library("a", "A.cs", SourceA, 0), Library("b", "nodes/B.cs", SourceB, 0)), "Invalid library name");
            var libraries = Path.Combine(root, "libraries");
            Check((await Listed(manager)).Count == 0 && (!Directory.Exists(libraries) || !Directory.EnumerateFiles(libraries).Any()), "a bad request writes nothing");
        });
    }
}

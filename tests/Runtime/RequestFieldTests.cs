using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

/// <summary>
/// The visual and node library requests answer a missing or wrongly typed field with a message that names it, never the
/// runtime's NullReference text; and the startup log says why a saved automation does not load.
/// </summary>
static class RequestFieldTests
{
    [RuntimeSuite("--request-fields", Order = 31)]
    public static async Task Run()
    {
        var previous = HassPath.HassConfiguration;
        var root = Path.Combine(Path.GetTempPath(), "request-fields-" + Guid.NewGuid());
        var log = PyInterop.Log;
        var messages = new List<(int Level, string Message)>();
        PyInterop.Log = (level, message) => { lock (messages) messages.Add((level, message)); };
        var manager = new HassSharpManager(root);
        try
        {
            HassPath.HassConfiguration = root;
            await manager.Init();
            async Task<JsonNode> Visual(string request) => JsonNode.Parse(await manager.VisualRequest(request))!;
            async Task<JsonNode> Library(string request) => JsonNode.Parse(await manager.NodeLibraryRequest(request))!;
            void Refused(JsonNode reply, string detail, string what)
            {
                Check(reply["ok"]!.GetValue<bool>() == false && reply["error"]!["code"]!.GetValue<string>() == "invalid_request"
                    && reply["error"]!["detail"]!.GetValue<string>().StartsWith(detail, StringComparison.Ordinal), $"{what}: " + reply.ToJsonString());
                Check(!reply.ToJsonString().Contains("Object reference", StringComparison.Ordinal), $"{what} answered with NullReference text: " + reply.ToJsonString());
            }
            var graph = G(N("log", "log", new { value = Lit("string", "hi") })).ToJsonString();
            Check((await Visual($$"""{"operation":"save","id":"a","expectedRevision":0,"graph":{{graph}}}"""))["ok"]!.GetValue<bool>(), "a save with every field works");

            // The visual API.
            Refused(await Visual($$"""{"operation":"save","id":"a","graph":{{graph}}}"""), "expectedRevision is required", "save without expectedRevision");
            Refused(await Visual("""{"operation":"delete","id":"a"}"""), "expectedRevision is required", "delete without expectedRevision");
            Refused(await Visual($$"""{"operation":"save","id":"a","expectedRevision":"1","graph":{{graph}}}"""), "expectedRevision must be a whole number", "expectedRevision as text");
            Refused(await Visual("""{"operation":"delete","id":"a","expectedRevision":1.5}"""), "expectedRevision must be a whole number", "a fractional expectedRevision");
            Refused(await Visual("""{"operation":"save","id":"a","expectedRevision":1}"""), "graph is required", "save without graph");
            Refused(await Visual("""{"operation":"preview","graph":[1]}"""), "graph must be an object", "graph as a list");
            Refused(await Visual("""{"operation":"read"}"""), "id is required", "read without id");
            Refused(await Visual("""{"operation":"read","id":7}"""), "id must be a string", "id as a number");
            Refused(await Visual("""{"id":"a"}"""), "operation is required", "no operation");
            Refused(await Visual("""{"operation":"run","id":"a"}"""), "triggerId is required", "run without triggerId");
            Refused(await Visual("""{"operation":"set_enabled","id":"a"}"""), "enabled is required", "set_enabled without enabled");
            Refused(await Visual("""{"operation":"set_enabled","id":"a","enabled":"no"}"""), "enabled must be true or false", "enabled as text");
            Refused(await Visual($$"""{"operation":"save","id":"a","expectedRevision":1,"graph":{{graph}},"enabled":1}"""), "enabled must be true or false", "save with enabled as a number");
            Refused(await Visual($$"""{"operation":"preview","graph":{{graph}},"deriveSignatures":"yes"}"""), "deriveSignatures must be true or false", "deriveSignatures as text");
            Refused(await Visual("""{"operation":"list","view":"home","todayUtc":5}"""), "todayUtc must be a string", "todayUtc as a number");
            Refused(await Visual("null"), "The request must be a JSON object", "a null request");

            // The node library API.
            const string source = "public static class Nodes { [Node(\"Use\")] public static int Use(int value) => value; }";
            Refused(await Library($$"""{"operation":"save","id":"lib","name":"Lib.cs","source":{{JsonSerializer.Serialize(source)}}}"""), "expectedRevision is required", "library save without expectedRevision");
            Refused(await Library("""{"operation":"delete","id":"lib","expectedRevision":true}"""), "expectedRevision must be a whole number", "expectedRevision as a flag");
            Refused(await Library("""{"operation":"save","id":"lib","expectedRevision":0,"name":"Lib.cs"}"""), "source is required", "library save without source");
            Refused(await Library("""{"operation":"save","id":"lib","expectedRevision":0,"name":3,"source":"x"}"""), "name must be a string", "name as a number");
            Refused(await Library("""{"operation":"read"}"""), "id is required", "library read without id");
            Refused(await Library("""{"operation":"preview","id":"lib","source":"x","workspaceVersion":"1","sources":[null]}"""), "Each item of sources must be an object", "a null item in sources");
            Refused(await Library("""{"operation":"preview","id":"lib","source":"x","workspaceVersion":"1","sources":{"id":"x"}}"""), "sources must be a list", "sources as an object");
            Refused(await Library("""{"operation":"completion","id":"lib","source":"x","workspaceVersion":"1","position":"3"}"""), "position must be a whole number", "position as text");
            Refused(await Library("""{"operation":"completion","id":"lib","source":"x","workspaceVersion":"1","generation":4}"""), "generation must be a string", "generation as a number");
            Refused(await Library("""{"operation":"custom_preview","nodeId":5,"source":"x"}"""), "nodeId must be a string", "nodeId as a number");
            Refused(await Library("""{"operation":"completion","custom":"yes","nodeId":"c","source":"x","workspaceVersion":"1"}"""), "custom must be true or false", "custom as text");
            Refused(await Library("[]"), "The request must be a JSON object", "a list as the request");
            Console.WriteLine("PASS request fields: a missing or wrongly typed field is answered with its name, never NullReference text");

            // A null reference inside HassSharp (every field is read by name, so it is not the request's) is an internal
            // error, logged with the exception: here a stored graph without nodes, which the running revision still matches.
            var stored0 = Path.Combine(root, ".storage", "hass_sharp", "visual", "a.json");
            var saved0 = JsonNode.Parse(File.ReadAllText(stored0))!;
            var withNodes = File.ReadAllText(stored0);
            saved0["graph"]!.AsObject().Remove("nodes");
            File.WriteAllText(stored0, saved0.ToJsonString());
            lock (messages) messages.Clear();
            var internalReply = await Visual("""{"operation":"run","id":"a","triggerId":"t"}""");
            Check(internalReply["ok"]!.GetValue<bool>() == false && internalReply["error"]!["code"]!.GetValue<string>() == "internal"
                && internalReply["error"]!["detail"]!.GetValue<string>() == ApiRequest.Internal, "a defect inside HassSharp is answered as internal: " + internalReply.ToJsonString());
            Check(!internalReply.ToJsonString().Contains("Object reference", StringComparison.Ordinal), "the internal answer has no NullReference text");
            string[] logged; lock (messages) logged = messages.Where(m => m.Level >= (int)PyLogLevel.Error).Select(m => m.Message).ToArray();
            Check(logged.Any(m => m.Contains("Visual request failed inside HassSharp", StringComparison.Ordinal) && m.Contains("NullReferenceException", StringComparison.Ordinal)),
                "the internal error is logged with its exception: " + string.Join(" | ", logged));
            File.WriteAllText(stored0, withNodes);
            Console.WriteLine("PASS internal errors: a null reference inside HassSharp is logged with its exception and answered internal, not as the request's fault");

            // The startup log names the automation and says why it does not load, without the exception's type or stack.
            await manager.UnloadAsync();
            var file = Path.Combine(root, ".storage", "hass_sharp", "visual", "a.json");
            var stored = JsonNode.Parse(File.ReadAllText(file))!;
            stored["graph"]!["name"] = "Hall light";
            stored["graph"]!["imports"] = new JsonArray("System.Linq");
            stored["graph"]!["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "log")!["config"]!["value"] = new JsonObject { ["kind"] = "variable", ["name"] = "reading" };
            File.WriteAllText(file, stored.ToJsonString());
            lock (messages) messages.Clear();
            await manager.Init();
            string[] errors; lock (messages) errors = messages.Where(m => m.Level >= (int)PyLogLevel.Error).Select(m => m.Message).ToArray();
            var line = errors.SingleOrDefault(m => m.Contains("Hall light", StringComparison.Ordinal));
            Check(line != null && line.StartsWith("Cannot load automation \"Hall light\" (a): 2 problems: ", StringComparison.Ordinal)
                && line.Contains("Usings are no longer part of an automation", StringComparison.Ordinal)
                && line.Contains("Message reads a variable, and variables are gone; connect a wire to it instead (step Log)", StringComparison.Ordinal),
                "the startup log names the automation and its problems: " + string.Join(" | ", errors));
            Check(!line!.Contains("Exception", StringComparison.Ordinal) && !line.Contains("   at ", StringComparison.Ordinal) && line.Count(c => c == '\n') <= 1,
                "the startup log line has no exception type or stack: " + line);
            Check(HassSharpManager.LoadFailure(new VisualDocument(1, "b", 1, new JsonObject { ["name"] = "Many" }, "", []),
                    new VisualValidationException(new VisualPreview(false, "", [], [.. Enumerable.Range(1, 5).Select(i => new VisualDiagnostic(null, "Problem " + i))])), VisualCatalogSnapshot.BuiltIn)
                    == "Cannot load automation \"Many\" (b): 5 problems: Problem 1; Problem 2; Problem 3; and 2 more", "the log names the first problems and counts the rest");
            Check(HassSharpManager.LoadFailure(new VisualDocument(1, "c", 1, new JsonObject(), "", []), new CompilationErrorException(["(1,1): error CS1002: ; expected"]), VisualCatalogSnapshot.BuiltIn)
                    == "Cannot load automation c: its code does not compile: ; expected (CS1002)", "a compile failure is named with its errors");
            // A compiler error on a line the source map gives a step is named with that step.
            var mapped = new VisualDocument(1, "m", 1, G(N("log", "log", new { value = Lit("string", "hi") })), "", [new("log", 10, 12)]);
            var located = HassSharpManager.LoadFailure(mapped, new CompilationErrorException(["/config/.storage/hass_sharp/visual/visual_m.cs(11,5): error CS0103: The name 'x' does not exist in the current context", "(40,1): error CS1513: } expected"]), VisualCatalogSnapshot.BuiltIn);
            Check(located == "Cannot load automation \"Execution\" (m): its code does not compile: The name 'x' does not exist in the current context (CS0103, step Log); } expected (CS1513)", "compiler errors name their step: " + located);
            // Each problem is cut at a length, so one long message cannot fill the log.
            var longProblem = HassSharpManager.LoadFailure(new VisualDocument(1, "l", 1, new JsonObject(), "", []),
                new VisualValidationException(new VisualPreview(false, "", [], [new VisualDiagnostic(null, new string('w', 5000))])), VisualCatalogSnapshot.BuiltIn);
            Check(longProblem.Length < HassSharpManager.LoggedProblemLength + 60 && longProblem.EndsWith("…", StringComparison.Ordinal), "a long problem is cut: " + longProblem.Length);
            // Describing a failure that fails itself still logs a line, and does not throw (Init goes on with the next automation).
            lock (messages) messages.Clear();
            HassSharpManager.ReportLoadFailure("broken", () => throw new InvalidOperationException("describing failed"));
            string[] plain; lock (messages) plain = messages.Where(m => m.Level >= (int)PyLogLevel.Error).Select(m => m.Message).ToArray();
            Check(plain.SequenceEqual(["Cannot load automation broken; see the debug log for why"]), "a failure to describe the failure is logged plainly: " + string.Join(" | ", plain));
            Console.WriteLine("PASS load failure log: the automation's name and its first problems, with their steps, on one line");
        }
        finally
        {
            await manager.UnloadAsync();
            PyInterop.Log = log;
            HassPath.HassConfiguration = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}

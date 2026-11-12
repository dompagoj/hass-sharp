using HassSharp;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

static class NodeLibraryStoreTests
{
    static void Check(bool ok, string message) => VisualExecutionTests.Check(ok, message);
    static async Task<JsonObject> Request(HassSharpManager manager, object request)
    {
        var method = typeof(HassSharpManager).GetMethod("NodeLibraryRequest", [typeof(string)]);
        Check(method != null, "manager exposes shared node-library application boundary");
        return JsonNode.Parse(await (Task<string>)method!.Invoke(manager, [JsonSerializer.Serialize(request)])!)!.AsObject();
    }
    static JsonNode Result(JsonObject response) { Check(response["ok"]!.GetValue<bool>(), response.ToJsonString()); return response["result"]!; }
    static async Task<JsonObject> Visual(HassSharpManager m, object r) => JsonNode.Parse(await m.VisualRequest(JsonSerializer.Serialize(r)))!.AsObject();
    public static async Task Run()
    {
        await RunOrderingReview();
        var previous = HassPath.HassConfiguration;
        var path = Path.Combine(Path.GetTempPath(), "library-store-" + Guid.NewGuid());
        var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
        var manager = new HassSharpManager(path);
        try
        {
            await manager.Init([]);
            var initial = Result(await Request(manager, new { operation = "catalog" }));
            Check(initial["catalog"]!["types"]!.AsObject().Count > 0, "empty saved catalog includes Roslyn types");
            const string source = "public static class Nodes { [Node(\"Use\")] public static int Use(int value=3) => value; }";
            var saved = Result(await Request(manager, new { operation="save", id="shared", name="Shared.cs", source, expectedRevision=0 }));
            Check(saved["document"]!["revision"]!.GetValue<long>() == 1, "library starts at revision one");
            var catalog = Result(await Request(manager,new {operation="catalog"}));
            var kind = catalog["catalog"]!["nodes"]!.AsObject().First(p=>p.Key.StartsWith("library:")).Key;
            var graph = G(N("use", kind, new {arguments=new {value=Lit("global::System.Int32", 9)}}));
            var visual = Result(await Visual(manager,new{operation="save",id="dependent",graph,expectedRevision=0}));
            Check(visual["active"]!.GetValue<bool>(), "library-backed graph activates");
            var rejected = await Request(manager,new{operation="save",id="shared",name="Shared.cs",source=source.Replace("value", "renamed"),expectedRevision=1});
            Check(!rejected["ok"]!.GetValue<bool>() && rejected.ToJsonString().Contains("use") && rejected.ToJsonString().Contains("value"), "optional parameter rename rejects stale literal input before persistence");
            Check(Result(await Request(manager,new{operation="read",id="shared"}))["revision"]!.GetValue<long>()==1,"rejected edit preserves library");
            Check(! (await Request(manager,new{operation="delete",id="shared",expectedRevision=1}))["ok"]!.GetValue<bool>(),"referenced deletion rejected");
            var renamed = Result(await Request(manager,new{operation="save",id="shared",name="Renamed.cs",source,expectedRevision=1}));
            Check(renamed["document"]!["id"]!.GetValue<string>()=="shared", "rename preserves identity");
            var read = Result(await Visual(manager,new{operation="read",id="dependent"}));
            Check(read["revision"]!.GetValue<long>()==2 && read["activeRevision"]!.GetValue<long>()==2, "dependent revision and active identity advance");
            Check(read["source"]!.GetValue<string>().Contains(source), "artifact embeds exact source snapshot");
            var race = await Task.WhenAll(Request(manager,new{operation="save",id="shared",name="Race.cs",source,expectedRevision=2}), Request(manager,new{operation="save",id="shared",name="Race2.cs",source,expectedRevision=2}));
            Check(race.Count(r=>r["ok"]!.GetValue<bool>())==1,"competing revisions serialize");
            var currentGraph = Result(await Visual(manager,new{operation="read",id="dependent"}));
            var graphRevision = currentGraph["revision"]!.GetValue<long>();
            var competing = await Task.WhenAll(Request(manager,new{operation="save",id="shared",name="Concurrent.cs",source,expectedRevision=3}), Visual(manager,new{operation="save",id="dependent",graph,expectedRevision=graphRevision}));
            Check(competing[0]["ok"]!.GetValue<bool>(),"library side of graph/library race commits");
            var finalGraph=Result(await Visual(manager,new{operation="read",id="dependent"}));
            Check(finalGraph["source"]!.GetValue<string>().Contains(source),"simultaneous graph save retains fixed library snapshot");
            // Arbitrary library CLR types may derive from Automation without becoming visual roots.
            const string trap = "public class Unrelated : Automation { public Unrelated() { throw new Exception(\"library instantiated\"); } public override AutomationConfiguration Configure() => throw new Exception(\"library configured\"); }";
            var trapped=Result(await Request(manager,new{operation="save",id="trap",name="Trap.cs",source=trap,expectedRevision=0}));
            Check(trapped["automations"]!.AsArray().All(a=>a!["active"]!.GetValue<bool>()),"only designated visual root activates on dependent rebuild");
            await manager.UnloadAsync(); await manager.Init([]);
            Check(Result(await Visual(manager,new{operation="read",id="dependent"}))["active"]!.GetValue<bool>(),"reload ignores Automation-derived library helper");
            // A saved generation can fail activation while the prior runtime remains active.
            var failureSource = source.Replace("public static class Nodes {", "public static class Nodes { public static bool Fail => false;");
            Result(await Request(manager,new{operation="save",id="shared",name="Shared.cs",source=failureSource,expectedRevision=4}));
            var beforeFailure=Result(await Visual(manager,new{operation="read",id="dependent"}));
            var failingGraph=graph.DeepClone().AsObject(); failingGraph["members"]="public VisualAutomation() { if(NodeLib.Nodes.Fail) throw new Exception(\"activation blocked\"); }";
            Result(await Visual(manager,new{operation="save",id="dependent",graph=failingGraph,expectedRevision=beforeFailure["revision"]!.GetValue<long>()}));
            var runningBefore=Result(await Visual(manager,new{operation="read",id="dependent"}));
            var failedActivation=Result(await Request(manager,new{operation="save",id="shared",name="Shared.cs",source=failureSource.Replace("Fail => false", "Fail => true"),expectedRevision=5}));
            Check(failedActivation["automations"]![0]!["active"]!.GetValue<bool>()==false,"post-commit activation failure visible");
            var current=Result(await Visual(manager,new{operation="read",id="dependent"}));
            Check(current["active"]!.GetValue<bool>() && !current["currentRevisionActive"]!.GetValue<bool>(),"old generation remains active but cannot masquerade as current");
            Check(current["activeRevision"]!.GetValue<long>()==runningBefore["revision"]!.GetValue<long>() && current["revision"]!.GetValue<long>()>current["activeRevision"]!.GetValue<long>(),"saved and running revision identities remain separate");
            // Indirect helper edits and advanced C# are validated against the whole candidate.
            Result(await Request(manager,new{operation="save",id="helper",name="Helper.cs",source="public static class Helper { public static int Count => 1; }",expectedRevision=0}));
            var withHelper=failureSource.Replace("=> value;", "=> Helper.Count + value;");
            Result(await Request(manager,new{operation="save",id="shared",name="Shared.cs",source=withHelper,expectedRevision=6}));
            var helperDelete=await Request(manager,new{operation="delete",id="helper",expectedRevision=1});
            Check(!helperDelete["ok"]!.GetValue<bool>() && helperDelete.ToJsonString().Contains("shared"),"deletion rejects indirect cross-file helper dependency");
            var helperEdit=await Request(manager,new{operation="save",id="helper",name="Helper.cs",source="public static class Helper { }",expectedRevision=1});
            Check(!helperEdit["ok"]!.GetValue<bool>(),"helper signature edit rejected before commit");
            var helperGraph=G(N("code","custom",new{code="var count = NodeLib.Helper.Count;"}));
            Result(await Visual(manager,new{operation="save",id="advanced",graph=helperGraph,expectedRevision=0}));
            Result(await Request(manager,new{operation="save",id="shared",name="Shared.cs",source=failureSource,expectedRevision=7}));
            var advancedDelete=await Request(manager,new{operation="delete",id="helper",expectedRevision=1});
            Check(!advancedDelete["ok"]!.GetValue<bool>() && advancedDelete.ToJsonString().Contains("advanced") && advancedDelete.ToJsonString().Contains("code"),"advanced C# dependency mapped to graph and node");
            // Saved versus draft catalogs, stale generations and other-file unsaved snapshots.
            var beforeEntityRefresh=Result(await Request(manager,new{operation="catalog"}))["generation"]!.GetValue<string>();
            manager.GenerateHassEntities(["light.updated"]);
            Check(Result(await Request(manager,new{operation="catalog"}))["generation"]!.GetValue<string>()!=beforeEntityRefresh,"entity-symbol refresh invalidates the saved workspace generation");
            var savedCatalog=Result(await Request(manager,new{operation="catalog"}));
            var draft=Result(await Request(manager,new{operation="preview",id="shared",source,workspaceVersion="client-42",generation=savedCatalog["generation"]!.GetValue<string>(),sources=new[]{new{id="unsaved",source="public record Draft(int Value);"}}}));
            Check(!draft["saved"]!.GetValue<bool>() && draft["workspaceVersion"]!.GetValue<string>()=="client-42", "draft catalog carries client workspace identity");
            Check(Result(await Request(manager,new{operation="catalog"}))["generation"]!.GetValue<string>()==savedCatalog["generation"]!.GetValue<string>(),"preview cannot publish saved catalog");
            Check((await Request(manager,new{operation="completion",id="shared",source,position=0,workspaceVersion="old",generation="stale"}))["error"]!["code"]!.GetValue<string>()=="conflict","stale saved catalog request rejected");
            var malformed=Result(await Request(manager,new{operation="preview",id="broken",source="// 😀\npublic class Broken { int Value = missing; }",workspaceVersion="malformed"}));
            Check(!malformed["data"]!["valid"]!.GetValue<bool>() && malformed["data"]!["diagnostics"]!.AsArray().Any(d=>d!["severity"]!.GetValue<int>()==3 && d["sourceId"]!.GetValue<string>()=="broken" && d["startLine"]!.GetValue<int>()==2),"malformed unsaved diagnostics map to selected unwrapped file");
            var firstDraft=Result(await Request(manager,new{operation="completion",id="editing",source="public class Editing { void Run() { Other. } }",position=41,workspaceVersion="first",sources=new[]{new{id="other",source="public static class Other { public static int First; }"}}}));
            var secondDraft=Result(await Request(manager,new{operation="completion",id="editing",source="public class Editing { void Run() { Other. } }",position=41,workspaceVersion="second",sources=new[]{new{id="other",source="public static class Other { public static int Second; }"}}}));
            Check(firstDraft["sourceGeneration"]!.GetValue<string>()!=secondDraft["sourceGeneration"]!.GetValue<string>(),"other-file edits change workspace identity even with unchanged selected source");
            await manager.UnloadAsync();
            var cancelled=await Request(manager,new{operation="completion",id="shared",source,position=0,workspaceVersion="unloaded"});
            Check(cancelled["error"]!["code"]!.GetValue<string>()=="cancelled","manager unload cancels language lifetime");
            await manager.Init([]);
            await TransactionRecovery(manager,path);
            Console.WriteLine("PASS node-library persistence/revisions/dependencies/concurrency/activation identity/catalog generations/recovery");
        }
        finally { await manager.UnloadAsync(); PyInterop.Log=log; HassPath.HassConfiguration=previous; }
    }
    public static async Task RunOrderingReview(string? scenario = null)
    {
        foreach (var change in scenario == null ? new[] { "edit", "create", "prefix-create" } : new[] { scenario })
        {
            var previous = HassPath.HassConfiguration;
            var log = PyInterop.Log; PyInterop.Log = (_, _) => { };
            var path = Path.Combine(Path.GetTempPath(), "library-ordering-" + Guid.NewGuid());
            var manager = new HassSharpManager(path);
            try
            {
                await manager.Init([]);
                const string sourceA = "public static class A { public static int Value => 1; }";
                if (change == "edit")
                    Result(await Request(manager, new { operation = "save", id = "a", name = "A.cs", source = sourceA, expectedRevision = 0 }));
                var otherId = change == "prefix-create" ? "a-helper" : "b";
                Result(await Request(manager, new { operation = "save", id = otherId, name = "B.cs", source = "public class B {}", expectedRevision = 0 }));
                Result(await Visual(manager, new { operation = "save", id = "ordered", graph = G(), expectedRevision = 0 }));
                Result(await Request(manager, new { operation = "save", id = "a", name = "A.cs", source = sourceA.Replace("=> 1", "=> 2"), expectedRevision = change == "edit" ? 1 : 0 }));
                var before = Result(await Visual(manager, new { operation = "read", id = "ordered" }));
                var source = before["source"]!.GetValue<string>();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
                Check(before["revision"]!.GetValue<long>() == 2 && before["currentRevisionActive"]!.GetValue<bool>(), change + ": committed revision activates before reload");
                Check(before["activeSourceHash"]!.GetValue<string>() == hash, change + ": running artifact exactly matches saved source before reload");
                await manager.UnloadAsync(); await manager.Init([]);
                var after = Result(await Visual(manager, new { operation = "read", id = "ordered" }));
                Check(after["active"]!.GetValue<bool>() && after["activationError"] == null, change + ": reload successfully activates the graph");
                Check(after["revision"]!.GetValue<long>() == 2 && after["activeRevision"]!.GetValue<long>() == 2, change + ": reload preserves saved and active revision");
                Check(after["source"]!.GetValue<string>() == source, change + ": reload preserves exact saved artifact");
                Check(after["activeSourceHash"]!.GetValue<string>() == hash, change + ": running source hash survives reload");
                Check(after["currentRevisionActive"]!.GetValue<bool>(), change + ": saved revision remains current after reload");
                Console.WriteLine("PASS library ordering reload identity: " + change);
            }
            finally { await manager.UnloadAsync(); PyInterop.Log = log; HassPath.HassConfiguration = previous; }
        }
    }

    static async Task TransactionRecovery(HassSharpManager manager,string path)
    {
        var root=Path.Combine(path,".storage","hass_sharp");
        var graphPath=Path.Combine(root,"visual","dependent.json");
        var original=File.ReadAllText(graphPath);
        File.WriteAllText(graphPath,"corrupt saved graph");
        var corruptSave=await Request(manager,new{operation="save",id="should-not-save",name="Denied.cs",source="public class Fine {}",expectedRevision=0});
        Check(!corruptSave["ok"]!.GetValue<bool>(),"library mutation cannot silently skip an unreadable dependent document");
        Check(!File.Exists(Path.Combine(root,"libraries","should-not-save.json")),"unreadable dependency prevents library persistence");
        File.WriteAllText(graphPath,original);
        var blocked=Path.Combine(root,"libraries","blocked.json"); Directory.CreateDirectory(blocked);
        try
        {
            new AuthoringTransaction(root).Commit(new Dictionary<string,string?> { [graphPath]="partial", [blocked]="new library" });
            throw new Exception("expected storage failure");
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        Check(File.ReadAllText(graphPath)==original,"mid-transaction storage failure restores earlier replaced graph");
        Check(!(await Request(manager,new{operation="catalog"}))["ok"]!.GetValue<bool>(),"unrecoverable journal fails closed before authoring read");
        Directory.Delete(blocked);
        Result(await Request(manager,new{operation="catalog"}));
        Check(!File.Exists(Path.Combine(root,"authoring-undo.json")),"recovery removes journal after successful rollback");
        File.WriteAllText(Path.Combine(root,"authoring-undo.json"),JsonSerializer.Serialize(new Dictionary<string,string?>{["visual/dependent.json"]=original}));
        File.WriteAllText(graphPath,"interrupted transaction");
        await manager.UnloadAsync(); await manager.Init([]);
        Check(File.ReadAllText(graphPath)==original,"startup recovers interrupted transaction before reading graph");
    }

}

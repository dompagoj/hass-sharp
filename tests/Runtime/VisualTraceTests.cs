using HassSharp;
using System.Text.Json;
using System.Text.Json.Nodes;
using static VisualExecutionTests;

// Per-node trace recorder (contract C10): events, ports, values, frames, caps, flushes and the untraced path.
static class VisualTraceTests
{
    sealed record Write(JsonObject Doc, int Count, bool Truncated, bool Final, bool Immediate, int Bytes);

    sealed class Sink : IRunTraceSink
    {
        readonly Lock gate = new();
        readonly List<Write> writes = [];
        public bool Throws;
        void IRunTraceSink.Write(string json, int eventCount, bool truncated, bool final, bool immediate)
        {
            if (Throws) throw new InvalidOperationException("sink unavailable");
            var write = new Write(JsonNode.Parse(json)!.AsObject(), eventCount, truncated, final, immediate, System.Text.Encoding.UTF8.GetByteCount(json));
            lock (gate) writes.Add(write);
        }
        public Write[] Writes { get { lock (gate) return writes.ToArray(); } }
        /// <summary>The final document: written once, and nothing after it.</summary>
        public JsonObject Final
        {
            get
            {
                var all = Writes;
                Check(all.Length > 0 && all[^1].Final && all.Count(w => w.Final) == 1, "the final write is not the single last write: " + string.Join(",", all.Select(w => w.Final)));
                var doc = all[^1].Doc;
                Check(all[^1].Count == Events(doc).Count && all[^1].Truncated == doc["truncated"]!.GetValue<bool>(), "sink count/truncated disagree with the document");
                Check(Events(doc).All(e => S(e!["status"]) != "running"), "an event is still running in the final document: " + doc.ToJsonString());
                // Documents are stored in the order they were built: a later one never has fewer events or a lower seq.
                var partial = all[..^1].Select(w => (Events(w.Doc).Count, Seq: Events(w.Doc).Select(e => e!["seq"]!.GetValue<int>()).DefaultIfEmpty(0).Max())).ToArray();
                for (var i = 1; i < partial.Length; i++)
                    Check(partial[i].Count >= partial[i - 1].Count && partial[i].Seq >= partial[i - 1].Seq, $"document {i} is older than document {i - 1}: {partial[i]} after {partial[i - 1]}");
                return doc;
            }
        }
    }

    sealed class Loaded(Automation instance, Action unload) : IDisposable
    {
        public InvocationContext? Context;
        public Task Start(IRunTraceSink? sink, CancellationToken token = default, JsonElement? payload = null, bool context = true)
        {
            instance._cancellationToken.Value = token;
            var prior = InvocationContext.Current;
            Context = context ? new InvocationContext("run-" + Guid.NewGuid().ToString("N"), InvocationCause.Manual, null, payload ?? TriggerPayload.Manual()) { TraceSink = sink } : null;
            InvocationContext.Current = Context;
            try { return (Task)instance.GetType().GetMethod("Run_74")!.Invoke(instance, null)!; }
            finally { InvocationContext.Current = prior; }
        }
        public void Dispose() => unload();
    }

    static async Task<Loaded> Load(JsonObject graph, VisualCatalogSnapshot? snapshot = null)
    {
        Diagnostics.GenerateHassEntities(["sensor.test"]);
        var preview = snapshot == null ? new VisualGenerator(Diagnostics).Preview(graph) : new VisualGenerator(Diagnostics).Preview(graph, snapshot);
        Check(preview.Valid, "graph compiles: " + JsonSerializer.Serialize(preview.Diagnostics));
        var compiled = await new CodeCompiler(Diagnostics).CompileSingleFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".cs"), preview.Source, true);
        return new((Automation)Activator.CreateInstance(compiled.Assembly.GetType("VisualAutomation")!)!, compiled.Unload);
    }

    static async Task<(Sink Sink, JsonObject Final, Loaded Run)> Trace(JsonObject graph, bool fails = false, JsonElement? payload = null)
    {
        using var loaded = await Load(graph);
        var sink = new Sink();
        try { await loaded.Start(sink, default, payload).WaitAsync(TimeSpan.FromSeconds(30)); Check(!fails, "the run should have failed"); }
        catch (Exception) when (fails) { }
        return (sink, sink.Final, loaded);
    }

    static JsonObject Unset() => new() { ["kind"] = "unset" };
    static JsonObject C(string id, string code) => N(id, "custom", new { code });
    static JsonObject Log(string id, string value) => N(id, "log", new { value = Lit("string", value) });
    static JsonArray Events(JsonObject doc) => doc["events"]!.AsArray();
    static JsonObject[] Of(JsonObject doc, string node) => Events(doc).Select(e => e!.AsObject()).Where(e => e["node"]!.GetValue<string>() == node).ToArray();
    static JsonObject One(JsonObject doc, string node) { var found = Of(doc, node); Check(found.Length == 1, $"expected one event of {node}, got {found.Length}"); return found[0]; }
    static string S(JsonNode? node) => node?.GetValue<string>() ?? "<absent>";
    static bool Same(JsonNode? actual, string expected) => JsonNode.DeepEquals(actual, JsonNode.Parse(expected));
    static int Depth(JsonNode? node) => node switch { JsonObject o => 1 + o.Select(p => Depth(p.Value)).DefaultIfEmpty(0).Max(), JsonArray a => 1 + a.Select(Depth).DefaultIfEmpty(0).Max(), _ => 0 };

    static async Task Until(Func<bool> condition, string message)
    {
        for (var i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(20); }
        throw new Exception("timed out: " + message);
    }

    // The document and its events have exactly the C10 fields.
    static void Shape(JsonObject doc)
    {
        Check(doc.Select(p => p.Key).SequenceEqual(["format", "runId", "rootNodeId", "startedUtc", "complete", "truncated", "dropped", "trigger", "events", "summary"]), "document fields: " + string.Join(",", doc.Select(p => p.Key)));
        Check(doc["format"]!.GetValue<int>() == 1 && doc["trigger"] is JsonObject && DateTime.TryParse(S(doc["startedUtc"]), out _), "document header");
        string[] allowed = ["seq", "node", "frame", "t", "ms", "status", "port", "inputs", "outputs", "error", "trunc"];
        foreach (var e in Events(doc).Select(e => e!.AsObject()))
        {
            Check(e.All(p => allowed.Contains(p.Key)) && e.Select(p => Array.IndexOf(allowed, p.Key)).SequenceEqual(e.Select(p => Array.IndexOf(allowed, p.Key)).Order()), "event fields: " + e.ToJsonString());
            Check(new[] { "ok", "error", "failed", "running", "cancelled", "stopped" }.Contains(S(e["status"])) && (e["ms"] == null) == (S(e["status"]) == "running"), "event status/ms: " + e.ToJsonString());
            foreach (var side in new[] { "inputs", "outputs" })
                foreach (var (_, value) in e[side] as JsonObject ?? [])
                    Check(value!.AsObject().All(p => p.Key is "type" or "v" or "trunc" or "redacted" or "src") && value["type"] is JsonValue && (value["src"] == null || value["src"]!.AsObject().Select(p => p.Key).SequenceEqual(["node", "port"])), "value fields: " + value.ToJsonString());
        }
        var seqs = Events(doc).Select(e => e!["seq"]!.GetValue<int>()).ToArray();
        Check(seqs.SequenceEqual(seqs.Order()) && seqs.Distinct().Count() == seqs.Length, "events are not in start order");
    }

    [RuntimeSuite("--visual-trace", Order = 19)]
    public static async Task Run()
    {
        var previousPath = HassPath.HassConfiguration; HassPath.HassConfiguration = Path.Combine(Path.GetTempPath(), "visual-trace-" + Guid.NewGuid());
        var previousLog = PyInterop.Log; var messages = new System.Collections.Concurrent.ConcurrentQueue<string>(); PyInterop.Log = (_, m) => messages.Enqueue(m);
        var failures = new List<string>();
        async Task Test(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine("PASS visual trace: " + name); }
            catch (Exception ex) { failures.Add(name + ": " + ex); }
        }
        try
        {
            await Test("linear run records events, ports, inputs with src, outputs and timings", async () =>
            {
                var graph = G(N("a", "not", new { value = Lit("boolean", false) }), N("b", "and", new { left = Unset(), right = Lit("boolean", true) }), N("l", "log", new { value = Unset() }));
                graph["edges"]!.AsArray().Add(D("a", "b", "left")); graph["edges"]!.AsArray().Add(D("b", "l", "value"));
                var (sink, doc, run) = await Trace(graph);
                Shape(doc);
                Check(Events(doc).Select(e => S(e!["node"])).SequenceEqual(["t", "a", "b", "l"]), "event order: " + doc.ToJsonString());
                Check(S(doc["runId"]) == run.Context!.RunId && S(doc["rootNodeId"]) == "t" && doc["complete"]!.GetValue<bool>() && !doc["truncated"]!.GetValue<bool>() && doc["dropped"]!.GetValue<int>() == 0, "header: " + doc.ToJsonString());
                Check(Same(doc["trigger"], """{"platform":"manual"}"""), "trigger payload");
                foreach (var e in Events(doc).Select(e => e!.AsObject()))
                    Check(S(e["status"]) == "ok" && S(e["port"]) == "next" && S(e["frame"]) == "" && e["t"]!.GetValue<double>() >= 0 && e["ms"]!.GetValue<double>() >= 0 && e["error"] == null, "event: " + e.ToJsonString());
                var times = Events(doc).Select(e => e!["t"]!.GetValue<double>()).ToArray();
                Check(times.SequenceEqual(times.Order()), "start times decrease");
                Check(Same(One(doc, "t")["outputs"], """{"data":{"type":"object","v":{"platform":"manual"}}}""") && One(doc, "t")["inputs"] == null, "trigger outputs: " + One(doc, "t").ToJsonString());
                Check(Same(One(doc, "a")["inputs"], """{"value":{"type":"boolean","v":false}}""") && Same(One(doc, "a")["outputs"], """{"value":{"type":"boolean","v":true}}"""), "literal input and output: " + One(doc, "a").ToJsonString());
                Check(Same(One(doc, "b")["inputs"], """{"left":{"type":"boolean","v":true,"src":{"node":"a","port":"value"}},"right":{"type":"boolean","v":true}}"""), "wired input src: " + One(doc, "b").ToJsonString());
                Check(Same(One(doc, "l")["inputs"], """{"value":{"type":"boolean","v":true,"src":{"node":"b","port":"value"}}}""") && One(doc, "l")["outputs"] == null, "log input: " + One(doc, "l").ToJsonString());
                Check(Same(doc["summary"], """{"t":{"count":1,"failed":0,"last":"ok"},"a":{"count":1,"failed":0,"last":"ok"},"b":{"count":1,"failed":0,"last":"ok"},"l":{"count":1,"failed":0,"last":"ok"}}"""), "summary: " + doc["summary"]!.ToJsonString());
                Check(sink.Writes.Length == 1, "a short run without waits writes only the final document: " + sink.Writes.Length);
            });

            await Test("error-port recovery records error then continues", async () =>
            {
                var graph = G(C("x", "throw new InvalidOperationException(\"boom\");"), Log("r", "recovered"));
                graph["edges"] = new JsonArray(E("t", "x"), E("x", "r", "error"));
                var (_, doc, _) = await Trace(graph);
                Shape(doc);
                var x = One(doc, "x");
                Check(S(x["status"]) == "error" && S(x["port"]) == "error" && Same(x["error"], """{"type":"InvalidOperationException","message":"boom"}"""), "recovered node: " + x.ToJsonString());
                Check(S(One(doc, "r")["status"]) == "ok" && Same(doc["summary"]!["x"], """{"count":1,"failed":1,"last":"error"}"""), "continuation and summary: " + doc.ToJsonString());
            });

            await Test("failing run records failed", async () =>
            {
                var graph = G(Log("a", "before"), C("x", "throw new InvalidOperationException(\"fatal\");"), Log("never", "after"));
                var (_, doc, _) = await Trace(graph, fails: true);
                Shape(doc);
                var x = One(doc, "x");
                Check(S(x["status"]) == "failed" && x["port"] == null && S(x["error"]!["message"]) == "fatal" && x["ms"] != null, "failed node: " + x.ToJsonString());
                Check(doc["complete"]!.GetValue<bool>() && Of(doc, "never").Length == 0 && Same(doc["summary"]!["x"], """{"count":1,"failed":1,"last":"failed"}"""), "failed run document: " + doc.ToJsonString());
            });

            await Test("stop node records stopped and the run is Stopped", async () =>
            {
                var (_, doc, run) = await Trace(G(Log("l", "before stop"), N("x", "stop")));
                Shape(doc);
                Check(S(One(doc, "x")["status"]) == "stopped" && One(doc, "x")["port"] == null && S(One(doc, "l")["status"]) == "ok" && run.Context!.Stopped, "stop: " + doc.ToJsonString());
            });

            await Test("parallel branches and loop iterations under concurrency get their frames", async () =>
            {
                var nodes = new List<JsonObject> { N("p", "parallel", new { branches = Enumerable.Range(0, 6).Select(i => "b" + i).ToArray() }), Log("join", "joined") };
                var edges = new JsonArray(E("t", "p"), E("p", "join"));
                for (var i = 0; i < 6; i++)
                {
                    nodes.Add(N("r" + i, "repeat", new { count = Lit("number", 20) }));
                    nodes.Add(C("c" + i, i % 2 == 0 ? "await global::System.Threading.Tasks.Task.Yield();" : "await global::System.Threading.Tasks.Task.Delay(60);"));
                    edges.Add(E("p", "r" + i, "branch:b" + i)); edges.Add(E("r" + i, "c" + i, "body"));
                }
                var graph = G([.. nodes]); graph["edges"] = edges;
                var (sink, doc, _) = await Trace(graph);
                Shape(doc);
                // Sink.Final checked that the partial documents never go back; there must be several to compare.
                var partial = sink.Writes.Where(w => !w.Final).ToArray();
                Check(partial.Length >= 2 && partial.All(w => w.Doc["complete"]!.GetValue<bool>() == false), "partial documents while the branches wait: " + partial.Length);
                foreach (var w in partial) Shape(w.Doc);
                Check(Events(doc).Count == 1 + 1 + 6 + 120 + 1, "event count: " + Events(doc).Count);
                Check(S(One(doc, "p")["frame"]) == "" && S(One(doc, "p")["status"]) == "ok" && S(One(doc, "join")["frame"]) == "", "top-level frames");
                for (var i = 0; i < 6; i++)
                {
                    Check(S(One(doc, "r" + i)["frame"]) == "p:branch:b" + i && S(One(doc, "r" + i)["status"]) == "ok", "branch frame of r" + i);
                    var frames = Of(doc, "c" + i).Select(e => S(e["frame"])).Order(StringComparer.Ordinal).ToArray();
                    Check(frames.SequenceEqual(Enumerable.Range(0, 20).Select(k => $"p:branch:b{i}/r{i}#{k}:body").Order(StringComparer.Ordinal)), "iteration frames of c" + i + ": " + string.Join(",", frames));
                }
                var p = One(doc, "p");
                Check(Events(doc).Where(e => S(e!["frame"]).StartsWith("p:")).All(e => e!["t"]!.GetValue<double>() >= p["t"]!.GetValue<double>()), "branch events start before their parallel node");
            });

            await Test("a 1,000-iteration loop hits the event cap with summary and dropped still correct", async () =>
            {
                var graph = G(N("loop", "repeat", new { count = Lit("number", 1000) }), N("n1", "not", new { value = Lit("boolean", true) }), N("n2", "not", new { value = Unset() }), N("n3", "not", new { value = Unset() }), Log("after", "done"));
                graph["edges"] = new JsonArray(E("t", "loop"), E("loop", "n1", "body"), E("n1", "n2"), E("n2", "n3"), E("loop", "after"), D("n1", "n2", "value"), D("n2", "n3", "value"));
                var (sink, doc, _) = await Trace(graph);
                Shape(doc);
                Check(Events(doc).Count == VisualTrace.MaxEvents && doc["dropped"]!.GetValue<int>() == 3003 - 2000 && doc["truncated"]!.GetValue<bool>() && sink.Writes[^1].Count == 2000, $"cap: {Events(doc).Count} events, dropped {doc["dropped"]}");
                Check(Same(doc["summary"], """{"t":{"count":1,"failed":0,"last":"ok"},"loop":{"count":1,"failed":0,"last":"ok"},"n1":{"count":1000,"failed":0,"last":"ok"},"n2":{"count":1000,"failed":0,"last":"ok"},"n3":{"count":1000,"failed":0,"last":"ok"},"after":{"count":1,"failed":0,"last":"ok"}}"""), "summary: " + doc["summary"]!.ToJsonString());
                var loop = One(doc, "loop");
                Check(S(loop["status"]) == "ok" && loop["ms"] != null && S(loop["port"]) == "next" && Of(doc, "after").Length == 0, "the loop event listed before the cap is updated in place: " + loop.ToJsonString());
                Check(S(Of(doc, "n1")[0]["frame"]) == "loop#0:body" && S(Of(doc, "n3")[^1]["frame"]) == "loop#665:body", "iteration frames up to the cap");
                // 2,000 events with their values pass 256 KiB: the oldest lose their values first, the newest keep them.
                Check(sink.Writes[^1].Bytes <= VisualTrace.MaxTraceBytes, "trace over 256 KiB: " + sink.Writes[^1].Bytes);
                var first = Of(doc, "n2")[0]; var last = Of(doc, "n2")[^1];
                Check(first["trunc"]!.GetValue<bool>() && first["inputs"] == null && first["outputs"] == null && S(first["frame"]) == "loop#0:body", "oldest values not dropped: " + first.ToJsonString());
                Check(last["trunc"] == null && Same(last["inputs"], """{"value":{"type":"boolean","v":false,"src":{"node":"n1","port":"value"}}}"""), "newest values dropped: " + last.ToJsonString());
                var kept = Events(doc).Select(e => e!["trunc"] == null).ToArray();
                Check(kept.SkipWhile(k => !k).All(k => k) && kept.Count(k => !k) > 1000, "values not dropped oldest first");
                Check(Events(doc).All(e => e!["status"] != null && e["t"] != null && e["ms"] != null && e["port"] != null), "dropped values kept status, timing and port");
            });

            await Test("large values, events and triggers are truncated with trunc", async () =>
            {
                JsonObject deep = new() { ["leaf"] = 1 };
                for (var i = 0; i < 10; i++) deep = new() { ["d"] = deep };
                var wide = new JsonObject(); for (var i = 0; i < 100; i++) wide["k" + i] = i;
                var literal = new JsonObject { ["long"] = new string('x', 5000), ["wide"] = wide, ["deep"] = deep };
                var fields = new JsonObject(); for (var i = 0; i < 16; i++) fields["f" + i] = Lit("string", new string((char)('a' + i), 3000));
                var graph = G(N("big", "object", new { value = Lit("object", literal) }), N("l", "log", new { value = Unset() }), N("many", "object_create", new JsonObject { ["fields"] = fields }));
                graph["edges"]!.AsArray().Add(D("big", "l", "value"));
                var huge = new JsonObject { ["platform"] = "manual" }; for (var i = 0; i < 100; i++) huge["p" + i] = new string('z', 900);
                var (sink, doc, _) = await Trace(graph, payload: JsonSerializer.SerializeToElement(huge));
                Shape(doc);
                var output = One(doc, "big")["outputs"]!["value"]!;
                var v = output["v"]!.AsObject();
                Check(output["trunc"]!.GetValue<bool>() && System.Text.Encoding.UTF8.GetByteCount(v.ToJsonString()) <= TraceJson.MaxValueBytes, "value over 4 KiB: " + output.ToJsonString().Length);
                Check(S(v["long"]).Length == TraceJson.MaxChars && v["wide"]!.AsObject().Count <= TraceJson.MaxItems && Depth(v) == TraceJson.MaxDepth && Same(v["deep"]!["d"]!["d"]!["d"]!["d"]!["d"], "null"), "string, item or depth cap: " + v.ToJsonString());
                var input = One(doc, "l")["inputs"]!["value"]!;
                Check(input["trunc"]!.GetValue<bool>() && Same(input["src"], """{"node":"big","port":"value"}"""), "wired input truncated: " + input.ToJsonString());
                var many = One(doc, "many");
                var bytes = System.Text.Encoding.UTF8.GetByteCount(many.ToJsonString());
                var inputs = many["inputs"]!.AsObject();
                Check(bytes <= VisualTrace.MaxEventBytes && inputs.Count == 16 && inputs.Any(p => p.Value!["v"] == null && p.Value["trunc"]!.GetValue<bool>()) && inputs.First().Value!["v"] != null, $"event cap: {bytes} bytes, {many.ToJsonString()[..300]}");
                var trigger = System.Text.Encoding.UTF8.GetByteCount(doc["trigger"]!.ToJsonString());
                Check(trigger <= VisualTrace.MaxTriggerBytes && S(doc["trigger"]!["platform"]) == "manual" && doc["trigger"]!.AsObject().Count < huge.Count && doc["truncated"]!.GetValue<bool>(), "trigger over 16 KiB: " + trigger);
                Check(One(doc, "t")["outputs"]!["data"]!["trunc"]!.GetValue<bool>(), "trigger data output not truncated");
            });

            await Test("the bounded writer stops early and always writes valid JSON", () =>
            {
                // Only materialised values are walked: a lazy sequence is never enumerated and no getter runs.
                var pulled = 0;
                IEnumerable<int> Lazy() { pulled++; yield return 1; }
                var (json, cut) = TraceJson.Value(Lazy(), TraceJson.MaxValueBytes);
                Check(cut && pulled == 0 && JsonNode.Parse(json)!.GetValue<string>().StartsWith("global::"), "lazy sequence: pulled " + pulled + ", " + System.Text.Encoding.UTF8.GetString(json));
                var (selected, selectedCut) = TraceJson.Value(new[] { 1, 2 }.Select(i => { pulled++; return i; }), TraceJson.MaxValueBytes);
                Check(selectedCut && pulled == 0 && JsonNode.Parse(selected) is JsonValue, "LINQ query enumerated: pulled " + pulled);
                var effect = new Effect();
                var (effectJson, effectCut) = TraceJson.Value(effect, TraceJson.MaxValueBytes);
                Check(effectCut && effect.Calls == 0 && Same(JsonNode.Parse(effectJson), """{"Stored":2,"Calls":0}"""), "getter with a side effect: " + effect.Calls + ", " + System.Text.Encoding.UTF8.GetString(effectJson));
                // Public fields and anonymous types are read through their fields; a hidden (new) member appears once.
                var (fields, fieldsCut) = TraceJson.Value(new Fields(), TraceJson.MaxValueBytes);
                Check(!fieldsCut && Same(JsonNode.Parse(fields), """{"A":1,"B":"x"}"""), "public fields: " + System.Text.Encoding.UTF8.GetString(fields));
                var (anonymous, anonymousCut) = TraceJson.Value(new { A = 1, B = "x" }, TraceJson.MaxValueBytes);
                Check(!anonymousCut && Same(JsonNode.Parse(anonymous), """{"A":1,"B":"x"}"""), "anonymous object: " + System.Text.Encoding.UTF8.GetString(anonymous));
                var (hidden, hiddenCut) = TraceJson.Value(new Derived(), TraceJson.MaxValueBytes);
                Check(hiddenCut && Same(JsonNode.Parse(hidden), """{"X":"d"}"""), "hidden property: " + System.Text.Encoding.UTF8.GetString(hidden));
                var (materialised, materialisedCut) = TraceJson.Value(new Dictionary<string, object?> { ["list"] = new List<int> { 1, 2 }, ["set"] = new HashSet<string> { "a" }, ["array"] = new[] { 3 }, [DayOfWeek.Monday.ToString()] = new Dictionary<DayOfWeek, int> { [DayOfWeek.Friday] = 5 }, ["queue"] = new Queue<double>([1.5]), ["sorted"] = new SortedDictionary<int, string> { [2] = "b" }, ["readings"] = new List<Reading> { new("hall", 1) } }, TraceJson.MaxValueBytes);
                Check(!materialisedCut && Same(JsonNode.Parse(materialised), """{"list":[1,2],"set":["a"],"array":[3],"Monday":{"Friday":5},"queue":[1.5],"sorted":{"2":"b"},"readings":[{"Name":"hall","Value":1}]}"""), "materialised collections: " + System.Text.Encoding.UTF8.GetString(materialised));
                // Keys the base library cannot format get their type and position, so they stay distinct; 1 and "1" too.
                var (keys, keysCut) = TraceJson.Value(new Dictionary<Key, int> { [new Key()] = 1, [new Key()] = 2 }, TraceJson.MaxValueBytes);
                Check(keysCut && Key.Formatted == 0 && Same(JsonNode.Parse(keys), """{"Key#0":1,"Key#1":2}"""), "user keys: " + System.Text.Encoding.UTF8.GetString(keys));
                var (alike, alikeCut) = TraceJson.Value(new Dictionary<object, int> { [1] = 1, ["1"] = 2 }, TraceJson.MaxValueBytes);
                Check(alikeCut && Same(JsonNode.Parse(alike), """{"1":1,"1#1":2}"""), "keys that format alike: " + System.Text.Encoding.UTF8.GetString(alike));

                // A user-defined collection is never enumerated, nor is a base-library wrapper over one.
                var list = new UserList();
                foreach (var user in new object[] { list, new UserMap { ["a"] = 1 }, new System.Collections.ObjectModel.ReadOnlyCollection<int>(list) })
                {
                    var (opaque, opaqueCut) = TraceJson.Value(user, TraceJson.MaxValueBytes);
                    Check(opaqueCut && JsonNode.Parse(opaque)!.GetValue<string>() == TraceJson.Kind(user) && list.Calls == 0, $"user collection {user.GetType().Name}: {list.Calls} calls, {System.Text.Encoding.UTF8.GetString(opaque)}");
                }
                // Readable C# type names, never assembly-qualified.
                Check(TraceJson.Kind(list) == "global::VisualTraceTests.UserList" && TraceJson.Kind(new List<int>()) == "global::System.Collections.Generic.List<int>"
                    && TraceJson.Kind(new Dictionary<string, Reading?>()) == "global::System.Collections.Generic.Dictionary<string, global::VisualTraceTests.Reading>"
                    && TraceJson.Kind(new int?[0, 0]) == "int?[,]" && TraceJson.Kind(new KeyValuePair<int, string>[1]) == "global::System.Collections.Generic.KeyValuePair<int, string>[]",
                    "type names: " + TraceJson.Kind(new Dictionary<string, Reading?>()) + " " + TraceJson.Kind(new int?[0, 0]));
                var linq = S(JsonNode.Parse(selected));
                Check(!linq.Contains("Version=") && !linq.Contains('`') && !linq.Contains("[["), "LINQ type name: " + linq);

                // Failures inside the walk shorten the value instead of failing.
                var (message, messageCut) = TraceJson.Value(new BrokenMessage(), TraceJson.MaxValueBytes);
                Check(messageCut && Same(JsonNode.Parse(message), """{"type":"BrokenMessage","message":""}"""), "a throwing Exception.Message: " + System.Text.Encoding.UTF8.GetString(message));
                foreach (var odd in new JsonNode?[] { JsonValue.Create(double.NaN), JsonValue.Create(double.PositiveInfinity), JsonValue.Create(12.5m), JsonValue.Create(7L), JsonValue.Create(DateTime.UnixEpoch), new JsonObject { ["nan"] = double.NaN } })
                {
                    var (bytes, _) = TraceJson.Value(odd, TraceJson.MaxValueBytes);
                    _ = JsonSerializer.Serialize(JsonNode.Parse(bytes));
                }
                Check(Same(JsonNode.Parse(TraceJson.Value(JsonValue.Create(12.5m), 4096).Json), "12.5") && Same(JsonNode.Parse(TraceJson.Value(JsonValue.Create(double.NaN), 4096).Json), "\"NaN\""), "numeric JsonValue");

                // A huge JSON string or array is read only as far as it is written.
                using var text = JsonDocument.Parse("\"" + new string('a', 5_000_000) + "\"");
                using var array = JsonDocument.Parse("[" + string.Join(",", Enumerable.Repeat("123456789", 300_000)) + "]");
                var before = GC.GetAllocatedBytesForCurrentThread();
                var (s, sCut) = TraceJson.Value(text.RootElement, TraceJson.MaxValueBytes);
                var (a, aCut) = TraceJson.Value(array.RootElement, TraceJson.MaxValueBytes);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Check(sCut && aCut && JsonNode.Parse(s)!.GetValue<string>().Length == TraceJson.MaxChars && JsonNode.Parse(a)!.AsArray().Count == TraceJson.MaxItems && allocated < 200_000, $"huge values allocated {allocated} bytes");

                // Every byte budget, with escapes, astral characters and lone surrogates, cuts to valid JSON within the budget.
                var tricky = new JsonObject { ["a"] = "\"\\\n😀<é>\ud83dx", ["b"] = new JsonArray("😀😀😀", "\ud800", 1.5, true, null), ["c"] = new string('q', 3000) };
                var element = JsonSerializer.SerializeToElement(tricky);
                foreach (var budget in Enumerable.Range(6, 120).Concat([500, 1000, 4096]))
                    foreach (var value in new object?[] { element, (JsonNode)tricky, "😀\"x\u0001" + new string('y', 100) + "\udc00" })
                    {
                        var (bytes, _) = TraceJson.Value(value, budget);
                        Check(bytes.Length <= budget, $"budget {budget} exceeded: {bytes.Length}");
                        // As run_trace re-serialises it.
                        _ = JsonSerializer.Serialize(JsonNode.Parse(bytes));
                    }

                // CLR objects: auto-properties as declared; computed properties are never called; cycles end at the depth cap with null.
                var loop = new Link(); loop.Next = loop;
                var (clr, clrCut) = TraceJson.Value(new Reading("hall", 21.5), TraceJson.MaxValueBytes);
                var (cyclic, cyclicCut) = TraceJson.Value(loop, TraceJson.MaxValueBytes);
                var (faulty, faultyCut) = TraceJson.Value(new Faulty(), TraceJson.MaxValueBytes);
                Check(Same(JsonNode.Parse(clr), """{"Name":"hall","Value":21.5}""") && !clrCut, "record: " + System.Text.Encoding.UTF8.GetString(clr));
                Check(cyclicCut && Depth(JsonNode.Parse(cyclic)) == TraceJson.MaxDepth && Same(JsonNode.Parse(cyclic), """{"Next":{"Next":{"Next":{"Next":{"Next":{"Next":null}}}}}}"""), "cycle: " + System.Text.Encoding.UTF8.GetString(cyclic));
                Check(faultyCut && Same(JsonNode.Parse(faulty), "\"global::VisualTraceTests.Faulty\""), "computed properties only: " + System.Text.Encoding.UTF8.GetString(faulty));

                // The 4 KiB value cap through a port entry: under it the value is whole, over it cut to valid JSON within it.
                JsonObject Props(int count) { var o = new JsonObject(); for (var i = 0; i < count; i++) o["p" + i] = new string((char)('a' + i % 26), 200); return o; }
                foreach (var (count, over) in new[] { (15, false), (30, true) })
                {
                    var (full, bare) = TraceJson.Entry("value", "object", Props(count), ("n", "value"));
                    var entry = JsonNode.Parse(full)!.AsObject();
                    var v = entry["v"]!.AsObject();
                    var vBytes = System.Text.Encoding.UTF8.GetByteCount(v.ToJsonString());
                    Check(vBytes <= TraceJson.MaxValueBytes && (entry["trunc"] != null) == over && (over ? v.Count < count : v.Count == count) && S(entry["type"]) == "object" && Same(entry["src"], """{"node":"n","port":"value"}"""),
                        $"{count} properties: {vBytes} bytes, {v.Count} kept, {entry.ToJsonString()[..120]}");
                    Check(Same(JsonNode.Parse(bare), """{"type":"object","trunc":true,"src":{"node":"n","port":"value"}}"""), "bare entry: " + System.Text.Encoding.UTF8.GetString(bare));
                }
                return Task.CompletedTask;
            });

            await Test("service fields, target and response, and typed trigger outputs, are traced", async () =>
            {
                var previousCall = PyInterop.CallServiceWithResponse;
                PyInterop.CallServiceWithResponse = (_, _, _, done, _) => done("""{"ok":true}""");
                try
                {
                    var config = new JsonObject { ["service"] = "light.turn_on", ["fields"] = new JsonObject { ["brightness_pct"] = Lit("number", 40), ["name"] = Unset() }, ["target"] = Unset(), ["returnResponse"] = true };
                    var graph = G(N("s", "service", config));
                    graph["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test" };
                    graph["edges"]!.AsArray().Add(D("t", "s", "target", "entity_id")); graph["edges"]!.AsArray().Add(D("t", "s", "field:name", "entity_id"));
                    HasEntityState State(string state) => new() { EntityId = "sensor.test", State = state, Attributes = new() { ["unit"] = "C" }, LastChanged = 1_700_000_000, LastReported = 1_700_000_000 };
                    var (_, doc, _) = await Trace(graph, payload: TriggerPayload.Entity(State("on"), State("off")));
                    Shape(doc);
                    var t = One(doc, "t")["outputs"]!.AsObject();
                    Check(t.Select(p => p.Key).Order().SequenceEqual(["data", "entity_id", "from_state", "to_state"]) && Same(t["entity_id"], """{"type":"entity_id:sensor","v":"sensor.test"}"""), "trigger outputs: " + t.ToJsonString());
                    Check(S(t["to_state"]!["type"]) == "global::HassSharp.EntityStateSnapshot" && S(t["to_state"]!["v"]!["state"]) == "on" && S(t["from_state"]!["v"]!["state"]) == "off" && S(t["to_state"]!["v"]!["attributes"]!["unit"]) == "C", "entity states: " + t.ToJsonString());
                    var s = One(doc, "s");
                    Check(Same(s["inputs"], """{"field:brightness_pct":{"type":"number","v":40},"field:name":{"type":"entity_id:sensor","v":"sensor.test","src":{"node":"t","port":"entity_id"}},"target":{"type":"entity_id:sensor","v":"sensor.test","src":{"node":"t","port":"entity_id"}}}"""), "service inputs: " + s.ToJsonString());
                    Check(Same(s["outputs"], """{"response":{"type":"object","v":{"ok":true}}}"""), "service response: " + s.ToJsonString());
                }
                finally { PyInterop.CallServiceWithResponse = previousCall; }
            });

            await Test("a partial trace is readable while a delay waits and is updated in place on completion", async () =>
            {
                using var loaded = await Load(G(N("d", "delay", new { seconds = Lit("number", 0.4) }), Log("l", "after delay")));
                var sink = new Sink();
                var run = loaded.Start(sink);
                Write? partial = null;
                await Until(() => (partial = sink.Writes.FirstOrDefault(w => !w.Final && Of(w.Doc, "d").Any(e => S(e["status"]) == "running"))) != null, "a running delay event is stored");
                Check(partial!.Immediate && !partial.Doc["complete"]!.GetValue<bool>() && One(partial.Doc, "d")["ms"] == null && Same(One(partial.Doc, "d")["inputs"], """{"seconds":{"type":"number","v":0.4}}""") && Of(partial.Doc, "l").Length == 0, "partial document: " + partial.Doc.ToJsonString());
                Shape(partial.Doc);
                await run.WaitAsync(TimeSpan.FromSeconds(10));
                var doc = sink.Final;
                var d = One(doc, "d");
                Check(S(d["status"]) == "ok" && d["ms"]!.GetValue<double>() >= 350 && d["seq"]!.GetValue<int>() == One(partial.Doc, "d")["seq"]!.GetValue<int>() && doc["complete"]!.GetValue<bool>() && Of(doc, "l").Length == 1, "completed in place: " + doc.ToJsonString());
                Check(sink.Writes.Count(w => w.Immediate) == 1, "one immediate write for one wait: " + sink.Writes.Count(w => w.Immediate));
            });

            await Test("a cancelled run records cancelled", async () =>
            {
                using var loaded = await Load(G(N("d", "delay", new { seconds = Lit("number", 60) })));
                var sink = new Sink();
                using var cancel = new CancellationTokenSource();
                var run = loaded.Start(sink, cancel.Token);
                await Until(() => sink.Writes.Any(w => Of(w.Doc, "d").Length == 1), "the delay starts");
                cancel.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
                var doc = sink.Final;
                Shape(doc);
                Check(S(One(doc, "d")["status"]) == "cancelled" && One(doc, "d")["ms"] != null && doc["complete"]!.GetValue<bool>() && Same(doc["summary"]!["d"], """{"count":1,"failed":0,"last":"cancelled"}"""), "cancelled: " + doc.ToJsonString());
            });

            await Test("a recorder failure never fails the run and leaves a valid truncated trace", async () =>
            {
                var graph = G(N("a", "not", new { value = Lit("boolean", true) }), N("b", "not", new { value = Lit("boolean", true) }), Log("c", "after the fault"));
                var logged = messages.Count(m => m.Contains("Stopped recording the trace"));
                // A fault at each point the recorder can fail: starting an event, recording an input or output, building the document.
                foreach (var (stage, nodes) in new (string, string[])[] { ("start:b", ["t", "a"]), ("input:b", ["t", "a", "b"]), ("output:b", ["t", "a", "b"]), ("build", []) })
                {
                    var before = messages.Count(m => m.Contains("after the fault"));
                    VisualTrace.Fault = at => { if (at == stage) throw new InvalidOperationException("recorder bug"); };
                    JsonObject doc;
                    try { (_, doc, _) = await Trace(graph); }
                    finally { VisualTrace.Fault = null; }
                    Shape(doc);
                    Check(doc["truncated"]!.GetValue<bool>() && doc["complete"]!.GetValue<bool>() && Events(doc).Select(e => S(e!["node"])).SequenceEqual(nodes) && messages.Count(m => m.Contains("after the fault")) == before + 1, $"after a recorder failure at {stage}: " + doc.ToJsonString());
                    Check(Events(doc).All(e => S(e!["status"]) == "ok"), $"events after a fault at {stage} did not complete: " + doc.ToJsonString());
                    if (stage == "input:b") Check(One(doc, "b")["inputs"] == null && One(doc, "b")["outputs"] == null, "values recorded after an input fault");
                    if (stage == "output:b") Check(One(doc, "b")["inputs"] != null && One(doc, "b")["outputs"] == null, "values recorded after an output fault");
                    Check(messages.Count(m => m.Contains("Stopped recording the trace")) == ++logged, $"the failure at {stage} is not logged exactly once");
                }
                // A build fault while a node waits: no partial document, the final one is the minimal valid document.
                {
                    using var waiting = await Load(G(N("d", "delay", new { seconds = Lit("number", 0.1) }), Log("e", "after a build fault")));
                    var sink = new Sink();
                    VisualTrace.Fault = at => { if (at == "build") throw new InvalidOperationException("recorder bug"); };
                    try { await waiting.Start(sink).WaitAsync(TimeSpan.FromSeconds(10)); }
                    finally { VisualTrace.Fault = null; }
                    var doc = sink.Final;
                    Shape(doc);
                    Check(sink.Writes.Length == 1 && doc["truncated"]!.GetValue<bool>() && doc["complete"]!.GetValue<bool>() && Events(doc).Count == 0 && messages.Contains("after a build fault"), "build fault while waiting: " + doc.ToJsonString());
                    Check(messages.Count(m => m.Contains("Stopped recording the trace")) == ++logged, "the build fault is not logged exactly once");
                }
                // A sink that throws on every write is the same: logged once, the run still succeeds.
                using var loaded = await Load(G(N("d", "delay", new { seconds = Lit("number", 0.05) }), Log("e", "after a broken sink")));
                await loaded.Start(new Sink { Throws = true }).WaitAsync(TimeSpan.FromSeconds(10));
                Check(messages.Contains("after a broken sink") && messages.Count(m => m.Contains("Stopped recording the trace")) == ++logged, "a throwing sink");
            });

            await Test("no sink: no recorder and no recording path", async () =>
            {
                var nodes = new List<JsonObject> { N("p", "parallel", new { branches = new[] { "a", "b" } }), N("r", "repeat", new { count = Lit("number", 50) }), N("n", "not", new { value = Lit("boolean", true) }), C("c", "await global::System.Threading.Tasks.Task.Yield();") };
                var graph = G([.. nodes]); graph["edges"] = new JsonArray(E("t", "p"), E("p", "r", "branch:a"), E("r", "n", "body"), E("p", "c", "branch:b"));
                using var loaded = await Load(graph);
                var before = Volatile.Read(ref VisualTrace.Created);
                await loaded.Start(null, context: false).WaitAsync(TimeSpan.FromSeconds(10));
                await loaded.Start(null).WaitAsync(TimeSpan.FromSeconds(10));
                Check(Volatile.Read(ref VisualTrace.Created) == before, "a run without a sink created a recorder");
                // The counter does count: one recorder for one traced run.
                var sink = new Sink();
                await loaded.Start(sink).WaitAsync(TimeSpan.FromSeconds(10));
                Check(Volatile.Read(ref VisualTrace.Created) == before + 1 && Events(sink.Final).Count == 1 + 1 + 1 + 50 + 1, "traced control run");
            });

            await Test("the sink stores an immediate write at once and drops the held one", () =>
            {
                var folder = Path.Combine(HassPath.HassConfiguration, "sink"); Directory.CreateDirectory(folder);
                var store = new RunHistoryStore(Path.Combine(folder, "runs.db"));
                var run = new InvocationRecord { AutomationId = "a", Revision = "r", Action = "A::Run", Cause = "Manual" };
                store.Save(run);
                var time = new ManualTime();
                IRunTraceSink sink = new RunTraceSink(store, run.RunId, TimeSpan.FromSeconds(1), time);
                sink.Write("{\"n\":1}", 1, false, false);
                time.Advance(TimeSpan.FromMilliseconds(100));
                sink.Write("{\"n\":2}", 2, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":1}", "write inside the interval stored at once");
                sink.Write("{\"n\":3}", 3, false, false, immediate: true);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":3}" && time.Pending == 0, "immediate write not stored at once");
                time.Advance(TimeSpan.FromSeconds(2));
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":3}", "the held write replaced the immediate one");
                time.Advance(TimeSpan.FromMilliseconds(100));
                sink.Write("{\"n\":4}", 4, false, false);
                Check(store.ReadTrace(run.RunId)?.Json == "{\"n\":4}", "the interval restarts at the immediate write");
                return Task.CompletedTask;
            });

            await Test("while and until loops record condition frames with the carried values", async () =>
            {
                static JsonObject V(string name) => new() { ["kind"] = "variable", ["name"] = name };
                foreach (var kind in new[] { "while", "until" })
                {
                    var cfg = new JsonObject { ["maxIterations"] = 5, ["carry"] = new JsonArray(new JsonObject { ["name"] = "n", ["type"] = "number", ["initial"] = Lit("number", 0) }) };
                    var graph = G(N("loop", kind, cfg), N("body", "set_value", new { type = "number", value = Unset(), output = "next" }), C("inc", "values.Set(\"next\", values.Get<double>(\"next\",\"inc\")+1);"),
                        N("result", "loop_result", new { values = new JsonObject { ["carry:n"] = V("next") } }),
                        N("compare", "compare", new { left = Unset(), right = Lit("number", 3), @operator = kind == "while" ? "lt" : "ge" }), N("condition", "condition_result", new { value = Unset() }));
                    graph["edges"] = new JsonArray(E("t", "loop"), E("loop", "body", "body"), E("body", "inc"), E("inc", "result"), D("loop", "body", "value", "carry:n"),
                        E("loop", "compare", "condition"), E("compare", "condition"), D("loop", "compare", "left", "carry:n"), D("compare", "condition", "value"));
                    var (_, doc, _) = await Trace(graph);
                    Shape(doc);
                    // while tests n = 0, 1, 2, 3 before each body; until tests n = 1, 2, 3 after it.
                    var tested = kind == "while" ? new[] { 0, 1, 2, 3 } : [1, 2, 3];
                    var compares = Of(doc, "compare");
                    Check(compares.Select(e => S(e["frame"])).SequenceEqual(tested.Select((_, i) => $"loop#{i}:condition")), kind + " condition frames: " + string.Join(",", compares.Select(e => S(e["frame"]))));
                    Check(compares.Select(e => e["inputs"]!["left"]!["v"]!.GetValue<double>()).SequenceEqual(tested.Select(n => (double)n)) && compares.All(e => Same(e["inputs"]!["left"]!["src"], """{"node":"loop","port":"carry:n"}""")), kind + " carried values: " + string.Join(",", compares.Select(e => e["inputs"]!.ToJsonString())));
                    Check(Of(doc, "condition").Select(e => S(e["frame"])).SequenceEqual(compares.Select(e => S(e["frame"]))) && Of(doc, "condition").All(e => e["port"] == null && S(e["status"]) == "ok"), kind + " condition_result events: " + doc.ToJsonString());
                    Check(Of(doc, "body").Select(e => S(e["frame"])).SequenceEqual(["loop#0:body", "loop#1:body", "loop#2:body"]) && Of(doc, "inc").Length == 3, kind + " body frames");
                    Check(S(One(doc, "loop")["frame"]) == "" && S(One(doc, "loop")["status"]) == "ok" && S(One(doc, "loop")["port"]) == "next", kind + " loop event: " + One(doc, "loop").ToJsonString());
                }
            });

            await Test("a failing parallel branch cancels its siblings and fails or recovers at the parallel node", async () =>
            {
                foreach (var recover in new[] { false, true })
                {
                    var graph = G(N("p", "parallel", new { branches = new[] { "a", "b" } }), C("x", "await global::System.Threading.Tasks.Task.Delay(50); throw new InvalidOperationException(\"branch\");"), N("d", "delay", new { seconds = Lit("number", 30) }));
                    graph["edges"] = new JsonArray(E("t", "p"), E("p", "x", "branch:a"), E("p", "d", "branch:b"));
                    if (recover) { graph["nodes"]!.AsArray().Add(Log("r", "recovered")); graph["edges"]!.AsArray().Add(E("p", "r", "error")); }
                    var (_, doc, _) = await Trace(graph, fails: !recover);
                    Shape(doc);
                    var x = One(doc, "x"); var d = One(doc, "d"); var p = One(doc, "p");
                    Check(S(x["status"]) == "failed" && S(x["frame"]) == "p:branch:a" && S(x["error"]!["message"]) == "branch", "failing branch node: " + x.ToJsonString());
                    Check(S(d["status"]) == "cancelled" && S(d["frame"]) == "p:branch:b" && d["ms"]!.GetValue<double>() < 20_000, "sibling: " + d.ToJsonString());
                    if (recover) Check(S(p["status"]) == "error" && S(p["port"]) == "error" && S(One(doc, "r")["status"]) == "ok", "recovered parallel: " + doc.ToJsonString());
                    else Check(S(p["status"]) == "failed" && p["port"] == null && p["error"] != null && Of(doc, "r").Length == 0 && Same(doc["summary"]!["p"], """{"count":1,"failed":1,"last":"failed"}"""), "failed parallel: " + doc.ToJsonString());
                }
            });

            await Test("secret-looking names and token= values are redacted everywhere", async () =>
            {
                var previousCall = PyInterop.CallService;
                var sent = new List<string>();
                PyInterop.CallService = (_, _, json, done, _) => { lock (sent) sent.Add(json ?? ""); done?.Invoke(); };
                try
                {
                    var literal = JsonNode.Parse("""{"user":"me","auth":{"Password":"s3cr3t-3","pin":1234,"deeper":[{"api_key":"s3cr3t-5"}]},"note":"see token=s3cr3t-4 here","Token":"s3cr3t-6"}""")!.AsObject();
                    var graph = G(N("o", "object", new { value = Lit("object", literal) }), N("s", "service", new JsonObject { ["service"] = "lock.unlock", ["fields"] = new JsonObject { ["code"] = Lit("string", "4321") }, ["target"] = Unset() }));
                    graph["nodes"]![0]!["config"] = new JsonObject { ["type"] = "entity", ["entity"] = "sensor.test" };
                    graph["edges"]!.AsArray().Add(D("t", "s", "target", "entity_id"));
                    var attributes = new Dictionary<string, object> { ["access_token"] = "s3cr3t-1", ["entity_picture"] = "/api/camera_proxy/camera.door?token=s3cr3t-2&width=10", ["unit"] = "C" };
                    HasEntityState State(string state) => new() { EntityId = "sensor.test", State = state, Attributes = new(attributes), LastChanged = 1_700_000_000, LastReported = 1_700_000_000 };
                    var (_, doc, _) = await Trace(graph, payload: TriggerPayload.Entity(State("on"), State("off")));
                    Shape(doc);
                    var text = doc.ToJsonString();
                    Check(!text.Contains("s3cr3t") && !text.Contains("4321"), "a secret reached the trace: " + text);
                    // A redaction hides nothing else, so it is not a cut: no value is trunc and the trace is not truncated.
                    Check(!doc["truncated"]!.GetValue<bool>() && !text.Contains("\"trunc\""), "redaction marked the trace cut: " + text);
                    Check(sent.Count == 1 && JsonNode.Parse(sent[0])!["code"]!.GetValue<string>() == "4321", "the service did not get the real code: " + string.Join(",", sent));
                    var code = One(doc, "s")["inputs"]!["field:code"]!;
                    Check(S(code["v"]) == TraceJson.Redacted && code["redacted"]!.GetValue<bool>() && S(code["type"]) == "string", "field:code port: " + code.ToJsonString());
                    var picture = "/api/camera_proxy/camera.door?token=***&width=10";
                    foreach (var state in new[] { doc["trigger"]!["to_state"]!, One(doc, "t")["outputs"]!["to_state"]!["v"]!, One(doc, "t")["outputs"]!["data"]!["v"]!["from_state"]! })
                        Check(S(state["attributes"]!["access_token"]) == "***" && S(state["attributes"]!["entity_picture"]) == picture && S(state["attributes"]!["unit"]) == "C", "state attributes: " + state.ToJsonString());
                    var o = One(doc, "o")["outputs"]!["value"]!;
                    Check(Same(o["v"], """{"user":"me","auth":{"Password":"***","pin":"***","deeper":[{"api_key":"***"}]},"note":"see token=*** here","Token":"***"}""") && o["redacted"] == null, "nested secrets: " + o.ToJsonString());
                    var (clr, _) = TraceJson.Value(new Credentials("me", "pw", new Credentials("you", "pw2")), TraceJson.MaxValueBytes);
                    Check(Same(JsonNode.Parse(clr), """{"User":"me","Password":"***","Inner":{"User":"you","Password":"***","Inner":null}}"""), "CLR secrets: " + System.Text.Encoding.UTF8.GetString(clr));
                    var names = new Dictionary<string, object>
                    {
                        ["Authorization"] = "Bearer x", ["PASSWD"] = 1, ["passcode"] = 2, ["api-key"] = 3, ["Client_Secret"] = 4, ["refresh_token"] = 5, ["Webhook_Secret"] = 6,
                        ["wifi_password"] = 7, ["door_passcode"] = 8, ["private_key"] = 9,
                        ["codes"] = 10, ["zip_code"] = 11, ["pincode"] = 12, ["tokens"] = 13, ["keyboard"] = 14, ["secretary"] = 15,
                    };
                    var (map, mapCut) = TraceJson.Value(names, TraceJson.MaxValueBytes);
                    Check(!mapCut && Same(JsonNode.Parse(map), """{"Authorization":"***","PASSWD":"***","passcode":"***","api-key":"***","Client_Secret":"***","refresh_token":"***","Webhook_Secret":"***","wifi_password":"***","door_passcode":"***","private_key":"***","codes":10,"zip_code":11,"pincode":12,"tokens":13,"keyboard":14,"secretary":15}"""), "dictionary keys: " + System.Text.Encoding.UTF8.GetString(map));
                    Check(TraceJson.Secret("field:refresh_token") && TraceJson.Secret("field:PIN") && !TraceJson.Secret("field:zip_code") && !TraceJson.Secret("field:pincode"), "port names");
                    // token= redaction alone is not a cut, but a cut string still is.
                    var (url, urlCut) = TraceJson.Value("/api/camera_proxy/camera.door?token=" + new string('a', 64), TraceJson.MaxValueBytes);
                    var (longUrl, longCut) = TraceJson.Value("?token=" + new string('a', 64) + "&x=" + new string('b', 2000), TraceJson.MaxValueBytes);
                    Check(!urlCut && S(JsonNode.Parse(url)) == "/api/camera_proxy/camera.door?token=***" && longCut && S(JsonNode.Parse(longUrl)).Length == TraceJson.MaxChars && S(JsonNode.Parse(longUrl)).StartsWith("?token=***&x=bbb"), "token= cut flag: " + S(JsonNode.Parse(url)));
                }
                finally { PyInterop.CallService = previousCall; }
            });

            await Test("waits store at most once per 500 ms and a long wait is still stored promptly", () =>
            {
                // A manual clock and the writer run inline, so every store happens inside the call that makes it due.
                var time = new Clock();
                var sink = new Sink();
                var prior = InvocationContext.Current;
                InvocationContext.Current = new InvocationContext("run-manual", InvocationCause.Manual, null, TriggerPayload.Manual()) { TraceSink = sink };
                VisualTrace trace;
                try { trace = VisualTrace.Create("t", ["w", "long"], (_, _) => null, (_, _) => null, time, run => run())!; }
                finally { InvocationContext.Current = prior; }
                // Nothing stored yet: the first wait is stored at once.
                var first = trace.Start("", "w")!; trace.Waiting();
                Check(sink.Writes.Length == 1 && sink.Writes[0].Immediate && S(One(sink.Writes[0].Doc, "w")["status"]) == "running", "first wait: " + sink.Writes.Length);
                trace.End(first, "ok", "next", null);
                // A long wait 10 ms after that store is stored when the 500 ms gap ends.
                time.Advance(TimeSpan.FromMilliseconds(10));
                var wait = trace.Start("", "long")!; trace.Waiting();
                Check(sink.Writes.Length == 1, "stored inside the 500 ms gap: " + sink.Writes.Length);
                time.Advance(TimeSpan.FromMilliseconds(489));
                Check(sink.Writes.Length == 1, "stored before the 500 ms gap ended");
                time.Advance(TimeSpan.FromMilliseconds(1));
                Check(sink.Writes.Length == 2, "the long wait is not stored when the gap ends");
                var shown = sink.Writes[1];
                Check(shown.Immediate && S(One(shown.Doc, "long")["status"]) == "running" && S(One(shown.Doc, "w")["status"]) == "ok", "long wait: " + shown.Doc.ToJsonString());
                time.Advance(TimeSpan.FromSeconds(5));
                Check(sink.Writes.Length == 2, "stored without a change");
                // The gaps are measured on the monotonic clock: stepping the wall clock back an hour delays nothing.
                trace.End(wait, "ok", "next", null);
                Check(sink.Writes.Length == 3, "an end more than a second after the last store is stored at once");
                time.Wall(TimeSpan.FromHours(-1));
                var stepped = trace.Start("", "w")!; trace.Waiting();
                time.Advance(TimeSpan.FromMilliseconds(499));
                Check(sink.Writes.Length == 3, "stored before the gap ended after a clock step");
                time.Advance(TimeSpan.FromMilliseconds(1));
                Check(sink.Writes.Length == 4 && S(Of(sink.Writes[3].Doc, "w")[^1]["status"]) == "running", "a wall-clock step back delayed the store");
                trace.End(stepped, "ok", "next", null);
                // 1,000 waits of 10 ms each over 10 s: one store per 500 ms, not one per wait.
                time.Advance(TimeSpan.FromSeconds(2));
                var before = sink.Writes.Length;
                for (var i = 0; i < 1000; i++) { var ev = trace.Start("", "w")!; trace.Waiting(); time.Advance(TimeSpan.FromMilliseconds(10)); trace.End(ev, "ok", "next", null); }
                var during = sink.Writes.Length - before;
                Check(during == 10_000 / 500, "stores during 1,000 short waits: " + during);
                trace.Complete();
                var doc = sink.Final;
                Check(Events(doc).Count == 1003 && doc["complete"]!.GetValue<bool>() && time.Pending == 0, $"final document: {Events(doc).Count} events, {time.Pending} timers");
                trace.Waiting(); time.Advance(TimeSpan.FromSeconds(2));
                Check(sink.Writes.Length == before + during + 1, "a store after the final document");
                return Task.CompletedTask;
            });

            await Test("end to end through the manager: saved graph, run, run_trace", async () =>
            {
                var root = Path.Combine(HassPath.HassConfiguration, "manager");
                var previous = HassPath.HassConfiguration; HassPath.HassConfiguration = root;
                var api = new HassSharpManager(root);
                try
                {
                    await api.Init(["sensor.test"]);
                    var graph = G(N("g", "get_field", new { value = Unset(), pointer = "/platform", type = "string" }), N("l", "log", new { value = Unset() }));
                    graph["edges"]!.AsArray().Add(D("t", "g", "value", "data")); graph["edges"]!.AsArray().Add(D("g", "l", "value"));
                    async Task<JsonNode> Visual(object request) => JsonNode.Parse(await api.VisualRequest(JsonSerializer.Serialize(request)))!;
                    var saved = await Visual(new { operation = "save", id = "traced", graph, expectedRevision = 0 });
                    Check(saved["ok"]!.GetValue<bool>() && saved["result"]!["active"]!.GetValue<bool>(), "save rejected: " + saved.ToJsonString());
                    var read = (await Visual(new { operation = "read", id = "traced" }))["result"]!;
                    var runId = (await Visual(new { operation = "run", id = "traced", triggerId = "t" }))["result"]!["runId"]!.GetValue<string>();
                    await Until(() => JsonNode.Parse(api.GetRunTrace(runId))!["run"]!["endUtc"] != null, "the run completes");
                    var response = JsonNode.Parse(api.GetRunTrace(runId))!;
                    Check(response["run"]!["hasTrace"]!.GetValue<bool>() && S(response["run"]!["status"]) == "Succeeded" && !response["live"]!.GetValue<bool>(), "run: " + response["run"]!.ToJsonString());
                    var doc = response["trace"]!.AsObject();
                    Shape(doc);
                    Check(doc["complete"]!.GetValue<bool>() && S(doc["runId"]) == runId && Events(doc).Select(e => S(e!["node"])).SequenceEqual(["t", "g", "l"]), "trace: " + doc.ToJsonString());
                    Check(Same(One(doc, "g")["inputs"], """{"value":{"type":"object","v":{"platform":"manual"},"src":{"node":"t","port":"data"}}}""") && Same(One(doc, "g")["outputs"], """{"value":{"type":"string","v":"manual"}}"""), "g values: " + One(doc, "g").ToJsonString());
                    Check(Same(One(doc, "l")["inputs"], """{"value":{"type":"string","v":"manual","src":{"node":"g","port":"value"}}}"""), "l values: " + One(doc, "l").ToJsonString());
                    Check(JsonNode.DeepEquals(response["snapshot"]!["graph"], read["graph"]), "snapshot graph is not the saved graph");
                }
                finally { await api.UnloadAsync(); HassPath.HassConfiguration = previous; }
            });
        }
        finally
        {
            PyInterop.Log = previousLog;
            try { Directory.Delete(HassPath.HassConfiguration, true); } catch { }
            HassPath.HassConfiguration = previousPath;
        }
        if (failures.Count != 0) throw new Exception(string.Join("\n", failures));
    }

    public sealed record Reading(string Name, double Value);
    public sealed class Link { public Link? Next { get; set; } }
    public sealed class Faulty { public int Fine => 1; public int Broken => throw new InvalidOperationException("getter"); }
    public sealed class Effect { public int Calls; public int Computed { get { Calls++; return 1; } } public int Stored { get; set; } = 2; }
    public sealed record Credentials(string User, string Password, Credentials? Inner = null);
    public sealed class Key : IFormattable
    {
        public static int Formatted;
        public string ToString(string? format, IFormatProvider? provider) { Formatted++; return "formatted"; }
    }
    /// <summary>A user collection whose enumeration has a side effect: tracing must never call it.</summary>
    public sealed class UserList : IList<int>, System.Collections.ICollection
    {
        public int Calls;
        IEnumerable<int> Items() { Calls++; yield return 1; }
        public IEnumerator<int> GetEnumerator() => Items().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public int this[int index] { get { Calls++; return 1; } set { } }
        public int Count { get { Calls++; return 1; } }
        public bool IsReadOnly => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public void Add(int item) { }
        public void Clear() { }
        public bool Contains(int item) { Calls++; return true; }
        public void CopyTo(int[] array, int index) { Calls++; }
        public void CopyTo(Array array, int index) { Calls++; }
        public int IndexOf(int item) { Calls++; return 0; }
        public void Insert(int index, int item) { }
        public bool Remove(int item) => false;
        public void RemoveAt(int index) { }
    }
    public sealed class UserMap : Dictionary<string, int>;

    /// <summary>A manual clock whose monotonic timestamp and wall clock move separately; timers fire inside <see cref="Advance"/>.</summary>
    sealed class Clock : TimeProvider
    {
        readonly Lock gate = new();
        readonly List<(TimerCallback Callback, object? State, TimeSpan Due, Timer Handle)> timers = [];
        TimeSpan elapsed;
        DateTimeOffset wall = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() { lock (gate) return wall; }
        public override long GetTimestamp() { lock (gate) return elapsed.Ticks; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public int Pending { get { lock (gate) return timers.Count; } }
        /// <summary>Steps only the wall clock, as an NTP correction would.</summary>
        public void Wall(TimeSpan by) { lock (gate) wall += by; }
        public void Advance(TimeSpan by)
        {
            (TimerCallback Callback, object? State, TimeSpan Due, Timer Handle)[] due;
            lock (gate) { elapsed += by; wall += by; due = timers.Where(t => t.Due <= elapsed).ToArray(); timers.RemoveAll(t => t.Due <= elapsed); }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var handle = new Timer(this);
            lock (gate) timers.Add((callback, state, elapsed + dueTime, handle));
            return handle;
        }
        sealed class Timer(Clock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { lock (owner.gate) owner.timers.RemoveAll(t => t.Handle == this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    public sealed class Fields { public int A = 1; public string B = "x"; }
    public class Base { public int X { get; set; } = 1; }
    public sealed class Derived : Base { public new string X { get; set; } = "d"; }
    public sealed class BrokenMessage : Exception { public override string Message => throw new InvalidOperationException("message"); }
}

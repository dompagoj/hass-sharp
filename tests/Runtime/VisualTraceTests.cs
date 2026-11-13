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
                    Check(value!.AsObject().All(p => p.Key is "type" or "v" or "trunc" or "src") && value["type"] is JsonValue && (value["src"] == null || value["src"]!.AsObject().Select(p => p.Key).SequenceEqual(["node", "port"])), "value fields: " + value.ToJsonString());
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
                    nodes.Add(C("c" + i, "await global::System.Threading.Tasks.Task.Yield();"));
                    edges.Add(E("p", "r" + i, "branch:b" + i)); edges.Add(E("r" + i, "c" + i, "body"));
                }
                var graph = G([.. nodes]); graph["edges"] = edges;
                var (_, doc, _) = await Trace(graph);
                Shape(doc);
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
                Check(S(v["long"]).Length == TraceJson.MaxChars && v["wide"]!.AsObject().Count <= TraceJson.MaxItems && Depth(v) <= TraceJson.MaxDepth + 1, "string, item or depth cap: " + v.ToJsonString());
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
                var pulled = 0;
                IEnumerable<int> Endless() { for (var i = 0; ; i++) { pulled++; yield return i; } }
                var (json, cut) = TraceJson.Value(Endless(), TraceJson.MaxValueBytes);
                Check(cut && JsonNode.Parse(json)!.AsArray().Count == TraceJson.MaxItems && pulled <= TraceJson.MaxItems + 1, "endless sequence: pulled " + pulled);

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

                // CLR objects: members as declared; a throwing getter is left out; cycles end at the depth cap.
                var loop = new Link(); loop.Next = loop;
                var (clr, clrCut) = TraceJson.Value(new Reading("hall", 21.5), TraceJson.MaxValueBytes);
                var (cyclic, cyclicCut) = TraceJson.Value(loop, TraceJson.MaxValueBytes);
                var (faulty, faultyCut) = TraceJson.Value(new Faulty(), TraceJson.MaxValueBytes);
                Check(Same(JsonNode.Parse(clr), """{"Name":"hall","Value":21.5}""") && !clrCut, "record: " + System.Text.Encoding.UTF8.GetString(clr));
                Check(cyclicCut && Depth(JsonNode.Parse(cyclic)) == TraceJson.MaxDepth + 1, "cycle: " + System.Text.Encoding.UTF8.GetString(cyclic));
                Check(faultyCut && Same(JsonNode.Parse(faulty), """{"Fine":1}"""), "throwing getter: " + System.Text.Encoding.UTF8.GetString(faulty));
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
                VisualTrace.Fault = node => { if (node == "b") throw new InvalidOperationException("recorder bug"); };
                JsonObject doc;
                try { (_, doc, _) = await Trace(graph); }
                finally { VisualTrace.Fault = null; }
                Shape(doc);
                Check(doc["truncated"]!.GetValue<bool>() && doc["complete"]!.GetValue<bool>() && Events(doc).Select(e => S(e!["node"])).SequenceEqual(["t", "a"]) && messages.Contains("after the fault"), "after a recorder failure: " + doc.ToJsonString());
                Check(messages.Count(m => m.Contains("Stopped recording the trace")) == logged + 1, "the failure is not logged exactly once");
                // A sink that throws on every write is the same: logged once, the run still succeeds.
                using var loaded = await Load(G(N("d", "delay", new { seconds = Lit("number", 0.05) }), Log("e", "after a broken sink")));
                await loaded.Start(new Sink { Throws = true }).WaitAsync(TimeSpan.FromSeconds(10));
                Check(messages.Contains("after a broken sink") && messages.Count(m => m.Contains("Stopped recording the trace")) == logged + 2, "a throwing sink");
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
}

using System.Text.Json;
using System.Text.Json.Nodes;
using HassSharp;
using static VisualExecutionTests;

/// <summary>
/// The relays library of <see cref="CustomNodeTests.Relays"/> with a hub that also draws a page and counts its starts,
/// and what the run time tests of a library class node share: a node of the hub as the editor drops one, a definition of
/// nodes, and a whole manager (<see cref="EntityTestHome"/>) that saves the library and the definitions.
/// </summary>
static class RelayHubFixture
{
    /// <summary>
    /// The hub's code: <see cref="CustomNodeTests.Relays"/>'s settings and switch, and a <c>Start()</c> that adds 1 to its
    /// data's <c>starts</c> and draws the list <c>relays</c> with one card, <c>relay</c>, whose action Switch turns the
    /// switch on, or off when it is on.
    /// </summary>
    internal const string HubCode = """
        /// <summary>A hub of relays.</summary>
        [Node("Relay hub", Category = "Relays")]
        public class RelayHub : SharpDevice
        {
            readonly SharpSwitch relay;
            [Setting] public string Address { get; set; }
            [Setting(Secret = true)] public string? Token { get; set; }
            [Setting("Poll every", Minimum = 1, Maximum = 60)] public double PollEvery { get; set; } = 5;
            public RelayHub() { relay = Add(new SharpSwitch("relay", "Relay")); }
            protected override Task Start()
            {
                Data.Update<int>("starts", n => n + 1);
                Ui.List("relays", "Relays", [new UiCard("relay").Text("Relay").Action("Switch", _ => { relay.IsOn = relay.IsOn != true; return Task.CompletedTask; })]);
                return Task.CompletedTask;
            }
        }
        """;
    /// <summary>The library's other classes, as in <see cref="CustomNodeTests.Relays"/>.</summary>
    internal const string OtherCode = """
        /// <summary>One relay.</summary>
        [Node("Relay")]
        public class Relay : SharpSwitch
        {
            [Setting] public string Address { get; set; } = "";
        }
        public class Helper : SharpSwitch { }
        [Node("Base relay")]
        public abstract class BaseRelay : SharpSwitch { }
        """;
    /// <summary>The whole library's code.</summary>
    internal const string Code = HubCode + "\n" + OtherCode;
    /// <summary>The library, of id <c>relays</c>.</summary>
    internal static readonly NodeLibrarySource Library = new("relays", Code, 1);

    static readonly Lazy<VisualCatalogSnapshot> Catalog = new(() =>
    {
        var preview = new NodeLibraryCompiler(Diagnostics).Preview([Library]);
        Check(preview.Valid, "the relays library with a page compiles: " + string.Join(" | ", preview.Diagnostics.Select(d => d.Message)));
        // The hub is the shared fixture's, its page and its count aside: both libraries give the same entries.
        var shared = new NodeLibraryCompiler(Diagnostics).Preview([CustomNodeTests.Relays]).Snapshot.Catalog["nodes"]!;
        foreach (var kind in new[] { CustomNodeTests.HubKind, CustomNodeTests.RelayKind })
            Check(JsonNode.DeepEquals(preview.Snapshot.Catalog["nodes"]![kind], shared[kind]), $"the entry of {kind} is the shared fixture's: " + preview.Snapshot.Catalog["nodes"]![kind]?.ToJsonString());
        return preview.Snapshot;
    });

    /// <summary>A node of the hub as the editor drops one, of the id and key, named Hub, with these values under arguments.</summary>
    internal static JsonObject Hub(string id, string key, JsonObject? arguments = null)
    {
        var node = VisualGraphContract.CreateNode(CustomNodeTests.HubKind, id, Catalog.Value);
        node.Remove("label");
        node["config"]!["key"] = key; node["config"]!["name"] = "Hub"; node["config"]!["arguments"] = arguments ?? new JsonObject { ["Address"] = Lit("string", "10.0.0.2") };
        return node;
    }

    /// <summary>
    /// A C# device node (<paramref name="kind"/> <c>csharp_device</c>, named Hub) or C# entity node (<c>csharp_entity</c>)
    /// of the id and key whose code is <paramref name="source"/>, such as <c>public class FrontHub : RelayHub { }</c>, its
    /// settings as the editor's Apply writes them: as the code gives them against the relays library of
    /// <paramref name="code"/>. With these values under arguments, Address 10.0.0.2 when none are given.
    /// </summary>
    internal static JsonObject Derived(string kind, string id, string key, string source, JsonObject? arguments = null, string code = Code)
    {
        var compiled = new NodeLibraryCompiler(Diagnostics).Preview([new NodeLibrarySource("relays", code, 1)], [new CustomNodeSource(id, source, kind)]).Snapshot.Custom[id];
        Check(compiled.Valid, $"the {kind} code {source} compiles: " + string.Join(" | ", compiled.Diagnostics.Select(d => d.Message)));
        var config = new JsonObject { ["source"] = source, ["signature"] = compiled.Signature!.DeepClone(), ["arguments"] = arguments ?? new JsonObject { ["Address"] = Lit("string", "10.0.0.2") } };
        return kind == "csharp_device" ? EntityNodeTests.CSharpDevice(id, key, "Hub", config) : EntityNodeTests.CSharpEntity(id, key, config);
    }

    /// <summary>A definition of the nodes and value wires, with these settings.</summary>
    internal static JsonObject Definition(string name, JsonObject[] nodes, JsonObject[]? edges = null, params JsonObject[] settings) => new()
    {
        ["name"] = name, ["settings"] = new JsonArray([.. settings]),
        ["logic"] = new JsonObject { ["kind"] = "graph", ["graph"] = new JsonObject { ["version"] = 5, ["name"] = name, ["nodes"] = new JsonArray([.. nodes]), ["edges"] = new JsonArray([.. edges ?? []]) } },
    };

    /// <summary>A whole manager over a home of its own.</summary>
    internal sealed class Rig : IAsyncDisposable
    {
        public EntityTestHome Home { get; }
        public HassSharpManager Api { get; private set; } = null!;
        Rig(string name) => Home = new("relay-hub-" + name);
        public static async Task<Rig> Start(string name)
        {
            var rig = new Rig(name);
            rig.Api = new(rig.Home.Root);
            await rig.Api.Init();
            return rig;
        }
        public async Task<JsonNode> Request(object request) => JsonNode.Parse(await Api.EntityRequest(JsonSerializer.Serialize(request)))!;
        public async Task<JsonObject> Ok(object request)
        {
            var answer = await Request(request);
            Check(answer["ok"]!.GetValue<bool>(), "the request failed: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        /// <summary>Saves the node library of the id and code at the revision it replaces; the whole answer, ok or not.</summary>
        public async Task<JsonObject> Library(string id, long revision, string code) =>
            JsonNode.Parse(await Api.NodeLibraryRequest(JsonSerializer.Serialize(new { operation = "save", id, name = id, expectedRevision = revision, source = code })))!.AsObject();
        /// <summary>Saves the node library and checks that the save went ahead.</summary>
        public async Task<JsonObject> Saved(string id, long revision, string code)
        {
            var answer = await Library(id, revision, code);
            Check(answer["ok"]!.GetValue<bool>(), $"the library {id} saves: " + answer.ToJsonString());
            return answer["result"]!.AsObject();
        }
        public Task<JsonObject> Save(string id, JsonObject definition, long revision = 0) => Ok(new { operation = "save", id, expectedRevision = revision, definition });
        public Task<JsonObject> Read(string id) => Ok(new { operation = "read", id });
        /// <summary>Saves the definition, checks that it runs and gives its host once it has started.</summary>
        public async Task<DefinitionHost> Running(string id, JsonObject definition, long revision = 0)
        {
            var saved = await Save(id, definition, revision);
            Check(saved["active"]!.GetValue<bool>() && saved["activationError"] == null, $"{id} runs: " + saved.ToJsonString());
            return await Started(id);
        }
        /// <summary>The definition's host once it has started, checked to be running.</summary>
        public async Task<DefinitionHost> Started(string id)
        {
            var host = Scripts.Entities.Host(id) ?? throw new Exception($"{id} has no host");
            await host.Started;
            Check(host.Status == "running", $"{id} is running: {host.Status} {host.Error}");
            return host;
        }
        public UserScriptManager Scripts => (UserScriptManager)typeof(HassSharpManager).GetField("_userScriptManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(Api)!;
        /// <summary>The value of <paramref name="name"/> the entity last showed, as JSON; null before it showed one.</summary>
        public string? Shown(string uniqueId, string name = "value") =>
            Home.Snapshots().LastOrDefault(s => s["uniqueId"]!.GetValue<string>() == uniqueId)?["values"]?[name]?.ToJsonString();
        /// <summary>What a node's data holds under the key, as stored; null when it holds nothing there.</summary>
        public string? Stored(string definitionId, string nodeKey, string key) =>
            Path.Combine(Home.Root, ".storage", "hass_sharp", "data", "devices", definitionId, nodeKey, key) is var file && File.Exists(file) ? File.ReadAllText(file) : null;
        public async ValueTask DisposeAsync()
        {
            await Api.UnloadAsync();
            await Home.DisposeAsync();
        }
    }
}

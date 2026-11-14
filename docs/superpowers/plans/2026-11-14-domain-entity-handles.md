# Domain-Typed Entity Handles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A node method parameter declared `BinarySensorEntity` is a pin only binary_sensor entities reach, with a typed `State` and attributes in code and the same members readable in the graph through Select property.

**Architecture:** One class per Home Assistant domain is generated at startup from Home Assistant's `entity_component` translations, pushed by Python and built by Roslyn into its own assembly beside the typed service calls. A pin of type `XEntity` is the editor's existing `entity_id:<domain>` type; wires and traces keep carrying entity id strings, and the runtime makes the handle only when node code or Select property reads it.

**Tech Stack:** C# / .NET 11 / Roslyn, Python (Home Assistant 2026.11 integration, Python.NET), TypeScript / SolidJS / Bun.

**Spec:** `docs/superpowers/specs/2026-11-14-domain-entity-handles-design.md`

## Global Constraints

- Paths are under `custom_components/hass_sharp/` unless they start with `tests/` or `docs/`.
- Work in a worktree from `main`. Task 1 starts by committing the spec and this plan into it.
- A graph valid today stays valid; no saved graph is migrated.
- Home Assistant text reaches generated code only as identifiers made by `ServiceGenerator.Words`, string literals made by `SymbolDisplay.FormatLiteral`, or doc text made by `ServiceGenerator.Xml`.
- One domain whose generated code does not compile is left out; the rest are kept.
- A service push must not change the entities assembly or the library generation.
- Nothing is cached by `System.Type` across runs (collectible load contexts).
- Generated types live in namespace `HassSharp.Entities`, assembly `HassSharpEntities_<16 hex of the source's SHA-256>`.
- Catalog type ids: `entity_id` for `Entity`, `entity_id:<domain>` for a domain class. Enum ids are the CLR identity, e.g. `global::HassSharp.Entities.LightState`.
- A generated enum's `enumValues` are Home Assistant's texts (`"on"`), never numbers; the catch-all member `Other` has no text and is not in `enumValues`.
- `sensor` always has `string State`.
- Suites, from the repository root: `dotnet run --project tests/Runtime --disable-build-servers [-- --flag]`, `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`, `python3 -m unittest discover -s tests/python`, `(cd custom_components/hass_sharp/frontend && bun run test)`.
- Commit after each task; end commit messages with `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.

## Review Focus

1. **An entity that does not exist when a handle is read** (removed, or not yet created at startup): the reading step fails with a message naming the entity id and takes its error pin; nothing else stops. Test in Task 1.
2. **An attribute whose value is not what the enum expects** (`color_mode` holding a number or a list, an effect not in the list): the property reads `Other`, and a `bool?` property reads null; no exception. Test in Task 3.
3. **A Home Assistant update that drops a state a saved graph names** (a Compare literal `"jammed"`): the validator reports that value on that step, the automation fails to load with that message, and other automations keep running. Test in Task 7.
4. **First start with no saved schema**: an automation whose code names `LightEntity` fails to compile before the first push and loads after it without a restart. Test in Task 4.
5. **Two domains whose names collide after PascalCase** (`input_boolean` and a custom `input__boolean`): both get a class, the first id in ordinal order keeps the plain name, and the names do not move when an unrelated domain appears. Test in Task 3.

---

## File Structure

| File | Responsibility |
|---|---|
| `HassSharp/Entities/Entity.cs` (new) | The base handle, its JSON converter, `EntityDomainAttribute`, `EntityTextAttribute`, `EntityText` |
| `HassSharp/Visual/EntitySchemaCatalog.cs` (new) | Normalises Python's push into a per-domain schema |
| `HassSharp/EntityGenerator.cs` (new) | Schema to C# source |
| `HassSharp/DiagnosticsProvider.cs` | `GeneratedEntities`, `UseEntitySchema`, the swap |
| `HassSharp/Visual/ServiceCallSchema.cs` | Saved-schema file, now for either schema |
| `HassSharp/Manager.cs` | `SetEntityDescriptions`, refresh, retry |
| `HassSharp/Visual/NodeLibraryCompiler.cs` | Pin ids for handles, catalog members, text enums |
| `HassSharp/Visual/VisualRuntime.cs`, `TraceJson.cs` | Id to handle and back, text enums, Select property |
| `HassSharp/Visual/VisualTypes.cs`, `VisualNodeCatalog.cs`, `frontend/src/visual/contract.ts` | Members on `entity_id[:domain]`, on both validators |
| `frontend/src/visual/widgets/model.ts`, `flow/flowModel.ts`, `NodeConfig.tsx` | Compare dropdown, hidden types |
| `entities.py` (new), `__init__.py` | The push |
| `tests/Runtime/EntityHandleTests.cs`, `EntitySchemaTests.cs`, `EntityGeneratorTests.cs` (new) | Runtime suites `--entity-handles`, `--entity-schema`, `--entity-generator` |
| `tests/python/test_entity_schema.py` (new) | The push |
| `tests/fixtures/ha-entity-components.json` (new) | Captured Home Assistant 2026.11.1 data |

Runtime test files follow `tests/Runtime/ServiceCallTests.cs`: a static class, a `Check(bool, string)` helper, one `[RuntimeSuite("--flag", Order = n)]` method that saves and restores the `PyInterop` delegates and `HassPath.HassConfiguration`. Fixtures in `tests/fixtures/*.json` are copied beside the test binary and read with `File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name))`.

---

### Task 1: `Entity` and the runtime's id/handle conversions

**Files:**
- Create: `HassSharp/Entities/Entity.cs`
- Modify: `HassSharp/Visual/NodeContextServices.cs` (`ServiceTarget`), `HassSharp/Visual/VisualRuntime.cs` (constructor, `Coerce`, `Input<T>`, the immutable list in `Copy`), `HassSharp/Visual/TraceJson.cs` (beside the `EntityStateSnapshot` case), `HassSharp/DiagnosticsProvider.cs` (`GlobalUsings`), `frontend/src/visual/csharpType.ts` (`inScope`)
- Test: `tests/Runtime/EntityHandleTests.cs`

**Interfaces:**
- Consumes: `VisualRuntime.ReadEntity(string, bool)`, `EntityStateSnapshot`.
- Produces:

```csharp
namespace HassSharp.Entities;

[AttributeUsage(AttributeTargets.Class)]
public sealed class EntityDomainAttribute(string domain) : Attribute { public string Domain { get; } }

[AttributeUsage(AttributeTargets.Field)]
public sealed class EntityTextAttribute(string text) : Attribute
{ public string Text { get; } public string? Label { get; set; } }

public static class EntityText
{
    public static string? Of(Enum value);                    // the member's text; null without the attribute
    public static object? Parse(Type enumType, string text); // the member with that text; null when none
    public static bool Marked(Type enumType);                // any member has the attribute
}

[JsonConverter(typeof(EntityJsonConverter))]   // writes the id; reads T : Entity through its (string) constructor
public class Entity
{
    public Entity(string entityId);            // ArgumentException unless ^[a-z0-9_]+\.[a-z0-9_]+$
    public string EntityId { get; }
    public string RawState { get; }
    public double? NumericState { get; }       // invariant-culture parse of RawState, finite, else null
    public JsonElement Attributes { get; }
    public JsonElement? Attribute(string name);
    public EntityStateSnapshot Snapshot();
    public override string ToString();         // the id
    public static implicit operator string(Entity entity);
}
```

`Equals`/`GetHashCode` are by `EntityId`. `EntityText` reflects on each call and keeps no cache.
`ServiceTarget`: `public static implicit operator ServiceTarget?(Entity? entity)` and `(Entity[]? entities)`.
`VisualRuntime`: a last optional constructor parameter `Func<string, Entity>? entities = null` (default `id => new Entity(id)`); `Coerce` gains a last optional parameter of the same type.

- [ ] **Step 1: Create the worktree and commit the documents**

Use superpowers:using-git-worktrees for branch `domain-entity-handles` from `main`. Copy `docs/superpowers/specs/2026-11-14-domain-entity-handles-design.md` and this file from the main checkout into it and commit them.

- [ ] **Step 2: Write the failing suite**

`tests/Runtime/EntityHandleTests.cs`, `[RuntimeSuite("--entity-handles", Order = 37)]`:

```csharp
static void Basics()
{
    PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["brightness"] = 128 } };
    var light = new Entity("light.kitchen");
    Check(light.EntityId == "light.kitchen" && (string)light == "light.kitchen" && light.ToString() == "light.kitchen", "id");
    Check(light.RawState == "on" && light.NumericState == null, "state");
    Check(light.Attribute("brightness")?.GetInt32() == 128 && light.Attribute("missing") == null, "attribute");
    Check(light.Snapshot().State == "on", "snapshot");
    Check(light.Equals(new Entity("light.kitchen")) && !light.Equals(new Entity("light.hall")), "equality");
    PyInterop.Entity = id => new() { EntityId = id, State = "21.5" };
    Check(new Entity("sensor.t").NumericState == 21.5, "numeric");
    Throws<ArgumentException>(() => new Entity("not an id"), "bad id");
}
static void Missing()   // Review Focus 1
{
    PyInterop.Entity = _ => null!;
    var error = Throws<InvalidOperationException>(() => _ = new Entity("light.gone").RawState, "missing");
    Check(error.Message.Contains("light.gone"), "names the entity");
}
static void Conversions()
{
    Check(VisualRuntime.Coerce("light.kitchen", typeof(Entity), "x", false) is Entity { EntityId: "light.kitchen" }, "id to handle");
    Check(VisualRuntime.Coerce(new Entity("light.kitchen"), typeof(string), "x", false) is "light.kitchen", "handle to id");
    Check(VisualRuntime.Coerce(null, typeof(Entity), "x", false) == null, "null stays null");
    ServiceTarget? target = new Entity("light.kitchen"); ServiceTarget? same = "light.kitchen";
    Check(target!.ToJson()!.ToJsonString() == same!.ToJson()!.ToJsonString(), "target");
    Check(JsonSerializer.Serialize(new Entity("light.kitchen")) == "\"light.kitchen\"", "json");
    Check(JsonSerializer.Deserialize<Entity>("\"light.kitchen\"")!.EntityId == "light.kitchen", "json back");
    Check(TraceJson.Value(new Entity("light.kitchen"))!.ToJsonString() == "\"light.kitchen\"", "trace");   // use TraceJson's existing entry point
}
```

Also:
- `TriggerState`: inside a run started by an entity change (set up as `ServiceCallTests.EntityTrigger` does), a handle for the triggering entity reads the trigger's state while `PyInterop.Entity` returns another.
- `OwnNames`: a node library declaring `public record Entity(string Name);` and `public record LightEntity(string Name);` with a `[Node]` method returning each previews `Valid`.
- `Copied`: a handle wired into a For each body is read there (it is copied as an immutable value).

- [ ] **Step 3: Run it and see it fail to compile**

Run: `dotnet run --project tests/Runtime --disable-build-servers -- --entity-handles`
Expected: build errors naming `Entity`.

- [ ] **Step 4: Implement `Entity.cs` and the `ServiceTarget` operators**

Reads go through `VisualRuntime.ReadEntity(EntityId)`, which already throws `Entity '<id>' is unavailable.` for a missing entity.

- [ ] **Step 5: Implement the runtime conversions**

In `Coerce`, before the final `throw`: a string for a target assignable to `Entity` goes through the factory, and the result must be an instance of the target, else `InvalidOperationException($"{pin?.Invoke() ?? label} needs {target.Name}, and {id} is not one")`; an `Entity` for target `string` gives its id. `Input<T>` passes the instance's factory and traces the id, not the handle. Add `Entity` to the immutable values in `Copy` and to `TraceJson`'s special values (written as its id). Add `global using HassSharp.Entities;` to `GlobalUsings` and `'HassSharp.Entities'` to `inScope` in `csharpType.ts`.

- [ ] **Step 6: Run the suite, then everything**

Run: `dotnet run --project tests/Runtime --disable-build-servers -- --entity-handles`, then with no flag, then `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`.
Expected: all pass.

- [ ] **Step 7: Commit** — `Add the Entity handle and its id conversions`

---

### Task 2: The entity schema and its saved copy

**Files:**
- Create: `HassSharp/Visual/EntitySchemaCatalog.cs`, `tests/fixtures/ha-entity-components.json`, `tests/Runtime/EntitySchemaTests.cs`
- Modify: `HassSharp/Visual/ServiceCallSchema.cs`

**Interfaces:**
- Produces:

```csharp
sealed class EntitySchemaCatalog
{
    public static EntitySchemaCatalog Empty { get; }
    public static EntitySchemaCatalog Parse(string json);   // JsonException for a non-object or a missing part
    public JsonObject Items { get; }                         // domain -> schema, ordinal order
    public int Count { get; }
}
// ServiceCallSchema(string path, string what = "service")   // what: "service" | "entity", used in log lines
// public IReadOnlyCollection<KeyValuePair<string, JsonObject>>? Update(JsonObject liveItems, bool started)
```

Input (what Python sends in Task 9):
`{"domains": ["light", ...], "translations": {"component.light.entity_component._.state.on": "On", ...}}`

Normalised item, per domain:
`{"states": [{"id": "off", "label": "Off"}], "attributes": {"color_mode": {"label": "Color mode", "values": [{"id": "hs", "label": "Hue/Saturation"}]}}}`

Rules:
- A domain is kept only when it matches `^[a-z0-9_]+$`. Every domain in `domains` gets an item, with empty `states` and `attributes` when it has no keys; so does a domain that has keys but is not in `domains`.
- A key counts only in the shapes `component.<domain>.entity_component.<class>.state.<id>`, `...state_attributes.<attr>.name` and `...state_attributes.<attr>.state.<id>`; `<id>` may contain dots after that prefix and is taken whole. An id or attribute name that is empty or longer than 100 characters is dropped.
- States, attributes and values are the union over device classes, in ordinal order of id. A label comes from device class `_` when it has one, else from the first class in ordinal order.

- [ ] **Step 1: Capture the fixture**

```sh
docker exec -i hass python - > tests/fixtures/ha-entity-components.json <<'EOF'
import json, pathlib, homeassistant
root = pathlib.Path(homeassistant.__file__).parent / "components"
out = {}
def walk(prefix, node):
    for key, value in node.items():
        if isinstance(value, dict): walk(f"{prefix}.{key}", value)
        else: out[f"{prefix}.{key}"] = value
domains = set()
for path in sorted(root.glob("*/translations/en.json")):
    body = json.loads(path.read_text()).get("entity_component")
    if body: domains.add(path.parent.parent.name); walk(f"component.{path.parent.parent.name}.entity_component", body)
print(json.dumps({"domains": sorted(domains | {"scene", "zone"}), "translations": dict(sorted(out.items()))}, indent=1))
EOF
```

Expected: a file with at least 56 domains, `scene` among them, whose translations include `component.binary_sensor.entity_component._.state.on`. If `scene` turns out to have keys, use another domain without any for the "no metadata" checks here and in Task 3.

- [ ] **Step 2: Write the failing suite**

`[RuntimeSuite("--entity-schema", Order = 38)]`:

```csharp
var schema = EntitySchemaCatalog.Parse(Fixture("ha-entity-components.json"));
string[] Ids(JsonNode? list) => list!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToArray();
Check(Ids(schema.Items["binary_sensor"]!["states"]).SequenceEqual(["off", "on"]), "binary_sensor");
Check(Ids(schema.Items["lock"]!["states"]).SequenceEqual(["jammed", "locked", "locking", "open", "opening", "unlocked", "unlocking"]), "lock");
Check(Ids(schema.Items["light"]!["attributes"]!["color_mode"]!["values"]).Contains("hs"), "color modes");
Check(schema.Items["light"]!["attributes"]!["brightness"]!["values"]!.AsArray().Count == 0, "brightness has no values");
Check(Ids(schema.Items["weather"]!["states"]).Contains("clear-night"), "hyphen kept");
Check(schema.Items["scene"]!["states"]!.AsArray().Count == 0, "a domain without metadata");
var hostile = EntitySchemaCatalog.Parse("""{"domains":["Bad Domain","ok"],"translations":{"component.ok.entity_component._.state.":"x","component.ok.other.a":"x","component.Bad.entity_component._.state.on":"x"}}""");
Check(hostile.Count == 1 && hostile.Items["ok"]!["states"]!.AsArray().Count == 0, "hostile keys dropped");
Throws<JsonException>(() => EntitySchemaCatalog.Parse("[]"), "not an object");
```

Persistence, with a `ServiceCallSchema(path, "entity")` over a temporary file: `Update(a, started: false)` then `Accept` saves; a new instance's `Update(b, false)` returns the union of the saved and `b`; `Update(b, true)` returns only `b`; the same schema proposed twice returns null the second time; a corrupt file is ignored.

- [ ] **Step 3: Run it and see it fail** — `-- --entity-schema`, expected: build error naming `EntitySchemaCatalog`.

- [ ] **Step 4: Implement `EntitySchemaCatalog` and the `ServiceCallSchema` changes**

The existing `Update(ServiceSchemaCatalog, bool)` calls the new overload with `live.Items`.

- [ ] **Step 5: Run `--entity-schema` and `--service-calls`** — expected: pass.

- [ ] **Step 6: Commit** — `Normalise and keep Home Assistant's entity states and attributes`

---

### Task 3: `EntityGenerator`

**Files:**
- Create: `HassSharp/EntityGenerator.cs`, `tests/Runtime/EntityGeneratorTests.cs`
- Modify: `HassSharp/ServiceGenerator.cs` (`Assign` and `Literal` become `internal`)

**Interfaces:**
- Consumes: Task 1's `Entity`, `EntityDomainAttribute`, `EntityTextAttribute`; Task 2's item shape; `ServiceGenerator.Words`, `Assign`, `Literal`, `Xml`, `Escape`, `Span`.
- Produces:

```csharp
static class EntityGenerator
{
    public const string Namespace = "HassSharp.Entities";
    public const string Factory = "EntityHandles";
    public sealed record Generated(string Source, IReadOnlyList<ServiceGenerator.Span> Spans)
    { public IEnumerable<string> DomainsAt(int line); }
    public static Generated Generate(IEnumerable<KeyValuePair<string, JsonObject>> domains);
}
```

Generated source, for `light`:

```csharp
namespace HassSharp.Entities
{
    [global::HassSharp.Entities.EntityDomain("light")]
    public sealed class LightEntity : global::HassSharp.Entities.Entity
    {
        public LightEntity(string entityId) : base(entityId) { /* ArgumentException unless the id starts with "light." */ }
        public LightState State => ...;                 // switch over RawState's string literals; default => Other
        public LightColorMode? ColorMode => ...;        // null when absent; a JSON string by switch; anything else => Other
        public global::System.Text.Json.JsonElement? Brightness => Attribute("brightness");
    }
    public enum LightState
    {
        [global::HassSharp.Entities.EntityText("off", Label = "Off")] Off,
        [global::HassSharp.Entities.EntityText("on", Label = "On")] On,
        [global::HassSharp.Entities.EntityText("unknown", Label = "Unknown")] Unknown,
        [global::HassSharp.Entities.EntityText("unavailable", Label = "Unavailable")] Unavailable,
        Other,
    }
    public static class EntityHandles
    {
        public static global::HassSharp.Entities.Entity Create(string entityId) => /* switch on the text before the first '.' */;
    }
}
```

Rules:
- **State:** a domain other than `sensor` with at least one state gets `<Class>State`: its states in ordinal order, then `unknown` and `unavailable` when not already listed, then the catch-all. Otherwise `public string State => RawState;`.
- **Attributes:** values exactly `false` and `true` give `bool?` (JSON true/false, else null). Other listed values give a nullable enum `<Domain><Attribute>` with the catch-all last. When several attributes of a domain list the identical value set, only the shortest name (ordinal first on a tie) is the enum. Everything else is `JsonElement?`.
- **Names:** type names (classes, enums, `EntityHandles`) are assigned together with `Assign` over ordinal ids, reserving `Entity`, `EntityHandles`, `EntityText`, `EntityTextAttribute`, `EntityDomainAttribute`, `EntityJsonConverter`. Property names reserve `EntityId`, `RawState`, `NumericState`, `Attributes`, `Attribute`, `Snapshot`, `State`, the `object` members and the class's own name. Enum members: listed texts first, then the added ones, each taking the lowest free name (`Other`, else `Other2`).
- **Spans:** each domain's class and enums are contiguous and recorded as one span; its arm in `EntityHandles.Create` is on its own line and recorded as a second span for the same domain. Numbering is implicit (0, 1, ...).
- The source depends only on the schema, byte for byte.

- [ ] **Step 1: Write the failing suite**

`[RuntimeSuite("--entity-generator", Order = 39)]`, with `Source(json)` = `EntityGenerator.Generate(EntitySchemaCatalog.Parse(json).Items...)` and `Load(source)` compiling it against `NodeLibraryCompiler.FrameworkReferences()` and loading it into a collectible context:

```csharp
var source = Source(Fixture("ha-entity-components.json"));
var assembly = Load(source);                                             // compiles
Check(source == Source(Fixture("ha-entity-components.json")), "deterministic");
Check(source.Contains("public sealed class BinarySensorEntity") && source.Contains("public enum BinarySensorState"), "binary_sensor");
Check(source.Contains("EntityText(\"clear-night\"") && source.Contains("ClearNight"), "hyphenated state");
Check(!source.Contains("enum SensorState") && Member(assembly, "SensorEntity", "State").PropertyType == typeof(string), "sensor");
Check(Member(assembly, "SceneEntity", "State").PropertyType == typeof(string), "no metadata");
Check(Member(assembly, "LightEntity", "Brightness").PropertyType == typeof(JsonElement?), "json attribute");
Check(Member(assembly, "LightEntity", "SupportedColorModes").PropertyType == typeof(JsonElement?), "same list, longer name");
Check(Member(assembly, "LightEntity", "ColorMode").PropertyType.Name.StartsWith("Nullable"), "enum attribute");
Check(Member(assembly, "MediaPlayerEntity", "IsVolumeMuted").PropertyType == typeof(bool?), "flag");

var create = assembly.GetType("HassSharp.Entities.EntityHandles")!.GetMethod("Create")!;
PyInterop.Entity = id => new() { EntityId = id, State = "jammed" };
var front = (Entity)create.Invoke(null, ["lock.front"])!;
Check(front.GetType().Name == "LockEntity" && State(front) == "Jammed", "listed state");
PyInterop.Entity = id => new() { EntityId = id, State = "weird" };          Check(State(front) == "Other", "off-list state");
PyInterop.Entity = id => new() { EntityId = id, State = "unavailable" };    Check(State(front) == "Unavailable", "unavailable");
Check(create.Invoke(null, ["made_up.thing"])!.GetType() == typeof(Entity), "unknown domain");

// Review Focus 2
PyInterop.Entity = id => new() { EntityId = id, State = "on", Attributes = new() { ["color_mode"] = 5, ["is_volume_muted"] = "yes" } };
Check(Get(create.Invoke(null, ["light.a"])!, "ColorMode")!.ToString() == "Other", "a number where text was expected");
Check(Get(create.Invoke(null, ["media_player.a"])!, "IsVolumeMuted") == null, "text where a flag was expected");

// Review Focus 5
// "input__boolean" sorts before "input_boolean", so it keeps the plain name.
string Names(string domains) => string.Join(",", Regex.Matches(Source($$"""{"domains":[{{domains}}],"translations":{}}"""), @"\[global::HassSharp\.Entities\.EntityDomain\(""(\w+)""\)\]\s+public sealed class (\w+) ").Select(m => m.Groups[1].Value + "=" + m.Groups[2].Value));
Check(Names("\"input_boolean\",\"input__boolean\"") == "input__boolean=InputBooleanEntity,input_boolean=InputBooleanEntity2", "collision");
Check(Names("\"a\",\"zone\",\"input_boolean\",\"input__boolean\"").Contains("input__boolean=InputBooleanEntity,input_boolean=InputBooleanEntity2"), "names do not move");

// Hostile text stays inside a literal
var hostile = Source("""{"domains":["x"],"translations":{"component.x.entity_component._.state.on\"); System.IO.File.Delete(\"y\"); //":"l\n*/"}}""");
Load(hostile);
```

- [ ] **Step 2: Run it and see it fail** — `-- --entity-generator`, expected: build error naming `EntityGenerator`.

- [ ] **Step 3: Implement `EntityGenerator.Generate`**

- [ ] **Step 4: Run `--entity-generator` and `--service-calls`** — expected: pass.

- [ ] **Step 5: Commit** — `Generate a class per entity domain from Home Assistant's lists`

---

### Task 4: Build, install and deliver the entities assembly

**Files:**
- Modify: `HassSharp/DiagnosticsProvider.cs`, `HassSharp/ScriptLoadContext.cs`, `HassSharp/Compiler.cs`, `HassSharp/Visual/NodeLibraryCompiler.cs` (the compilation's references), `HassSharp/Visual/VisualGenerator.cs` (references), `HassSharp/Visual/NodeLibraryApi.cs` (`LibraryWorkspaceGeneration`), `HassSharp/Manager.cs`
- Test: `tests/Runtime/EntityGeneratorTests.cs` (new methods), `tests/Runtime/ServiceCallTests.cs` (`LibraryGeneration`)

**Interfaces:**
- Consumes: `EntityGenerator.Generate`, `EntitySchemaCatalog`, `ServiceCallSchema(path, "entity")`.
- Produces:

```csharp
public sealed class GeneratedEntities
{
    public const string AssemblyPrefix = "HassSharpEntities_";
    public string Source { get; }  public SyntaxTree Tree { get; }  public string AssemblyName { get; }
    public byte[] Image { get; }   public MetadataReference Reference { get; }
    internal static GeneratedEntities? Build(string source, IEnumerable<KeyValuePair<string, JsonObject>> domains, out Diagnostic[] errors);
}
// GeneratedServiceCalls
public GeneratedEntities Entities { get; }
public ImmutableArray<MetadataReference> References { get; }   // the service calls, then the entities
// DiagnosticsProvider
public const string EntitiesPath = "Entities.g.cs";
internal sealed record EntitySchemaResult(bool Accepted, bool Changed, bool Grew,
    IReadOnlyList<KeyValuePair<string, JsonObject>> Domains, IReadOnlyList<string> Excluded, IReadOnlyList<string> Errors);
internal EntitySchemaResult UseEntitySchema(IReadOnlyCollection<KeyValuePair<string, JsonObject>> domains);
internal Func<IEnumerable<KeyValuePair<string, JsonObject>>, EntityGenerator.Generated> EntitiesGenerator { get; set; }
// Manager
public void SetEntityDescriptions(string json, bool started = false);
```

`UseEntitySchema` follows `UseServiceSchema`: three attempts, excluding the domains at the failing lines, and installing by replacing the one `GeneratedServiceCalls` object under `_gate` (a new object carrying the same service calls and the new entities), plus the workspace document `Entities.g.cs`. A service schema swap keeps the current entities. The constructor installs the empty schema (only `EntityHandles`), which must compile. `ScriptLoadContext` keeps both names and images. `Manager` keeps the schema in `.storage/hass_sharp/entity_schema.json`, refreshes in `Init` beside `RefreshServiceCalls()`, and calls `RetryFailedLoads()` when the result grew. `LibraryWorkspaceGeneration` adds `"\0entities\0" + AssemblyName` whenever a preview is given.

- [ ] **Step 1: Write the failing tests**

In `EntityGeneratorTests`:
- `Installed`: after `UseEntitySchema(fixture)` and a service schema with `light.turn_on`, a visual automation with the Custom C# node `public static async Task<bool> Run(BinarySensorEntity s, LightEntity light, NodeContext context) { await context.Services.Light.TurnOn(light); return context.State(s).State == "on" && s.State == BinarySensorState.On; }` compiles (as `CustomNodeTests` compiles one), and completion in a library project offers `LightEntity`.
- `ServicesLeaveEntitiesAlone`: `var before = provider.ServiceCalls().Entities.AssemblyName; provider.UseServiceSchema(other);` then the name is unchanged and `ReferenceEquals` of the carrier is false.
- `BrokenDomain`: with `EntitiesGenerator` set to break one domain's class, `Excluded` is `["light"]`, `Accepted` is true and `LockEntity` still compiles.
- `SwappedWhileRunning`: an automation compiled against one schema keeps reading `State` after a second schema is installed (as `ServiceCallTests.SwappedWhileRunning`).
- `FirstStart` (Review Focus 4): a manager with no saved schema and a saved automation naming `LightEntity` fails to load in `Init`; after `SetEntityDescriptions(fixture, started: true)` and the retry it is loaded.
- `Persisted`: a second manager over the same directory compiles that automation in `Init` with no push.
- `MalformedPush`: `SetEntityDescriptions("[]")` logs, throws nothing and keeps the previous classes.

In `ServiceCallTests.LibraryGeneration`: add that a service push still leaves the generation unchanged for a library with a `LightEntity` pin, and that an entity push changes it.

- [ ] **Step 2: Run them and see them fail** — `-- --entity-generator`, expected: build errors.

- [ ] **Step 3: Implement `GeneratedEntities`, `UseEntitySchema` and the carrier changes**

- [ ] **Step 4: Add both references at the three compile sites; make `ScriptLoadContext` serve either image**

- [ ] **Step 5: Implement the `Manager` and `LibraryWorkspaceGeneration` changes**

- [ ] **Step 6: Run the suites**

Run: `-- --entity-generator`, `-- --service-calls`, `-- --reload-memory`, then no flag, then the editor tests.
Expected: all pass.

- [ ] **Step 7: Commit** — `Build the entity classes at startup and give them to every compile`

---

### Task 5: Handle pins

**Files:**
- Modify: `HassSharp/Visual/NodeLibraryCompiler.cs` (`Preview`, `Describe`), `HassSharp/Visual/VisualGenerator.cs` (the `new VisualRuntime(...)` it emits)
- Test: `tests/Runtime/EntityHandleTests.cs`

**Interfaces:**
- Consumes: `GeneratedServiceCalls.Entities`, `EntityDomainAttribute`, the runtime factory parameter from Task 1.
- Produces: in `Preview`, a local `string PinType(ITypeSymbol type)`: `entity_id` for `HassSharp.Entities.Entity`; `entity_id:<domain>` for a class in an assembly whose name starts with `GeneratedEntities.AssemblyPrefix` carrying `[EntityDomain]`; otherwise `TypeIdentity(type)`. A nullable annotation is ignored for the id. Messages:

```csharp
internal const string EntityResultMessage = "A node returns an entity, or nothing at all: remove the ? from this entity type";
internal const string EntityNestedMessage = "An entity cannot be inside a list or another type on a pin; take or return one entity";
```

Rules: `PinType` is used for parameters (`NodeParameterDescriptor.TypeId`; `TypeExpression` keeps the C# identity), results, tuple elements and flow values. A handle type is not registered as a catalog type. A nullable or defaulted handle parameter is `required: false` with no `default` literal. A nullable handle result, tuple element or flow value reports `EntityResultMessage`; a handle inside an array, a generic argument or a tuple-typed parameter reports `EntityNestedMessage`. The emitted runtime gets `global::HassSharp.Entities.EntityHandles.Create` as its factory.

- [ ] **Step 1: Write the failing tests**

`EntityHandleTests.Pins`, with a provider given the fixture and this library:

```csharp
public static class Motion
{
    [Node("Is on")] public static bool IsOn(BinarySensorEntity sensor) => sensor.State == BinarySensorState.On;
    [Node("Id")] public static string Id(Entity entity) => entity.EntityId;
    [Node("Pick")] public static LightEntity Pick(LightEntity light, LightEntity? other = null) => other ?? light;
}
```

```csharp
Check(Types(node("IsOn"), "sensor").SequenceEqual(["entity_id:binary_sensor"]), "domain pin");
Check(Types(node("Id"), "entity").SequenceEqual(["entity_id"]), "any entity");
Check(node("Pick")["outputs"]!["result"]!["type"]!.GetValue<string>() == "entity_id:light", "result");
Check(node("Pick")["inputs"]!["other"]!["required"]!.GetValue<bool>() == false && node("Pick")["inputs"]!["other"]!["default"] == null, "optional");
Check(!catalog["types"]!.AsObject().ContainsKey("global::HassSharp.Entities.LightEntity"), "no second type");
```

Graphs (validated and run as `NodeLibraryTests` does):
- an entity trigger on `binary_sensor.hall`, its `entity_id` wired to `IsOn.sensor`, validates; with `PyInterop.Entity` giving state `on` the result is true, and the trace shows the input as `"binary_sensor.hall"`;
- the same with the trigger on `light.hall` is refused with the existing wrong-type message;
- an unwired `IsOn.sensor` with literal `{type: "entity_id:binary_sensor", value: "binary_sensor.hall"}` runs; a literal `light.hall` is refused;
- `Pick.result` wired to a Call service `light.turn_on` target validates and the call gets `light.kitchen`;
- `public static LightEntity? Find()` reports `EntityResultMessage`; `public static int Count(List<LightEntity> lights)` reports `EntityNestedMessage`.

Custom C# node: `public static bool Run(BinarySensorEntity motionSensor) => ...` derives `signature.inputs.motionSensor.types == ["entity_id:binary_sensor"]`, and deriving again leaves the pins the same (`SamePins`).

- [ ] **Step 2: Run them and see them fail** — `-- --entity-handles`, expected: the first `Check` fails with `domain pin`.

- [ ] **Step 3: Implement `PinType` and its uses in `Describe`; pass the factory in `VisualGenerator`**

- [ ] **Step 4: Run** `-- --entity-handles`, `-- --node-library`, `-- --custom-nodes`, `-- --flow-outputs` — expected: pass.

- [ ] **Step 5: Commit** — `Type a node's entity pins by domain`

---

### Task 6: Members and text enums in the catalog, on both validators

**Files:**
- Modify: `HassSharp/Visual/NodeLibraryCompiler.cs` (`Preview`, `PropertyOwner`, `DescribeType`), `HassSharp/Visual/VisualTypes.cs` (`Resolve`), `HassSharp/Visual/VisualNodeCatalog.cs` (`TypeProperties`), `HassSharp/Visual/VisualGenerator.cs` (the embedded catalog), `frontend/src/visual/contract.ts` (`resolveTypeSpec`, `typeProperties`, the `TypeSpec` and parametric types), `frontend/src/visual/NodeConfig.tsx` and `constantTypes` (type lists), `frontend/src/visual/csharpType.ts`, `tests/Runtime/NodeLibraryFixture.cs`
- Test: `tests/Runtime/SelectPropertyTests.cs`, `tests/Runtime/VisualTypesTests.cs`, `frontend/tests/visual-contract.test.ts`, `frontend/tests/select-property.test.tsx`

**Interfaces:**
- Produces, in the catalog `Preview` returns:
  - `types.entity_id.properties`: the members of `Entity`, in the existing property entry shape.
  - `parametricTypes.entity_id.members.<domain> = { "clrType": "HassSharp.Entities.LightEntity", "properties": [...] }` for each generated class.
  - For each enum from the entities assembly: `literalKind: "enum"`, `enumValues` (the `EntityText` texts, in member order), `enumLabels` (the `Label`s), `listed: false`.
- `VisualTypes.Resolve` and `resolveTypeSpec`: a resolved `entity_id:<domain>` gets `properties` from `members.<domain>`, else from `types.entity_id`. `TypeProperties` / `typeProperties` read `properties` from the resolved spec.
- `TypeSpec.listed?: boolean`; a type with `listed === false` is left out of the Constant, Convert and loop type lists.
- The catalog embedded in a compiled automation keeps only the `members` of domains its graph names.

- [ ] **Step 1: Regenerate the shared fixture source**

Make `NodeLibraryFixture.Build` use a provider given `ha-entity-components.json`, so `tests/fixtures/visual-rich-catalog.json` carries the members. Do not write the file yet.

- [ ] **Step 2: Write the failing tests**

`SelectPropertyTests`:

```csharp
string[] Names(string type) => VisualNodeCatalog.TypeProperties(catalog, type).Select(p => p["name"]!.GetValue<string>()).ToArray();
Check(Names("entity_id").SequenceEqual(["EntityId", "RawState", "NumericState", "Attributes"]), "base members");
Check(Names("entity_id:light").Take(4).SequenceEqual(["EntityId", "RawState", "NumericState", "Attributes"]) && Names("entity_id:light").Contains("State") && Names("entity_id:light").Contains("Brightness"), "light members");
Check(Names("entity_id:made_up").SequenceEqual(Names("entity_id")), "an unknown domain has the base members");
var state = catalog["types"]!["global::HassSharp.Entities.BinarySensorState"]!;
Check(state["enumValues"]!.ToJsonString() == """["off","on","unknown","unavailable"]""" && state["listed"]!.GetValue<bool>() == false, "text enum");
```

and a graph: entity trigger on `light.hall`, its `entity_id` into a Select property with `property: "State"`, validates and resolves the output to `global::HassSharp.Entities.LightState`; the same from a plain `string` output is refused.

`VisualTypesTests`: `ValidLiteral(catalog, "global::HassSharp.Entities.BinarySensorState", "on", true)` is true, and false for `"jammed"` and for `1`.

`frontend/tests/visual-contract.test.ts`, over the regenerated fixture: the same three `typeProperties` expectations, `validLiteral` for `"on"` / `"jammed"`, and that `constantTypes(catalog)` holds no id starting with `global::HassSharp.Entities.`. `select-property.test.tsx`: the picker for a wire from a `light` trigger lists State and Brightness.

- [ ] **Step 3: Run them and see them fail** — `-- --select-property`, expected: `base members` fails.

- [ ] **Step 4: Implement the C# side**

`PropertyOwner` also accepts the entities assembly. Register `Entity` and each generated class's member types so the enums get entries. Before keeping the pairwise `assignableTo` pass for them, time `Preview` over the fixture with a stopwatch in the test; if it is more than 20% slower than before this task, give entities-assembly enums `assignableTo` of only themselves and their nullable form without classifying against every type.

- [ ] **Step 5: Write the fixture and implement the TypeScript side**

Run: `dotnet run --project tests/Runtime --disable-build-servers -- --write-node-library-fixture`
Then `resolveTypeSpec`, `typeProperties`, the type lists, and `csharpType.ts` showing `members.<domain>.clrType`'s short name for `entity_id:<domain>` and `Entity` for `entity_id`.

- [ ] **Step 6: Run** `-- --select-property`, `-- --visual-types`, `-- --node-library`, no flag, and `bun run test` — expected: pass.

- [ ] **Step 7: Commit** — `List an entity's members for Select property`

---

### Task 7: Select property and text enums at run time

**Files:**
- Modify: `HassSharp/Visual/VisualRuntime.cs` (`SelectProperty`, `FromJson`, `Coerce`), `HassSharp/Visual/TraceJson.cs`
- Test: `tests/Runtime/SelectPropertyTests.cs`

**Interfaces:**
- Consumes: the runtime's factory, `EntityText`, `VisualTypes.Refines`.
- Produces: `SelectProperty` turns a string value into a handle when the wire's declared type refines `entity_id`; `FromJson` reads a string literal of an `EntityText`-marked enum through `EntityText.Parse` and throws `InvalidOperationException($"{label}: {text} is not a value of {type}")` when there is none; `Coerce` with `convert` does the same for a string into such an enum; `TraceJson` writes such an enum as its text, and `Other` as `"other"`.

- [ ] **Step 1: Write the failing tests**

`SelectPropertyTests.Entities`, graph: entity trigger on `binary_sensor.hall` → Select property `State` → Compare `eq` with right literal `{kind: "literal", type: "global::HassSharp.Entities.BinarySensorState", value: "on"}` → If → Log on each branch.

```csharp
PyInterop.Entity = id => new() { EntityId = id, State = "on" };   Check(await Branch(graph) == "true", "state read and compared");
PyInterop.Entity = id => new() { EntityId = id, State = "off" };  Check(await Branch(graph) == "false", "other state");
Check(TraceOutput(graph, "select", "value") == "\"on\"", "trace shows the text");
```

- Select property `RawState` gives `"on"`; `Brightness` on a light with `brightness: 128` gives a JSON element holding 128, and null when absent.
- A nullable enum: Select property `ColorMode` on a light with `color_mode: "hs"`, compared `eq` with the literal `{type: "global::HassSharp.Entities.LightColorMode", value: "hs"}`, validates and takes the true branch; with the attribute absent it takes the false branch.
- Review Focus 3: the same graph with the literal `"jammed"` fails validation with a message naming `jammed` on the Compare step; saved through the manager beside a second, valid automation, the second still loads and runs.

- [ ] **Step 2: Run them and see them fail** — `-- --select-property`, expected: `state read and compared` fails.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run** `-- --select-property`, `-- --visual-trace`, then no flag — expected: pass.

- [ ] **Step 5: Commit** — `Read an entity's members in the graph`

---

### Task 8: Compare's dropdown

**Files:**
- Modify: `frontend/src/visual/widgets/model.ts`, `frontend/src/visual/flow/flowModel.ts` (where an unwired input gets its spec, so `NodeCard` and `InputEditor` both see it)
- Test: `frontend/tests/widgets.test.tsx`, `frontend/tests/canvas-typed.test.tsx`

**Interfaces:**
- Consumes: `resolveTypeSpec`, `widgetFor`, `literalFor`, the catalog's `resolvedOutputs`.
- Produces:

```ts
/** The input a Compare side shows when the other side is wired to an enum: the same input, taking only that enum. */
export function comparedEnumInput(input: InputSpec, otherType: string | undefined, catalog: VisualCatalogSnapshot): InputSpec
```

It returns `input` unchanged unless `otherType`, with a trailing `?` removed, resolves to a type with `literalKind: 'enum'`; then `{ ...input, types: [thatType] }`. `flowModel` applies it to `left` and `right` of a `compare` node, with `otherType` the type of the wire on the other side.

- [ ] **Step 1: Write the failing tests**

```ts
const state = 'global::HassSharp.Entities.BinarySensorState'
expect(comparedEnumInput(anyInput, state, catalog).types).toEqual([state])
expect(comparedEnumInput(anyInput, state + '?', catalog).types).toEqual([state])
expect(comparedEnumInput(anyInput, 'string', catalog)).toBe(anyInput)
expect(comparedEnumInput(anyInput, undefined, catalog)).toBe(anyInput)
expect(widgetFor(comparedEnumInput(anyInput, state, catalog), catalog)).toMatchObject({ kind: 'select',
  options: [{ value: 'off', label: 'Off' }, { value: 'on', label: 'On' }, { value: 'unknown', label: 'Unknown' }, { value: 'unavailable', label: 'Unavailable' }] })
```

`canvas-typed.test.tsx`: a Compare whose left is wired from a Select property `State` shows a select on the right; choosing On stores `{ kind: 'literal', type: state, value: 'on' }` and the graph has no diagnostics; removing the left wire brings back the mixed widget.

- [ ] **Step 2: Run them and see them fail** — `(cd custom_components/hass_sharp/frontend && bun run test)`, expected: `comparedEnumInput` is not exported.

- [ ] **Step 3: Implement**

- [ ] **Step 4: Run the frontend suite** — expected: pass.

- [ ] **Step 5: Commit** — `Pick a state from a list when comparing one`

---

### Task 9: The Python push

**Files:**
- Create: `entities.py`, `tests/python/test_entity_schema.py`
- Modify: `__init__.py` (`async_setup_entry`, both cleanup paths)

**Interfaces:**
- Consumes: `manager.SetEntityDescriptions(json, started)`.
- Produces, mirroring `services.py`:

```python
DEBOUNCE_SECONDS = 2
async def async_domains(hass) -> list[str]
async def async_translations(hass, domains) -> dict          # {} on failure, with a warning
def payload(domains, translations) -> str                    # {"domains": [...], "translations": {...}}
async def async_push(hass, manager, active=lambda: True)     # failures are logged, never raised
async def async_start(hass, manager)                         # listens, pushes once, returns unsubscribe
def async_track(hass, manager)
```

`async_domains`: `homeassistant.const.Platform` values, the domain of every state and of every entity registry entry, and every top-level name in `hass.config.components`; sorted. Translations are asked only for those that `loader.async_get_integrations` resolves, in `hass.config.language`, category `entity_component`. Re-push, debounced: `EVENT_COMPONENT_LOADED`, `EVENT_HOMEASSISTANT_STARTED`, a language change in `EVENT_CORE_CONFIG_UPDATE`, and an entity registry `create` whose domain was not in the last push. `__init__.py` starts it after `services.async_start` and before `Init`, and stops it wherever the service listener is stopped.

- [ ] **Step 1: Write the failing tests**, modelled on `tests/python/test_services.py`:

- `test_push_sends_domains_and_translations`: the manager receives JSON whose `domains` contains `lawn_mower` (a `Platform` with nothing loaded) and `sun` (from a state), and whose `translations` has `component.binary_sensor.entity_component._.state.on`.
- `test_unknown_domain_is_listed_but_not_translated`: a state `made_up.thing` puts `made_up` in `domains`, `async_get_translations` is not asked for it, and nothing is logged at WARNING.
- `test_started_flag`, `test_debounce` (three events, one push), `test_nothing_after_stop`, `test_failure_is_logged_not_raised`, `test_new_entity_domain_pushes_again`, `test_translation_failure_sends_domains_only`.

- [ ] **Step 2: Run them and see them fail** — `python3 -m unittest discover -s tests/python`, expected: `ModuleNotFoundError` for `entities`.

- [ ] **Step 3: Implement `entities.py` and wire it into `__init__.py`**

- [ ] **Step 4: Run the Python suite**, on the host and in the container as `AGENTS.md` describes — expected: pass, the container run skipping nothing.

- [ ] **Step 5: Commit** — `Send Home Assistant's entity states and attributes to .NET`

---

### Task 10: Editor, type links, documentation, live check

**Files:**
- Modify: `HassSharp/Visual/TypeDefinitions.cs`, `HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`, `HassSharp.Editor.Tests/DiagnosticsProviderTests.cs`, `llm.py`, `authoring_examples.json`, `docs/node-libraries.md`, `docs/writing-visual-automations.md`, `tests/python/test_mcp_adapter.py`
- Test: `frontend/tests/type-definition.test.tsx`, `frontend/tests/csharp-type.test.ts`

- [ ] **Step 1: Write the failing tests**

- Editor: completion after `Light` offers `LightEntity`; hover on `BinarySensorState.On` shows its doc; completion still offers no `Entities` tree. Rename `Completion_no_longer_offers_the_typed_entities` to `Completion_offers_no_per_entity_tree` and `The_typed_entities_are_gone` to `There_is_no_per_entity_tree`, keeping their assertions about `Entities.` and the old wrappers.
- Type link: `TypeDefinitions` finds `HassSharp.Entities.LightEntity` in the entities assembly and returns its generated source.
- Frontend: `csharpType('entity_id:light', catalog)` is `LightEntity`, `csharpType('entity_id', catalog)` is `Entity`.
- MCP: `get_node_schema` for a library node with a `BinarySensorEntity` pin returns `entity_id:binary_sensor`, and `parametricTypes.entity_id.members` is present.

- [ ] **Step 2: Run them and see them fail**

- [ ] **Step 3: Implement the `TypeDefinitions` change and update the tests' names**

- [ ] **Step 4: Update the documentation and guidance**

`docs/node-libraries.md`: a section "Entities" with the `Motion lights` example from the spec, the rules for `State`, attributes, `Other`, `RawState`, and that a `string` parameter still accepts any entity. `llm.py`: replace "there are no typed entity objects" with two sentences on `XEntity` pins and Select property on an entity wire. `authoring_examples.json`: one example with a `BinarySensorEntity` pin.

- [ ] **Step 5: Run every suite in `AGENTS.md`** — expected: pass.

- [ ] **Step 6: Live check in the local container**

```sh
dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out
(cd custom_components/hass_sharp/frontend && bun install && bun run build)
docker compose restart hass && docker compose logs --tail=200 hass
```

In the panel at <http://localhost:5001>, confirm each and note the result in the commit message:
1. A Custom C# node `public static bool Run(BinarySensorEntity motionSensor) => motionSensor.State == BinarySensorState.On;` shows a pin whose picker lists only binary sensors.
2. A State trigger on a binary sensor wires its Entity output to that pin; a trigger on a light is refused.
3. Select property on the trigger's Entity output lists State; Compare shows a dropdown with On and Off; the run takes the expected branch.
4. `await context.Services.Light.TurnOn(light);` with a `LightEntity light` pin turns the light on.
5. After `docker compose restart hass`, the log shows the automations loading before the first entity push, and `/config/.storage/hass_sharp/entity_schema.json` exists.

- [ ] **Step 7: Commit** — `Document entity handles and show them in the editor`

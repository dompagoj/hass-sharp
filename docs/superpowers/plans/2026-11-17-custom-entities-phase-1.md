# Custom Entities Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A C# class in a node library that derives from `SharpDevice` or `SharpEntity` can be saved as an
entity definition through the JSON API and appears in Home Assistant as real binary sensor, sensor and switch
entities with a device, live state and working commands.

**Architecture:** Definitions are a new document kind stored beside visual automations and hosted by the
existing automation host (`UserScriptManager`): a definition compiles to a factory plus the library sources,
loads into its own collectible context, and a `DefinitionHost` attached to its `UserScript` runs the
lifecycle. .NET describes a definition's entities to Python as JSON; a Python `EntityBridge` creates Home
Assistant entities for them, applies batched state pushes, and forwards commands back.

**Tech Stack:** C# / .NET 11 (Roslyn, existing), Python 3 inside Home Assistant 2026.11 (Python.NET 3.1.0),
`unittest`, the custom runtime test runner in `tests/Runtime`, xUnit v3 for editor tests.

**Spec:** `docs/superpowers/specs/2026-11-17-custom-entities-design.md`

**Spike findings:** `docs/superpowers/plans/2026-11-17-custom-entities-phase-1-spike.md` (Home Assistant entity
lifecycle and Python.NET bytes, verified in a throwaway container). Tasks 9 and 10 follow it.

## Global Constraints

- Every implementer and reviewer subagent runs on Opus 5.5 (`model: "opus"`). The main session writes no product code.
- Branch `custom-entities`, in place. Commit after each task, imperative subject like the repo's
  (`Add entity state pump`), ending with the trailer `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`.
- No new NuGet or Python packages in this phase.
- The C# project treats nullable warnings as errors and writes a docs XML file: public members get doc comments.
- No change to `Visual/VisualGenerator.cs`, `Visual/VisualRuntime.cs` or `visual_node_catalog.json`: an
  existing automation's generated source stays byte-identical.
- Only strings cross the Python/.NET boundary in this phase. No user-assembly object is handed to Python.
- Unique id `<definitionId>:<entityKey>`. Entity keys match `^[a-z][a-z0-9_]{0,39}$`. Definition ids match the
  visual id rule `^[a-z0-9][a-z0-9_-]{0,79}\z`. Device identifier `("hass_sharp", definitionId)`.
- Entity types in this phase: `binary_sensor`, `sensor`, `switch`. Logic kind in this phase: `class` only.
- A definition that is off, fails to compile or fails setup keeps its entities as unavailable. Registry
  entries are removed only by delete, by removing an entity from the definition, or by the startup prune of
  definitions whose file no longer exists. Unload and reload never purge.
- New names: `DefinitionGenerator` (not `EntityGenerator`), `entity_bridge.py` and `hass.data["hass_sharp_entity_bridge"]`
  (not `entities.py` / `hass_sharp_entities`).
- Never use `mcp__hass__*` tools: they reach the production instance. Live checks use the local `hass`
  container (port 5001), which mounts this checkout.
- Suites that must pass at the end of every task that touches their area, from the repository root:
  `python3 -m unittest discover -s tests/python`, `python3 -m unittest discover -s tests`,
  `dotnet run --project tests/Runtime --disable-build-servers`,
  `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`.
  Baseline on this branch: Python 87 tests (20 skipped without Home Assistant), packaging 5, runtime 213 `PASS` lines, editor 21.

## Review Focus

1. **A setting value of the wrong JSON type** (`3.5` or `1e40` for an `int`, a number for a `string`, a text that
   is not one of an enum's names): refused at save with a diagnostic naming the setting, never an activation crash. (Task 4)
2. **User code that throws in an `Every` or `Track` callback:** logged with the definition's name; the timer keeps
   ticking and the definition stays running. (Task 5)
3. **Two entities with the same key, or a key that breaks the pattern,** in a class: activation fails naming the
   key, and nothing is synced to Home Assistant. (Task 5)
4. **A definition file that cannot be read at startup:** skipped and logged; its entities are not pruned, and the
   other definitions load. (Task 7)
5. **A sensor value JSON cannot carry** (`double.NaN`, infinity): published as no value, never an exception in
   the property setter. (Task 1)

## File Structure

Paths under `custom_components/hass_sharp/` unless they start with `tests/` or `docs/`.

| File | Responsibility |
|---|---|
| `HassSharp/Devices/SharpComponent.cs` | Lifecycle and helper base; the `IComponentHost` seam |
| `HassSharp/Devices/SharpEntity.cs` | Entity base: key, metadata, availability, attributes, describe/snapshot JSON, command dispatch |
| `HassSharp/Devices/SharpEntities.cs` | `SharpBinarySensor`, `SharpSensor`, `SharpSwitch`; `SharpDomains` |
| `HassSharp/Devices/SharpDevice.cs` | Device base, `SettingAttribute`, `EntityCategory` |
| `HassSharp/Devices/EntityStatePump.cs` | Debounced, deduplicated batched state pushes |
| `HassSharp/Devices/EntityDefinition.cs` | Document records and `EntityDefinitionStore` |
| `HassSharp/Devices/DefinitionGenerator.cs` | Validates a definition body and generates its source |
| `HassSharp/Devices/DefinitionHost.cs` | Runs one definition: build, settings, setup, sync, start, stop; `EntityDirectory` |
| `HassSharp/Devices/EntityApi.cs` | `HassSharpManager` partial: `EntityRequest`, `EntityCommand`, activation |
| `entity_bridge.py` | `EntityBridge`: platform callbacks, live entities, sync, registry cleanup, push, command |
| `entity_base.py` | `HassSharpEntity`: the shared Home Assistant entity base |
| `binary_sensor.py`, `sensor.py`, `switch.py` | One thin platform each |

---

### Task 1: Entity base classes

**Files:**
- Create: `HassSharp/Devices/SharpComponent.cs`, `SharpEntity.cs`, `SharpEntities.cs`, `SharpDevice.cs`
- Test: `tests/Runtime/SharpEntityTests.cs` (suite flag `--sharp-entities`, `Order = 12`)

**Interfaces:**
- Consumes: `EntityStateSnapshot`, `VisualRuntime.ReadEntity(string, bool fresh)` (`Visual/VisualRuntime.cs:539`),
  `NodeContext.CallService` (`Visual/NodeContext.cs:57`) for the service-call path, `PyLogLevel`.
- Produces (namespace `HassSharp`):

```csharp
internal interface IComponentHost {
    CancellationToken Stopping { get; }
    void Publish(SharpEntity entity);
    IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately);
    IDisposable Track(string entityId, Func<EntityChange, Task> action);
    void Log(string message, PyLogLevel level);
}
public sealed record EntityChange(string EntityId, EntityStateSnapshot To, EntityStateSnapshot? From);
public enum EntityCategory { Config, Diagnostic }
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string? label = null) : Attribute {
    public string? Label { get; } = label; public string? Description { get; set; } public bool Secret { get; set; } }

public abstract class SharpComponent {
    internal IComponentHost? Host { get; set; }
    protected internal virtual Task Setup();   // default Task.CompletedTask, as Start and Stop
    protected internal virtual Task Start();
    protected internal virtual Task Stop();
    protected CancellationToken Stopping { get; }                       // CancellationToken.None before attach
    protected IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately = true);
    protected IDisposable Track(string entityId, Func<EntityChange, Task> action);
    protected EntityStateSnapshot State(string entityId);               // fresh read
    protected Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false);
    protected void Log(string message, PyLogLevel level = PyLogLevel.Info);
}
public abstract class SharpEntity : SharpComponent {
    protected SharpEntity();                                            // key "main"
    protected SharpEntity(string key, string? name = null);
    public string Key { get; }
    public string? Name { get; init; }  public string? Icon { get; init; }  public string? DeviceClass { get; init; }
    public EntityCategory? Category { get; init; }  public bool EnabledByDefault { get; init; } = true;
    public bool Available { get; set; }                                 // default true; setting publishes
    public IDictionary<string, object?> Attributes { get; }             // changes need Publish()
    public void Publish();                                              // no-op before attach
    internal abstract string Domain { get; }
    internal virtual IReadOnlyList<string> Commands { get; }            // default empty
    internal virtual Task Command(string name, JsonElement data);       // default: NotSupportedException
    internal virtual JsonObject Values();                               // domain state fields
    internal virtual JsonObject Options();                              // domain description fields
    internal JsonObject Describe(string uniqueId);
    internal JsonObject Snapshot(string uniqueId, bool running);
}
public abstract class SharpDevice : SharpComponent {
    public string? Manufacturer { get; set; }  public string? Model { get; set; }  public string? SwVersion { get; set; }
    public string? HwVersion { get; set; }  public string? ConfigurationUrl { get; set; }  public string? SuggestedArea { get; set; }
    protected T Add<T>(T entity) where T : SharpEntity;                 // throws ArgumentException on a duplicate key
    public IReadOnlyList<SharpEntity> Entities { get; }
}
public class SharpBinarySensor : SharpEntity { public bool? IsOn { get; set; } }             // Domain "binary_sensor"
public class SharpSensor : SharpEntity {                                                     // Domain "sensor"
    public object? Value { get; set; }  public string? Unit { get; init; }  public string? StateClass { get; init; }
    public IReadOnlyList<string>? Options { get; init; } }
public class SharpSwitch : SharpEntity {                                                     // Domain "switch"
    public bool? IsOn { get; set; }
    public Func<Task>? OnTurnOn { get; init; }  public Func<Task>? OnTurnOff { get; init; }
    protected virtual Task TurnOn();  protected virtual Task TurnOff(); }                    // default: the delegate, else NotSupportedException
internal static class SharpDomains { public static string? Of(Type type); }                  // nearest typed base → domain
```

  Each typed class has both constructors (`()` and `(string key, string? name = null)`). JSON shapes, exact:
  - `Describe` → `{"uniqueId","key","domain","name","icon","deviceClass","entityCategory","enabledByDefault","commands":[...],"options":{...}}`
    with `entityCategory` `"config"`, `"diagnostic"` or null.
  - `Snapshot` → `{"uniqueId","available": Available && running,"values":{...},"attributes":{...}}`.
  - `values`: binary sensor and switch `{"is_on": true|false|null}`; sensor `{"value": <string|number|bool|null>}`.
  - `options`: sensor `{"unit","stateClass","options"}`; the others `{}`.
  - `SharpSwitch.Commands` is `["turn_on","turn_off"]`; `Command` routes those two names and throws
    `NotSupportedException` for any other.
  - Sensor `Value`: numbers stay numbers, `DateTime`/`DateTimeOffset` become ISO 8601 text, NaN and infinities
    become null, any other object its `ToString()`.

- [ ] **Step 1: Write the failing suite** `SharpEntityTests.Run` with a recording `IComponentHost` fake. Checks, each a `PASS` line:
  - setting `IsOn` on an attached switch publishes once, and the snapshot is
    `{"uniqueId":"plug:relay","available":true,"values":{"is_on":true},"attributes":{}}`;
  - no publish and no exception before a host is attached;
  - `Snapshot(id, running: false)` has `"available":false` while `Available` is true;
  - `Describe` of `new SharpSensor("power", "Power") { Unit = "W", DeviceClass = "power", StateClass = "measurement" }`
    equals the exact JSON above with `"commands":[]`;
  - `Command("turn_on", default)` calls an overridden `TurnOn`, and calls `OnTurnOn` on a composed switch;
    `Command("toggle", default)` throws `NotSupportedException`; a switch with neither throws on `turn_on`;
  - `new SharpSensor().Key == "main"`; a key `"Relay"` or `"1x"` throws `ArgumentException` from the constructor;
  - `Add` of a second entity with the same key throws `ArgumentException` naming the key;
  - `Value = double.NaN` and `double.PositiveInfinity` publish `{"value":null}` without throwing;
    `Value = 41.2` publishes `41.2`; `Value = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)` publishes `"2026-11-17T00:00:00.0000000Z"`;
  - `SharpDomains.Of(typeof(Derived))` is `"switch"` for a class deriving `SharpSwitch`, null for `SharpDevice`.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --sharp-entities`. Expected: does not compile (types missing).
- [ ] **Step 3: Implement** the four files to the signatures above.
- [ ] **Step 4: Run** the same command. Expected: every check prints `PASS`, exit 0. Then the full runtime suite: exit 0.
- [ ] **Step 5: Commit** `Add SharpEntity and SharpDevice base classes`.

### Task 2: Bridge delegates and the state pump

**Files:**
- Modify: `HassSharp/PyInterop.cs:174-200`, `tests/Runtime/Program.cs:20-24`
- Create: `HassSharp/Devices/EntityStatePump.cs`
- Test: `tests/Runtime/EntityPumpTests.cs` (`--entity-pump`, `Order = 12`)

**Interfaces:**
- Produces, on `PyInterop` (same style as `BeginRegisterHaTriggers` / `RegisterHaTriggersAsync`):

```csharp
public static Action<string, Action<string>, Action<string>> BeginSyncEntities { get; set; } = null!;  // definitionJson, success(resultJson), error
public static Action<string, Action, Action<string>> BeginRemoveEntities { get; set; } = null!;        // definitionId: purge registry and device
public static Action<string, Action, Action<string>> BeginPruneEntities { get; set; } = null!;         // JSON array of known definition ids
public static Action<string> PushEntityStates { get; set; } = null!;                                   // JSON array of snapshots
public static Task<string> SyncEntitiesAsync(string definitionJson);
public static Task RemoveEntitiesAsync(string definitionId);
public static Task PruneEntitiesAsync(string definitionIdsJson);
```

```csharp
internal sealed class EntityStatePump(TimeSpan? debounce = null) {   // default 25 ms
    public void Post(string uniqueId, JsonObject snapshot);          // latest wins per id
    public void Forget(IEnumerable<string> uniqueIds);               // the next Post for these is sent even if unchanged
    public Task FlushAsync();                                        // sends what is pending now
    public void Reset();                                             // drops pending and remembered values
}
```

  A flush sends one `PushEntityStates` call with a JSON array, in first-posted order, leaving out any snapshot
  whose JSON text equals the last one sent for that id. A failing `PushEntityStates` is logged with
  `Logger.Error` and never thrown; the values it carried are not remembered as sent.

- [ ] **Step 1: Write the failing suite.** Checks: three posts for one id inside the debounce send one batch with
  the last; an unchanged repost sends nothing; after `Forget` the same snapshot is sent again; two ids arrive in
  one array in post order; `Reset` drops a pending post; a throwing `PushEntityStates` does not throw from
  `FlushAsync` and the next identical post is sent; `SyncEntitiesAsync` completes with the text given to
  `success` and faults with `InvalidOperationException` carrying the text given to `error`.
- [ ] **Step 2: Run** `-- --entity-pump`. Expected: does not compile.
- [ ] **Step 3: Implement.** In `Program.cs`, beside the existing defaults, set defaults every suite inherits:
  `BeginSyncEntities = (_, ok, _) => ok("""{"entities":{}}""")`, `BeginRemoveEntities = (_, ok, _) => ok()`,
  `BeginPruneEntities = (_, ok, _) => ok()`, `PushEntityStates = _ => { }`.
- [ ] **Step 4: Run** the suite, then the full runtime suite. Expected: `PASS`, exit 0.
- [ ] **Step 5: Commit** `Add entity bridge delegates and state pump`.

### Task 3: Entity types in the library snapshot

**Files:**
- Modify: `HassSharp/Visual/NodeLibraryCompiler.cs` (the `Preview` at `:94`), `HassSharp/Visual/VisualCatalogSnapshot.cs:46-75`
- Test: `tests/Runtime/EntityTypeTests.cs` (`--entity-types`, `Order = 12`);
  `HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs` (one new fact)

**Interfaces:**
- Consumes: Task 1 types; `NodeLibraryCompiler.TypeIdentity(symbol)`.
- Produces:

```csharp
public sealed record EntitySettingDescriptor(string Key, string Label, string? Description, string Type, bool Secret, ImmutableArray<string> Options);
public sealed record EntityTypeDescriptor(string Id, string SourceId, string TypeExpression, string Kind, string? Domain,
    string Title, string? Description, ImmutableArray<EntitySettingDescriptor> Settings);
// on VisualCatalogSnapshot:
public ImmutableDictionary<string, EntityTypeDescriptor> EntityTypes { get; }   // by Id; empty for BuiltIn
```

  - A type is listed when it is public, non-abstract, not nested, declared in a library source, derives
    `SharpDevice` or `SharpEntity`, and has a public parameterless constructor.
  - `Id` is `library:<sourceId>:<namespace-qualified name>`, e.g. `library:shelly:NodeLib.ShellyPlug`.
    `TypeExpression` is `TypeIdentity(symbol)`. `Kind` is `"device"` or `"entity"`. `Domain` is the nearest typed
    base's domain for an entity, null for a device. `Title` is the class name; `Description` its XML doc summary.
  - A setting is a public instance property marked `[Setting]` with a public setter. `Key` is the property name
    as written. `Label` is the attribute's label, else the property name. `Type` is `"string"`, `"boolean"`,
    `"integer"` (`int`, `long`), `"number"` (`double`) or `"enum"` (then `Options` holds the member names).
    Nullable forms of those are accepted.
  - Library diagnostics (severity 3, at the property): `[Setting]` on any other type —
    `A setting is a string, bool, int, long, double or enum; <Type> cannot be one`; `[Setting]` without a public
    setter — `A setting needs a public setter`. Entity-handle settings are deferred to phase 4.
  - `EntityTypes` is not part of `Catalog`/`Root`: automations must not embed it.

- [ ] **Step 1: Write the failing suite.** Library source `shelly` declares `public class ShellyPlug : SharpDevice`
  with `[Setting("Host")] public string Host`, `[Setting(Secret = true)] public string? Token`,
  `[Setting] public int Channel`, `[Setting] public Mode Mode` (`enum Mode { Eco, Boost }`), plus
  `public class Relay : SharpSwitch`, an abstract `Base : SharpDevice`, and `NoCtor : SharpDevice` with only a
  `(string)` constructor. Checks: ids are exactly `library:shelly:NodeLib.ShellyPlug` and
  `library:shelly:NodeLib.Relay`; `ShellyPlug` is `device` with four settings of types `string`, `string`,
  `integer`, `enum` and options `["Eco","Boost"]`; `Token.Secret`; `Relay` is `entity` with domain `switch`;
  `Base` and `NoCtor` are absent; `[Setting] public DateTime When { get; set; }` gives the first message;
  `[Setting] public string X { get; }` gives the second; `snapshot.Catalog.ToJsonString()` does not contain `ShellyPlug`.
- [ ] **Step 2: Run** `-- --entity-types`. Expected: does not compile.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Editor fact** `CompletionOffersSharpDeviceInLibraryCode`: completion at `class X : Sharp|` in a library
  source lists `SharpDevice`, `SharpSwitch` and `SharpSensor`. Run `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`. Expected: 22 passed.
- [ ] **Step 5: Run** the suite and the full runtime suite. Expected: exit 0.
- [ ] **Step 6: Commit** `Discover SharpEntity and SharpDevice classes in node libraries`.

### Task 4: Definition document, store and generator

**Files:**
- Create: `HassSharp/Devices/EntityDefinition.cs`, `HassSharp/Devices/DefinitionGenerator.cs`
- Test: `tests/Runtime/EntityStoreTests.cs` (`--entity-store`, `Order = 12`)

**Interfaces:**
- Consumes: `EntityTypeDescriptor` (Task 3), `VisualDiagnostic`, `VisualSourceMap`, `VisualConflictException`,
  `VisualStore.Json`, `NodeLibraryCompiler.WrapSource`. Model the store on `Visual/VisualStore.cs:31-151`
  (gate per directory, temp file then move, revision conflict, recovery delete at revision 0).
- Produces:

```csharp
public sealed record DefinitionVia(string? Definition, string? DeviceId);
public sealed record DefinitionDevice(string? Manufacturer, string? Model, string? SwVersion, string? HwVersion,
    string? ConfigurationUrl, string? SuggestedArea, DefinitionVia? Via);
public sealed record DefinitionSetting(string Key, JsonNode? Value);
public sealed record DefinitionLogic(string Kind, string? Type, JsonObject? Graph);
public sealed record DefinitionBody(string Name, DefinitionDevice? Device, List<DefinitionSetting>? Settings,
    JsonArray? Entities, DefinitionLogic Logic);
public sealed record EntityDefinitionDocument(int Version, string Id, long Revision, string Name, DefinitionDevice? Device,
    List<DefinitionSetting> Settings, JsonArray? Entities, DefinitionLogic Logic, string Source, List<VisualSourceMap> SourceMap) {
    public bool Enabled { get; init; } = true;  public DateTime? SavedUtc { get; init; }  public string? CatalogGeneration { get; init; }
    public DefinitionBody Body { get; }          // [JsonIgnore]
}
public sealed class DefinitionValidationException(DefinitionPreview preview) : Exception { public DefinitionPreview Preview { get; } }
public sealed record DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source, EntityTypeDescriptor? Type);
public sealed class DefinitionGenerator {
    public DefinitionPreview Preview(DefinitionBody body, VisualCatalogSnapshot snapshot);
}
public sealed class EntityDefinitionStore(string directory, DefinitionGenerator generator) {
    public EntityDefinitionDocument? Read(string id);
    public EntityDefinitionDocument[] List(bool strict = false);
    public string[] Ids();                        // every *.json file name without extension, readable or not
    public EntityDefinitionDocument Save(string id, DefinitionBody body, long expectedRevision, VisualCatalogSnapshot catalog,
        string? catalogGeneration, bool? enabled = null);                    // throws DefinitionValidationException
    public EntityDefinitionDocument Rebuilt(EntityDefinitionDocument old, DefinitionPreview preview, string? catalogGeneration); // revision + 1, not written
    public EntityDefinitionDocument SetEnabled(string id, bool enabled);
    public void Delete(string id, long expectedRevision);
    internal string FilePath(string id);
}
```

  Generated source for a class definition, exact (library sections as `VisualGenerator.cs:405` writes them):

```
// HassSharp entity definition
public static class SharpDefinition { public static object Create() => new <TypeExpression>(); }
// NodeLibrarySource "<id>" revision <n>
<NodeLibraryCompiler.WrapSource(source)>        (one pair per library source, in snapshot order)
```

  Diagnostics (severity 3, `NodeId` null, `InputId` the setting key where one applies):

| Code | When | Message |
|---|---|---|
| `definition.name` | name empty or over 120 characters | `Give the definition a name of up to 120 characters` |
| `definition.logic` | `Kind` is neither `class` nor `graph` | `Logic is a graph or a class` |
| `definition.graphUnsupported` | `Kind == "graph"` | `Graph definitions are not available yet` |
| `definition.entitiesNotAllowed` | `Entities` given with class logic | `A class definition's entities come from its code` |
| `definition.library` | the snapshot's libraries have errors | `The node libraries have errors; fix them first` |
| `definition.unknownType` | `Type` not in `snapshot.EntityTypes` | `No entity class <Type> in the node libraries` |
| `definition.unknownSetting` | key not a setting of the type | `<Type title> has no setting <Key>` |
| `definition.settingType` | value does not fit the setting | `<Label> takes <a text / true or false / a whole number / a number / one of A, B>` |
| `definition.duplicateSetting` | key given twice | `<Key> is set twice` |

  A JSON null value means "leave the class default" and is always accepted. `integer` accepts only JSON numbers
  with no fraction inside the `long` range.

- [ ] **Step 1: Write the failing suite** over a temp directory and a snapshot compiled from the Task 3 library.
  Checks: save at revision 0 writes revision 1 with the exact source above; a second save with the old revision
  throws `VisualConflictException`; `enabled` on an existing document throws `ArgumentException`; `SetEnabled`
  keeps revision and `SavedUtc`; `Delete` with a wrong revision conflicts; `Ids()` includes a file holding `{`
  while `List()` skips it and `List(strict: true)` throws; each row of the table above produces its code, with
  `3.5`, `1e40` and `"x"` for `Channel` and `"Turbo"` for `Mode` all giving `definition.settingType` at
  `InputId == "Channel"` / `"Mode"`; a null value passes; an id `Bad Id` throws `ArgumentException`;
  `Rebuilt` returns revision + 1 with the old `Enabled` and `SavedUtc`.
- [ ] **Step 2: Run** `-- --entity-store`. Expected: does not compile.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the suite and the full runtime suite. Expected: exit 0.
- [ ] **Step 5: Commit** `Add entity definition store and generator`.

### Task 5: Definition host

**Files:**
- Create: `HassSharp/Devices/DefinitionHost.cs`
- Modify: `HassSharp/UserScriptManager/CompiledUserScript.cs`, `UserScript.cs:90-112,135-156`,
  `UserScriptManager.cs:147-178,225-236,325-350`, `HassSharp/DependencyTracking.cs`
- Test: `tests/Runtime/EntityHostTests.cs` (`--entity-host`, `Order = 13`)

**Interfaces:**
- Consumes: Tasks 1, 2 and 4.
- Produces:

```csharp
public sealed record CompiledDefinition(string Id, string Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings) {
    internal Func<object>? Factory { get; init; }   // test seam; otherwise SharpDefinition.Create() of the compiled assembly
}
// CompiledUserScript:
public CompiledDefinition? Definition { get; set; }
public sealed record LiveEntity(string UniqueId, string Key, string Domain, string? Name, string? EntityId);
internal sealed class EntityDirectory {
    public bool TryGet(string uniqueId, out DefinitionHost host, out SharpEntity entity);
    public DefinitionHost? Host(string definitionId);
}
internal sealed class DefinitionHost : IComponentHost {
    public string DefinitionId { get; }
    public IReadOnlyList<LiveEntity> Entities { get; }       // filled by PrepareAsync
    public string Status { get; }                             // "setup", "running", "failed"
    public string? Error { get; }
    public Task PrepareAsync(CancellationToken lifetime);
    public void Start();                                      // never blocks, never throws
    public Task StopAsync();
    public Task Command(SharpEntity entity, string name, JsonElement data);
}
// UserScript:
internal DefinitionHost? Host { get; }
// UserScriptManager:
public EntityDirectory Entities { get; } = new();
public EntityStatePump Pump { get; init; } = new();
public Task UnloadScriptAsync(string scriptPath);            // DeleteScriptAsync without History.Forget
// DependencyTracking:
public IDisposable TrackDelegate(string entityId, UserScript owner, Func<TriggerContext, Task> action);
```

  Behaviour:
  - `LoadUserScript` creates the host when `compiledScript.Definition != null`. A definition script has
    `DesignatedRootType = "SharpDefinition"`, so `Classes` is empty.
  - `PrepareAsync` (called at the end of `UserScript.PrepareCoreAsync`): create the root; it must be a
    `SharpDevice` or `SharpEntity` (else `InvalidOperationException`). Apply settings by reflection to
    `[Setting]` properties, skipping null values. Collect entities: a device's `Entities`, or the root entity
    alone. Await `Setup()` on the device, then on each entity, then collect again (entities may be added in
    `Setup`). Validate keys (pattern, unique) — a failure throws `InvalidOperationException` naming the key.
    Last, `await PyInterop.SyncEntitiesAsync(json)` and record each `entityId` from the result.
  - Sync JSON, exact: `{"definitionId","name","device": {...}|null,"entities":[Describe(...)...]}`.
    `device` for a `SharpDevice` root is `{"name": definition name,"manufacturer","model","swVersion","hwVersion","configurationUrl","suggestedArea","via"}`
    from the class's properties, each overridden by a non-null field of the document's device. For a
    `SharpEntity` root it is the document's device with `"name"` the definition name, or null when the
    document has none. `via` is `{"definition": id}` or `{"deviceId": id}` or null.
  - `Start` (called at the end of `UserScript.Start`, inside the activation gate): register in the directory,
    replacing a previous host of the same definition; then, off the gate, `pump.Forget` this host's ids, run
    `Start()` on the device then each entity, set `Status` to `running` and publish every entity. If a `Start()`
    throws: `Status = "failed"`, `Error` the message, logged, and every entity published unavailable.
  - `StopAsync` (called from `UserScript.ClearCoreAsync`, after `DependencyTracking.RemoveScript`): cancel
    `Stopping`, dispose timers and tracks, await `Stop()` on entities then device for at most
    `UserScript.RunDrainTimeout`, and leave the directory only if still the registered host. It pushes nothing.
  - `IComponentHost.Publish` posts `entity.Snapshot(uniqueId, running: Status == "running")` to the pump; before
    `Start` it does nothing.
  - `Every`: a `PeriodicTimer` loop bound to `Stopping`; no overlapping calls; an exception from the action is
    logged as `<definition name>: a timer callback failed: <message>` and the loop continues.
  - `Track`: `DependencyTracking.TrackDelegate`; `UserScriptManager.RunEntries` also runs delegate entries whose
    owner is active, enabled and not cancelled; an exception is logged the same way with `an entity callback`.
    `DependencyTracking.RemoveScript` removes the owner's delegate entries and unsubscribes entities left unused.
  - `Command` awaits `entity.Command(name, data)`; when `Status != "running"` it throws
    `InvalidOperationException("The entity is not running")`.

- [ ] **Step 1: Write the failing suite** using `CompiledDefinition.Factory` with classes in the test assembly, a
  `UserScriptManager`, and recording fakes for `BeginSyncEntities` / `PushEntityStates`. Checks:
  - order for a device with two entities is `settings, device.Setup, entity.Setup ×2, sync, device.Start, entity.Start ×2`;
  - the sync JSON for a plug class equals the exact expected text (device block and two described entities);
  - a `SharpSwitch`-derived root with no document device syncs `"device":null` and unique id `<id>:main`;
  - document device `Model` overrides the class's `Model`; a null document field does not;
  - after start, a batch arrives with every entity `"available":true`; `Start()` throwing gives `Status == "failed"`,
    `Error` set and every entity `"available":false`;
  - `Setup()` throwing fails `PrepareAsync`, and `BeginSyncEntities` was never called;
  - duplicate keys and a key added in `Setup` that breaks the pattern fail naming the key, with no sync;
  - `UpdateUserScript` with a second compiled definition: the old instance's `Stop` runs before the new
    `Start`, no unavailable snapshot is pushed in between, and the directory returns the new instance;
  - a command through the directory reaches the instance and its state change is pushed; after `StopAsync`,
    `Command` throws `The entity is not running`;
  - an `Every` callback that throws is called again on the next tick and the error is logged with the definition name;
  - `Track("sensor.a", …)` receives a change fired through `RunEntries`, a throwing handler is logged, and after
    unload `RuntimeCoreTests.Subscriptions` no longer has `sensor.a`;
  - `UnloadScriptAsync` stops the host and leaves `History` untouched (a `RunHistoryStore` over a temp file still
    answers `Identity(path)` with the same id).
- [ ] **Step 2: Run** `-- --entity-host`. Expected: does not compile.
- [ ] **Step 3: Implement.** `DeleteScriptAsync` and `UnloadScriptAsync` share one core that takes whether to forget history.
- [ ] **Step 4: Run** the suite, then the full runtime suite (the lifecycle, handover and runtime-core suites
  exercise the touched code). Expected: exit 0.
- [ ] **Step 5: Commit** `Host entity definitions in the script manager`.

### Task 6: Entity API

**Files:**
- Create: `HassSharp/Devices/EntityApi.cs` (`public partial class HassSharpManager`)
- Modify: `HassSharp/Manager.cs:37-47` (construct the manager with the pump), `:432-433` (unload resets the pump)
- Test: `tests/Runtime/EntityApiTests.cs` (`--entity-api`, `Order = 14`)

**Interfaces:**
- Consumes: Tasks 3–5; `_visualGate`, `SavedLibraries`, `RecordLoadError`, `_loadErrors`, `ApiRequest`, `Error`,
  `InternalError`, `_compiler.CompileSingleFile` — follow `Visual/VisualApi.cs:13-67,83-163`.
- Produces:

```csharp
public Task<string> EntityRequest(string requestJson);                         // under _visualGate
public Task EntityCommand(string uniqueId, string command, string? dataJson);  // never takes _visualGate
EntityDefinitionStore EntityDocuments { get; }                                  // .storage/hass_sharp/entities
string EntityRuntimePath(string id);                                            // .../entities/entity_<id>.cs
Task<object> ActivateDefinition(EntityDefinitionDocument document, GeneratedServiceCalls? services = null);
```

  Envelope and error codes as `VisualRequest` (`storage`, `validation`, `conflict`, `not_found`,
  `invalid_request`, `internal`); a `DefinitionValidationException` answers `validation` with the preview.
  Operations:

| Operation | Request | Result |
|---|---|---|
| `types` | — | `{types: [EntityTypeDescriptor…], generation}` |
| `list` | — | array of documents with runtime fields, without `source` and `sourceMap` |
| `read` | `id` | the document with runtime fields |
| `preview` | `definition` | `{valid, diagnostics, source}` |
| `save` | `id`, `expectedRevision`, `definition`, `enabled?` | `{id, saved: true, document, active, activationError}` |
| `set_enabled` | `id`, `enabled` | `{id, enabled}` |
| `delete` | `id`, `expectedRevision` | `{deleted: true}` |

  - `definition` is a `DefinitionBody` (`{name, device, settings, entities, logic}`).
  - Runtime fields on a returned document: `automationId`, `active`, `currentRevisionActive`, `activationError`,
    `status` (`"running"`, `"setup"`, `"failed"`, or `"off"` when not loaded), and `liveEntities`
    (`[{uniqueId, key, domain, name, entityId}]`, empty when not loaded).
  - `save` stores, then activates when the document is enabled (compile with `DesignatedRootType = "SharpDefinition"`,
    `Definition` set, `VisualRevision` the document revision, `CatalogGeneration`; load or `UpdateUserScript`
    as `ActivateVisualDocument` does). A failed activation records the load error and returns it as
    `activationError`; the save still succeeded. If no revision of the definition is running after a failed
    activation, its known entities are pushed unavailable.
  - `set_enabled false`: store, `UnloadScriptAsync`, then one `PushEntityStates` batch marking the entities it
    had unavailable (`{"uniqueId","available":false,"values":{},"attributes":{}}`). `true`: store, then activate.
  - `delete`: store delete (revision check first), `UnloadScriptAsync`, clear the load error,
    `await PyInterop.RemoveEntitiesAsync(id)`. A failing removal is logged and the delete still answers `deleted`.
  - `EntityCommand`: looks the entity up in `_userScriptManager.Entities`; unknown →
    `InvalidOperationException("The entity is not running")`. `dataJson` null or empty is `{}`.

- [ ] **Step 1: Write the failing suite** over a full `HassSharpManager` in a temp config directory (set up as
  `tests/Runtime/AutomationHomeTests.cs:123-150` does), with recording fakes for the four entity delegates.
  Save a library `shelly` whose `FakePlug : SharpDevice` has `[Setting] Host`, a `SharpSwitch("relay")` with
  `OnTurnOn`/`OnTurnOff` that set `IsOn` and append to a static log, and a `SharpSensor("power") { Unit = "W" }`.
  Checks:
  - `types` lists `library:shelly:NodeLib.FakePlug` with the `Host` setting;
  - `save` at revision 0 answers `active: true`, `activationError: null`, `status: "running"`, two `liveEntities`
    with unique ids `plug1:relay`, `plug1:power`; the recorded sync JSON has `definitionId: "plug1"` and a device
    named as the definition;
  - `EntityCommand("plug1:relay", "turn_on", null)` completes, the log shows the call with the saved `Host`, and
    a pushed batch has `plug1:relay` with `"is_on":true`;
  - `EntityCommand("plug1:nope", "turn_on", null)` and `EntityCommand("plug1:relay", "toggle", null)` fault;
  - a second definition `plug2` from the same class with another `Host` runs beside the first, each with its own host value;
  - saving `plug1` again with the stale revision answers `conflict`; with the right one, the sync is repeated
    and the command still works (new instance);
  - `preview` with an unknown setting answers `valid: false` with `definition.unknownSetting`; `save` of it
    answers `validation` and stores nothing;
  - a class whose `Setup()` throws: `save` answers `saved: true`, `active: false`, an `activationError` with the
    message, no sync recorded; `read` shows `status: "off"`;
  - `set_enabled false` unloads (`EntityCommand` faults) and pushes both entities unavailable; `set_enabled true`
    syncs and runs again;
  - `delete` with the right revision records `RemoveEntities("plug1")`, and `read` answers `not_found`;
  - `list` omits `source`; `read` includes it.
- [ ] **Step 2: Run** `-- --entity-api`. Expected: does not compile.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the suite and the full runtime suite. Expected: exit 0.
- [ ] **Step 5: Commit** `Add the entity definition API`.

### Task 7: Startup, retry, prune and library rebuild

**Files:**
- Modify: `HassSharp/Manager.cs:49-99` (`Init`), `:396-428` (`RetryFailedLoadsAsync`),
  `HassSharp/Visual/NodeLibraryApi.cs:250-278`, `HassSharp/Devices/EntityApi.cs`
- Test: `tests/Runtime/EntityStartupTests.cs` (`--entity-startup`, `Order = 15`)

**Interfaces:**
- Consumes: Tasks 4–6.
- Produces: no new public members. Behaviour:
  - `Init`: after the visual documents, each enabled definition is previewed against the saved libraries and
    compiled; a failure is recorded with `RecordLoadError(EntityRuntimePath(id), ex)` and logged as
    `Cannot load entity definition "<name>" (<id>): <reason>` in the style of `LoadFailure`; the others load.
    Definitions are loaded with the automations by `LoadUserScripts`. Last,
    `await PyInterop.PruneEntitiesAsync(json array of EntityDocuments.Ids())`; a failure there is logged, not thrown.
  - `RetryFailedLoadsAsync` also retries enabled definitions whose load failed and that are not loaded.
  - A library `save`/`delete` previews every definition against the candidate libraries, with the same
    "already invalid before" rule as graphs. A definition valid before and invalid after refuses the save: the
    `validation` detail gains `definitions: [{id, name, diagnostics}]`. Otherwise valid definitions are rebuilt
    (`EntityDefinitionStore.Rebuilt`), written in the same `AuthoringFiles.Commit` as the graphs and the library,
    and reactivated after the graphs. The result gains `definitions` (their activation results) and
    `invalidDefinitions` (`[{id, name, diagnostics, running, message}]`, the message as for graphs with
    "definition" wording).

- [ ] **Step 1: Write the failing suite.** Checks:
  - "restart": a second `HassSharpManager` over the same directory, after `Init`, has `plug1` running, synced
    again, and its command works;
  - the prune call's JSON array holds every stored id, including the id of a definition file overwritten with
    `{`; that broken file is logged and skipped, and the other definition still runs;
  - a disabled definition is not loaded at `Init`, and is still in the prune list;
  - a definition whose class no longer exists (library file deleted on disk) records a load error naming
    `definition.unknownType`'s message and does not stop the others or the automations;
  - `PruneEntitiesAsync` failing does not fail `Init`;
  - a library save that changes `FakePlug`'s behaviour answers `definitions` with one activation, the stored
    definition's revision is 2, and a command shows the new behaviour;
  - a library save that removes `FakePlug` answers `validation` with `definitions[0].id == "plug1"`, and nothing
    on disk changed (library and definition revisions as before);
  - a library save while `plug1` is already invalid (its library file was removed on disk before this manager
    started, so its type is unknown) succeeds and lists it in `invalidDefinitions`;
  - after a failed load cured by an entity-schema push (`SetEntityDescriptions`), `RetriesDone()` leaves the
    definition running.
- [ ] **Step 2: Run** `-- --entity-startup`. Expected: FAIL (definitions not loaded at `Init`).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the suite and the full runtime suite (`--node-library` covers the rebuild path). Expected: exit 0.
- [ ] **Step 5: Commit** `Load entity definitions at startup and rebuild them with libraries`.

### Task 8: Reload does not keep definition memory

**Files:**
- Modify: `tests/Runtime/ReloadMemoryTests.cs`
- (Fixes, if the test finds a leak, go in `HassSharp/Devices/DefinitionHost.cs`.)

**Interfaces:**
- Consumes: Tasks 5–7.

- [ ] **Step 1: Extend the suite's generation** with one class definition whose device uses `Every(TimeSpan.FromMilliseconds(20), …)`,
  `Track("sensor.memory", …)`, a `SharpSwitch` with `OnTurnOn`, and calls a typed service once from `Start()`.
  Each generation sends it one command. Keep the existing assertions: every generation's load contexts are
  collected and the quiet window stays under `BoundMiB`.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --reload-memory`.
  Expected: `PASS`. If a context is not collected, find what holds it (timer, tracked delegate, directory or
  pump entry, serializer cache) and fix it in the host.
- [ ] **Step 3: Run** the full runtime suite. Expected: exit 0.
- [ ] **Step 4: Commit** `Cover entity definitions in the reload memory test`.

### Task 9: Python entity bridge

**Files:**
- Create: `entity_bridge.py`, `entity_base.py`
- Modify: `const.py`
- Test: `tests/python/test_entity_bridge.py`

**Interfaces:**
- Consumes: the JSON shapes of Tasks 1, 2 and 5; the spike findings.
- Produces:

```python
# const.py
PLATFORMS = ["binary_sensor", "sensor", "switch"]

# entity_base.py
class HassSharpEntity(Entity):            # _attr_should_poll = False, _attr_has_entity_name = True
    def __init__(self, bridge, description: dict, device: dict | None, definition_name: str): ...
    def apply(self, description: dict, device: dict | None, definition_name: str) -> None   # metadata; loop only
    def apply_state(self, snapshot: dict) -> None                                           # available, values, attributes
    def apply_values(self, values: dict) -> None                                            # per platform
    async def command(self, name: str, **data) -> None                                      # raises HomeAssistantError

# entity_bridge.py
class EntityBridge:
    def __init__(self, hass, entry, manager): ...
    def attach_platform(self, domain: str, platform, factory) -> None  # platform: the EntityPlatform
    def begin_sync(self, definition_json, success, error) -> None      # callable from any thread
    def begin_remove(self, definition_id, done, error) -> None
    def begin_prune(self, definition_ids_json, done, error) -> None
    def push(self, batch_json) -> None
    async def async_command(self, unique_id: str, command: str, data: dict) -> None
    def close(self) -> None                                             # later calls are dropped; begin_* answer error("closed")
```

  - `begin_*` and `push` marshal onto the loop with `hass.loop.call_soon_threadsafe` (as `on_loop` in
    `__init__.py:132-143`) and never raise into .NET. `success`/`done`/`error` are called exactly once.
  - Sync is a desired-state diff for one definition, run as one task on the loop (spike rules 2, 5, 6, 8, 9):
    - **Device:** `dr.async_get_or_create(config_entry_id=…, identifiers={("hass_sharp", definition_id)}, name=…,
      manufacturer=…, model=…, sw_version=…, hw_version=…, configuration_url=…, suggested_area=…, via_device=…)`,
      then `dr.async_update_device(id, <field>=None)` for each described field that is now null. A definition
      described without a device: its entities are moved off (`er.async_update_entity(entity_id, device_id=None)`)
      and the device it had is removed with `dr.async_remove_device`.
    - **Name:** `_attr_name` is always set: the entity's `name`; when that is null, `None` on a device (the
      device's main entity) and the definition's name on a standalone entity.
    - **Live entity:** `apply(...)`, `async_write_ha_state()`, then
      `er.async_update_entity(entity_id, original_icon=…, entity_category=…, unit_of_measurement=…)`, and
      `device_id=…` when its device changed.
    - **New entity:** built with `device_info` set, then `await platform.async_add_entities([entity])`. An entity
      left without an `entity_id` afterwards was not added: when the registry holds a disabled entry for its
      unique id that is normal (its `entityId` comes from the registry and it is not live); otherwise sync
      answers `error` naming the unique id.
    - **Dropped entity:** `er.async_remove(entity_id)` for every registry entry of this config entry whose
      unique id starts with `<definition_id>:` and is not described.
    - Result JSON `{"entities": {unique_id: {"entityId": ...}}}`.
  - A `via` of `{"definition": id}` resolves to `("hass_sharp", id)`; `{"deviceId": id}` to that device's first
    identifier. A parent that does not exist is left out (Home Assistant ignores it).
  - A push for a unique id that is not live is kept (latest only) and applied when that entity is added.
    Liveness is the bridge's own record, never `entity.hass`.
  - Remove: `dr.async_remove_device` for the definition's device, then `er.async_remove` for every remaining
    registry entry with the prefix. Prune does the same for each unique id prefix (text before the first `:`)
    of this config entry that is not in the list.
  - `close()` forgets the platforms and live entities, so nothing is added to an unloaded platform.
  - `async_command` runs `manager.EntityCommand(unique_id, command, json.dumps(data))` through
    `dotnet_tasks.async_run_dotnet_task` and raises `HomeAssistantError` with the .NET message on failure.
  - Home Assistant imports that the pure logic does not need stay inside functions, so the module is testable
    with fakes (the pattern of `tests/python/test_services.py:25-40`).
  - Exact registry and entity calls follow the spike findings document.

- [ ] **Step 1: Write the failing tests** against fakes for the registries, the platform callbacks and the loop:
  `test_sync_adds_entities_through_the_platform`, `test_sync_updates_a_live_entity_in_place`,
  `test_sync_writes_icon_category_and_unit_to_the_registry`, `test_sync_clears_a_device_field_set_to_null`,
  `test_sync_names_main_and_standalone_entities`, `test_sync_treats_a_disabled_registry_entry_as_not_live`,
  `test_sync_answers_error_when_an_add_fails`, `test_sync_moves_an_entity_onto_a_new_device`,
  `test_sync_removes_entities_no_longer_described`, `test_sync_without_device_removes_the_old_device`,
  `test_sync_result_maps_unique_ids_to_entity_ids`, `test_sync_for_a_platform_not_attached_answers_error`,
  `test_push_applies_state_and_availability`, `test_push_before_add_is_applied_on_add`,
  `test_push_for_unknown_entity_after_close_is_dropped`, `test_remove_purges_registry_and_device`,
  `test_prune_keeps_known_definitions_and_purges_the_rest`, `test_prune_ignores_entries_of_other_config_entries`,
  `test_calls_from_another_thread_run_on_the_loop`, `test_callbacks_fire_exactly_once`,
  `test_command_failure_raises_home_assistant_error`, `test_close_answers_error_and_drops_pushes`.
- [ ] **Step 2: Run** `python3 -m unittest tests.python.test_entity_bridge -v` from the repository root.
  Expected: errors (module missing).
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the same command, then `python3 -m unittest discover -s tests/python`. Expected: OK.
- [ ] **Step 5: Commit** `Add the Python entity bridge`.

### Task 10: Platforms and integration wiring

**Files:**
- Create: `binary_sensor.py`, `switch.py`
- Modify: `sensor.py` (replace the stub), `__init__.py:148-378`
- Test: `tests/python/test_entity_platforms.py`, `tests/python/test_entry_lifecycle.py`

**Interfaces:**
- Consumes: Task 9; `PyInterop` members of Task 2; `manager.EntityCommand`.
- Produces:
  - Each platform module: one `HassSharpEntity` subclass (`HassSharpBinarySensor(BinarySensorEntity)`,
    `HassSharpSensor(SensorEntity)`, `HassSharpSwitch(SwitchEntity)`) and
    `async_setup_entry(hass, entry, async_add_entities)` that calls
    `hass.data["hass_sharp_entity_bridge"][entry.entry_id].attach_platform(<domain>, entity_platform.async_get_current_platform(), <class>)`.
    `apply_values`: `is_on` → `_attr_is_on`; sensor `value` → `_attr_native_value`. Sensor `apply` maps options
    `unit` → `_attr_native_unit_of_measurement`, `stateClass` → `_attr_state_class`, `options` → `_attr_options`.
    The switch's `async_turn_on` / `async_turn_off` call `self.command("turn_on")` / `("turn_off")`.
  - `__init__.py`: setup creates the bridge and stores it in `hass.data["hass_sharp_entity_bridge"]`, assigns
    `PyInterop.BeginSyncEntities = Action[String, Action[String], Action[String]](bridge.begin_sync)`,
    `BeginRemoveEntities = Action[String, Action, Action[String]](bridge.begin_remove)`,
    `BeginPruneEntities = Action[String, Action, Action[String]](bridge.begin_prune)`,
    `PushEntityStates = Action[String](bridge.push)`, forwards `PLATFORMS` (replacing the three `["sensor"]`
    literals), then runs `Init` as today. The failure path and `async_unload_entry` call `bridge.close()` before
    unloading the platforms and pop it from `hass.data` after `UnloadAsync`.

- [ ] **Step 1: Update `test_entry_lifecycle.py`** so its fakes provide the new `PyInterop` members and the bridge,
  and add: `test_platforms_are_forwarded_from_the_constant`, `test_bridge_is_closed_before_platforms_unload`,
  `test_entity_delegates_are_assigned_before_init`, `test_failed_setup_closes_the_bridge`.
- [ ] **Step 2: Write `test_entity_platforms.py`** against a real `HomeAssistant` (skipped without it, as
  `tests/python/test_native_triggers.py:12-14`), with a `MockConfigEntry`-free setup that forwards the three
  platforms and a fake manager: `test_synced_entities_reach_the_state_machine`,
  `test_device_and_entities_are_registered_with_the_unique_id`, `test_push_changes_state`,
  `test_unavailable_push_gives_unavailable`, `test_switch_turn_on_reaches_the_manager`,
  `test_failed_command_fails_the_service_call`, `test_resync_renames_a_live_entity`,
  `test_removed_entity_leaves_the_registry`, `test_unload_keeps_registry_entries`.
- [ ] **Step 3: Run** `python3 -m unittest tests.python.test_entry_lifecycle tests.python.test_entity_platforms -v`.
  Expected: lifecycle tests fail; platform tests skip on the host.
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run** `python3 -m unittest discover -s tests/python` (host), then the suite inside the container
  per `AGENTS.md` ("copy `tests/python` and `tests/fixtures` into a temporary directory in the container…") so
  the platform tests run with nothing skipped. Expected: OK both times; report the skipped count of each.
- [ ] **Step 6: Commit** `Add binary sensor, sensor and switch platforms`.

### Task 11: Websocket command and authoring service

**Files:**
- Modify: `websocket.py:58-88`, `authoring.py:66-67`
- Test: `tests/python/test_visual.py` (the `load_routes` helper at `:76`), `tests/python/test_authoring_services.py`

**Interfaces:**
- Consumes: `manager.EntityRequest` (Task 6).
- Produces: websocket command `hass_sharp/entities` with payload `{request: {operation, ...}}`, admin-only and
  gated on the entry being ready, exactly as `hass_sharp/node_library`; `AuthoringService.entities(request, *, is_admin)`
  passing through to `manager.EntityRequest`.

- [ ] **Step 1: Write the failing tests:** `test_entities_route_passes_the_request_to_the_manager`,
  `test_entities_route_requires_admin`, `test_entities_route_answers_not_ready_before_setup`,
  `test_authoring_entities_returns_the_parsed_envelope`.
- [ ] **Step 2: Run** `python3 -m unittest tests.python.test_visual tests.python.test_authoring_services -v`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `python3 -m unittest discover -s tests/python`. Expected: OK.
- [ ] **Step 5: Commit** `Add the hass_sharp/entities websocket command`.

### Task 12: Python.NET smoke and live check

**Files:**
- Create: `tests/packaging/entity_bridge_smoke.py`
- Modify: `.github/workflows/tests.yaml:56-61`, `AGENTS.md` (tests section: one line naming the smoke)

**Interfaces:**
- Consumes: everything above, published (`dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`).

- [ ] **Step 1: Write the smoke,** modelled on `tests/packaging/native_trigger_bridge_smoke.py`: load the published
  assembly under Python.NET, assign Python callables to the four entity delegates, drive a `HassSharpManager`
  over a temp config directory (library save, definition save, `EntityCommand`), and assert the sync JSON, a
  pushed batch and the command's effect arrive in Python as `str`, with the callbacks invoked from .NET threads.
  Exit non-zero with a message on any mismatch.
- [ ] **Step 2: Run** it against the local container without touching its data:
  `docker exec -i hass python - /config/custom_components/hass_sharp /config/custom_components/hass_sharp/dotnet < tests/packaging/entity_bridge_smoke.py`.
  Expected: prints `entity bridge smoke OK`, exit 0.
- [ ] **Step 3: Add the smoke to `tests.yaml`** beside the other Python.NET smokes, and the line to `AGENTS.md`.
- [ ] **Step 4: Live check in the local container.** Publish, `docker compose restart hass`, wait for the
  integration to load (`docker compose logs --tail=200 hass`: no errors from `custom_components.HassSharp`).
  With a long-lived token for the local instance, over the websocket API:
  1. `hass_sharp/node_library` save of a library `livecheck` declaring a `LiveCheckPlug : SharpDevice` with a
     switch, a power sensor fed by `Every`, and a binary sensor.
  2. `hass_sharp/entities` save of definition `livecheck_plug`. Expect `active: true`.
  3. `config/device_registry/list` and `config/entity_registry/list` show one device and three entities with
     unique ids `livecheck_plug:<key>`; `get_states` shows live values.
  4. `call_service switch.turn_on` on the switch changes its state; a command the class rejects returns an error.
  5. Reload the integration (`config_entries/reload`): same entity ids, no duplicates.
  6. `hass_sharp/entities` delete: the device and the three registry entries are gone. Delete the library.
  Report each step's observed result. Leave the instance as it was found.
- [ ] **Step 5: Run all four suites** from Global Constraints. Expected: all pass.
- [ ] **Step 6: Commit** `Add the entity bridge Python.NET smoke`.

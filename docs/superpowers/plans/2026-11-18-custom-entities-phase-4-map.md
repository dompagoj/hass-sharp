# Phase 4 code map (tools, six more domains, state restore)

Repo `/Users/dompa/dev/HassSharp`, branch `custom-entities`, HEAD `1288fc2`. Paths are under
`custom_components/hass_sharp/` unless they start with `tests/` or `docs/`. Every line number was read at HEAD.
Short names: `D` = `HassSharp/Devices/`, `V` = `HassSharp/Visual/`, `DH` = `D/DefinitionHost.cs`, `DHG` = `D/DefinitionHost.Graph.cs`,
`SE` = `D/SharpEntities.cs`, `RT` = `V/VisualRuntime.cs`, `VNC` = `V/VisualNodeCatalog.cs`, `VGV` = `V/VisualGraphValidator.cs`,
`cat` = `visual_node_catalog.json`, `fe` = `frontend/src/`, `ct` = `fe/visual/contract.ts`, `ft` = `frontend/tests/`, `tr` = `tests/Runtime/`,
`tp` = `tests/python/`. "P2 hand-off" = `docs/superpowers/plans/2026-11-17-custom-entities-phase-2.md:402-411`; "P3 hand-off" = `…-phase-3.md:263-274`.

## 1. `HassSharp/Devices/` at a glance

| File (lines) | Holds |
|---|---|
| `SharpComponent.cs` (145) | `IComponentHost` (l.11-28), `EntityChange` (l.34), `SettingAttribute` (l.41-50), `SharpComponent` (l.56-145) |
| `SharpEntity.cs` (183) | `EntityCategory`; abstract `SharpEntity`: key/name/icon/deviceClass/category/available/attributes, `Publish`, `Domain`, `Commands`, `Command(name, JsonElement)`, `Values()`, `DomainOptions()`, `Describe`, `Snapshot`, `ValueJson`, `AttributeJson`, `KeyPattern` |
| `SharpDevice.cs` (49) | device metadata, `Add<T>`, `Entities`, `Sealed` |
| `SharpEntities.cs` (229) | `SharpBinarySensor` l.7, `SharpSensor` l.30, `SharpSwitch` l.72, `SharpDomains` (l.113-204: `Typed`, `All`, `Of`, `OfName`, `Create`, `Graph`), `EntityDescription` l.207, `GraphDomain` l.221, `GraphState` l.228 |
| `DefinitionHost.cs` (583) | `CompiledDefinition` l.15; host: prepare (class), start, stop, command, publish, `Every`, `Track`, `Log`, JSON cache clearing |
| `DefinitionHost.Graph.cs` (403) | graph prepare, `GraphStates`/`Carry`, `RunFlow`, `SetState`/`SetAvailable`/`Setting`/`IsSecret`, `StateEnded`, `FlowFailed`, `CommandLabels`, `GraphSettings` |
| `IDefinitionRuntime.cs` (79) | the runtime→host contract (`SetState`, `SetAvailable`, `Setting`, `IsSecret`) |
| `EntityStatePump.cs` (110) | debounce/dedupe/batch to `PyInterop.PushEntityStates` |
| `EntityDirectory.cs` (42) | `LiveEntity` record; started hosts by definition id; `TryGet(uniqueId)` |
| `EntityApi.cs` (396) | `EntityJson`, `CompileDefinition`, `TrackHistory`, activate/load/unload, request dispatch, `EntityCommand` (l.389-395) |
| `EntityLifecycle.cs` (211) | Init loading, prune, library-save rebuild, masked answers |
| `EntityDefinition.cs` (321) | document records and `EntityDefinitionStore` (files `<id>.json`, l.114) |
| `DefinitionDocument.cs` (142) | document checks (entities, options l.79-105, settings) |
| `DefinitionGenerator.cs` (140) | class factory source, class-setting checks (l.86-122), preview |
| `DefinitionGraph.cs` (74) | `Compose`/`Decompose`, `Roots` (commands keyed `<key>:<command>`, l.51-73) |
| `DefinitionSettings.cs` (56) | `[Setting]` values applied by reflection (`Apply` l.13-25, `TryConvert` l.38-55: string, enum name, bool, int, long, double) |

## 2. `SharpComponent` and what the host owns

- Members today (`SharpComponent.cs`): `internal IComponentHost? Host` l.59; `protected internal virtual Task Setup()` l.67, `Start()` l.74, `Stop()` l.83; `protected CancellationToken Stopping => Host?.Stopping ?? CancellationToken.None` l.89; `protected IDisposable Every(TimeSpan interval, Func<Task> action, bool immediately = true)` l.98; `protected IDisposable Track(string entityId, Func<EntityChange, Task> action)` l.107; `protected EntityStateSnapshot State(string entityId)` l.111; `protected async Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false)` l.123 (waits on `Host.Calls`, not `Stopping`, l.127-130); `protected void Log(string message, PyLogLevel level = PyLogLevel.Info)` l.137.
- **`Http`, `Files` and `SharpFiles` do not exist in any form** (no stub, no member, no type; grep of the repo finds no `SharpFiles`, `http_request` or `download_file`). The design lists them (`docs/superpowers/specs/2026-11-17-custom-entities-design.md:153,163-164`).
- `IComponentHost` (l.11-28): `Stopping`, `Calls`, `Publish`, `Every`, `Track`, `Log`. Adding `Http`/`Files` there means updating the test fake `tr/SharpEntityTests.cs:14-25` `RecordingHost` (it returns `new MemoryStream()` as a no-op disposable).
- **Where `Stopping` comes from:** `DH:53` `CancellationTokenSource stopping`; `PrepareAsync` replaces it with `CreateLinkedTokenSource(lifetime)` (`DH:88`), `lifetime` = `UserScript.LifetimeToken` (`UserScriptManager/UserScript.cs:103`); cancelled in `StopCoreAsync` (`DH:277`). `calls` (`DH:52`) is cancelled after `UserScript.RunDrainTimeout` (`DH:275`) and finally (`DH:320`).
- **Ownership pattern for timers/tracks (the model for a host-owned `HttpClient`):** lists `timers`, `tracks`, `running` under `gate` (`DH:46-49`). `Every` (`DH:420-442`): refuses after stop (`Nothing`, l.427), `PeriodicTimer` wrapped in `Repeating` (l.574-582) whose loop task is kept. `Track` (`DH:445-488`): subscribes outside the lock (Python GIL), registers the handle under the lock, disposes it at once if a stop came in between. `StopAsync` (`DH:243-262`) snapshots and clears both lists under the lock; `StopCoreAsync` (`DH:264-324`): bound `calls` → cancel `stopping` → dispose tracks and timers → drain `Start()` → drain running callbacks/commands/timer loops → `Stop()` on entities then device → `finally` drops `device`, `entities`, `graphEntities`, `awaiting`, clears JSON caches (`DH:307-323`). A host-owned `HttpClient` must outlive the drain (callbacks may still use it) and `Stop()`, which "may still call services" (`SharpComponent.cs:77-78`): dispose it in the `finally` after the `Stop()` calls (l.298-304).
- `UserScript.ClearCoreAsync` calls `Host.StopAsync()` (`UserScriptManager/UserScript.cs:149`).

## 3. The domain table, and what one domain touches

- `SharpDomains.Typed` (`SE:116-121`) = `[(SharpBinarySensor,"binary_sensor"),(SharpSensor,"sensor"),(SharpSwitch,"switch")]`; `All` l.124; `Of(Type)` l.127 (walks base types); `OfName(string)` l.139 (used by the library compiler, `V/NodeLibraryCompiler.cs:743`).
- `SharpDomains.Graph` (`SE:166-182`): `domain → GraphDomain(Func<EntityDescription, SharpEntity> Create, IReadOnlyDictionary<string, GraphState> State)` (l.221); `GraphState(Func<SharpEntity, object?, string?> Refuses, Action<SharpEntity, object?> Keep, Func<SharpEntity, object?> Read)` (l.228). Helpers `OnOff(read, keep)` l.185-186, `SensorRefuses` l.192-203. `Create(domain, description)` throws for a domain not in `Graph` (l.154-159). `EntityDescription` reads `key,name,icon,deviceClass,entityCategory,enabledByDefault,options` (l.207-218).
- Per typed class: `Domain` (e.g. `SE:101`), `Commands` (`SE:102`, a static list l.74), `Command(name, data)` switch (`SE:103-108`), `Values()` (`SE:109`, the state keys on the wire), `DomainOptions()` (`SE:60-65`, the options on the wire). Each command is a `protected virtual Task TurnOn()` + `public Func<Task>? OnTurnOn { get; init; }` delegate (`SE:92-99`); **`data` is ignored by `SharpSwitch`** (l.105-106).
- `tr/EntityDomainTests.cs:16-38` pins: `entityDomains` keys == `SharpDomains.All` in order (l.20); per domain, `state` keys == `Values()` keys, `commands` keys == `Commands`, `options` keys == `DomainOptions()` keys (l.25-27), a label (l.28), state types are catalog types (l.29-30); `SharpDomains.Graph` keys == `entityDomains` keys and state keys in order (l.33-36). l.42-72: automations never embed `entityDomains`.

**Checklist for one new domain** (worked from `switch`, with `sensor` for options):

| # | Where | `switch` / `sensor` today |
|---|---|---|
| 1 | Typed class in `SE` (state setters publish, command method + delegate, `Domain`, `Commands`, `Command`, `Values`, `DomainOptions`) | `SE:72-110` / `SE:30-66` |
| 2 | `SharpDomains.Typed` entry | `SE:120` |
| 3 | `SharpDomains.Graph` entry: `Create` from `EntityDescription` (+ options), one `GraphState` per state key | `SE:179-181` / `SE:171-178` |
| 4 | `cat` `entityDomains.<domain>` `{label, state{key:{types,label}}, commands{c:{label}}, options{o:{type,enum?,label}}}` | `cat:2031-2050` / `cat:1999-2030` |
| 5 | Document option checks read the table (types `string`, `array`, `enum`; others via `Fits`) | `D/DefinitionDocument.cs:79-105` |
| 6 | Python platform module + `const.PLATFORMS` | `switch.py:1-23`, `const.py:4` |
| 7 | Python test copy of the list | `tp/test_entry_lifecycle.py:10` |
| 8 | Panel: type list, option fields, labels (section 14) | `fe/entities/EntityRows.tsx:62,176-209`, `NodePickers.tsx:17` |
| 9 | Tests: `EntityDomainTests` passes by itself once 1-4 agree; add domain cases to the fixture and to `SharpEntityTests` | — |

`ValueJson` (`SharpEntity.cs:128-147`) decides how a state value goes on the wire (P2 hand-off T1: misses `Half`/`nint`/`BigInteger` (l.146 → text), `DateTime` Unspecified written without offset (l.144), `IReadOnlyDictionary`-only attributes become an array of pair texts (`AttributeJson` l.161-166)).

## 4. Commands end to end

1. HA service → platform method, e.g. `switch.py:13-17` `async_turn_on(self, **kwargs)` → `await self.command("turn_on")` (**kwargs dropped**).
2. `entity_base.py:53-55` `async def command(self, name: str, **data)` → `bridge.async_command(unique_id, name, data)`.
3. `entity_bridge.py:117-124` → `dotnet_tasks.async_run_dotnet_task(hass, manager.EntityCommand, unique_id, command, json.dumps(data))`; any exception → `HomeAssistantError(_message(error))`.
4. `D/EntityApi.cs:389-395` `public async Task EntityCommand(string uniqueId, string command, string? dataJson)`: `EntityDirectory.TryGet` (else "The entity is not running"), `JsonDocument.Parse(dataJson or "{}")` (a non-object is passed through; malformed → `JsonException`, P2 untied item), `host.Command(entity, command, data.RootElement.Clone())`.
5. `DH:375-395` `Command(SharpEntity, string, JsonElement)`: refuses unless `Running` (l.381), counted in `running`; graph + known command → `RunFlow` (l.387), else `entity.Command(name, data)` (class override/delegate; unknown → `NotSupportedException`, `SharpEntity.cs:86-87`).
6. Graph: `DHG:122-145` `RunFlow`: method from `roots.Commands["<key>:<command>"]`; `FlowClass(method).RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(entity.Key, name, data))`; waits `CommandTimeout` (`DHG:13`, 30 s; the flow continues in the background, l.130-131); `RunOutcome.Status` `Succeeded`/`Stopped` ok, `Failed` → first line of error, `Cancelled` → `Because(reason)` (l.151-158).
- **Where `data` goes:** `TriggerPayload.Command` (`V/TriggerPayload.cs:94-99`) → `{"platform":"command","entity":<key>,"command":<c>,"data":<data>}` (data omitted when null/undefined). That payload is the run's trigger: readable by a library node as `NodeContext.Trigger` (`V/NodeContext.cs:31`) and recorded in the trace (capped at `MaxTriggerBytes` 16 KiB, `V/VisualTrace.cs:34`); the run summary leaves data out (`History/TriggerSummary.cs`). **No graph node reads it**: `entity_command` has `outputs: {}` (`cat:1720-1763`) and `RT:591` does nothing for it; only `kind == "trigger"` gets outputs from the payload (`RT:575` → `Trigger()` `RT:776-786`, pointers per `triggerOutputs`).

## 5. State

- `IDefinitionRuntime.SetState(string entityKey, IReadOnlyDictionary<string, object?> values, IReadOnlySet<string> hidden)` (`D/IDefinitionRuntime.cs:64`; doc: one entry per domain state key, plain `bool`/`double`/`string`/null). `SetAvailable(string entityKey, bool available)` l.66.
- Host side `DHG:179-199`: ignored once stopping/replaced (`Ignoring` l.171); checks **every given key** (unknown key or `Refuses` → `InvalidOperationException`, secret values not quoted) then `Keep`s each — **it already accepts a subset of keys and keeps the others** (l.188-194). The "every key" rule lives in the validators and the runtime (section 7). `SetAvailable` `DHG:204-216`. Both `Publish` outside the lock.
- `DH:404-417` `Publish`: only while `Running` or `Failed`; `Pump.Post(id, entity.Snapshot(id, running: status == Running))`.
- Snapshot (`SharpEntity.cs:114-122`): `{"uniqueId","available": Available && running,"values": Values(),"attributes": {...sorted}}`. Per domain on the wire: binary_sensor/switch `{"is_on": true|false|null}`, sensor `{"value": number|text|null}`. Unavailable posted by the API: `{"uniqueId","available":false,"values":{},"attributes":{}}` (`D/EntityApi.cs:215-220`).
- Pump (`D/EntityStatePump.cs`): debounce 25 ms default (l.13); `Post` keeps the latest JSON per id in first-post order (l.29-40); `FlushAsync` drops snapshots equal to `sent[id]` (l.64-65), pushes one JSON array (l.71-73), remembers what was sent unless a `Forget`/`Reset` happened meanwhile (l.81-84); `Forget(ids)` l.43; `Reset()` l.90. `StartAsync` forgets the new entities' ids so a replaced revision's identical states are sent again (`DH:191`).
- Push payload: `PyInterop.PushEntityStates(string json)` (`HassSharp/PyInterop.cs:211`) = `[snapshot, …]`.
- **Carry over a new revision** (`DHG:32-61`, `63-113`): `Replaced()` reads the running host's `GraphStates()` *before* the setup flow (l.38) — `GraphEntityState(Key, Domain, Available, KeyValuePair<string, object?>[] Values)` (l.64), only when `Running` and not invalidated, only plain values (l.78-84); `Carry` (l.102-113) applies to each entity no State works out (`awaiting`), same key and domain, all values accepted, **and copies `Available`**; then the When started flow may overwrite. Class definitions carry nothing (`GraphStates` reads `graphEntities` only). The two gaps in the P2 hand-off are visible here: the copy is taken once at l.38 while the old revision keeps taking commands until the swap (`UserScriptManager/UserScriptManager.cs:293-305`); availability is copied even for an entity the old revision's failing State left unavailable (l.111).
- First publish: entities a State works out are held back (`awaiting`, `DHG:19,43`; `DH:218-223`) until a run sets them; others are published at start with whatever they hold — a graph switch with no State starts `is_on: null` → HA `unknown`. Python starts each entity unavailable until its first snapshot (`entity_base.py:21-22`).
- **Places a restored state could enter:** (a) next to `Carry` in `PrepareGraph` (`DHG:38-45`, before the setup flow) from a .NET-side store; (b) the sync answer: Python answers `{"entities": {uid: {"entityId": …}}}` (`entity_bridge.py:278,284`), parsed at `DH:100-102` — **after** the setup flow has run (`DHG:47-59` precede `DH:97`); (c) a new delegate called before `PrepareAsync`. Class definitions have no hook at all (no "last state" member on `SharpEntity`).

## 6. Storage paths

- `AuthoringRoot => Path.Combine(HassPath.HassConfiguration, ".storage", "hass_sharp")` (`V/NodeLibraryApi.cs:8`); libraries `AuthoringRoot/libraries` (l.9), undo journal `authoring-undo.json` (`V/NodeLibraryStore.cs:53`); definitions `AuthoringRoot/entities/<id>.json` (`D/EntityApi.cs:11`, `D/EntityDefinition.cs:114`), runtime path `entities/entity_<id>.cs` never written (`D/EntityApi.cs:18`).
- Manager constructor builds its own: `runs.db`, `service_schema.json`, `entity_schema.json` under `hassConfigPath/.storage/hass_sharp` (`HassSharp/Manager.cs:45-47`). `HassPath` (`HassSharp/HassPath.cs:1-15`) has no storage helper. A `files/` root belongs beside `AuthoringRoot` (one more property) or in `HassPath`.
- The history DB is EF Core SQLite with migrations (`History/RunHistoryStore.cs:63-90`: `InitialHistory`, `ScheduleOccurrences`, `CompletedRetentionIndex`); `tests/packaging/sqlite_smoke.py` checks migrations — a last-state table would be one more migration.

## 7. Settings

- Attribute: `SettingAttribute(string? label = null)` with `Description`, `Secret` (`SharpComponent.cs:41-50`). No range, no title.
- Discovery `V/NodeLibraryCompiler.cs:691-758`: public instance property with public setter; form type from `SettingType` (l.761-772): enum → member names; `int` → `integer` with `int.MinValue..int.MaxValue` (l.765, the P3 hand-off); `long` → `integer` no range; `string`, `bool`, `double`. Label = ctor arg else property name; description = named arg else `<summary>` (l.724-726); `Secret` named arg (l.728). Unsupported → `SettingTypeMessage` "A setting is a string, bool, int, long, double or enum…" (l.679). **Title = `type.Name`** (l.754-755). Named arguments are read with `attribute.NamedArguments.FirstOrDefault(a => a.Key == "…")` — the place a `Minimum`/`Maximum` would be read.
- Descriptor `EntitySettingDescriptor(Key, Label, Description?, Type, Secret, Options)` + `Minimum?`/`Maximum?` (`V/VisualCatalogSnapshot.cs:51-59`); `EntityTypeDescriptor(Id, SourceId, TypeExpression, Kind, Domain?, Title, Description?, Settings)` (l.72-73). Save-time range check in `D/DefinitionGenerator.cs:86-122` ("takes a whole number from … to …"); `DefinitionSettings.Apply` does not range-check.
- Graph settings: types `string|boolean|number` (`D/DefinitionDocument.cs:55`), read once per prepare (`DHG:378-402`).

## 8. Failure logging and statuses

- **State dedupe (the pattern):** `DHG:239-290` `StateEnded(method, outcome, recorded, failure)`: per entity key, `failing[key]` (l.21) holds the last reason; warn once per new reason ("<Name (key)> is unavailable: <why>"), Debug with the whole failure, Info "has a state again" on recovery. Called from `UserScriptManager/ScriptSyncRunner.cs:139,179`.
- **Other flows (the failing poll):** `ScriptSyncRunner.cs:175` → `DHG:302-330` `FlowFailed(method, cause, failure)` logs **every failure** — Warn for a command, Error for When started and triggers ("<label> failed: <reason>") plus Debug with the stack. No dedupe.
- No slow-State warning exists; the model is `StartWarning` (`DH:42`, `DH:228-233`).
- Statuses: constants `setup|running|failed|stopped` (`DH:34`); `running` at `DH:199`, `failed` only when a `Start()` throws (`DH:201-212`); API maps no host or `stopped` → `off` (`D/EntityApi.cs:27,42`); `activationError` = load error, else host error when `failed`, else `null` when running, else "Not active" (`D/EntityApi.cs:48`).
- A class `Setup()` that throws, or a graph When started that fails (`DHG:53-59`), fails `PrepareAsync` → no host → `off` with the load error. On save/replace (`D/EntityApi.cs:145-158` → `RecordDefinitionLoadError` l.175-188 → `Manager.cs:16-36`) it is **recorded only, never logged**; at Init it logs Error "Cannot activate script <path>: <ex>" (`UserScriptManager/UserScriptManager.cs:203-206`). The graph's When started failure is also logged by `FlowFailed` (Error) as its run fails.
- Setup bound: `SetupTimeout` 60 s for all `Setup()`s together (`DH:38`, `DH:119-127`) and for the When started flow (`DHG:49-50`); Init prepares definitions one after another (`UserScriptManager.cs:189-199`).

## 9. Graph side: catalog entries

`entityDomains` (`cat:1985-2051`, complete):
```json
{"binary_sensor":{"label":"Binary sensor","state":{"is_on":{"types":["boolean"],"label":"On"}},"commands":{},"options":{}},
 "sensor":{"label":"Sensor","state":{"value":{"types":["number","string"],"label":"Value"}},"commands":{},
   "options":{"unit":{"type":"string","label":"Unit"},"stateClass":{"type":"string","enum":["measurement","total","total_increasing"],"label":"State class"},"options":{"type":"array","label":"Options"}}},
 "switch":{"label":"Switch","state":{"is_on":{"types":["boolean"],"label":"On"}},"commands":{"turn_on":{"label":"Turn on"},"turn_off":{"label":"Turn off"}},"options":{}}}
```

| Kind (cat) | Flags | Config | Inputs | Outputs / execution |
|---|---|---|---|---|
| `entity_state` (1684) | `root`, `documents:["entity"]`, `dynamicInputs:"entity"` | `entity` string req.; `trackHistory` bool default false | `{}` + state keys | `{}`; `execution: []` |
| `entity_command` (1720) | `root`, `documents:["entity"]` | `entity`, `command` strings req.; `trackHistory` default true | `{}` | `{}`; `["next"]` |
| `entity_setup` (1764) | `root`, `singleton`, `documents:["entity"]` | `trackHistory` default true | `{}` | `{}`; `["next"]` |
| `set_entity_state` (1796) | `documents:["entity"]`, `dynamicInputs:"entity"` | `entity` req. | `{}` + state keys | `["next"]` |
| `set_entity_available` (1826) | `documents:["entity"]` | `entity` req. | `available` boolean req., default literal true | `["next"]` |
| `setting` (1870) | `pure`, `documents:["entity"]` | `key` req. | `{}` | `value` type `""` (resolved per graph) |
| `entity_is` (1904) | `pure` | `{}` | `entity` [entity_id] req.; `state` [string] req., default "on" | `value` boolean |
| `entity_current` (1951) | `pure` | `{}` | `entity` [entity_id] req. | `value` `global::HassSharp.EntityStateSnapshot` |

- `dynamicInputs: "entity"` — C#: `VNC:432-439` (one port per `entityDomains[domain].state` key, `required: true`, no default; domain from `EntityDomain(catalog, config.entity)` `VNC:64-65`). TS: `ct:532-537` (same; `"required": true` hardcoded). Runtime: `RT:313-321` resolves the same ports through `VNC.DataInputs`; `EntityValues` (`RT:745-754`) calls `Input()` for **every** port, and an unset unwired port fails "Input … is unset" (`RT:427`); `EntityStateValue` (`RT:760-773`) converts. An optional state input needs: a flag per state key in `entityDomains` (e.g. `"required": false`), both validators reading it (`VNC:436`, `ct:536`), `EntityValues` skipping unsupplied ports with the existing `Supplied(id, port, operand)` (`RT:819`), and the host's merge, which exists (section 5). For a State (whole state recomputed) the partial meaning must be decided.
- `entity_command` outputs: would be declared per command in `entityDomains.<d>.commands.<c>` (e.g. `"data": {"brightness": {"types":["number"],"pointer":"/data/brightness"}}`), exposed through a new `dynamicOutputs` branch beside `"trigger"` in C# `DataOutputs` (`VNC:311`) and TS `dataOutputs` (`ct:446`), and set by the runtime the way `Trigger()` does (`RT:776-786`: pointer into the run's `trigger`, absent → `absentTriggerOutputs`, read error names it at `RT:388-390`). The payload already carries `data` (section 4). The validator's command checks: `VGV:646-648,664,832-837`, `ct:1229-1240,1383-1410`. `DefinitionGraph.Roots` keys commands `<key>:<command>` (`D/DefinitionGraph.cs:64-66`).
- Embedded catalog (`V/VisualGenerator.cs:110-146`): `nodes` pruned to the kinds a graph uses (l.114), `types` to those reached, `entityDomains` kept **only** for entity graphs (l.140), `triggerOutputs` only with a trigger (l.141), **every other section kept whole** (`limits`, `operandKinds`, `edgeKinds`, `structures`, `scopeContract`, `parametricTypes`, `version`, `runtimeSupport`). So a new node kind, a new `entityDomains` entry or a new pruned type never changes an automation's generated source (pinned by `tr/GeneratedSourcePinTests.cs`, fixture `tests/fixtures/generated-source-pins.json`); editing `service`/`trigger`/`get_field` specs, `triggerOutputs` or a whole-kept section does.

## 10. `call_service` as the model for `http_request`

- Spec (`cat:292-345`): config `service` (pattern `^[a-z0-9_]+\.[a-z0-9_]+$`), `fields` object default `{}`, `returnResponse` bool; input `target` `["target","entity_id"]` optional; output `response` `object` with `"when": "returnResponse"` (an output that exists only while a boolean config is true, `VNC:313-315`); `execution: ["next","error"]`; `dynamicInputs: "service"`.
- Generator: **emits nothing for it**. Built-in kinds run in the runtime's `Walk` switch (`RT:574-596`); only library/Custom C# nodes get a `case` in the generated handler, whose default throws "Unknown compiled node" (`V/VisualGenerator.cs:401-429`). A built-in `http_request` = catalog spec + one `case` in `RT` (+ validators only if it has rules beyond its spec).
- Runtime (`RT:825-849`): fields by name if `Supplied`, target, `hidden` list of secret-fed keys; `Call` → `HassServices.CallServiceAsync(…, hidden).WaitAsync(scope.CancellationToken)` wrapped in `Waiting(scope, task)` (`RT:788-789`, stores the trace before a wait); response → `Output(scope, id, ConvertTo(response,"object",…), "response")`. Cancellation: `scope.CancellationToken`; checks after each step (`RT:597-599`).
- Error routing is generic: a step whose `error` port is wired takes it, sets `scope.LastError`/`LastErrorStep` (`RT:602-610`); `last_error` reads them (`RT:673`). Trace: inputs via `Input()`→`Traced` (`RT:378-383`), outputs via `Output()` (`RT:456-467`); values capped by `TraceJson` (`MaxChars 1000`, `MaxValueBytes 4096`, `V/TraceJson.cs:31`) and events by `MaxEventBytes 16 KiB` (`V/VisualTrace.cs:34`).
- **Secret guard** (a5bda9b): `SecretInput(node, port, value)` `RT:276`, `Secret(output, value)` `RT:269-274` (bool outputs not secret), `Hidden(node, port)` `RT:280-281`, `HiddenValue` `RT:278`, taint sources from `FollowSettings` `RT:203-248` — **a step's own outputs are never secret** (doc l.199). Guard sites (the repeated idiom): `RT:357` (`Input<T>`), `461-463` (`Output`), `573` (`Number`), `583` (Log), `648`, `650` (Compare sides), `659` (convert), `664` (entity_field), `669` (object/array), `680` (get_field), `692` (setting), `734` (`EntityOf`), `750-751` (`EntityValues`), `829`, `832` (service `hidden`). **Not guarded:** `Compare(a,b,op)` at `RT:654` → `RT:807-817`, whose ordered fallback `Convert.ToDouble(a/b)` (l.815) can throw a `FormatException` that quotes the text.
- Checklist for a new action node:

| Part | Where | Note |
|---|---|---|
| Catalog spec | `cat` `nodes` | label, category, inputs/outputs/types, `execution:["next","error"]`, `documents` (omit = any document) |
| C# validator | `VGV` (generic from spec) | special rules only (cf. `Service` `VGV:907`) |
| Generator | none | built-in kinds are interpreted |
| Runtime | `RT:574-596` `case` + helper | `Waiting`, `scope.CancellationToken`, guard idiom, `Output` |
| TS contract | `ct` (generic from spec) | special rules mirrored with C#; `NodeSpec` type `ct:48` |
| Node config UI | `fe/visual/NodeConfig.tsx` | generic `ConfigField`s (enum → select, bool → switch) and `InputRow`s; `kindConfig` (l.31-35) only for a custom section |
| Summary | `fe/visual/nodeSummary.ts:186-268` `switch (node.kind)` | e.g. `service` case l.188-193 |
| Trace words | `fe/visual/trace/traceWords.ts` (64 l.; only `service` fields special, l.15) | usually none |
| Icon/category | `fe/visual/icons.ts:41-58`, `fe/styles/tokens.css:38-43` | known categories only |
| Shared fixture | `tests/fixtures/visual-graph-validation.json` (419 cases `{name, graph, valid}`), run by `tr/VisualGraphContractTests.cs:9` and `ft/visual-contract.test.ts` | both validators |
| Examples | `authoring_examples.json` (`{graphs, library}`), validated and compiled by `tr/AuthoringAdapterTests.cs:18` | |
| MCP listing | `llm.py:192-204` returns the catalog through `automation_catalog` (`llm.py:239-245`, drops kinds whose `documents` exclude `automation`) | a node without `documents` appears to the MCP; `download_file` with `documents:["entity"]` would not |

## 11. Values and trigger flows

- Graph types (`cat:12-159`): `boolean`, `number` (double), `string`, `object` (`JsonObject`), `array` (`JsonArray`), `entity_id` (refines string), `target` (refines object), `global::HassSharp.EntityStateSnapshot`, CLR aliases. JSON is read with **Get field** (`cat:1526-1581`: `pointer` RFC 6901, `type` enum `boolean|number|string|object|array|entity_id`, input `["object","array"]`; runtime `RT:674-681`). `select_property` is for typed C# values, not JSON. An `object` output cannot carry a JSON array or scalar (`ConvertTo` refuses, `RT:546`).
- Trigger flows in a definition: any `trigger` node is a root (`VNC:40`); a time pattern is the poll. Recorded only with `trackHistory: true` (`D/EntityApi.cs:98-102`; trigger default false in a definition, `ct` `trackHistoryDefault` ~l.1425). A failure logs on every tick (section 8).

## 12. HTTP today, load contexts, leak test

- No `HttpClient`/`System.Net.Http`/`HttpListener`/`TcpListener` anywhere in `HassSharp/` or `tests/` except one compile check: `tr/NodeLibraryTests.cs:164` previews a library method returning `new System.Net.Http.HttpClient()` and expects it valid.
- **User code can use `HttpClient` today**: libraries and Custom C# compile against `ProjectReferences.GetDefaultReferences()` + every `TRUSTED_PLATFORM_ASSEMBLIES` path (`V/NodeLibraryCompiler.cs:25-28`; `Compiler.cs:53`). Nothing owns or disposes such a client.
- `ScriptLoadContext` (`HassSharp/ScriptLoadContext.cs`): collectible, one per script; the HassSharp assembly is shared (l.33-37), the generated service/entity assemblies load from bytes (l.39-44), anything else through `AssemblyDependencyResolver(HassSharp.dll)` (l.46-47) — private copies of HassSharp's NuGet dependencies; framework assemblies (System.Net.Http) resolve to the default context (inference: the resolver returns null for them). A `SharpImage`-style rule (no third-party types in the API) applies to anything `Http`/`Files` expose.
- `tr/ReloadMemoryTests.cs` (`--reload-memory`, Order 30, ~50 s): each generation runs a definition whose library device **already** starts a 20 ms `Every` timer and a `Track`, calls a service from `Start()` and takes a switch command (doc l.12-17); then a graph definition saved repeatedly (l.119-130). Asserts every earlier generation's load context and assembly is collected (l.149-156) and managed memory grows at most `BoundMiB = 0.5` in the quietest window (l.44, 159). Extend the device class there with `Http` use (and a request to a loopback listener) for the leak check.
- Loopback server for tests: none exists; `tr/Runtime.csproj` references only `HassSharp.csproj` (no ASP.NET Core). `System.Net.HttpListener` or `TcpListener` (shared framework) are available without new packages.

## 13. Bridge and Python

- `PyInterop` entity members (`HassSharp/PyInterop.cs:205-240`): `BeginSyncEntities(json, success(string), error(string))` l.205, `BeginRemoveEntities` l.207, `BeginPruneEntities` l.209, `PushEntityStates(string)` l.211; awaitable wrappers `SyncEntitiesAsync` l.214, `RemoveEntitiesAsync` l.223, `PruneEntitiesAsync` l.232. Assigned in `__init__.py:285-288`; `EntityCommand` is called on the manager directly.
- Sync payload (`DH:90-96`): `{"definitionId","name","device": DeviceJson()|null,"entities":[Describe()…]}`; `Describe` (`SharpEntity.cs:99-111`): `{"uniqueId","key","domain","name","icon","deviceClass","entityCategory":"config"|"diagnostic"|null,"enabledByDefault","commands":[…],"options": DomainOptions()}`; device (`DH:545-565`): `{"name","manufacturer","model","swVersion","hwVersion","configurationUrl","suggestedArea","via":{"definition"}|{"deviceId"}|null}`. Example: `tp/test_entity_bridge.py:221` `PLUG`.
- Platforms: `const.py:4` `PLATFORMS = ["binary_sensor", "sensor", "switch"]`; forwarded `__init__.py:290`, unloaded l.338, 360. Each module's `async_setup_entry` calls `bridge.attach_platform(domain, entity_platform.async_get_current_platform(), Class)` (`switch.py:20-23`; bridge `entity_bridge.py:90-93` stores `(platform, factory)`). A sync with a domain whose platform is not attached is **refused whole**: `_Refused("The <d> platform of HassSharp is not set up")` (`entity_bridge.py:256-258`) → the .NET sync fails → `PrepareAsync` fails.
- Platform shapes: `binary_sensor.py:8-10` and `switch.py:9-11` `apply_values`: `self._attr_is_on = values.get("is_on")`; switch commands l.13-17; `sensor.py:9-17`: `apply()` sets `native_unit_of_measurement`, `state_class`, `options` from `description["options"]`, `apply_values`: `_attr_native_value = values.get("value")`.
- `entity_base.py` contract: `HassSharpEntity(Entity)`, `_attr_should_poll = False`, `_attr_has_entity_name = True` (l.13-14); `__init__(bridge, description, device, definition_name)` starts unavailable (l.16-23); `apply(description, device, definition_name)` metadata (l.25-42); `apply_state(snapshot)` → `available`, `extra_state_attributes`, `apply_values(values)` (l.44-48); `command(name, **data)` (l.53-55). No `RestoreEntity` anywhere.
- Bridge flow: pushes marshalled to the loop and applied to live entities or kept as the latest pending (`entity_bridge.py:105-115,214-228`); write failures logged per id/message, capped at 5 (l.230-249); sync adds or updates (l.251-284, `_async_sync_entity` l.327-361; a live entity's registry `unit_of_measurement` rewritten from the shown unit, l.337-338 — P2 hand-off T10 test gap).
- **Home Assistant is not importable on the host** (`ModuleNotFoundError: No module named 'homeassistant'`); the base classes of `ButtonEntity`, `NumberEntity`, `SelectEntity`, `TextEntity`, `LightEntity`, `CoverEntity` and `RestoreEntity` were not read. Read them in the 2026.11.1 container before writing the platform modules.

## 14. Python tests

- `tp/test_entity_bridge.py` (839 l.): no Home Assistant; `load(name, env)` execs a module with its `from … import` lines stripped into a dict of fakes (l.197-201); fakes `EntityCategory`, `HomeAssistantError`, `Entity`, `EntityRegistry`, `DeviceRegistry`, `Platform` (l.22-196); helpers `device()`, `description(key, domain='switch', …)`, `definition(*entities)`, `snapshot(unique_id, available, values, attributes)` (l.204-228). A new base class (e.g. `RestoreEntity`) must be put into `env`.
- `tp/test_entity_platforms.py` (510 l.): real Home Assistant (skipped without it, l.91-92); `load_package()` imports `entity_bridge`, `const`, `switch` (l.30-41); forwards `const.PLATFORMS` (l.149); service-call tests per platform (l.258-279). New platforms: add their state and command tests here.
- `tp/test_entry_lifecycle.py:10` keeps its own `PLATFORMS` list.
- **No contract test** ties the platform modules' commands to `entityDomains` (only `tp/test_mcp_adapter.py:39` reads the catalog); the design's "Python contract test" (spec l.199-200) is not built.
- `tests/packaging/entity_bridge_smoke.py` (423 l.): Python stand-ins on the four delegates; `EXPECTED` class entities (l.61), `GRAPH_ENTITIES` (l.96); checks sync shapes, pushes and commands. New domains are optional there.

## 15. Frontend

Domain knowledge (catalog-driven unless noted):

| Place | What |
|---|---|
| `fe/entities/NodePickers.tsx:17-21` | **hardcoded** `domainLabels` fallback + `entityTypeLabel(catalog, domain)` |
| `fe/entities/NodePickers.tsx:196-197` | command label (`entityDomains…commands[c].label` else `words.key`) |
| `fe/components/TriggerSummary.tsx:19-27` | command label for run rows ("Main relay: Turn on") |
| `fe/visual/nodeSummary.ts:251-256` | command label in the When commanded summary |
| `ct:1233-1235`, `ct:1356`, `ct:1383`, `ct:1410` | domain and command labels in validator messages (`text(entry.label, domainWords(key))`) |
| C#: `VGV:62` `DomainWords`, `VGV:617-625,664,667,835`; `DHG:165-166,365-374` `CommandLabels`; `D/DefinitionDocument.cs:83` | the server's copies |
| `fe/entities/EntityRows.tsx:17-21` | **hardcoded** `optionHints` for `unit`, `options` |
| `fe/entities/EntityRows.tsx:62,69,113-123,166-179` | type list = `Object.keys(entityDomains)`, new entity takes the first; retype; option fields |
| `fe/entities/EntityRows.tsx:189-209` `OptionField` | handles `array` (lines), `enum` (select), else **text** — a number option (min/max/step) would be saved as text and refused by the document check |
| `fe/entities/EntityRows.tsx:168-169` | device class is free text for every domain |
| `fe/entities/model.ts:218` | a new graph definition starts with a `binary_sensor` |
| `fe/entities/model.ts:241` | `definitionEntityKinds` |
| `fe/entities/liveState.ts:8` | `LiveState {state, available, unit?}` — no attributes, so brightness/position are not shown |
| `fe/entities/DefinitionCard.tsx:71-79` | `stateText`: HA state text, number + unit, Unavailable/Unknown (generic) |
| `fe/entities/EntityHelp.tsx:8` | text names "a binary sensor, a sensor or a switch" |
| `fe/components/CatalogPicker.tsx:10-28` `domainPaths` | per-domain icon paths (registry picker) |
| `ct:71` `EntityDomainSpec {label; state: Record<string,{types; label?}>; commands: Record<string,{label?}>; options: Record<string, ConfigSpec>}` | the TS typing to extend (`required`, command `data`) |
| `ct:205-231` | `entityDomain`, `entityCommands`, `entityChoices` (l.219), `withEntityCommand` (l.228-231) |
| `ct:1436-1456` | `addGraphNode` pre-fill → `prefilled(graph, kind, spec, catalog)` |

- `ft/visual-fixture.ts:1` imports `nodeCatalog` from `ct`, which imports the real `visual_node_catalog.json` (`ct:1,74`): **no duplicated `entityDomains`**. Leniencies (P3 hand-off): `disabled: false` always (l.253), host status only `running|failed|off` (l.245).
- Handed-over items: first-save id retry twice — `fe/visual/session.ts:399-419` (`adapter.newId` → `adapter.save`, on `conflict` once more) and `fe/entities/classSession.ts:167-181` (same with `EntityApiError`); `fe/views/RunTraceView.tsx:96-98` (`if (current.isError)` answers before `current.data`, the cached document, is consulted); `own` in `fe/visual/NodeConfig.tsx`: helper at l.37, shadowed by `const own = new Set(…)` at l.204 (used again at l.130, 284).

## 16. Runtime tests and tooling

| File (lines, flag) | Covers / helpers |
|---|---|
| `SharpEntityTests.cs` (250, `--sharp-entities`) | base classes alone; `RecordingHost : IComponentHost` l.14-25; snapshot/describe JSON shapes; commands to override/delegate |
| `EntityDomainTests.cs` (90, `--entity-domains`, `-embedded`, `--entity-graph-order`) | section 3 |
| `EntityHostTests.cs` (912, `--entity-host`) | host with `CompiledDefinition.Factory` (l.67): settings, setup, sync JSON, start, stop, timers, tracks |
| `EntityPumpTests.cs` (255, `--entity-pump`) | pump against a recording `PushEntityStates` |
| `EntityGraphRuntimeTests.cs` (503, `--entity-graph-runtime`) | `FakeDefinition : IDefinitionRuntime` (l.21-36: `Calls` "state k a=bool:true", `Settings`, `Secrets`, `FailUnavailable`), `TraceSink`, `Entity(nodes, edges, entities, settings)` graph builder l.56 |
| `EntityGraphTests.cs` (1727, `--entity-graph`) | whole manager; `Rig.Start(name, states)` (l.156-189), `Save/Read/Ok/Until/History`; commands via `rig.Api.EntityCommand(uid, cmd, dataJson)` (l.359-366) |
| `EntityApiTests.cs` (333, `--entity-api`) | API over a manager; commands l.132-140 (`{"transition":2}` passed, ignored) |
| `EntityStartupTests.cs` (485), `EntityStoreTests.cs` (613), `EntityTypeTests.cs` (112), `EntityGraphGeneratorTests.cs` (306), `EntitySchemaTests.cs`, `EntityGeneratorTests.cs`, `EntityHandleTests.cs` | restart/prune; documents; library types/settings; root emission; HA entity classes |
| `EntityTestHome.cs` (126) | temp config dir + recording fakes for sync/push/remove/prune/log: `Syncs(id)`, `Pushes()`, `Snapshots(fromBatch)`, `Logs(level)`, `Until(cond, what)`; restores `DefinitionHost.CommandTimeout` etc. on dispose (l.19,123) |

- Release packaging: `scripts/package_release.py:43` ships every `*.py` and `*.json` of the integration plus `out/`, `www/`, `translations/`, `brand/` — new platform modules ship with no change; `required` (l.34-38) needs nothing new. `tests/test_release_package.py` (5 tests) unaffected. A runtime `files/` cache lives under `/config/.storage`, not in the package.
- CI `.github/workflows/tests.yaml`: Python job on HA 2026.11.1 (l.15-37: `import probatio, …mcp_server…` check l.31, `unittest discover -s tests/python`, `-s tests`); .NET job (l.40-63: runtime suites, editor tests, publish, three Python.NET smokes l.56-63); frontend (l.65-78: `bun install --frozen-lockfile`, `bun run test`, `bun run build`).

## 17. Open questions for the planner

1. **Restore: Home Assistant's `RestoreEntity` or a .NET store?** Facts: no restore code anywhere; .NET carries graph states only between revisions in memory (`DHG:63-113`); the pump already holds the last JSON sent per unique id (`D/EntityStatePump.cs:21`), a natural tap for persisting; runs.db is EF SQLite with a migration pattern and a packaging smoke (`History/RunHistoryStore.cs:63-90`); a .NET-side value can enter before the setup flow (`DHG:38-45`), whereas anything Python learns at add time reaches .NET only in the sync answer, after the setup flow (`DH:97-102`); HA's restore semantics (what it stores for an entity that is unavailable at shutdown, when it saves) could not be read here (HA not importable). Class definitions need an API either way (no hook on `SharpEntity`). The .NET store is the one whose ordering the code already supports.
2. **Which states are restored, and against what?** Only entities no State works out (as `Carry`)? Restored availability (the P2 gap says do not carry it for keys that had a State)? Does a When started that sets a state win over a restored one (it wins over carry, `DHG:30`)? Class entities: a `virtual` restore callback or a pre-set property?
3. **Command data schemas.** `entityDomains.<d>.commands.<c>` holds only `label` (`cat:2041-2046`); a `data` map with types and pointers would serve both validators (`VNC:311`, `ct:446`), the runtime (`RT:776-786` pattern), the Python platforms and a contract test. HA's own field names and ranges (e.g. brightness 0-255, position 0-100) are not in the repo; classes get raw `JsonElement data` (`SharpEntity.cs:86`) and would parse it per typed class.
4. **Optional state inputs** (light brightness, cover position): flag in `entityDomains.state`, skip in `EntityValues` (`RT:745-754`) via `Supplied` (`RT:819`); what a State means when an optional input is unset (keep, or unknown).
5. **`http_request` output shape.** The graph has `string`, `object`, `array`; `get_field` reads object or array (`cat:1526`); an `object` output cannot hold a JSON array (`RT:546`). Options: `text` (string) + `json` (object|array via two outputs or a `responseType` config with `$type` like `get_field`'s `"type": "$type"`), `status` number, `headers` object. Non-2xx: error port or a status output? The `error` port and `last_error` come free (`RT:602-610,673`).
6. **Limits.** Trace caps values at 1000 chars / 4 KiB (`V/TraceJson.cs:31`); the run step limit is `executionSteps: 100000` (`cat:9`); nothing bounds a body size or a request time today. Timeout default vs `CommandTimeout` 30 s (`DHG:13`) and `SetupTimeout` 60 s (`DH:38`); redirects, TLS validation and a user-supplied certificate callback (a user delegate held by a shared handler would pin a load context).
7. **One client per host or one process-wide.** A per-host `HttpClient` must be disposed in `StopCoreAsync` (section 2); a process-wide `SocketsHttpHandler` in the non-collectible HassSharp assembly holds no user types if no user callback is set. `SharpComponent.Http` exposes either; the type it exposes must be a framework one (`ScriptLoadContext` rule).
8. **Secrets through HTTP.** A setting wired into a header or URL is traced `***` (`RT:380-383`), but the response is an action output and never secret (`RT:199`); a server echoing a token would show it. The P3 hand-off asks to revisit that rule and to route `http_request` through the guard from the start (and to factor the ~15 guard sites, section 10). `Compare`'s fallback (`RT:815`) stays unguarded until fixed.
9. **`download_file`.** Where it may run (entity documents only, so hidden from the MCP by `llm.py:243-244`, or automations too); what it outputs (the path as `string`? an object `{path, sha256, size}`?); whether a checksum mismatch deletes and fails; concurrency for two definitions downloading the same `sha256`; cleanup of unused cache folders; whether downloads count against `SetupTimeout` (P2 hand-off: revisit it, and prepare definitions off Init's sequential path, `UserScriptManager.cs:189-199`).
10. **Failing poll dedupe.** `FlowFailed` (`DHG:302-330`) has no memory; `StateEnded`'s `failing` map (`DHG:21,265-275`) is per entity key, a poll needs per trigger method; recovery line? Same for a class's `Every` callback failures (`DH:510-521`, logged every tick at Error).
11. **Statuses.** A failing class `Setup()` or When started answers `off` with `activationError`, and a save logs nothing (`D/EntityApi.cs:145-158`); `failed` comes only from `Start()` (`DH:201-212`). Whether to add a `setup failed` status (the panel says "Not running") and a warning log on save.
12. **Settings range and title.** Add `Minimum`/`Maximum` named arguments on `SettingAttribute` (read as `Secret` is, `V/NodeLibraryCompiler.cs:728`) and a class-level title attribute (title today `type.Name`, l.754); `DefinitionSettings.Apply` would then need the same range check as `DefinitionGenerator.cs:86-122`.

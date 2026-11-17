# Custom entities: design

## Context

HassSharp can automate Home Assistant but cannot add anything to it. The integration creates no
entities or devices: `sensor.py` is an empty platform stub, the panel's `/entities` and `/devices`
routes show a placeholder, and .NET can read state and call services but has no way to register an
entity, set its state or receive a command.

This design adds an **Entities** section to the panel's top navbar where entities and devices are
defined with a node graph or a C# class. It has to cover:

1. **A derived entity.** A binary sensor that is on when either of two motion sensors is on, or any
   other combination, built from nodes alone.
2. **New hardware.** A Shelly plug over its REST API: one poll feeds a switch and two sensors, and
   there may be several plugs, each with its own address.
3. **A custom camera.** Frames from an existing camera, rectangles and text drawn on them,
   published as a new camera entity.
4. **Devices.** Entities grouped into a Home Assistant device with its metadata.

## Decisions (yours)

1. **Entities get their own definitions**, a new document kind beside visual automations.
2. **A definition holds one or more entities, optional device details, settings and logic.** A
   standalone entity is a definition with one entity and no device. A device can carry shared
   logic, so one poll feeds all its entities, and settings such as an address.
3. **Logic is a node graph or a C# class.** Nodes alone are enough for simple entities; C# is
   opt-in for what nodes do not cover.
4. **Hand-written classes live in the node library files** and derive from new `SharpEntity` and
   `SharpDevice` base classes. The Entities section creates instances of them with settings, so one
   class serves many devices.
5. **Camera: frames and drawing.** HassSharp provides source frames, a draw API and publishing as
   a camera (snapshot plus a low-rate MJPEG stream). It ships no detection model or inference
   runtime.
6. **A setup step with file downloads.** Every definition has a setup step that runs before its
   entities come up, with cached, checksum-verified file downloads (models, data). NuGet packages
   and in-process inference are the next project.
7. **ImageSharp** is the imaging library; the project is open source. The licence itself is not
   named yet: ask at the start of the camera phase and add a `LICENSE` file then.
8. **Feature branch in place** (`custom-entities`), not a worktree.
9. **Opus 5.5 subagents do all implementation.** The main session coordinates, reviews and
   verifies; it does not write product code.

## Goals, non-goals, anti-goals

Goals
- The four cases above work end to end, the first without any C#.
- Entities appear in Home Assistant as real entities: registry entries, devices, commands,
  availability, and for cameras a snapshot and a stream.
- A graph definition and a class definition are the same thing to the host, the bridge and Home
  Assistant; only how the logic is written differs.
- Saving a definition replaces it in place, as saving an automation does, without restarting Home
  Assistant.

Non-goals
- NuGet package dependencies for user code, and in-process model inference.
- Entity types beyond the first ten (below), and the full feature surface of light and cover.
- Device triggers, device actions and diagnostics.
- A key-value store for user code beyond restored entity state.
- Reusable graph templates: a second graph-defined plug is a duplicate with other settings.

Anti-goals
- An automation valid today becomes invalid, changes its generated source, or needs migrating.
- Unloading or reloading the integration removes entities from the registry.
- User-assembly objects or imaging-library types crossing into Python or between load contexts.
- A definition that fails to compile or set up taking other definitions or automations down.
- A polling definition flooding run history.

## Approaches

- **A. Own definitions with a graph or a class (chosen).** Each definition is one document with
  its entities, device, settings and logic. Largest build, but entity behaviour lives in one place
  and both nodes and classes have a home.
- **B. Empty slots filled by automations.** The section only declares entities; automations set
  state with a new node and react to commands with a new trigger. Much smaller, but an entity's
  behaviour is scattered, nothing recomputes at startup unless wired, and classes and cameras fit
  poorly. Rejected.
- **C. Every entity a C# class, nodes only wiring inputs.** Fails "programmable using nodes".
  Rejected.

Within A, the alternative of keeping a class inside the one definition that uses it was rejected
because a second device would mean copying the code.

## Design

Paths are under `custom_components/hass_sharp/` unless they start with `tests/` or `docs/`.

### What the user sees

```
Entities                                              [+ New]
▾ Kitchen plug            device · 3 entities · graph
    switch.kitchen_plug           on
    sensor.kitchen_plug_power     41.2 W
    sensor.kitchen_plug_energy    3.1 kWh
  binary_sensor.any_motion        standalone · graph   on
▾ Driveway faces          device · class FaceCam
    camera.driveway_faces         streaming
```

The derived motion sensor is four nodes and no trigger:

```
[Is on ▸ motion A]─┐
[Is on ▸ motion B]─┴─[Or]──▶[State ▸ any motion]
```

A device with shared logic, as a graph:

```
[Time pattern /10 s]─▶[HTTP request {host}/rpc/Switch.GetStatus]─▶[Set state ▸ relay]─▶[Set state ▸ power]
[On turn on ▸ relay]─▶[HTTP request {host}/relay/0?turn=on]─▶[Set state ▸ relay = on]
```

The same device as a class in a node library:

```csharp
public class ShellyPlug : SharpDevice
{
    [Setting] public string Host { get; set; } = "";
    readonly SharpSwitch relay;
    readonly SharpSensor power;
    public ShellyPlug()
    {
        relay = Add(new SharpSwitch("relay") { OnTurnOn = () => Turn(true), OnTurnOff = () => Turn(false) });
        power = Add(new SharpSensor("power") { Unit = "W", DeviceClass = "power" });
    }
    protected override Task Start() { Every(TimeSpan.FromSeconds(10), Poll); return Task.CompletedTask; }
}
```

### Stored document

`.storage/hass_sharp/entities/<id>.json`:
`{version, id, revision, name, enabled, device|null, settings[], entities[], logic, source, sourceMap}`.

- `device`: manufacturer, model, swVersion, hwVersion, configurationUrl, suggestedArea, and `via`
  (`{definition}` or `{deviceId}`).
- `settings[]`: `{key, label, type, value, secret}`.
- `entities[]`: `{key, domain, name, deviceClass, icon, entityCategory, enabledByDefault, options}`;
  keys match `^[a-z][a-z0-9_]{0,39}$`.
- `logic`: `{kind: "graph", graph}` or `{kind: "class", type: "library:<sourceId>:NodeLib.<Type>"}`.
  For a class, the code declares the entities and the settings schema; the document stores setting
  values and device overrides.
- Unique id `<definitionId>:<entityKey>`; device identifier `(DOMAIN, definitionId)`.
- For validation and running, entities and settings are composed into `graph.definition`. Unknown
  graph keys are preserved (`HassSharp/Visual/VisualGraphContract.cs:27-37`) and both validators
  already lift per-graph data into the catalog (`VisualNodeCatalog.cs:70`,
  `frontend/src/visual/contract.ts:210`). Compose and decompose are pure functions on both sides.

### C# API (`HassSharp/Devices/`)

- `SharpComponent`: `Setup()`, `Start()`, `Stop()`, `Stopping`, `Http`, `Files`,
  `Every(interval, fn)`, `Track(entityId, fn)`, `State(id)`, `CallService(...)`, `Log(...)`.
- `SharpDevice : SharpComponent`: device metadata properties, `Add<T>(entity)`, `Entities`.
  `[Setting]` marks properties (string, bool, int, long, double, enum, entity handles).
- `SharpEntity : SharpComponent`: `Key`, `Name`, `Icon`, `DeviceClass`, `Category`, `Available`,
  `Attributes`, `Publish()`.
- Typed entities are concrete, with state setters and each command as both an overridable method
  and a delegate, so deriving (`class Relay : SharpSwitch`) and composing inside a device both
  work: `SharpBinarySensor`, `SharpSensor`, `SharpSwitch`, `SharpButton`, `SharpNumber`,
  `SharpSelect`, `SharpText`, `SharpLight`, `SharpCover`, `SharpCamera`.
- `SharpFiles.Download(url, sha256, name?)` returns a cached path under
  `.storage/hass_sharp/files/<sha256>/`.
- `SharpImage` (`Load`, `DrawRect`, `DrawText`, `ToJpeg`, `Width`, `Height`) hides ImageSharp. Each
  script load context gets private copies of non-HassSharp assemblies (`ScriptLoadContext.cs:47-48`),
  so library types must not appear in the API.

### Hosting

Definitions are hosted by the existing automation host, not a parallel one.

- `UserScript` gains an optional `DefinitionHost`, hooked at the existing points in
  `UserScriptManager/UserScript.cs`: prepare (build the device, run setup, register entities last so
  a failed stage leaves Home Assistant untouched), start, clear (`Stop()`, dispose `Http`, timers and
  tracks; never purge) and set-enabled. Stage, swap and retire in `UserScriptManager.cs` are reused.
- A class definition compiles to a factory (`SharpDefinition.Create()`); settings are applied by
  reflection. It has no `Automation` classes.
- A graph definition compiles through `VisualGenerator` and `VisualRuntime`. A reactive
  `entity_state` sink becomes an `AutomationAction` in Restart mode that runs at startup and when
  any entity its pure upstream reads changes; the dependencies are extracted statically from the
  graph. Commands and setup become actions too.
- `ScriptSyncRunner.RunRecorded` returns the run record, so a failed command fails the Home
  Assistant service call.
- One state pump per manager: latest value per unique id, a short debounce, unchanged values
  dropped, one batched push per flush.
- `NodeLibraryCompiler` discovers public non-abstract `SharpDevice` and `SharpEntity` classes and
  their settings as entity types. A library save previews, commits and reactivates dependent
  definitions with the dependent automations.

### Catalog (`visual_node_catalog.json`)

`graphVersion` stays 5; everything is additive.

- New spec fields `root`, `documents` and `singleton`, and a table `entityDomains` (state inputs,
  commands and options per domain) that is the single source for both validators, the runtime, the
  editor forms and a Python contract test.
- "Is a trigger" becomes "is a root" in both validators, the generator and the flow code. Existing
  `trigger` nodes stay valid roots in entity graphs, so polling is a time-pattern trigger.
- New nodes: `entity_state` (reactive sink), `entity_command` (root; a camera frame request is the
  command `frame`), `entity_setup` (root), `set_entity_state`, `set_entity_available`, `setting`
  (pure), `entity_is` (pure), `http_request`, `download_file`, `camera_snapshot`,
  `image_draw_rect`, `image_draw_text`; new type `image`.

### Bridge

- `PyInterop`: `BeginSyncEntities`, `BeginRemoveEntities`, `BeginPruneEntities`,
  `PushEntityStates`, `BeginCameraSnapshot`.
- `HassSharpManager`: `EntityRequest(json)` (list, read, preview, save, delete, set_enabled, types,
  run), `EntityCommand(uniqueId, command, dataJson)`, `CameraImage(uniqueId, width, height)`.
  Camera frames are pulled, so nothing runs when nobody is watching.
- Only strings and bytes cross the boundary.

### Python

- `const.py`: `PLATFORMS`, replacing the three hardcoded `["sensor"]` in `__init__.py`.
- `entity_bridge.py`: `EntityBridge`, holding the per-platform add callbacks and the live entities,
  syncing one definition's desired state, cleaning the entity and device registries, and carrying
  pushes, commands and camera frames. `entity_base.py`: the shared entity base.
- One thin platform module per entity type.
- Setup creates the bridge, assigns the delegates, forwards the platforms, then runs `Init`. Unload
  closes the bridge before the platforms unload and never purges the registry.
- `websocket.py`: `hass_sharp/entities`. `llm.py`: `entities_*` MCP tools.

### Frontend (`frontend/src/`)

- A nav link, and routes `/entities`, `/entities/:id` and `/entities/:id/runs/:runId`.
- A new `entities/` folder for the model, API, definition panel (entities, device, settings),
  class-definition editor, cards and live state; two views; one stylesheet.
- The canvas, inspector, bottom drawer, top bar, palette, pickers and dialogs are reused. The
  editor session and `VisualEditor` take the document kind and API as parameters.

## Phases

Each phase ends with every suite in `AGENTS.md` passing and a live check in the local container.

1. **Bridge, platforms, base classes, host, class definitions over the JSON API** (binary sensor,
   sensor, switch). Begins with a spike on Home Assistant's entity lifecycle and on passing bytes
   through Python.NET.
2. **Graph-defined definitions.** Catalog and shared validation fixture first, then the generator
   and runtime. The derived motion sensor is the acceptance case.
3. **The Entities section in the panel.** Both example definitions built without leaving it.
4. **Tools and the remaining types.** `Http`, file downloads, `http_request`, `download_file`,
   button, number, select, text, light, cover, restored state.
5. **Camera.** Begins with a spike on ImageSharp (version, licence text, Alpine on arm64, fonts).
6. **MCP tools and docs.**

## Builder's calls

- First entity types: binary_sensor, sensor, switch, button, number, select, text, light (on/off
  and brightness), cover (open, close, stop, position), camera.
- Class names `SharpSwitch`, `SharpSensor` and so on; `SwitchEntity` and friends are the generated
  read-only handles.
- A definition that is off, fails to compile or fails setup keeps its entities as unavailable.
  Registry entries go only on delete or when an entity is removed from the definition.
- `http_request`, `camera_snapshot`, the image nodes and `entity_is` are also usable in automations.
- Run history is on for command and setup runs and off by default for reactive state.
- `/devices` redirects to `/entities`; `TODOView` is deleted.
- New names avoid existing ones: `DefinitionGenerator` (not `EntityGenerator`), `entity_bridge.py`
  (not `entities.py`), `frontend/src/entities/` (not `entities.ts`).

## Risks

- **Load-context leaks** from long-lived user code: host-owned disposables, and the reload memory
  test extended with a definition that uses a timer.
- **Home Assistant entity lifecycle** (adding entities late from a stored callback, updating a live
  entity, unavailable after unload): the phase 1 spike, turned into tests against a real instance.
- **Bytes through Python.NET**: the phase 1 smoke; base64 strings are the fallback.
- **ImageSharp on Alpine**, and its licence terms: the phase 5 spike.
- **Validator parity** between C# and TypeScript: the shared fixture, both changed together.
- **Static dependency extraction** misses entity reads inside C# code: a validator warning now,
  dynamic collection later.
- **Setup of a new revision overlaps the old running one**: setup is for downloads, start is for
  resources.
- **Secret settings are stored as plain JSON**: masked in the panel and redacted in traces.

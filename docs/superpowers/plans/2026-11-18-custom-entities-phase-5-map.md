# Phase 5 code map (camera: frames, snapshots, images)

Repo `/Users/dompa/dev/HassSharp`, branch `custom-entities`, HEAD `f646c0e`. Paths are under
`custom_components/hass_sharp/` unless they start with `tests/` or `docs/`. Every line number was read at HEAD `f646c0e`.
Short names: `D` = `HassSharp/Devices/`, `V` = `HassSharp/Visual/`, `DH` = `D/DefinitionHost.cs`, `DHG` = `D/DefinitionHost.Graph.cs`,
`SE` = `D/SharpEntities.cs`, `SCt` = `D/SharpControls.cs`, `SCo` = `D/SharpComponent.cs`, `EA` = `D/EntityApi.cs`,
`EL` = `D/EntityLifecycle.cs`, `PI` = `HassSharp/PyInterop.cs`, `RT` = `V/VisualRuntime.cs`, `VGV` = `V/VisualGraphValidator.cs`,
`TJ` = `V/TraceJson.cs`, `VNC` = `V/VisualNodeCatalog.cs`, `cat` = `visual_node_catalog.json`, `EB` = `entity_bridge.py`, `init` = `__init__.py`,
`fe` = `frontend/src/`, `ct` = `fe/visual/contract.ts`, `ft` = `frontend/tests/`, `tr` = `tests/Runtime/`, `tp` = `tests/python/`,
`EDT` = `tr/EntityDomainTests.cs`, `fx` = `tests/fixtures/visual-graph-validation.json`,
design = `docs/superpowers/specs/2026-11-17-custom-entities-design.md`, spike = `docs/superpowers/plans/2026-11-17-custom-entities-phase-1-spike.md`,
P4 = `docs/superpowers/plans/2026-11-18-custom-entities-phase-4.md`.

What the design asks of phase 5: `SharpImage` (Load, DrawRect, DrawText, ToJpeg, Width, Height) hides ImageSharp
(design:165-167); nodes `camera_snapshot`, `image_draw_rect`, `image_draw_text`, new type `image`, the frame request is
command `frame` under `entity_command` (design:198-206); bridge `BeginCameraSnapshot`, `CameraImage(uniqueId,width,height)`,
"Camera frames are pulled", "Only strings and bytes cross" (design:208-215); phase 5 opens with the ImageSharp spike
(design:236-249); no imaging-library type crosses (design:65); polling must not flood history (design:67); snapshot +
low-rate MJPEG (design:32-34); ImageSharp licence asked, LICENSE added (design:38-39; P4:316-321).

## 1. The four domain tables, and what a tenth row touches

| Table | Where | Cover (ninth row) as the model |
|---|---|---|
| CLR class per domain | `SE:120-131` `SharpDomains.Typed` (cover l.130); `All` l.134, `Of` l.137-143, `OfName` l.149-154 (used by `V/NodeLibraryCompiler.cs:774` to tag library classes) | `SCt:476-576` `SharpCover` |
| Graph factory + state table | `SE:181-232` `SharpDomains.Graph` (`GraphDomain(Create, State)` record l.316, `GraphState(Refuses, Keep, Read)` l.323); button's empty state l.196-198; `Create(domain, description)` l.169-174 throws for a domain not in `Graph` | cover l.225-231 |
| Catalog row | `cat:2161-2469` `entityDomains` (button `state:{}` l.2230; cover l.2421-2468: `state`, `commands.<c>.data`, `options`) | read by `DHG:630-641` (`CommandLabels`, `CommandValues`), `D/DefinitionDocument.cs:37,99-152` (options), `VNC:312,377` (command outputs), `ct:217-261` |
| Python platform | `const.py:4` `PLATFORMS` (9 names); `cover.py` (43 l.) | `HassSharpCover` l.10, `attach_platform("cover", …)` l.40-43 |

A tenth row (`camera`) touches, beyond the four tables:
- `DHG:50` builds every described graph entity through `SharpDomains.Create` → needs a `Graph["camera"]` entry even when stateless; `DHG:134,197,220,298,357` look the domain up in `Graph`.
- `D/DefinitionDocument.cs:152` `SharpDomains.DefaultOptions(domainId)` (`SE:157-158`) for option defaults.
- `VGV:852` lists domains that take commands in `definition.unknownCommand`'s message.
- Frontend lists derived from the row: `fe/entities/EntityRows.tsx:57` (domain choice), `fe/entities/NodePickers.tsx:225-229` ("takes no commands"), `ct:247-261`. Hand-written: `fe/entities/EntityHelp.tsx:8` (nine types in prose), `fe/entities/DefinitionCard.tsx:103-121` (state text per domain), `fe/components/CatalogPicker.tsx:22` (camera icon already there).
- Python test copies: `tp/test_entry_lifecycle.py:10` `PLATFORMS` literal; `tp/test_entity_bridge.py:229-231` `PLATFORM_BASES` (no `Camera`), l.237-239 env for `platform_class`, `COMMANDS` l.251-253.

Tests that pin the tables, and what a stateless, bytes-returning camera row breaks:

| Test | Asserts (line) | Camera row |
|---|---|---|
| `EDT --entity-domains` | row keys == `SharpDomains.All` order l.38 | add to both in the same position |
| | `SharpDomains.Create(domain, {key:"main", options: all flags true})` l.45-47 | needs `Graph["camera"]` |
| | state keys == `Values()` l.48; each state key a read/write property, optional → nullable l.54-59 | `state:{}` + no `Values` override passes (button does) |
| | commands == `entity.Commands` l.49 | `frame` in the row ⇒ `SharpCamera.Commands` has `frame` |
| | options == `DomainOptions()` l.50; option types string/number/boolean/array + label l.97-98 | e.g. `frame_interval` number is fine |
| | per data value: wrong type refused, `ArgumentException` ending `", not <wrong>"` l.70-72; missing refused with `" name: it takes "` unless optional l.73-75; defaults l.78-84 | `width`/`height` must be optional numbers, or go through `CommandData.Whole` (`SCt:582-609`) |
| | **if a command has data, `On<Pascal>` is a `Func` of `data.Count` args whose last type arg is exactly `typeof(Task)` l.87-91; nullability matches optional l.92-94** | **`Func<int?,int?,Task<SharpImage?>> OnFrame` fails l.91**; a `frame` without `data` skips the check |
| | `Graph` keys == row keys, graph state keys == row state l.101-104 | `Graph["camera"]` with empty state |
| `EDT --entity-domains-embedded` l.108-139 | automations never embed `entityDomains` | unchanged |
| `tp/test_entity_contract.py` | `PLATFORMS == list(DOMAINS)` l.48-49; `<domain>.py` calls `attach_platform` once l.51-56; commands sent via `self.command("<literal>", kw=…)` == row commands l.58-64 (`sent()` l.31-44 raises on any other call shape); required data keys sent l.66-73 | **a `frame` row command forces `camera.py` to call `self.command("frame", …)`**, which returns `None` today (§2) |
| `fx` case "code definition.unknownCommand: entity that takes no commands" | message at l.33759 lists "switch, button, number, select, text, light or cover" | **changes** if the row has a command |
| `VGV:674-678` `command.unhandled` warning | every row command without a When commanded warns | a camera graph without a `frame` flow warns; a class camera never does |
| `tp/test_entity_bridge.py` `PlatformModulesTest` l.905+ | each platform over fake bases | needs a `Camera` fake base and a frame test |
| `tp/test_entity_platforms.py` `controls()` l.89-107, `CONTROLS`/`CALLS` l.111-115 | one entity per platform through real HA | camera has no service that reaches a command; add a `camera.async_get_image` test |

## 2. Commands end to end, and where a JPEG would travel

Today (no value comes back anywhere):

| Hop | Code | Returns |
|---|---|---|
| HA service → platform | `cover.py:27-37` `await self.command("set_position", position=…)` | — |
| `HassSharpEntity.command(name, **data)` | `entity_base.py:63-65` → `bridge.async_command` | `None` |
| `EB:117-124` `async_command` | `dotnet_tasks.async_run_dotnet_task(hass, manager.EntityCommand, uid, command, json.dumps(data))`; error → `HomeAssistantError(_message(error))` | discards result |
| `dotnet_tasks.py:23-55` | `OnCompleted` → `GetAwaiter().GetResult()` on a .NET pool thread → `loop.call_soon_threadsafe(set_result, result)` (l.20-28 of the function) | **would return a `Task<T>`'s value** (`System.Byte[]` for `byte[]`); no cancellation flows into .NET |
| `EA:453-459` `EntityCommand(uid, command, dataJson)` | `Entities.TryGet` else "The entity is not running"; `JsonDocument.Parse`; `await host.Command(…)` | `Task` |
| `DH:486-506` `Command(entity, name, data)` | refuses unless Running l.492; tracked in `running`; graph + known command → `RunFlow` l.498, else `entity.Command` l.499 | `Task` |
| `DHG:258-282` `RunFlow` | `CheckData` l.262; method `roots.Commands[key+":"+name]` l.263 (`D/DefinitionGraph.cs:62-63`); `FlowClass(method).RunToEnd(method, InvocationCause.Command, TriggerPayload.Command(…))` l.264; timeout 30 s `DHG:13`, l.267-268 "goes on in the background"; Failed → first line, Cancelled → `Because` | nothing; `RunOutcome(Status, Error)` only (`UserScriptManager/ScriptSyncRunner.cs:19`) |
| `UserScriptManager/UserScriptClass.cs:216` `RunToEnd` | `Task<RunOutcome>` via `SyncRunner.Run` | no flow value |
| Class: `SharpEntity.Command(name, JsonElement)` `D/SharpEntity.cs:158-159`; cover `SCt:556-563` | delegate or virtual | `Task` |

What blocks a frame through this path:
- `entity_command` roots run **Queued** (`V/VisualGenerator.cs:40-42`): HA pulls at ≥ 0.5 s (spike:74-83) and an abandoned pull still runs; backlog grows when a frame takes longer than the interval.
- `trackHistory` default true for `entity_command` (`cat:1720-1764`, `ct:1556`); `ScriptSyncRunner` records history unless `TrackHistory == false` (l.94+) → 2 runs/s into history (design:67, design:260-261).
- 30 s `CommandTimeout` vs HA's 10 s `camera.async_get_image` timeout (spike:74-83).
- `IDefinitionRuntime` (`D/IDefinitionRuntime.cs:55-84`: `SetState`, `SetAvailable`, `Setting`, `IsSecret`, `Files`) has no frame setter; a graph flow cannot hand an image to `RunFlow`.

Where `CameraImage` would sit: `EA` next to `EntityCommand` (l.453): `Task<byte[]?> CameraImage(string uniqueId, int? width, int? height)`; Python `camera.py`'s `async_camera_image` awaits it through `dotnet_tasks.async_run_dotnet_task` (`dotnet_tasks.py:58-65`) and returns `bytes(result)` (spike rule 16: `bytes(System.Byte[])` 0.03 ms/200 KB). Python `None` → `int?` through Python.NET is unverified here (spike has no row for it). Pacing: HA calls `async_camera_image()` every `_attr_frame_interval` (default/min 0.5 s) and writes a frame only when bytes changed; a viewer leaving raises `CancelledError` in the pending await, which `dotnet_tasks` does not pass to .NET (spike:74-83).

## 3. The bridge, delegates and threads

- Delegates are static fields on `PI:174+` assigned from Python at `init:271-288`. Shapes to copy: `BeginSyncEntities: Action<string, Action<string>, Action<string>>` (`PI:205`, set `init:285` from `entity_bridge.begin_sync`); `CallService: Action<string,string,string?,Action?,Action<string>?>` `PI:192`; `CallServiceWithResponse` `PI:194` (answers on an executor thread, `init:201-227`). Task wrappers with `TaskCompletionSource(RunContinuationsAsynchronously)`: `PI:179-186` (`RegisterHaTriggersAsync`), `PI:214-220` (`SyncEntitiesAsync`), `HassServices.cs:67-83,87-115`.
- **`BeginCameraSnapshot(entityId, width, height, onBytes, onError)`** goes beside `BeginSyncEntities` (`PI:205`) as `Action<string, int?, int?, Action<byte[]>, Action<string>>` (or `Action<string,string,…>` with a JSON size), wrapped in a `CameraSnapshotAsync` like `SyncEntitiesAsync`. Python side: a function in `init` (beside `call_service` l.181-199, which uses `hass.add_job`) or `EB`, scheduled on the loop (`init:132-143` `on_loop` = `call_soon_threadsafe`), awaiting `camera.async_get_image(hass, entity_id, timeout=10, width=…, height=…)` → `Image(content_type, content)`; answers with `System.Byte[]` built by `Array.CreateInstance(Byte, n)` + `memoryview(arr)[:] = data` (0.07 ms/200 KB) — not the implicit conversion (10–30 ms/200 KB) (spike rule 16). Continuations must leave the loop thread: `RunContinuationsAsynchronously`.
- **`CameraImage(uniqueId, width, height)`**: `HassSharpManager` method in `EA` (§2); Python calls it, so no new delegate.
- No `byte[]` crosses today; the only base64 is `init:68-70` `serialize_service_response`. `HassSharp/PythonNetAssemblies.cs:61-94` keeps collectible user assemblies out of Python.NET: a `SharpImage` (HassSharp assembly) is safe, a user type is not; neither should reach Python — only `byte[]`.
- Threads: `CallService` callbacks run on the loop (`init:181-199`); `CallServiceWithResponse` on an executor (`init:201-227`); `dotnet_tasks` continuations on a .NET pool thread → `call_soon_threadsafe`.
- Test stubs: `tr/EntityTestHome.cs:14-42` saves and replaces the four entity delegates + `Log`, restores at dispose (~l.110-126); `tests/packaging/entity_bridge_smoke.py:195-225` assigns them from Python (and `Entity`, `CallService*`, tracking) — a fifth delegate belongs in both.

## 4. Graph type system: everything that enumerates types

| Place | Line | What an `image` type needs |
|---|---|---|
| `cat` `types` | l.12-159 (fields: description, clrType, nullable, category, assignableTo, literalKind, label, listed) | `image` with `clrType: global::HassSharp.…SharpImage`; **no `literalKind`** and `listed:false` |
| `V/NodeLibraryCompiler.cs` | l.228-232 resolves every `clrType` by `GetTypeByMetadataName` into `VisualCatalogSnapshot.Types` (`V/VisualCatalogSnapshot.cs:88`) | the type must be public in the HassSharp assembly |
| `V/VisualTypes.cs` | `Resolve` l.60, `IsAssignable` l.96-111, `ValidLiteral` l.122 | no refinement; not assignable to object/string |
| `VGV` | `BuiltIn` labels l.51-55, `Primitive` l.56-57, `TypeLabel` l.159-183, `CompatibleTypes` l.428, `KnownType` l.437, wire error l.602, `enumSource` types l.884 | label "Image" in `BuiltIn` |
| `fe/visual/typeLabels.ts` | `builtIn` l.3-6 (TS twin), `typeLabel` l.31-60, **`typeColor` l.124-136** (else stable hue) | label + colour token |
| `fe/styles/tokens.css` | port colours l.20-26, entity/target l.35-37 | `--hs-port-image` |
| `ct` | `TypeSpec.listed` l.53; `listedTypes` l.139 (Constant/Convert/loops `enumSource:"types"`, `cat:785,841,1161`; `fe/visual/NodeConfig.tsx:152`, `StructureEditors.tsx:151`); `constantType` needs `literalKind` (`fe/visual/constant.tsx:31,46`) | `listed:false` keeps it out of Convert and Constant |
| `ft/visual-contract.test.ts:216` | `listedTypes(nodeCatalog) == Object.keys(nodeCatalog.types)` | **breaks** with `listed:false` in the built-in catalog; `ft/widgets.test.tsx:156,188`, `ft/InputEditor.test.tsx:253` take all type keys |
| Generated runtime | `V/VisualGenerator.cs:385-388` `[id]=typeof(…)` for used types; embedded catalog prunes types l.136 | automatic |
| `RT.ConvertTo` / `Coerce` | l.594-603 / l.525-577 (`IsInstanceOfType` l.529) | instance passes as is |
| **`VisualSnapshot.Copy`** | `RT:18-83`: as-is list l.24 (primitives, strings, `EntityStateSnapshot`, `Entity`), `IVisualSnapshot` l.28-32, arrays element-wise l.34-37, refuses Delegate/Task/Stream/SafeHandle l.58 and `System.`/`Python.` types l.77, else `MemberwiseClone` + field recursion l.78-82 | **`SharpImage` must implement `IVisualSnapshot` (`RT:10`) or join l.24 as immutable**; otherwise every wire read (`VisualScope.TryGetOutput` l.128-136) and loop item (l.1119-1120) deep-copies ImageSharp internals. A raw `byte[]` output is copied element by element (l.34-37) |
| **`TJ` trace writer** | caps `MaxDepth=6, MaxItems=50, MaxChars=1000, MaxValueBytes=4096` l.31; special cases l.337-348 (`EntityStateSnapshot`, `Entity`, `Exception`); `ShapeOf` l.206+ walks HassSharp types by public fields/auto-props, `System`/`Microsoft` → opaque l.229-230 | add a `case SharpImage` (e.g. `{width,height}` or a type name) beside l.342; never the pixels/bytes |
| `fe/visual/trace/traceModel.ts` | opaque `global::` strings l.72, `valueText` l.80-94 | how an image value reads in a trace |
| `TJ` secret names l.34-60, `RT` `Secret`/`Guarded` l.261-311 | redaction | a frame from a camera is not a secret; decide whether `http_request` body bytes that become an image carry its secrecy (`RT:244` carries url/sha256 for downloads) |

## 5. How a step is added (`http_request` / `download_file` as worked example)

| Piece | `http_request` | `download_file` |
|---|---|---|
| Catalog node | `cat:1985-2104` (category Data, no `documents`, config method/responseType/timeout/failOnStatus/hideResponse, inputs url/headers/body, outputs status/body `$responseType`/headers, execution next/error) | `cat:2105-2159` (category Entity, `documents:["entity"]`, output path) |
| Validator rules | `VGV:951-962` (codes `http.url`, `http.body`; `WebAddressPattern` l.136, `BodyMethod` l.141) | `download.checksum` (`ChecksumPattern` l.138) |
| TS twin | `ct:1425-1438`; codes in `diagnosticCodes` l.973 | same |
| Runtime | `RT.Walk` l.637 → `HttpRequest` l.940-1014 (`SharpHttp.CreateForRequests()` l.957, deadline l.959-960, redirects ≤ 5 l.925, answer ≤ 4 MiB l.923, `ReadAnswer` l.1059-1069 reads `byte[]`) | `RT.Walk` l.639-642 → `definition!.Files.Download(…)` in `Waiting` l.860-861 |
| Settings secrecy | `HidesResponse` l.287, `HeadBody` l.285 | `FollowSettings` l.244 |
| Entity-only kinds | — | `RT:795` `EntityKinds`; `tr/GeneratedSourcePinTests.cs:24` `UnpinnableKinds` |
| Frontend | `fe/visual/nodeSummary.ts:271-275`, `fe/visual/connectionIndex.ts:211`, `fe/visual/trace/traceWords.ts:84`; palette by `documents` `fe/visual/graph.ts:36-39` | `nodeSummary.ts:276-283` |
| Tests | `fx` cases "code http.url…", "code http.body…"; pin "automation: HTTP request reads JSON…" in `tests/fixtures/generated-source-pins.json`; `ft/palette.test.tsx:10,28,54`; `ft/node-summary.test.ts:94-107`; `tr/LoopbackServer.cs` (`Reply` l.28-50, `Start` l.70) | "code download.checksum…"; `ft/palette.test.tsx:10` entity-only list |
| Authoring | `authoring_examples.json` "Read a device over HTTP" (`tr/AuthoringAdapterTests.cs:18`) | — |

Checklist for `camera_snapshot` / `image_draw_rect` / `image_draw_text`: catalog node (+`documents` if entity-only) → `VGV` rules + `ct` twin + `fx` cases (C# and TS read one fixture) → `RT.Walk` case beside l.636-642 (async ones in `Waiting`, `scope.CancellationToken`) or `Evaluate` l.697-758 for pure draw steps → pins file line or `UnpinnableKinds` (`tr/GeneratedSourcePinTests.cs:53-58` prints the line to add) → `nodeSummary.ts` switch l.186 → `ft/palette.test.tsx` lists → `fe/visual/icons.ts` category ~l.43-50 → `llm.py:239-245` drops kinds whose `documents` exclude automation.

## 6. Reading another entity, and async HA calls (the `call_service` model)

- Sync reads: `RT.HomeAssistantState` l.781-784 (`PyInterop.Entity`, `PI:191`), `ReadEntity` l.785; class `SCo:145` `State(entityId)` → `VisualRuntime.ReadEntity(entityId, fresh:true)`; graph `entity_is`/`entity_current` evaluated in `RT.Evaluate` l.753-754.
- Async model: `RT.Service` l.897-908 → `Call` l.916-921: `HassServices.CallServiceAsync(…).WaitAsync(scope.CancellationToken)` inside `Waiting`. `HassServices.CallServiceAsync` (`HassSharp/HassServices.cs:67-83`) = TCS + `PyInterop.CallService(domain, service, json, done, error)`; with response l.87-115.
- Class model: `SCo:157-166` `CallService` waits on `Host.Calls` (`IComponentHost.Calls`, `SCo:19`), not `Stopping`.
- `NodeContext` (`V/NodeContext.cs`, 64 l.) for Custom C#: `State` l.44, `CallService` l.57, `Log` l.63, `CancellationToken` l.22 — a `CameraSnapshot` there too, if custom nodes may take frames.
- A `camera_snapshot` step reads `entity_id` input (picker kinds as `read_entity` `cat:728`), calls the `CameraSnapshotAsync` wrapper (§3), `WaitAsync(scope.CancellationToken)`, outputs `image`. Works in automations unless `documents:["entity"]` (design:259 wants it in automations).

## 7. Class side

- `IComponentHost` (`SCo:11-35`): `Stopping` l.14, `Calls` l.19, `Publish` l.21, `Every` l.23, `Track` l.25, `Log` l.27, `Http` l.32, `Files` l.34. Implementations: `DH:33` and the test fake `tr/SharpEntityTests.cs:16-31` `RecordingHost` (Files throws). A snapshot needs no host member (it is a bridge call like `CallService`) unless it must be cut by `Calls`.
- `SharpComponent` (`SCo:71-181`): `Setup` l.82, `Start` l.89, `Stop` l.98, `Stopping` l.104, `Every` l.115-116, `Track` l.124, `Http` l.134, `Files` l.141, `State` l.145, `CallService` l.157-166, `Log` l.171-175, `Attached` l.177-181. **`protected Task<SharpImage> CameraSnapshot(string entityId, int? width = null, int? height = null)`** goes beside `CallService`, with its `Host.Calls` wait.
- `SharpCover` in full (`SCt:476-576`), the pattern for `SharpCamera`:
  - `static readonly string[] CoverCommands = ["open","close","stop","set_position"]` l.478
  - state properties publish on set (`IsClosed` l.491-495; `Position` checked l.502-515); option `Positionable {get;init;}` l.518
  - delegates `Func<Task>? OnOpen/OnClose/OnStopMoving`, `Func<int,Task>? OnSetPosition` l.521-527
  - virtuals `Open/Close/StopMoving/SetPosition(int)` default to the delegate or throw `NotSupportedException` l.530-542
  - `Domain` l.553, `RestoresByDefault` l.554, `Commands` l.555, `Command` switch l.556-563, `CheckData` l.564, `Values` l.565, `Restore` l.566-567, `DomainOptions` l.568, `Given(data)` via `CommandData.Required` l.570-575
- Stateless model: `SharpButton` `SCt:35-59` (no `Values`, `ButtonCommands=["press"]` l.37).
- `SharpCamera` frame override: `protected virtual Task<SharpImage?> Frame(int? width, int? height)` (or `Func<int?,int?,Task<SharpImage?>>? OnFrame`) — it is **not** a `Command` (returns a value; see `EDT:87-94` in §1). Host side: `CameraImage` (§2) finds the entity via `Entities.TryGet` (`EA:455`), refuses unless Running as `DH:492`, tracks the task in `running` like `DH:486-506`, encodes `ToJpeg` in .NET.
- `SharpImage` lives in the HassSharp assembly; user scripts share it (`HassSharp/ScriptLoadContext.cs:35-37`), other dependencies resolve per context (l.47-48).

## 8. Python platforms

- Base `entity_base.py` (65 l.): `HassSharpEntity(Entity)` l.12; `should_poll=False`, `has_entity_name` l.13-14; `__init__(bridge, description, device, definition_name)` calls `super().__init__()` l.16-23 (starts unavailable l.22) — for `class HassSharpCamera(HassSharpEntity, Camera)` the MRO makes that call `Camera.__init__()` (spike rule 15 requires it to run); `refusal` l.26-33; `apply` l.35-52; `apply_state` l.54-58; `apply_values` l.60-61; `command` l.63-65.
- `cover.py` (43 l.) shape: features in `apply` l.11-16, `apply_values` l.18-25, commands l.27-37, `async_setup_entry` → `bridge.attach_platform("cover", entity_platform.async_get_current_platform(), HassSharpCover)` l.40-43.
- `camera.py` would: set `_attr_frame_interval` from an option, `content_type="image/jpeg"`, implement only `async_camera_image(width=None, height=None)` (spike rule 15), no `apply_values`.
- `EB`: `attach_platform` l.90-93; missing platform refused at sync l.259-261; new entity added l.360-363; live entity applied in place l.341-348.
- `const.py:4` `PLATFORMS` → `init:290` forwards them; unload `init:330,338`.

## 9. Frontend

- Entity editor: `fe/entities/EntityRows.tsx` domains l.57, new entity default l.64, spec l.123, `OptionField` l.190-235 (array l.225, number l.230, boolean l.235); `fe/entities/model.ts:218` new definition starts `binary_sensor`.
- Live state: `fe/entities/liveState.ts` `LiveState` l.9 (unit, brightness, position, mode), kept attributes l.24-29 — camera's `entity_picture`/`access_token` are not kept (good: never store the token).
- Card text: `fe/entities/DefinitionCard.tsx:103-121` (button, light, cover l.112, text special; else HA state — camera "idle"/"streaming"); `cardStateText` l.124.
- Help: `fe/entities/EntityHelp.tsx:8` (lists nine). Pickers: `fe/entities/NodePickers.tsx:206-256`. Class editor: `fe/entities/ClassDefinitionEditor.tsx:236,289` (`entityTypeLabel`).
- Graph: §4 (type colour/label), §5 (node summary, palette).
- Tests: `ft/EntityDefinitionView.test.tsx`, `ft/entity-roots.test.ts`, `ft/EntitiesListView.test.tsx`, `ft/entity-node-config.test.tsx`, `ft/widgets.test.tsx` mention `cover` (models for a camera case); `ft/entity-live-state.test.ts` for kept attributes.

## 10. Packaging and size

- `scripts/package_release.py` (89 l.): ships `*.py`, `*.json`, `out/`, `www/`, translations, brand l.43-45; keeps `runtimes/linux*` only l.21-30; refuses > 100 MiB unpacked l.61-62, > 50,000 files l.63, zip > 100 MiB l.74-76.
- `tests/test_release_package.py` (121 l., 5 tests): unpacked limit l.77-83; linux runtimes l.85-110. No assertion on the size of a real build.
- `HassSharp/HassSharp.csproj` (27 l.): Roslyn 5.12.0 ×2 l.16-17, EF Core Sqlite 11.0.0 l.18, pythonnet 3.1.0 l.20; nullable warnings are errors l.7; XML docs l.11.
- Measured locally (read-only, no build): `out/` 85 MB, `out/runtimes` 52 MB (31 RID folders, `linux*` ≈ 20 MB); packager rules over the local tree give 410 files, 138.1 MiB unpacked (out 51.9 MiB, www 85.9 MiB) — **overstated**: vite `outDir: '../www'` (`frontend/vite.config.ts:22`) is outside root and not emptied, so stale hashed chunks pile up (three 5 MB `editor.api-*.js`). CI builds clean (`.github/workflows/release.yaml:27-45`). `out`/`www` gitignored (`.gitignore:4,6` in the integration).
- Headroom for ImageSharp (size measured by the separate spike): shipped `out` ≈ 52 MiB of the 100 MiB.

## 11. Tests and tooling

- Runtime suites: `tr/Program.cs:8-33` finds `[RuntimeSuite("--flag", Order=n)]` methods; an unknown flag fails l.15-17. Entity suites: `--entity-domains`, `--entity-domains-embedded`, `--entity-graph`, `--entity-graph-runtime`, `--entity-host`, `--entity-api`, `--entity-startup`, `--entity-tools` (`tr/EntityToolsTests.cs:23`, `LoopbackServer` l.28), `--sharp-entities`, `--generated-source-pins`, `--reload-memory`, `--python-net-filter`.
- Fakes to extend: `tr/EntityTestHome.cs:14-42` (delegates), `tr/SharpEntityTests.cs:16-31` (`RecordingHost`), `tr/EntityGraphRuntimeTests.cs:21-40` (`FakeDefinition : IDefinitionRuntime` — gains any frame member), `tr/ServiceCallTests.cs:158-159,350` (stub `CallService*`/`Entity` — model for stubbing `BeginCameraSnapshot`).
- Graph rig: `tr/EntityGraphTests.cs:182-193` `Rig.Start`, commands l.385-392 (`rig.Api.EntityCommand`).
- Leak test: `tr/ReloadMemoryTests.cs` (420 l.) — definition with a 20 ms timer, a track, `Http` l.293-298; `BoundMiB = 0.5` l.46, check l.171. Frames retained by a scope/trace show here if a camera definition is added to a generation.
- Python.NET smoke: `tests/packaging/entity_bridge_smoke.py` (459 l.) — "arrive in Python as str" (docstring l.1-17); four entity delegates l.195-198; class `SmokePlug` l.39-58; graph light/switch; `manager.EntityCommand` l.342. The only place real `byte[]` ↔ `bytes` crossing can be proven (CI `.github/workflows/tests.yaml:56-62`).
- Python: `tp/test_entity_platforms.py` (724 l.; skipped without HA, l.117), `tp/test_entity_bridge.py` (1004 l.; fakes l.22-262, `DotNetTask` returns nothing — `tp/test_entity_platforms.py:43-57` `GetResult` returns `None`; a frame needs a value-returning fake), `tp/test_entity_contract.py` (77 l.).
- Authoring: `authoring_examples.json` (805 l.) every example previews clean (`tr/AuthoringAdapterTests.cs:18`); `llm.py:269-290` node schema knows only `dynamicOutputs=="trigger"` l.278 (P4:327).

## 12. Leftovers from phase 4 (P4:316-321)

- **`Located` slices without checking**: `EL:83-90`, l.88 `document.SourceMap.FirstOrDefault(…)?.NodeId?["$library:".Length..]`. A source-map entry for a graph step whose id is shorter than 9 characters throws `ArgumentOutOfRangeException`; one longer but not prefixed yields a garbage name. Callers: `EL:75` (`DefinitionLoadFailure`, via `ReportLoadFailure` l.53) and `EA:216` (`RecordDefinitionLoadError`), which `EL:48` calls **inside** the `catch` at l.45-50 — a throw there escapes the `foreach` over documents (l.26-56) and stops Init. Reachable only by a compile error the preview did not catch. Fix: `NodeId is { } id && id.StartsWith("$library:", StringComparison.Ordinal) ? id["$library:".Length..] : null`.
- **Startup warning uncapped**: `EA:216` stores `string.Join("\n", compilation.Errors.Select(e => Located(document, e)))` — every error; `EL:49` passes it to `WarnCannotStart` (`EA:173-189`), which logs it whole (`"Entity definition {name} cannot start: {error}"` l.185-186). Graph problems are joined uncapped the same way (`EA:224`). At `9ac2433` the same path went through `ReportLoadFailure(…DefinitionLoadFailure…)` (`git show 9ac2433:…/EntityLifecycle.cs:46,79`), which `Listed` the errors: first `LoggedProblems = 3` (`HassSharp/Manager.cs:121`), each `Capped` at `LoggedProblemLength = 200` (l.124, l.183-189), then `"; and N more"` (l.192-193). Pinned for automations by `tr/RequestFieldTests.cs:108`; entity wording pinned by `tr/EntityStartupTests.cs:111,126,204,557` (single reasons) — a many-errors case is missing.

## 13. Open questions for the planner

1. **Is `frame` a catalog command or its own thing?** The design puts it under `entity_command` (design:198-206), but then `EDT:49` needs `frame` in `SharpCamera.Commands`, `EDT:87-91` needs `OnFrame` to end in exactly `Task` (a returning `Func<…,Task<SharpImage?>>` fails), `tp/test_entity_contract.py:58-64` needs `camera.py` to send `self.command("frame", …)` (which returns `None`), `fx:33759` changes, and `VGV:674-678` warns for a camera graph without a frame flow. Option: a row field (e.g. `frame: {…}` or `returns: "image"`) that the tests and the command list skip.
2. **How a graph frame flow hands back its image.** `RunFlow`/`RunOutcome`/`IDefinitionRuntime` carry no value (§2). A "Show frame" step (`IDefinitionRuntime.SetFrame(entityKey, SharpImage)`) the host reads after `RunToEnd`, or a flow output port? And does the host cache the last frame so a slow flow does not stall HA's 0.5 s pulls?
3. **Run history and queueing for frame flows.** `entity_command` is Queued (`V/VisualGenerator.cs:42`) with `trackHistory` default true; at 2 pulls/s that floods history (design:67) and queues runs a disconnected viewer no longer wants. Default `trackHistory` false for `frame`, mode Single/drop, and a timeout below HA's 10 s instead of 30 s (`DHG:13`)?
4. **Where bytes cross and who copies.** Out: `CameraImage` as `Task<byte[]?>` through `dotnet_tasks` (value returned, `bytes(arr)` cheap) vs a callback delegate. In: `BeginCameraSnapshot` must build `System.Byte[]` by `memoryview` (spike rule 16), not implicit conversion. Is `None` → `int?` accepted by Python.NET 3.1.0 for width/height (not in the spike)?
5. **Cancellation.** HA cancels `async_camera_image` when a viewer leaves; `dotnet_tasks.py:23-55` never cancels the .NET task. Pass a token (a `CancelCameraImage(id)` call) or let frames finish and drop them?
6. **Image values in scopes and traces.** `SharpImage` as `IVisualSnapshot` vs immutable (`RT:24`)? Trace text (`TJ:337-348`) — size only, or a thumbnail? Never into state, logs, history JSON or `localStorage`; who disposes the pixel buffer when a run ends (scopes keep outputs until the run's end)?
7. **What `image` means in an automation.** design:259 wants `camera_snapshot` and the image steps there, but no automation step consumes an image yet (no "send to notify", no file write). Ship them entity-only (`documents:["entity"]`, `UnpinnableKinds`) or add a consumer? Does `http_request` gain `responseType: image` (P4:316-321 says fetching image steps reuse its handler and rules)?
8. **Type listing.** `listed:false` for `image` breaks `ft/visual-contract.test.ts:216` and changes the "all types" widget tests (§4); update them or leave `image` listed but without `literalKind`?
9. **Camera options and card.** Which options (frame interval ≥ 0.5 s, still-only)? Card text is HA's "idle"/"streaming" (`DefinitionCard.tsx:103-121`); show a thumbnail through `camera_proxy` (needs the access token the live state drops, `liveState.ts:24-29`) or nothing?
10. **Size.** No test asserts a real build's size; add one (release workflow or `test_release_package.py`) once ImageSharp's size is known? Clean `www/` before local measurements (vite `outDir` outside root).
11. **Licence.** ImageSharp's licence must be asked about and a LICENSE added before the dependency lands (design:38-39, P4:316-321) — is that a gate for the first commit of phase 5 or for release?
12. **Leftovers' scope.** Fix `Located` and cap `WarnCannotStart` (via `Listed`, per line, or a count) in phase 5's first task, and cap graph problems at `EA:224` the same way?

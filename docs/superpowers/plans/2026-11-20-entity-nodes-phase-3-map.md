# Phase 3 (Devices as nodes): code map

Read from the worktree at `f7a6a07` (2026-11-20) while another agent deletes `entity_state`, `entity_command`,
`camera_frame`: line numbers in `visual_node_catalog.json`, `VisualGraphValidator.cs`, `contract.ts`, `VisualRuntime.cs`,
`DefinitionGraph.cs`, `EntityGraphTests.cs` are approximate (`~`). `CC/` = `custom_components/hass_sharp/`, `FE/` =
`CC/frontend/`. Spec: `docs/superpowers/specs/2026-11-19-entity-nodes-design.md`. Greps used are named where they matter.

## 1. The Entity kind as the template for Device

**Catalog entry** `CC/visual_node_catalog.json` `nodes.entity` (~l.1695-1795; `nodes` is an object, palette order is its
key order, and `entity` currently sits after `entity_field`, before `entity_state`): `label "Entity"`, `category "Entity"`,
`description`, `root: true`, `declares: "entity"`, `documents: ["entity"]`, `minGraphVersion: 5`,
`argumentStorage: "arguments"`, `dynamicInputs/dynamicOutputs/dynamicExecution: "declared"`, `config {key, domain
(required, default binary_sensor), name, icon, deviceClass, entityCategory (enum config|diagnostic), enabledByDefault
(default true), options {}, history {}, arguments {}}`, `inputs: {}`, `outputs: {}`, `execution: []`,
`runtimeVersions [5]`, `keywords [...]`. No `declaration` flag exists anywhere yet (grep `declaration` in Visual/ and
contract.ts: only `loopDeclaration`). No `structural` key on any type or input (python walk of the catalog).
- Every key read on node specs: `execution, dynamicOutputs, category, inputs, keywords, singleton, scopePorts,
  dynamicInputs, documents, dynamicExecution, minGraphVersion, config, outputs, root, runtimeVersions,
  requiredExecution, declares, label, argumentStorage, pure, description, comparison`.
- `entityDomains[d].state` keys today: is_on, value, option, brightness, is_closed, position. None is `device`, so a
  static `inputs.device` on the hub does not collide with a state pin (state pins are added by `dataInputs` and would
  overwrite a same-named static input silently).

**Types** (`types` section): boolean, number, string, object, array, entity_id, target, image (`listed: false`),
`global::HassSharp.EntityStateSnapshot`, and the `global::System.*` CLR ids. `image` is the precedent for a non-CLR-listed
built-in: label in `VisualGraphValidator.Words.BuiltIn` (~l.53-57) and `FE/src/visual/typeLabels.ts` l.5, colour
`typeColor` (`typeLabels.ts` l.124-128) + `--hs-port-image` (`FE/src/styles/tokens.css` l.38), and
`VisualCatalogSnapshot.Unlisted` (`CC/HassSharp/Visual/VisualCatalogSnapshot.cs` l.138) / `NodeLibraryCompiler.cs`
l.229-241 (a type whose `clrType` resolves to no symbol is skipped: `device` will have none, so Roslyn never compares it;
`isAssignable("device","device")` is true by `source === target` in both languages once the type has an entry).

**`$any` and "all types" expansion** (every place a new type silently joins input type lists):
- C# `VisualNodeCatalog.DataInputs` (`CC/HassSharp/Visual/VisualNodeCatalog.cs` ~l.476-540): `$any` → every
  `types` key (l.485); `$properties` (l.486); `var all = types keys` (l.500) used for `fields` (l.504), unknown service
  fields (l.511), `items` (l.518-520).
- TS `dataInputs` (`FE/src/visual/contract.ts` ~l.710-745): `$any` → `Object.keys(catalog.types)` (l.712); `types`
  (l.714) for `fields`, unknown service fields, `items`.
- So a structural `device` type must be filtered at all four spots in each language, not only at `$any`, or a Device
  wire into a service field / list item / Object create field is accepted and then evaluated at run time.
- `listedTypes` (`contract.ts` l.153) filters `listed: false` (Constant/Convert/loops). C# config `enumSource: "types"`
  accepts any known id ("must be a known type"), so an MCP-written Constant of type `device` passes the validator.

**Declared entities** (C# `VisualNodeCatalog.cs`): `Declares(spec, what)` l.44 (`Text(spec,"declares") == what`),
`EntityDescription(node)` l.49-55 (config minus `history`, `arguments`), `DeclaredEntities(graph, catalog)` l.60-64 →
`(NodeId, Entity)` in node order, `GraphDocument` l.69 (`definition` is an object ⇒ `entity`), `DefinitionItems`
l.75-82, `DeclaredDomain` l.97, `DeclaredExecution` l.103-108, `WithGraphTypes` l.200-234 (copies `graph.definition`
into `merged.definition` with `entities = DeclaredEntities(...)`; returns the catalog unchanged for an over-limit graph,
so derived lists are empty then). There is no C# `DerivedEntities`; the name is a test table
(`tests/Runtime/AuthoringAdapterTests.cs` l.148). TS mirrors (`contract.ts`): `entityDescription` l.236,
`declaredEntities` l.244-248 (filter `declares === 'entity'`), `definitionEntityKeys` l.253, `freeKey` l.258 (40-char
entity-key rule baked in), `entityKey` l.275, `derivedEntities` l.282, `definitionCatalog` l.289-292,
`declaredDomain` l.304, `declaredExecution` l.310, `withGraphTypes` l.478. Types: `GraphDefinition {settings; device?:
unknown}` l.17, `CatalogDefinition` l.22, `NodeSpec.declares?: 'entity'` l.56, `InputSpec` l.36 (has `wireOnly`,
`connectable`; no `structural`), `TypeSpec.listed` l.60.
- Call sites of `Declares(`/`declares === 'entity'` (grep): C# `VisualGenerator.cs` ~l.348; validator l.94, ~704, ~715,
  ~735, ~748, ~792, ~993; `EntityApi.cs` l.83; `DefinitionDocument.cs` l.36; `DefinitionGraph.cs` ~l.87. TS
  `contract.ts` l.202-ish (executionLabel uses `dynamicExecution`), l.835 (`entityNodeName`), l.1516, l.1713, l.1730,
  l.1771, l.1827, l.1864-1867 (`addGraphNode`); `connectionIndex.ts` l.103; `clipboard.ts` l.196-200; `NodeConfig.tsx`
  l.83; `entities/refit.ts` (retypeNode, `declares !== 'entity'`); `components/TriggerSummary.tsx` l.26.
  `catalogPalette`'s label rule `spec.dynamicInputs === 'custom' || spec.declares ? {} : {label}` (l.1867) is truthy
  for any `declares`, so a Device node gets no `label` — its title must come from `entityNodeName`-like code.

**Palette** `FE/src/visual/graph.ts`: `paletteOrder = {entity: ['Entity']}` l.47; `catalogPalette` l.53-60 filters by
`documents`, stable-sorts by category rank: within Entity the order is the catalog object's key order. To list Device
second, insert `device` right after `entity` in the JSON (or sort by an explicit kind order).

**`addGraphNode`** (`contract.ts` ~l.1850-1869): config from `spec.config[*].default`; input defaults go under
`config.arguments` for `argumentStorage`; `named = spec.declares === 'entity' ? ownEntity(graph, domain) : prefilled(...)`;
`ownEntity` l.1871 → `{name: entityTypeLabel(domain), key: freeKey(entityKey(name), definitionEntityKeys(graph))}`.
Device needs its own default (`name: "Device"`? key `device`, `device_2`… over device keys only).

**Titles**: `nodeTitle` (`FE/src/visual/flow/flowModel.ts` l.179-180) = `entityNodeName || label || signature title ||
kind label`; `entityNodeName` (`contract.ts` ~l.830-840) returns undefined unless `declares === 'entity'`. C#
`Words.StepName` l.93-97 / `Words.EntityName` l.107-110 same rule (name, else key, else kind label). Both need a device
branch so messages and cards say the device's name.

**Inspector**: `FE/src/visual/NodeConfig.tsx`: `kindConfig` l.33-37 hides keys a `stepPickers` section edits;
`declares()` l.83 (`=== 'entity'`); `retitle` l.99-107 (declaring kind: writes `config.name`, drops `label`; coalesce
`name:<id>`); `commitTitle` l.112-116 trims; header input l.258-261 (`aria-label` "Entity name"/"Node label",
`data-input-id="name"`, placeholder "Not set"); picker section l.319; generic Inputs section l.324-329 lists every
data input (the hub's `device` input would appear here as a "wire" row unless filtered). `FE/src/entities/NodePickers.tsx`
`stepPickers` l.206-216 (`entity: {keys: [key, domain, name, icon, deviceClass, entityCategory, enabledByDefault,
options, history, arguments], Section: EntityNodeSection}`). `FE/src/entities/EntityNodeSection.tsx` l.31-146: Key
(read-only once saved: `ctx.saved().keys`), Type (`retypeNode`), Device class, Icon, Category, Enabled by default,
options, Run history, entity id + Show. A `DeviceNodeSection` would hold key, manufacturer, model, swVersion,
hwVersion, configurationUrl, suggestedArea and Part of; `ctx.saved` today carries entity keys/ids only.

**Card** (`flowModel.ts` `buildNodeView` l.221-329; `FE/src/visual/flow/NodeCard.tsx` header l.390-453): the header
renders exactly two pins: `execIn` (left, l.391; absent for roots and value nodes, l.252) and `next` (right, l.452,
`headerNext` l.256). No data pin is ever in a header. Rows pair `inputs[i]` with `right[i]` (l.312); `right` puts
outputs under their exec pin by `scope === port || group === port` (l.306-311). A hub is a root, so its left header slot
is free: a header data pin needs a new `NodeView` field (e.g. `headerInput?: InputView`, `NodeView` l.84-109), its
removal from `rows`, rendering in `NodeCard` header, `viewKey` (l.~335) coverage, and handle id `data:device`
(`dataHandle` l.41). `unreachable()` l.216-219 marks every non-root non-pure node not in `analysis.scopes` muted: a
Device card would be drawn unreachable. A Device card has no exec pins only if `executionPorts` returns [] and `execIn`
is suppressed (today suppressed only for `isRoot || pure`).

**Clipboard** `FE/src/visual/clipboard.ts`: `copySteps` l.26-36 keeps only edges with both ends copied; `pasteSteps`
l.135; `entityKeys` l.209-220 gives each pasted `declares==='entity'` node `freeKey(key, definitionEntityKeys)`;
`rekey` l.225-231 makes steps naming a copied key follow. Device needs the same over device keys
(`definition.duplicateDevice`), and a pasted device's `via: {device: <copied key>}` should follow its renamed parent.

**Delete**: `EditorAdapter.removal?(graph, ids, catalog)` (`FE/src/visual/adapter.ts` l.226; dialog in
`VisualEditor.tsx` l.403-412, l.699-707); entity adapter `removal: entityRemoval` (`FE/src/entities/adapter.ts` l.126);
`entityRemoval` (`definitionEdits.ts` l.57-72) asks only when steps still name a deleted entity's key. Device deletion
leaves its entities standalone; nameless ones then get `entity.name`, and `via: {device}` on other devices becomes
`device.viaUnknown` — whether to ask is open.

**refit** `FE/src/entities/refit.ts`: `retypeNode` l.19-30 (only `declares === 'entity'`), `refit` l.40-101 drops
values/wires of inputs the new type lacks by comparing `dataInputs` before/after: a static `inputs.device` survives a
retype; a structural input excluded from `dataInputs` would instead be *removed* by refit — keep it in `dataInputs`
and skip it where state is meant.

## 2. Where a non-step, non-value kind trips existing checks

A `device` node is neither `root`, `pure` nor a library/custom method. Today's behaviour, unchanged, would be:
- **Reachability** C# `Run` ~l.617-620 / TS l.1346-1347: not pure, not in `scopes` ⇒ `node.unreachable` error
  "<name> is not connected into the flow…". `pure.unused` only for pure. `declaration: true` must skip both.
- **Execution wires** C# l.591-607 / TS ~l.1300-1312: only `Pure` ends get `flow.pure`; a wire *out of* a device gets
  `port.unknownExecution` (no ports); a wire *into* a device is accepted (not root) and **puts the device in a scope**
  (`Scope` l.822), silencing unreachable. `canConnect` (`graph.ts` l.156-181) refuses only root/pure targets, and the
  card draws an `in` handle ⇒ the editor lets you wire a step into a Device. Declarations need: no `in` pin,
  `canConnect` false, validator error for exec wires touching them.
- **Roots / `root.incoming` / `graph.noTrigger`**: noTrigger only for automations (C# l.615, TS l.1342); fine.
- **Unknown kind** (C# l.576, TS l.1283): only for kinds missing from the catalog; `node.document` (C# ~l.966) refuses
  `documents: ["entity"]` kinds in automations — Device gets that for free.
- **Data-edge type checks** C# l.621-650 / TS after l.1350: output type vs `expected.Any(CompatibleTypes)`; `scope.data`
  only when both ends are in `scopes` (device never is) — fine. `maxSourcesPerInput: 1` (`edgeKinds.data`) gives "one
  device per entity" via `port.inputTaken`; `maxConsumersPerOutput: null` allows one Device → many entities.
- **Hub rules that would misfire on a `device` input** (all read every data input of the hub):
  - `entity.partialState` C# ~l.1004-1007 / TS l.1532-1537: `given` includes `device` when wired; the loop then demands
    every required state value (device wired, no state ⇒ errors), and an unwired `device` is itself reported missing
    (`Bool(state["device"], "required", true)` defaults true).
  - `scope.stateSource` (`EntityScopes` ~l.733-737 / TS `entityScopes` l.1768-1776, `StateSource` ~l.764 / `stateSource`
    l.1802): the source of the wire into `device` is not pure ⇒ returned as a "step" ⇒ a `scope.` error, which is
    **blocking** (`blockingDiagnostic` l.1153), so `connectData` and the connection index refuse the device wire.
  - `state.dynamicSource` (C# ~l.703-708 / TS l.1713): a hub counts as a State when *any* input is `Supplied`; a
    device-only hub becomes a State and its upstream is walked (stops at the device: harmless but wrong).
  - `entity.no_value`, `Supplied` (C# l.805 / TS l.1374) read `config.arguments.<port>` for argument kinds.
- **`connectData`** (`contract.ts` l.1890-1918): `setInput(node, 'device', {kind:'unset'})` writes
  `config.arguments.device = {kind: 'unset'}` (also `disconnectData`); the unknown-argument check (C# ~l.1043-1044)
  passes only while `device` is in `DataInputs`. Refusals ignore `cycle.*`, `pure.unused`, `state.dynamicSource`,
  `entity.partialState`; any new diagnostic on the target input refuses.
- **Connection index** `FE/src/visual/flow/connectionIndex.ts`: `hubs` l.103, `reworked = {entity.partialState,
  scope.stateSource, scope.flow}` l.104, fast `verdict` l.150-240 re-runs `entityScopes` with the candidate edge
  (l.215-219) ⇒ same `scope.stateSource` refusal; `storageIsPrivate` l.313-325 (argument kinds). It never recomputes
  graph-wide rules (`entity.name`, device rules) — fine since a wire only removes those.
- **Limits** `limits {nodes 256, edges 512}`; `OverLimit` makes `WithGraphTypes`/`DefinitionGraph.Entities` return
  nothing, so derived `devices[]` from an over-limit graph would be empty too (the preview is invalid anyway).
- **Generator** `CC/HassSharp/Visual/VisualGenerator.cs`: roots ~l.347-349 (`IsRoot` + `HubFlows`); the dispatch switch
  ~l.404-445 emits a `case` only for nodes with a library/custom method or a compiled conversion (`continue` at
  ~l.407); built-ins are walked by the runtime. A device node is silently skipped; it still sits in the embedded graph
  JSON. `default: throw "Unknown compiled node"` ~l.445 is reached only if the runtime asks for an un-cased node.
  `EmbeddedCatalog` l.113-145 keeps used node specs and the types whose id appears as **any string** in the graph or
  the used specs (`Strings(graph)` l.124).
- **Runtime** `CC/HassSharp/Visual/VisualRuntime.cs`: constructor l.172-193 records every data edge and registers only
  **pure** sources as `pureInputs` (l.187-191) — the old map's claim "every data-edge source" is wrong; a non-pure
  device is not prepared there. But `EntityValues` (~l.891-902) iterates `InputSpecs(id)` and reads every supplied
  input with `Input(scope,id,port)`: a wired `device` would be read (no value: throws or null) and passed to
  `SetState`/`EntityStateValue`. `Walk` entity branch ~l.643-650; `EntityKinds` ~l.860; `default:` ~l.696/804 calls
  `custom()` (generated switch) for any unhandled kind.
- **State flows** `CC/HassSharp/Devices/DefinitionGraph.cs` `HubFlows` ~l.118-143: a state flow exists when any
  `DataInputs` key is wired or has a non-unset argument (~l.132-134). A device wire alone ⇒ a "state" flow ⇒
  `DefinitionRoots.StateEntities` ⇒ `stateKeys` (`DefinitionHost.Graph.cs` l.34) ⇒ the entity is `awaiting`, not
  `Restorable` (`DefinitionHost.cs` l.704), not carried on hot replace. TS `TriggerSummary.tsx` l.35 lists `state`
  for every hub regardless.
- **`StateDependencies.Of`** (`Visual/StateDependencies.cs` l.25-38) via `ValueUpstream` (`VisualNodeCatalog.cs`
  ~l.239; TS `valueUpstream`): walks data wires into the hub, stops at a non-pure source — a device adds nothing, but
  "skipped by state-dependency analysis" should be explicit.
- **Layout** `FE/src/visual/layout.ts` `tidyLayout` l.40: union-find over *all* edges (l.~97-100) joins every entity on
  one Device into one band; a node with only data wires is placed in the column before its first reader (like a value).
- **Type colour/label**: unknown non-entity type ⇒ hashed hue and `Object`-ish label; add `device` to both label maps.

## 3. The device today, end to end

**Records** `CC/HassSharp/Devices/EntityDefinition.cs`: `record DefinitionVia(string? Definition, string? DeviceId)` l.12;
`record DefinitionDevice(string? Manufacturer, string? Model, string? SwVersion, string? HwVersion, string?
ConfigurationUrl, string? SuggestedArea, DefinitionVia? Via)` l.22-23 (no name: the definition's name is the device's);
`record DefinitionLogic(string Kind, string? Type, JsonObject? Graph)` l.58; `record DefinitionBody(string Name,
DefinitionDevice? Device, List<DefinitionSetting>? Settings, JsonArray? Entities, DefinitionLogic Logic)` l.68-69;
`record EntityDefinitionDocument(int Version, string Id, long Revision, string Name, DefinitionDevice? Device,
List<DefinitionSetting> Settings, JsonArray? Entities, DefinitionLogic Logic, string Source, List<VisualSourceMap>
SourceMap)` l.76-96 (`Body` l.88 drops Entities for graphs). Nothing validates device fields (grep
`ConfigurationUrl`: only records, `SharpDevice`, `DeviceJson`); HA refuses a bad `configuration_url` at sync.

**Store** (same file): `Version = 2` l.129-130; `EarlierBuild` l.135 = "saved by an earlier development build; delete it
and build it again"; `Complete` l.138-139 accepts `Version == 2` or `1 && class`; `Read` l.143-155 throws `EarlierBuild`
only for `{Version: 1, Logic.Kind: "graph"}` (l.150) — a v2 graph document after the bump would get "Invalid entity
definition envelope" instead unless this test becomes `Version < 3 && graph`. `Save` l.221-260: `Decompose` +
`FillMissingLayout`, `generator.Preview`, entities derived `DefinitionGraph.Entities(preview.Graph!, catalog)` (l.252),
document written with `Version` (l.253) — class documents are re-saved at the current version too. `Rebuilt`
(l.285-293, library rebuilds) keeps `old.Entities` (not re-derived) — `devices[]` would need the same treatment
or re-derivation. `RefreshDerived` keeps the body. Request parsing `EntityApi.DefinitionBody(request)` l.280-289:
`Deserialize<DefinitionBody>`, `entities` presence by `ContainsKey` (l.284); unknown members (e.g. `devices`) are
ignored silently.

**Preview** `DefinitionGenerator.Preview` l.46-80: `definition.entitiesNotAllowed` for graph + entities (l.57-62, returns
early) — the pattern for refusing a graph request's `device`. Note the panel and MCP read answers send `device: null`
today, so refusing only a non-null device keeps them working. `DefinitionDocument.Check` l.34-56 (graph present, at
least one Entity node `definition.entitiesRequired`, options, settings).

**Compose / marker** `DefinitionGraph.Compose` l.46-56: `definition = {settings: [{key,type}]}` plus
`definition.device = {}` when `body.Device != null` (l.53). Readers of the marker (grep `"device"` in Visual/ and
`hasDevice`): C# validator field `device` set in `Definition()` ~l.667, used only by `entity.name` ~l.1003; TS
`hasDevice` l.1274/l.1675, used only at l.1530. `entity.name` fires when the name key is absent too (`Typed` returns ""
for a missing key), so every nameless hub relies on the marker today.

**Compile / host** `EntityApi.CompileDefinition` l.64-100: class ⇒ `new CompiledDefinition(id, name, document.Device,
settings)` (l.72); graph ⇒ re-`Compose(document.Body)`, `Roots`, triggers per `HubFlows`, `Definition = new(id, name,
document.Device, settings) { Entities = DefinitionGraph.Entities(graph, snapshot), Roots = roots }` (l.97-98) —
entities are re-derived from the graph, not read from the stored list. `record CompiledDefinition(string Id, string
Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings) { Factory; Entities; Roots }`
(`DefinitionHost.cs` l.15-23). Payload `DefinitionHost.PrepareAsync` l.130-148:
```
{ "definitionId": definition.Id, "name": definition.Name, "device": DeviceJson(),
  "entities": all.Select(e => e.Describe(UniqueId(e))) }
```
`SharpEntity.Describe` (`SharpEntity.cs` l.181-193): `{uniqueId, key, domain, name, icon, deviceClass, entityCategory,
enabledByDefault, commands, options}` — no device; graph entities come from `SharpDomains.Create(domain, description)`
(`PrepareGraph`, `DefinitionHost.Graph.cs` l.53-107; extra members of the description are ignored). `UniqueId` l.878 =
`<id>:<key>`. `DeviceJson()` l.900-920: null when neither a class `SharpDevice` nor a document device; else `{name:
definition.Name, manufacturer…suggestedArea: document ?? class, via: {definition}|{deviceId}|null}` (via only from the
document). Class path `PrepareClass` l.151-206: root is a `SharpDevice` (entities = `device.Entities`) or one
`SharpEntity` (key `main`); `CheckKeys` l.888-897. `SharpDevice.cs` l.9-52: Manufacturer, Model, SwVersion, HwVersion,
ConfigurationUrl, SuggestedArea (no name, no via). A class `SharpEntity` with no document device syncs `device: null` and
relies on the Python name fallback.

**Lifecycle** `ForgetRetyped` (`EntityApi.cs` l.315-322) forgets stored states of keys whose domain changed, by
`<id>:<key>`; delete (l.466-487) forgets `<id>:` states, then `PyInterop.RemoveEntitiesAsync(id)`; `PruneEntities`
(`EntityLifecycle.cs` l.101-127) sends all file ids and forgets states whose `unique[..first ':']` is unknown;
`EntityDirectory.TryGet` splits on the first colon. Nothing keys state, history, handover or camera frames by device
(grep `device` in `DefinitionHost.Graph.cs`, `EntityStateStore.cs`, `CameraFrames.cs`, `EntityDirectory.cs`).

**Bridge** `CC/entity_bridge.py`: `DEVICE_FIELDS` l.42-43 (name, manufacturer, model, swVersion, hwVersion,
configurationUrl ⇒ HA keywords); `_async_sync` l.342-382: refuse unknown platforms/refusals, remove entries whose
unique id comes back under another domain, `device_info, device_id = self._sync_device(..., definition.get("device"))`
(l.368), each entity `_async_sync_entity(entities, description, device_info, device_id, definition["name"])`, then
removes entries no longer described (only after success). `_sync_device` l.384-410: identifiers
`{(DOMAIN, definition_id)}`; `device is None` ⇒ move this definition's registry entries off the existing device, then
`async_remove_device` (order matters: removing a device removes its entities, spike rule 9); else
`async_get_or_create(config_entry_id, identifiers, name…, suggested_area, via_device_id)` and clear nulls with
`async_update_device`. `_via` l.412-423: `{definition}` ⇒ lookup `(DOMAIN, via.definition)`; `{deviceId}` ⇒
`devices.async_get`; unknown parent ⇒ None (HA refuses unknown `via_device_id`). `_async_sync_entity` l.425-467: live
entity ⇒ `apply`, write, `async_update_entity(..., device_id=...)` when it differs (the move, l.443-448); new ⇒
`factory(self, description, device_info, definition_name)` and `platform.async_add_entities` — HA (venv 2025.12
`entity_platform.py` ~l.895-903 → `entity_registry.async_get_or_create` → `async_update_entity(device_id=…)`) sets an
existing entry's `device_id` from `device_info` (None when standalone), so a not-live entity also moves; a
**disabled** entry is never added and never updated. `_async_remove` l.469; `_async_prune` l.473-488: kept ids vs
`found` = entity unique-id prefixes **plus every DOMAIN device identifier taken whole** (l.482-483); `_purge` l.490-499
removes the device `(DOMAIN, definition_id)` then entries `<id>:*`.
`CC/entity_base.py` `apply` l.38-55: `name is None and device is None` ⇒ `definition_name` (the fallback the spec
removes); `_attr_device_info = device`. Every platform override (`light, sensor, cover, number, camera, text, select`.py
l.10-16) passes `(description, device, definition_name)` through.

**MCP / authoring** `CC/llm.py`: `PROMPT` l.20 ("and optionally their device"); `SEMANTICS["definitions"]` l.30 (body
`{name, device, settings, logic}`, "An entity without a device needs a name", the device/via paragraph incl.
`via {definition}`, entity-id naming); `DEFINITION` schema l.101-104 (`device: None|OBJECT`); `STORED_DOCUMENT_KEYS`
l.108-109 (add `devices`); `ENTITY_SPECS` l.161-171 (entities_list `device` boolean l.163, entities_read "send back name,
device, settings and logic" l.164, preview/save shape l.165-166, delete "entities and device" l.168); `class_kinds`
l.315-330; `DEFINITION_PARTS = ("settings","device","logic")` l.366 (also drives `DEFINITION_MARKERS`, `document_kind`);
`entity_tool_hints` `takes` l.404, l.425; `definition_hints` l.456; `graph_hints` l.485-490 (moves a graph's
`settings`/`device` to the top); `definition_summary` l.555-576 (`device` = document device or class kind, l.574);
`node_schema` l.613 adds `entityDomains` for `declares == "entity"`. `CC/authoring.py` has no device code
(`registered_entities` l.34-55 partitions unique ids on the first colon). `CC/authoring_examples.json`
`definitions`: only **Plug over HTTP** has a device (`{model "Smart plug", suggestedArea "Living room", others null}`;
relay + power hubs, Set state/Set available steps); Motion in either room, Camera with a box, File at setup, Plug from a
class have `device: null`.
**Docs**: `docs/custom-entities.md` intro l.6, l.14, "document version 2" l.22, example JSON `"device": null` l.77
(pinned by `tests/test_docs_examples.py` l.55/61), panel text l.142-143 ("Device and settings"), name rule l.179-180,
**## Devices** l.242-262; `docs/mcp-authoring.md` l.55, entities_list/read l.241-256, delete l.280, **### The Entity
node** l.306-314; `docs/backend/entity-definitions.md` intro l.4, **## The definition** l.46-84 (`device` l.80-84),
`version: 2` l.96, **## The document** l.141, removal l.183.

**Frontend** `FE/src/entities/model.ts`: `DefinitionVia {definition?, deviceId?}` l.40; `DefinitionDevice` l.45-54;
`DefinitionBody {name, device?, settings?, logic}` l.58-63; `EntityDocument` l.90-115 (`version: 1 | 2` l.92,
`device` l.95, `entities` l.98); `EditorDefinition {settings, device}` l.149-152; `editorGraph` l.165-172 (puts
`device` into `graph.definition`); `definitionBody` l.198-208 (sends `device`); `newGraphDefinition` l.224-231
(`device: null`); `definitionEntityKinds` l.253. `definitionEdits.ts`: `definitionOf` l.18-21, `withDefinition`
l.22-23 (spreads the old `graph.definition`, so a stale `device` survives edits), Device section l.268-287
(`withDetail`, `withVia`, `updateDevice`, `setDevice`), `problemRow`/`definitionProblems` l.293-311 (only setting
codes; device problems will be on nodes). `DeviceForm.tsx`: fields l.12-19 (Manufacturer, Model, Software version,
Hardware version, Configuration URL `type: url` + hint, Suggested area + hint); `DeviceMemory` l.27-38; `DeviceForm`
l.46-60; `parentSources` l.70-83 (definitions list + class types); `DeviceParent` l.91-153 ("Part of": Nothing /
another definition that is a device (`d.device != null` or class kind device) / "A Home Assistant device…" with
`SearchSelect` over the registry minus devices of `ownEntityIds`); `DeviceSection` l.161-203 (switch "Group these
entities as a device", hint "…named after this entity definition."). No client-side validation of device fields.
`DefinitionPanel.tsx` l.49-77 renders `DeviceForm` (l.72) + `SettingRows`; `DefinitionOverview` l.92-110 passes
`savedDevice`; `liveEntitiesOf` l.80-86 strips `<id>:`. `adapter.ts`: `overviewPeek: 'Device and settings'` l.36,
`deviceMemory()` l.84. Class kind keeps its device until phase 5: `ClassDefinitionEditor.tsx` l.22, l.166, l.272-276
(`DeviceSection`), `classSession.ts` `deviceToSend` l.29-35, `setDevice` l.258. `api.ts`: no device code.
`DefinitionCard.tsx` l.204 "Device" chip on `item.device`; `EntitiesListView.tsx` l.215 ("entities and devices");
`DeleteDefinition.tsx` l.56 ("with their device if it has one"); `EntityHelp.tsx` l.8 ("device and settings… Device
and settings") and l.21 "In Home Assistant" (grouped as a device, Part of another definition's device).

## 4. Tests and fixtures that pin the device

- **`tests/fixtures/visual-graph-validation.json`** (472 cases, read by C# and `FE/tests/visual-contract.test.ts`): 15
  cases carry `definition.device: {}` only to keep nameless hubs valid — "entity graph: state input left unset",
  "code definition.unknownCommand" (3), "code state.duplicate", "code command.duplicate", "code entity.no_value…",
  "code camera.entity…", "code camera.no_frame…", "an Entity node's category/enabled by default/name/icon/device class
  is…" (5), "an entity of a device needs no name"; plus "an entity without a device needs a name". Several of those are
  old-kind cases being deleted now. New cases needed first: device rules, `entity.name` by wire, `$any`/field refusal of
  a device wire, exec wire into/out of a Device, partialState/stateSource not counting `device`.
- **`tests/fixtures/visual-rich-catalog.json`** must DeepEqual the server catalog (`tests/Runtime/NodeLibraryFixture.cs`
  l.209-211); any catalog edit needs `dotnet run --project tests/Runtime -- --write-node-library-fixture`
  (`Program.cs` l.5).
- **Runtime**: `EntityHostTests.cs` `Compiled(id, name, factory, DefinitionDevice? device, …)` l.67-71; `Ordered`
  l.156-168 pins the class sync JSON (`"device":{"name":"Plug one",…}` l.160, mirrored as `PLUG` in the Python test);
  `RootEntity` l.170-188 (`"device":null`, `<id>:main`; document device with `via {deviceId}`); `DocumentDevice` l.192-200
  (`via {definition:"hub"}`). `EntityStoreTests.cs`: bodies l.37/79; `ASaveWritesVersionTwoWithTheDerivedEntities`
  l.217-230; earlier-build tests l.244-268 (`EarlierBuild`, v1 graph); `AnOlderClassDocumentStillLoads` l.282-286;
  `ADeviceMarksTheGraph` l.572-584 (the marker + `entity.name`); `ClassDefinitionFilesAreUnchanged` l.778-808 (byte-exact
  class file at version 2 with `"device":{…}`). `EntityGraphTests.cs`: `Definition(name, graph, settings, device)` helper
  ~l.149-154 (`"device"` always sent, null by default), `Unnamed` ~l.144, sync "has no device" ~l.320, unnamed hub made
  valid by `unnamed["device"] = {}` ~l.448-449, `ADeviceDefinitionGroupsItsEntities` ~l.637-655 (pins `sync["device"]`
  and the entities JSON with a nameless `online`), ~l.1261. `EntityDomainTests.cs` l.135 (graph with `device: {}`).
  `AuthoringAdapterTests.cs` `DerivedEntities` l.148-152 (exact derived entity JSON per example — changes if stored
  entities gain a `device` member) and `ExampleDefinitions`. `EntityToolsTests.cs` `GraphPlug` (~l.600-670).
- **Python**: `tests/python/test_entity_bridge.py` (fakes, no HA): `DeviceRegistry` l.105-150 (`add(definition_id)` makes
  id `device-<id>`, identifier `(DOMAIN, <id>)`; `async_get_or_create` refuses unknown `via_device_id`), helpers
  `device()` ~l.268-271, `description()` l.280, `definition(*entities, definition_id, name, device=device())` l.285-288,
  `PLUG` l.291. Device tests: `test_sync_adds_entities_through_the_platform` l.419 (identifiers `{(DOMAIN,'plug1')}`
  l.424), `test_sync_clears_a_device_field_set_to_null` l.456, `test_sync_names_main_and_standalone_entities` l.469
  (standalone takes the definition's name), `test_sync_moves_an_entity_onto_a_new_device` l.521,
  `test_sync_that_fails_keeps_the_entities_it_dropped` l.546 (device step failure too),
  `test_sync_without_device_removes_the_old_device` l.582, `test_sync_resolves_the_via_device` l.630 (`{definition}`,
  `{deviceId}`, gone), `test_remove_purges_registry_and_device` l.734, `test_prune_keeps_known_definitions_and_purges_the_rest`
  l.750 (a device `empty` with no entities is purged), l.762, l.773. `tests/python/test_entity_platforms.py` (real HA
  2026.11, skips otherwise): `test_device_and_entities_are_registered_with_the_unique_id` (~l.260-275),
  `test_device_field_set_to_null_is_cleared`, `test_via_device_links_and_unlinks` l.780-792, prune/unload/failed-sync
  tests. `tests/python/test_mcp_adapter.py` `test_entities_list_says_whether_a_definition_makes_a_device` l.621 plus the
  hint tests (`DEFINITION_PARTS`). `tests/packaging/entity_bridge_smoke.py` l.393-394 (class sync `device.name ==
  'Smoke plug'`, manufacturer), l.445 (graph save with `device: None`), l.459 (graph sync `device is None`).
- **Frontend** (`FE/tests`): `DefinitionPanel.test.tsx` describe 'the device' l.121-173 (3 tests);
  `entity-model.test.ts` l.19-80 (editorGraph/definitionBody device round trips, `via`), l.114-140 (fixture runner
  injects a full device when the fixture has `device`), l.206 (`newGraphDefinition`); `entity-node.test.ts` l.42-44,
  l.161 (`definition` `{settings: [], device: null}`); `EntityDefinitionView.test.tsx` l.99, l.180, l.247-300 (turns
  the device on, `sent.definition.device`), l.528-535 ("Edit Device and settings"), l.568, l.598-601 (help text),
  l.634-693, l.772, l.1669 (Part of another definition / HA device), l.1807; `EntitiesListView.test.tsx` l.76,
  l.132-170 (Device chip), l.396; `ClassDefinitionEditor.test.tsx` (class device stays; l.1033 seeds a graph `hub` with
  `device: {}` as a parent definition); `editor-shell.test.tsx` l.1002.

## 5. Traps and open questions

1. **Prune deletes every phase-3 device.** `_async_prune` adds each DOMAIN device identifier *whole* to `found`
   (l.482-483); `"plug:main"` is never a kept id ⇒ `_purge("plug:main")` removes that device, and HA removes the entities
   on it (rule 9). Split identifiers on the first colon, and make `_purge` remove every device whose identifier is
   `<id>` or starts with `<id>:`.
2. **Old device left behind.** Phase-2 builds made `(DOMAIN, "<id>")`; nothing in a phase-3 sync looks it up. Sync must,
   after success, move this definition's entries (including disabled ones, which `_async_sync_entity` never touches)
   off every DOMAIN device of the definition that is not described, then remove it.
3. **Device order and via.** `via: {device}` ⇒ identifier `(DOMAIN, "<id>:<key>")`; the parent must be created before
   the child in one sync (topological order; `device.viaCycle` keeps it acyclic) or `_via` drops it (unknown parent ⇒
   None). `via: {definition}` exists today (FE `DeviceParent`, `DeviceJson`, `_via`, docs, `SEMANTICS`, tests) and has no
   meaning once a definition has several devices; for the class shim it could map to `<other id>:main`, or be dropped.
4. **The `device` input as a state pin** (section 2): `entity.partialState`, `scope.stateSource` (blocking ⇒ the wire is
   refused by `connectData` and the index), `state.dynamicSource`, `HubFlows` (a device wire alone creates a state flow
   ⇒ stateKeys ⇒ no restore/handover for that entity), `EntityValues` (reads the device's non-existent output),
   `entity.no_value`, `refit`. Every consumer of a hub's `DataInputs` needs a structural filter, but `refit` must keep it.
5. **Structural type leaks beyond `$any`**: the C# `all`/TS `types` lists for `fields`, unknown service fields and
   `items`; `enumSource: "types"`; `EmbeddedCatalog` pulls a type into **automations** whenever any string in the graph
   equals its id — a native HA device trigger has `"trigger": "device"`, so an id of `device` changes such automations'
   generated source (anti-goal "no automation changes"); a less common id avoids it.
6. **Declarations and execution**: `canConnect` allows an exec wire into a Device, the card draws an `in` handle, and the
   validator then scopes the Device (silencing unreachable); the card's `unreachable()` mutes it. Declarations need
   their own branch in `executionPorts`/`execIn`/`canConnect`/`Scope`/reachability on both sides.
7. **Header pin**: no card has a data pin in its header today (only `execIn`, `next`); rows pair inputs with outputs by
   index, so moving `device` out of `rows` changes every hub's row alignment and `viewKey`. The inspector's generic
   Inputs section would still list the device input.
8. **Names.** `entity.name` treats a missing `name` key as blank; with the marker gone every nameless hub not wired to a
   Device fails (many fixtures and tests rely on `device: {}`). Python's fallback to the definition's name also covers a
   class's root `SharpEntity` with no device (`RootEntity`); with it removed, C# must name that entity. The spec gives
   no rule for a Device with no name, yet "an entity on a device with no name takes the device's name".
9. **Version 3**: `Read` throws `EarlierBuild` only for `Version 1 && graph`; `Complete` accepts 2 — both need
   `< 3`. Class documents at 1 and 2 must still load (phase 5 deletes them). `Rebuilt` does not re-derive `Entities`,
   so `devices[]` must be handled there too; `ClassDefinitionFilesAreUnchanged` pins the class file byte for byte.
10. **Request shape**: the panel (`definitionBody`), MCP read answers (`device: null`) and `EntityGraphTests.Definition`
    all send `device`; refuse only a non-null graph `device` (as `entitiesNotAllowed` does), or every client breaks.
    `devices` in a sent-back read answer is ignored by the server; add it to `STORED_DOCUMENT_KEYS`.
11. **Moving between devices** keeps the registry entry (unique id `<id>:<key>` unchanged): live ⇒
    `async_update_entity(device_id)`, not live ⇒ HA's add updates `device_id`; disabled entries are neither, and would
    be deleted with a removed device unless moved first (trap 2).
12. **Where membership is stored**: payload entities need `device: <key>|null`; whether the stored `entities[]` (and
    `EntityDescription`) gain that member changes `AuthoringAdapterTests.DerivedEntities`, `docs`, `optionProblems`
    inputs and `ForgetRetyped` (harmless). Deriving it needs the data edges, which `DeclaredEntities` does not read.
13. **Class shim**: `CompiledDefinition.Device` and `DeviceJson` (class device + document overrides, named after the
    definition) become `devices: [{key: "main", …}]` with entity `device: "main"`; a class root `SharpEntity` without a
    document device stays standalone (`devices: []`). The panel's class editor keeps `DeviceSection` (and its `via
    {definition}` choice, whose parents list reads `d.device != null` — false for every graph definition after phase 3).
14. **Registry devices in the FE** expose only `{id, name, area_id}` (`CC/utils.py` l.95): a Device node's "Part of a
    Home Assistant device" cannot exclude this definition's own devices by identifier, only via its entities' device ids.

## Facts I could not establish

- Home Assistant 2026.11 itself: the local venv is 2025.12 (`.venv/.../homeassistant`); device-removal and
  `device_id`-on-add behaviour above is from that source and the spike notes (rules 8-9).
- Whether a changed generated source of an existing automation (trap 5) triggers a rebuild or a "not current" state:
  `VisualStore`'s staleness logic was not read.
- Exact post-deletion line numbers in the six files being edited concurrently.

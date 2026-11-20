# Phase 4 code map: C# entity and C# device nodes

Read at HEAD 8c32df8 (phase 3 Task 1, the `device` kind, is done; Tasks 2-9 are not). Another agent is editing phase 3
in this checkout, so line numbers are approximate (`~l.`). `CC/` = `custom_components/hass_sharp/`,
`FE/` = `CC/frontend/`. Sources: the design `docs/superpowers/specs/2026-11-19-entity-nodes-design.md`, the phase 3
plan `docs/superpowers/plans/2026-11-20-entity-nodes-phase-3.md` and its map. "(inferred)" marks what I worked out
but did not see run.

What the design asks for (spec, "C# nodes" and "Decisions (mine)"): `csharp_entity` config `key, source,
signature{title, description, domain, settings[]}, arguments{}`, inputs `device` (structural) plus one per
`[Setting]`; `csharp_device` config `key, name, via, source, signature{…, settings[]}, arguments{}`, inputs one per
`[Setting]`. Both reuse the Custom C# pipeline (approach b). A setting pin takes a typed value or a direct wire from a
Setting node, applied before `Setup()`; a secret setting refuses a typed value (`setting.secretLiteral`); only a
Setting can be wired in (`scope.settingSource`); `csharp.signature` covers the stored signature. A C# device's
entities have unique ids `<definition id>:<device key>:<entity key>` and are not nodes; "A C# device's `entities` is
null until it has run." No automation change; no user class runs on preview. Phase 5 deletes the class kind; until
then (phase 3) a class goes through as the implicit device `main`.

## 1. The Custom C# step, end to end

### 1.1 Catalog (`CC/visual_node_catalog.json` ~l.398-473, kind `custom`)
- `label "Custom C#"`, `category "Code"`, description "A step you write in C#: one static method marked [Node]. Its
  parameters are the inputs and its return value is the output." No `documents` field: offered in automations and
  entity graphs alike. `dynamicInputs: "custom"`, `dynamicOutputs: "custom"`, `argumentStorage: "arguments"`,
  `execution ["next","error"]`.
- `config.source` (required, label "Code"), default `public static class CustomNode { … [Node("Rename this node")]
  public static string RenameThisNode(string text) … }`. `config.signature` (required object, label "Pins"), default
  `{title:"Rename this node", description, pure:false, inputs:{text:{types:["global::System.String"], required:true,
  label:"Text", description, nullable:false, hasDefaultValue:false}}, outputs:{result:{type:"global::System.String",
  label:"Result", description}}, types:{}}`. `config.arguments` (label "Values"), default
  `{text:{kind:"literal",type:"string",value:""}}`.
- `tests/Runtime/CustomNodeTests.cs` `Template()` ~l.59-89 pins that the default signature is what the compiler
  derives from the default source (`VisualGenerator.SameJson`) and is valid against the static catalog. A new kind
  with a default source needs the same pin.

### 1.2 Compile (`CC/HassSharp/Visual/NodeLibraryCompiler.cs`)
- `CustomNamespace(nodeId)` ~l.36-41 gives `Custom_` + the node id, with ASCII letters and digits kept and any other
  character written as `_XXXX` hex.
- `CustomWrapperPrefix(nodeId)` ~l.42 is `"#nullable enable\nnamespace NodeLib.<ns>\n{\n"`. `WrapCustomSource`
  ~l.43 adds the closing brace. `SourceLineOffset = 3` maps diagnostics back to the node's own lines.
- `Preview(libs, customSources)` ~l.95-532 compiles libraries and every custom source in one compilation. Custom
  trees have path `$custom:<id>` (a duplicate id is skipped); a brace check gives "The code must stay inside the
  namespace it is placed in; check its braces"; CS1591/CS1573 are suppressed for custom code.
- Per custom node ~l.480-527: `NodeMethods(tree)` must find exactly one `[Node]` method, else "Mark one method with
  [Node]: it becomes this node" / "Mark only one method with [Node]; this code has {n}". `Describe(..., "custom",
  nodeId, ...)` builds `{title, description, pure, inputs, outputs, types}` plus flows
  `execution/scopePorts/executionLabels`. Own types are private (`OwnTypesArePrivate`): "{type} belongs to the Custom
  C# node {id}; declare the type in this code or in a node library to use it here". The result is a
  `CustomNodeDescriptor(NodeId, Method?, Signature?, Types, Diagnostics){Valid}` (`VisualCatalogSnapshot.cs`
  ~l.40-43) in `snapshot.Custom` (never in Root).
- `EntityTypes(...)` ~l.528 is called with the library `trees` only. **Custom trees are never scanned for
  SharpComponent classes.**

### 1.3 Generator (`CC/HassSharp/Visual/VisualGenerator.cs`)
- `IsCustom` ~l.153 is `dynamicInputs=="custom"`. `CustomSources` ~l.155-157: custom nodes with a string `source` and
  no `code`, as `CustomNodeSource(NodeId, Source)`
  (`VisualCatalogSnapshot.cs` ~l.34).
- `WithCustom` ~l.188-201 keeps a one-entry cache `LastCustom` keyed by SHA256 of (services, libraries, (nodeId,
  source)…), then calls `NodeLibraryCompiler.Preview(snapshot.Sources, sources)`.
- `SamePins` ~l.209-213 compares only what changes wiring: `pure`, each input's `types/required/nullable/
  hasDefaultValue/default`, and each output's `type`. Labels and descriptions are not compared.
- `SameSelected` ~l.220 and `SameJson` ~l.239 are helpers.
- `DeriveSignatures` ~l.252-281 (MCP and authoring) writes `custom.Signature` into each config and drops arguments
  and wires of pins that are gone, warning "Removed the value of {Label}: the code no longer has that input",
  "Removed the connection to/from …", "Removed a step order wire: this node is now a value node, which has no place
  in the run order".
- `Preview` ~l.287-474: compile diagnostics land on InputId `source`; a stored signature differing in pins gives
  `Words.StaleSignature` (code `custom.signature`), one differing only in words is refreshed and returned in `Graph`.
  Dispatch `MethodOf` ~l.385-445 resolves a custom node by id through `snapshot.Custom[id].Method` (method key
  `"custom:"+id`); any other compiled kind is `default: throw "Unknown compiled node"`. The whole graph JSON is
  embedded in the source (`Pieces(graph.ToJsonString())` ~l.401), so every `config.arguments` literal is in it. Each
  custom node's code is appended in `NodeLib.Custom_<id>` ~l.447-451, with a section map ~l.466-468 for diagnostics.
- At run time a custom step is a direct static call to `NodeLib.Custom_<id>.<Class>.<Method>(…)`, with operands from
  `VisualRuntime.OperandOf/FromJson` ~l.480-519 (inferred from MethodOf plus the runtime).

### 1.4 Signature shape and checks
- C# `VisualNodeCatalog.WellFormedSignature` ~l.197-211 requires text `title`/`description`, bool `pure`, objects
  `inputs/outputs/types`, each input's `types` a non-empty string list and `required` a bool, each output's `type` a
  string, pin ids `^[A-Za-z_][A-Za-z0-9_]*$`; `execution/scopePorts/executionLabels` optional.
- `CustomSignature(node, spec)` ~l.189-190 is used only when `dynamicInputs=="custom"` and the signature is well formed.
- TS mirrors: `wellFormedSignature` `FE/src/visual/contract.ts` ~l.449 and `customSignature` ~l.462, both gated on
  `dynamicInputs === 'custom'`.
- What the signature drives: `IsPure` (C# `VisualNodeCatalog.cs` ~l.33; TS `isPure` contract.ts ~l.228-231),
  `ExecutionPorts` ~l.355-368 (custom flows from the signature; `[]` for pure and declaration kinds), `DataOutputs`
  ~l.377-393, `DataInputs` ~l.486-549 (custom inputs ~l.513), `WithGraphTypes` ~l.221-255 (merges the signature's
  `types` into the catalog), `Words.StepName` ~l.91-97 (the custom title).
- The stored signature is what TS trusts, because only C# compiles. When pins differ, C# reports StaleSignature "The
  pins are out of date with the code: open Edit code and apply it". The editor's Apply rewrites the signature. MCP
  and authoring re-derive it (`deriveSignatures: True`).
- Validator words (`CC/HassSharp/Visual/VisualGraphValidator.cs`; TS contract.ts ~l.975-977): `MalformedSignature`
  ~l.128 "The pins of this node cannot be read; open Edit code and apply it", checked ~l.1090 for
  `dynamicInputs=="custom"` (TS ~l.1597); `OldCustomCode` ~l.132, a removed `config.code` (~l.972, TS ~l.1482). The
  codes list ~l.39-42 (TS ~l.1127) has `custom.signature`.

### 1.5 Preview over the websocket and MCP
- `CC/HassSharp/Visual/NodeLibraryApi.cs` `custom_preview` ~l.155-184 takes `nodeId`, `source`, optional `sources`,
  compiles off the gate, answers `{valid, diagnostics, signature}`; with broken libraries it adds "The node library X
  has errors; fix the node library first, then this code can be checked". **No kind field.** `type_definition` with a
  nodeId ~l.145-151; language services for `custom: true` ~l.185-197 (`NodeLibraryLanguageServices.cs` ~l.30-51,
  `CustomDocumentId="$custom"`, wrapped with `CustomWrapperPrefix(nodeId)`).
- Python has no handler of its own: `websocket.py` ~l.70 passes `("node_library",
  envelope_command(authoring.node_library, …))` through.
- MCP has no custom_preview tool. Preview and save send `deriveSignatures: True` (`llm.py` ~l.265, ~l.285;
  `authoring.py` ~l.119-121, ~l.153-155). `EntityApi.DerivedBody` ~l.332-338 and `VisualApi.cs` ~l.105-109 call
  `VisualGenerator.DeriveSignatures`.
- `llm.py` `SEMANTICS["csharp"]` ~l.28 is one paragraph describing kind `custom`. The new kinds need text there and
  in the `definitions` paragraph ~l.30.

### 1.6 Frontend
- `FE/src/visual/customCode.ts`: `exampleSource(kind='custom', catalog)` ~l.11 reads `config.source.default` of the
  given kind (works for any kind). `applyCustomCode(graph, nodeId, source, signature, catalog)` ~l.21-103 writes
  `source` and `signature`, resets arguments keeping those that still fit, drops wires that no longer fit, handles a
  pure/step flip. Notices: "Removed N connection(s) that no longer fit: …", "Cleared … values that no longer fit:
  …", "… is now a value node; …", "It is now a step: connect it into the step order".
- `FE/src/visual/CustomCodeEditor.tsx`: full-screen dialog titled `nodeTitle(node, catalog, 'Custom C#')`, header
  "Custom C# code"; Apply only on a current valid check with a well-formed signature; "Reset to example" uses
  `exampleSource(props.node.kind)`; the `Pins` panel speaks of steps and value nodes; `librariesBroken` and
  `CodeLeftBehind` ("The Custom C# step whose code you were editing was removed…"); `CustomCodeHelp` describes [Node]
  methods only (a `document` prop changes wording).
- `FE/src/components/Editor/CustomCode.tsx` `CustomCodeSurface{nodeId, initial, readOnly, label, onReady, onApply}`:
  Monaco model `customModelUri(nodeId)` and `useEditorActions(hass, {context, custom:{readOnly}})`.
- `editor-actions.ts` ~l.194-213 and ~l.262-293: `api.customPreview(code)` with a 400 ms debounce.
  `library-api.ts` ~l.81-92: `customPreview` sends `{nodeId, source}` only.
- `FE/src/visual/VisualEditor.tsx`: a restored draft checks `node.kind === 'custom'` ~l.104; `editCode` ~l.318;
  `applyCode` ~l.324-329 (applyCustomCode, commit, notice); `<CustomCodeEditor … document={adapter.document}>` ~l.724.
- Other custom-aware sites: `flow/FlowAdapter.tsx` ~l.1031 (hasCode = `dynamicInputs === 'custom'`); `NodeCard.tsx`
  ~l.422/430 (`flow.editCode`); `NodeConfig.tsx` ~l.312-322 ("Edit code" gated on `kind() === 'custom'`, hint "Its
  inputs and outputs come from the code."; generic Inputs ~l.328-333); `contract.ts addGraphNode` ~l.1856 (no `label`
  for `dynamicInputs==='custom' || spec.declares`); `clipboard.ts` `customNamespace` ~l.235 and `customTypeRenames`
  ~l.248-254 (on paste rewrites `NodeLib.Custom_<old>.` to the new id, only for `dynamicInputs==='custom'`).

### 1.7 Fixture and generated-source pins
- `tests/fixtures/visual-graph-validation.json` (~1217 cases; read by `tests/Runtime/VisualGraphContractTests.cs` and
  `FE/tests/visual-fixture.ts`/`visual-contract.test.ts`). Custom cases start ~l.13851: "custom node: its signature
  gives the pins a wire joins", "code custom.signature: a signature the compiler does not write", "custom node: the
  template is valid against the built-in catalog", "custom node: a pure one is a value and takes no step order wire",
  "removed: Custom C# code written as statements"; CLR conversions ~l.14842-20191; flows ~l.31858-32049; "code
  state.dynamicSource: connected entity on a Custom C# value node" ~l.33981.
- `tests/Runtime/GeneratedSourcePinTests.cs`: "pin: pure custom node" ~l.149, "pin: custom node with flows" ~l.156,
  Select property of a custom record ~l.162-167. `UnpinnableKinds` ~l.24 lists the entity-only kinds (`entity,
  device, entity_setup, set_entity_state, …`); the new kinds must join it or the coverage check fails (inferred).
- `tests/Runtime/CustomNodeTests.cs` (`--custom-nodes`): Template, Contract, Numbers, Context, CustomNodes,
  Conversions, Signatures, ErrorDetails, Removed, Api (custom_preview through the request).
- `FE/tests/custom-code.test.tsx`, `clipboard.test.ts`, `custom-types.test.ts`.
- `CC/HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`.

## 2. The class kind of definition

### 2.1 Scan and descriptors
- `NodeLibraryCompiler.EntityTypes` ~l.707-802 lists classes deriving SharpComponent that are declared in library
  sources.
- A class is listed only when it is public, not nested, not abstract, not generic and has a public parameterless
  constructor. Anything else is silently not listed.
- Its domain is the nearest HassSharp base by `SharpDomains.OfName`.
- Its kind is `"device"` if it derives SharpDevice, or `"entity"` if it derives SharpEntity and has a domain.
  Otherwise it is not listed.
- Its title is `[Node]` on the class, else `InWords(name)` ("Shelly plug"). Its description is the doc summary.
- Its id is `"library:"+sourceId+":"+type.ToDisplayString()`, for example `library:shelly:NodeLib.ShellyPlug`.
  `TypeExpression` is `global::NodeLib.ShellyPlug`.
- Settings are the [Setting] properties in declaration order, base classes first:
  - label: the attribute's, else the property name;
  - description: the attribute's, else the doc summary;
  - `Secret`, and `Minimum/Maximum` through `Range`.
- `SettingType` ~l.808-819 maps an enum to `"enum"` (options are the member names), int to `"integer"` with the int
  range, long to `"integer"`, string to `"string"`, bool to `"boolean"` and double to `"number"`.
- Setting messages ~l.688-699: "A setting is a string, bool, int, long, double or enum; {type} cannot be one"; "A
  setting needs a public setter"; "Setting {property} must be a public instance property"; "Minimum and Maximum are
  for an int, long or double setting; {type} cannot have them"; "The setting's Minimum is above its Maximum"; "A
  whole-number setting's Minimum and Maximum are whole numbers".
- Descriptors (`VisualCatalogSnapshot.cs`): `EntitySettingDescriptor(Key, Label, Description, Type, Secret,
  Options){Minimum, Maximum}` ~l.51-62; `EntityTypeDescriptor(Id, SourceId, TypeExpression, Kind, Domain, Title,
  Description, Settings)` ~l.75-76; `snapshot.EntityTypes` ~l.97, never in the catalog Root (pinned in
  `EntityTypeTests.Listed`).
- The `types` op answers `snapshot.EntityTypes` (`EntityApi.EntityRequest` ~l.393-394). MCP calls it
  `entities_types` (`llm.py` ~l.162). `class_kinds` ~l.315 reads it for the `device` summary flag.

### 2.2 The C# model (`CC/HassSharp/Devices/`)
- `SharpComponent.cs`: `IComponentHost`; `SettingAttribute(label){Description, Secret, Minimum, Maximum (NaN =
  unset)}` ~l.51-66; `SharpComponent{Host; Setup; Start; Stop}` ~l.73-100 (also the Every/Track/Http/Files helpers,
  inferred from the example).
- `SharpDevice.cs` ~l.9-52: `Manufacturer … SuggestedArea`, **no Name, no Via**; `Sealed`; `Added` callback;
  `Add<T>` after sealing: "{Type} cannot add entity '{key}' now: entities are added in the constructor or in Setup(),
  before they are registered with Home Assistant."; duplicate: "{Type} already has an entity with key '{key}'."
- `SharpEntity.cs`: `()` gives key `"main"` (~l.30-31); `(key, name)` checks "Entity key '{key}' must be a
  lower-case letter followed by up to 39 …". `Key` get-only; Name/Icon/DeviceClass/Category/EnabledByDefault
  init-only. `RestoreState` ~l.66; `Publish` → `Host?.Publish`; `Domain`, `Commands`, `Command`, `CheckData`,
  `Values`, `DomainOptions`. `Describe(uniqueId)` ~l.181-193 → `{uniqueId,key,domain,name,icon,deviceClass,
  entityCategory,enabledByDefault,commands,options}`; `Snapshot` ~l.196.
- `SharpEntities.cs`: typed bases, each with public `()` and `(key, name)`. `SharpDomains` ~l.117: `Typed` (10
  domains), `Of(Type)`, `OfName`, `DefaultOptions`; `Create(domain, description)` ~l.170 builds a graph entity
  ("Entity {key} has the domain {d}, which a definition built from nodes cannot create"); `Graph` ~l.182, the domains
  a graph may create.

### 2.3 Generate, compile, create
- `DefinitionGenerator.Preview` ~l.46-81, in order: "Give the definition a name of up to 120 characters"; "Logic is
  a graph or a class"; `definition.entitiesNotAllowed` ("A class definition's entities come from its code" / for a
  graph "Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each");
  `DefinitionDocument.Check`; "The node libraries have errors; fix them first"; for a class "No entity class was
  given: choose the class the definition creates" or "No entity class {name} in the node libraries", then `Settings`.
- `Settings` ~l.99-110: "{Title} has no setting {key}", "{Label} takes {Phrase}", "{key} is set twice".
- `Fits` ~l.114, `Within` ~l.129, `Phrase` ~l.136 are the value checks against the descriptor.
- `Generate` ~l.160-174 emits `public static class SharpDefinition { public static object Create() => new
  {TypeExpression}(); }` plus the library sections. One type per assembly.
- `EntityApi.CompileDefinition` ~l.64-100: a class gets `DesignatedRootType="SharpDefinition"` and
  `Definition=new(Id,Name,Device,Settings)`; a graph gets `Compose`, the embedded catalog, `Roots`, triggers including
  hub flows, `DesignatedRootType="VisualAutomation"`, `Definition={Entities=DefinitionGraph.Entities(…), Roots}`.
- `DefinitionHost.CreateCompiled` ~l.880-886 reflects `SharpDefinition.Create()`: "…has no SharpDefinition.Create()"
  or "…returned null".

### 2.4 Keys and unique ids
- `DefinitionHost.UniqueId` ~l.878 is `$"{definition.Id}:{entity.Key}"`. A class entity built with `()` is `<id>:main`.
- `CheckKeys` ~l.888-897: "{Name}: entity key '{k}' must be a lower-case letter followed by up to 39 lower-case
  letters, digits or underscores"; "{Name}: two entities have the key '{k}'".
- `EntityDirectory.TryGet` (`Devices/EntityDirectory.cs` ~l.20-29) splits on the **first** colon (a definition id
  never holds one). `LiveEntity(UniqueId, Key, Domain, Name, EntityId)` ~l.11.
- Python splits the same way: `entity_bridge.py` ~l.269/360/496/503 (prefix `definition_id + ":"`), prune ~l.480-486
  (`split(":",1)[0]`), `authoring.py` ~l.42 (`partition(":")`), and `llm.py definition_summary` ~l.568, which takes
  `partition(":")[2]` as a class entity's key. **With `<id>:<dev>:<key>` that key reads `dev:key`** (inferred).
- `EntityLifecycle.PruneEntities` ~l.102-126 forgets states by `unique[..first ':']`. `EntityApi.ForgetRetyped`
  ~l.315-322 matches `<id>:<key>`.

## 3. The host (`CC/HassSharp/Devices/DefinitionHost.cs`, `DefinitionHost.Graph.cs`)

### 3.1 Shape
- `record CompiledDefinition(Id, Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings)`
  ~l.15-23, with `Factory`, `Entities` (JsonArray, graph only), `Roots`; phase 3 Task 6 adds `Devices`. Fields: one
  `SharpDevice? device` ~l.58, `entities` ~l.59.
- Graph fields (Graph.cs ~l.19-42): `graphEntities`, `awaiting`, `frameFlows`, `graphSettings`, `stateKeys`, and
  successor/predecessor/handed/own.

### 3.2 PrepareAsync ~l.130-148
- It is **either-or**: `definition.Entities is {} ? PrepareGraph : PrepareClass`.
- It then builds the payload `{definitionId, name, device: DeviceJson(), entities: all.Select(e =>
  e.Describe(UniqueId(e)))}` and calls `SyncEntitiesAsync`, which returns the `LiveEntity` list.
- Phase 3 Task 6 changes the payload to `{definitionId, name, devices[], entities[{…, device}]}` and makes bridge
  identifiers `(DOMAIN, "<id>:<key>")`. A class becomes the single device `main` (rulings: `via {definition}` maps to
  `<other>:main`; the bridge keeps its name fallback for a class root entity).
- `DeviceJson` ~l.900-920 (today): named after the definition, with document fields or the class's own, and
  `via {definition}|{deviceId}`.

### 3.3 PrepareClass ~l.151-206, in order
1. root = `Factory` or `CreateCompiled`.
2. Type check: "{Name}: {Type} is neither a SharpDevice nor a SharpEntity".
3. `DefinitionSettings.Apply(Name, definition.Settings, root)`.
4. `device = root as SharpDevice`.
5. `RestoreClass` for each entity so far; `device.Added = RestoreClass` for those Setup adds.
6. SetupClock: `Host=this` and `Setup()` on the device, then the same for each entity, including those added during
   Setup. On timeout: "{Name}: Setup() did not finish within N s".
7. `device.Sealed = true`; 8. `CheckKeys`; 9. `Replaced()`; 10. `entities = all`.

### 3.4 PrepareGraph (Graph.cs ~l.53-107), in order
1. `graphSettings = GraphSettings(definition.Settings)` ~l.763-787 (string, bool and double only).
2. `SharpDomains.Create` for each description in `definition.Entities`; these have **no Host** ("A graph's entity has
   no host of its own").
3. `CheckKeys`; 4. `Replaced()`; 5. stored states for the Restorable ones; 6. under the lock `graphEntities`,
   `awaiting`, `Carry`, `Take`; 7. `entities = all`; 8. FrameNeeded.
9. The When started flow within SetupTimeout; on failure "{Name} does not start: its When started flow …".

### 3.5 Settings
- `DefinitionSettings.Apply(name, settings, root)` (`Devices/DefinitionSettings.cs` ~l.13-28) finds the property by
  reflection (`SettingProperty`: the most derived [Setting] property with a public setter); `TryConvert` handles
  string, enum by name, bool, int, long, double. Messages: "{name}: {Type} has no setting {key}", "{name}: the saved
  value of setting {key} does not fit its type {T}", "{name}: the saved value of setting {label} is not {Phrase}".
  **It takes any object, so it can run once per node.**
- Graph side: `Setting/IsSecret` Graph.cs ~l.486-490, read by `VisualRuntime case "setting"` ~l.777-781 (converted to
  the resolved type). Secret propagation is `VisualRuntime.FollowSettings` ~l.192-211 and `SecretSetting` ~l.262.

### 3.6 Run: start, stop, commands, publish
- `Start/StartAsync` ~l.257-320: `Pump.Forget`; `states.Forget` for ids with prefix `<id>:` not Restorable;
  `Start()` on the device then each entity; status Running or Failed; publishes every entity not in `awaiting`.
- `StopAsync/StopCoreAsync` ~l.340-454: `Inherit` to the successor, Stop each entity, then the device.
- `Command` ~l.505-525: `if (definition.Roots is {} roots && entity.Commands.Contains(name)) await RunFlow(...) else
  await entity.Command(name, data)`. **Every command of a graph definition goes to RunFlow** (~l.276-300, keyed
  `entity.Key+":"+name`, failing "{who} has no flow for {what}").
- `Publish` ~l.677-697 remembers state when Restorable. `Restorable` ~l.704 is
  `definition.Entities != null ? !stateKeys.Contains(key) : entity.RestoreState`, **per definition**.
- `RestoreClass` ~l.709. `HandedFrames` ~l.663. `Entity(uniqueId)` ~l.670.
- `SetState/SetAvailable/SetFrame` Graph.cs ~l.401-469 find the entity by key through `GraphEntity` and fail with
  "…has no entity {key}". A Set state naming a C# entity would hit this, which may be what we want.
- These read the domain/name from the `definition.Entities` JSON: `StateEnded`, `FlowFailed`, `Flow` ~l.629-643 and
  `Named` ~l.730-733.

### 3.7 Hot replace and handover
- `Replaced` Graph.cs ~l.152; `Inherit` ~l.173, matched by key+domain (cameras by key); `Heir` ~l.214; `Carry`
  ~l.221; `Take` ~l.236.
- `CurrentStates` ~l.137-148 covers only `graphEntities` whose domain is in `SharpDomains.Graph`, as plain values.
- A class instance hands nothing over. A new instance starts, and RestoreClass restores from the store.
- `EntityDirectory.Leave` ~l.44-53 lets the heir answer for the definition until it registers.
- Tests: `tests/Runtime/EntityHostTests.cs` `HotReplace` ~l.305-330 pins `device.Setup, relay.Setup, power.Setup,
  sync, relay.Stop, power.Stop, device.Stop, device.Start, relay.Start, power.Start`, nothing pushed unavailable, and
  the directory answering the new instance. Graph handover in `EntityGraphTests.cs`:
  `AWhenStartedFlowOverridesTheStateCarriedOver` ~l.1608, `AValueTheNewRevisionRefusesIsNotCarriedOver` ~l.1646,
  `AnAvailabilityAStateWorkedOutIsNotCarriedOver` ~l.1758, `AnEntityOnADeviceIsHandedOverToANewRevision` ~l.2134.
  (`HandoverTests.cs` is automations' Restart mode; not relevant.)

### 3.8 What is per definition today and must become per node (inferred list)
The single `device` field and `device.Sealed/Added`; the either-or PrepareAsync; `Restorable`; the command route
(RunFlow versus `entity.Command`); `UniqueId` (one prefix); `DefinitionSettings.Apply` against one root;
`CreateCompiled` (one `SharpDefinition.Create`); the Setup timeout scope; Start/Stop order; `CheckKeys` across graph
and C# entities; `CurrentStates/Heir` (graph entities only); `DeviceJson` (one device, until phase 3 Task 6).

## 4. Settings and the Setting node: values at prepare time
- Catalog `setting` ~l.1995-2028: `pure:true`, `documents:["entity"]`, `config.key`, output `value` of `type:""`,
  resolved per graph by `WithGraphTypes`/`ResolveSettings` ~l.260 from `definition.settings[].type`.
- Graph settings may be only `SettingTypes = ["string","number","boolean"]` (`VisualNodeCatalog.cs` ~l.92, TS
  contract.ts ~l.240). `DefinitionDocument.Check` ~l.32-57: "Give setting {i} a key, such as threshold"; "Setting
  {key} has the type {t}, which a graph cannot read; choose string (a text), number or boolean (true or false)"; "Two
  settings have the key {key}; give each a key of its own".
- [Setting] adds `integer` and `enum`. `DefinitionSettings.TryConvert` already turns number→int/long and string→enum
  by name, so a `number` Setting node wired to an int pin converts at apply time. Fractions and out-of-range values
  then fail at start, not at validate time (inferred).
- A way to get a value at prepare time with no flow (inferred): for each `[Setting]` pin read `config.arguments[pin]`;
  a `{kind:"literal", value}` gives the value, a data wire from a `setting` node gives `definition.Settings[key].Value`
  (`scope.settingSource` makes any other wire invalid); build a `DefinitionSetting(propertyName, value)` list and call
  `DefinitionSettings.Apply(Name, list, instance)` before `Setup()`.
- Secrets: literals are stored in the graph JSON and embedded in the source (§1.3), and the read answer's graph is
  not masked, hence `setting.secretLiteral`. `EntityLifecycle.WithoutSecretValues` ~l.229-244 knows a graph's secrets
  by `DefinitionSetting.Secret`, a class's by the `EntityTypes` settings; a C# node's secret pins would need the
  custom descriptor's. `VisualRuntime.FollowSettings` already marks values from secret Setting nodes.
- `DefinitionHost.Graph.cs GraphSettings/SettingValue` ~l.763-787 keep only string, bool and double. A setting read by
  a C# pin would come from `definition.Settings` directly, not from `graphSettings` (inferred).

## 5. The Entity node as the second template

### 5.1 Catalog `entity` ~l.1704-1810
- `root:true`, `declares:"entity"`, `documents:["entity"]`, `argumentStorage:"arguments"`, `dynamicInputs/
  dynamicOutputs/dynamicExecution: "declared"`; config key, domain (default binary_sensor), name, icon, deviceClass,
  entityCategory, enabledByDefault, options, history, arguments.
- `inputs.device {types:["device"], structural:true, required:false, label "Device", description "The device the
  entity is on: connect the Entities pin of a Device node. Without one, it stands alone."}`.
- `device` ~l.1811-1888: `declares:"device"`, `declaration:true`, config key/name/manufacturer/model/swVersion/
  hwVersion/configurationUrl/suggestedArea/via (object, nullable), output `entities {type:"device"}`.
- The type `device` ~l.106 is structural and is never embedded (`EmbeddedCatalog` ~l.108-147).
- `entity_setup` ~l.1889 is a root singleton. `set_entity_state` ~l.1921 (`config.entity`, `dynamicInputs:"entity"`)
  and `set_entity_available` ~l.1951.
- `csharp_device` needs `declares:"device"` and `declaration:true`, but its pins come from a signature, not
  `"declared"`. `csharp_entity` needs `inputs.device` (structural) plus signature pins. Neither is pure or a step
  (inferred).

### 5.2 Catalog helpers (`VisualNodeCatalog.cs`)
- `IsRoot` ~l.40, `Declares(spec, x)` ~l.44, `Declaration` ~l.50, `Structural` ~l.56, `StateInputs` ~l.61.
- `EntityDescription` ~l.69-76: the config minus history/arguments, with a blank name dropped.
- `DeclaredEntities` ~l.81-85 counts only `declares=="entity"`. `GraphDocument` ~l.90.
- `DefinitionItems` ~l.97, `DefinitionEntity` ~l.105, `EntityDomain` ~l.107, `DeclaredDomain` ~l.116,
  `DeclaredExecution` ~l.122, `SettingType` ~l.129, `ValueUpstream` ~l.137-160.
- `WithGraphTypes` ~l.221-255 sets `definition.entities = DeclaredEntities(graph)`.
  - A `csharp_entity` declaring "entity" would be described from its config, which has no domain or name; the domain
    is in `signature.domain`. `EntityDescription` would need to take it from there (inferred).
  - A `csharp_device`'s entities cannot be described before it runs, so they are not in the list. Steps cannot name
    them (consistent with the design: not nodes).
- `DefinitionGraph.Compose` ~l.46-56, `Entities` ~l.62, `Roots` ~l.78-97, `HubFlows` ~l.107-132.
  - Roots walks `IsRoot && Declares("entity")` and makes hub flows. A `csharp_entity` must not get state or command
    flows, or the generator would emit methods for pins that are settings (inferred).
- TS mirrors in `contract.ts`: `entityDescription` ~l.253, `declaredEntities` ~l.263, `definitionEntityKeys` ~l.272,
  `freeKey` ~l.277, `entityKey` ~l.293, `definitionCatalog` ~l.308, `withGraphTypes` ~l.476, `dataInputs` ~l.694,
  `entityNodeName` ~l.823, `NodeSpec` ~l.57 (`declares?: 'entity'|'device'`, `declaration?`, `documents?`).
  `addGraphNode` ~l.1841-1858: `ownEntity` names and keys a new Entity node from its domain; a C# node has no domain
  until its code is applied.

### 5.3 Validator rules on declaring nodes (`VisualGraphValidator.cs`)
- `node.document` ~l.973-974 (TS ~l.1483): a kind whose `documents` leave the graph's document out.
- `Words.StepName` ~l.95: `Declares(spec,"entity")||Declares(spec,"device") ? EntityName(node, spec)`.
- ~l.711-715: `state.dynamicSource` over the Entity nodes' state inputs.
- ~l.716-729, per `Declares("entity")` node: duplicate key `definition.duplicateEntity` (`Words.KeyTaken`);
  `command.unhandled` for each domain command without a step wire; `camera.no_frame`. A `csharp_entity` declaring
  "entity" with a domain row would get false `command.unhandled` errors (inferred).
- ~l.990-1015, per Entity node: `definition.entityKey` (`EntityKeyMissing`, `EntityKey`); `definition.entityDomain`;
  `entity.name` ("unless the definition's device names its entities"); `entity.partialState`; history members.
- ~l.976-981: a `config.entity` naming an unknown key gives `definition.unknownEntity`, and `camera.entity`.
- ~l.674-686: the definition check (`definition.invalid`); `setting.unknown` ~l.1017-1018.
- `DefinitionDocument.Check` "Add an Entity node: an entity definition creates the entities on its canvas, and has
  none" (`definition.entitiesRequired`) counts only `declares=="entity"`. **A graph with only a `csharp_device` would
  fail it.**
- `entity.name` vs `csharp_entity`: its config has no name, and the name lives in code (init-only), so the rule must
  skip it or read the code (inferred).

### 5.4 Editor
- `FE/src/visual/NodeConfig.tsx`: `declares()` ~l.84 is `=== 'entity'`; `retitle` ~l.100-108 writes `config.name`
  for a declaring kind; Edit code is gated on `kind()==='custom'` (§1.6). The new kinds need both Edit code and the
  key/name pickers.
- `FE/src/entities/NodePickers.tsx` `stepPickers` ~l.163-169 maps kind → `{keys, Section}`; `entity` has
  `['key','domain','name','icon','deviceClass','entityCategory','enabledByDefault','options','history','arguments']`
  with `EntityNodeSection`. New kinds need entries or their config shows as generic rows (inferred).
- `FE/src/entities/EntityNodeSection.tsx`, `refit.ts` ~l.59/81 (domain refit, `declares !== 'entity'`) and
  `definitionEdits.ts` (`entityRemoval`, the delete confirmation for steps that name the key, ~l.62).
- `clipboard.ts` `entityKeys/rekey` ~l.208-229 re-keys pasted `declares==='entity'` nodes (a C# device's key is not
  covered). `FE/src/visual/graph.ts` ~l.51-58: the palette filters by `documents`. `flowModel.ts` ~l.181: a declaring
  kind's card name comes from `entityNodeName`. Phase 3 Tasks 4 and 7 (Device card/panel, add/copy/delete) can be reused.

## 6. Tests, fixtures, examples and docs that pin today's behaviour
- Runtime suites: `EntityTypeTests.cs` (`--entity-types`: Listed, Problems, RangesAndTitles; "the built-in snapshot
  has no entity types"); `EntityHostTests.cs` (`--entity-host`, 35 checks: Ordered, RootEntity, DocumentDevice,
  Settings, StartFails, SetupFails, BadKeys, HotReplace, Commands, Controls, LateAdd, RestoredState,
  WhatSetupSetsWins, Unloading, …); `EntityStartupTests.cs` (`--entity-startup`: UnknownClass ~l.649, LibraryRebuild
  ~l.718, EditedOnDisk, Retry, ForgetsTheStatesOfDefinitionsGone, SlowSetup); `SharpEntityTests.cs`,
  `EntityGeneratorTests.cs`. `kind:"class"` also appears in `EntityStoreTests`, `EntityApiTests`, `EntityToolsTests`,
  `CameraTests`, `ReloadMemoryTests`, `ServiceCallTests`. Graph side: `EntityGraphTests.cs`,
  `EntityGraphGeneratorTests.cs` (both modified in the working tree by the phase 3 agent), `EntityNodeTests.cs`.
- `tests/packaging/entity_bridge_smoke.py`: `SmokePlug : SharpDevice` ~l.57 and cameras ~l.77-89, saved as
  `{'kind':'class','type':TYPE}` ~l.383. It checks `definition['device']['name']=='Smoke plug'` ~l.393, the
  pre-Task-6 payload.
- Python: `test_entity_authoring.py`, `test_mcp_adapter.py` (class definitions, `class_kinds`), `test_entity_bridge.py`.
- Frontend: `ClassDefinitionEditor.test.tsx`, `EntityDefinitionView.test.tsx`, `EntitiesListView.test.tsx`,
  `entity-api.test.ts`, `entity-model.test.ts`, `entity-node.test.ts`, `entity-node-config.test.tsx`,
  `custom-code.test.tsx`, `visual-fixture.ts`. Class UI: `FE/src/entities/{ClassDefinitionEditor.tsx, classSession.ts,
  DeviceForm.tsx, model.ts}`, `FE/src/views/{EntityDefinitionView,EntitiesListView}.tsx`.
- Example: `CC/authoring_examples.json` `definitions[4]` "Plug from a class": `SmartPlug : SharpDevice` with `[Setting("Address")]`,
  a `relay` switch and a `power` sensor, `Every` 10 s, Http. Its definition is `{name, device:null,
  settings:[{key:"Address", value:"http://192.168.1.50/rpc"}], entities:[], logic:{kind:"class",
  type:"library:example_plug:NodeLib.SmartPlug"}}`. This is the natural `csharp_device` example.
- `tests/test_docs_examples.py`: `test_the_class_example_is_the_example_library_word_for_word` ~l.51 ties
  `docs/custom-entities.md` to that example. Also `test_the_entity_node_shown_for_authors_is_the_examples` ~l.61.
- Docs: `docs/custom-entities.md` ("## Classes" ~l.357, "### SharpDevice and SharpEntity" ~l.459, "### Settings"
  ~l.485, "### Setup, Start and Stop" ~l.495, "### Keys and ids" ~l.591, "### Saving and replacing" ~l.610, "###
  Commands" ~l.637, "### Secrets" ~l.661); `docs/node-libraries.md` ("## Custom C# nodes" ~l.418, "## Entity classes"
  ~l.566); `docs/backend/entity-definitions.md` ("### Secret settings" ~l.128, "## Node library answers" ~l.218);
  `docs/mcp-authoring.md` ("## Entity definitions" ~l.220, "### The Entity node" ~l.312);
  `docs/backend/visual-automations.md` (custom_preview).

## 7. Traps
1. **Everything custom keys on `dynamicInputs=="custom"`.** That covers `IsCustom`/`CustomSources`, `CustomSignature`,
   `IsPure`, `ExecutionPorts`/`DataInputs`/`DataOutputs`, `MethodOf` dispatch, the malformed-signature rule, the
   stale check, FE `customSignature`/`isPure`/hasCode/Edit code (`kind()==='custom'`), clipboard renaming and the
   draft restore. Reusing `"custom"` compiles the new kinds as `[Node]` methods ("Mark one method with [Node]: it
   becomes this node") and dispatches them as steps; a new value gets none of the pipeline. Each site must branch.
2. **`custom_preview` and `DeriveSignatures` carry no kind**, and `EntityTypes` scans library trees only. A C#
   entity's class in a node's source is never described, and the signature shape (`domain, settings[]`) is different
   from the [Node] one, so `WellFormedSignature` would call it malformed.
3. **The host is graph or class, never both.** PrepareAsync is either-or with one `device` field; graph entities have
   no `Host`, but a C# entity needs `Host=this` to publish; with `Roots` set, `Command` sends every command to
   RunFlow, so a C# switch's turn_on fails with "has no flow for".
4. `Restorable` is per definition: graph → `!stateKeys.Contains`, class → `entity.RestoreState`. A mixed graph
   needs it per entity.
5. Handover: `CurrentStates`/`Heir`/`Carry` cover only graph entities in `SharpDomains.Graph`. A C# entity is a new
   instance each revision. The class path relies on RestoreClass and the HotReplace order pinned in EntityHostTests.
6. **Key source.** The C# `Key` is get-only and comes from the constructor (`main` by default), while the node has
   `config.key`. `UniqueId` is `<id>:<entity.Key>`, so two `csharp_entity` nodes whose classes use `()` collide as
   `main` (`CheckKeys` "two entities have the key 'main'"). Likewise Name/Icon/… are init-only in code, while
   `entity.name` requires a name on an Entity node.
7. **A three-part unique id breaks first-colon parsers that read the rest as a key**: `LiveEntity.Key`,
   `llm.definition_summary` (`partition(":")[2]`), `ForgetRetyped` (`<id>:<key>`), `Named`/`Flow` lookups by key.
   Prefix parsers (directory, prune, bridge, `authoring.py`) still work. Phase 3's device identifiers
   `(DOMAIN, "<id>:<key>")` share text with entity unique ids when a device key equals an entity key (separate HA
   registries, so likely harmless; inferred). Check phase 3 Task 6's final form.
8. **`entities` null before the first run** (C# device) hits: `DeclaredEntities`/`WithGraphTypes`,
   `DefinitionGraph.Entities` and `EntityDefinition.Save` (stores derived entities, ~l.225-264);
   `definition.entitiesRequired` (a csharp_device-only graph fails "Add an Entity node…"); `llm.definition_summary`
   (a graph's entities from `document.entities`, not registered ones); `PrepareAsync`'s `definition.Entities is {}`
   mode test; `Named`, `Flow`, `StateEnded`, `FlowFailed` (read `definition.Entities` JSON).
9. A C# node declaring "entity" inherits hub flows (`DefinitionGraph.Roots/HubFlows`), `command.unhandled`,
   `camera.no_frame`, `entity.partialState` and `state.dynamicSource`. Its setting pins would look like state values.
   `StateInputs` and `HubFlows` skip only `structural` inputs.
10. Settings types: graph settings are string/number/boolean, [Setting] adds integer/enum. `TryConvert` converts
    number→int and string→enum by name, so validation needs its own fit check (reuse
    `DefinitionGenerator.Fits/Within/Phrase`) or a bad value or range surfaces only at start.
11. Literal `config.arguments` are embedded in the generated source and stored unmasked, so a secret literal leaks
    into source and read answers. Also `WithoutSecretValues` knows only definition-level and class secrets.
12. One assembly, many classes: `NodeLib.Custom_<id>` keeps two nodes' types apart. But `Generate` and
    `CreateCompiled` assume one `SharpDefinition.Create()` per assembly, and `DesignatedRootType` is one name. A mixed
    graph needs a per-node factory, for example a static method per node or reflection on
    `NodeLib.Custom_<id>.<Type>` (inferred).
13. "No user class is run on preview": the constructor runs `Add(...)`, so entity lists can only come from running
    it, at prepare time. Preview must not instantiate.
14. `GeneratedSourcePinTests.UnpinnableKinds` and the fixture template case must learn the new kinds. Phase 3's
    working tree is already editing `EntityGraphGeneratorTests.cs`, `EntityGraphTests.cs` and the fixture, so expect
    merge churn there.
15. `SharpDevice` has no Name/Via, while `csharp_device` config has `name, via`. The device JSON must take these from
    the node and the rest (manufacturer…) from the class, unlike the `device` kind, which takes all from config.
16. `CustomCodeEditor`/`CustomCodeHelp` text says "step", "[Node]" and "value node", and the `Pins` panel assumes
    inputs/outputs. Reusing the dialog as is gives wrong words for a class.

## Facts I could not establish
- Phase 3 Task 6's final payload and device-identifier form; it is not written yet. I read the plan only.
- Whether `SharpComponent` exposes anything a host needs beyond `Host/Setup/Start/Stop` (Every/Track wiring is read
  from the example, not the class).
- The exact assertions in `test_entity_bridge.py` on prune, and in `entity_bridge_smoke.py` past ~l.450.
- Whether `NodeLibraryLanguageServices` type-checks a class-shaped custom document unchanged (likely: it wraps any source).

## Open questions for the planner
1. Who owns an entity's key and name: the node (`config.key`, as the design's config has) or the class constructor?
   If the node, `Key` must become settable by the host before `Setup()`, and `main` stops being the default.
2. Do Name/Icon/DeviceClass come from code only, or can the node override them? This decides how `entity.name` and
   `EntityDescription` treat `csharp_entity`.
3. Which `dynamicInputs` value (or new flag) marks the kinds? Which of the ~15 `"custom"` sites branch on it?
4. Does `custom_preview` gain a `kind` (or `shape: "class"`) field? Does `DeriveSignatures` dispatch by kind? And
   does `EntityTypes` scan the node's own tree?
5. How does a C# entity's command route when the definition also has Roots? Is RunFlow tried only for keys in
   `Roots.Commands`?
6. Is `Restorable` per entity: graph entities by `stateKeys`, C# entities by `RestoreState`?
7. Does a `csharp_device` count toward `definition.entitiesRequired`? Do `csharp_entity` nodes appear in
   `definition.entities`, so steps can Set state on them (design: they are entities; a C# device's are not nodes)?
8. Unique-id shape `<id>:<device key>:<entity key>`: which parsers move off `partition(":")[2]`? How does prune treat
   a C# device whose entity set changes between runs?
9. Setting type mapping: may an `integer`/`enum` pin take a `number`/`string` Setting node? When does the range check
   run: validate time, or prepare time only?
10. Handover: is a C# entity's state carried over to the new revision (as graph entities are), or only restored from
    the store (as classes are)? Which HotReplace order holds in a mixed graph?
11. Does the class kind keep its own path until phase 5? Or does phase 4 already prepare a class definition as one
    `csharp_device` node, sharing the code paths?
12. How is each node's root instance created, given one `SharpDefinition.Create()` per assembly?

# Entity nodes, phase 3: devices as nodes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A device is a node on the canvas. A wire from its Entities pin to an Entity node's Device pin puts that entity on the device; an Entity node without the wire is a standalone entity. A definition may hold several devices, and is no longer a device itself: the right panel's Device form and the request's `device` are gone.

**Architecture:** One new catalog kind, `device`, flagged `declares: "device"` and `declaration: true` (not a step, not a value), and one new data type, `device`, flagged `structural: true` (never evaluated, never offered to an "any type" input). Membership is a data edge into the Entity node's `device` input, so the canvas, the connection checks and the limits already handle it. Each side derives the definition's device list, and each entity's device, from the graph where it derives the entity list today. The host hands the bridge `devices[]` and each entity's `device`; the bridge keys a device by `(DOMAIN, "<definition id>:<device key>")`. The work is in three stages, as phase 2 was: the Device node is added beside the definition's device (Tasks 1 to 7), every test, example and document moves to it (Task 8), then the definition's device is removed (Task 9).

**Tech Stack:** C# on .NET 11 (`custom_components/hass_sharp/HassSharp/`, custom test runner in `tests/Runtime/`), SolidJS with TypeScript 7 and vitest 5 (`custom_components/hass_sharp/frontend/`), Python 3 (`custom_components/hass_sharp/*.py`, `tests/python/`). `CC/` below is `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`.

**Spec:** `docs/superpowers/specs/2026-11-19-entity-nodes-design.md` (decision 4, "Shapes", "Validation", phase 3). Code map for this phase: `docs/superpowers/plans/2026-11-20-entity-nodes-phase-3-map.md` (written at `f7a6a07`; its sections 2 and 5 list every place a Device node or its wire trips existing code). Line numbers here and there are approximate; find the symbol.

## Global Constraints

- **One catalog, two sides.** `CC/visual_node_catalog.json` is embedded in the C# library and imported by `FE/src/visual/contract.ts`. A catalog edit and the code on both sides that reads it land in one commit. The same holds for a case added to `tests/fixtures/visual-graph-validation.json`: both validators read it, and their messages must be byte-identical.
- **Every commit leaves these green**, run from the repository root:
  - `python3 -m unittest discover -s tests/python` (the MCP and platform tests skip on the host: also run the suite in the test container, as the controller's notes say)
  - `python3 -m unittest discover -s tests`
  - `dotnet run --project tests/Runtime --disable-build-servers`
  - `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`
  - `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`
  - after `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`: the entity bridge smoke and the SQLite smoke
- **Out of scope.** No C# entity or device node (phase 4). The class kind is not deleted (phase 5): a class definition must load, save and run after every task, its device edited in the class editor as today. Nothing changes for automations: an automation's generated source is byte-identical before and after every task (the generated-source pins prove it; a pin of an automation graph that changes is a defect, not something to re-pin).
- **Both forms work from Task 1 until Task 9.** A graph definition may still send `device` (the definition as one device) and may declare Device nodes. From Task 6 the definition's device goes to Home Assistant as a device keyed `main` that holds every entity with no wire. A graph that uses both forms is not a supported case: it gets no rule and no test beyond that sentence.
- **Names in the graph.** Kind `device`. Its output is `entities`, of type `device`. The Entity node's input is `device`, of type `device`, `structural: true`. Membership is the data edge `{kind: "data", source: <device node>, sourcePort: "entities", target: <entity node>, targetPort: "device"}`.
- **`graphVersion` stays 5.** The stored document's `version` goes to 3 in Task 5.
- **Keys.** A device key follows the entity key rule (`^[a-z][a-z0-9_]{0,39}$`). Device keys and entity keys are separate namespaces. A device's identifier in Home Assistant is `(DOMAIN, "<definition id>:<device key>")`; an entity's unique id stays `<definition id>:<key>` whatever device it is on.
- **Generated-source pins and catalog fixtures.** A pin of an *entity definition's* graph that fails only because the embedded catalog changed is re-pinned with `-- --describe-generated-source-pins`, named in the commit body. When a frontend test fails because `tests/fixtures/visual-rich-catalog.json` lacks a kind, regenerate it with `-- --write-node-library-fixture`.
- **Fixture messages.** Write a new case's `messages` by hand from the copy below, then check them with `-- --describe-visual-graph=<file of cases>`. A message does not end with a full stop and does not use the words the fixture test's jargon check refuses.
- **Exact copy.** User-facing strings are hardcoded English. `{Name}` is the device's name, else its key, else `Device`; for an entity as in phase 2.
  - Kind: label `Device`; description `A device in Home Assistant that groups entities. Connect its Entities pin to the Device pin of each entity that belongs to it.`
  - Pins and type: output `Entities`; the Entity node's input `Device`; type label `Device`
  - `definition.deviceKey`, blank: `Give this device a key, such as plug`
  - `definition.deviceKey`, malformed: `Device key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters`
  - `definition.duplicateDevice`: `The key {key} is already used by {Name of the first}: give this device its own`
  - `device.name`: `Name this device: Home Assistant shows it by this name`
  - `device.via`: `Part of must be nothing, a Home Assistant device or another device of this definition`
  - `device.viaUnknown`: `{Name} is part of the device {key}, which is not a device of this definition: choose another, or none`
  - `device.viaCycle`: `{Name} ends up being part of itself: change what one of these devices is part of`
  - `device.configurationUrl`: `The configuration URL of {Name} must start with http://, https:// or homeassistant://`
  - `device.empty` (a warning): `{Name} has no entities: connect its Entities pin to an entity's Device pin`
  - `flow.declaration`: `{Name} is a device and is not part of the run order`
  - `entity.name` (existing words, kept): `Name this entity: it has no device, so Home Assistant shows it by its own name`
  - `definition.deviceNotAllowed`, from Task 9: `Devices are Device nodes in the graph now: leave device out, add a Device node and connect its Entities pin to each entity's Device pin`
  - A graph document below version 3 (the existing sentence): `saved by an earlier development build; delete it and build it again`
  - Inspector of a Device node: header input `aria-label="Device name"`, placeholder `Not set`; card summary `Device · {key}`; `Key` with the hint `The key is part of the device's identity in Home Assistant and cannot change.`; `Manufacturer`, `Model`, `Software version`, `Hardware version`, `Configuration URL`, `Suggested area` and `Part of` keep the labels, placeholders and hints they have in `FE/src/entities/DeviceForm.tsx` today, except that Part of's second choice reads `Another device of this definition`
  - Inspector of an Entity node: a select `Device` whose first choice is `None`, then one choice per Device node by `{Name}`; hint `An entity on a device is listed on that device's page. None: it stands alone.`
  - Entities page card chip: `Device` for one device, `{n} devices` for more
- **Commits.** One per task, files staged by name. The message is one sentence saying what the user now gets, ending with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Home Assistant restarts.** The registry clean-up at start-up must keep every device of a definition that still exists, with its entities: a device identifier is `<definition id>:<device key>`, and the clean-up compares only the part before the first colon. Tests in Task 6 (the fake registry and, in the container, the real one).
2. **An entity moves to another device, or its device is deleted.** The entity keeps its entity id, its registry entry and its history; an entity Home Assistant has disabled moves too; the device left with nothing described is removed only after its entries are off it. Tests in Task 6.
3. **The Device wire is not a state value.** A wire into the Device pin does not make a state flow, is not read at run time, does not ask for the entity's other state values, and is not refused as "a step's output". Tests in Tasks 1 and 2 (fixture cases and a runtime test of an entity with a device wire and pushed state that is restored after a restart).
4. **A Device node is not a step.** No step-order wire can be drawn into or out of it, it is never "not connected into the flow", and its Entities pin connects to nothing but a Device pin: not to a service field, a list item, or any "any type" input. Tests in Task 1.
5. **An upgrade from a phase 2 build.** A version 2 graph document fails to load with the one sentence and can be deleted from the Entities page; the device `(DOMAIN, "<definition id>")` an earlier build made is removed at the next sync or clean-up, its entities kept. Tests in Tasks 5 and 6.

---

### Task 1: A Device node and the Device wire exist, and no rule mistakes them for a step or a value

**Files:**
- Modify: `CC/visual_node_catalog.json` (type `device`; kind `device` right after `entity`; `inputs.device` on `entity`)
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs`, `CC/HassSharp/Visual/VisualGraphValidator.cs`
- Modify: `FE/src/visual/contract.ts`, `FE/src/visual/graph.ts` (`canConnect`), `FE/src/visual/flow/connectionIndex.ts`, `FE/src/visual/typeLabels.ts`, `FE/src/styles/tokens.css`
- Modify: `tests/fixtures/visual-graph-validation.json`, `tests/fixtures/visual-rich-catalog.json` (regenerate)
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/connection-index.test.ts`, `FE/tests/palette.test.tsx`

**Interfaces:**
- Produces, catalog: `types.device = {label: "Device", description, structural: true, listed: false, assignableTo: ["device"]}` (no `clrType`); `nodes.device = {label, category: "Entity", description, declares: "device", declaration: true, documents: ["entity"], minGraphVersion: 5, config: {key, name, manufacturer, model, swVersion, hwVersion, configurationUrl, suggestedArea (each `string`), via (`object`, nullable)}, inputs: {}, outputs: {entities: {type: "device", label: "Entities"}}, execution: [], runtimeVersions: [5], keywords}`; `nodes.entity.inputs.device = {type: "device", structural: true, required: false, label: "Device", description}`.
- Produces, C# (`VisualNodeCatalog`): `static bool Structural(JsonNode? typeOrInput)`, `static bool Declaration(JsonObject? spec)`, `static IEnumerable<KeyValuePair<string, JsonNode?>> StateInputs(JsonObject node, JsonObject catalog)` (a hub's data inputs without the structural ones).
- Produces, TS (`contract.ts`): `structural(spec: {structural?: boolean} | undefined): boolean`, `isDeclaration(kind: string, catalog): boolean`, `stateInputs(node, catalog)`; `NodeSpec.declares?: 'entity' | 'device'`, `NodeSpec.declaration?: boolean`, `InputSpec.structural?: boolean`, `TypeSpec.structural?: boolean`.

**Behaviour:**
- **Structural types** are left out wherever a list of "all types" is made: `$any`, the Object fields, unknown service fields and list `items` (C# `DataInputs`: `$any` and the `all` list; TS `dataInputs`: `$any` and `types`), `listedTypes`, and a config value with `enumSource: "types"` (a Constant of type `device` is refused as an unknown type is).
- **A structural input** takes a wire only: a typed value in `config.arguments.device` other than the wire marker is the existing "nothing can be typed in here" problem. It stays in `DataInputs` / `dataInputs` (the argument check and `refit` rely on that) and is skipped by everything that means "state value": `entity.partialState` (neither counted as given nor asked for), `scope.stateSource` and `entityScopes`, `state.dynamicSource`, `entity.no_value`, `Supplied`.
- **A declaration** (`declaration: true`) has no execution input and no execution outputs. A step-order edge that starts or ends at one gets `flow.declaration` on the declaration (as `flow.pure` is reported for a value) and does not put it in a scope. It never gets `node.unreachable` or `pure.unused`. `canConnect` refuses a step-order wire to or from it; the connection index agrees with `connectData` on every pair that involves a Device node or a Device pin.
- **Types of the wire.** `device` is assignable only to `device`, so `connectData` accepts Device.entities → Entity.device and refuses every other pairing by the existing type words. `maxSourcesPerInput: 1` already gives "one device per entity" (`port.inputTaken`).
- **Palette.** The Entity group lists `entity`, `device`, `entity_setup`, … (the catalog's key order).
- A Device node's own rules (key, name, Part of) come in Task 3; here a Device node with any config is valid.

- [ ] **Step 1: Write the failing fixture cases** in `tests/fixtures/visual-graph-validation.json` (entity documents; build them like the existing Entity node cases):
  - `a Device node wired to an Entity node is valid`: `noDiagnostics`.
  - `a Device node with nothing connected is not "not connected into the flow"`: no `node.unreachable` (the case may carry Task 3's `device.empty` later; assert on codes present today: none).
  - `a step-order wire into a Device node is refused`: `valid: false`, code `flow.declaration`, `nodeId` the Device node, message `Hall plug is a device and is not part of the run order`; the Device node is still not `node.unreachable`.
  - `a Device wire does not ask for the entity's state values`: a light with only the device wire: `noDiagnostics` (no `entity.partialState`).
  - `a light with Brightness and a Device wire is asked for On only`: `entity.partialState` whose message lists `Brightness` as given, not `Device`.
  - `a Device node's Entities pin connects only to a Device pin`: a data edge Device.entities → Log.message (an `$any` input): `valid: false` with the existing type-mismatch code; and Device.entities → an Object create field: the same.
  - `a value typed into the Device pin is refused`: `config.arguments.device = {kind: "literal", …}`.
  - `a Constant of type device is refused`.

- [ ] **Step 2: Write the failing unit tests**

```csharp
// tests/Runtime/EntityNodeTests.cs
static void ADeviceNodeIsADeclaration()
{
    var catalog = VisualNodeCatalog.Root;
    var device = VisualNodeCatalog.GetNode("device")!;
    Check(VisualNodeCatalog.Declaration(device) && VisualNodeCatalog.Declares(device, "device"), "the Device kind declares a device");
    Check(VisualNodeCatalog.Structural(catalog["types"]!["device"]), "the device type is structural");
    Check(VisualNodeCatalog.Structural(VisualNodeCatalog.GetNode("entity")!["inputs"]!["device"]), "the Entity node's Device input is structural");
    // DataInputs of a `log` node (an $any input) does not list "device" among its types.
    // StateInputs of a switch hub is ["is_on"]; DataInputs of it also has "device".
    // The catalog's Entity group begins entity, device, entity_setup.
}
```

```ts
// FE/tests/entity-node.test.ts
it('a Device node connects to an Entity node\'s Device pin and to nothing else', () => {
  // connectData(graph, device, 'entities', hub, 'device') is accepted and adds the data edge;
  // connectData(graph, device, 'entities', log, 'message') is refused;
  // connectNodes / canConnect refuse a step-order wire from a step to the Device node and from it.
})
it('a new Device node has no step pins and no state', () => {
  // executionPorts(device) is []; stateInputs(switchHub) is ['is_on']; dataInputs(switchHub) has 'device'.
})
// FE/tests/connection-index.test.ts: the index and connectData agree on every pair of a graph that has a Device node,
//   two Entity nodes and a Log step (extend the existing all-pairs test's graph and its pinned totals).
// FE/tests/palette.test.tsx: in the entity editor the Entity group begins Entity, Device, When started.
```

- [ ] **Step 3: Run** `-- --entity-nodes`, `-- --visual-contract` (the suite that reads the fixture) and the three frontend files. Expected: FAIL: no `device` kind.

- [ ] **Step 4: Implement** the catalog entries and the two sides. Add the type's label to `Words.BuiltIn` and `typeLabels.ts`, and a port colour token `--hs-port-device` beside `--hs-port-image`. Regenerate `visual-rich-catalog.json`.

- [ ] **Step 5: Run** every suite. Expected: pass; the generated-source pins of automations unchanged (Task 2 owns the embedded catalog; if an automation pin fails here, stop and report it).

- [ ] **Step 6: Commit**

```bash
git commit -m "$(printf 'A Device node can be placed and wired to an entity, and it is not treated as a step or a value.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 2: A definition with a Device node generates and runs as before

**Files:**
- Modify: `CC/HassSharp/Visual/VisualGenerator.cs` (`EmbeddedCatalog`, the node switch), `CC/HassSharp/Visual/VisualRuntime.cs` (the constructor's data-edge table, `EntityValues`), `CC/HassSharp/Visual/StateDependencies.cs` and `VisualNodeCatalog.ValueUpstream`, `CC/HassSharp/Devices/DefinitionGraph.cs` (`HubFlows`)
- Modify: `FE/src/visual/contract.ts` (`valueUpstream`, any mirror of `HubFlows` such as `flowPins`), `FE/src/components/TriggerSummary.tsx` if it counts inputs
- Test: `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/EntityNodeTests.cs`, `tests/Runtime/GeneratedSourcePinTests.cs`

**Interfaces:**
- Consumes: `Structural`, `Declaration`, `StateInputs` (Task 1).
- Produces: nothing new. `DefinitionGraph.HubFlows` gives a state flow only when a *state* input is wired or set.

**Behaviour:**
- A device wire alone makes no state flow: the entity is not in `DefinitionRoots.StateEntities`, so it stays restorable and is carried over on a hot replace.
- The runtime never reads a structural input: `EntityValues` and the state flow use `StateInputs`; the constructor does not record a structural edge as a value to prepare. A Device node is never walked or dispatched ("Unknown compiled node" is unreachable for it).
- `StateDependencies.Of` and `ValueUpstream` skip structural edges explicitly.
- `EmbeddedCatalog` never embeds a structural type (it embeds a type when its id appears as any string in the graph, and a native device trigger says `"trigger": "device"`): an automation's generated source does not change.

- [ ] **Step 1: Write the failing tests**

```csharp
// EntityGraphTests (the Rig; build with the shared Hub/Flow helpers and a Device node `plug`)
//   AnEntityOnADeviceKeepsItsPushedState: a switch hub wired to a Device node, its state pushed by Set state in its
//     Turn on flow. Save, turn on, restart the rig: the switch is on again (restored). Its roots have no state flow.
//   AnEntityOnADeviceWorksOutItsState: a binary sensor hub with `is_on` wired from a value AND a device wire: the
//     state flow sets is_on, and the run does not fail on the Device input.
//   ADeviceNodeIsNeverRun: a definition with a Device node and nothing wired to it saves, starts and runs its other
//     flows; the generated source has no case for the Device node.
// EntityNodeTests
//   TheRootsOfANodeIgnoreItsDeviceWire: HubFlows of a hub with only a device wire has no "state".
// GeneratedSourcePinTests
//   AnAutomationWithADeviceTriggerIsUnchanged: add a pin of an automation whose trigger config holds the string
//     "device" (a native device trigger), taken BEFORE the implementation (it must already pass at Task 1's commit),
//     and keep it passing.
```

- [ ] **Step 2: Run** `-- --entity-graph`, `-- --entity-nodes`. Expected: FAIL: the hub with a device wire has a state flow and is not restored.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** every suite, publish, both smokes. Expected: pass; no automation pin changed.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'An entity wired to a Device node runs, restores its state and is handed over exactly as one without the wire.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 3: Problems of a device show on its node, and an entity on a device needs no name

**Files:**
- Modify: `CC/HassSharp/Visual/VisualGraphValidator.cs` (`Codes`, `Words`, the per-node rules, the graph-wide device rules, `entity.name`), `CC/HassSharp/Visual/VisualNodeCatalog.cs`
- Modify: `FE/src/visual/contract.ts` (`diagnosticCodes`, `words`, `graphRules`, `entityNodeName` and `words.stepName` for a Device node), `FE/src/visual/flow/connectionIndex.ts` (a wire into a Device pin removes `entity.name`; it must not be refused for the problems it leaves)
- Modify: `tests/fixtures/visual-graph-validation.json`
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`

**Interfaces:**
- Produces, C#: `VisualNodeCatalog.DeclaredDevices(JsonObject graph, JsonObject catalog) : IEnumerable<(string NodeId, JsonObject Device)>` (node order; the device is the node's config), `VisualNodeCatalog.DeviceOf(JsonObject graph, string entityNodeId, JsonObject catalog) : string?` (the node id of the Device node wired into the hub's `device` input, else null).
- Produces, TS: `declaredDevices(graph, catalog): {nodeId: string; device: Record<string, unknown>}[]`, `deviceNodeOf(graph, entityNodeId, catalog): string | undefined`, `definitionDeviceKeys(graph, catalog): string[]`.
- New codes on both sides: `definition.deviceKey`, `definition.duplicateDevice`, `device.name`, `device.via`, `device.viaUnknown`, `device.viaCycle`, `device.configurationUrl`, `device.empty`.

**Behaviour** (every message is in Global Constraints; each problem is on the Device node with `inputId` the config key it is about):
- `definition.deviceKey` (`inputId: "key"`): blank, or not of the key form. `definition.duplicateDevice`: of two Device nodes with one key, the later in node order has it, naming the first.
- `device.name` (`inputId: "name"`): the name is blank or missing.
- `device.via` (`inputId: "via"`): `via` is not null, `{deviceId: <non-empty string>}` or `{device: <non-empty string>}`. `device.viaUnknown`: `{device: key}` names no Device node of this graph. `device.viaCycle`: following `{device}` from the node comes back to it (a device that is part of itself included); every node on the circle has it.
- `device.configurationUrl` (`inputId: "configurationUrl"`): set, and not starting with `http://`, `https://` or `homeassistant://`.
- `device.empty`, a warning: no data edge leaves the node's `entities` output.
- `entity.name`: an Entity node is standalone when `DeviceOf` is null **and** the definition has no device marker (`definition.device`, which stays until Task 9). Standalone and nameless: the existing problem. On a device and nameless: no problem.
- Card titles and messages name a Device node by its name, else its key, else `Device` (`Words.StepName`, `entityNodeName`'s rule extended to `declares === 'device'`).

- [ ] **Step 1: Write the failing fixture cases**, one per rule and message above, and:
  - `an entity wired to a device needs no name` (`noDiagnostics`, no marker in the case);
  - `an entity not wired to a device needs a name` (two hubs, one wired, one not: only the second has `entity.name`);
  - `two devices that are part of each other` (both have `device.viaCycle`);
  - `a device that is part of another device of this definition is valid`;
  - `a device with no entities is warned about` (`valid: true`, code `device.empty`).

- [ ] **Step 2: Write the failing unit tests**

```csharp
static void DevicesAreDerivedFromTheirNodes()
{
    // A graph with Device nodes `plug` (key plug) and `hub` (key hub), entities relay → plug, power → plug, bare:
    // DeclaredDevices gives plug then hub in node order with their config;
    // DeviceOf(relay) == "plug", DeviceOf(bare) == null;
    // DeviceOf of a hub whose Device input is wired from a node that is not a Device node is null.
}
```

```ts
it('wiring an entity to a device takes away its need for a name', () => {
  // validateGraph of a nameless hub: entity.name. After connectData(device.entities → hub.device): none.
  // connectData is accepted although the Device node still has device.name: a wire is refused only for a problem it makes.
})
```

- [ ] **Step 3: Run** the fixture suite on both sides. Expected: FAIL: unknown codes.

- [ ] **Step 4: Implement** on both sides.

- [ ] **Step 5: Run** every suite. Expected: pass.

- [ ] **Step 6: Commit**

```bash
git commit -m "$(printf 'A Device node says what is wrong with its key, name and Part of, and an entity wired to a device needs no name of its own.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 4: The card and the right panel of a Device node, and the Device pin of an Entity node

**Files:**
- Modify: `FE/src/visual/flow/flowModel.ts` (`NodeView`, `buildNodeView`, `nodeTitle`, `unreachable`, `viewKey`), `FE/src/visual/flow/NodeCard.tsx` (the header), `FE/src/visual/layout.ts` if a Device node needs placing
- Modify: `FE/src/visual/NodeConfig.tsx` (the header name input for any declaring kind; the generic Inputs section skips structural inputs), `FE/src/entities/NodePickers.tsx` (`stepPickers.device`), `FE/src/entities/EntityNodeSection.tsx` (the Device select), `FE/src/entities/adapter.ts` and `FE/src/visual/adapter.ts` (`SavedEntities` gains `deviceKeys: string[]`)
- Create: `FE/src/entities/DeviceNodeSection.tsx`
- Modify: `FE/src/entities/DeviceForm.tsx` (export its field list and the Part of control so both the class editor and the node section use one definition of each; the class editor is unchanged in behaviour)
- Test: `FE/tests/canvas-cards.test.tsx`, `FE/tests/entity-node-config.test.tsx`, `FE/tests/NodeConfig.test.tsx`

**Interfaces:**
- Consumes: `declaredDevices`, `deviceNodeOf`, `definitionDeviceKeys`, `structural` (Tasks 1, 3).
- Produces: `NodeView.headerInput?: InputView` (the structural input of a root card, drawn in the header's left slot with handle id `data:device`; it is not among `rows`); `DeviceNodeSection` (props as `EntityNodeSection`'s).

**Behaviour:**
- **Entity card.** The Device pin sits in the header's left slot (a root has no step input there), with the accessible name `Device`; the state rows keep their alignment with the command pins. Wired, the header shows the device's `{Name}` beside the pin.
- **Device card.** Title `{Name}`, summary `Device · {key}`, one output row `Entities`, no step pins, never drawn muted as unreachable.
- **Device inspector.** The header input edits `config.name`. The section has Key (read-only once the saved document has that device key: `savedEntities().deviceKeys`), Manufacturer, Model, Software version, Hardware version, Configuration URL, Suggested area and Part of. Part of offers `Nothing`, `Another device of this definition` (a select of the other Device nodes by name; writes `via: {device: <key>}`) and the Home Assistant device search as today (`via: {deviceId}`), minus the devices this definition's own entities are on. A blank text field is stored as the key removed, not as `""`.
- **Entity inspector.** A `Device` select: `None` and each Device node. Choosing one replaces the wire into the Device pin (`connectData`); `None` removes it (`disconnectData`). One undo step each. The generic Inputs list does not show the Device input.
- Problems of Task 3 focus their field (`inputId`) as an Entity node's do.

- [ ] **Step 1: Write the failing tests**

```tsx
// canvas-cards.test.tsx
//   'an Entity card has its Device pin in the header': the card of a switch hub has a handle `data:device` inside
//     its header and none among its rows; its On row is still level with Turn on.
//   'an Entity card on a device names it': after the wire, the header shows "Hall plug".
//   'a Device card has an Entities pin and no step pins': no `in` handle, no `next` handle, one output handle
//     `data:entities`; title "Hall plug", summary "Device · plug"; it does not have the unreachable class.
// entity-node-config.test.tsx
//   'the Device select of an Entity node wires and unwires it': choose "Hall plug" → the graph has the data edge;
//     choose "None" → it is gone; each is one undo step.
//   'a Device node's fields edit its config': type a Model → config.model; clear it → the key is removed.
//   'Part of another device of this definition': choose it → config.via is {device: "hub"}; the node itself is not
//     among the choices.
//   'a saved device's key is read-only': with savedEntities().deviceKeys = ['plug'] the Key input is read-only.
// NodeConfig.test.tsx
//   'the header input of a Device node edits its name' (aria-label "Device name").
```

- [ ] **Step 2: Run** the three files. Expected: FAIL.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** `bun run test`, `bunx tsc --noEmit`, `bun run build`. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'A Device node has its own card and right panel, and an Entity node shows the device it is on in its header.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 5: The server derives the devices, and the stored document is version 3

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionGraph.cs` (`Devices`, `Entities`), `CC/HassSharp/Visual/VisualNodeCatalog.cs` (`WithGraphTypes`), `CC/HassSharp/Devices/EntityDefinition.cs` (`DefinitionVia`, `DefinitionDevice`, `EntityDefinitionDocument`, `Version`, `Complete`, `Read`, `Save`, `Rebuilt`), `CC/HassSharp/Devices/EntityApi.cs` (the read and list answers)
- Modify: `FE/src/visual/contract.ts` (`derivedEntities`, `derivedDevices`, `withGraphTypes`), `FE/src/entities/model.ts` (`EntityDocument`: `version: 1 | 2 | 3`, `devices`), `FE/src/entities/api.ts`
- Modify: `CC/llm.py` (`STORED_DOCUMENT_KEYS` gains `devices`)
- Test: `tests/Runtime/EntityStoreTests.cs`, `tests/Runtime/EntityNodeTests.cs`, `tests/Runtime/AuthoringAdapterTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/entity-model.test.ts`

**Interfaces:**
- Produces, C#: `DefinitionGraph.Devices(JsonObject graph, JsonObject catalog) : JsonArray` (each Device node's config, in node order); `DefinitionGraph.Entities` adds to each derived entity the member `device`: the key of the Device node it is wired to, or JSON null. `record DefinitionVia(string? Definition, string? DeviceId, string? Device = null)`. `DefinitionDevice` gains `string? Key { get; init; }` and `string? Name { get; init; }` (null on a class's or a request's device). `EntityDefinitionDocument` gains `JsonArray? Devices` (null for a class document).
- Produces, TS: `derivedDevices(graph, catalog)`; `withGraphTypes(...).definition.devices`; each derived entity has `device: string | null`.

**Behaviour:**
- The read answer, the stored file and `catalog.definition` carry the derived `devices` and each entity's `device`. A request that sends `devices` is not refused: the member is ignored, as a read answer sent back must work.
- `Save` writes `version: 3` for every document. `Read`: a **graph** document below version 3 throws the earlier-build sentence (it is listed under "Cannot be read" and can be deleted, as a version 1 one is today); a **class** document of version 1, 2 or 3 loads. `Rebuilt` (a library rebuild) keeps the stored `Devices` as it keeps `Entities`.
- The request's `device` is untouched in this task.

- [ ] **Step 1: Write the failing tests**

```csharp
// EntityStoreTests
//   ASaveWritesVersionThreeWithTheDerivedDevices: a graph with Device node plug (name "Hall plug", model "P1"),
//     relay → plug, bare: the stored file has "version": 3, "devices": [{"key":"plug","name":"Hall plug","model":"P1"…}]
//     and "entities" whose relay has "device":"plug" and bare "device":null. (Replaces the version-two test.)
//   AVersionTwoGraphDocumentSaysToBuildItAgain: a version 2 graph document written by hand: List() skips it and
//     reports it as unreadable with the sentence; Delete(id, 0) removes it.
//   AnOlderClassDocumentStillLoads: versions 1 and 2.
//   ARequestThatSendsDevicesBackIsAccepted: a read answer's body, with its `devices` and `entities[].device`… note:
//     `entities` is still refused (phase 2); send name, settings, logic and `devices`: accepted, `devices` ignored.
// EntityNodeTests.DevicesAreDerivedFromTheirNodes (Task 3) gains: DefinitionGraph.Devices and Entities' `device`.
// AuthoringAdapterTests.DerivedEntities: each expected entity gains "device": null.
```

```ts
// entity-node.test.ts: withGraphTypes(graph).definition.devices lists the Device nodes' config in node order, and
//   each derived entity has `device` (the key, or null).
// entity-model.test.ts: editorGraph ignores an answer's `devices`; definitionBody sends none.
```

- [ ] **Step 2: Run** `-- --entity-store`, `-- --entity-nodes`, `-- --authoring-adapter` and the two frontend files. Expected: FAIL.

- [ ] **Step 3: Implement.** Update `ClassDefinitionFilesAreUnchanged`'s pinned bytes for the version and the new member, and say in the report exactly what changed in them.

- [ ] **Step 4: Run** every suite (Python in the container too), publish, both smokes. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'A saved entity definition records the devices on its canvas and which device each entity is on.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 6: Home Assistant gets one device per Device node

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionHost.cs` (`CompiledDefinition`, `PrepareAsync`'s payload, `DeviceJson` → `DevicesJson`), `CC/HassSharp/Devices/DefinitionHost.Graph.cs`, `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition`)
- Modify: `CC/entity_bridge.py` (`_async_sync`, `_sync_device`, `_via`, `_async_sync_entity`, `_async_prune`, `_purge`)
- Test: `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/EntityGraphTests.cs`, `tests/python/test_entity_bridge.py`, `tests/python/test_entity_platforms.py`, `tests/packaging/entity_bridge_smoke.py`

**Interfaces:**
- Consumes: `DefinitionGraph.Devices`, the derived entity's `device` (Task 5).
- Produces, the payload the host hands the bridge:

```json
{ "definitionId": "plug1", "name": "Plug one",
  "devices": [{ "key": "plug", "name": "Hall plug", "manufacturer": null, "model": "P1", "swVersion": null,
                "hwVersion": null, "configurationUrl": null, "suggestedArea": null, "via": null }],
  "entities": [{ "uniqueId": "plug1:relay", "key": "relay", "domain": "switch", "…": "…", "device": "plug" }] }
```

  `devices` is ordered so that a device comes after the device it is part of. `via` is null, `{"deviceId": id}`, `{"device": key}` or, for a class definition only, `{"definition": id}`. An entity's `device` is a key in `devices`, or null.
- `CompiledDefinition` gains `IReadOnlyList<DefinitionDevice> Devices` (with `Key` and `Name`).

**Behaviour:**
- **Graph definition.** `devices` are its Device nodes; each entity's `device` is its wire's. Until Task 9 a graph request's `device` (the definition as a device) is added as a device keyed `main`, named after the definition, holding every entity that has no wire.
- **Class definition.** Its device (the class's `SharpDevice` with the document's overrides, as `DeviceJson` builds it today) goes through as one device keyed `main`, named after the definition, with every entity on it. A class that is one `SharpEntity` with no document device has `devices: []` and `device: null`, and keeps the bridge's fallback to the definition's name (that fallback is removed with the class kind in phase 5).
- **Bridge, sync.** Devices are created in the order given, each with identifiers `{(DOMAIN, f"{definition_id}:{key}")}`. `_via`: `{device: k}` is `(DOMAIN, f"{definition_id}:{k}")`; `{definition: d}` is `(DOMAIN, f"{d}:main")`; `{deviceId}` as today; a parent that is not found is no parent. Each entity is synced with its own device's info, or none. After the entities: every registry entry of this definition that is described and is not on its described device is moved there (this covers an entry Home Assistant has disabled, which the sync never adds); then every registry device whose identifier is `(DOMAIN, definition_id)` or `(DOMAIN, f"{definition_id}:<key>")` with a key not described is removed, after any entry still on it is moved off. A sync that fails removes nothing, as today.
- **Bridge, clean-up.** `_async_prune` takes a device identifier's definition id as the text before the first colon. `_purge(definition_id)` removes that definition's registry entries and every device `(DOMAIN, definition_id)` or `(DOMAIN, f"{definition_id}:…")`.

- [ ] **Step 1: Write the failing tests**

```python
# tests/python/test_entity_bridge.py (the fake registries; `definition()` now takes devices=[…] and per-entity device)
#   test_sync_makes_one_device_per_described_device: two devices, three entities (two on `plug`, one on `hub`, and a
#     fourth standalone): identifiers {(DOMAIN, 'plug1:plug')} and {(DOMAIN, 'plug1:hub')}; each entity's device_id.
#   test_sync_moves_an_entity_to_another_device: sync, then sync with relay on `hub`: the same registry entry
#     (same entity_id), device_id now hub's.
#   test_sync_moves_a_disabled_entity_too: the entry is disabled in the registry; after the second sync its device_id
#     is the new device's, and it is still disabled.
#   test_sync_without_its_device_leaves_the_entities_standalone: second sync has devices [] and device None: the
#     entries are kept with device_id None and the device is removed.
#   test_sync_removes_the_device_an_earlier_build_made: the registry starts with device (DOMAIN, 'plug1') holding the
#     entries; after a sync with device `plug`, the entries are on (DOMAIN, 'plug1:plug') and the old device is gone.
#   test_sync_links_a_device_to_another_of_the_definition: via {device: 'hub'}: via_device_id is hub's; the parent is
#     created first whatever the order of the nodes.
#   test_prune_keeps_the_devices_of_known_definitions: known ids ['plug1']; the registry has (DOMAIN, 'plug1:plug'),
#     (DOMAIN, 'gone:main') and (DOMAIN, 'gone'): the first is kept with its entities, the other two are purged.
#   test_remove_purges_every_device_of_the_definition.
#   Existing device tests are moved to the new payload, their subjects kept (a cleared field, the failing sync, via a
#   Home Assistant device, via another definition → (DOMAIN, '<other>:main')).
# tests/python/test_entity_platforms.py (real Home Assistant, in the container): the same three for the real
#   registries: one device per described device; an entity moved between devices keeps its entity_id; prune after a
#   restart keeps a `<id>:<key>` device and its entities.
```

```csharp
// EntityHostTests: `Ordered` pins the class sync JSON with "devices":[{"key":"main","name":"Plug one",…}] and
//   "device":"main" on each entity; RootEntity has "devices":[] and "device":null; DocumentDevice's via {definition}.
// EntityGraphTests
//   TwoDevicesAndAStandaloneEntityReachHomeAssistant: Device nodes plug and hub (hub part of nothing, plug part of
//     hub, declared plug first): the sync's devices are hub then plug; relay "device":"plug", online "device":"hub",
//     bare "device":null.
//   ADeviceDefinitionGroupsItsEntities (the definition's device, until Task 9): devices [{"key":"main","name":<the
//     definition's name>…}], every entity "device":"main".
```

- [ ] **Step 2: Run** `-- --entity-host`, `-- --entity-graph`, `python3 -m unittest discover -s tests/python -k bridge`. Expected: FAIL.

- [ ] **Step 3: Implement** both sides in this one commit (the payload is their contract). Move `tests/packaging/entity_bridge_smoke.py` to the new payload (`devices[0]["name"] == 'Smoke plug'`, the graph sync's `devices == []`).

- [ ] **Step 4: Run** every suite, the Python suite in the container, publish, both smokes. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'Each Device node becomes its own device in Home Assistant, and an entity moved between devices keeps its entity id and history.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 7: Adding, copying and deleting a Device node

**Files:**
- Modify: `FE/src/visual/contract.ts` (`addGraphNode`, `freeKey` use), `FE/src/visual/clipboard.ts` (`pasteSteps`), `FE/src/entities/definitionEdits.ts` (`entityRemoval` stays as it is; nothing asks for a Device node), `FE/src/visual/layout.ts`
- Test: `FE/tests/entity-node.test.ts`, `FE/tests/clipboard.test.ts`, `FE/tests/layout.test.ts` (or the file that tests `tidyLayout`)

**Interfaces:**
- Consumes: `definitionDeviceKeys`, `declaredDevices` (Task 3).

**Behaviour:**
- **A new Device node** is named `Device` with the key `device` (`device_2`, … when taken, by `freeKey` over device keys). It has no `label`.
- **Paste and duplicate.** A copied Device node gets a key no other device has. A copied Device node whose `via: {device: k}` names a device copied with it follows that device's new key; one that names a device not copied keeps `k`. A wire between a copied Device node and a copied Entity node is copied (both ends are in the copy); an Entity node copied without its device arrives standalone.
- **Delete.** A Device node is deleted at once, with its wires; its entities stay as standalone entities (a nameless one then shows `entity.name`, a device that was part of it shows `device.viaUnknown`). No confirmation: nothing is lost that Undo does not bring back.
- **Layout.** `tidyLayout` places a Device node in the column before the first entity it is wired to, with no overlap; entities of one device stay in one band.

- [ ] **Step 1: Write the failing tests**

```ts
// entity-node.test.ts
it('a new Device node is named and keyed', () => {
  // addGraphNode(graph, 'device') twice: config.name 'Device' both, keys 'device' and 'device_2', no label.
})
// clipboard.test.ts
it('a pasted device gets its own key, and a device pasted with it stays part of it', () => {
  // copy hub (key hub), plug (key plug, via {device: 'hub'}) and relay (wired to plug); paste:
  // new keys hub_2, plug_2; plug_2.via is {device: 'hub_2'}; relay's copy is wired to plug_2; the originals are unchanged.
})
it('an entity pasted without its device stands alone', () => { /* no data edge into its Device pin */ })
// layout test
it('a device sits before the entities wired to it', () => { /* x(device) < x(each entity); no two cards overlap */ })
// definitionEdits / EditorAdapter.removal
it('deleting a Device node asks nothing', () => { /* removal(graph, [deviceId]) is undefined */ })
```

- [ ] **Step 2: Run** the files. Expected: FAIL.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** `bun run test`, `bunx tsc --noEmit`. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'A Device node dropped on the canvas arrives named and keyed, and copying devices with their entities keeps them together.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 8: Tests, examples, MCP guidance and docs use Device nodes

Everything that still sends the definition's `device` for a graph, or leans on the `definition.device` marker, moves to Device nodes, so that Task 9 can remove the old form.

**Files:**
- Modify: `tests/fixtures/visual-graph-validation.json` (the cases that carry `definition.device: {}` only to keep nameless hubs valid: give the hub a name, or a Device node where the case is about a device), `tests/Runtime/EntityGraphTests.cs` (`Definition(…, device)` helper and its callers, `ADeviceDefinitionGroupsItsEntities`), `EntityStoreTests.cs` (`ADeviceMarksTheGraph`), `EntityDomainTests.cs`, `EntityToolsTests.cs`, `AuthoringAdapterTests.cs`
- Modify: `FE/tests/*` (`grep -rln "device" FE/tests` and the map's section 4): tests of a graph definition's device move to a Device node; tests of the right panel's Device form are listed in the report and left for Task 9; `ClassDefinitionEditor.test.tsx` is untouched
- Modify: `CC/authoring_examples.json` (`Plug over HTTP`: a Device node `plug`, name `Plug over HTTP`, model `Smart plug`, suggested area `Living room`, wired to `relay` and `power`; the request has no `device`)
- Modify: `CC/llm.py` (`PROMPT`, `SEMANTICS["definitions"]`, `ENTITY_SPECS`, the hints, `node_schema` for a Device node), `docs/custom-entities.md` (its Devices section), `docs/mcp-authoring.md`, `docs/backend/entity-definitions.md`
- Modify: `tests/packaging/entity_bridge_smoke.py` (the graph half gains a Device node with two entities and one standalone)
- Test: `tests/python/test_entity_authoring.py`, `tests/python/test_mcp_adapter.py`, `tests/test_docs_examples.py`

**What the texts say** (the wording is the writer's; these facts are not):
- A device is a `device` node: `config` has `key`, `name` and, where wanted, `manufacturer`, `model`, `swVersion`, `hwVersion`, `configurationUrl`, `suggestedArea`, `via` (`{deviceId}` for a Home Assistant device, `{device}` for another Device node's key).
- An entity is on a device when a data edge goes from the Device node's `entities` output to the Entity node's `device` input; without it the entity stands alone and needs a name.
- A definition may hold several devices and is not a device. The request is `{name, settings, logic: {kind: "graph", graph}}`.
- The stored document and the read answer carry `devices` and each entity's `device`; nobody sends them.
- A class definition still takes `device` (its own section of the docs says so; it goes in phase 5).

- [ ] **Step 1: Write the failing tests**

```python
# tests/python/test_entity_authoring.py
def test_no_graph_example_sends_a_device(self):
    # every graph example: "device" not in definition; the Plug over HTTP example has one node of kind "device"
    # and a data edge from it to each of relay and power.
def test_the_guidance_describes_a_device_as_a_node(self):
    # llm.py's SEMANTICS["definitions"] and docs/custom-entities.md mention the `device` node and its `entities`
    # output, and do not say "Group these entities as a device".
```

`AuthoringAdapterTests.DerivedEntities` expects `relay` and `power` with `"device":"plug"`.

- [ ] **Step 2: Run** them. Expected: FAIL.

- [ ] **Step 3: Convert**, keeping every test's subject. A test whose only subject is the definition-as-device form (the marker, the request's device for a graph) is listed in the report for Task 9 and left as it is.

- [ ] **Step 4: Check the leftovers:** `grep -rn "\"device\": {}\|device: {}\|\"device\":{}" tests custom_components/hass_sharp/frontend/tests` and every graph request in the tests that sends a non-null `device`; paste both lists into the report with, for each line, why it stays until Task 9.

- [ ] **Step 5: Run** every suite (Python in the container too), publish, both smokes. Expected: pass.

- [ ] **Step 6: Commit**

```bash
git commit -m "$(printf 'The examples, the MCP guidance and the custom entities guide describe a device as a Device node wired to its entities.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 9: A definition is no longer a device

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionGenerator.cs` (`Preview`), `CC/HassSharp/Devices/DefinitionGraph.cs` (`Compose`: no marker), `CC/HassSharp/Visual/VisualGraphValidator.cs` (`Definition()`, `entity.name`), `CC/HassSharp/Devices/DefinitionHost.cs` / `EntityApi.cs` (the `main` device of a graph definition), `CC/HassSharp/Devices/EntityApi.cs` (the list and home summaries)
- Modify: `CC/llm.py` (`DEFINITION_PARTS`, `DEFINITION_MARKERS`, `definition_summary`, the hints)
- Modify: `FE/src/visual/contract.ts` (`GraphDefinition`, `hasDevice`), `FE/src/entities/model.ts` (`EditorDefinition`, `editorGraph`, `definitionBody`, `newGraphDefinition`, `DefinitionVia`), `FE/src/entities/definitionEdits.ts` (the Device edits; `withDefinition`), `FE/src/entities/DefinitionPanel.tsx`, `FE/src/entities/adapter.ts` (`overviewPeek: 'Settings'`, `deviceMemory`), `FE/src/entities/DefinitionCard.tsx`, `FE/src/entities/EntityHelp.tsx`, `FE/src/entities/DeleteDefinition.tsx`, `FE/src/views/EntitiesListView.tsx`
- Modify: the tests Task 8 listed
- Test: `tests/Runtime/EntityStoreTests.cs`, `tests/python/test_mcp_adapter.py`, `FE/tests/DefinitionPanel.test.tsx`, `FE/tests/entity-model.test.ts`, `FE/tests/EntitiesListView.test.tsx`

**Behaviour:**
- **Request.** A graph request whose `device` is not null is refused alone, after the name check, as a listed `entities` is: `definition.deviceNotAllowed`, the sentence of Global Constraints, input `device`. `device: null` is accepted and ignored (a read answer sent back has it). A class request keeps `device`. Through MCP the caller gets the sentence, not a schema error; `DEFINITION_PARTS` and every description stop listing `device` for a graph.
- **Marker.** `Compose` injects `definition = {settings}`. `entity.name` depends on the wire alone, on both sides. The `main` device of a graph definition (Task 6) is gone; a class definition keeps its.
- **Summaries.** The list and home answers replace `device` (a boolean) with `devices` (a count: the stored `devices` of a graph definition; 1 or 0 for a class definition). The card's chip follows Global Constraints.
- **Frontend.** The right panel's overview is the problems list and Settings; `graph.definition = {settings}`; `definitionBody` sends no `device`; `withDefinition` does not carry a stale `device` along. The class editor keeps its Device section (`DeviceForm.tsx` stays for it; its "another definition" choices are the class definitions that make a device). Copy that says a definition is or has a device is reworded: the help text, the delete dialog (`with their devices`), the overview's peek label.

- [ ] **Step 1: Write the failing tests**

```csharp
// EntityStoreTests
//   AGraphRequestWithADeviceIsRefused: "device": {"model":"x"} → preview and save answer one error, code
//     definition.deviceNotAllowed, the sentence, inputId "device"; "device": null is accepted.
//   AClassRequestKeepsItsDevice.
//   ComposeGivesNoDeviceMarker: Compose(body)["definition"] has "settings" only.
//   (ADeviceMarksTheGraph is deleted.)
// EntityGraphTests.ADeviceDefinitionGroupsItsEntities becomes the Device-node form (or is deleted if
//   TwoDevicesAndAStandaloneEntityReachHomeAssistant covers it; say which in the report).
```

```python
# test_mcp_adapter.py: entities_save with a device on a graph definition answers the sentence; entities_list gives
#   `devices` as a count; DEFINITION_PARTS has no "device".
```

```tsx
// DefinitionPanel.test.tsx: the overview has Settings and no "Group these entities as a device" switch.
// entity-model.test.ts: newGraphDefinition(...) has no device; editorGraph(...).definition is {settings: []};
//   definitionBody(graph) has no `device`.
// EntitiesListView.test.tsx: a definition with devices: 2 shows "2 devices"; with 1, "Device"; with 0, no chip.
```

- [ ] **Step 2: Run** them. Expected: FAIL.

- [ ] **Step 3: Implement.** Afterwards `grep -rn "hasDevice\|definition\.device\|Group these entities" custom_components/hass_sharp/frontend/src custom_components/hass_sharp/HassSharp` prints only the class editor's use.

- [ ] **Step 4: Run** every suite (Python in the container too), publish, both smokes. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -m "$(printf 'An entity definition is no longer a device itself: devices are Device nodes, and a request that sends a device is told to use them.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

## Rulings

Each is a decision this plan made where the spec left room. Format: what, why, cost if wrong.

1. **The type's id is `device`, as the spec says, and structural types are never embedded in generated source.** `EmbeddedCatalog` embeds a type when its id appears as any string in a graph, and native device triggers say `"device"`; leaving structural types out keeps automations byte-identical without a stranger id. Cost if wrong: one filter; Task 2 pins an automation with a device trigger.
2. **A Device node needs a name (`device.name`), a well-formed key and a well-formed Part of and configuration URL.** The spec lists four device rules; these add `definition.deviceKey`, `device.name`, `device.via` and `device.configurationUrl`, because Home Assistant shows a nameless device as "Unnamed device" and refuses a bad URL only at sync, where the whole definition fails. Cost if wrong: a rule to delete on each side.
3. **`device.viaCycle` is reported on every device of the circle.** Any one of them can break it. Cost if wrong: one comparator.
4. **The derived entity carries `device` (key or null) in the catalog, the stored document and the read answer.** One list answers "which device" for the host, the summaries and MCP. Cost if wrong: the member is derived, so nothing stored by hand depends on it.
5. **Device keys are read-only once saved.** The key is the device's identity in Home Assistant; a changed key makes a new device and drops the old one's area and name overrides. Cost if wrong: one condition in the section.
6. **Deleting a Device node asks nothing.** Its entities stay, and what breaks shows as problems; phase 2 asks only when steps lose their entity. Cost if wrong: one branch in `removal`.
7. **The Entity node's right panel has a Device select** besides the wire. A wire cannot be drawn from the keyboard; the select can be used from it. Cost if wrong: a control to remove.
8. **The definition's device goes through as a device keyed `main`** for a class definition (until phase 5) and for a graph request's `device` (until Task 9), holding every entity without a wire. One shim serves both. Its identifier changes from `<id>` to `<id>:main`, so its device page settings in a development instance are lost once. Cost if wrong: none after phase 5.
9. **`via: {definition}` survives for class definitions only**, resolved to `<other id>:main`. A graph definition has several devices or none, so "another definition" names no device. Cost if wrong: the class editor's Part of offers class definitions only, until phase 5 removes it.
10. **The bridge's fallback to the definition's name stays** for a class that is one entity with no device, until phase 5. Every graph entity is named or on a device by `entity.name`. Cost if wrong: none found.
11. **`DeviceForm.tsx` is not deleted in this phase** (the approved plan said it would be): the class editor still uses it. Its fields and Part of control are shared with the new section. Cost if wrong: a file deleted one phase later.
12. **The summaries' `device` boolean becomes a `devices` count.** A definition can make several. Cost if wrong: one member renamed in three readers.
13. **A graph request's `device: null` is accepted**, unlike `entities`, which is refused whenever the key is present. Read answers carry `device: null` for as long as the class kind shares the document type. Cost if wrong: one condition.

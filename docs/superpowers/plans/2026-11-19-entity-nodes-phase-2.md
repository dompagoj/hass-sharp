# Entity nodes, phase 2: entities as hub nodes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An entity is a node on the canvas. Its state values are input pins on the left, each command is an execution pin on the right with that command's values under it, and its name, key and type are edited in the right panel. The right panel's entity list, and the State, When commanded and When a frame is needed steps, are gone.

**Architecture:** One new catalog kind, `entity`, flagged `declares: "entity"`. Each side derives the definition's entity list from the graph at one place (`VisualNodeCatalog.WithGraphTypes` in C#, `withGraphTypes` in TypeScript); everything downstream already reads that list. One hub node starts several flows, so a flow is named by node and port (`FlowMethod(node, port)` = `Run_<hex(id)>_<hex(port)>`) and `DefinitionRoots` keeps its shape, which leaves the host and the run recorder unchanged. The work is in three stages: the hub is added beside the old kinds (Tasks 1 to 7), every test, example and document moves to it while both forms still work (Tasks 8 to 10), then the old form is removed (Tasks 11 and 12).

**Tech Stack:** C# on .NET 11 (`custom_components/hass_sharp/HassSharp/`, custom test runner in `tests/Runtime/`), SolidJS with TypeScript 7 and vitest 5 (`custom_components/hass_sharp/frontend/`), Python 3 (`custom_components/hass_sharp/*.py`, `tests/python/`). `CC/` below is `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`.

**Spec:** `docs/superpowers/specs/2026-11-19-entity-nodes-design.md` (the `entity` hub, removed kinds, request and stored document, validation). Code map: `docs/superpowers/plans/2026-11-19-entity-nodes-map.md` (bends 1, 4, 5, 8, 11 and "Couplings"). Line numbers here are approximate; find the symbol.

## Global Constraints

- **One catalog, two sides.** `CC/visual_node_catalog.json` is embedded in the C# library and imported by `FE/src/visual/contract.ts`. A catalog edit and the code on both sides that reads it land in one commit. The same holds for a case added to `tests/fixtures/visual-graph-validation.json`: both validators read it, and their messages must be byte-identical.
- **Every commit leaves these green**, run from the repository root:
  - `python3 -m unittest discover -s tests/python`
  - `python3 -m unittest discover -s tests`
  - `dotnet run --project tests/Runtime --disable-build-servers`
  - `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`
  - `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`
  - after `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`: `python tests/packaging/entity_bridge_smoke.py custom_components/hass_sharp` (needs Python.NET; say so in the report if it could not run)
  One runtime suite runs alone with its flag, for example `dotnet run --project tests/Runtime --disable-build-servers -- --entity-nodes`. Record the counts of every suite in the ledger before Task 1.
- **Out of scope.** No `device` kind and no `device` pin on the hub (phase 3; the right panel's Device section stays as it is). No C# entity or device node (phase 4). The class kind is not deleted (phase 5): a class definition must load, save and run exactly as before after every task.
- **Both forms work from Task 1 until Task 11.** A definition may list entities (`entities` in the request) and may declare them with Entity nodes. A graph that uses both forms for the same entity is not a supported case: it gets no rule and no test, and where the code has to choose, the node wins.
- **Pin ids of the hub.** State inputs are the type's state value names (`is_on`, `value`, `brightness`). Execution outputs are `command:<command>` and, for a camera, `frame`. Data outputs are `command:<command>:<value>`. The state flow has no pin; its port name, where one is needed, is `state`.
- **`graphVersion` stays 5.** Only the stored document's `version` goes to 2, in Task 11.
- **Generated-source pins and catalog fixtures.** When `--generated-source-pins` fails only because the embedded catalog changed, re-pin with `dotnet run --project tests/Runtime --disable-build-servers -- --describe-generated-source-pins` and say so in the commit body. When a frontend test fails because `tests/fixtures/visual-rich-catalog.json` lacks a kind, regenerate it with `-- --write-node-library-fixture`.
- **Fixture messages.** For a new case in `visual-graph-validation.json`, write the expected `messages` by hand from the copy below, then check them with `dotnet run --project tests/Runtime --disable-build-servers -- --describe-visual-graph=<path to a file of cases>`. A message must not end with a full stop and must not use the words the fixture test's jargon check refuses (`literal`, `unset`, `operand`, `payload`).
- **Exact copy.** User-facing strings are hardcoded English. `{Name}` is the entity's name, else its key, else `Entity`. `{Command}` is the command's catalog label (`Turn on`). `{Value}` is a pin's catalog label (`Brightness`). `{Step}` is a step as `Words.StepName` names it.
  - Kind: label `Entity`; description `One entity this definition creates in Home Assistant. Connect values to its state on the left; steps connected to a command on the right run when Home Assistant sends it.`
  - Camera execution pin: `When a frame is needed`
  - Run names: `{Name}: State`, `{Name}: {Command}`, `{Name}: When a frame is needed`
  - `definition.entityKey`, blank: `Give this entity a key, such as power`
  - `definition.entityKey`, malformed (existing words, kept): `Entity key {key} is not a key: start it with a lowercase letter and use only lowercase letters, digits and underscores, up to 40 characters`
  - `definition.duplicateEntity` on a node: `The key {key} is already used by {Name of the first}: give this entity its own`
  - `definition.entityDomain` on a node: `{domain} is not an entity type: choose one from the list`
  - `entity.name`: `Name this entity: it has no device, so Home Assistant shows it by its own name`
  - `entity.partialState`: `{Name} has a value for {Given} but none for {Missing}: connect or set {Missing} too` (`{Given}`: the labels of the values it has, in pin order, joined by `, `)
  - `scope.stateSource`: `{Value} of {Name} cannot read {Step}, which runs in a flow: use a Set state step after {Step} instead`
  - `scope.flow`: `{Step} reads {Value} from {Command} of {Name} but also runs from {Other}, where there is no {Value}` (`{Other}`: `{Command} of {Name}` for a hub pin, else the root step's name)
  - `command.unhandled` on a node: `{Name} does nothing on {Command}: connect a step to {Command}`
  - `camera.no_frame` on a node (existing words, kept): `{Name} never shows a frame: add a Show frame step for it`
  - `definition.entitiesRequired` from Task 11: `Add an Entity node: an entity definition creates the entities on its canvas, and has none`
  - `definition.entitiesNotAllowed` for a graph, from Task 11: `Entities are Entity nodes in the graph now: leave entities out and add one Entity node for each`
  - A version 1 graph document, from Task 11: `saved by an earlier development build; delete it and build it again`
  - Inspector: header input `aria-label="Entity name"`, placeholder `Not set`; card summary `{Type label} · {key}`; section `Run history` with switches `Record state updates`, `Record commands`, `Record frames`; every other field keeps the label, placeholder and hint it has in `FE/src/entities/EntityRows.tsx` today.
  - Delete dialog: title `Delete this entity?` (several: `Delete these entities?`), body `N steps use this entity. They will show a problem until you change or remove them.` (several: `these entities`; one step: `1 step uses`), buttons `Delete` and `Cancel`.
  - Picker hint when the definition has no entity, from Task 11: `Add an Entity node from the step library first.`
- **Commits.** One per task. The message is one sentence saying what the user now gets, ending with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **A light's Brightness is wired before On.** The wire is accepted and the node shows `entity.partialState` on On; `connectData` must not refuse the wire because it makes a new problem. Tests in Task 5 (`connectData` test and the fixture case).
2. **Two commands share a later step.** Turn on and Turn off of one switch both lead to the same Log step: valid. The same Log reading Brightness from Turn on of a light: `scope.flow`, and the editor refuses that wire. Tests in Task 5.
3. **A pasted or duplicated Entity node.** It gets a key no other entity has, and the pasted steps that named the copied entity follow the new key; steps outside the paste do not change. Test in Task 7.
4. **A type change on the node.** Values and wires of pins the new type does not have are removed, with the notice that says what was removed; steps that name the entity are refitted; no `Unknown input` problem is left behind. Test in Task 7.
5. **An old document.** A version 1 graph document fails to load with the one sentence and can be deleted; a version 1 class document still loads and runs. Tests in Task 11.

---

### Task 1: An Entity node declares an entity

A node of the new kind `entity` adds an entity to the definition. It has no pins and starts no flow yet: the entity exists in Home Assistant, and a Set state step can name it.

**Files:**
- Modify: `CC/visual_node_catalog.json` (new `entity` entry, placed before `entity_state` so it leads the Entity group)
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs` (`WithGraphTypes` about l.158; new `Declares`, `DeclaredEntities`, `EntityDescription`)
- Modify: `CC/HassSharp/Devices/DefinitionGraph.cs` (new `Entities`)
- Modify: `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition` about l.64-95; the save path)
- Modify: `CC/HassSharp/Devices/DefinitionDocument.cs` (`Check` about l.34-69)
- Modify: `CC/HassSharp/Visual/VisualGraphValidator.cs` (`Definition()` about l.633-649)
- Modify: `CC/HassSharp/Visual/VisualGenerator.cs` (root loop about l.345-373 and the root methods)
- Modify: `FE/src/visual/contract.ts` (`NodeSpec`, `withGraphTypes` l.383, `checkDefinition` about l.1504, `addGraphNode` about l.1599; new `declaredEntities`, `entityDescription`, `definitionCatalog`)
- Modify: `FE/src/visual/nodeSummary.ts:304`, `FE/src/visual/trace/traceWords.ts:19`, `FE/src/components/TriggerSummary.tsx:18-26`, `FE/src/entities/model.ts` (`editorGraph`)
- Create: `tests/Runtime/EntityNodeTests.cs` (`[RuntimeSuite("--entity-nodes", Order = 11)]`), `FE/tests/entity-node.test.ts`
- Test: also `tests/Runtime/EntityGraphTests.cs` (one end-to-end test), `FE/tests/palette.test.tsx`

**Interfaces:**
- Produces (catalog): `nodes.entity` =
  ```json
  { "label": "Entity", "category": "Entity", "description": "<the copy above>", "root": true, "declares": "entity",
    "documents": ["entity"], "minGraphVersion": 5, "argumentStorage": "arguments",
    "config": {
      "key": { "type": "string", "label": "Key", "description": "What the entity is called inside this definition and in its unique id." },
      "domain": { "type": "string", "required": true, "default": "binary_sensor", "label": "Type", "description": "The kind of entity, such as sensor or switch." },
      "name": { "type": "string", "label": "Name", "description": "The name Home Assistant shows for it." },
      "icon": { "type": "string", "label": "Icon", "description": "A Material Design icon, such as mdi:motion-sensor." },
      "deviceClass": { "type": "string", "label": "Device class", "description": "A Home Assistant device class, for example motion." },
      "entityCategory": { "type": "string", "enum": ["config", "diagnostic"], "label": "Category", "description": "Config and diagnostic entities are listed apart on the device's page." },
      "enabledByDefault": { "type": "boolean", "default": true, "label": "Enabled by default", "description": "Off: Home Assistant adds it turned off, to be turned on in its settings." },
      "options": { "type": "object", "default": {}, "label": "Options", "description": "The settings of its type, such as a sensor's unit." },
      "history": { "type": "object", "default": {}, "label": "Run history", "description": "Which of its flows record their runs: state, commands, frame." },
      "arguments": { "type": "object", "default": {}, "label": "State values", "description": "The state values set on the node instead of connected." }
    },
    "inputs": {}, "outputs": {}, "execution": [], "runtimeVersions": [5],
    "keywords": ["sensor", "binary sensor", "switch", "light", "cover", "button", "number", "select", "text", "camera", "state", "command"] }
  ```
  `key` carries no `required` and no `pattern`: Task 5's rule gives the one message for a blank or malformed key.
- Produces (C#):
  - `internal static bool VisualNodeCatalog.Declares(JsonNode? spec, string what)`: the spec's `declares` equals `what`.
  - `internal static JsonObject VisualNodeCatalog.EntityDescription(JsonObject node)`: a deep copy of `node["config"]` without `history` and `arguments` (the shape `SharpDomains.Create` reads); an empty object when the config is not an object.
  - `internal static IEnumerable<(string NodeId, JsonObject Entity)> VisualNodeCatalog.DeclaredEntities(JsonObject graph, JsonObject catalog)`: one per node whose catalog entry declares an entity, in the order of `graph["nodes"]`; nothing for a graph of the wrong shape.
  - `internal static JsonArray DefinitionGraph.Entities(JsonObject composed, VisualCatalogSnapshot snapshot)`: the derived list, cloned: `DefinitionItems(WithGraphTypes(snapshot.Root, composed), "entities")`.
- Produces (TypeScript): `NodeSpec.declares?: 'entity'`; `entityDescription(node: GraphNode): Record<string, unknown>`; `declaredEntities(graph: unknown, catalog?: VisualCatalogSnapshot): { nodeId: string; entity: Record<string, unknown> }[]`; `definitionCatalog(catalog: VisualCatalogSnapshot, graph: unknown): VisualCatalogSnapshot` (the catalog as it is when it already has a `definition`, else with the graph's definition and the derived entities).

**Behaviour:**
- **The derived list**, identically on both sides, written to `merged.definition.entities` by `WithGraphTypes` / `withGraphTypes` whenever the graph has a `definition`: the listed entities (`graph.definition.entities`, absent means none) whose key no node declares, in their order, followed by `DeclaredEntities` in node order. The existing "first of each key" reading (`DefinitionItems` / `definitionItems`) is unchanged.
- `Definition()` and `checkDefinition` accept a `definition` without `entities`; when present it must still be an array.
- `CompileDefinition` gives the host the derived list: `Entities = DefinitionGraph.Entities(graph, snapshot)` for a graph definition (a class definition is untouched).
- **Save stores the derived list.** In the API's save path, a graph body is saved with `Entities` replaced by `DefinitionGraph.Entities(DefinitionGraph.Compose(body), snapshot)`. Saving a read answer back is then idempotent (a listed entry of a node's key is replaced by the node's).
- `DefinitionDocument.Check`: `definition.entitiesRequired` counts listed and declared entities together. Its key, duplicate, domain, field and option checks run on listed entries only, and skip a listed entry whose key a node declares.
- The generator emits no action and no method for a root whose spec declares an entity (Task 3 adds its flows); it must not throw `No action for the root kind entity`. `CompileDefinition` adds no `VisualTrigger` for it.
- Frontend: replace the three hand-written "lift `graph.definition` into the catalog" expressions (`contract.ts` about l.1599, `nodeSummary.ts:304`, `traceWords.ts:19`) with `definitionCatalog`, and make `definitionNames` in `TriggerSummary.tsx` read the entities from `definitionCatalog(catalog ?? nodeCatalog, graph).definition`. `editorGraph(body)` drops the listed entries whose key an Entity node of the graph declares, so the right panel's list does not repeat them.

- [ ] **Step 1: Write the failing tests**

`tests/Runtime/EntityNodeTests.cs` (copy `N` and `Check` from `EntityGraphRuntimeTests.cs` or make them shared; follow that file's shape for the suite method):

```csharp
static JsonObject Hub(string id, string key, string domain, JsonObject? more = null)
{
    var config = new JsonObject { ["key"] = key, ["domain"] = domain, ["name"] = key, ["options"] = new JsonObject(), ["history"] = new JsonObject(), ["arguments"] = new JsonObject() };
    foreach (var (name, value) in more ?? []) config[name] = value?.DeepClone();
    return new JsonObject { ["id"] = id, ["kind"] = "entity", ["label"] = "", ["config"] = config };
}
static JsonObject Graph(JsonArray? listed, params JsonObject[] nodes)
{
    var definition = new JsonObject { ["settings"] = new JsonArray() };
    if (listed != null) definition["entities"] = listed;
    return new JsonObject { ["version"] = 5, ["name"] = "Nodes", ["nodes"] = new JsonArray(nodes), ["edges"] = new JsonArray(), ["definition"] = definition };
}

static void NodesDeclareEntities()
{
    var graph = Graph(null, Hub("a", "door", "binary_sensor"), Hub("b", "power", "sensor", new() { ["options"] = new JsonObject { ["unit"] = "W" } }));
    var declared = VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).ToList();
    Check(string.Join(",", declared.Select(d => d.NodeId + "=" + d.Entity["key"])) == "a=door,b=power", "nodes declare entities in node order");
    Check(declared[1].Entity["options"]!["unit"]!.GetValue<string>() == "W" && declared[1].Entity["history"] == null && declared[1].Entity["arguments"] == null,
        "an entity's description is its node's settings without history and arguments");
    var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, graph);
    Check(string.Join(",", VisualNodeCatalog.DefinitionItems(merged, "entities").Select(e => e["key"])) == "door,power", "the catalog of the graph lists them");
}

static void ANodeReplacesAListedEntityOfItsKey()
{
    var listed = new JsonArray(new JsonObject { ["key"] = "old", ["domain"] = "switch" }, new JsonObject { ["key"] = "door", ["domain"] = "switch" });
    var merged = VisualNodeCatalog.WithGraphTypes(VisualNodeCatalog.Root, Graph(listed, Hub("a", "door", "binary_sensor")));
    var entities = VisualNodeCatalog.DefinitionItems(merged, "entities");
    Check(string.Join(",", entities.Select(e => e["key"] + ":" + e["domain"])) == "old:switch,door:binary_sensor", "listed first, a node's entity in place of the listed one of its key");
}

static void AGraphOfTheWrongShapeDeclaresNothing()
{
    foreach (var graph in new JsonObject[] { new(), new() { ["nodes"] = 3, ["definition"] = new JsonObject() }, new() { ["nodes"] = new JsonArray(1, "x", new JsonObject { ["kind"] = "entity", ["config"] = 5 }), ["definition"] = new JsonObject() } })
        Check(VisualNodeCatalog.DeclaredEntities(graph, VisualNodeCatalog.Root).All(d => d.Entity.Count == 0), "no entity is read from a malformed graph");
}
```

In `tests/Runtime/EntityGraphTests.cs`, added to the list in `Run()`, written with that file's `Rig` and helpers as the neighbouring tests are:

```csharp
// AnEntityNodeCreatesItsEntity: a definition whose request has no "entities" and whose graph is
//   Hub("door", "door", "binary_sensor"), a trigger, and Set state (entity "door", is_on true) after it.
// Assert, in this order:
//   - preview is valid and reports no definition.entitiesRequired;
//   - after save, the read answer's "entities" is [{ key: "door", domain: "binary_sensor", name: "door", ... }];
//   - the home was asked to sync the unique id "<id>:door";
//   - running the trigger sets it: Is(snapshot, "is_on", true);
//   - saving the read answer back unchanged answers the same "entities".
// APreviewWithoutAnyEntityAsksForOne: no "entities", no Entity node → the one error has code definition.entitiesRequired.
```

`FE/tests/entity-node.test.ts`:

```ts
import { expect, it } from 'vitest'
import { addGraphNode, declaredEntities, definitionCatalog, nodeCatalog, validateGraph, withGraphTypes, type Graph } from '../src/visual/contract'
import { updateNode, type VisualGraph } from '../src/visual/graph'
import { editorGraph } from '../src/entities/model'

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const empty = (entities: Record<string, unknown>[] = []): VisualGraph => ({ version: 5, name: 'Nodes', nodes: [], edges: [], definition: { entities, settings: [] } as never })
const add = (graph: Graph, kind: string, id: string, config: Record<string, unknown> = {}) => {
  const added = addGraphNode(graph, kind, id, nodeCatalog)
  return updateNode(added, id, { config: { ...added.nodes.at(-1)!.config, ...config } })
}

it('an Entity node declares an entity', () => {
  let graph = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'entity', 'b', { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  const declared = declaredEntities(graph, nodeCatalog)
  expect(declared.map(d => [d.nodeId, d.entity.key])).toEqual([['a', 'door'], ['b', 'power']])
  expect(declared[1].entity).toMatchObject({ key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  expect(declared[1].entity).not.toHaveProperty('history')
  expect(declared[1].entity).not.toHaveProperty('arguments')
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => e.key)).toEqual(['door', 'power'])
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
})

it('a node replaces a listed entity of its key, and a definition without a list is one', () => {
  const graph = add(empty([{ key: 'old', domain: 'switch' }, { key: 'door', domain: 'switch' }]), 'entity', 'a', { key: 'door', domain: 'binary_sensor' })
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => `${e.key}:${e.domain}`)).toEqual(['old:switch', 'door:binary_sensor'])
  const bare = { ...graph, definition: { settings: [] } }
  expect(definitionCatalog(nodeCatalog, bare).definition!.entities.map(e => e.key)).toEqual(['door'])
  expect(validateGraph(bare).some(d => d.code === 'definition.invalid')).toBe(false)
})

it('a new Set state step chooses a node\'s entity', () => {
  const graph = add(add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }), 'set_entity_state', 'set')
  expect(graph.nodes.at(-1)!.config.entity).toBe('door')
})

it('the editor does not list an entity that a node declares', () => {
  const node = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }).nodes[0]
  const body = { name: 'Nodes', device: null, settings: [], entities: [{ key: 'door', domain: 'binary_sensor' }, { key: 'old', domain: 'switch' }], logic: { kind: 'graph', graph: { version: 5, name: 'Nodes', nodes: [node], edges: [] } } }
  expect((editorGraph(body as never).definition as never as { entities: { key: string }[] }).entities.map(e => e.key)).toEqual(['old'])
})
```

In `FE/tests/palette.test.tsx`, phase 1's test of quick-add's first option names `label('entity_state')`; it becomes `label('entity')`.

- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --entity-nodes` and `(cd custom_components/hass_sharp/frontend && bun run test tests/entity-node.test.ts tests/palette.test.tsx)`. Expected: FAIL. C# does not compile (`DeclaredEntities` is not defined); the frontend fails on the missing exports and on `'entity'` not being a kind.

- [ ] **Step 3: Implement** the catalog entry and the behaviour above. If a catalog contract test (`--visual-graph`, `--catalog-wording`, `FE/tests/visual-contract.test.ts`, `tests/python/test_entity_contract.py`) pins the keys a node spec may have, add `declares` there.

- [ ] **Step 4: Run** every suite of Global Constraints. Expected: pass. The new kind is in the step library of an entity definition and absent from an automation's.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(printf 'An Entity node on the canvas adds an entity to the definition, which steps can name like a listed one.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 2: The Entity node has its type's pins

**Files:**
- Modify: `CC/visual_node_catalog.json` (`nodes.entity` gains three flags; `entityDomains.camera` gains `frame`)
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs` (`ExecutionPorts` l.281, `DataOutputs` l.301, `CommandOutputs` l.374, `DataInputs` l.403)
- Modify: `FE/src/visual/contract.ts` (`executionPorts` l.171, `executionLabel` l.185, `dataOutputs` l.502, `commandOutputs` about l.569, `dataInputs` l.596, `NodeSpec`, `OutputSpec`, `EntityDomainSpec`)
- Modify: `FE/src/visual/flow/flowModel.ts` (`buildNodeView`, the right column about l.299-304)
- Modify: `tests/fixtures/visual-graph-validation.json`
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`

**Interfaces:**
- Consumes: `nodes.entity` and `Hub` / `add` helpers of Task 1.
- Produces (catalog): on `nodes.entity`: `"dynamicInputs": "declared"`, `"dynamicOutputs": "declared"`, `"dynamicExecution": "declared"`. On `entityDomains.camera`: `"frame": { "label": "When a frame is needed" }`. A type has the `frame` pin exactly when its row has `frame`.
- Produces (both sides, same results), all read from the node's own `config.domain`, never by looking its key up:
  - inputs: one per state value of the type, in catalog order, with the value's `types` and `label`, every one `required: false`;
  - execution ports: `command:<command>` per command in catalog order, then `frame` when the type has it; none for an unknown type;
  - data outputs: for each value of each command, `command:<command>:<value>` with the type and label `commandOutputs` gives that value today, `pointer: "/data/<value>"`, and `group: "command:<command>"`. `group` is layout only: it is not a scope.
  - `executionLabel(node, port)`: the command's label, or the type's `frame.label`.
- Produces (TypeScript types): `OutputSpec.group?: string`, `EntityDomainSpec.frame?: { label: string }`.

**What it renders:** `buildNodeView` puts each data output under the execution pin it belongs to: the condition becomes `scopeOf(i) === port || outputs[i].group === port`. A light's right column reads: Turn on, Brightness, Turn off.

- [ ] **Step 1: Write the failing tests**

`tests/Runtime/EntityNodeTests.cs`:

```csharp
static void TheNodeHasItsTypesPins()
{
    var light = Hub("l", "lamp", "light");
    Check(string.Join(",", VisualNodeCatalog.DataInputs(light).Select(p => p.Key)) == "is_on,brightness", "a light's state values are its inputs");
    Check(VisualNodeCatalog.DataInputs(light).All(p => p.Value?["required"]?.GetValue<bool>() == false), "every state input is optional as a pin");
    Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(light)) == "command:turn_on,command:turn_off", "one execution pin per command");
    var outputs = VisualNodeCatalog.DataOutputs(light);
    Check(string.Join(",", outputs.Select(p => p.Key)) == "command:turn_on:brightness", "a command's values are outputs");
    Check(outputs["command:turn_on:brightness"]!["pointer"]!.GetValue<string>() == "/data/brightness" && outputs["command:turn_on:brightness"]!["group"]!.GetValue<string>() == "command:turn_on", "an output points into its command's data and is grouped under it");
    Check(string.Join(",", VisualNodeCatalog.ExecutionPorts(Hub("c", "cam", "camera"))) == "frame", "a camera has the frame pin");
    Check(VisualNodeCatalog.ExecutionPorts(Hub("s", "t", "sensor")).Length == 0 && VisualNodeCatalog.DataOutputs(Hub("s", "t", "sensor")).Count == 0, "a sensor has inputs only");
    var unknown = Hub("u", "u", "nothing");
    Check(VisualNodeCatalog.DataInputs(unknown).Count == 0 && VisualNodeCatalog.ExecutionPorts(unknown).Length == 0, "an unknown type has no pins");
    Check(VisualNodeCatalog.DataInputs(Hub("d", "", "light")).Count == 2, "the pins do not wait for a key");
}
```

`FE/tests/entity-node.test.ts`:

```ts
it('the node has the pins of its type', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' }).nodes[0]
  expect(Object.keys(dataInputs(light))).toEqual(['is_on', 'brightness'])
  expect(Object.values(dataInputs(light)).every(input => input.required === false)).toBe(true)
  expect(executionPorts(light)).toEqual(['command:turn_on', 'command:turn_off'])
  expect(executionLabel(light, 'command:turn_on')).toBe('Turn on')
  expect(dataOutputs(light)['command:turn_on:brightness']).toMatchObject({ pointer: '/data/brightness', group: 'command:turn_on' })
  const camera = add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera' }).nodes[0]
  expect(executionPorts(camera)).toEqual(['frame'])
  expect(executionLabel(camera, 'frame')).toBe('When a frame is needed')
  expect(Object.keys(dataInputs({ ...light, config: { ...light.config, key: '' } }))).toEqual(['is_on', 'brightness'])
})

it('a value set on the node is kept under arguments, and one can be connected', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'entity_is', 'is', { entity: { kind: 'literal', type: 'entity_id', value: 'binary_sensor.motion' } })
  graph = connectData(graph, 'is', 'value', 'l', 'is_on')
  expect(graph.edges.some(e => e.kind === 'data' && e.target === 'l' && e.targetPort === 'is_on')).toBe(true)
  const set = setInput(graph.nodes[0], 'brightness', { kind: 'literal', type: 'number', value: 128 })
  expect(set.config.arguments).toEqual({ brightness: { kind: 'literal', type: 'number', value: 128 } })
  expect(set.config).not.toHaveProperty('brightness')
})

it('a step can be connected to a command, and the card lists each command with its values under it', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'log', 'log')
  graph = connectNodes(graph, 'l', 'log', 'command:turn_on')
  expect(graph.edges.some(e => e.source === 'l' && e.target === 'log' && e.sourcePort === 'command:turn_on')).toBe(true)
  // Build the view as tests/entity-roots.test.ts does and read the right column's labels, top to bottom.
  expect(rightColumn(graph, 'l')).toEqual(['Turn on', 'Brightness', 'Turn off'])
})
```

`setInput`'s exact signature is in `contract.ts`; call it as `NodeConfig.tsx` does. `rightColumn` is a three-line local helper over `buildNodeView`.

`tests/fixtures/visual-graph-validation.json`, three cases, each on the skeleton of an existing case whose graph has a `definition` (give the Entity nodes a `name`, so that Task 5's rule leaves them valid):

| name | graph | expects |
|---|---|---|
| `an Entity node takes its state from a value step and runs a step on a command` | switch `relay`; Has state (`entity_is`) → `is_on`; execution `command:turn_on` → Log, `command:turn_off` → a second Log | `valid: true`, `noDiagnostics: true` |
| `a state input of an Entity node takes only its value's type` | binary sensor `door`; a Constant of type string → `is_on` | `valid: false`, `nodeId` the node, `inputId: "is_on"` |
| `an Entity node refuses a value its type does not have` | binary sensor `door` with `arguments: { brightness: <number 5> }` | `valid: false`, `nodeId` the node, `contains: "Unknown input brightness"` |

- [ ] **Step 2: Run** the runtime suite's `--entity-nodes` and `--visual-graph` flags (`--visual-graph` is the fixture's suite) and `bun run test tests/entity-node.test.ts tests/visual-contract.test.ts`. Expected: FAIL: no inputs, no execution ports, the wires are refused.

- [ ] **Step 3: Implement.** Add a `declared` branch beside the existing `entity`, `command` and `branches` branches in the six functions; factor the per-value output out of `CommandOutputs` / `commandOutputs` so both the old kind and the hub use it.

- [ ] **Step 4: Run** every suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'An Entity node shows its state values as inputs and each command as an output that steps can be connected to.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 3: An Entity node's flows run

The node works out its entity's state from its inputs, and runs the steps connected to a command or to the frame pin.

**Files:**
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs` (new `FlowMethod`, beside `EffectiveTriggerMethod` l.278)
- Modify: `CC/HassSharp/Devices/DefinitionGraph.cs` (new `HubFlow`, `HubFlows`; `Roots` l.53-77)
- Modify: `CC/HassSharp/Visual/VisualGenerator.cs` (`RootActions` l.41-46, root loop l.345-373, root methods)
- Modify: `CC/HassSharp/Visual/VisualRuntime.cs` (`RunAsync`, `Walk`, `EntityKinds`, `EntityValues`, `InputSpecs`, `OutputSpecs`, `Read`)
- Modify: `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition` triggers)
- Test: `tests/Runtime/EntityGraphRuntimeTests.cs`, `tests/Runtime/EntityNodeTests.cs`, `tests/Runtime/EntityGraphTests.cs`

**Interfaces:**
- Consumes: Task 2's pins.
- Produces:
  - `public static string VisualNodeCatalog.FlowMethod(JsonObject node, string port)` = `EffectiveTriggerMethod(node) + "_" + Convert.ToHexString(Encoding.UTF8.GetBytes(port))`.
  - `internal sealed record HubFlow(string NodeId, string Port, string Method, string Kind, string Key, string? Command, string Label, bool TrackHistory)`; `Kind` is `state`, `command` or `frame`.
  - `internal static IReadOnlyList<HubFlow> DefinitionGraph.HubFlows(JsonObject node, JsonObject graph, VisualCatalogSnapshot snapshot)`, in this order:
    - `state`, when at least one state input is supplied (a data wire into it, or a value under `config.arguments` whose `kind` is not `unset`): `TrackHistory` = `config.history.state`, default false;
    - `command:<command>`, for each execution port of a command that has an outgoing execution wire: `TrackHistory` = `config.history.commands`, default true;
    - `frame`, when that port has an outgoing wire: `TrackHistory` = `config.history.frame`, default false.
    `Label` is the run name of Global Constraints.
  - `VisualRuntime.RunAsync(string root, string? port = null)`.
- `DefinitionGraph.Roots` keeps its record. Hub flows are added first, then the old kinds with `TryAdd`: `States` and `StateEntities[method] = key` for a `state` flow, `Commands["<key>:<command>"] = method`, `Frames[key] = method`.

**Behaviour:**
- **Generator.** For each hub flow, one `AutomationAction(nameof(<Method>), …)` and one method `public Task <Method>() => run("<node id>", "<port>");`. `state`: mode `Restart`, `Startup: true`, entities from `StateDependencies.Of(graph, nodeId, snapshot)`. `command`: `Queued`. `frame`: `Single`. `Manual` false for all. A hub with no flow emits nothing; a graph whose only root is such a hub must still generate a class that compiles (test).
- **`CompileDefinition`**: one `VisualTrigger(flow.NodeId, flow.Label, flow.TrackHistory)` per flow, keyed by `flow.Method`.
- **Runtime**, `RunAsync(root, port)` when the root's spec declares an entity:
  - `state`: what `Walk` does for `entity_state` today, for the hub's own `config.key`: prepare the pure inputs, read the supplied values (from the wire, else from `config.arguments`; a value that is not supplied is left out, not sent as null), and on failure mark the entity unavailable with the reason; on success `SetState` then `SetAvailable(true)`.
  - `command:<command>`: record the node as reached, give each `command:<command>:<value>` output its value from the command's data (`/data/<value>`, the catalog default when the command came without it, absent otherwise, as `Command(id, scope)` does today), then walk the step after that port.
  - `frame`: walk the step after `frame`.
  - A step that reads a command value which this run did not set gets today's `AbsentCommandValue` sentence, keyed by node and port.
- A command pin with no wire has no flow: the host keeps answering `has no flow for`, as for a missing When commanded today.

- [ ] **Step 1: Write the failing tests**

`tests/Runtime/EntityNodeTests.cs`:

```csharp
static void FlowsAreNamedByNodeAndPort()
{
    var hub = Hub("l", "lamp", "light");
    Check(VisualNodeCatalog.FlowMethod(hub, "state") == "Run_6C_7374617465", "a flow's method is the node's and the port's, in hex");
    Check(VisualNodeCatalog.FlowMethod(hub, "command:turn_on") != VisualNodeCatalog.FlowMethod(hub, "command:turn_off"), "each port has its own");
}
```

`tests/Runtime/EntityGraphRuntimeTests.cs`, with that file's `N`, `D`, `E`, `Lit`, `Compiled`, `FakeDefinition`. Add `Hub` (as in Task 1, with a `values` parameter that fills `arguments`), `static JsonObject Flow(string hub, string port, string target) { var wire = E(hub, target); wire["sourcePort"] = port; return wire; }`, a graph builder without listed entities, and an overload `compiled.Run(string root, string port, FakeDefinition definition)` that invokes `FlowMethod`:

```csharp
// AnEntityNodeWorksOutItsState
//   Hub("door", "door", "binary_sensor") with is_on wired from a Constant true:
//   Run("door", "state") → definition.Calls ends with "state door is_on=bool:true", "available door yes".
// ASetValueOnTheNodeIsItsState
//   Hub light, arguments { is_on: Lit("boolean", true) }, nothing for brightness:
//   Calls has "state lamp is_on=bool:true" and no "brightness" in it.
// AFailingValueMakesTheEntityUnavailable
//   is_on wired from a Custom C# value node that throws: Calls ends with "available door no: …" and no "state door".
// ACommandRunsTheStepsOnItsPin
//   Hub switch "relay"; Flow("relay", "command:turn_on", "set_on"); set_on = Set state relay is_on true;
//   Flow("relay", "command:turn_off", "set_off"). Run("relay", "command:turn_on") → "state relay is_on=bool:true";
//   Run("relay", "command:turn_off") → "state relay is_on=bool:false".
// ACommandsValueIsReadOnItsFlow
//   Hub light; Flow(lamp, "command:turn_on", "set"); D("lamp", "set", "brightness", "command:turn_on:brightness").
//   Run with command data { brightness: 40 } → "state lamp … brightness=…40"; with no data → the catalog default 255.
// AFrameRunsTheStepsOnItsPin
//   Hub camera; Flow("cam", "frame", "show"): the Show frame step runs (as the camera_frame test beside it asserts).
// TheRootsOfANodeAreItsWiredFlows
//   DefinitionGraph.Roots(graph): States == [FlowMethod(door, "state")], StateEntities[that] == "door",
//   Commands == { "relay:turn_on": …, "relay:turn_off": … }, a switch with turn_off unwired has no "relay:turn_off".
//   HubFlows(light with nothing supplied and nothing wired) is empty.
// AGraphOfANodeWithoutFlowsCompiles
//   One Hub button, nothing wired: Compiled.Of(graph, …) succeeds and the class has no Run_ method for it.
// HistoryFollowsTheNodesSwitches
//   HubFlows: state false / command true / frame false by default; history { state: true, commands: false } flips them.
```

`tests/Runtime/EntityGraphTests.cs`, end to end with its `Rig`: `ASwitchNodeRunsItsCommandFlows` (the hub form of `ASwitchRunsItsCommandFlows`: the home commands `turn_on`, the entity's snapshot `Is(…, "is_on", true)`, and the run is recorded under the name `Relay: Turn on`), and `ACommandPinWithoutStepsFails` (the hub form of `ACommandWithoutAFlowFails`, same error text).

- [ ] **Step 2: Run** the three suites' flags. Expected: FAIL: `FlowMethod` is not defined.

- [ ] **Step 3: Implement.** Keep `RootActions` for the old kinds; a hub's actions come from `HubFlows`. Add `entity` to `EntityKinds`. `InputSpecs`/`OutputSpecs` and `EntityValues` must read the hub's pins (`dynamicInputs == "declared"`) and its values through `argumentStorage`.

- [ ] **Step 4: Run** every suite, then publish and run the packaging smoke. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'An Entity node sets its entity from the values connected to it and runs the steps connected to each command.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 4: A run says which of the entity's flows it was

All flows of one hub share the trigger node id; the trace and the run list tell them apart.

**Files:**
- Modify: `CC/HassSharp/Visual/VisualTrace.cs` (`Create`, the header about l.469, the size about l.135)
- Modify: `CC/HassSharp/Visual/VisualRuntime.cs` (`RunAsync` passes the port)
- Modify: `CC/llm.py` (the trace view's keys about l.637)
- Modify: `FE/src/types.ts` (the trace type about l.465), `FE/src/visual/contract.ts` (new `flowMethod`), `FE/src/components/TriggerSummary.tsx` (`definitionNames`)
- Test: `tests/Runtime/VisualTraceTests.cs`, `tests/Runtime/EntityGraphTests.cs`, `FE/tests/entity-node.test.ts`, the frontend test that covers `runName` (find it with `grep -rl runName FE/tests`), the Python test that covers the trace view (`grep -rln rootNodeId tests/python`)

**Interfaces:**
- Consumes: `FlowMethod`, `HubFlow.Label`.
- Produces: trace header field `"rootPort"`, written only when the run has a port, so an automation's trace is byte-for-byte what it was; `RunTrace.rootPort?: string`; `flowMethod(node: GraphNode, port: string): string` (TypeScript mirror of `FlowMethod`); `definitionNames(graph, catalog).roots` also maps each hub flow's method to its run name, for every execution port of the hub and for `state`.

- [ ] **Step 1: Write the failing tests**

```csharp
// VisualTraceTests: a trace created with rootPort "command:turn_on" has "rootPort":"command:turn_on" in its header,
//   stays within its size budget with a 40-character key and the longest command name, and a trace created
//   without one serialises to exactly the bytes it did before (compare with the existing header assertion).
// EntityGraphTests.ASwitchNodeRunsItsCommandFlows gains: the recorded trace's rootNodeId is the node's id and its
//   rootPort is "command:turn_on".
```

```ts
it('a flow is named by node and port, as the server names it', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' }).nodes[0]
  expect(flowMethod(light, 'state')).toBe('Run_6C_7374617465')
})

it('a run of a node\'s flow reads as the entity and the flow', () => {
  const graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const names = definitionNames(graph, nodeCatalog)!
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_on'), names)).toBe('Lamp: Turn on')
  expect(runName(flowMethod(graph.nodes[0], 'state'), names)).toBe('Lamp: State')
  expect(names.entities!.map(e => e.key)).toEqual(['lamp'])
})
```

Python: the trace view keeps `rootPort` when the trace has one and has no such key when it does not.

- [ ] **Step 2: Run** `-- --visual-trace`, `-- --entity-graph`, the two frontend files and the Python file. Expected: FAIL on the missing field and export.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** every suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'A run started by an Entity node is listed as the entity and the flow, such as Lamp: Turn on.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 5: Problems of an entity show on its node

**Files:**
- Modify: `tests/fixtures/visual-graph-validation.json`, `tests/fixtures/entity-option-problems.json`
- Modify: `tests/Runtime/VisualGraphContractTests.cs` and `FE/tests/visual-contract.test.ts` (both fixture readers gain the optional case fields `portId` and `warningPortId`, asserted when present)
- Modify: `CC/HassSharp/Visual/VisualGraphValidator.cs` (`Codes` l.32-42, `Words`, `Definition()`, `GraphRules()` about l.658-693, `ValidateNode` about l.862-884, `StepName` l.92)
- Modify: `CC/HassSharp/Devices/DefinitionGraph.cs` (`Compose` adds the device marker), `CC/HassSharp/Devices/DefinitionDocument.cs` (`DefinitionProblem`, `Check`, `Options`), `CC/HassSharp/Devices/DefinitionGenerator.cs` (`Preview` l.43-68)
- Modify: `FE/src/visual/contract.ts` (`diagnosticCodes`, `words`, `words.stepName` l.743, `checkDefinition`, `graphRules`, the per-node rules, `connectData` l.1618), `FE/src/entities/definitionEdits.ts` (`optionProblems`)
- Test: also `tests/Runtime/EntityNodeTests.cs`, `tests/Runtime/EntityStoreTests.cs`, `tests/Runtime/EntityDomainTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/entity-option-problems.test.ts`

**Interfaces:**
- Consumes: `DeclaredEntities`, `HubFlows`' notion of "supplied", the hub's pins.
- Produces: codes `entity.name`, `entity.partialState`, `scope.stateSource`, `scope.flow` in `VisualGraphValidator.Codes` and `diagnosticCodes`. `delegate void DefinitionProblem(string code, string message, string? input, string? port = null, string? node = null)`. `definition.device` on the composed graph: an empty object when the definition has a device, absent otherwise (the editor's graph already carries the device there).
- A declaring node is named, in `Words.StepName` and `words.stepName`, by its trimmed `config.name`, else `config.key`, else its kind's label. Every message below uses that.

**Rules**, in both validators, each with the message of Global Constraints. Severity 3 unless said:

| code | on | when |
|---|---|---|
| `definition.entityKey` | the node, `inputId: "key"` | its key is blank, or not `^[a-z][a-z0-9_]{0,39}$` |
| `definition.duplicateEntity` | every declaring node after the first of a key, in node order, `inputId: "key"` | two nodes declare one key |
| `definition.entityDomain` | the node, `inputId: "domain"` | its type is not blank and not a row of `entityDomains` (a blank one gets the generic `Type is required`) |
| `entity.name` | the node, `inputId: "name"` | its name is blank and `definition.device` is not an object |
| `entity.partialState` | the node, `inputId` the missing value | at least one state value is supplied and a value the catalog does not mark `required: false` is not |
| `scope.stateSource` | the node, `inputId` the state value | the value's wire comes, directly or through value steps, from an output of a step that runs in a flow (anything not pure), the hub's own command outputs included |
| `scope.flow` | the reading step, `inputId` the reading input | the step reads `command:<c>:<v>` of a hub, directly or through value steps, and is reachable along execution wires from any root pin other than that hub's `command:<c>` |
| `command.unhandled` (severity 2) | the node, `portId: "command:<c>"` | that pin has no outgoing wire |
| `camera.no_frame` (severity 2) | the node, `portId: "frame"` | no Show frame step names the camera (the existing condition) |
| `state.dynamicSource` (severity 2) | the reader, as today | the existing rule, with a hub that has a state flow counted as a State |

The old rules stay for listed entities and old kinds until Task 12; `command.unhandled` and `camera.no_frame` for a node's entity come from the rows above only.

**Options.** `DefinitionDocument.Options` also checks each node's entity whose type is known, reporting on the node: `error(code, message, "option:<name>", null, nodeId)`, with the words it has today. `DefinitionGenerator.Preview` returns early only for document problems that are on no node; problems on a node are added to the graph's diagnostics, and the preview is invalid with no source. `optionProblems(graph, catalog)` in the frontend covers the same local subset as today over the Entity nodes, with the same `nodeId`, `inputId` and message, and `entity-option-problems.json` gains the hub form of each of its cases (an added `node: true` field per case is enough for both readers to build the entity as a node).

**`connectData`** exempts `entity.partialState` beside `pure.unused` and `state.dynamicSource`.

- [ ] **Step 1: Write the failing tests.** Fixture cases first (names are the test names):

| name | graph | expects |
|---|---|---|
| `an Entity node needs a key` | binary sensor, key `""` | `nodeId`, `inputId: "key"`, `codes: ["definition.entityKey"]`, `messages: ["Give this entity a key, such as power"]` |
| `an Entity node's key is lowercase letters, digits and underscores` | key `Front Door` | `codes: ["definition.entityKey"]`, the kept sentence with `Front Door` |
| `two Entity nodes cannot share a key` | `a` and `b` both `door`, `a` named `Front door` | `nodeId: "b"`, `inputId: "key"`, `messages: ["The key door is already used by Front door: give this entity its own"]` |
| `an Entity node's type is one Home Assistant has` | domain `fan` | `inputId: "domain"`, `messages: ["fan is not an entity type: choose one from the list"]` |
| `an entity without a device needs a name` | name `""`, no `definition.device` | `inputId: "name"`, `codes: ["entity.name"]`, `messages: ["Name this entity: it has no device, so Home Assistant shows it by its own name"]` |
| `an entity of a device needs no name` | the same with `definition.device: {}` | `valid: true`, `noDiagnostics: true` |
| `a light with a brightness needs to know whether it is on` | light `Lamp`, `arguments.brightness` number 128 | `inputId: "is_on"`, `codes: ["entity.partialState"]`, `messages: ["Lamp has a value for Brightness but none for On: connect or set On too"]` |
| `a light that is only on or off is whole` | light, `is_on` set | `valid: true` |
| `a cover with only a position is whole` | cover, `position` set | `valid: true` |
| `a state value cannot read a step that runs in a flow` | binary sensor `Door`; trigger → Read entity `read`; `read.value` → `is_on` | `nodeId` the hub, `inputId: "is_on"`, `codes: ["scope.stateSource"]` |
| `a state value cannot read the node's own command value` | number `Level`; `command:set_value:value` → its own `value` input | `codes: ["scope.stateSource"]` |
| `two commands can share a step` | switch; `command:turn_on` → Log `log`; `command:turn_off` → `log` | `valid: true`, `noDiagnostics: true` |
| `a command's value is read only by steps of that command` | light `Lamp`; `command:turn_on` → Log `log`; `command:turn_off` → `log`; `command:turn_on:brightness` → `log.message` (through a Convert if the types need it) | `nodeId: "log"`, `codes: ["scope.flow"]`, `messages: ["Log reads Brightness from Turn on of Lamp but also runs from Turn off of Lamp, where there is no Brightness"]` |
| `a command without steps is a warning on its pin` | switch `Relay`, only `command:turn_on` wired | `valid: true`, `warning: "Relay does nothing on Turn off: connect a step to Turn off"`, `warningNodeId` the hub, `warningPortId: "command:turn_off"` |
| `a camera that shows no frame is a warning on its pin` | camera `Cam`, nothing wired | `warningPortId: "frame"`, `warning: "Cam never shows a frame: add a Show frame step for it"` |

```ts
it('a light\'s Brightness can be connected before On, and the node then asks for On', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'constant', 'c', { type: 'number', value: { kind: 'literal', type: 'number', value: 128 } })
  const wired = connectData(graph, 'c', 'value', 'l', 'brightness')
  expect(wired).not.toBe(graph)
  expect(validateGraph(wired).filter(d => d.severity === 3)).toMatchObject([{ nodeId: 'l', inputId: 'is_on', code: 'entity.partialState' }])
})

it('the editor refuses a wire that reads a command\'s value from a step another command also runs', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: { kind: 'literal', type: 'boolean', value: true } })
  graph = connectNodes(connectNodes(graph, 'l', 'set', 'command:turn_on'), 'l', 'set', 'command:turn_off')
  expect(connectData(graph, 'l', 'command:turn_on:brightness', 'set', 'brightness')).toBe(graph)
  const one = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const alone = connectNodes(add(one, 'set_entity_state', 'set', { entity: 'lamp', is_on: { kind: 'literal', type: 'boolean', value: true } }), 'l', 'set', 'command:turn_on')
  expect(connectData(alone, 'l', 'command:turn_on:brightness', 'set', 'brightness')).not.toBe(alone)
})
```

Set the Constant's value as the catalog's `constant` kind stores it. If `canConnect` refuses a second step-order wire between the same two nodes, put a Log step after Turn off and connect that to `set`; the fixture case `two commands can share a step` does the same. C#, in `EntityStoreTests.cs`: `ANodesOptionProblemIsOnItsNode` (a select node without `options.options`: the preview's one error has the node's id, `InputId == "option:options"`, code `definition.entityOption`, and a second graph problem elsewhere in the same graph is reported with it, not hidden) and `ADeviceMarksTheGraph` (`Compose` of a body with a device has `definition.device` an empty object; without one, no such key).

- [ ] **Step 2: Run** `-- --visual-graph`, `-- --entity-store`, `-- --entity-domains`, and `bun run test tests/visual-contract.test.ts tests/entity-node.test.ts tests/entity-option-problems.test.ts`. Expected: FAIL: the cases report nothing, and the fixture readers do not know `warningPortId`.

- [ ] **Step 3: Implement** on both sides, then compare every new `messages` entry with `--describe-visual-graph`.

- [ ] **Step 4: Run** every suite. Expected: pass, and the two validators agree on every fixture case.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'A problem with an entity, such as a missing name, a repeated key or a command without steps, shows on its node.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 6: The card and the right panel of an Entity node

**Files:**
- Modify: `FE/src/visual/flow/flowModel.ts` (`nodeTitle`), `FE/src/visual/nodeSummary.ts` (new `case 'entity'`)
- Modify: `FE/src/visual/NodeConfig.tsx` (header about l.230-245; new prop), `FE/src/visual/Inspector.tsx` and `FE/src/visual/VisualEditor.tsx` (thread the prop), `FE/src/visual/adapter.ts` (`EditorAdapter.savedEntities`)
- Modify: `FE/src/entities/NodePickers.tsx` (`StepPickerContext.saved`, new `stepPickers.entity`), `FE/src/entities/adapter.ts`, `FE/src/entities/DefinitionPanel.tsx` (export `liveEntitiesOf` if it is not)
- Create: `FE/src/entities/EntityNodeSection.tsx` (the section's component, so `NodePickers.tsx` does not grow by 200 lines)
- Create: `FE/tests/entity-hub-config.test.tsx` (`FE/tests/entity-node-config.test.tsx` exists already and tests the old steps' pickers)
- Test: also `FE/tests/node-summary.test.ts`

**Interfaces:**
- Consumes: the hub's pins; `optionProblems`; `EntityRows.tsx`'s field copy and its `OptionField` (move `OptionField` into `EntityNodeSection.tsx` or a shared file; `EntityRows.tsx` keeps working until Task 11).
- Produces:
  - `export interface SavedEntities { keys: string[]; entityIds: Record<string, string> }` in `FE/src/visual/adapter.ts`; `EditorAdapter.savedEntities?(context: EditorContext): SavedEntities | undefined`; the entity adapter answers the saved document's entity keys and the entity ids `liveEntitiesOf` finds.
  - `InspectorProps.savedEntities?`, `NodeConfigProps.savedEntities?`, `StepPickerContext.saved?: () => SavedEntities | undefined`.
  - `stepPickers.entity = { keys: ['key', 'domain', 'name', 'icon', 'deviceClass', 'entityCategory', 'enabledByDefault', 'options', 'history', 'arguments'], Section: EntityNodeSection }`.

**What it renders:**
- **Card.** Title: the entity's name, else its key, else `Entity` (`nodeTitle` for a kind whose spec has `declares`). Summary: `{Type label} · {key}`, for example `Light · lamp`; without a key, the type label alone.
- **Header.** For a declaring kind the header input edits `config.name`, not `label`: `aria-label="Entity name"`, `placeholder="Not set"`, `data-input-id="name"` so the `entity.name` problem's row focuses it. The kind line under it reads `Entity · Entity` as other kinds' do.
- **Section**, in this order, each with the label, placeholder and hint it has in `EntityRows.tsx`: Key, Type, Device class, Icon, Category, Enabled by default, one field per option of the type, then `Run history`, then the entity id line with its `Show` button.
  - Key is read-only once `saved().keys` has it (hint `The key is part of the entity's identity in Home Assistant and cannot change.`); otherwise editing it uses `keyProblem` against the other entities' keys and, on a valid key, `renameEntityKey` so steps that name it follow. Each field carries `data-input-id` of its config key; an option's is `option:<name>`.
  - Type is a select of `entityDomains`. In this task a change sets `config.domain` only; Task 7 makes it refit.
  - `Run history`: `Record state updates` (shown when the type has state values; off by default), `Record commands` (when it has commands; on by default), `Record frames` (when it has `frame`; off by default). A switch writes `config.history.state` / `.commands` / `.frame`; setting one back to its default removes the key.
- **Inputs and Outputs** are the generic sections `NodeConfig` already renders from `dataInputs` and `dataOutputs`: each state value is an input row that can be set or connected.

- [ ] **Step 1: Write the failing tests** in `FE/tests/entity-hub-config.test.tsx`, mounting `NodeConfig` as `FE/tests/entity-node-config.test.tsx` does (copy its mount, `type` and `labels` helpers; the comments below say what each test does and asserts):

```tsx
it('the header names the entity, and the card follows', () => {
  // render NodeConfig for a light node named Lamp
  const name = root.querySelector<HTMLInputElement>('input[aria-label="Entity name"]')!
  expect(name.value).toBe('Lamp')
  type(name, 'Hall lamp')                       // fires input
  expect(changed().config.name).toBe('Hall lamp')
  expect(changed().label ?? '').toBe('')
  expect(nodeTitle(changed(), nodeCatalog)).toBe('Hall lamp')
  expect(nodeTitle({ ...changed(), config: { ...changed().config, name: '' } }, nodeCatalog)).toBe('lamp')
})

it('the section has the entity\'s fields and its type\'s options, and no generic field repeats them', () => {
  expect(labels(root)).toEqual(expect.arrayContaining(['Key', 'Type', 'Device class', 'Icon', 'Category', 'Enabled by default', 'Can be dimmed']))
  expect(root.querySelectorAll('[data-input-id="key"]').length).toBe(1)
  expect(root.textContent).not.toContain('State values')      // the catalog label of `arguments` is not a field
})

it('a saved key cannot change, and an unsaved one renames the steps that use it', () => {
  // saved: { keys: ['lamp'], entityIds: {} } → the Key input is readOnly and shows the identity hint.
  // not saved, with a Set state step naming lamp: typing hall → onGraphChange gets a graph whose step names hall.
  // typing a key another node has shows keyProblem's sentence and changes nothing.
})

it('the state values are inputs that can be set', () => {
  // the Inputs section has rows On and Brightness; setting On to true gives config.arguments.is_on.
})

it('run history switches follow the type and write only what differs from the default', () => {
  // light: Record state updates (off), Record commands (on), no Record frames.
  // turning Record commands off → config.history === { commands: false }; back on → config.history === {}.
  // camera: only Record frames.
})

it('a problem on a field focuses that field', () => {
  // diagnostics: [{ nodeId, inputId: 'name', code: 'entity.name', … }] → clicking the problem focuses the header input.
  // inputId 'option:options' on a select node → focuses the option's field.
})

it('the entity id line shows once Home Assistant has the entity', () => {
  // saved.entityIds = { lamp: 'light.lamp' } → the line reads light.lamp and Show dispatches hass-more-info.
})
```

`FE/tests/node-summary.test.ts`: the summary of a light node keyed `lamp` is `Light · lamp`; with a blank key, `Light`.

- [ ] **Step 2: Run** `bun run test tests/entity-hub-config.test.tsx tests/node-summary.test.ts`. Expected: FAIL: the header input is `Node label`, the section does not exist.

- [ ] **Step 3: Implement.**

- [ ] **Step 4: Run** `bun run test` and `bunx tsc --noEmit`. Expected: pass. Then build the frontend and open an entity definition in the local Home Assistant (AGENTS.md, "Run Home Assistant locally"; restart `hass` after the build): drop an Entity node, name it, choose Light, set On, connect a Set state step to Turn on, save, and check the entity in Home Assistant. Report what was seen. Do not use the `mcp__hass__*` tools for this: they reach a different Home Assistant.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'An Entity node shows its name and type on the card, and its name, key, type and options are edited in the right panel.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 7: Adding, retyping, copying and deleting an Entity node

**Files:**
- Modify: `FE/src/visual/contract.ts` (`addGraphNode`, `prefilled`; `freeKey` moves here from `FE/src/entities/definitionEdits.ts`, which re-exports it)
- Modify: `FE/src/visual/clipboard.ts` (`pasteSteps`)
- Modify: `FE/src/entities/NodePickers.tsx` (`retypeEntity` gains the node form), `FE/src/entities/EntityNodeSection.tsx` (Type calls it and shows the notice), `FE/src/entities/model.ts` (`newGraphDefinition`, `renameEntityKey`), `FE/src/entities/definitionEdits.ts` (`renameDefinition`, `stepsUsingEntity`), `FE/src/entities/adapter.ts`
- Modify: `FE/src/visual/adapter.ts` (`EditorAdapter.removal`), `FE/src/visual/flow/FlowAdapter.tsx` (`deleteSelection` about l.827-842), `FE/src/visual/Inspector.tsx` (`remove` about l.140-145), `FE/src/visual/VisualEditor.tsx` (the dialog)
- Test: `FE/tests/entity-node.test.ts`, `FE/tests/entity-hub-config.test.tsx` (choosing a type in the section shows the notice), `FE/tests/entity-model.test.ts`, `FE/tests/editor-shell.test.tsx`, and the clipboard test file (`grep -rl pasteSteps FE/tests`)

**Interfaces:**
- Consumes: Task 6's section; `refit` and `refitNotice` of `NodePickers.tsx`; `optionFits`.
- Produces:
  - `addGraphNode(graph, 'entity', …)` gives the node `name` = its type's label and `key` = `freeKey(entityKey(name), keys of the definition's entities)`.
  - `retypeNode(graph: VisualGraph, nodeId: string, domain: string, catalog: VisualCatalogSnapshot): { graph: VisualGraph; removed: … }` in `NodePickers.tsx`, returning what `refitNotice` needs.
  - `EditorAdapter.removal?(graph: Graph, ids: string[], catalog: VisualCatalogSnapshot): { title: string; body: string } | undefined`.
  - `onBeforeDelete?: (ids: string[], proceed: () => void) => void` on `FlowAdapter` and `Inspector`. Without the prop, or when `removal` answers undefined, deletion is immediate as today.

**Behaviour:**
- **Type change.** Sets `config.domain`; keeps the options that fit the new type (`optionFits`) and drops the rest; drops each value under `arguments` whose pin the new type lacks or whose value no longer fits; removes data wires into pins that are gone or no longer fit, and execution and data wires out of pins that are gone; refits every step that names the key, as `retypeEntity` does today. The notice is `refitNotice`'s (`Removed N connections that no longer fit: …`, `Cleared a value that no longer fits: …`), shown through `onNotice`.
- **Paste and duplicate.** In `pasteSteps`, a pasted node whose spec has `declares` gets `freeKey(its key, the keys in the target graph and those given earlier in this paste)`. A pasted step whose `config.entity` named the copied key follows the new key when the node that declared it is in the paste. Nothing outside the paste changes.
- **Delete.** `removal` of the entity adapter: for the Entity nodes among `ids`, count the steps not among `ids` that name one of their keys (`stepsUsingEntity`). Zero: undefined, so the node is deleted at once. Otherwise the dialog of Global Constraints; `Delete` calls `proceed`, `Cancel` and Escape close it and delete nothing. Both delete paths (the canvas's Delete key and the right panel's Delete) go through `onBeforeDelete`.
- **Starter.** `newGraphDefinition(name)` has no listed entity and one Entity node: a binary sensor named `name`, keyed `entityKey(name)`. Before the first save `renameDefinition` keeps that one node's name and key following the definition's name, as it does for the first listed entity today.

- [ ] **Step 1: Write the failing tests**

```ts
it('a dropped Entity node arrives named and with a key of its own', () => {
  let graph = add(empty(), 'entity', 'a')
  expect(graph.nodes[0].config).toMatchObject({ domain: 'binary_sensor', name: 'Binary sensor', key: 'binary_sensor' })
  graph = add(graph, 'entity', 'b')
  expect(graph.nodes[1].config.key).toBe('binary_sensor_2')
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
})

it('a type change drops what the new type does not have and says so', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp', options: { dimmable: true }, arguments: { is_on: lit('boolean', true), brightness: lit('number', 128) } })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true), brightness: lit('number', 10) })
  graph = connectNodes(graph, 'l', 'set', 'command:turn_on')
  const { graph: retyped, removed } = retypeNode(graph, 'l', 'binary_sensor', nodeCatalog)
  const node = retyped.nodes.find(n => n.id === 'l')!
  expect(node.config).toMatchObject({ domain: 'binary_sensor', options: {}, arguments: { is_on: lit('boolean', true) } })
  expect(retyped.edges.some(e => e.source === 'l')).toBe(false)
  expect(retyped.nodes.find(n => n.id === 'set')!.config).not.toHaveProperty('brightness')
  expect(validateGraph(retyped).some(d => d.message.startsWith('Unknown input'))).toBe(false)
  expect(refitNotice(removed)).toMatch(/^Removed 1 connection/)
})

it('a pasted Entity node gets a key of its own and its pasted steps follow it', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  graph = add(graph, 'set_entity_available', 'outside', { entity: 'lamp' })
  const pasted = paste(graph, ['l', 'set'])            // copy those two with the file's clipboard helper, paste at an offset
  const copies = pasted.nodes.slice(graph.nodes.length)
  expect(copies.find(n => n.kind === 'entity')!.config.key).toBe('lamp_2')
  expect(copies.find(n => n.kind === 'set_entity_state')!.config.entity).toBe('lamp_2')
  expect(pasted.nodes.find(n => n.id === 'outside')!.config.entity).toBe('lamp')
  expect(validateGraph(pasted).some(d => d.code === 'definition.duplicateEntity')).toBe(false)
  // A step pasted without its Entity node keeps the key it named.
  expect(paste(graph, ['set']).nodes.at(-1)!.config.entity).toBe('lamp')
})

it('a new definition starts with one Entity node that follows its name until it is saved', () => {
  const graph = editorGraph(newGraphDefinition('Front door'))
  expect(graph.nodes.map(n => [n.kind, n.config.key, n.config.name])).toEqual([['entity', 'front_door', 'Front door']])
  expect((graph.definition as never as { entities: unknown[] }).entities).toEqual([])
  const renamed = renameDefinition(graph, 'Back door', false)
  expect(renamed.nodes[0].config).toMatchObject({ key: 'back_door', name: 'Back door' })
  expect(renameDefinition(graph, 'Back door', true).nodes[0].config.key).toBe('front_door')
})
```

`renameDefinition`'s third argument is as `definitionEdits.ts` has it today. In `FE/tests/editor-shell.test.tsx`, with the entity adapter:

```tsx
it('deleting an Entity node that steps use asks first', async () => {
  // graph: Entity node lamp, a Set state step naming lamp. Select the Entity node, press Delete on the canvas.
  expect(dialog()!.textContent).toContain('Delete this entity?')
  expect(dialog()!.textContent).toContain('1 step uses this entity. They will show a problem until you change or remove them.')
  click(button(dialog()!, 'Cancel'));  expect(cards()).toContain('Lamp')
  // again, then Delete: the node is gone and the step shows definition.unknownEntity.
})
it('deleting an Entity node nothing uses, or together with every step that uses it, does not ask', async () => { /* no dialog */ })
it('the right panel\'s Delete asks the same question', async () => { /* same dialog from Inspector's Delete */ })
```

- [ ] **Step 2: Run** the four frontend test files. Expected: FAIL: the node arrives without a name, `retypeNode` is not defined, the pasted key repeats, the starter lists an entity, no dialog.

- [ ] **Step 3: Implement.** Update the existing tests this breaks in the files this task touches (`entity-model.test.ts`'s starter assertions; `EntityDefinitionView.test.tsx` where it reads the starter's listed entity).

- [ ] **Step 4: Run** `bun run test`, `bunx tsc --noEmit`, and the C# and Python suites (unchanged, as a check). Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'A new entity definition starts with an Entity node, a copied one gets its own key, a type change tidies what no longer fits, and deleting one that steps use asks first.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 8: The runtime suites and shared fixtures build entities as nodes

Tests only. After this task no C# test and no shared fixture needs a listed entity or one of the three old root kinds, except the tests of those kinds' own rules.

**Files:**
- Modify: `tests/Runtime/EntityGraphTests.cs`, `EntityGraphRuntimeTests.cs`, `EntityGraphGeneratorTests.cs`, `EntityStoreTests.cs`, `EntityDomainTests.cs`, `EntityHostTests.cs`, `EntityStartupTests.cs`, `EntityApiTests.cs`, `EntityToolsTests.cs`, `EntityStateStoreTests.cs`, `CameraTests.cs`, `HandoverTests.cs`, `HistoryTrackingTests.cs`, `ReloadMemoryTests.cs`, `GeneratedSourcePinTests.cs`, `AuthoringAdapterTests.cs` (those that build a graph definition; find the rest with `grep -rln "entity_state\|entity_command\|camera_frame\|\"entities\"" tests/Runtime`)
- Modify: `tests/fixtures/visual-graph-validation.json`, `tests/fixtures/entity-graph-order.json`, and `FE/tests/visual-contract.test.ts` only where it names a converted case

**Interfaces:**
- Consumes: everything of Tasks 1 to 5. Produces nothing.

**Conversion rules**, applied in each file's shared helpers first so most tests change by their helper alone:
- A listed entity becomes `Hub(id, key, domain, more)`. Where the test has a State for that entity, the hub takes the State's node id (wires into the State then reach the hub unchanged) and the State's values move under `arguments`. Otherwise the hub's id is its key.
- Give every hub of a definition without a device a `name`; default it to the key in the helper.
- `Command(id, key, command)` followed by `Next(id, step)` becomes `Flow(hubId, "command:<command>", step)`; a read of the command's value `D(id, step, port, value)` becomes `D(hubId, step, port, "command:<command>:<value>")`.
- When a frame is needed followed by a step becomes `Flow(hubId, "frame", step)`.
- Assertions follow: a run's method is `FlowMethod(hub, port)`, its name is `{Name}: …`, its trace's `rootNodeId` is the hub with `rootPort`; a trace no longer has an event for a separate root node.
- `tests/fixtures/visual-graph-validation.json`: convert every entity case that is not about `state.duplicate`, `command.duplicate`, `camera.duplicate`, or a problem only a listed entity can have (`definition.invalid` for a malformed list). Leave those: Task 12 deletes them. Re-derive each converted case's `messages` with `--describe-visual-graph`; a case whose expected problem moved onto the node gains `nodeId`.
- `tests/fixtures/entity-graph-order.json`: the hub form of each graph; where the fixture is about the order of State and When commanded roots, it becomes the order of a hub's flows (`state`, then commands in catalog order, then `frame`).

- [ ] **Step 1: Convert the helpers and fixtures.** No new behaviour is tested, so there is no failing step; instead, before converting a file, note the number of checks it runs (`PASS` lines) and keep it from falling: a test that only made sense for the old kinds is listed in the report, not silently dropped.

- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers` and `bun run test tests/visual-contract.test.ts`. Expected: pass. A failure here is a gap in Tasks 1 to 5: fix the code, with a test that names the gap, in this commit.

- [ ] **Step 3: Check the leftovers.** `grep -rn "entity_state\|entity_command\|camera_frame" tests/Runtime tests/fixtures` lists only the old-rule tests and cases named above; paste the list into the report.

- [ ] **Step 4: Run** every suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'The runtime suites and shared validation cases build every entity as an Entity node, so the node form carries all the coverage the old steps had.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 9: The frontend suites build entities as nodes

Tests only, as Task 8, for `FE/tests/`.

**Files:**
- Modify: `FE/tests/entity-roots.test.ts`, `entity-node-config.test.tsx` (its tests of Set state, Set available, Show frame and Setting move to Entity nodes; its tests of the three old steps' pickers stay until Task 12), `node-summary.test.ts`, `EntityDefinitionView.test.tsx`, `entity-model.test.ts`, `palette.test.tsx`, `visual-contract.test.ts`, `NodeConfig.test.tsx`, `RunTraceView.test.tsx`, `AutomationRuns.test.tsx`, `trace-model.test.ts` (`grep -rln "entity_state\|entity_command\|camera_frame\|entities: \[" FE/tests`)
- Leave for Task 11: `DefinitionPanel.test.tsx`'s tests of the Entities list, and any test of `EntityRows`.

**Conversion rules:** as Task 8's, with `add(graph, 'entity', id, { key, domain, name })`, `connectNodes(graph, hub, step, 'command:<command>')` and `connectData(graph, hub, 'command:<command>:<value>', step, port)`. `entity-roots.test.ts`'s subject becomes the hub: no step-order wire can be drawn into it, its flows lay out from it (`tidyLayout`), quick-add from a command pin connects to that pin (`addConnected`, `canAddFor`). A trace fixture gains `rootPort`.

- [ ] **Step 1: Convert**, keeping each file's test count from falling except for tests of the three old kinds' own pickers and summaries, which are listed in the report and deleted in Task 12.

- [ ] **Step 2: Run** `bun run test` and `bunx tsc --noEmit`. Expected: pass. A failure that is a gap in Tasks 2, 6 or 7 is fixed in this commit with a test that names it.

- [ ] **Step 3: Check the leftovers** with the grep above; paste the list into the report.

- [ ] **Step 4: Run** every suite. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'The editor suites build every entity as an Entity node, so quick-add, layout, traces and summaries are covered for the node form.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 10: Examples, MCP guidance, docs and the packaging smoke speak Entity nodes

**Files:**
- Modify: `CC/authoring_examples.json` (`definitions` 0 to 3: `Motion in either room`, `Plug over HTTP`, `Camera with a box`, `File at setup`)
- Modify: `CC/llm.py` (`PROMPT` about l.20, `SEMANTICS["definitions"]` about l.30, `ENTITY_SPECS` about l.158-168, the hints about l.343-480, `definition_summary` about l.549-561)
- Modify: `docs/custom-entities.md`, `docs/mcp-authoring.md`
- Modify: `tests/packaging/entity_bridge_smoke.py` (the nodes about l.122-135 and the entities it lists)
- Test: `tests/python/test_entity_authoring.py`, `tests/python/test_mcp_adapter.py`, `tests/test_docs_examples.py`, `tests/Runtime/AuthoringAdapterTests.cs`

**Interfaces:**
- Consumes: the hub end to end. Produces nothing in code. `DEFINITION` (the request schema, about l.99-102) and `DEFINITION_PARTS` still take `entities` until Task 11; the guidance stops mentioning it now.

**What the texts say** (the wording is the writer's; these facts are not):
- An entity is an `entity` node: `config` has `key`, `domain`, `name` and, where wanted, `icon`, `deviceClass`, `entityCategory`, `enabledByDefault`, `options`, `history`; a state value is set under `config.arguments` or connected to the input of its name.
- A command runs the steps on the execution output `command:<command>`; its values are the outputs `command:<command>:<value>`. A camera's frame flow starts at `frame`.
- An entity without a device needs a name. The request is `{name, device?, settings, logic: {kind: "graph", graph}}`.
- The three old steps are not mentioned anywhere.

- [ ] **Step 1: Write the failing tests.** In `tests/python/test_entity_authoring.py` (or the file that already walks the examples):

```python
def test_every_graph_example_declares_its_entities_as_nodes(self):
    examples = json.loads((ROOT / "custom_components/hass_sharp/authoring_examples.json").read_text())
    for example in examples["definitions"]:
        definition = example["definition"]
        if definition["logic"]["kind"] != "graph":
            continue
        kinds = [node["kind"] for node in definition["logic"]["graph"]["nodes"]]
        with self.subTest(definition["name"]):
            self.assertNotIn("entities", definition)
            self.assertIn("entity", kinds)
            self.assertFalse({"entity_state", "entity_command", "camera_frame"} & set(kinds))

def test_the_guidance_does_not_name_the_removed_steps(self):
    text = (ROOT / "custom_components/hass_sharp/llm.py").read_text() + (ROOT / "docs/custom-entities.md").read_text() + (ROOT / "docs/mcp-authoring.md").read_text()
    for gone in ("entity_state", "entity_command", "camera_frame", "When commanded"):
        self.assertNotIn(gone, text)
```

`AuthoringAdapterTests.cs` already previews every example; it must stay green, and gains one check that each graph example's preview derives the entities the example had before (`any_motion`; `relay`, `power`; `boxed`; `model`).

- [ ] **Step 2: Run** `python3 -m unittest discover -s tests/python -k authoring` and `dotnet run --project tests/Runtime --disable-build-servers -- --authoring-adapter`. Expected: FAIL: the examples list `entities` and use the old kinds.

- [ ] **Step 3: Rewrite** the four examples, the texts and the smoke's graph. `Plug over HTTP` keeps its device and its trigger flow. In the smoke, `motion`, `relay`, `bare` and `lamp` become Entity nodes and each `on`/`off` pair becomes that node's two command pins; what the smoke asserts about the five entity delegates is unchanged.

- [ ] **Step 4: Run** every suite, publish, run the smoke and `python tests/packaging/sqlite_smoke.py custom_components/hass_sharp`. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git commit -am "$(printf 'The examples, the MCP guidance and the custom entities guide describe an entity as an Entity node with its state and commands on it.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 11: The request no longer lists entities, and the right panel no longer has an entity list

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionGenerator.cs` (`Preview` about l.52), `CC/HassSharp/Devices/DefinitionDocument.cs` (`Check`), `CC/HassSharp/Devices/DefinitionGraph.cs` (`Compose`), `CC/HassSharp/Devices/EntityDefinition.cs` (`Body`, `Complete`, `Save`), `CC/HassSharp/Visual/VisualNodeCatalog.cs` (`WithGraphTypes`), `CC/HassSharp/Visual/VisualGraphValidator.cs` (`Definition()`)
- Modify: `CC/llm.py` (`DEFINITION`, `DEFINITION_PARTS`)
- Modify: `FE/src/visual/contract.ts` (`withGraphTypes`, `checkDefinition`, `GraphDefinition`), `FE/src/entities/model.ts` (`EditorDefinition`, `editorGraph`, `definitionBody`, `draftGraph`, `renameEntityKey`), `FE/src/entities/definitionEdits.ts` (remove `addEntity`, `removeEntity`, `updateEntity`, `setEntityOption`, `entityNamed`; `definitionOf`, `problemRow`, `definitionProblems`), `FE/src/entities/DefinitionPanel.tsx`, `FE/src/entities/NodePickers.tsx` (the picker hints; `retypeEntity`'s listed form), `FE/src/entities/adapter.ts`
- Delete: `FE/src/entities/EntityRows.tsx`
- Test: `tests/Runtime/EntityStoreTests.cs`, `EntityApiTests.cs`, `EntityGraphTests.cs`, `EntityNodeTests.cs`; `tests/python/test_mcp_adapter.py`; `FE/tests/DefinitionPanel.test.tsx`, `entity-model.test.ts`, `entity-node.test.ts`

**Interfaces:**
- Consumes: everything; Tasks 8 to 10 left nothing that sends `entities`.
- Produces: the request body `{name, device?, settings, logic}`; the stored document `version: 2` with `entities` derived; the editor's graph with `graph.definition = {settings, device}`.

**Behaviour:**
- **Request.** A graph request that has an `entities` key at all, even an empty array, is refused: `definition.entitiesNotAllowed`, the sentence of Global Constraints, input `entities`. A class request keeps its own sentence and rule. Through MCP the caller gets that sentence, not a schema error: `DEFINITION` keeps accepting the key and passes it on, while `DEFINITION_PARTS` and every description stop listing it.
- **Derivation.** `WithGraphTypes` / `withGraphTypes` derive from nodes only; a `definition.entities` on a graph is ignored. `Compose` stops injecting `entities` (it injects `settings` and the device marker). `Definition()` / `checkDefinition` drop their checks of the list.
- **Stored document.** `Save` writes `version: 2` and the derived list (as since Task 1). `Body` answers `Entities: null` for a graph, so a regeneration composes from the graph alone. The read answer still has `entities`: read-only, for lists and summaries.
- **Loading.** `Complete` accepts version 2; a version 1 document whose logic is a class is accepted as it is; a version 1 graph document throws `JsonException("saved by an earlier development build; delete it and build it again")`. That reaches the log through `List`'s `Skipping entity definition {path}: …`, the read of that id answers it as its error, and `Delete(id, 0)` removes the file as it does for any unreadable one.
- **`definition.entitiesRequired`** has its new sentence and no input.
- **Frontend.** The right panel's overview has no Entities section: `DefinitionPanel` renders the problems list, the Device form and the Settings rows. `definitionBody` sends no `entities`; `editorGraph` ignores the answer's. A definition problem that used to focus an entity's row now selects the node (`nodeId`) and focuses its field. The three pickers' hints that say `under Entities, shown when no step is selected.` say `Add an Entity node from the step library first.` instead.

- [ ] **Step 1: Write the failing tests**

```csharp
// EntityStoreTests
//   ARequestThatListsEntitiesIsRefused: body with "entities": [] and with one entry → preview and save answer one
//     error, code definition.entitiesNotAllowed, message "Entities are Entity nodes in the graph now: leave entities
//     out and add one Entity node for each".
//   AClassRequestKeepsItsOwnRefusal: unchanged sentence "A class definition's entities come from its code".
//   ADefinitionWithoutAnEntityNodeAsksForOne: "Add an Entity node: an entity definition creates the entities on its
//     canvas, and has none".
//   ASaveWritesVersionTwoWithTheDerivedEntities: the stored file has "version": 2 and "entities" of the nodes.
//   AnOlderGraphDocumentSaysToBuildItAgain: write a version 1 graph document by hand into the store's folder;
//     List() skips it; reading it fails with a message that contains "saved by an earlier development build; delete
//     it and build it again"; Delete(id, 0) removes the file.
//   AnOlderClassDocumentStillLoads: a version 1 class document written by hand is listed, read, and (EntityGraphTests,
//     with the Rig) starts and registers its entities.
//   ComposeGivesNoEntities: Compose(body)["definition"] has "settings" and no "entities".
// EntityNodeTests.ANodeReplacesAListedEntityOfItsKey becomes AListOnTheGraphIsIgnored: only the node's entity is derived.
```

```python
# tests/python/test_mcp_adapter.py (skips without Home Assistant 2026.11; run it in the container as AGENTS.md says)
#   entities_save with "entities" in the definition answers the server's sentence, and the tool descriptions and
#   DEFINITION_PARTS do not contain "entities".
```

```tsx
// FE/tests/DefinitionPanel.test.tsx: the overview has Device and Settings and no "Entities" heading or "Add entity" button.
// FE/tests/entity-model.test.ts:
it('the body sent to the server has no entities, and the editor ignores the answer\'s', () => {
  const graph = editorGraph(newGraphDefinition('Front door'))
  expect(definitionBody(graph)).not.toHaveProperty('entities')
  expect(graph.definition).toEqual({ settings: [], device: null })
  const answered = { ...definitionBody(graph), entities: [{ key: 'front_door', domain: 'binary_sensor' }] }
  expect(editorGraph(answered as never).definition).toEqual({ settings: [], device: null })
})
// FE/tests/entity-node.test.ts: 'a node replaces a listed entity of its key…' becomes
//   'a list on the graph is ignored': withGraphTypes(...).definition.entities has the node's entity only.
```

- [ ] **Step 2: Run** `-- --entity-store`, `-- --entity-nodes`, and the three frontend files. Expected: FAIL: the request is accepted, the file has version 1, the panel lists entities.

- [ ] **Step 3: Implement**, then delete `EntityRows.tsx` and what only it used (`OptionField` stays where Task 6 put it). `grep -rn "EntityRows\|addEntity\|removeEntity\|updateEntity" FE/src` must print nothing.

- [ ] **Step 4: Run** every suite, publish, run the packaging smoke. Then, in the local Home Assistant: the definitions saved by earlier builds are skipped with the one sentence in `docker compose logs --tail=200 hass`; delete one from the entities page and build it again. Report what was seen.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(printf 'An entity definition is its graph: the right panel no longer lists entities, and a request that lists them is told to use Entity nodes.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

### Task 12: The State, When commanded and When a frame is needed steps are gone

**Files:**
- Modify: `CC/visual_node_catalog.json` (remove `entity_state`, `entity_command`, `camera_frame`)
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs` (the `entity` and `command` dynamic branches that only they used), `VisualGenerator.cs` (`RootActions` keeps `entity_setup` only), `VisualRuntime.cs` (`Walk`'s cases, `EntityKinds`, `Command`), `VisualGraphValidator.cs` (`state.duplicate`, `command.duplicate`, `camera.duplicate` and their words; the old forms of `command.unhandled`, `state.dynamicSource`, `definition.unknownCommand`, `camera.entity` where only a removed kind could raise them; `Codes`), `StateDependencies.cs` (its summary), `CC/HassSharp/Devices/DefinitionGraph.cs` (`Roots`, the summaries), `DefinitionHost.Graph.cs` (comments that name the steps)
- Modify: `FE/src/visual/contract.ts` (`diagnosticCodes`, `graphRules`, `cameraKinds`, `entityChoices`, `withEntityCommand`, `commandOutputs`'s old form, `trackHistoryDefault` if only they used it), `FE/src/visual/nodeSummary.ts` (the two cases and `entityStepWords`), `FE/src/entities/NodePickers.tsx` (`stepPickers.entity_state`, `.entity_command`, `.camera_frame`, `CommandPicker`, `chooseCommand`), `FE/src/entities/model.ts` (`definitionEntityKinds`), `FE/src/components/TriggerSummary.tsx` (its comment)
- Modify: `tests/fixtures/visual-graph-validation.json` (delete the cases Task 8 left), `tests/fixtures/visual-rich-catalog.json` (regenerate)
- Delete: the tests Tasks 8 and 9 listed as testing only the removed kinds
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/palette.test.tsx`

**Interfaces:**
- Consumes: nothing new. Produces: a catalog whose Entity group is `entity`, `entity_setup`, `set_entity_state`, `set_entity_available`, `setting`, `download_file`, `camera_snapshot`, `image_draw_rect`, `image_draw_text`, `show_frame`, in that order.

**Behaviour:** a graph that still has one of the three kinds gets the validator's existing unknown-kind problem on that node; nothing else is said about them. `set_entity_state`, `set_entity_available` and `show_frame` are unchanged: they name an entity by key and now find it among the nodes.

- [ ] **Step 1: Write the failing tests**

```csharp
static void TheOldStepsAreNotKinds()
{
    foreach (var kind in new[] { "entity_state", "entity_command", "camera_frame" })
        Check(VisualNodeCatalog.GetNode(kind) == null, kind + " is not in the catalog");
    foreach (var code in new[] { "state.duplicate", "command.duplicate", "camera.duplicate" })
        Check(!VisualGraphValidator.Codes.Contains(code), code + " is not a problem any more");
}
```

```ts
it('the old steps are gone from the catalog and the step library', () => {
  for (const kind of ['entity_state', 'entity_command', 'camera_frame']) expect(nodeCatalog.nodes).not.toHaveProperty(kind)
  const entity = catalogPalette(nodeCatalog, 'entity').filter(item => item.category === 'Entity').map(item => item.kind)
  expect(entity.slice(0, 3)).toEqual(['entity', 'entity_setup', 'set_entity_state'])
  expect(diagnosticCodes).not.toContain('state.duplicate')
})
```

Add one fixture case, `a step of a kind this version does not have is named as unknown`: a graph with an `entity_state` node, `valid: false`, `nodeId` that node (fill `messages` from `--describe-visual-graph`).

- [ ] **Step 2: Run** `-- --entity-nodes` and `bun run test tests/entity-node.test.ts`. Expected: FAIL: the kinds are in the catalog.

- [ ] **Step 3: Remove.** Afterwards `grep -rn "entity_state\|entity_command\|camera_frame" custom_components/hass_sharp --include="*.cs" --include="*.ts" --include="*.tsx" --include="*.py" --include="*.json" --include="*.md" docs tests` (quote the globs; skip `bin/`, `obj/`, `out/`, `node_modules/`) prints only the new tests above, the unknown-kind fixture case, and this plan, the spec and the map.

- [ ] **Step 4: Run** every suite, publish, run both packaging smokes, re-pin the generated-source pins if only the embedded catalog changed. Expected: pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "$(printf 'The State, When commanded and When a frame is needed steps are removed: an Entity node does all three.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>')"
```

---

## Rulings

Each is a decision this plan made where the spec left room. Format: what, why, cost if wrong.

1. **The entity list is derived inside `WithGraphTypes` / `withGraphTypes`.** Every reader of the list already goes through the catalog these return, and the generator embeds it for the runtime, so one function per side changes. Cost if wrong: a reader that lifts `graph.definition` by hand misses node entities; Task 1 replaces the three known ones with `definitionCatalog`, and a fourth would show as "unknown entity" on a step.
2. **From Task 1 the stored document holds the derived list, and a node replaces a listed entry of its key.** Lists, cards and MCP summaries stay right for node-only definitions during the transition, and saving a read answer back is idempotent. Cost if wrong: between Task 1 and Task 11, an MCP caller that deletes an Entity node but sends the old `entities` back keeps that entity as a listed one. Task 11 ends it.
3. **Entities are listed in node order, and of two nodes with one key the later in node order has the problem.** Node order is the order they were added, which is the order a person expects on the device page; one order for both keeps the rule explainable. Cost if wrong: one comparator on each side.
4. **A flow exists only where something is connected.** A state flow needs at least one supplied value; a command or frame flow needs a wire. An unwired command keeps the host's `has no flow for` error, with a warning on the pin. Cost if wrong: an entity whose state is only pushed by Set state would otherwise get a state flow that sets nothing and fights the pushes.
5. **Every state input is optional as a pin, and `entity.partialState` asks for the required ones once any is supplied.** The examples push state with Set state and connect nothing. Cost if wrong: only a light can raise it today (Brightness without On); a new type with two required values would need the message checked.
6. **`scope.flow` is static and blocks the wire; `AbsentCommandValue` stays for values a command may come without.** Two commands may share a step (they can today), so command outputs are not scopes. Cost if wrong: a graph the rule misses fails at run time with the existing sentence, not silently.
7. **`entity.name` reads `definition.device`, an empty-object marker on the server.** Phase 3 replaces it with the device wire. The bridge's fallback to the definition's name stays for class definitions through this phase. Cost if wrong: one condition in each validator.
8. **Option checks stay in `DefinitionDocument` on the server, reported on the node, with the frontend's local subset.** Moving all of them into both validators would need the frontend to mirror every server check. `Preview` no longer stops at a node's option problem, so it cannot hide a graph problem. Cost if wrong: an option problem the frontend does not check locally appears only after the server previews, as today.
9. **Hub command and frame pins come from `config.domain`, not from a lookup by key.** The pins must show while the key is blank or repeated. Cost if wrong: none found.
10. **A camera's frame pin is declared by `entityDomains.camera.frame` in the catalog.** It keeps "which types have a frame flow" out of code on two sides. Cost if wrong: a Python contract test that pins a type row's keys needs the key added (Task 2 says to look).
11. **History is per kind of flow on the node:** `history.state` (default off), `history.commands` (default on), `history.frame` (default off), the defaults the three old steps had. Per-command switches were not asked for. Cost if wrong: a config shape change inside one node kind.
12. **A request with `entities` is refused only from Task 11**, and then even when the list is empty. Before it, tests and examples could move one at a time with both forms valid. Cost if wrong: none after Task 11.
13. **Version 1 class documents still load; only version 1 graph documents are refused.** A class document's format does not change in this phase, and the user's SmartPlug example is one. Cost if wrong: one condition in `Complete`.
14. **Deleting an Entity node asks only when steps outside the deletion use it.** The spec asks for a confirmation "when steps use it"; a node nothing uses is undone with Ctrl+Z like any step. Cost if wrong: one condition in `removal`.
15. **A new Entity node is named after its type and keyed from that name.** A node dropped on the canvas should not arrive with two problems. Cost if wrong: two default values.
16. **The transition leaves the right panel's Entities list in place until Task 11**, hiding only entries a node declares. Removing it earlier would break tests that Tasks 8 and 9 have not moved yet. Cost if wrong: for four commits the editor offers both ways to add an entity; no build is released from them.
17. **Mixing a hub with an old root step for the same entity is unsupported and untested.** The old steps exist for eleven commits and no document format is released with both. Cost if wrong: an odd graph in a development build runs the hub's flow, not the old step's.

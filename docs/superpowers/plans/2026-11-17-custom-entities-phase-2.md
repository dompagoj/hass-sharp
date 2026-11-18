# Custom Entities Phase 2 (Graph-Defined Definitions) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An entity definition whose logic is a node graph (`logic: {kind: "graph", graph}`) is validated, compiled, hosted and published like a class definition, so a derived binary sensor is four nodes and no trigger.

**Architecture:** The definition's entities and settings are composed into `graph.definition`, which both validators and the generator read. The catalog gains an `entityDomains` table and eight node kinds; "is a trigger" becomes "is a root". The graph compiles through the existing `VisualGenerator` into a `VisualAutomation` whose actions are the graph's roots, and the existing `DefinitionHost` owns the entities, built from the document instead of from a user class. `VisualRuntime` reaches the host through the ambient invocation, so generated source needs no new argument.

**Tech Stack:** .NET 11 (C#), Roslyn, System.Text.Json; TypeScript (SolidJS frontend, Vitest under Bun); Python 3.14 (Home Assistant 2026.11, Python.NET 3.1.0).

**Spec:** `docs/superpowers/specs/2026-11-17-custom-entities-design.md` (sections "Stored document", "Hosting", "Catalog"). Code map made for this plan, with file and line references at `207c814`: read the sections your task names in `docs/superpowers/plans/2026-11-17-custom-entities-phase-2-map.md`.

Paths are under `custom_components/hass_sharp/` unless they start with `tests/`, `docs/` or `.github/`. C# paths are under `custom_components/hass_sharp/HassSharp/`.

## Global Constraints

- **Existing automations' generated source stays byte-identical.** Their revision is the SHA-256 of the source, and startup regenerates and stores every automation's source. Task 3 pins hashes; every later task keeps that test green without editing the pinned values.
- `graphVersion` stays 5. Catalog changes are additive: no existing node spec, type or section changes, and nothing is added to the `trigger` spec.
- Both validators (C# `Visual/VisualGraphValidator.cs`, TypeScript `frontend/src/visual/contract.ts`) change together and agree on every case of `tests/fixtures/visual-graph-validation.json`. New diagnostic codes go into both `Codes` arrays.
- New catalog labels and descriptions pass `--catalog-wording` (no "boolean", "string", "snapshot", "typed", "operand", "literal", "payload").
- A graph is an *entity graph* exactly when it has an object `definition`; otherwise it is an automation graph. The stored document never holds `logic.graph.definition`: it is composed from `entities` and `settings` for validation and generation.
- Entity types in this phase: `binary_sensor`, `sensor`, `switch`. Setting types for graph definitions: `string`, `boolean`, `number`.
- Unique id `<definitionId>:<entityKey>`; keys match `^[a-z][a-z0-9_]{0,39}$`; device identifier `("hass_sharp", definitionId)`. Only strings cross the Python/.NET boundary. The sync JSON, snapshot JSON and the Python bridge do not change.
- A definition that is off, fails to compile or fails setup keeps its entities as unavailable. Registry entries are removed only by delete, by removing an entity from the definition (once the new revision's sync has succeeded), or by the startup prune.
- Class definitions behave exactly as at the end of phase 1; their tests pass unchanged except where a task says otherwise.
- No new NuGet or Python packages. No change to `llm.py` beyond Task 4's filter. No panel UI beyond Task 4's palette filter (the Entities section is phase 3).
- Run history: off by default for reactive state (`entity_state`), on by default for command and setup flows.
- Commits: imperative subject, ending with the trailer `Co-Authored-By: Claude Fable 5.2 <noreply@anthropic.com>`. No push.
- Suites (repository root; all green at the end of every task that touches their code):
  `dotnet run --project tests/Runtime --disable-build-servers` (baseline 317 PASS),
  `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests` (22),
  `PYTHONPATH=tests/python python3 -m unittest discover -s tests/python` (149 OK, 39 skipped on the host),
  `python3 -m unittest discover -s tests` (5),
  `(cd custom_components/hass_sharp/frontend && bun run test)` (1319).
- Never use `mcp__hass__*` tools (they reach a production instance). The `hass` container is the user's development instance: `docker exec` and `docker compose restart hass` only, as `AGENTS.md` describes.

## Review Focus

1. A source entity that does not exist or is unavailable when the state is worked out (at startup, or later): the derived entity is unavailable, the definition keeps running, and the state returns when the source does. → Task 6 and Task 9 tests.
2. Sources changing faster than a computation finishes: the entity ends on the value of the last change (Restart mode), never an older one. → Task 9 test.
3. An entity removed from the definition while nodes still name it: the save is refused and names the node. → Task 4 fixture case, Task 7 test.
4. A command flow that fails: the Home Assistant service call fails with the flow's error and the entity's state is unchanged; a command with no flow fails with a message naming the entity and the command. → Task 9 tests.
5. An instance upgraded with existing automations: none of them gets a new revision or a rewritten stored source at startup. → Task 3 pins, Task 4 test.

---

### Task 1: Close the two findings carried from phase 1

**Files:**
- Modify: `Devices/DefinitionHost.cs` (`PrepareAsync`, the `Within(...)` calls around `Setup()`), `entity_bridge.py` (the write-failure record, about lines 226-235), `Devices/EntityApi.cs` (the "entities directory missing" log, about line 216)
- Test: `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/EntityStartupTests.cs`, `tests/python/test_entity_bridge.py`

**Interfaces:**
- Consumes: `DefinitionHost.SetupTimeout` (internal, settable by tests), `Within(task, timeout, message)`.
- Produces: no new names. Behaviour: a `Setup()` that blocks its thread is bounded by `SetupTimeout` like one that awaits; at most 5 distinct write-failure messages are logged per unique id between syncs, then one line saying further failures for that entity are not logged; the prune-skip line is a warning.

- [ ] **Step 1: Write the failing tests:** `SetupThatBlocksItsThreadTimesOut` (a `Setup()` calling `Thread.Sleep(Timeout.Infinite)` fails the prepare within the shortened bound with the `Setup() did not finish within` message, and the next definition still loads at `Init`); `test_write_failures_with_changing_messages_are_bounded` (20 pushes whose failure messages all differ log 5 messages and one "not logged" line, and the record holds at most 6 entries; a new sync of the definition resets it); extend the missing-directory startup test to expect the skip at warning level.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --entity-host --entity-startup` and `PYTHONPATH=tests/python python3 -m unittest tests.python.test_entity_bridge -v`. Expected: the new tests FAIL.
- [ ] **Step 3: Implement.** Run each component's `Setup()` on the thread pool (`Task.Run`) inside the existing bound; the user's constructor and setting setters keep running inline.
- [ ] **Step 4: Run** the same commands, then `--reload-memory`. Expected: PASS.
- [ ] **Step 5: Commit** `Bound a Setup() that blocks its thread and cap logged write failures`.

### Task 2: Split the two large definition files (pure moves)

**Files:**
- Create: `Devices/EntityDirectory.cs`, `Devices/DefinitionSettings.cs`, `Devices/EntityLifecycle.cs`
- Modify: `Devices/DefinitionHost.cs`, `Devices/EntityApi.cs`

**Interfaces:**
- Produces: the same types and members, in new files: `EntityDirectory` and `LiveEntity` in `EntityDirectory.cs`; the conversion of stored setting values onto `[Setting]` properties (`ApplySettings` and its helpers) as an internal static class `DefinitionSettings` in `DefinitionSettings.cs`; the startup and library-rebuild half of the manager's entity code (`CompileSavedDefinitions`, `PruneEntities`, `RetryFailedDefinitions`, `CheckDefinitions`, `Reactivate`, `DefinitionLoadFailure` and what only they use) as a `partial class HassSharpManager` in `EntityLifecycle.cs`. `EntityApi.cs` keeps the request operations.

- [ ] **Step 1: Move the code.** No behaviour, signature, name or comment changes; `DefinitionHost` calls `DefinitionSettings.Apply(...)` where it called its private method.
- [ ] **Step 2: Verify the move is pure:** `git diff --color-moved=dimmed-zebra --stat HEAD` shows only moved lines apart from the new call site, `using`s and file headers.
- [ ] **Step 3: Run** `dotnet run --project tests/Runtime --disable-build-servers`. Expected: 318 or more PASS, exit 0.
- [ ] **Step 4: Commit** `Split the definition host and entity API into focused files`.

### Task 3: Pin the generated source of existing automations

**Files:**
- Create: `tests/Runtime/GeneratedSourcePinTests.cs`, `tests/fixtures/generated-source-pins.json`

**Interfaces:**
- Consumes: `VisualGenerator.Preview(JsonObject graph, VisualCatalogSnapshot snapshot)`, the cases of `tests/fixtures/visual-graph-validation.json` (map §3), `VisualTests.Graph()`.
- Produces: suite flag `--generated-source-pins` (runs by default). `generated-source-pins.json` is an object `{"<case name>": "<lower-case hex SHA-256 of Preview(...).Source as UTF-8>"}` holding every fixture case that is `valid`, has no `definition` in its graph, and previews as valid with the built-in catalog (apply the case's `catalogPatch` and `services` exactly as `VisualGraphContractTests` does), plus the key `"VisualTests.Graph"`. Helper `--describe-generated-source-pins` prints the object for the current code.

- [ ] **Step 1: Write the test and generate the pins at the current HEAD** with the helper. The test fails when a pinned case's hash differs, when a pinned case no longer exists, and when a valid automation case has no pin (message: add it with the helper *only* for a new case).
- [ ] **Step 2: Prove it bites:** temporarily add a top-level key to `visual_node_catalog.json`, run `-- --generated-source-pins`, see every pin fail; revert.
- [ ] **Step 3: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --generated-source-pins`. Expected: PASS.
- [ ] **Step 4: Commit** `Pin the generated source of existing automation graphs`.

### Task 4: Catalog, both validators and the shared fixture

**Files:**
- Modify: `visual_node_catalog.json`, `Visual/VisualNodeCatalog.cs` (`WithGraphTypes`, `DataInputs`, root helper), `Visual/VisualGraphValidator.cs`, `Visual/VisualGenerator.cs` (`EmbeddedCatalog` only), `frontend/src/visual/contract.ts`, the frontend palette sources (`frontend/src/visual/NodeLibraryPanel.tsx`, `QuickAdd.tsx`), the node listing the MCP tools use (`llm.py` / `authoring.py`, wherever catalog nodes are enumerated for discovery)
- Test: `tests/fixtures/visual-graph-validation.json`, `tests/Runtime/VisualGraphContractTests.cs`, `tests/Runtime/EntityDomainTests.cs` (new), `frontend/tests/visual-contract.test.ts` and a palette test beside the existing ones, `tests/python/test_mcp_adapter.py`

**Interfaces:**
- Consumes: Task 3's pins.
- Produces — catalog (exact keys; labels and descriptions are yours, within the wording rules):
  - Top-level `"entityDomains"`:
    ```json
    {
      "binary_sensor": { "label": "Binary sensor", "state": { "is_on": { "types": ["boolean"], "label": "On" } }, "commands": {}, "options": {} },
      "sensor": { "label": "Sensor", "state": { "value": { "types": ["number", "string"], "label": "Value" } }, "commands": {},
                  "options": { "unit": { "type": "string", "label": "Unit" }, "stateClass": { "type": "string", "enum": ["measurement", "total", "total_increasing"], "label": "State class" }, "options": { "type": "array", "label": "Options" } } },
      "switch": { "label": "Switch", "state": { "is_on": { "types": ["boolean"], "label": "On" } }, "commands": { "turn_on": { "label": "Turn on" }, "turn_off": { "label": "Turn off" } }, "options": {} }
    }
    ```
    State keys are the snapshot's `values` keys.
  - Node spec fields: `root` (bool: the node starts a flow), `documents` (array; `["entity"]` = entity graphs only), `singleton` (bool: at most one per graph), and `dynamicInputs: "entity"` (inputs are the `state` entries of the domain of the definition entity named by `config.entity`, each required, with no default).
  - Node kinds, all `minGraphVersion` 5 and `runtimeVersions` [5]:

    | kind | pure | root | documents | config | inputs | outputs | execution |
    |---|---|---|---|---|---|---|---|
    | `entity_state` | no | yes | entity | `entity` (string, required), `trackHistory` (boolean, default false) | dynamic `"entity"` | none | `[]` |
    | `entity_command` | no | yes | entity | `entity` (string, required), `command` (string, required), `trackHistory` (boolean, default true) | none | none | `["next"]` |
    | `entity_setup` | no | yes, singleton | entity | `trackHistory` (boolean, default true) | none | none | `["next"]` |
    | `set_entity_state` | no | no | entity | `entity` (string, required) | dynamic `"entity"` | none | `["next"]` |
    | `set_entity_available` | no | no | entity | `entity` (string, required) | `available` (`boolean`, required, default true) | none | `["next"]` |
    | `setting` | yes | no | entity | `key` (string, required) | none | `value`, typed as the setting | — |
    | `entity_is` | yes | no | any | none | `entity` (`entity_id`, required), `state` (`string`, required, default `"on"`) | `value` (`boolean`) | — |
    | `entity_current` | yes | no | any | none | `entity` (`entity_id`, required) | `value` (`global::HassSharp.EntityStateSnapshot`) | — |

    Category `Entity` for the first six, `Hass` for the last two.
  - `graph.definition` (what both validators read): `{"entities": [{"key", "domain", …}], "settings": [{"key", "type"}]}`. `WithGraphTypes` (C#) and `withGraphTypes` (TS) lift it into the per-graph catalog as `catalog.definition`, and resolve each `setting` node's `value` output type into `resolvedOutputs` from the setting's `type`.
- Produces — validation rules (same in both validators), with codes:
  - A node is a root when its kind is `trigger` or its spec has `root: true`. Scopes and reachability start from roots.
  - `graph.noTrigger` applies only to automation graphs. An entity graph needs no root.
  - `definition.invalid` (error, no node): `definition` is not an object of that shape, an entity's `domain` is not in `entityDomains`, or a key is repeated.
  - `node.document` (error): a node whose spec lists `documents` in a graph of another kind.
  - `node.singleton` (error, on every node after the first by id order).
  - `root.incoming` (error): an execution edge into a `root: true` node. `trigger.incoming` is unchanged for triggers.
  - `definition.unknownEntity` (error, `inputId` `entity`): `config.entity` is not a key of the definition. `definition.unknownCommand` (error, `inputId` `command`): not a command of that entity's domain. `setting.unknown` (error, `inputId` `key`).
  - `state.duplicate` (error, on the later node by id order): two `entity_state` nodes for one entity. `command.duplicate`: two `entity_command` nodes for one entity and command.
  - `state.dynamicSource` (warning, on the reader): an `entity_is` or `entity_current` node in the value-only upstream of an `entity_state` node has its `entity` input connected rather than chosen.
  - `command.unhandled` (warning, no node): an entity whose domain has commands has no `entity_command` for one of them.
- Produces — C#: `internal static bool VisualNodeCatalog.IsRoot(JsonObject node, VisualCatalogSnapshot? snapshot)`; `VisualGenerator.EmbeddedCatalog` leaves `entityDomains` out unless the graph has `definition`. TS: `Graph.definition?: GraphDefinition`, `NodeSpec.root?`, `NodeSpec.documents?`, `NodeSpec.singleton?`, `VisualCatalogSnapshot.entityDomains?`, `isRoot(node, catalog)`.
- Produces — hiding: nodes with `documents` that exclude automations are absent from the automation editor's palette and quick-add, and from the node list the MCP discovery tools return.

- [ ] **Step 1: Add the fixture cases** (fill `messages` and `codes` with `--describe-visual-graph` once the C# side exists): `entity graph: derived binary sensor`, `entity graph: empty graph is valid`, `entity graph: trigger root sets state`, `entity graph: command and setup roots`, `entity graph: sensor state from a setting`, `code definition.invalid: unknown domain`, `code definition.invalid: repeated key`, `code node.document: entity node in an automation`, `code node.singleton: two setups`, `code root.incoming: order wire into a state`, `code definition.unknownEntity`, `code definition.unknownCommand`, `code setting.unknown`, `code state.duplicate`, `code command.duplicate`, `code state.dynamicSource: connected entity`, `code command.unhandled: switch without flows`, `entity graph: state input of the wrong type`, `entity graph: step not reached from a root`, `automation: entity_is and entity_current feed a condition`.
- [ ] **Step 2: Write the other failing tests:** `EntityDomainsMatchTheSharpClasses` (`--entity-domains`: for each domain of `SharpDomains`, the state keys equal the keys of the typed class's `Values()`, the commands equal its `Commands`, the option keys equal `DomainOptions()`'s); `EmbeddedCatalogLeavesEntityDomainsOutOfAutomations`; a frontend test that the automation palette and quick-add list none of the six entity-only kinds and do list `entity_is`; `test_discovery_leaves_out_entity_only_nodes`.
- [ ] **Step 3: Run** `-- --visual-graph --entity-domains --generated-source-pins` and `bun run test`. Expected: the new cases FAIL; the pins PASS before any catalog edit.
- [ ] **Step 4: Implement** the catalog, then C#, then TypeScript, then the palette and discovery filters.
- [ ] **Step 5: Run** all five suites. Expected: PASS, with `--generated-source-pins` unchanged apart from the one new automation case's pin added with the helper.
- [ ] **Step 6: Commit** `Add entity nodes and entity domains to the catalog and both validators`.

### Task 5: Generator: roots, actions and state dependencies

**Files:**
- Modify: `Visual/VisualGenerator.cs` (root discovery about line 327, the per-root action about lines 334-346, the per-root method about line 356)
- Create: `Visual/StateDependencies.cs`
- Test: `tests/Runtime/EntityGraphGeneratorTests.cs` (`--entity-graph-generator`)

**Interfaces:**
- Consumes: `VisualNodeCatalog.IsRoot`, `EffectiveTriggerMethod(node)` (`"Run_" + hex(id)`), Task 4's kinds.
- Produces:
  - `internal static class StateDependencies { internal static IReadOnlyList<string> Of(JsonObject graph, string stateNodeId, VisualCatalogSnapshot snapshot); }` — the distinct entity ids, ordinal-sorted, chosen (not connected) on inputs of type `entity_id` of every value-only node upstream of that node through value wires.
  - Generated source for an entity graph: one `AutomationAction` and one `Run_<hex>()` method per root, in id order. `entity_state` → `Mode: Restart, Startup: true, Manual: false, Entities: [its dependencies]`. `entity_command` → `Mode: Queued, Startup: false, Manual: false`. `entity_setup` → `Mode: Single, Startup: false, Manual: false`. Triggers in an entity graph as in an automation.
  - `VisualPreview` for a graph with `definition` is otherwise as for an automation (same `Source`, `SourceMap`, `Graph`, `EmbeddedCatalog` fields).

- [ ] **Step 1: Write the failing tests:** `DerivedSensorHasOneRestartStartupActionWithBothSources` (the acceptance graph: one action, `Restart`, `Startup: true`, `Manual: false`, entities `binary_sensor.motion_a` and `binary_sensor.motion_b`); `DependenciesComeFromChosenEntitiesOnly` (a connected entity input adds nothing; a chosen entity two value nodes upstream is found; duplicates collapse; order is ordinal); `CommandAndSetupRootsBecomeActions` (modes and flags as above, method names `Run_<hex(id)>`); `ATriggerInAnEntityGraphKeepsItsOwnAction`; `GeneratedSourceCompiles` for each (through `CodeCompiler`, as `VisualExecutionTests.Execute` does); `AutomationSourceIsUnchanged` (delegates to the pins suite's check for `VisualTests.Graph`).
- [ ] **Step 2: Run** `-- --entity-graph-generator`. Expected: FAIL.
- [ ] **Step 3: Implement.** Existing trigger roots must take the same code path with the same output as before.
- [ ] **Step 4: Run** `-- --entity-graph-generator --generated-source-pins --visual --visual-execution`. Expected: PASS.
- [ ] **Step 5: Commit** `Generate actions for the roots of an entity graph`.

### Task 6: Runtime: the entity nodes and the channel to the host

**Files:**
- Modify: `Visual/VisualRuntime.cs` (`Walk` about lines 436-451, `Evaluate` about lines 490-520), `UserScriptManager/InvocationContext.cs`
- Create: `Devices/IDefinitionRuntime.cs`
- Test: `tests/Runtime/EntityGraphRuntimeTests.cs` (`--entity-graph-runtime`)

**Interfaces:**
- Consumes: Task 5's generated methods; `VisualRuntime.ReadEntity`.
- Produces:
  ```csharp
  /// What a run of an entity graph can do to its definition. Reached through the run in progress.
  internal interface IDefinitionRuntime
  {
      /// values: the state keys of the entity's domain (entityDomains[domain].state) to their new values.
      void SetState(string entityKey, IReadOnlyDictionary<string, object?> values);
      void SetAvailable(string entityKey, bool available);
      object? Setting(string key);   // string, bool or double; throws KeyNotFoundException for an unknown key
  }
  public sealed record InvocationContext(...) { /* adds */ internal IDefinitionRuntime? Definition { get; init; } }
  ```
  `ScriptSyncRunner` fills `Definition` from the running script's host when it has one (Task 9 makes the host implement it; until then it is null). Tests install a fake by running under an `InvocationContext` they build.
  - Node behaviour: `entity_state` evaluates its inputs and calls `SetState` then `SetAvailable(key, true)`; when evaluating throws, it calls `SetAvailable(key, false)` and the run fails with that error. `set_entity_state` calls `SetState`; `set_entity_available` calls `SetAvailable`. `setting` gives `Setting(key)`. `entity_is` is true when the entity exists and its state equals the `state` input, false otherwise (also for a missing or unavailable entity). `entity_current` gives the entity's state or throws `Entity '<id>' is unavailable.` as `read_entity` does. An entity node run without a definition throws `InvalidOperationException("This node only works in an entity definition")`.

- [ ] **Step 1: Write the failing tests** (compile and run in-process as `VisualExecutionTests` does, with `PyInterop.Entity` faked): `StateNodeSetsStateFromItsInputs`; `OrOfTwoSourcesFollowsEither` (the acceptance graph over the four on/off combinations); `StateNodeMarksTheEntityUnavailableWhenASourceIsMissing` (an `entity_current` source that does not exist: `SetAvailable(key, false)`, the run fails with the unavailable message; after the source appears a new run sets the state and `SetAvailable(key, true)`); `EntityIsIsFalseForAMissingEntity`; `SetStateAndSetAvailableStepsCallTheDefinition`; `SettingGivesItsValueAsItsType` (string, boolean, number); `SensorStateAcceptsNumberAndText`; `EntityNodesFailWithoutADefinition`; `EntityIsWorksInAnAutomation`.
- [ ] **Step 2: Run** `-- --entity-graph-runtime`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `-- --entity-graph-runtime --visual-execution --pure-nodes --generated-source-pins`. Expected: PASS.
- [ ] **Step 5: Commit** `Run entity nodes in the visual runtime`.

### Task 7: Definition document, compose and preview for graph logic

**Files:**
- Modify: `Devices/EntityDefinition.cs` (`DefinitionSetting`, the store), `Devices/DefinitionGenerator.cs` (`Preview`, line 36 refusal)
- Create: `Devices/DefinitionGraph.cs`
- Test: `tests/Runtime/EntityStoreTests.cs` (`--entity-store`)

**Interfaces:**
- Consumes: `VisualGenerator.Preview(graph, snapshot)`, Task 4's rules, `VisualNodeCatalog.EffectiveTriggerMethod`.
- Produces:
  - `DefinitionSetting(string Key, JsonNode? Value) { string? Label; string? Type; bool Secret; }` — the three new members are optional init properties, omitted from JSON when null/false, so class definitions' stored files do not change.
  - ```csharp
    internal static class DefinitionGraph
    {
        /// The graph with `definition` = { entities: body.Entities as given, settings: [{key, type}] }; any `definition` already in the graph is replaced.
        internal static JsonObject Compose(DefinitionBody body);
        /// The graph without `definition`.
        internal static JsonObject Decompose(JsonObject graph);
        internal static DefinitionRoots Roots(JsonObject graph, VisualCatalogSnapshot snapshot);
    }
    /// Method names of the graph's roots. Commands are keyed "<entityKey>:<command>".
    internal sealed record DefinitionRoots(string? Setup, IReadOnlyDictionary<string, string> Commands, IReadOnlyList<string> States);
    ```
  - `DefinitionGenerator.Preview` for `kind == "graph"`: checks the document, composes, runs `VisualGenerator.Preview`, and returns `DefinitionPreview` with the generator's `Source`, `SourceMap` and diagnostics (their `NodeId`s kept), plus new members `JsonObject? Graph` (composed) and `JsonObject? EmbeddedCatalog`. Document checks (no node), codes: `definition.entitiesRequired` (graph logic with no entity), `definition.entityKey` (pattern), `definition.duplicateEntity`, `definition.entityDomain` (not in `entityDomains`), `definition.entityOption` (an option the domain does not have, or of the wrong JSON type), `definition.settingType` (type not `string`/`boolean`/`number`, or a value that does not fit it), `definition.duplicateSetting`, `definition.graph` (logic has no graph object). `definition.graphUnsupported` is gone.
  - The store saves `logic.graph` decomposed and with layout filled as automations' are; `Rebuilt` and `RefreshDerived` regenerate graph definitions through the same path.

- [ ] **Step 1: Write the failing tests:** `GraphDefinitionSavesAndReadsBack` (the acceptance definition: source non-empty, stored graph has no `definition`, `revision` 1); `ComposeAndDecomposeAreInverse`; `ComposeCarriesNoSettingValuesOrSecrets`; `GraphDocumentChecks` (one case per code above, each naming the entity or setting in `InputId` or the message); `RemovingAnEntityStillNamedByANodeIsRefused` (code `definition.unknownEntity`, `NodeId` of the node); `RootsListSetupCommandsAndStates`; `ClassDefinitionFilesAreUnchanged` (a class definition saved before and after has byte-identical stored JSON apart from `savedUtc`); `RebuiltRegeneratesAGraphDefinition`.
- [ ] **Step 2: Run** `-- --entity-store`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `-- --entity-store --entity-api --entity-startup`. Expected: PASS (an API test that expected `definition.graphUnsupported` now expects a stored but not yet runnable definition: adjust it to assert the preview is valid, and leave activation to Task 9).
- [ ] **Step 5: Commit** `Validate, compose and store graph-defined definitions`.

### Task 8: Runs that report their outcome, and the command and setup causes

**Files:**
- Modify: `UserScriptManager/ScriptSyncRunner.cs` (`Run`, `RunCore`, `RunRecorded`), `UserScriptManager/UserScriptClass.cs` (`RunMethod`), `UserScripts/AutomationConfiguration.cs` (`InvocationCause`), the trigger payload builder (`TriggerPayload`)
- Test: `tests/Runtime/RunOutcomeTests.cs` (`--run-outcome`)

**Interfaces:**
- Produces:
  ```csharp
  public enum InvocationCause { Startup, Entity, Manual, Schedule, HomeAssistantTrigger, Command, Setup }   // two new members, appended
  /// Status is the run record's final status ("Succeeded", "Failed", "Cancelled", "Skipped", "Stopped" — the runner's own names).
  internal sealed record RunOutcome(string Status, string? Error);
  internal Task<RunOutcome> UserScriptClass.RunToEnd(string method, InvocationCause cause, System.Text.Json.JsonElement payload);
  internal static JsonElement TriggerPayload.Command(string entity, string command, JsonElement? data);
  internal static JsonElement TriggerPayload.Setup();
  ```
  `RunToEnd` completes when the run has finished (not when it was accepted), with the status and error the run record carries. Existing `Run`/`RunMethod` callers behave as before: a failed run is still logged and still does not throw to them.

- [ ] **Step 1: Write the failing tests:** `RunToEndReportsSuccess`; `RunToEndReportsTheErrorOfAFailedRun` (the method throws `InvalidOperationException("boom")` → `("Failed", "boom")`); `RunToEndReportsASkippedSingleRun`; `RunToEndReportsCancellation` (the script unloads mid-run); `QueuedRunsFinishInOrder`; `ExistingCallersStillDoNotSeeFailures`; `HistoryRecordsCarryTheNewCauses`.
- [ ] **Step 2: Run** `-- --run-outcome`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `-- --run-outcome --lifecycle --history-dispatch --visual-execution` (use the flags `Program.cs` lists for `LifecycleTests` and `HistoryDispatchTests`). Expected: PASS.
- [ ] **Step 5: Commit** `Let a caller await a run's outcome and add command and setup causes`.

### Task 9: Host and API for graph definitions

**Files:**
- Modify: `Devices/DefinitionHost.cs`, `Devices/EntityDirectory.cs`, `Devices/EntityApi.cs` (`CompileDefinition`, delete), `Devices/EntityLifecycle.cs` (`Regenerated`, `CheckDefinitions`), `Devices/SharpEntities.cs` (`SharpDomains`), `UserScriptManager/UserScriptManager.cs` (`LoadUserScript` root filter), `UserScriptManager/CompiledUserScript.cs`, `UserScriptManager/ScriptSyncRunner.cs` (fill `InvocationContext.Definition`)
- Test: `tests/Runtime/EntityGraphTests.cs` (`--entity-graph`), `tests/Runtime/ReloadMemoryTests.cs`

**Interfaces:**
- Consumes: `IDefinitionRuntime` (Task 6), `DefinitionGraph.Roots` and `DefinitionPreview.Graph`/`EmbeddedCatalog` (Task 7), `RunToEnd`, `TriggerPayload.Command/Setup` (Task 8), `VisualTrigger(NodeId, Label, TrackHistory)`.
- Produces:
  - `CompiledDefinition` gains `JsonArray? Entities` (the document's entity descriptions; null for a class definition) and `DefinitionRoots? Roots`.
  - `internal static SharpEntity SharpDomains.Create(string domain, JsonObject description)` — the typed entity for a document entry (`key`, `name`, `icon`, `deviceClass`, `entityCategory`, `enabledByDefault`, `options`).
  - A graph definition's compiled script has `DesignatedRootType = "VisualAutomation"`, `Definition` set, and the visual run metadata automations have (`VisualId` = the definition id, `VisualRevision`, `VisualGraph`, `VisualCatalog`, `VisualTriggers` with one entry per root: `TrackHistory` from the node's `trackHistory`, defaulting as the catalog says). `LoadUserScript` gives such a script both its classes and a host.
  - `DefinitionHost` for a graph definition: builds the entities with `SharpDomains.Create` (no user class, no `[Setting]` reflection); the sync JSON has the same shape as a class definition's (`device` = the document's device, named after the definition, or null); it implements `IDefinitionRuntime` (calls after stop are ignored; a `SetState` key the domain lacks throws); it runs the setup root with `RunToEnd(..., InvocationCause.Setup, …)` inside `SetupTimeout` before the sync, failing the prepare with the run's error; a command runs the matching command root with `RunToEnd(..., InvocationCause.Command, …)` and throws `InvalidOperationException(<the run's error>)` unless it succeeded; a command with no root throws `InvalidOperationException("<entity name> has no flow for <command label>")`.
  - Entity delete forgets the script's run-history identity (`DeleteScriptAsync` rather than `UnloadScriptAsync`), for class and graph definitions alike.

- [ ] **Step 1: Write the failing tests** (through `EntityRequest` on an `EntityTestHome`, with `manager.RunEntries(entityId, state)` to change sources): `DerivedMotionSensorFollowsItsSources` (save the acceptance definition: sync JSON has one `binary_sensor` with unique id `<id>:any_motion` and `device: null`; the first push has `is_on` from the sources' startup states; turning `binary_sensor.motion_a` on pushes `is_on: true`; both off pushes `false`); `AnUnavailableSourceMakesTheEntityUnavailableUntilItReturns`; `TheLastOfSeveralQuickChangesWins` (a slow value node, three changes in a row: the final push matches the third); `ASwitchRunsItsCommandFlows` (`turn_on` flow sets the state on: `EntityCommand` completes and a push shows `is_on: true`); `AFailingCommandFlowFailsTheCommand` (the flow's error text is the exception message; no push); `ACommandWithoutAFlowFails` (message names the entity and `Turn on`); `SetupRunsBeforeTheSyncAndItsFailureFailsTheSave` (`activationError` carries the flow's error, nothing synced, the previous revision keeps running); `ADeviceDefinitionGroupsItsEntities` (three entities under the document's device); `SettingsReachTheGraph`; `ReplacingARevisionKeepsCustomisedEntities` (rename an entity key in the document: after a successful sync the old unique id is gone and the others are untouched); `DisableEnableAndDelete`; `ALibrarySaveRebuildsAGraphDefinitionThatUsesALibraryNode`; `StartupLoadsGraphDefinitions`; `ReactiveRunsAreNotRecordedAndCommandRunsAre` (history rows by cause); `DeleteForgetsTheRunHistory`; extend the reload-memory test with a graph definition replaced 100 times.
- [ ] **Step 2: Run** `-- --entity-graph`. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the full runtime suite. Expected: PASS, exit 0.
- [ ] **Step 5: Commit** `Host graph-defined definitions`.

### Task 10: Smoke, live check and suites

**Files:**
- Modify: `tests/packaging/entity_bridge_smoke.py`, `AGENTS.md` (only if the smoke's arguments change)

**Interfaces:**
- Consumes: everything above, published.

- [ ] **Step 1: Extend the smoke** with a graph definition (a binary sensor that is on when a faked source entity is on, using `PyInterop.Entity` as the existing smokes fake it, and a switch with `turn_on`/`turn_off` flows): the sync JSON, a pushed state after the source changes, and both commands' effects arrive in Python as `str`; a command with no flow raises with the message. Break one expectation deliberately and see exit 1.
- [ ] **Step 2: Publish, restart `hass` once, check its log** for a clean load (as in phase 1), and run the smoke in the container: `docker exec -i -e PYTHONDONTWRITEBYTECODE=1 hass python - /config/custom_components/hass_sharp /config/custom_components/hass_sharp/dotnet < tests/packaging/entity_bridge_smoke.py`. Expected: `entity bridge smoke OK`.
- [ ] **Step 3: Live check in a throwaway `hass_wt` container** (port 5002, this checkout's integration directory mounted, onboarded through the REST API with made-up credentials; removed with `down -v` afterwards). Over the websocket API:
  1. Create two helpers with `input_boolean/create` (`Motion A`, `Motion B`).
  2. `hass_sharp/entities` save of `any_motion`: one `binary_sensor` entity (device class `motion`), graph = two `entity_is` nodes on the helpers, `or`, `entity_state`. Expect `active: true`.
  3. `get_states`: `binary_sensor.any_motion` is `off`. `input_boolean.turn_on` on A → `on`; on B as well → `on`; both off → `off`.
  4. Save a second definition `desk_plug` with a device, a switch `relay` whose `turn_on`/`turn_off` flows set its own state, and a sensor `power` set by a 10-second time-pattern trigger flow. `switch.turn_on` → state `on`; the sensor gets a value.
  5. Reload the integration (REST `POST /api/config/config_entries/entry/<id>/reload`): same entity ids, states come back.
  6. An existing automation saved before the check still has the same `revision` after the reload.
  7. Delete both definitions: registries clean.
  Report each step's observed result, and warnings logged by `custom_components.HassSharp`.
- [ ] **Step 4: Run all five suites,** and the Python suite inside the `hass` container as `AGENTS.md` describes. Expected: all pass, none skipped in the container.
- [ ] **Step 5: Commit** `Cover graph definitions in the entity bridge smoke`.

---

## Carried forward to later phases

Findings from phase 1's reviews that this phase does not take. Each later plan copies its own section forward.

### Phase 3 (panel)

- (T6) currentRevisionActive/activationError formulas duplicated from VisualApi.cs:16,20-21 (EntityApi.cs:31,36-37)
- (T6) a disabled definition answers document.activationError "Not active" but top-level null (EntityApi.cs:37 vs :77)
- (T6) EntityJson reads the host status twice (EntityApi.cs:24,37); a host stopped between the reads could answer status "stopped"
- (T7) a Setup() failure at Init is logged as "Cannot activate script <path>", not the definition form
- (T6) a failure after the store write answers invalid_request although the store changed (EntityApi.cs:150,155)
- Websocket failure code for hass_sharp/entities is entity_request_failed. Secret settings are returned in plain text by list/read: mask in the panel.

### Phase 4 (tools and more types)

- (T1) ValueJson misses Half/nint/BigInteger (Half.NaN publishes "NaN" text) — SharpEntity.cs:118-137
- (T1) DateTime with Kind Unspecified is published without an offset; HA timestamp sensors reject naive datetimes — SharpEntity.cs:134
- (T1) IReadOnlyDictionary-only attribute values serialize as an array of pair strings — SharpEntity.cs:145-146
- (T10) no test for a user's sensor unit override surviving a resync (entity_bridge.py:296-297 writes the shown unit to the registry)
- Prepare definitions off Init's critical path or concurrently; retry a failed Setup() with backoff (today retried only on a services/entities schema push, each retry under _visualGate for up to SetupTimeout); revisit SetupTimeout (60 s) when downloads arrive.

### Phase 6 (docs)

- Renaming an entity key creates a new entity (the unique id changes) and the new one may get a _2 entity id while the old registry entry still exists during the sync; say so in docs/custom-entities.md.
- A command or callback that stops its own definition waits out RunDrainTimeout (10 s); commands answer 'The entity is not running' during a replace; Stop() is not called at Home Assistant shutdown.

### Not tied to a phase

- (T4) ~25 lines of Delete/List copied from VisualStore (EntityDefinition.cs:103-116,186-201)
- (T7) CheckDefinitions and DefinitionLoadFailure.Located mirror the graph code's structure (EntityApi.cs:222-239 vs NodeLibraryApi.cs:255-269; Manager.cs LoadFailure)
- (T3) local `All` type walker beside `TypesIn` (NodeLibraryCompiler.cs:698 vs :558); a [Setting] typed by a generic base's type parameter is always an error
- Automations whose own C# serializes their own types with System.Text.Json default options pin their load context (pre-existing, same cause as the definition leak fixed in phase 1): clear in UserScript.ClearCoreAsync after DrainRunsAsync.
- entity_bridge.py docstring overclaims ('a sync that fails leaves the revision running as Home Assistant showed it'): a failed sync still updates live entities and the device in place.
- The unreadable-definition-file log dedupe is static and survives integration reloads (EntityDefinition.cs:78).
- (T8) a Start() that overruns the drain timeout can refill the cache after the clear (DefinitionHost.cs:224-228)
- (T5) DependencyTracking.Unsubscribe logs before unsubscribing; a throwing log skips that entity's unsubscribe (pre-existing behaviour)
- (T5) zero pump debounce would push to Python under both the pump and host locks (EntityStatePump.cs:105) — same root as the T2 debounce minor; DependencyTracking calls PyInterop.Subscribe/UnSubscribe under its own lock (pre-existing)
- (T2) debounce value unchecked — zero runs the push under the gate lock, negative faults the timer and stalls the pump (EntityStatePump.cs:13,102)
- (T2) a failed push keeps the batch ids' old sent values; wrong if Python wrote part of the batch (EntityStatePump.cs:74-78)
- (T2) error log prints the whole exception on every failed push (EntityStatePump.cs:76); a throwing Logger.Error escapes FlushAsync (:105)
- (T5) a Start() failure that coincides with a stop is never logged (DefinitionHost.cs:168-170)
- (T6) blanket catch around the Started wait (EntityApi.cs:74)
- (T6) EntityCommand passes non-object dataJson through; malformed JSON surfaces as JsonException (EntityApi.cs:193)
- (T6) after a removal timeout a late Python `fail` faults unobserved; a late removal could remove the entities of a definition re-created under the same id (EntityApi.cs:169) — carry into T9: removals and syncs for one definition are applied in order on the loop
- (T9) colon in a definition id would misattribute prune/remove matches — not reachable: the store validates ids with ^[a-z0-9][a-z0-9_-]{0,79}\z (R8); an explicit assert would document it
- (T9) an operation scheduled while the loop is stopping is never answered (entity_bridge.py:132) — shutdown only
- (T9) a partially failed sync answers only the error; entity ids of the entities it did add are dropped (entity_bridge.py:223-224)
- (T10) a platform unload returning False leaves the bridge closed with the entry loaded — entities freeze on last state (__init__.py:358-363); comment or reopen
- (T10) EntityBridge created before the try in async_setup_entry (__init__.py:260-264), stranded if AuthoringService raises — pre-existing pattern

### Test and tooling items

- (T1) tests — weak not-running assertion (SharpEntityTests.cs:103), State freshness not provable (:204), MemoryStream as no-op disposable (:20-21)
- (T2) Reset check is timing-sensitive, 20 ms window (EntityPumpTests.cs:120-125)
- (T3) untested edge cases — int?/Mode?/long/double settings, enum from outside the library, class deriving SharpEntity directly (R7), nested public class, base in another library source
- (T4) test gaps — non-definition JSON ({} / null / []), file name vs inner id mismatch, object/array setting value, "true" as text, enum case, Rebuilt with an invalid preview
- (T5) test gaps — long/nullable-int settings, StopAsync twice on a started host, a Track handle disposed by user code; key checks reached by reflection
- (T5) TrackOffLock test can raise an unobserved ObjectDisposedException when it is already failing (EntityHostTests.cs:391-404)
- (T6) test cleanup catches only IOException (EntityApiTests.cs:135)
- (T6) negative check with a 60 ms wait (EntityApiTests.cs:226-228)
- (T7) no test for library delete through CheckDefinitions; EntityStartupTests.cs:37-45 re-declares helpers EntityApiTests has
- (T8) reload-memory suite now ~50 s and ~93–106 MiB with steps up to 6 MiB — watch for flakes in CI
- (T9) the disabled-entity test checks only the first aborted object after the second sync; the [] prune test has no definition of this config entry to purge (test_entity_bridge.py:376-392,550-557)
- (T10) test_entity_platforms.py:116-118 patch.dict(sys.modules) drops modules after every test (order-dependent, slow) — patch only the System entry
- (T10) unload_platforms test helper skips async_wait_closed (test_entity_platforms.py:151-155)
- (T10) no test that the delegates are assigned before the platform forward (test_entry_lifecycle.py:400-419 checks only before Init)
- (T10) container run noise from test_native_triggers and test_mcp_adapter; plan test commands need PYTHONPATH=tests/python
- (T11) test_visual.py:143-156 fake switches its answer by rebinding a closure variable; all websocket handlers are now named `run` (websocket.py:61) — set __name__ for tracebacks
- (T12) smoke compares threads by get_ident() in a set that never forgets — ident reuse could flake (entity_bridge_smoke.py:57,82,94,198); use current_thread() and _DummyThread
- (T12) a malformed payload ends in a bare traceback instead of FAIL: (entity_bridge_smoke.py:207,224,236-238); unexpected stand-in delegate calls only printed, not asserted (:274)
- (T12) spike erratum carries two details from HA's source rather than the tests ("removal due in 2027.8", the quoted DeviceInfoError text) (spike.md:91,95)

---

## Outcome of phase 2, and what it hands on

Phase 2 ended at `7e4accb` (23 task commits and 5 fix-wave commits after `207c814`). All five suites pass (runtime 388 PASS lines); both packaging smokes pass in the container; the live check against Home Assistant 2026.11.1 passed every step of Task 10 and of the fix wave's re-check.

Decisions taken during the phase that the spec and the tasks above do not state:

- `entity_is` is labelled **Has state**; a second value node, **Current state** (`entity_current`), reads any entity's state. Both work in automations.
- An entity graph is a graph with an **object** `definition`; a non-object one is ignored. The automation API drops an object `definition` from a client's graph. An entity graph needs no root.
- A State that fails makes its entity unavailable and logs one warning per reason (naming the node), one info line on recovery, the detail at debug. A value its domain refuses fails the State the same way.
- A sensor a graph publishes takes: with `options`, one of them; with a unit or state class, a number or numeric text; otherwise any number or text.
- A command waits at most 30 s (`DefinitionHost.CommandTimeout`); Home Assistant has no limit of its own. A command or setup flow that ends at a Stop step has succeeded.
- A failing command, setup or trigger flow of a definition logs one line naming the definition and the flow (warning for a command, error otherwise).
- Trigger flows in a definition are not recorded in run history unless the node sets `trackHistory: true` (host default, `EntityApi.TrackHistory`). Command and setup runs are recorded; State runs are not.
- A new revision of a graph definition starts each entity (same key and domain) from the running revision's state; a setup flow or a State overrides it.
- Setting values are not part of the generated source; a library save rebuilds every definition, as it does every automation, and its answer masks secret setting values.

### For phase 3 (panel)

- There is no TypeScript compose/decompose yet. The editor must compose `definition` (entities and settings schema) into the graph before validating — otherwise `graphDocument` reads it as an automation — and decompose before saving.
- `config.entity`, `config.command` and `config.key` of the entity nodes are free strings: the panel needs pickers fed from `catalog.definition` and `entityDomains`. Entity-only kinds are hidden from the automation palette (`catalogPalette`); the entity editor needs the opposite filter.
- The flow code still tests `kind == "trigger"`: generalise to "is a root" in `flow/flowModel.ts`, `flow/autoConnect.ts`, `layout.ts`, `graph.ts`, `FlowAdapter.tsx`.
- History default: `addGraphNode` (`contract.ts`) copies every spec default into config and `TriggerConfig.tsx` shows `trackHistory !== false`, so a trigger added in the panel would be recorded on every tick. Add a document-aware `trackHistoryDefault(kind, document)` used by both, mirroring `EntityApi.TrackHistory`.
- Run history: `hass_sharp/entities` has no history operation; a definition's runs are read through `hass_sharp/run_history` with the document's `automationId`. `AutomationRuns.tsx` shows the causes `Command` and `Setup` as raw names and their trigger summary as a generic "Trigger"; snapshot graphs of a definition's runs carry `definition`. A `run` operation for definitions is not built.
- A command's error carries no node name (`RunOutcome.Error` is the innermost message): the run trace must show where it failed.
- `command.unhandled` has a null `nodeId` (show it at definition level) and names the entity by key while the command error uses its name: say "name (key)" in both validators and the fixture.
- A definition's save diagnostics include compiler warnings of unrelated node libraries (`nodeId` starting `$library:`), as an automation's do.
- Secret settings: the entity API's list, read and save answers return values in clear. Mask them with a round trip (keep the stored value when the mask comes back).
- An entity's key: make it read-only after the first save, or offer an explicit rename (the bridge updates the registry entry's unique id before the sync). A plain rename through the API creates a new entity whose id ends in `_2`.
- `get_capabilities` carries `entityDomains` for automations too (harmless).

### For phase 4 (tools, more domains, restore)

- A domain is four entries: `SharpDomains` typed class, `SharpDomains.Graph` (state check and apply), the catalog's `entityDomains`, and a Python platform. `EntityDomainTests` pins the first three to each other.
- `entity_command` has no outputs: a flow cannot read a command's data (a number's value, a light's brightness). `entityDomains.commands` needs a data schema and the node needs outputs.
- `dynamicInputs: "entity"` makes every state key a required input and `IDefinitionRuntime.SetState` expects every key: light and cover need optional inputs and a merge.
- First state of an entity: after a reload or restart, states set only by command or trigger flows come back `unknown` (no restore yet). Over a new revision they are carried from the running one, with two gaps to close here: a command or trigger handled by the old revision while the new one is being prepared is overwritten by the earlier copy (carry again after the sync for keys the setup flow did not set, or read the old host's final values once it is invalidated); and an entity that a failing State left unavailable stays unavailable in a revision that no longer has that State (do not carry availability for keys that had a State).
- A failing poll (a trigger flow) logs an error on every tick: dedupe per reason as State does. A State that never finishes leaves its entity unavailable with no log: add a slow-State warning.
- A secret setting that flows into another node's inputs is traced there in clear, and reaches State warnings and run errors through refusal texts, which quote the value.
- `options: []` passes the document check and then refuses every value; a sensor with a numeric device class but no unit or state class takes any text.
- A number a graph computes as NaN or infinity is refused by the runtime, so the entity is unavailable (a class definition's NaN shows as unknown).

### For phase 6 (MCP and docs)

- A State updates only for entities chosen on its nodes' pins; a library or Custom C# node that reads an entity through `NodeContext.State` does not make it a dependency.
- Entities of a definition without a device get ids from their name alone (`binary_sensor.any_motion`): name them with clashes in mind.
- The history default for trigger flows in a definition, the Stop rule and the 30 s command limit belong in the MCP authoring guidance.

### Not tied to a phase

- Turning a definition off cancels its runs with the runner's "reloaded" reason, so a cancelled command says the definition was reloaded.
- Between the old revision being invalidated and the new one registering, a command still reaches the old host, which ignores its state changes (as in phase 1: commands answer 'The entity is not running' during a replace).
- `DefinitionGenerator`'s optional diagnostics provider falls back to a process-wide static (production always passes it).

### Test and tooling items

- `tests/python/test_entity_bridge.py`: the write-failure cap test could assert exactly six lines, separate records per entity, and silence after a repeat. No test blocks inside an entity's (not the device's) `Setup()`.
- `EntityHostTests`: a test's `finally` can miss a thread on an already-failing path. `EntityGraphRuntimeTests`: two tests overwrite `PyInterop.Log` without restoring it; `FakeDefinition.Calls` is a plain list.
- `RunOutcomeTests`: the thread test sets `Entered` before `Register` (it can only pass falsely, in a microsecond window); `Within` relabels a `TimeoutException` thrown by the awaited task.
- `EntityGraphTests`: the setup flow's "cancelled" text is untested (it needs the run's own cancellation to win a race with the definition's lifetime).
- Commits `1c3a3a9`, `888e011`, `5698e38` and every commit from `4c690ed` on carry a "Claude Opus 5.5" co-author trailer (the implementing agents' harness rule); the others carry "Claude Fable 5.2".

# Entity nodes, phase 5: the class kind of definition is deleted

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An entity definition is a node graph and nothing else. "Use a class from the node library" is gone from the New dialog, which asks for a name and opens the node editor. The form editor for a class, the `types` operation, the `entities_types` tool, the generated `SharpDefinition`, the `main` device and every branch on `logic.kind == "class"` are deleted. C# is written in a C# entity or C# device node (phase 4). The tests that were the only cover of behaviour the C# nodes share with the class kind are moved onto C# nodes before the class path goes.

**Architecture:** Tests move first, deletions follow, one layer at a time so every commit is green: (1) the host gets one production seam, `CompiledDefinition.Create`, so host-level tests can hand it a component without compiling, and the host tests move onto one-component graph definitions; (2) the manager-level fixtures move onto graphs with C# nodes; (3) the frontend stops offering and opening the class kind; (4) Python and the examples stop describing it; (5) the C# paths are deleted and the store answers a class document on disk as it answers any document of an earlier development build; (6) documents and comments say what is left.

**Tech Stack:** C# on .NET 11 (`custom_components/hass_sharp/HassSharp/`, custom test runner in `tests/Runtime/`), SolidJS with TypeScript 7 and vitest 5 (`custom_components/hass_sharp/frontend/`), Python 3 (`custom_components/hass_sharp/*.py`, `tests/python/`). `CC/` below is `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`.

**Spec:** `docs/superpowers/specs/2026-11-19-entity-nodes-design.md` (decision 6 "Class-from-library kind is deleted", phase 5). Deletion map for this phase: `docs/superpowers/plans/2026-11-20-entity-nodes-phase-5-map.md` (written at `122b347`, before phase 4's Tasks 8 and 9 landed): every pointer in it is a place to look, with line numbers that have moved; find the symbol. Where the map and the code disagree the code wins.

## Global Constraints

- **Class kind against class nodes.** "Class kind" is a definition with `logic.kind: "class"`. "Class nodes" are the kinds `csharp_entity` and `csharp_device` (`IsClassNode`, `isClassNode`, `ClassSignature`, `CustomNodeSource.IsClass`): they are the graph kind and nothing of them is removed or changed in behaviour.
- **Every commit leaves these green**, run from the repository root:
  - `python3 -m unittest discover -s tests/python` (also in the test container, as the controller's notes say)
  - `python3 -m unittest discover -s tests`
  - `dotnet run --project tests/Runtime --disable-build-servers`
  - `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`
  - `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`
  - after `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`: the entity bridge smoke and the SQLite smoke
- **Nothing changes for graphs.** An automation's generated source is byte-identical before and after every task (the 117 pins in `tests/fixtures/generated-source-pins.json` pass unchanged), and so is the source of the entity graph examples (`EntityGraphGeneratorTests.AnEntityGraphWithoutCSharpNodesKeepsItsSource` passes unchanged). The payload the host sends Home Assistant for a graph definition is byte-identical. `CC/visual_node_catalog.json` is not edited.
- **A test is moved, not dropped, when it is the only cover.** A class-kind test whose behaviour a C# node shares (set-up order, settings applied before `Setup()`, restore, hot replace order, start and stop, timers and tracks, cameras' frames, time limits, failure sentences, the store's revisions) is rewritten onto a C# node and keeps every assertion that is still true, with the new unique ids (`<definition id>:<key>` for a C# entity node, `<definition id>:<device key>:<entity key>` for an entity a C# device adds) and the sentences that name the node. A moved test must still be able to fail: after moving it, check that each wait and lookup by unique id finds something (a restore test that restores nothing, or a camera test whose lookup throws early, passes for the wrong reason). A test is deleted only when what it covers is deleted. Each task's report lists every test it deleted and why, and every test it moved and to where.
- **No migration.** Nothing has shipped. A class document on disk, at any version, is listed as unreadable with the reason the store already gives older graph documents (`saved by an earlier development build; delete it and build it again`), with Delete as its only action. The document version stays 3.
- **Exact copy.**
  - A request whose `logic.kind` is not `graph`: code `definition.logic`, message `A definition is built from nodes: logic is {kind: "graph", graph}. Write C# in a C# entity or C# device node`
  - An operation `types`: the existing unknown-operation answer, nothing special.
  - New dialog: title `New entity definition`, the existing line `Set it up in the editor, then save it: its entities appear in Home Assistant.`, the field `Entity definition name` with its hint, and the existing buttons; no "How it works" choice.
  - Empty state: heading `No entity definitions yet`, text `An entity definition creates new entities and devices in Home Assistant. Build what they do with nodes.`, one button `New entity definition`.
  - `Phrase` for a whole number whose range is the type's own: `a whole number` (today `a whole number from -2147483648 to 2147483647`); a declared range reads as today.
- **Names that go** (no reader may remain; the last task greps for them): `SharpDefinition`, `PrepareClass`, `CreateCompiled`, `MainDevice`, `MAIN_DEVICE`, `CompiledDefinition.Factory`, `CompiledDefinition.Device`, `DefinitionVia.Definition`, `DefinitionPreview.Type`, `DefinitionLogic.Type`, `VisualCatalogSnapshot.EntityTypes`, `EntityTypeDescriptor.Id`, the `types` operation, `entities_types`, `ClassDefinitionEditor`, `classSession`, `classDefinitionCode`, `class_definition`, `EntityType`, `EntityTypeSetting`, `entityTypesQueryKey`, `DeviceMemory`, `deviceMemory`, `DeviceSection`, `DeviceParent`, `withDetail`, `withVia`, `definition.unknownType`, `definition.unknownSetting`.
- **Names that stay.** `SetUpClass` and `RestoreClass` (renamed `SetUpComponent` and `RestoreComponent`, without the `named` flag: every failure names its node), `DefinitionSettings.Apply`, `DefinitionGenerator.Fits` / `Within` / `Phrase`, `EntitySettingDescriptor`, `EntityClasses`, `DescribeEntityType`, the check of every library class's `[Setting]` properties (`CheckSettings`, which also gives a node's class the settings it inherits from a library base class), `DeviceForm.tsx`'s `deviceFields` / `PartOf` / `PartOfParents` / `PartOfVia` (the Device node's section uses them), `DefinitionBody.Device` as a member that is only refused (`definition.deviceNotAllowed`, phase 3's sentence), `definition.entitiesNotAllowed`.

## Review Focus

1. **A class document on disk** (version 1, 2 or 3). It is listed unreadable with the standard reason and Delete; reading it answers the same reason; start-up skips it and every other definition starts; its Home Assistant entities stay (unavailable) until it is deleted, and deleting it removes them. It never reaches the node editor. Tests in Task 6.
2. **A C# node whose class derives from a library class.** The settings it inherits are in its signature, and a bad `[Setting]` in the library is still reported in the library, after the library's class list is gone. Tests in Task 3, kept green by Task 6.
3. **A moved test that passes for the wrong reason.** Unique ids changed, so a restore, a camera lookup or a wait by id can find nothing and still pass. Each mover checks by breaking the behaviour once (Tasks 1 to 3; the reviewer of each asks for the evidence).
4. **A client that still speaks the class kind**: `logic.kind: "class"`, a `device` in the body, the `types` operation, the `entities_types` tool. Each is refused in words that say what to do; none crashes or is silently accepted. Tests in Tasks 5 and 6.
5. **An old link.** `/entities/new?logic=class&type=…` opens the node editor with the name, ignoring what it does not know. Test in Task 4.

---

### Task 1: The host takes a component from its definition, and the host tests run C# nodes

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionHost.cs` (`CompiledDefinition`), `CC/HassSharp/Devices/DefinitionHost.Graph.cs` (`CreateNode`), `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition` sets `Create`)
- Test: `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/EntityToolsTests.cs` (the three host-level tests and their harness)

**Interfaces:**
- Produces: `CompiledDefinition.Create : Func<string, object?>?` — what makes the class of the node with that id. `EntityApi.CompileDefinition` sets it to the compiled assembly's `SharpNodes.Create` for a graph with C# nodes (null when the graph has none); `CreateNode` calls it and nothing else. Its behaviour for a real definition is unchanged (same thread, same clock, same sentences).
- Produces, tests: one harness `Compiled(...)` in `EntityHostTests` that builds a graph-kind `CompiledDefinition` with `Entities = []`, one `DefinitionComponent` per class under test (kind `device` or `entity`, a key, settings) and `Create` returning the test's own instance; `EntityToolsTests` uses the same harness.

**Behaviour:**
- Every one of the 28 tests of `--entity-host` and the three host-level tests of `EntityToolsTests` (`FailedPrepareStops`, `SetupLimit`, `ClientOwnership`) runs through the graph path with one C# node, asserting what it asserts today, with: unique ids as Global Constraints give them; failure sentences that name the node (`{Definition}: {Name}: …`); a C# entity's key the node's key.
- `EntityHostTests.DocumentDevice` (a class document's own device over the class's: the `main` device) is deleted: nothing of it exists for a graph. No other test is deleted in this task.
- `EntityHostTests.Settings`: the refusal sentences for an unknown, ill-typed and out-of-range saved value stay asserted where a graph can still reach them (`DefinitionSettings.Apply` through a component's settings); a sentence only the class kind's request check gave is listed in the report for Task 3 to move to the generator's tests.
- The class path (`PrepareClass`, `Factory`) still exists after this task and nothing in production calls the new seam differently: only the tests moved.

- [ ] **Step 1:** Add `Create` and make `CreateNode` use it; run every suite: green with no test changed.
- [ ] **Step 2:** Move the harness and the tests, one group at a time, running `-- --entity-host` after each; for each moved group break the behaviour once (for example skip the restore, or the `Stop()` call) and see its test fail, then restore. Record the evidence.
- [ ] **Step 3:** Run every suite. **Step 4: Commit** — `The host makes a C# node's class through its compiled definition, and the host tests run C# nodes.`

---

### Task 2: The API, start-up and graph tests build their plug as a C# device node

**Files:**
- Test: `tests/Runtime/EntityApiTests.cs` (`Library`, `Definition`, `Plug`, `Scenario`, `RunsATriggerStep`), `tests/Runtime/EntityStartupTests.cs`, `tests/Runtime/EntityGraphTests.cs` (the class halves the map's section 4 lists)
- No product file changes. If a test cannot be moved without one, stop and report it.

**Behaviour:**
- The shared fixtures (`EntityApiTests.Library` / `Definition` / `Plug`, used by all three files) become: the library keeps its `FakePlug` class; `Definition` is a graph with one C# device node whose code is `public class Plug : NodeLib.FakePlug { }` (or the fixture's class written in the node where a test needs to edit it), its settings wired or typed as the class definition's settings were.
- `EntityApiTests.Scenario` keeps every check that is not about the class kind: save, read, list and preview; two definitions of one class with their own settings; commands; a failed resave keeps the old revision taking commands; the stale-revision conflict; a resave restores the switch; an invalid preview is not stored; `Setup()` throws → failed and nothing synced; `Start()` throws → active and failed; the list counts the device; off and on; `enabled` refused on a resave; activating a revision that is off unloads; the run-now refusal; the unknown operation. It loses the `types` checks and the assertion on `new global::NodeLib.FakePlug()` in the source. `RunsATriggerStep` loses the "A class definition has no steps to run" lines.
- `EntityStartupTests`: every test keeps its subject (`Restart`, `SlowSetup`, `SetupThatBlocksItsThreadTimesOut`, `PruneFails`, `LibraryRebuild`, `EditedOnDisk`, `Retry`, `ForgetsTheStatesOfDefinitionsGone`, `CannotStartWarnsOnceInPlainWords`); `UnknownClass` becomes a node whose base class the library no longer has. `ManyCompilerErrorsAreCapped` and `DoesNotCompileWarnsWithoutAPath` (a compile error at load, capped and located by library): reach them with a library edited on disk after the definition was saved; if no graph can reach a load-time compile error, say so in the report with what you tried, and keep the two branches covered by calling the load-failure formatter directly.
- `EntityGraphTests`: the class halves of the mixed tests (secrets masked in every answer, a kept secret, a library save answering no secret, the source map, delete forgetting history, a definition that cannot start and the library save that warns again) move onto a C# device node with a secret `[Setting]`; `AClassRequestsDeviceIsStored…` and `AnOlderClassDocumentStillLoadsAndRuns` stay untouched in this task (Task 6 rewrites them).

- [ ] **Step 1:** Move the fixtures and `Scenario`; run `-- --entity-api`. **Step 2:** `EntityStartupTests`; **Step 3:** the `EntityGraphTests` halves. After each, break the behaviour once per moved group and record the failing test.
- [ ] **Step 4:** Run every suite. **Step 5: Commit** — `The API, start-up and secret tests build their plug as a C# device node.`

---

### Task 3: Cameras, the store, the leak check and the smoke test run on C# nodes

**Files:**
- Test: `tests/Runtime/CameraTests.cs`, `tests/Runtime/ReloadMemoryTests.cs`, `tests/Runtime/EntityStoreTests.cs` (`Store`, `Diagnostics`' phrase cases, the mixed tests' class halves), `tests/Runtime/EntityToolsTests.cs` (`LibraryDevice`), `tests/Runtime/CustomNodeTests.cs` and `tests/Runtime/EntityGraphGeneratorTests.cs` (the moved checks), `tests/packaging/entity_bridge_smoke.py`
- No product file changes. If a test cannot be moved without one, stop and report it.

**Behaviour:**
- `CameraTests` (13 tests, the only cover of `SharpCamera`'s `Show`, `Frame()`, `OnFrame`, one frame at a time, the interval, the last good frame, the size limit, cancelling on stop, replace, the snapshot, no libjpeg): each class camera becomes a `csharp_entity` node; unique ids go from `<id>:main` to `<id>:<node key>`.
- `ReloadMemoryTests` (`Populate`, `RunAll`): the leak check's class device (a timer, a track, a service call in `Start()`, `Http`) becomes a C# device node.
- `EntityToolsTests.LibraryDevice`: a C# device node deriving the library's device (downloads in `Setup()`, polls with `Http` from `Every()`).
- `EntityStoreTests.Store` (revisions, enabled, conflict, `Rebuilt`, delete, ids and list skipping an unreadable file, the strict list, recovery at revision 0, no `.tmp` left): on a graph body. The class-only tests (`AClassRequestKeepsItsOwnRefusal`, `AClassRequestKeepsItsDevice`, `ClassSettingsKeepTheirShape`, the class halves of `TheListCountsADefinitionsDevices` and `KeepIsNeverStored`) are deleted here only where their subject is a class request's shape; `AnOlderClassDocumentStillLoads` and `ClassDefinitionFilesAreUnchanged` stay until Task 6 rewrites them.
- Moved checks, so that nothing only the class kind tested is lost: the one-sided range phrases (`of at least 1`, `of at most 60`, `of at least 0.5`) to `EntityGraphGeneratorTests` (through `setting.value`); from `EntityTypeTests`: a class name in words as a title (`Shelly plug pro`, `HTTP sensor`) and `[Node]` on a class making no step to `CustomNodeTests`; **a C# node whose class derives from a library class inherits its `[Setting]`s in its signature, and a library's bad `[Setting]` is reported at the property in the library** to `CustomNodeTests` (new; the map's first risk). `EntityTypeTests.cs` itself stays until Task 6.
- `entity_bridge_smoke.py`: the class plug and the class cameras (Still, Lens, Push) become C# nodes of a graph definition; it still drives the five entity delegates and prints what it prints. Phase 4 left one small class-kind case: it goes.

- [ ] **Step 1:** `CameraTests`; **Step 2:** the store and the moved checks; **Step 3:** `ReloadMemoryTests`, `LibraryDevice`; **Step 4:** the smoke test (publish, run it in the container). After each, break the behaviour once per moved group and record the failing test.
- [ ] **Step 5:** Run every suite and both smokes. **Step 6: Commit** — `Cameras, the store, the leak check and the smoke test run on C# nodes.`

---

### Task 4: The panel builds entity definitions with nodes only

**Files:**
- Delete: `FE/src/entities/ClassDefinitionEditor.tsx`, `FE/src/entities/classSession.ts`, `FE/tests/ClassDefinitionEditor.test.tsx`
- Modify: `FE/src/views/EntitiesListView.tsx`, `FE/src/views/EntityDefinitionView.tsx`, `FE/src/views/RunTraceView.tsx`, `FE/src/entities/DefinitionCard.tsx`, `FE/src/entities/DeviceForm.tsx`, `FE/src/entities/definitionEdits.ts`, `FE/src/entities/adapter.ts`, `FE/src/entities/api.ts`, `FE/src/entities/model.ts`, `FE/src/styles/entities.css`
- Test: `FE/tests/EntitiesListView.test.tsx`, `FE/tests/EntityDefinitionView.test.tsx`, `FE/tests/entity-api.test.ts`, `FE/tests/entity-model.test.ts`, `FE/tests/RunTraceView.test.tsx`, `FE/tests/visual-fixture.ts`

**Behaviour:**
- **New.** "New entity definition" (the page's button and the empty state's) opens the dialog of Global Constraints: a name, then `/entities/new?name=…`, which opens the node editor on the starter graph. No choice, no `types` query. `/entities/new?logic=class&type=x&name=Plug` opens the same editor with the name.
- **Opening.** `EntityDefinitionView` has one page: the node editor. The class read branch, `classPage`, `classSaved` and the `class_definition` code are gone; `RunTraceView` loses its branch for it.
- **Card.** No kind label (`Nodes` / the class's title), no "class not found" hint; the card of a graph definition is otherwise pixel-identical (its tests unchanged).
- **`DeviceForm.tsx`** keeps `deviceFields`, `deviceChoice`, `PartOf`, `PartOfParents`, `PartOfVia` (without `definition`); `DeviceMemory`, `deviceMemory`, `DeviceSection`, `DeviceParent`, `parentSources`, `definitionChoice`, `withDetail`, `withVia` and the imports only they used go. The Device node's and the C# device's sections behave as before (their tests unchanged).
- **Types.** `EntityType`, `EntityTypeSetting`, `api.types`, `entityTypesQueryKey`, the class arm of `DefinitionLogic`, `DefinitionVia`'s `definition` arm go. Members the server still answers until Task 6 (`logic.type: null`, a document's `device: null`) stay typed as optional so that nothing breaks between the two tasks; Task 6 removes them.
- **CSS.** Rules only the deleted markup used go (`hs-cdef*` except what the loading section uses, `hs-def-device`, `hs-ent-card__logic--missing`, `hs-ent-card__hint`, `hs-ent-starts`, `hs-ent-start`, `hs-ent-choices`, `hs-ent-choice`): check each selector has no user left before deleting it.
- Exports that lose their only outside user keep their code and lose `export`.

- [ ] **Step 1: Write the failing tests:** the dialog has a name field and no radio; submitting goes to `/entities/new?name=…`; the empty state's copy and single button; `/entities/new?logic=class&type=x&name=Plug` renders the node editor named Plug; a card has no kind label; the list page makes no `types` request.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Run** the frontend suite, `tsc`, the build.
- [ ] **Step 5: Commit** — `A new entity definition is named and opens in the node editor: the class editor is gone.`

---

### Task 5: The MCP tools and the examples describe node definitions only

**Files:**
- Modify: `CC/llm.py` (the prompt, `SEMANTICS`, the `DEFINITION` schema, `ENTITY_SPECS`, the hints, `definition_summary`), `CC/entity_bridge.py` (`MAIN_DEVICE`, `_via`'s `definition` arm), `CC/authoring.py` (docstrings), `CC/authoring_examples.json` (only if an example or a title still speaks of a class definition)
- Test: `tests/python/test_mcp_adapter.py`, `tests/python/test_entity_authoring.py`, `tests/python/test_entity_bridge.py`, `tests/python/test_entity_platforms.py`, `tests/python/test_visual.py`, `tests/test_docs_examples.py`, `tests/Runtime/AuthoringAdapterTests.cs`

**Behaviour:**
- The `entities_types` tool is gone (`ENTITY_READS` and every list of tools in tests and descriptions with it). The `DEFINITION` schema has the graph alternative only; a body with `logic.kind: "class"` is refused by the schema or by the server with the sentence of Global Constraints (pin whichever answers, in `test_mcp_adapter`).
- `definition_summary` has one shape: every entity `{key, domain, name, device, entityId}`; no `logic` member (it could only say `graph`); the branch for a document without a graph is gone.
- The prompt, `SEMANTICS["definitions"]` and the tool descriptions say nothing of a class definition, its device, `via: {definition}` or `registeredEntities` being "a class's entities"; they keep what is true of C# nodes.
- `_via` resolves `{deviceId}` and `{device}`; a `{definition}` parent is treated as no parent (it can no longer be sent); `MAIN_DEVICE` is gone. The device and entity identifiers the bridge makes and prunes are unchanged.
- An example whose title still says "from a class" is renamed (`Plug in C#`) in the file, the documents' markers, `test_docs_examples.py`, `test_entity_authoring.py` and `AuthoringAdapterTests.cs` together.
- The server still accepts a class request after this task (Task 6 removes it): the Python tests no longer send one, except the refusal case, which is written to pass both before and after Task 6 only if the schema refuses it; otherwise that one test is added in Task 6.

- [ ] **Step 1: Write the failing tests** (no `entities_types` in the tool list; the summary's single shape; the description pins). **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Run** every suite; the Python suite in the container.
- [ ] **Step 5: Commit** — `The MCP tools build entity definitions from nodes only.`

---

### Task 6: The class kind is deleted from the library, and a class document on disk reads as an earlier build's

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionGenerator.cs`, `DefinitionHost.cs`, `DefinitionHost.Graph.cs`, `EntityApi.cs`, `EntityDefinition.cs`, `EntityLifecycle.cs`, `DefinitionSettings.cs` (docs only), `CC/HassSharp/Visual/NodeLibraryCompiler.cs`, `VisualCatalogSnapshot.cs`, `CC/HassSharp/CompiledUserScript.cs` (doc)
- Modify (follow-ups of the answers' shape): `FE/src/entities/model.ts`, `FE/tests/visual-fixture.ts`, `CC/llm.py` (schema's `type`), their tests
- Delete: `tests/Runtime/EntityTypeTests.cs` (its three kept checks moved in Task 3)
- Test: `tests/Runtime/EntityStoreTests.cs`, `tests/Runtime/EntityGraphTests.cs`, `tests/Runtime/EntityApiTests.cs`, `tests/python/test_mcp_adapter.py`

**Interfaces:**
- Produces: `EntityDefinitionStore.Read` throws `JsonException(EarlierBuild)` for a document whose `logic.kind` is `class` at any version, and for a graph document below version 3; `Complete` accepts version 3 only. `CompiledDefinition(…)` without `Factory` and `Device`. `SetUpComponent` / `RestoreComponent` (renamed, no `named`). `EntityJson(document)` and `DeviceCount(document)` without the libraries they no longer need. `VisualCatalogSnapshot` without `EntityTypes`. `DefinitionLogic` without `Type`; `EntityDefinitionDocument` without `Device`.

**Behaviour:**
- **Requests.** `Preview` accepts `logic.kind: "graph"` only; anything else is `definition.logic` with the sentence of Global Constraints. `Generate`, the class `Settings` check, `definition.unknownType` and `definition.unknownSetting` are gone. A body's `device` is still refused with phase 3's sentence; `entities` with `definition.entitiesNotAllowed`. The `types` operation is gone: it answers as any unknown operation does.
- **Host.** `PrepareAsync` prepares a graph. `PrepareClass`, `CreateCompiled`, `Factory`, the host's own `device` field, `MainDevice`, `DeviceOf`'s fallback, the `main` entry of `Devices()` and `DeviceJson`'s `{Definition}` arm are gone; `IsHub` is "has no node". Every component failure names its node (the `named` flag is gone).
- **Compiler.** The library's class list (`EntityTypes(...)`'s descriptors and the snapshot member) is gone; the check of every library class's `[Setting]` properties stays, and a node's class still inherits a library base class's settings (Task 3's test).
- **Store.** As Interfaces say. A class file at version 1, 2 or 3: listed unreadable with `EarlierBuild` and deletable; `read` answers the reason; start-up skips it and starts the others; `Ids()` still names it, so its Home Assistant entities are kept until it is deleted; deleting it at revision 0 removes it. `AnOlderClassDocumentStillLoads`, `ClassDefinitionFilesAreUnchanged` (`EntityStoreTests`) and `AnOlderClassDocumentStillLoadsAndRuns` (`EntityGraphTests`) are rewritten to say this; `AClassRequestsDeviceIsStored…` is deleted.
- **Answers.** A read, list or save answer has no `logic.type` and no top-level `device`; the frontend's types and fixture and the MCP schema drop them in this commit.
- **Phrase.** A whole number whose range is the type's own reads `a whole number` (Global Constraints), with its test in `EntityGraphGeneratorTests`.
- **Secrets.** `WithoutSecretValues` masks a graph's secret settings exactly as today (its tests unchanged); only its class branch and the parameters that fed it go.

- [ ] **Step 1: Write the failing tests:** the three class files (v1, v2, v3) unreadable, deletable, skipped at start-up beside a graph definition that starts; a `class` request refused with the sentence; `types` an unknown operation; the phrase.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Delete and simplify.** **Step 4:** grep the "Names that go" list over `CC/` (outside `out/`, `bin/`, `obj/`, `node_modules/`), `tests/` and `FE/`: no hit but the refusal's own test strings. **Step 5: Run** every suite, publish, both smokes.
- [ ] **Step 6: Commit** — `An entity definition is a node graph: the class kind is deleted, and a class file on disk reads as an earlier build's.`

---

### Task 7: The documents and comments describe what is left

**Files:**
- Modify: `docs/custom-entities.md`, `docs/backend/entity-definitions.md`, `docs/mcp-authoring.md`, `docs/node-libraries.md`, `README.md`
- Modify: comments and doc comments the map's section 6 lists in `CC/HassSharp/Devices/`, `CC/HassSharp/Visual/`, `FE/src/entities/`, `FE/src/views/`, `CC/*.py`; test names and doc comments that still say "class definition" or "main device"
- Test: `tests/test_docs_examples.py`, the wording checks of the runtime suite and the Python tests

**Behaviour:**
- The user guide has no "Classes" chapter about a definition made from a class and no New-dialog walk-through with a choice: what it says of `SharpDevice`, `SharpEntity`, `[Setting]`, `Setup` / `Start` / `Stop`, timers, `Http` and cameras is the C# nodes' API, told once. The sentence about files of earlier development builds also covers a definition that was a class.
- The backend document has no `types` section, no class body, no `via: {definition}`, no `main` device; `docs/mcp-authoring.md` has no `entities_types` step (its steps renumbered) and no class example.
- `docs/node-libraries.md` "Entity classes": a library can hold a base class; a C# entity or C# device node uses it by deriving from it in its own code, and inherits its `[Setting]`s.
- Comments: no comment or doc comment in product code describes the class kind as existing. `docs/superpowers/**` is history and is not edited.
- `grep -rn "entities_types\|SharpDefinition\|kind.*class\|class definition\|from the node library" CC docs README.md tests FE/src FE/tests` (outside build output and `docs/superpowers/`) returns nothing that describes the class kind (the refusal sentence and its tests remain).

- [ ] **Step 1:** Run the grep, list the hits. **Step 2:** Rewrite. **Step 3: Run** every suite (the docs word-for-word test, the wording checks). **Step 4: Commit** — `The documents describe entity definitions as node graphs only.`

---

## Rulings

1. **Tests move before code goes.** Most host behaviour (order, restore, time limits, cameras, the store's revisions) is tested only through the class kind; deleting first would delete the cover with it. Cost if wrong: three tasks of test work that a later reader finds over-careful.
2. **One production seam, `CompiledDefinition.Create`.** The host already makes a node's class through the compiled assembly's `SharpNodes.Create`; holding that delegate in the compiled definition lets a host test hand in its own instance without a second, test-only path. Cost if wrong: one member to inline.
3. **No version bump; a class document is refused by its kind.** Nothing a version-3 graph document holds reads differently. Cost if wrong: none; the check is one line.
4. **`device` in a request stays a refused member**, with phase 3's sentence, and `logic.kind: "class"` gets a sentence of its own: an MCP author who learned the old shape is told what to do instead of being ignored. Cost if wrong: two messages to delete later.
5. **`logic.type` and a document's `device` leave the answers.** They could only be null. Cost if wrong: a client that read them gets undefined instead of null.
6. **The summary loses its `logic` member and the card its kind label.** There is one kind. Cost if wrong: one label to put back.
7. **The bridge's handling of an earlier build's bare `<definition id>` device is left alone.** It is clean-up for instances that ran an earlier development build, not the class kind. Cost if wrong: a few lines that never run.
8. **A library save is still refused while an unreadable definition file exists** (the store's strict list), now also for a class file: the user deletes it from the Entities page first, as the guide says of earlier builds. Cost if wrong: one confusing refusal for someone who kept a class definition from a development build.
9. **A load-time compile error may be unreachable for a graph** (preview compiles it fully): Task 2 tries a library edited on disk and otherwise keeps the two formatter branches under a direct test. Cost if wrong: two branches with only unit cover.

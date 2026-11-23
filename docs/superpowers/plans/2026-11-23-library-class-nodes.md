# Library Classes as Nodes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A device or entity class in a node library, marked `[Node]`, is a node of its own in an entity definition's step list, with its settings as inputs and no code.

**Architecture:** The library compiler adds one catalog entry per marked class, of kind `library:<library id>:<type identity>`, built from the matching C# node's built-in entry without `source` and `signature` and carrying the class's settings as `classSignature`. A class node's settings are read through one function that prefers the entry's `classSignature` to the node's `config.signature`; "has code" becomes "is a Custom C# step, or a class node whose entry has no `classSignature`". The generated factory creates the library class directly. Everything that named `csharp_entity` / `csharp_device` to mean "class node" reads the catalog entry's facts instead.

**Tech Stack:** C# (.NET 11, Roslyn) in `custom_components/hass_sharp/HassSharp/`; TypeScript/SolidJS/vitest in `custom_components/hass_sharp/frontend/`; Python in `custom_components/hass_sharp/*.py`; the runtime suite in `tests/Runtime/`.

**Spec:** `docs/superpowers/specs/2026-11-23-library-class-nodes-design.md`

## Global Constraints

- The kind is `library:<Uri-escaped library id>:<Uri-escaped type identity>`, the type identity being `TypeIdentity(type)` as node methods use it, with no method name. It starts with `library:`.
- The catalog entry's member for the settings is `classSignature`, of the shape `DescribeClassNode` writes today: `{title, description, domain, settings:[{key,label,description,type,secret,options?,minimum?,maximum?,domain?,required?}]}`.
- A library class node's stored config has `key`, `arguments`, and for a device `name` and `via`. It has no `source` and no `signature`; one that holds them is never compiled or shown as code.
- The generated source of every existing graph stays byte for byte (`GeneratedSourcePinTests`, `tests/fixtures/generated-source-pins.json`). No pin is edited.
- Both validators agree: every validation case added goes into `tests/fixtures/visual-graph-validation.json`, which the C# and the TypeScript suites both read.
- `tests/Runtime/` is compiled by hass-sharp-vision as `HassSharp.RuntimeTests`: add helpers, never rename one, and leave `RuntimeTestAccess.cs`'s `InternalsVisibleTo` alone.
- A lock in new C# code is a `System.Threading.Lock` field taken with `lock`; never across an `await`.
- A C# device or C# entity node (`csharp_device`, `csharp_entity`) behaves exactly as before, including a class that derives from a marked library class.
- Commit messages are one plain sentence, no prefix, no trailer. Never push, tag or merge. Never use an `mcp__hass__*` tool. Read diffs with `git --no-pager diff --no-ext-diff`.
- Suites: `dotnet run --project tests/Runtime --disable-build-servers -- <--suite>`; `(cd custom_components/hass_sharp/frontend && bun run test <file>)`; `python3 -m unittest discover -s tests/python -p <file>`.

## The fixture library

Tasks 1 to 3 and 7 use one library, id `relays`. Write it against the real API as the existing fixtures do (`LibraryBaseSettings` in `tests/Runtime/CustomNodeTests.cs` is the model); its facts are fixed:

- `RelayHub : SharpDevice`, `[Node("Relay hub", Category = "Relays")]`, summary "A hub of relays.", settings in this order: `Address` (string, required: not nullable, no initialiser), `Token` (string, secret, optional), `PollEvery` (number, label "Poll every", 1 to 60, default 5). Its constructor adds one switch keyed `relay`.
- `Relay : SharpSwitch`, `[Node("Relay")]`, summary "One relay.", one setting `Address` (string, default "").
- `Helper : SharpSwitch`, unmarked.
- `BaseRelay : SharpSwitch`, `[Node("Base relay")]`, abstract.

Its two kinds are `library:relays:global%3A%3ANodeLib.RelayHub` and `library:relays:global%3A%3ANodeLib.Relay`.

## Review Focus

- A library class node pasted or imported into an **automation**: refused with the existing `node.document` error, as a C# node is. (Task 2)
- A value **typed into a secret setting** of a library class node: refused by both validators as for a C# node (`SecretLiteral`), and stripped from drafts and exports. (Tasks 2, 5)
- A library class node whose stored config holds a stray **`source` or `signature`** (hand-edited, or a file made by converting a C# node): the code is not compiled, the node has no code button, and its settings are the catalog's. (Tasks 2, 5)
- A class **title that makes no key** (`"2 Relays!"`, `"Détecteur"`): the dropped node still gets a valid, free key (`device`, or the entity's domain, as today's fallback). (Task 4)
- **Two libraries offering a class of the same title**: both are in the step list, their kinds differ, and dropping both gives two keys. (Tasks 1, 4)

---

### Task 1: The compiler offers marked library classes

**Files:**
- Modify: `custom_components/hass_sharp/HassSharp/Visual/NodeLibraryCompiler.cs` (after `CheckLibrarySettings(classes, compilation, trees)`; `DescribeEntityType`, `EntityClasses.Creatable` are reused)
- Modify: `custom_components/hass_sharp/HassSharp/Visual/VisualCatalogSnapshot.cs`
- Modify: `custom_components/hass_sharp/HassSharp/Visual/VisualNodeCatalog.cs:58-114`
- Modify: `custom_components/hass_sharp/HassSharp/Visual/NodeAttribute.cs` (summary only)
- Test: `tests/Runtime/CustomNodeTests.cs` (suite `--custom-nodes`)

**Interfaces:**
- Produces: `public sealed record LibraryClassDescriptor(string Kind, string SourceId, string TypeExpression, string Declares)` (`Declares` is `"device"` or `"entity"`); `VisualCatalogSnapshot.Classes : ImmutableDictionary<string, LibraryClassDescriptor>`, keyed by kind.
- Produces: catalog entry per marked class: a deep copy of the built-in `csharp_device` (or `csharp_entity`) entry with `config.source`, `config.signature` and `keywords` removed, and `label` = title, `category` = the attribute's `Category`, `description` = the class's summary (empty when none), `sourceId` = the library's id, `minGraphVersion` and `runtimeVersions` as a node method's entry has them, `classSignature` = the signature.
- Produces: `VisualNodeCatalog.HasCode(spec)` is true for `dynamicInputs` `"custom"`, and for `"settings"` only when the entry has no `classSignature`. `VisualNodeCatalog.ClassSignature(node, spec)` returns the entry's `classSignature` when it has one (well formed, no setting keyed as one of the kind's own pins), else `config.signature` as today. `IsClassNode` is unchanged.

- [ ] **Step 1: Write the failing tests** in `CustomNodeTests.cs`, registered where `ClassTitles` is:
  - `AMarkedLibraryClassIsAStep`: previewing the fixture library gives no error; `Catalog["nodes"]` has both kinds; the hub's entry has `label` "Relay hub", `category` "Relays", `description` "A hub of relays.", `declares` "device", `declaration` true, `documents` `["entity"]`, `sourceId` "relays", `dynamicInputs` "settings", `argumentStorage` "arguments", config members exactly `key`, `name`, `via`, `arguments`, and `classSignature.settings` keys `Address`, `Token`, `PollEvery` in that order with `Address.required` true, `Token.secret` true, `PollEvery.minimum` 1 and `maximum` 60; the relay's entry has `declares` "entity", `category` "Custom", `classSignature.domain` "switch", config members exactly `key`, `arguments`, and the `device` input of `csharp_entity`. `snapshot.Classes` has both kinds with `TypeExpression` `global::NodeLib.RelayHub` and `global::NodeLib.Relay`.
  - `AnUnmarkedOrUncreatableLibraryClassIsNoStep`: no kind names `Helper` or `BaseRelay`; the diagnostics hold exactly one warning (severity 2) at `BaseRelay`'s name, saying it has `[Node]` but is not offered as a step and why; `Helper` draws none.
  - `ALibraryClassNodeHasSettingsAndNoCode`: for a node of the hub's kind with config `{key:"hub", name:"Hub", arguments:{}}`, `IsClassNode` is true, `HasCode` is false, `ClassSignature` is the entry's, and its data inputs are `Address`, `Token`, `PollEvery`; the same node with `config.signature` set to a signature of one other setting still gives the entry's three. For a `csharp_device` node all four answers are as before.
  - `TwoLibrariesMayOfferTheSameTitle`: two libraries, ids `a` and `b`, each with a differently named class marked `[Node("Relay hub")]`: two kinds, both labelled "Relay hub".
  - Change `ClassTitles`: `[Node]` on a class in a C# node's code is still only its title and makes no step.
- [ ] **Step 2: Run** `dotnet run --project tests/Runtime --disable-build-servers -- --custom-nodes`. Expected: the four new tests FAIL (no such kind, no `Classes`).
- [ ] **Step 3: Implement.** In `Preview`, after `CheckLibrarySettings`: for each class declared in a library tree that has the `[Node]` attribute and for which `classes.Kind(type)` is a device or an entity, either add its entry and descriptor (when `DescribeEntityType` gives one and every setting key is a valid pin id and, for an entity, none is `device`), or report the warning at its identifier. The signature JSON is built by the code `DescribeClassNode` builds it with today: move that into one helper both use. Change `HasCode` and `ClassSignature` as the interfaces say. Correct `NodeAttribute`'s summary: on a library's device or entity class it makes the class a node.
- [ ] **Step 4: Run** the suite again. Expected: all PASS, and `dotnet run --project tests/Runtime --disable-build-servers -- --generated-source-pins` (or the suite that holds `GeneratedSourcePinTests`) PASS unchanged.
- [ ] **Step 5: Commit** `git add -A custom_components/hass_sharp/HassSharp tests/Runtime` then `git commit -m "A node library's device or entity class marked [Node] is offered to entity definitions as a kind of its own, with its settings in its catalog entry."`

### Task 2: A definition with a library class node validates, generates and lists its devices and entities

**Files:**
- Modify: `custom_components/hass_sharp/HassSharp/Visual/VisualNodeCatalog.cs:176-300` (`DeclaredEntities`, `DeclaredDevices`, `CSharpEntities`)
- Modify: `custom_components/hass_sharp/HassSharp/Visual/VisualGenerator.cs:155-163, 384-408, 530-560`
- Modify: `custom_components/hass_sharp/HassSharp/Visual/VisualGraphValidator.cs` (`MethodOf` and every place a `library:` kind is taken for a node method; `Words.EntityName`/`StepName` lines 108-115; line 1242)
- Modify: `custom_components/hass_sharp/HassSharp/Devices/DefinitionHost.Graph.cs:200-203` (`Called`), `custom_components/hass_sharp/HassSharp/Visual/NodeLibraryCompiler.cs:518`
- Test: `tests/Runtime/EntityGraphGeneratorTests.cs` (`--entity-graph-generator`), `tests/Runtime/VisualGraphContractTests.cs`, `tests/fixtures/visual-graph-validation.json`

**Interfaces:**
- Consumes: Task 1's `snapshot.Classes`, `HasCode`, `ClassSignature`.
- Produces: `DeclaredDevices` lists a library class device exactly as a C# device (`key`, `name`, `via`, `csharp: true`); `CSharpEntities` lists a library class entity exactly as a C# entity, its `domain` from the entry's `classSignature`; `DeclaredEntities` skips it as it skips `csharp_entity`. All three decide by `IsClassNode(spec)` and `Declares(spec, …)`, not by kind name; an unknown declaring kind that is no class node is still refused.
- Produces: the generated `SharpNodes.Create` has one case per class node in node order: `new <snapshot.Custom[id].TypeExpression>()` for a C# node, `new <snapshot.Classes[kind].TypeExpression>()` for a library class node. No code section is emitted for a library class node.

- [ ] **Step 1: Write the failing tests.**
  - In `EntityGraphGeneratorTests.cs`:
    - `ALibraryClassNodeGetsAFactoryAndNoCode`: a graph with one hub node (id `hub`, `Address` typed) previews valid; the source contains `"hub" => new global::NodeLib.RelayHub(),` and no `// Relay hub node "hub"` section.
    - `ALibraryClassNodeBesideACSharpNode`: a graph with the hub node and a `csharp_device` node whose class derives `RelayHub`: both cases are in the factory, in node order, and the C# node's code section is there.
    - `ALibraryClassNodesSettingValueMustFit`: `PollEvery` typed 0 gives the error a C# node's out-of-range setting gives (`setting.value`).
    - `ALibraryClassNodesStrayCodeIsNotCompiled`: the hub node with `config.source` set to text that does not compile and a `config.signature` of other settings previews valid, with the entry's three settings.
    - `ComponentsResolveALibraryClassNodesSettings`: `DefinitionGraph.Components` gives one component for the hub (kind "device", key, name, title "Relay hub", `Address` resolved, `Token` from a wired Setting node, `PollEvery` its literal), and lists `Address` under `Unset` when it has no value.
    - `DevicesAndEntitiesListALibraryClassNode`: `DerivedDevices` has the hub with `csharp` true; `CSharpEntities` has the relay with domain `switch`, name null and the key of the Device node it is wired to.
  - In `tests/fixtures/visual-graph-validation.json`, cases the C# suite reads now (the TypeScript suite reads them in Task 4): a valid hub node; `Address` unset → "is required"; a literal typed into `Token` → the secret-literal error; `Address` wired from a node that is no Setting → `scope.settingSource`; two hub nodes with one key → `definition.duplicateDevice`; a relay and an Entity node with one key → `definition.duplicateEntity`; a hub node in an automation document → `node.document`; a node of kind `library:relays:global%3A%3ANodeLib.Gone` → the missing-library-node message; a graph of one hub node and no Entity node → no `definition.entitiesRequired`. The fixture's catalog for these cases carries the two entries as Task 1 writes them.
- [ ] **Step 2: Run** `-- --entity-graph-generator` and the suite of `VisualGraphContractTests`. Expected: FAIL (the declaring kind is refused: "declares a device that no list knows how to describe"; no factory case).
- [ ] **Step 3: Implement** the three lists, the factory, and the `upToDate` set (a library class node is always up to date, so `SettingValues` checks it). Make `MethodOf` and its callers answer null for a class kind without throwing. Messages that say "C# device node" or "C# entity node" for a node name a library class node by its entry's label.
- [ ] **Step 4: Run** both suites and the generated-source pins. Expected: PASS, pins unchanged.
- [ ] **Step 5: Commit** `git commit -m "An entity definition's graph validates, lists and generates a library class node as it does a C# node of that class, creating the library's class directly."`

### Task 3: It runs, and a library change that breaks it is refused

**Files:**
- Modify (only if a test shows the need): `custom_components/hass_sharp/HassSharp/Devices/DefinitionHost.Graph.cs`, `custom_components/hass_sharp/HassSharp/Devices/EntityLifecycle.cs:169-215`, `custom_components/hass_sharp/HassSharp/Visual/NodeLibraryApi.cs:301-339`
- Test: `tests/Runtime/EntityHostTests.cs`, `tests/Runtime/DeviceUiHostTests.cs` (`--device-ui-host`), `tests/Runtime/DeviceDataTests.cs` (`--device-data`), `tests/Runtime/NodeLibraryTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 2.
- Produces: no new interface. This task proves the run time and settles section 5 of the spec.

- [ ] **Step 1: Write the failing tests** (add a page and a counter to the fixture hub for these: in `Start()` it draws one list with one card and one action, and each `Start()` adds 1 to `Data` key `starts`).
  - `ALibraryClassNodeRuns` (`EntityHostTests`): a definition with the hub node keyed `hub` starts; its device and its switch `…:hub:relay` are reported; `Address` and `PollEvery` are applied; `Token` comes from a secret Setting.
  - `ALibraryClassNodeHasAPageAndData` (`DeviceUiHostTests`): its page is read under node key `hub`, its action runs, and `starts` is 2 after a re-save.
  - `SwappingACSharpNodeForTheLibraryNodeKeepsEntitiesAndData` (`DeviceDataTests`): a definition with a `csharp_device` node keyed `hub` whose class derives `RelayHub` runs once; saved again with that node replaced by a hub node of another id and the same key, the entity's unique id is the same and `starts` is 2.
  - `ALibrarySaveThatRemovesAUsedClassIsRefused` (`NodeLibraryTests`): saving the library without `RelayHub`, or with its `[Node]` removed, while a definition uses the node, is refused naming the definition; the definition keeps running.
  - `ALibrarySaveThatRemovesAUsedSettingBehavesAsARemovedParameter`: first read what a library save does today to a definition whose **method** node has a value for a parameter the save removes (find the test that covers it, or write one), and assert the same outcome for removing `PollEvery` while a hub node has a value for it. Say in the report which outcome it is.
  - `ALibrarySaveThatAddsARequiredSettingIsRefused`: adding a required string setting without a default to `RelayHub` is refused naming the definition; adding one with a default is accepted and the definition is rebuilt and keeps running.
- [ ] **Step 2: Run** the four suites. Expected: the new tests FAIL or PASS; record which. A test that passes at once is kept: it pins the behaviour.
- [ ] **Step 3: Implement** only what the failing tests need.
- [ ] **Step 4: Run** the whole runtime suite: `dotnet run --project tests/Runtime --disable-build-servers`. Expected: exit 0.
- [ ] **Step 5: Commit** `git commit -m "A library class node runs as a C# node of its class does, keeps its entities and data under its key, and a library save that removes its class or adds a required setting is refused."`

### Task 4: The TypeScript contract mirrors it

**Files:**
- Modify: `custom_components/hass_sharp/frontend/src/visual/contract.ts` (`NodeSpec`; `isClassNode`/`hasCode`/`classSignature` 612-674; `declaredEntities`, `declaredDevices`, `csharpEntities` 275-418; `dataInputs` 971; unknown-kind check 1620; `addGraphNode` 2277-2301)
- Test: `custom_components/hass_sharp/frontend/tests/visual-contract.test.ts`, `custom_components/hass_sharp/frontend/tests/entity-node.test.ts`, fixtures `tests/library.ts`, `tests/visual-fixture.ts`

**Interfaces:**
- Consumes: the catalog entry of Task 1 (the frontend receives it in the catalog).
- Produces: `NodeSpec.classSignature?: ClassSignature`; `hasCode(spec)` and `classSignature(node, catalog)` with Task 1's meaning; `declaredEntities`, `declaredDevices`, `csharpEntities` with Task 2's meaning; `addGraphNode` for a library class kind gives `key` = the first free key from the title's key form (`relay_hub`, `relay_hub_2`; when the title makes no key, `device` for a device and the domain's key for an entity, as today) and, for a device, `name` = the title. A C# node's key and name are made as before.

- [ ] **Step 1: Write the failing tests.** `visual-contract.test.ts` already runs every case of the shared fixture: it now FAILS on Task 2's cases. In `entity-node.test.ts`: `a library class node has its class's settings and no code` (mirror of Task 1's third test, including the stray `source`); `dropping a library device names and keys it by its title` (`relay_hub`, then `relay_hub_2`, name "Relay hub"); `dropping a library entity keys it by its title` (`relay`); `a title that makes no key falls back` (titles `"2 Relays!"` and `"Détecteur"` give `device` for a device); `two libraries' classes of one title drop as two nodes with two keys`; `a library device and a library entity are listed as their C# kinds are` (`derivedDevices`, `csharpEntities`).
- [ ] **Step 2: Run** `(cd custom_components/hass_sharp/frontend && bun run test tests/visual-contract.test.ts tests/entity-node.test.ts)`. Expected: FAIL.
- [ ] **Step 3: Implement** the mirrors.
- [ ] **Step 4: Run** the two files, then `bun run test`. Expected: all PASS.
- [ ] **Step 5: Commit** `git commit -m "The panel's graph contract reads a library class node's settings from its catalog entry, lists its device or entity as a C# node's, and keys a dropped one by its class's title."`

### Task 5: The editor shows it

**Files:**
- Modify: `custom_components/hass_sharp/frontend/src/entities/NodePickers.tsx:169-171`, `src/entities/CSharpNodeSection.tsx`, `src/entities/definitionEdits.ts:55-106`, `src/entities/model.ts:193-201, 298-310, 362`, `src/visual/flow/flowModel.ts:246-260`, `src/visual/flow/FlowAdapter.tsx:1042-1045`, `src/visual/NodeConfig.tsx:90, 264-281, 352-354`, `src/visual/nodeSummary.ts:259-264`, `src/visual/trace/TraceInspector.tsx:20`, `src/visual/VisualEditor.tsx:337-361`
- Test: `frontend/tests/palette.test.tsx`, `canvas-cards.test.tsx`, `entity-node-config.test.tsx`, `editor-adapter.test.tsx`, `EntityDefinitionView.test.tsx`, `entity-node.test.ts`

**Interfaces:**
- Consumes: Task 4's `hasCode`, `classSignature`, `isClassNode`.
- Produces: every listed place decides by the catalog entry (`isClassNode(spec)`, `spec.declares`, `hasCode(spec)`), not by the kind's name. `withoutTypedSecrets` takes the live catalog.

- [ ] **Step 1: Write the failing tests.**
  - `palette.test.tsx`: the step list of an entity definition offers "Relay hub" under "Relays" and "Relay" under "Custom"; an automation's offers neither.
  - `canvas-cards.test.tsx`: a hub node's card shows its three inputs, the entities area ("Its entities appear here after it has started once"), and the waiting badge when its node key has a count; it has no code button. A hub node with a stray `config.source` has none either. A `csharp_device` card still has one.
  - `entity-node-config.test.tsx`: the right panel of a hub node edits Key, Name and Part of, shows "Settings" with the three settings, shows the line "From the node library relays." (the library's name when the catalog's sources give one), and offers no "Edit code"; a relay node's edits Key and shows the Device pin's hint.
  - `entity-node.test.ts`: `withoutTypedSecrets` removes a literal typed into `Token` of a hub node when given the live catalog, and leaves `Address`; a draft and an export of a graph with it hold no such literal; renaming a hub node's key renames it where C# device keys are renamed.
  - `editor-adapter.test.tsx`: deleting a saved hub node whose key has stored data asks, and the question's text is "Its stored data is deleted when you save the entity definition without a node keyed hub." (the existing sentence with "without this node" replaced; keep the rest of the existing wording).
  - `EntityDefinitionView.test.tsx`: a definition with a hub node shows the Device tab count from its node key.
- [ ] **Step 2: Run** the six files. Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `bun run test` and `bun run build`. Expected: all PASS, build succeeds.
- [ ] **Step 5: Commit** `git commit -m "The editor offers a library's classes in an entity definition's step list and shows such a node as a C# node without code, with its typed secrets stripped from drafts and exports."`

### Task 6: Import and the assistant

**Files:**
- Modify: `custom_components/hass_sharp/external.py:39, 239-249, 564-601, 695-728`, `custom_components/hass_sharp/llm.py:20, 30, 142, 518-630`, `custom_components/hass_sharp/authoring_examples.json` (only if a text there states that classes need a C# node)
- Test: `tests/python/test_external.py`, `tests/python/test_mcp_adapter.py`

**Interfaces:**
- Consumes: the catalog (operation `catalog` of the node library request) as it is after the import's libraries are saved.
- Produces: `keep_device_inputs(imported, existing, catalog)`: for two nodes of the same id that are both `csharp_entity`/`csharp_device` of the same kind, today's rule exactly. Otherwise, when both are class nodes (a C# kind, or a kind whose catalog entry has `classSignature`): a stored **literal** argument is kept when the file's node has a setting of that key (from the entry's `classSignature`, or from its `config.signature` for a C# node), the pin is not wired in the file, and the literal's `type` is the setting's pin type (`string` for string and enum, `number` for integer and number, `boolean`, `entity_id` or `entity_id:<domain>`). Anything else takes the file's value.

- [ ] **Step 1: Write the failing tests** in `test_external.py`:
  - `test_rewrite_library_kinds_rewrites_a_class_kind`: `library:vision:global%3A%3ANodeLib.PeopleDetector` becomes `library:<stored id>:…`.
  - `test_overwrite_keeps_inputs_from_a_csharp_node_to_a_library_node`: stored `csharp_device` node `front_door` with `Camera` literal `entity_id:camera` = `camera.mine`; the file's node `front_door` of a library class kind with `Camera` = `camera.front_door`; the result has `camera.mine`.
  - `…_from_a_library_node_to_a_library_node` and `…_from_a_library_node_to_a_csharp_node`: likewise.
  - `test_overwrite_takes_the_files_value_where_the_kept_one_does_not_fit`: the stored literal is `string` and the new setting is a number: the file's value stays.
  - `test_overwrite_keeps_nothing_across_node_ids`, and the existing `test_overwrite_keeps_device_inputs` unchanged.
  - In `test_mcp_adapter.py`: the catalog tools list a library class kind for an entity definition and not for an automation; `definition_summary` lists a library class entity as it lists a C# entity.
- [ ] **Step 2: Run** `python3 -m unittest discover -s tests/python -p test_external.py` and `-p test_mcp_adapter.py`. Expected: the new tests FAIL (on a host without Home Assistant 2026.11 the MCP tests skip; say so in the report).
- [ ] **Step 3: Implement.** The import fetches the catalog once after its libraries are saved. The assistant's texts that describe C# nodes also say a node library may offer a device or entity class as a node of its own, used without code.
- [ ] **Step 4: Run** `python3 -m unittest discover -s tests/python` and `python3 -m unittest discover -s tests`. Expected: OK.
- [ ] **Step 5: Commit** `git commit -m "Add external keeps the values typed into a node when an import changes it between a C# node and a library class node, and the assistant's texts and summaries know a library class node."`

### Task 7: The guides

**Files:**
- Modify: `docs/node-libraries.md` ("Entity classes"), `docs/custom-entities.md` (C# nodes: where a class comes from; "Keeping data": the stored-data question), `docs/superpowers/specs/2026-11-17-custom-entities-design.md` only if it states that a library class cannot be a node
- Test: the runtime suite's docs tests (`tests/Runtime/ServiceCallTests.cs` compiles blocks marked `<!-- compiled: Name -->`)

**Interfaces:** none.

- [ ] **Step 1: Write the failing test:** a block `<!-- compiled: RelayHubNode -->` in `docs/node-libraries.md` is compiled as a node library and offers the kind `library:<id>:global%3A%3ANodeLib.RelayHub` (follow how `SnapshotNamer` is compiled and checked).
- [ ] **Step 2: Run** the suite that holds it. Expected: FAIL (no such block).
- [ ] **Step 3: Write** the guide: `[Node]` on a library's device or entity class makes it a node of entity definitions; the three conditions and the warning; `Category`; that the node has a key, a name and the settings and no code; that deriving from the class in a C# node still works; that a new setting should have a default, or the library save is refused while a definition uses the class without a value for it; that replacing a C# node with the library node under the same key keeps its entities and data. Plain sentences, the guide's own style.
- [ ] **Step 4: Run** the suite. Expected: PASS.
- [ ] **Step 5: Commit** `git commit -m "The guides describe offering a library's device or entity class as a node of its own."`

### Task 8: Live check

**Files:** none in the repository. Scratch under the plan's SDD workspace.

- [ ] **Step 1:** Publish (`dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`) and build the panel in this worktree. Start a throwaway Home Assistant 2026.11.1 as compose project and container `hass_wt` on port 5002 with this worktree's `custom_components/hass_sharp` mounted. Never touch the containers `hass`, `hass-dummy-camera` or `hass_wt_stale_entity_nodes`.
- [ ] **Step 2:** Save a node library holding a marked device class that draws a page and counts its starts in `Data` (the fixture hub), and a marked entity class.
- [ ] **Step 3:** In the panel: both are in an entity definition's step list and in no automation's; drop the hub, set its settings (the secret one from a Setting), save; its entities appear on the card; its Device tab shows its page and the action works; the card has no code button.
- [ ] **Step 4:** Restart Home Assistant (`restart -t 60`) and re-save: the count in `Data` goes up, nothing is lost.
- [ ] **Step 5:** In a second definition, a C# device node deriving the same class, keyed `hub`, runs; replace it with the library node under key `hub`, save: the entity ids are the same and its data is kept.
- [ ] **Step 6:** Save the library without the class: refused, naming the definitions. Export a definition and a draft with a secret typed in: neither holds it.
- [ ] **Step 7:** Run the Python suite inside the container as `AGENTS.md` describes (none skipped) and `python tests/packaging/entity_bridge_smoke.py custom_components/hass_sharp`. Read the log: no HassSharp error or warning.
- [ ] **Step 8:** Write what was seen, with the evidence, to the report file. Leave the container running for the controller's own look; do not remove it.

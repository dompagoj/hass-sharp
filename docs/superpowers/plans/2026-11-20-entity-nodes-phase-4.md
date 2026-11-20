# Entity nodes, phase 4: C# entity and C# device nodes

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two new nodes in the entity editor. A **C# entity** node holds one class that derives from a typed entity (`SharpSwitch`, `SharpCamera`, …) and is one entity. A **C# device** node holds one `SharpDevice` class that adds its entities and is one device. Their code is written in the editor the Custom C# step uses, and each `[Setting]` property is an input pin that takes a typed value or a wire from a Setting node. Everything the class kind of definition can do is then possible in a node; the class kind itself stays until phase 5.

**Architecture:** Two catalog kinds, `csharp_entity` (`declares: "entity"`, `declaration: true`) and `csharp_device` (`declares: "device"`, `declaration: true`), both marked `dynamicInputs: "settings"`: their pins come from a stored `signature` the compiler derives from the code, as the Custom C# step's do. The compile reuses the Custom step's pipeline (one compilation, each node's code in `NodeLib.Custom_<node id>`, a preview operation, apply-and-refit) and the class kind's describe code (`EntityTypes`, `SettingType`). The generated assembly of a graph with such nodes gains a factory, `SharpNodes.Create(nodeId)`. The host prepares a graph definition as today and then sets up one component per C# node with the class kind's life-cycle code, so one definition can hold hubs, plain devices, C# entities and C# devices together. The runtime never walks a C# node: it is a declaration.

**Tech Stack:** C# on .NET 11 (`custom_components/hass_sharp/HassSharp/`, custom test runner in `tests/Runtime/`), SolidJS with TypeScript 7 and vitest 5 (`custom_components/hass_sharp/frontend/`), Python 3 (`custom_components/hass_sharp/*.py`, `tests/python/`). `CC/` below is `custom_components/hass_sharp/`, `FE/` is `CC/frontend/`.

**Spec:** `docs/superpowers/specs/2026-11-19-entity-nodes-design.md` (decisions 5 and 6, "Decisions (mine)" on setting pins and unique ids, "Shapes", "Validation", phase 4). Code map for this phase: `docs/superpowers/plans/2026-11-20-entity-nodes-phase-4-map.md` (written at `8c32df8`, before phase 3's Tasks 2 to 9; its section 7 lists the traps). Line numbers there are approximate; find the symbol. Phase 3's plan (`2026-11-20-entity-nodes-phase-3.md`) names what this phase builds on: `Structural`, `Declaration`, `StateInputs`, `DeclaredDevices`, `DeviceOf`, `DefinitionGraph.Devices`, the bridge payload `{definitionId, name, devices[], entities[{…, device}]}`, the header Device pin, `DeviceNodeSection.tsx`, the Entity node's Device select, `SavedEntities.deviceKeys`.

## Global Constraints

- **One catalog, two sides.** `CC/visual_node_catalog.json` is embedded in the C# library and imported by `FE/src/visual/contract.ts`. A catalog edit and the code on both sides that reads it land in one commit. A case added to `tests/fixtures/visual-graph-validation.json` is read by both validators, and their messages must be byte-identical.
- **Every commit leaves these green**, run from the repository root:
  - `python3 -m unittest discover -s tests/python` (also in the test container, as the controller's notes say)
  - `python3 -m unittest discover -s tests`
  - `dotnet run --project tests/Runtime --disable-build-servers`
  - `dotnet test custom_components/hass_sharp/HassSharp.Editor.Tests`
  - `(cd custom_components/hass_sharp/frontend && bun run test)` and `(cd custom_components/hass_sharp/frontend && bunx tsc --noEmit)`
  - after `dotnet publish -c Release ./custom_components/hass_sharp/HassSharp -o ./custom_components/hass_sharp/out`: the entity bridge smoke and the SQLite smoke
- **Out of scope.** The class kind is not deleted (phase 5): a class definition loads, saves and runs after every task, through its own path. The `custom` kind's catalog entry is not edited. Nothing changes for automations: an automation's generated source is byte-identical before and after every task, and so is the generated source of an entity graph that has no C# node (no pin holds an entity graph today, so Task 4 first adds a test that pins one such graph's source before the generator changes; that source changing is a defect).
- **No user class is created on preview.** Preview, save-time checks, `custom_preview` and `DeriveSignatures` compile and read symbols; only the host, when a definition starts, calls a constructor.
- **Names in the graph.** Kinds `csharp_entity` and `csharp_device`. Both: `dynamicInputs: "settings"`, `argumentStorage: "arguments"`, `documents: ["entity"]`, category `Entity`, no outputs, no execution ports. `csharp_entity` also has the static input `device` exactly as the Entity node has it (structural). Catalog order of the Entity group: `entity`, `device`, `csharp_entity`, `csharp_device`, `entity_setup`, then as today.
- **The class signature** (in `config.signature`): `{title: string, description: string, domain: string | null, settings: [{key, label, description, type: "string" | "integer" | "number" | "boolean" | "enum", secret: boolean, options?: string[], minimum?: number, maximum?: number}]}`. `domain` is the typed entity's domain for a C# entity and null for a C# device. A setting's `key` is the property's name and is the pin's id. Pin types: `string` and `enum` take the catalog type `string`, `integer` and `number` take `number`, `boolean` takes `boolean`; every setting pin is optional (an unset pin leaves the property as the class initialises it).
- **Keys and ids.** A C# entity's key is the node's `config.key`, in the same namespace as the Entity nodes' keys; its unique id is `<definition id>:<key>`. A C# device's key is the node's `config.key`, in the same namespace as the Device nodes' keys; its device identifier is `(DOMAIN, "<definition id>:<device key>")`. An entity a C# device adds keeps the key its code gives it; its unique id is `<definition id>:<device key>:<entity key>`. Nothing may read "the text after the first colon" of a unique id as an entity key.
- **Names.** A C# entity is called what its code sets as `Name`; when the code sets none it is called the signature's `title` if it stands alone, and takes its device's name if it is wired to a Device node. In the editor and in messages `{Name}` of a C# entity is the signature's title, else its key, else `C# entity`; of a C# device its `config.name`, else its key, else `C# device`.
- **`graphVersion` stays 5** and the stored document's `version` stays what phase 3 left (3): a document saved by phase 3 reads the same in phase 4.
- **Generated-source pins and catalog fixtures.** `GeneratedSourcePinTests` skips every fixture graph that has a `definition` and lists the entity kinds in `UnpinnableKinds`, so the generated source of an entity graph is pinned by Task 4's own tests (`EntityGraphGeneratorTests`), not by the pin file. When a frontend test fails because `tests/fixtures/visual-rich-catalog.json` lacks a kind, regenerate it with `-- --write-node-library-fixture`. The new kinds join `GeneratedSourcePinTests.UnpinnableKinds` (no automation graph can use them).
- **Fixture messages.** Write a new case's `messages` by hand from the copy below, then check them with `-- --describe-visual-graph=<file of cases>`. A message does not end with a full stop.
- **Exact copy.** User-facing strings are hardcoded English.
  - Kind `csharp_entity`: label `C# entity`; description `An entity you write in C#: one class that derives from a typed entity, such as SharpSwitch or SharpSensor. Its [Setting] properties are its inputs.`
  - Kind `csharp_device`: label `C# device`; description `A device you write in C#: one class that derives from SharpDevice and adds its entities. Its [Setting] properties are its inputs.`
  - Compile, no class: `Write one public class that derives from a typed entity, such as SharpSwitch: it becomes this entity` / `Write one public class that derives from SharpDevice: it becomes this device`
  - Compile, several: `Only one class can be this entity; this code has {n}` / `Only one class can be this device; this code has {n}`
  - Compile, a device class in a C# entity node: `{Class} is a device: write it in a C# device node`
  - Compile, a class that cannot be created: `{Class} must be public, not abstract or generic, and have a public constructor without parameters`
  - Compile, a C# entity's setting named `device`: `A setting cannot be called device: that is the pin the entity's device connects to`
  - The setting messages of `NodeLibraryCompiler` (`A setting is a string, bool, int, long, double or enum; {type} cannot be one` and the rest) are reused word for word
  - `csharp.signature`, unreadable: `The settings of this node cannot be read; open Edit code and apply it`
  - `csharp.signature`, out of date: `The settings are out of date with the code: open Edit code and apply it`
  - `scope.settingSource`: `{Label} takes a typed value or a Setting: it is set before anything runs`
  - `setting.secretLiteral`: `{Label} is a secret: connect a Setting instead of typing it here`
  - `setting.value`: `{Label} takes {Phrase}`, `{Phrase}` being what `DefinitionGenerator.Phrase` gives for the setting
  - `flow.declaration` for a C# entity: `{Name} is an entity and is not part of the run order` (a C# device keeps the device sentence)
  - Signature derivation (MCP and authoring): `Removed the value of {Label}: the code no longer has that setting`; `Removed the connection to {Label}: the code no longer has that setting`
  - Host: `{Definition}: {Name} could not be created: {reason}`; the existing `Setup() did not finish within {n} s` and key sentences are kept
  - Code editor: header `C# entity code` / `C# device code`; the pins panel is headed `Settings`, and empty reads `This class has no settings. Mark a property with [Setting] to make it an input.`
  - Card of a C# entity: summary `{Domain label} · {key}` (as an Entity node). Card of a C# device: summary `C# device · {key}`, then its entities as Home Assistant has them, or `Its entities appear here after it has started once`
  - Inspector: `Key` with the hint the Entity node (for a C# entity) or the Device node (for a C# device) uses; a secret pin has no value editor and the hint `A secret is not typed here: connect a Setting`; button `Edit code`
- **Commits.** One per task, files staged by name. The message is one sentence saying what the user now gets, ending with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Two C# device nodes holding the same class.** Each is its own device with its own entities (`<definition id>:<device key>:<entity key>`), its own settings and its own timers; deleting one removes only its device. Tests in Task 5 and the bridge test in Task 6.
2. **Code edited under a value or a wire.** A setting that is renamed, removed or changes type while its pin holds a value or a wire: the editor's Apply refits and says what it removed; MCP's derivation removes and warns; a graph saved with neither is told `csharp.signature`, and is never run with pins that do not match the code. Tests in Tasks 4 and 7.
3. **A constructor or `Setup()` that throws or never returns.** Preview does not run it. Starting fails with a sentence that names the node; the definition does not start half-made (no entity of it is left registered as available); other definitions are unaffected. Tests in Tasks 4 and 5.
4. **One definition with a hub and C# nodes.** A command of a hub runs its flow, a command of a C# entity reaches its class; a hub's state is carried over on a hot replace as today, a C# entity's is restored from the store as a class's is; after a restart both come back. Tests in Task 5.
5. **Secrets.** A secret setting's value is never in the generated source, a preview answer, a trace or a read answer: a typed value is refused, and a wired Setting's value is read only when the definition starts. Tests in Tasks 2, 4 and 5.

---

### Task 1: The two kinds exist, and no rule mistakes them for a hub, a plain Device node or a Custom C# step

**Files:**
- Modify: `CC/visual_node_catalog.json` (the two kinds after `device`)
- Modify: `CC/HassSharp/Visual/VisualNodeCatalog.cs`, `CC/HassSharp/Visual/VisualGraphValidator.cs`, `CC/HassSharp/Devices/DefinitionGraph.cs`, `CC/HassSharp/Devices/DefinitionDocument.cs`, `CC/HassSharp/Visual/VisualRuntime.cs`
- Modify: `FE/src/visual/contract.ts`, `FE/src/visual/connectionIndex.ts`, `FE/src/visual/flow/flowModel.ts` (only so a graph with the kinds renders, with the header Device pin), `FE/src/entities/EntityNodeSection.tsx` (the Device select's choices), `FE/src/entities/refit.ts`
- Modify: `tests/fixtures/visual-graph-validation.json`, `tests/fixtures/visual-rich-catalog.json` (regenerate), `tests/Runtime/GeneratedSourcePinTests.cs` (`UnpinnableKinds`, until Task 4)
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/palette.test.tsx`, `FE/tests/connection-index.test.ts`

**Interfaces:**
- Produces, catalog: `nodes.csharp_entity = {label, category: "Entity", description, declares: "entity", declaration: true, documents: ["entity"], minGraphVersion: 5, runtimeVersions: [5], argumentStorage: "arguments", dynamicInputs: "settings", config: {key (string), source (string, required, label "Code", default: the template), signature (object, required, label "Settings", default: the template's signature), arguments (object, label "Values", default {})}, inputs: {device: <as the Entity node's>}, outputs: {}, execution: [], keywords}`; `nodes.csharp_device` the same with `declares: "device"`, no `inputs.device`, and config `key, name (string), via (object, nullable), source, signature, arguments`.
  - Template of `csharp_entity`: a `Thermometer : SharpSensor` with one `[Setting("Address")] public string Address { get; set; } = "";`, compiling against the library as it is. Template of `csharp_device`: a `Plug : SharpDevice` with the same setting that adds one `SharpSwitch` `relay`. Each default signature is exactly what Task 3's compiler derives (Task 3 adds the test that pins it; here it is written by hand from the signature rule in Global Constraints).
- Produces, C# (`VisualNodeCatalog`): `static bool IsClassNode(JsonObject? spec)` (`dynamicInputs == "settings"`), `static bool HasCode(JsonObject? spec)` (`dynamicInputs` is `custom` or `settings`; there is no spec-level `IsCustom` today, the sites test `dynamicInputs == "custom"` inline), `static bool IsHub(JsonObject? spec)` (the spec's `root` flag and `Declares(spec, "entity")`; today's `IsRoot` takes a node and a snapshot), `static bool WellFormedClassSignature(JsonNode? signature)`, `static JsonObject? ClassSignature(JsonObject node, JsonObject? spec)` (null unless a class node with a well-formed signature).
- Produces, TS (`contract.ts`): `isClassNode(spec)`, `hasCode(spec)`, `isHub(spec)`, `wellFormedClassSignature(value)`, `classSignature(node, catalog)`, types `ClassSignature`, `ClassSetting`.

**Behaviour:**
- **Pins.** `DataInputs` / `dataInputs` of a class node: the kind's static inputs (`device` for a C# entity), then one input per `signature.settings` entry in order: `{types: [<pin type>], required: false, label, description}` plus the setting itself under `setting` (`{type, secret, options, minimum, maximum}`) for Task 2 and the inspector. A node whose signature is not well formed has only the static inputs and the problem `csharp.signature` (unreadable) on `inputId: "signature"`.
- **Hub-only rules stay hub-only.** Every site that today reads `Declares(spec, "entity")` / `declares === 'entity'` is classified. For a hub only (`IsHub`): `DefinitionGraph.HubFlows`, `Roots` and `Entities` (the list the host creates graph entities from), `WithGraphTypes`'s `definition.entities` (so Set state, Set available and Show frame offer and accept hubs only: a step naming a C# entity's key gets the existing `definition.unknownEntity`), `command.unhandled`, `camera.no_frame`, `entity.partialState`, `entity.no_value`, `state.dynamicSource`, `entityScopes`, `scope.stateSource`, `scope.flow`, `definition.entityDomain`, the option and history checks, `entity.name`, `refit` on a type change. For every node that declares an entity: `definition.entityKey` and `definition.duplicateEntity` (one key namespace), `DeviceOf` / `deviceNodeOf` membership, the name used in messages and on the card, re-keying on paste (Task 7).
- **A C# device is a device with code.** The rules phase 3 wrote for `declares: "device"` apply to it unchanged (`definition.deviceKey`, `definition.duplicateDevice`, `device.name`, `device.via`, `device.viaUnknown`, `device.viaCycle`; one key namespace with Device nodes; Part of may name either kind), except `device.empty`, which a C# device never gets (its entities come from its code), and `device.configurationUrl`, which reads a config key it does not have. `DeclaredDevices` / `declaredDevices` list it. A plain Device node wired only to C# entity nodes is not empty.
- **Nothing derived carries code.** Phase 3's `DerivedDevices` / `DeviceDescription` and `DeclaredEntities` / `EntityDescription` copy a declaring node's whole config. From this task on they switch on the kind explicitly (a kind they do not know is a failing test, not a silent copy): a Device node and a hub as today; a C# device node gives `{key, name, via, csharp: true}` (blank texts and a null `via` left out, as for a Device node), listed after the Device nodes; a C# entity node is in no derived entity list until Task 6. So `source`, `signature` and `arguments` never reach `catalog.definition` (which is embedded in the generated source), the stored `devices` / `entities`, a read answer or the host's payload. The TypeScript `derivedDevices` / `derivedEntities` do the same. Until Task 5 the host's payload leaves a `csharp: true` device out.
- **Two frontend places that assume phase 3's kinds.** The header Device pin (`flowModel.ts`, `headerInput`) is given to a root with a structural input: a C# entity is a declaration, not a root, so the gate becomes "a root or a declaration" and its Device pin sits in the header, never among the input rows with a "Set value". The Entity node's Device select (`EntityNodeSection.tsx`) lists every node that declares a device and wires its `entities` output: it lists only nodes that have an `entities` output, so a C# device is never offered there.
- **Declarations.** Both kinds are declarations as phase 3 defined them: no execution ports, never `node.unreachable` or `pure.unused`, `flow.declaration` on a step-order edge with the sentence for its kind, `canConnect` refuses step-order wires. A Setting node whose only wire goes into a setting pin is used (no `pure.unused`).
- **`definition.entitiesRequired`** is satisfied by an Entity node, a C# entity node or a C# device node. It is raised by `DefinitionDocument.Check` on preview and save, not by the validators, so its test is a preview test (`EntityGraphTests.AGraphWithOnlyACSharpNodeIsADefinition`: a graph holding only a C# entity node, and one holding only a C# device node, each preview without `definition.entitiesRequired`), beside the fixture cases below.
- **Runtime and generator.** The runtime's constructor records no data edge whose target is a declaration, and never prepares a value for one; `ValueUpstream` and `StateDependencies` are unaffected (hub state inputs only). The generator emits nothing for the kinds yet (Task 4): a graph with a C# node previews with one problem on the node, `inputId: "source"`, `C# entity and C# device nodes cannot run yet` (removed in Task 4; it keeps every commit between honest).

- [ ] **Step 1: Write the failing fixture cases** (entity documents; C# nodes carry the template's signature unless the case says otherwise):
  - `a C# entity node alone is a valid definition` and `a C# device node alone is a valid definition`: `noDiagnostics`.
  - `a C# entity is not asked for a name, a domain, state values or command wires`: a C# entity whose signature domain is `light`: `noDiagnostics`.
  - `a C# entity and an Entity node cannot share a key`: `definition.duplicateEntity` on the later.
  - `a C# device and a Device node cannot share a key`: `definition.duplicateDevice` on the later.
  - `a C# device with no wire is not an empty device`: no `device.empty`.
  - `a Device node wired only to a C# entity is not empty`: `noDiagnostics`.
  - `a device can be part of a C# device`: `via: {device: <csharp device key>}`: `noDiagnostics`.
  - `a step cannot name a C# entity`: Set state with `config.entity` the C# entity's key: `definition.unknownEntity`.
  - `a step-order wire into a C# entity is refused`: `flow.declaration`, message `Thermometer is an entity and is not part of the run order`.
  - `code csharp.signature: settings that cannot be read`: `signature: {title: 5}`: the unreadable sentence on `inputId: "signature"`, and the node has no setting pins.
  - `a Setting read only by a C# node is used`: Setting.value → C# device `Address`: no `pure.unused`.
- [ ] **Step 2: Write the failing unit tests.** C# `EntityNodeTests.ACSharpNodeIsNoHub`: `HubFlows` and `Roots` of a graph with a hub, a C# entity and a C# device hold the hub's only; `DefinitionGraph.Entities` holds the hub only; `DataInputs` of the C# entity is `device, Address`; `DeclaredDevices` holds the C# device. `EntityNodeTests.NothingDerivedCarriesCode`: for a graph with a Device node, a hub, a C# entity and a C# device, `DefinitionGraph.Devices`, the derived `devices` and `entities` and `WithGraphTypes`'s `definition` are serialised and none contains `source`, `signature` or `arguments`; the C# device's entry is exactly `{key, name, via, csharp: true}`. TS mirror in `entity-node.test.ts`, plus: the palette lists `entity, device, csharp_entity, csharp_device` first; a C# entity's view has `headerInput` for `device` and no `device` row; the Entity node's Device select offers a Device node and not a C# device; a type change of an Entity node (`refit`) leaves a C# entity untouched; the connection index and `connectData` agree on every pair with a C# node.
- [ ] **Step 3: Run** both fixture suites. Expected: FAIL (unknown kinds).
- [ ] **Step 4: Implement** on both sides; regenerate the rich catalog fixture.
- [ ] **Step 5: Run** every suite. Expected: pass; no pin changed.
- [ ] **Step 6: Commit** — `A C# entity and a C# device node can be placed, and are not treated as an Entity node, a step or a value.`

---

### Task 2: A setting pin takes a typed value or a Setting, and a secret takes only a Setting

**Files:**
- Modify: `CC/HassSharp/Visual/VisualGraphValidator.cs`, `FE/src/visual/contract.ts`, `FE/src/visual/connectionIndex.ts`, `FE/src/visual/graph.ts` (`canConnect`, if it needs it; `connectData` is in `contract.ts`)
- Modify: `tests/fixtures/visual-graph-validation.json`
- Test: `tests/Runtime/EntityNodeTests.cs`, `FE/tests/entity-node.test.ts`, `FE/tests/connection-index.test.ts`

**Interfaces:**
- Consumes: Task 1's `ClassSignature`, the `setting` member of a class node's inputs.
- Produces: codes `scope.settingSource` (a blocking `scope.` problem, so `connectData` and the connection index refuse the wire) and `setting.secretLiteral`, on the C# node with `inputId` the pin.

**Behaviour:**
- A data edge into a setting pin whose source is not a `setting` node: `scope.settingSource`. From a Setting node: accepted when the Setting's resolved type is assignable to the pin's type by the existing rules (a `string` Setting into a `number` pin is the existing type sentence).
- A typed value (`config.arguments[pin]` of kind `literal`) on a pin whose setting is `secret`: `setting.secretLiteral`. Any other kind of argument than a literal or the wire marker on a setting pin is the existing "cannot be used here" problem for an argument kind the input does not take (find what a hub's state input gives for an expression or a reference and follow it).
- A typed value of the wrong JSON type for the pin is the existing literal type problem. Whole numbers, ranges and enum members are checked by the server in Task 4 (`setting.value`), not here.
- A secret pin wired from a Setting that is not marked secret is accepted (the person chose where the value is kept).

- [ ] **Step 1: Fixture cases:** `code scope.settingSource: a step's output wired into a setting`, `code scope.settingSource: a state value wired into a setting` (a Has state value node), `a Setting wired into a setting of its type is valid`, `a text Setting wired into a number setting is refused` (existing type code), `code setting.secretLiteral: a typed secret`, `a secret wired from a Setting is valid`, `a typed value in a setting is valid`.
- [ ] **Step 2: Unit tests.** TS: `connectData(logStep.result → plug.Address)` is refused with the `scope.settingSource` sentence and the index says the same; `connectData(setting.value → plug.Address)` is accepted and leaves no problem. C#: the same two graphs through the validator.
- [ ] **Step 3: Run**, expect FAIL. **Step 4: Implement** on both sides. **Step 5: Run** every suite.
- [ ] **Step 6: Commit** — `A C# node's setting takes a typed value or a Setting node, and a secret setting only a Setting node.`

---

### Task 3: The compiler describes the class in a C# node, and Edit code can check it

**Files:**
- Modify: `CC/HassSharp/Visual/NodeLibraryCompiler.cs` (`EntityTypes` → shared `DescribeEntityType`; the per-node branch), `CC/HassSharp/Visual/VisualCatalogSnapshot.cs` (`CustomNodeSource`, `CustomNodeDescriptor`), `CC/HassSharp/Visual/NodeLibraryApi.cs` (`custom_preview`), `CC/HassSharp/Visual/VisualGenerator.cs` (`CustomSources`, `WithCustom`'s cache key)
- Modify: `CC/HassSharp/NodeLibraryLanguageServices.cs` only if a class-shaped document needs it
- Test: `tests/Runtime/CustomNodeTests.cs` (new checks in `--custom-nodes`), `tests/Runtime/EntityTypeTests.cs` (must stay green unchanged), `CC/HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`

**Interfaces:**
- Produces: `record CustomNodeSource(string NodeId, string Source, string Kind = "custom")` (`"custom"`, `"csharp_entity"`, `"csharp_device"`); `CustomNodeDescriptor` gains `string? TypeExpression` (`global::NodeLib.Custom_<ns>.<Class>`, set when valid) and, for a class node, `Signature` in the class shape of Global Constraints; `custom_preview` takes an optional `kind` (default `custom`) and answers `{valid, diagnostics, signature}` as today.
- Produces: `NodeLibraryCompiler.DescribeEntityType(INamedTypeSymbol type, …) : EntityTypeDescriptor?`-shaped helper used by both the library scan and the per-node path (the library scan's output must not change: `EntityTypeTests` proves it).

**Behaviour:**
- For a `csharp_entity` source: the candidates are the classes in the node's own tree that derive from `SharpEntity`. Exactly one must be creatable (public, top level in the node's code, not abstract, not generic, public parameterless constructor) and have a domain: it is the entity. None: the "Write one public class…" sentence. Several creatable: the "Only one class…" sentence with the count. A class deriving `SharpDevice` in the code: the "is a device" sentence. A single candidate that is not creatable: the "must be public…" sentence. A `[Setting]` property named `device`: its sentence.
- For a `csharp_device` source: the candidates are the classes deriving `SharpDevice`; entity classes beside it are its own helpers and are fine. The same none / several / not creatable sentences in their device wording.
- Setting problems use the class kind's sentences unchanged, placed on the property's line in the node's code (`SourceLineOffset`).
- The signature: `title` (`[Node]` on the class, else the class name in words, as the library scan does), `description` (the doc summary), `domain`, `settings` (declaration order, base classes first; `options` for an enum; `minimum` / `maximum` when the attribute or the type gives them, as `EntitySettingDescriptor` has them).
- Types the class declares stay private to the node as a Custom step's do; two nodes with the same class name compile side by side.
- `custom_preview` with `kind` compiles one node's code off the gate exactly as today and never loads or runs it. Language services (`custom: true`) complete and diagnose inside a class-shaped document.

- [ ] **Step 1: Write the failing tests** in `CustomNodeTests` (each through `NodeLibraryCompiler.Preview` with a `CustomNodeSource` of the kind, and one through the `custom_preview` request):
  - `ClassTemplates`: the `source` default of each kind compiles to exactly the `signature` default in the catalog (`VisualGenerator.SameJson`), and that signature is `WellFormedClassSignature`.
  - `ClassSignatures`: an entity with a string, a secret string, an int with `Minimum`/`Maximum`, a double, a bool and an enum setting gives the six settings with their types, `secret`, `options` and ranges; a camera class gives `domain: "camera"`; a device gives `domain: null`.
  - `ClassProblems`: one case per sentence above, asserting the exact message and that the line is the node's own line.
  - `TwoNodesOneClassName`: two device nodes with a class `Plug` each: both valid, type expressions differ.
  - `NothingRuns`: a class whose constructor and static constructor throw: valid, with its signature.
  - `Api`: `custom_preview` with `kind: "csharp_device"` answers the signature; without `kind` the same code answers the `[Node]` sentence, as today.
- [ ] **Step 2: Run** `-- --custom-nodes`. Expected: FAIL. **Step 3: Implement.** **Step 4: Run** every suite; `--entity-types` unchanged and green.
- [ ] **Step 5: Commit** — `The code of a C# entity or C# device node is checked as it is typed, and its settings are read from its class.`

---

### Task 4: A definition with C# nodes previews, saves and generates

**Files:**
- Modify: `CC/HassSharp/Visual/VisualGenerator.cs` (`Preview`, `DeriveSignatures`, `SamePins`, the emitted source), `CC/HassSharp/Devices/DefinitionGenerator.cs` (make the private `Fits` internal and reuse it with `Within` / `Phrase`; all three take an `EntitySettingDescriptor`, so build one from the signature's setting), `CC/HassSharp/Devices/DefinitionGraph.cs`
- Modify: `tests/Runtime/GeneratedSourcePinTests.cs`, `tests/fixtures/generated-source-pins.json`
- Test: `tests/Runtime/EntityGraphGeneratorTests.cs`, `tests/Runtime/CustomNodeTests.cs`

**Interfaces:**
- Consumes: Task 3's descriptors.
- Produces: in the generated source of a graph that holds at least one C# node, `public static class SharpNodes { public static object? Create(string nodeId) => nodeId switch { "<id>" => new <TypeExpression>(), … , _ => null }; }` (a graph without C# nodes has no such class: its source is unchanged).
- Produces: `record DefinitionComponent(string NodeId, string Kind, string Key, string? Name, DefinitionVia? Via, string? Device, string Title, IReadOnlyList<DefinitionSetting> Settings)` and `DefinitionGraph.Components(JsonObject graph, VisualCatalogSnapshot snapshot, IReadOnlyList<DefinitionSetting> settings) : IReadOnlyList<DefinitionComponent>` in node order. `Kind` is `"entity"` or `"device"`; `Device` is the key of the Device node a C# entity is wired to; `Settings` are the node's pins resolved to values: a typed value as typed, a wired Setting as the definition's saved value for that key, an unset pin left out.
- Produces: problem codes `csharp.signature` (out of date) and `setting.value`, on the node (`inputId: "signature"`, and the pin).

**Behaviour:**
- `Preview`: compile problems of a node's code are on the node with `inputId: "source"`. A stored signature that differs from the compiled one in what changes wiring or values (`domain`; each setting's `key`, `type`, `secret`, `options`, `minimum`, `maximum`; the set and order of keys) is `csharp.signature` (out of date); one that differs only in words (`title`, `description`, a setting's `label` or `description`) is refreshed and returned in `Graph`, as a Custom step's is. Task 1's "cannot run yet" problem is removed.
- `setting.value`: each pin's resolved value is checked against its setting with `DefinitionGenerator.Fits` / `Within`: a fraction in an `integer`, a number outside the range, a text that is no member of the enum. The value of a wired Setting is checked the same way and reported on the pin. A secret's value is never put into a message.
- `DeriveSignatures` (MCP and authoring, `deriveSignatures: true`): writes the compiled signature into each C# node, drops the arguments and wires of settings the code no longer has, with the two warnings of Global Constraints.
- The node's code is appended in `NodeLib.Custom_<id>` as a Custom step's is. A wired Setting's value and a secret are not in the source: `Components` resolves them from the definition's settings when the definition is compiled for a run, not into the source.
- Pins: no pin in `tests/fixtures/generated-source-pins.json` changes, and the two kinds join `UnpinnableKinds`. Before the generator is touched, add `EntityGraphGeneratorTests.AnEntityGraphWithoutCSharpNodesKeepsItsSource`: it generates the source of every graph example in `CC/authoring_examples.json` that is an entity definition without C# nodes (through the path `EntityApi.CompileDefinition` uses) and compares each source's SHA-256 with a constant recorded from the unmodified generator; it passes at the task's start and at its end.

- [ ] **Step 1: Write the failing tests**
  - `EntityGraphGeneratorTests.ACSharpNodeGetsAFactory`: the source of a graph with a C# device `plug` and a C# entity `temp` has `SharpNodes.Create` with both ids and compiles; the source of the same graph without them has no `SharpNodes`.
  - `…AStaleSignatureIsToldSo`: code with setting `Address`, stored signature with `Host`: `csharp.signature` with the out-of-date sentence; a stored signature differing only in `title` is refreshed in `Graph` with no problem.
  - `…ASettingValueMustFit`: `1.5` in an integer, `200` in `Minimum = 1, Maximum = 100`, `"Fast"` in an enum without it, and a wired number Setting holding `1.5` into an integer: each `setting.value` with `{Label} takes {Phrase}` on the pin.
  - `…ComponentsResolveTheirSettings`: `Components` gives, in node order, the device with `Address` from the wired Setting's saved value and `Interval` as typed, no entry for an unset pin, and a C# entity with `Device` the key of the Device node it is wired to.
  - `…ASecretIsNotInTheSource`: a secret Setting wired in: the generated source does not contain the saved value.
  - `…DerivingDropsWhatTheCodeNoLongerHas`: the two warnings, and the returned graph has neither the argument nor the wire.
  - `…PreviewRunsNoConstructor`: a class whose constructor throws previews valid.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Re-pin** only the two new pins; **run** every suite.
- [ ] **Step 5: Commit** — `A definition with C# entity and C# device nodes is checked against its code and compiles.`

---

### Task 5: The host runs C# entity and C# device nodes beside hubs

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionHost.cs` (the host, and the `CompiledDefinition` record declared there), `CC/HassSharp/Devices/DefinitionHost.Graph.cs`, `CC/HassSharp/Devices/SharpEntity.cs` (internal `Adopt`), `CC/HassSharp/Devices/SharpDevice.cs` (only what the host needs), `CC/HassSharp/Devices/EntityApi.cs` (`CompileDefinition`)
- Test: `tests/Runtime/EntityGraphTests.cs` (the real rig), `tests/Runtime/EntityHostTests.cs` (must stay green: the class path is not changed in behaviour)

**Interfaces:**
- Consumes: `SharpNodes.Create`, `DefinitionGraph.Components`, phase 3's payload (`devices[]`, each entity's `device`).
- Produces: `CompiledDefinition.Components : IReadOnlyList<DefinitionComponent>` (empty for a class definition and a graph without C# nodes); `internal void SharpEntity.Adopt(string key, string? defaultName)` (the host gives a C# entity node's entity its key, and its name when the code set none); the unique id of every entity comes from one place in the host that knows whether a C# device added it.

**Behaviour:**
- **Prepare.** `PrepareGraph` runs as today up to the graph entities, then sets up each component in node order with the class kind's steps, extracted from `PrepareClass` into one method both paths call: create it (`SharpNodes.Create`; a null or a throw is `{Definition}: {Name} could not be created: {reason}`), apply its settings (`DefinitionSettings.Apply`), for a C# entity `Adopt(key, standalone ? title : null)`, restore the states of entities that restore, `Host = this`, `Setup()` within the definition's set-up time (one clock for the whole definition, the existing sentence), then seal a device. Then the key check over everything, then what `PrepareGraph` does today (carry-over, When started). A failure anywhere fails the definition's start as a class's does today.
- **Payload.** `devices[]`: phase 3's Device nodes, then each C# device as `{key, name, via}` from its node and `manufacturer, model, swVersion, hwVersion, configurationUrl, suggestedArea` from its class. `entities[]`: a hub as today; a C# entity with `device` the key it is wired to (or null); an entity a C# device added with `device` that device's key and unique id `<definition id>:<device key>:<entity key>`.
- **No `main` device in a graph, and parents first across both kinds.** The class path keeps one `device` field on the host, and `DeviceOf` falls back to it (`main`) when it is set: the extracted set-up method must not set it for a graph's component, so an entity with no wire stays standalone (`device: null`) and the payload has no `main` device. The payload's `devices[]` is ordered parents first over Device nodes and C# devices together (phase 3's `InOrder` orders only the plain ones), so a Device node that is part of a C# device, or the reverse, finds its parent whatever the node order.
- **Commands.** A command for an entity the host made from a hub runs its flow; for any other entity it goes to the entity's class. **Restore.** Per entity: a hub's as today (`stateKeys`), a class's by its `RestoreState`. **Start / Stop.** After the graph's own start: each component's `Start()` (a device, then its entities), in node order; stop in the class kind's order. **Hot replace.** Hubs carry over as today; a C# node is a new instance that restores from the store; the old instance's `Stop()` runs after the new one's `Setup()` and before its `Start()`, as `EntityHostTests.HotReplace` pins for a class.
- **Set state on a C# entity** is not possible (Task 1 refuses the step). (`ForgetRetyped` reads the stored `entities`, which hold no C# entity until Task 6: forgetting a C# entity whose domain changed is Task 6's.)

- [ ] **Step 1: Write the failing tests** in `EntityGraphTests` (C# nodes built by a `CSharp(kind, id, key, source, arguments)` helper beside `Hub` and `Flow`):
  - `ACSharpEntityRunsBesideAHub`: a hub switch with flows and a C# sensor that publishes in `Start()`: both are synced with unique ids `<id>:lamp` and `<id>:temp`; the hub's Turn on runs its flow; the sensor's state is what its class published.
  - `ACSharpEntityTakesItsCommands`: a C# switch whose `TurnOn` sets `IsOn`: the command reaches the class (no "has no flow for").
  - `TwoPlugsOfOneClassAreTwoDevices`: two C# device nodes with the same class and different `Address` values: payload devices `plug_a`, `plug_b`; entities `<id>:plug_a:relay`, `<id>:plug_b:relay`, each with its device; each instance saw its own address before `Setup()`.
  - `AGraphWithCSharpNodesHasNoMainDevice`: a hub with no wire beside a C# device: the hub's entity has `device: null` and the payload's devices are the C# device only. `ADeviceCanBePartOfACSharpDeviceDeclaredLater`: a Device node `strip` with `via: {device: "plug"}` placed before the C# device `plug`: the payload lists `plug` before `strip`, and `strip`'s `via` is `{device: "plug"}`.
  - `ACSharpEntityOnADeviceTakesItsName` / `…AloneIsCalledItsTitle`: `name` null with `device` set; `name` the title with no wire; a `Name` the code sets wins in both.
  - `SettingsAreAppliedBeforeSetup`: a wired secret Setting and a typed integer are on the instance when `Setup()` runs.
  - `AClassThatCannotStartFailsTheDefinition`: a constructor that throws, and a `Setup()` that never returns: the sentence names the node; nothing of the definition is left available; another definition in the rig keeps running.
  - `ACSharpEntityIsRestoredAndAHubCarriedOver`: push state on both, save a new revision: order `new.Setup, sync, old.Stop, new.Start`; the hub's state is carried, the C# entity's restored; after a restart of the rig both are back.
  - `TwoEntitiesWithOneUniqueIdAreRefused`: a C# device that adds `relay` twice (its own sentence), and a hub and a C# entity with one key saved past the validator (the host's key sentence).
- [ ] **Step 2: Run** `-- --entity-graph`. Expected: FAIL. **Step 3: Implement.** **Step 4: Run** every suite, publish, both smokes in the container.
- [ ] **Step 5: Commit** — `A definition runs its C# entity and C# device nodes beside its Entity nodes, each C# device as its own device.`

---

### Task 6: The stored document, the answers and Home Assistant's registries know the C# nodes

**Files:**
- Modify: `CC/HassSharp/Devices/DefinitionGraph.cs`, `CC/HassSharp/Devices/EntityDefinition.cs` (the derived lists at save), `CC/HassSharp/Devices/EntityApi.cs`
- Modify: `CC/llm.py` (`definition_summary`, tool descriptions), `CC/entity_bridge.py` (only if a test shows a need), `CC/authoring.py` (if it reads a unique id's tail)
- Modify: `FE/src/entities/api.ts`, `FE/src/entities/model.ts`, `FE/src/entities/DefinitionCard.tsx` (the Entities page card: `cardEntities`, the devices chip), `FE/src/entities/DefinitionPanel.tsx` (`liveEntitiesOf`), `CC/HassSharp/Devices/EntityApi.cs` (`ForgetRetyped`)
- Test: `tests/Runtime/EntityStoreTests.cs`, `tests/Runtime/EntityApiTests.cs`, `tests/python/test_entity_bridge.py`, `tests/python/test_mcp_adapter.py`, `FE/tests/EntitiesListView.test.tsx`, `FE/tests/entity-model.test.ts`

**Interfaces:**
- Produces: the stored and answered `entities[]` lists the hubs, then each C# entity node as `{key, domain, name: null, device, csharp: true}`; `devices[]` lists the Device nodes, then each C# device node as `{key, name, via, csharp: true}` (as Task 1 already derives it). The entities a C# device adds are not stored: an answer shows them from `liveEntities` / `registeredEntities` (unique id `<id>:<device key>:<entity key>`), so they are known only once the definition has run.
- Produces, Python: `definition_summary` gives each entity `{key, domain, name, device, entityId}` (`device` is new: today's summary has `{key, domain, name, entityId}`; a declared entity's `device` is its stored one): declared ones otherwise as today, plus one per registered entity whose unique id has three parts (`device` the middle part, `key` the last).

**Behaviour:**
- Nothing reads a unique id's tail as a key by splitting on the first colon (`llm.definition_summary`, which partitions on the first colon; `FE/src/entities/DefinitionCard.tsx` `cardEntities` and `FE/src/entities/DefinitionPanel.tsx` `liveEntitiesOf`, which slice the definition's prefix off; anything the map's trap 7 lists): a three-part id is matched whole. `EntityApi.ForgetRetyped` builds `<id>:<key>` ids from the stored `entities`: it also forgets a C# entity whose domain changed between two saves (test `EntityApiTests`), and never a C# device's entity by a two-part id. The card lists the live, else registered, entities as today (declared entities only supply names), now including a C# device's.
- **Registry.** With the fake registry in `test_entity_bridge.py`: a sync of a payload with a C# device and its two entities creates the device `(DOMAIN, "<id>:plug")` with both entries on it; a later sync in which the class adds only one removes the other entry; the clean-up at start-up keeps both the device and the entries of a definition that still exists and removes those of one that is gone; deleting one of two C# devices of a definition removes that device and its entries only.
- **Entities page.** A definition's card lists its declared entities as today, then the entities of each C# device as Home Assistant has them, and counts its devices (`Device` / `{n} devices`) including C# devices. A definition whose only node is a C# device that has not run yet shows the device and no entity rows.

- [ ] **Step 1: Write the failing tests:** `EntityStoreTests.TheDerivedListsHoldTheCSharpNodes` (save, read back: the two lists as above; a request that sends `devices` back is accepted and ignored, one that sends `entities` back is refused with `definition.entitiesNotAllowed`, as phase 3 left it); `EntityApiTests` read and list answers; the four bridge cases; `test_mcp_adapter` summary of a definition with a hub, a C# entity and a running C# device (keys `lamp`, `temp`, `relay` with `device: "plug"`); the frontend card cases.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Run** every suite; the Python suite in the container.
- [ ] **Step 5: Commit** — `The Entities page and the MCP tools list what a definition's C# nodes make, and Home Assistant keeps each C# device's entities under it.`

---

### Task 7: Edit code opens for a C# node, and applying it refits the settings

**Files:**
- Modify: `FE/src/visual/customCode.ts`, `FE/src/visual/CustomCodeEditor.tsx`, `FE/src/components/Editor/CustomCode.tsx`, `FE/src/components/Editor/editor-actions.ts`, `FE/src/visual/library-api.ts` (`customPreview`), `FE/src/visual/VisualEditor.tsx`, `FE/src/visual/flow/FlowAdapter.tsx`, `FE/src/visual/flow/NodeCard.tsx`, `FE/src/visual/NodeConfig.tsx`, `FE/src/visual/contract.ts` (`addGraphNode`), `FE/src/visual/clipboard.ts`
- Test: `FE/tests/custom-code.test.tsx`, `FE/tests/clipboard.test.ts`, `FE/tests/entity-node.test.ts`

**Interfaces:**
- Consumes: `hasCode`, `isClassNode`, `classSignature`; `custom_preview`'s `kind`.
- Produces: `customPreview(code, kind)` sends `{nodeId, source, kind}`; `applyClassCode(graph, nodeId, source, signature: ClassSignature, catalog): {graph, notices: string[]}`; `applyCustomCode` unchanged for the Custom step.

**Behaviour:**
- Every "has code" site (the card's Edit code, the inspector's button, the restored draft, the editor's open/apply in `VisualEditor`) works for the three kinds by `hasCode`; what is about a `[Node]` method stays on the Custom step.
- The dialog for a class node: header by kind; help text about one class, its base, `[Setting]`, `Setup` / `Start` / `Stop` and, for a device, `Add`; the side panel lists the settings (label, type, `secret`) under `Settings`; Apply only on a current valid check with a well-formed class signature; "Reset to example" uses the kind's template.
- `applyClassCode`: writes `source` and `signature`; keeps each argument whose pin still exists with a type its value fits, clears the others; removes wires into pins that are gone or no longer take the Setting's type; a pin that became secret loses its typed value. Notices in the Custom step's words (`Removed N connection(s) that no longer fit: …`, `Cleared N value(s) that no longer fit: …`). A change of a C# entity's domain needs no refit (no step names it).
- **A new node.** Dropped from the palette, a C# entity gets a free key from its template's domain (`sensor`, `sensor_2`, …), a C# device a free key from `device` and the template's title as its name; neither gets a `label`. **Paste / duplicate** gives the copy a fresh key in its namespace (as an Entity node and, from phase 3 Task 7, a Device node) and its code is the copy's own.

- [ ] **Step 1: Write the failing tests:** the dialog opens from a C# device card with the kind's header and lists `Address` under Settings; a check sends `kind`; Apply with a signature that drops `Address` (wired from a Setting) and makes `Token` secret (typed) removes the wire and the value and shows both notices; `applyClassCode` unit cases for kept / cleared / removed; drop and paste keys; the Custom step's tests unchanged.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Run** the frontend suite, `tsc`, the build.
- [ ] **Step 5: Commit** — `A C# entity or C# device node's code is edited in the code editor, and applying it refits the node's settings.`

---

### Task 8: The cards and the right panel of the C# nodes

**Files:**
- Modify: `FE/src/visual/flow/flowModel.ts`, `FE/src/visual/flow/NodeCard.tsx`, `FE/src/visual/nodeSummary.ts`, `FE/src/visual/NodeConfig.tsx`, `FE/src/visual/layout.ts`, `FE/src/visual/trace/TraceInspector.tsx`, `FE/src/entities/NodePickers.tsx`, `FE/src/visual/KindPreview.tsx` (if it needs the kinds), `FE/src/entities/adapter.ts` (saved keys; live entities for the card)
- Create: `FE/src/entities/CSharpNodeSection.tsx`
- Test: `FE/tests/canvas-cards.test.tsx`, `FE/tests/entity-node-config.test.tsx` (it tests the Device node's section), `FE/tests/EntityDefinitionView.test.tsx`

**Interfaces:**
- Consumes: phase 3's header Device pin, the Entity node's Device select, `DeviceNodeSection`'s Part of control, `SavedEntities` (entity keys and `deviceKeys`).

**Behaviour:**
- **C# entity card:** title the signature's title, summary `{Domain label} · {key}`, the Device pin in the header as an Entity node has it, one input row per setting (a secret shows no value), Edit code.
- **C# device card:** title its name, summary `C# device · {key}`, setting rows, then the entities Home Assistant has for it (name and entity id, from the definition's live entities whose unique id starts `<id>:<device key>:`) or the "appear here after it has started once" line.
- **Right panel** (`CSharpNodeSection`, registered in `stepPickers` for both kinds so no config key shows as a generic row): for a C# device the header input names it (`aria-label="Device name"`); `Key` (locked once the definition was saved with it, by the rule the Entity and Device nodes use); for a C# entity the `Device` select; for a C# device `Part of`; `Edit code`; then the settings as inputs by type: text, a number field that takes whole numbers for `integer` and carries the range, a switch, a select of the enum's members; a secret has the hint and no editor; each can instead be wired (the existing input row's wire state).
- The canvas key (`viewKey`) holds what these cards show beyond the node itself: the live entities of a C# device.
- **Header, saved keys, layout, trace.** The inspector's header name input writes `config.name` for any declaring node today: a C# entity has no `name`, so its header shows the signature's title as text, not an input. The saved keys that lock `Key` come from the saved graph (as `deviceKeys` does), not from `document.entities`, so a saved C# entity's key is locked whatever the stored list holds. Tidy layout (`layout.ts`) assumes a declaration only feeds other nodes: a C# entity is placed as an Entity node is (in the column after its Device node), and a Setting node wired into a C# node's setting pin in the column before that node, with no two cards overlapping; the layout of a graph without C# nodes does not change. In a run trace a C# node is drawn as phase 3 draws a Device node (a declaration: never "not reached"), and the trace inspector says `A C# entity is not a step: it does not run.` / `A C# device is not a step: it does not run.`

- [ ] **Step 1: Write the failing tests:** each card's title, summary and rows; the device card with two live entities and with none; the section's fields per kind; key locked after a save; an enum select writes `{kind: "literal", type: "string", value}`; an integer field refuses `1.5`; a secret has no editor; changing Part of to another C# device writes `{device: key}`; a renamed setting label (after Apply) shows on the card at once; a C# entity's header has no name input; a saved C# entity's `Key` is locked; `tidyLayout` of a Device node, a C# entity wired to it and a Setting wired into the C# entity overlaps nothing and puts both sources in earlier columns; a C# node in a trace has no unreached style and the inspector's sentence.
- [ ] **Step 2: Run**, expect FAIL. **Step 3: Implement.** **Step 4: Run** the frontend suite, `tsc`, the build.
- [ ] **Step 5: Commit** — `A C# entity and a C# device node show their settings, key and device on the canvas and in the right panel.`

---

### Task 9: The example, the smoke test and the documents use C# nodes

**Files:**
- Modify: `CC/authoring_examples.json` ("Plug from a class" becomes a graph with one C# device node and a Setting wired to `Address`; a second, small example of a C# entity node), `tests/test_docs_examples.py`
- Modify: `CC/llm.py` (`SEMANTICS["csharp"]`, `SEMANTICS["definitions"]`, tool descriptions), `tests/python/test_mcp_adapter.py`
- Modify: `tests/packaging/entity_bridge_smoke.py` (its class half moves onto a C# device node and a C# camera entity node; the class kind keeps one small case until phase 5)
- Modify: `docs/custom-entities.md` (the "Classes" part becomes "C# nodes": the two nodes, settings as pins, keys and ids, Setup / Start / Stop, commands, secrets, saving and replacing), `docs/node-libraries.md` ("Custom C# nodes" names the two entity kinds; "Entity classes" says a class in a library is used by deriving from it in a node's code), `docs/backend/entity-definitions.md` (shapes, `custom_preview`'s `kind`, the derived lists), `docs/mcp-authoring.md`, `docs/backend/visual-automations.md` (`custom_preview`)
- Test: `tests/Runtime/AuthoringAdapterTests.cs` (every example previews valid and derives its lists)

**Behaviour:**
- Every example previews with no problem through the real authoring path with `deriveSignatures: true`, and the stored signature of each C# node is what the compiler derives.
- The MCP guide tells an author: the two kinds and their config, that `signature` may be left out when `deriveSignatures` is on, that a setting pin takes `{kind: "literal"}` or a wire from a `setting` node, that a secret takes only a wire, the key and unique-id rules, and that a C# device's entities are listed once it has run.
- The smoke test drives the five entity delegates through a C# device node and a C# camera entity node on the real assembly, and still reports what it reports today.
- The docs' code samples are the examples word for word (`tests/test_docs_examples.py`).
- The config members of the `device` and `csharp_device` kinds are written out in `llm.py`'s `SEMANTICS`, the `get_node_schema` description and the three documents: one test (`tests/test_docs_examples.py` or `test_mcp_adapter`) compares each of those lists with the catalog's `nodes.<kind>.config` keys, so they cannot drift (carried from phase 3's review).

- [ ] **Step 1: Write the failing tests** (the examples test, the docs word-for-word test, the MCP description pins). **Step 2: Run**, expect FAIL. **Step 3: Write** the examples, the guide and the documents; move the smoke test. **Step 4: Run** every suite, publish, both smokes in the container.
- [ ] **Step 5: Commit** — `The plug example, the guide and the documents build custom C# entities and devices as nodes.`

---

## Rulings

1. **`dynamicInputs: "settings"` marks the two kinds; the `custom` kind is not touched.** Editing `custom` would change the catalog embedded in every automation that uses it. Cost if wrong: one helper to rename.
2. **A C# entity declares an entity; hub-only rules move behind `IsHub`.** The spec gives it `declares: "entity"` and `declaration: true`. Every existing reader of `declares == "entity"` is classified in Task 1. Cost if wrong: a rule on the wrong side of the split, found by its fixture case.
3. **The node owns the key; the code owns the name.** The spec's config has `key` and no name. The host gives the entity its key (`Adopt`). A standalone C# entity whose code sets no name is called the class's title, so it never trips `entity.name`. Cost if wrong: a default name the user overrides in code.
4. **Steps cannot set a C# entity's state.** The class owns it. Set state, Set available and Show frame offer Entity nodes only. Cost if wrong: one list widened.
5. **A C# device's entities are not stored.** The spec says they are null until it has run; answers show them from what Home Assistant has registered, as a class definition's are shown today. Cost if wrong: a stopped definition's card shows its C# device without entity rows.
6. **The document version is not raised.** A phase 3 document holds nothing that reads differently now. Cost if wrong: none; the lists are derived again at every save.
7. **Whole numbers, ranges and enum members are checked by the server only** (`setting.value`, at preview and save), while the right panel's fields keep a person from typing a wrong one. The TypeScript validator does not mirror the phrase builder. Cost if wrong: a wrong value typed through MCP is reported one step later than a wrong type.
8. **A secret pin may be wired from a Setting that is not marked secret.** Where the value is kept is then the author's choice. Cost if wrong: one more rule.
9. **A C# node is a new instance on every revision and restores from the store**, as a class is today; only hubs carry state over in memory. Cost if wrong: a C# entity without `RestoreState` shows unknown for a moment after a save.
10. **The class kind keeps its own prepare path until phase 5**, sharing the extracted set-up method. Cost if wrong: none; phase 5 deletes it.
11. **A C# entity can sit on a plain Device node, not on a C# device.** A C# device has no Entities pin (spec: its pins are its settings); its entities are the ones its code adds. Cost if wrong: one output pin added later.
12. **Amended after phase 3, against the code at 5977b43** (a read-only check of every name and path this plan relies on): six file paths corrected; `HasCode` and `IsHub` defined from the spec's own flags; `Fits` made internal; the generated source of an entity graph without C# nodes is held by a hash test added before the generator changes, because the pin file holds no entity graph; `definition.entitiesRequired` gets a preview test, because the validators do not raise it; forgetting a retyped C# entity moved from Task 5 to Task 6, where the stored list first holds C# entities; the two frontend readers of a unique id's tail named. Cost if wrong: a task's implementer finds one more name that moved and says so.
13. **Amended from phase 3's final review** (its notes on what phase 3's code would do silently when a second declaring kind arrives): the derived lists switch on the kind and never carry a C# node's code (Task 1); a graph never gets the class path's `main` device, and devices are ordered parents first across both kinds (Task 5); the header Device pin and the Device select know a declaration from a root and a C# device from a Device node (Task 1); the inspector's header, the saved keys, the layout and the run trace handle a C# node (Task 8); the config members written out in the guide and the documents are tied to the catalog by a test (Task 9). Cost if wrong: a rule stated in a task that the code already satisfied.

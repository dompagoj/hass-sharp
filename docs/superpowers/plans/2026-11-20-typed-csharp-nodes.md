# Typed settings and typed services for C# nodes: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `[Setting]` of a C# entity or device class can be an entity handle and can be required, a class can follow the entity it was given, and a class calls services with typed methods.

**Architecture:** A setting's entry in a node's `signature.settings[]` gains two optional members, `domain` (with the new type word `entity`) and `required`; everything that turns a setting into a pin reads them through one function per side instead of a five-word table. The host turns a typed entity id into a handle of the property's class before `Setup()`. Typed services reach a class through a real `Services` member of `SharpComponent` whose type lives in HassSharp; the generated assembly adds one extension property per domain to that type and holds a caller interface instead of `NodeContext`.

**Tech Stack:** C# (.NET 11, Roslyn) in `custom_components/hass_sharp/HassSharp/` (`HS/`), TypeScript/SolidJS in `custom_components/hass_sharp/frontend/` (`FE/`), Python in `custom_components/hass_sharp/` (`CC/`).

**Spec:** `docs/superpowers/specs/2026-11-20-typed-csharp-nodes-design.md`

**Code map** (git-ignored, verified at 260010a; line numbers drift, names do not): `.superpowers/sdd/typed-csharp-nodes-code-map.md`. Every task's implementer reads the sections its task names before reading code.

## Global Constraints

- **The signature's shape.** One entry of `signature.settings[]` is `{key, label, description, type, secret, options?, minimum?, maximum?, domain?, required?}`. `type` is one of `string`, `integer`, `number`, `boolean`, `enum`, `entity`. `domain` is written only for `type: "entity"` whose property is a domain's class (`"domain": "light"`), never for plain `Entity`. `required` is written only when true. A signature without the two members reads exactly as it does today, so every stored document, example and fixture case that has neither is unchanged, byte for byte.
- **Pin type of a setting.** `string`→`string`, `enum`→`string`, `integer`→`number`, `number`→`number`, `boolean`→`boolean`, `entity`→`entity_id`, `entity` with `domain`→`entity_id:<domain>`. One function per side gives it: C# `VisualNodeCatalog.SettingPinType(JsonObject setting)`, TypeScript `settingPinType(setting: ClassSetting)`. No other code maps a setting's type to a pin type.
- **Required.** A setting is required when its property's type is `string` or an entity handle, is not nullable, and the property has no initializer. A value type (number, bool, enum) is never required. Nothing else marks a setting required.
- **Exact copy.**
  - Bad type: `A setting is a string, bool, int, long, double, enum or an entity class such as LightEntity; {type} cannot be one`
  - Secret entity: `An entity setting cannot be secret`
  - Empty required pin: the validators' existing sentence for a required input, `{label} is required` (no code, as today).
  - Start with a required setting that has no value: reason `the setting {Label} has no value`, in the host's existing failure form `{Definition}: {Node name}: {reason}`.
  - `setting.value` phrase for an entity: `an entity id` without a domain, `an entity id that starts with {domain}.` with one (so: `Source takes an entity id that starts with sensor.`).
  - Code editor's Settings panel, type words: `Entity`, and `Entity ({domain})` with a domain.
- **The two validators agree.** `HS/Visual/VisualGraphValidator.cs` and `FE/src/visual/contract.ts` give byte-identical messages and codes on `tests/fixtures/visual-graph-validation.json`. A new rule or a changed sentence gets its fixture case first.
- **Automations do not change.** The generated source of an automation is byte-identical (`tests/Runtime/GeneratedSourcePinTests.cs`, 117 pins), and so is that of an entity graph without C# nodes (`AnEntityGraphWithoutCSharpNodesKeepsItsSource`, `ADeviceNodeOutOfCatalogOrderKeepsItsSource`). No expected value in those tests is edited. `custom_components/hass_sharp/visual_node_catalog.json` changes only where a task says so.
- **A Custom C# step is not changed**: what it accepts, its pins, its generated call, `context.Services`.
- **Secrets**: no secret's value appears in a source, an answer, a message, a trace or the page; this plan changes no rule about them except refusing a secret entity setting.
- **Locks**: `AGENTS.md`, "Locks in C# code".
- **Commits**: one per task unless the task says otherwise, files staged by name, the subject line only (no body, no attribution line).
- **Checks**: each task runs the suites its files touch and says which; the suites are in `AGENTS.md`. The runtime suite is a custom runner: `dotnet run --project tests/Runtime --disable-build-servers -- --<flag>` runs one suite.

## Review Focus

1. **A handle class that does not exist yet.** The generated entity classes exist only after Home Assistant has pushed its entity schema (`HS/DiagnosticsProvider.cs`); a definition with a `LightEntity` setting that is compiled before that fails. Expected: it starts once the schema arrives (failed loads are retried), and the failure sentence in between names the node. Test in Task 3.
2. **The entity given is of another domain, or does not exist.** `switch.fan` typed into a `LightEntity` pin through MCP: refused at preview and save with the existing wrong-domain sentence, in both validators; and if such a value reaches the host from a stored file, the start fails with the saved-value sentence, naming the node. An id of the right domain that Home Assistant does not have: the definition starts, and reading the handle throws the existing `Entity '{id}' is unavailable.` in the class's own code. Tests in Tasks 2 and 3.
3. **A required setting wired to a Setting node that has no value.** The pin is connected, so the validators are satisfied; the value is null. Expected: the start fails with `the setting {Label} has no value`; `Setup()` never runs with it unset. Test in Task 3.
4. **A stale read.** `Light.State` read in a timer callback and in a `Track` callback after the entity changed gives the new state, not the state at start and not a trigger's. Test in Task 3.
5. **Code changed under a stored value.** A property retyped from `string` to `LightEntity`, or from `LightEntity` to `SwitchEntity`: Apply in the editor removes the value that no longer fits and says so; an MCP save keeps it and is refused by the validators with the sentence on that pin. Tests in Tasks 2 and 5.

---

### Task 1: The compiler describes entity settings and required settings

**Files:**
- Modify: `HS/Visual/NodeLibraryCompiler.cs` (`SettingType`, `SettingTypeMessage`, `EntityClasses.CheckSettings`, `DescribeClassNode`, `DescribeEntityType`), `HS/Visual/VisualCatalogSnapshot.cs` (`EntitySettingDescriptor`), `HS/Visual/VisualGenerator.cs` (`SameSettings`)
- Test: `tests/Runtime/CustomNodeTests.cs` (`--custom-nodes`), `tests/Runtime/EntityHandleTests.cs` if its schema fixture is the easiest way to have domain classes in a test compilation

**Interfaces:**
- Consumes: `EntityPin(ITypeSymbol)` (gives `entity_id` / `entity_id:<domain>` for a handle type, null otherwise), `Nullable(ITypeSymbol)`, `Scalar(ITypeSymbol)` (code map A, D).
- Produces: `EntitySettingDescriptor` gains `string? Domain` and `bool Required` (init members, as `Minimum`/`Maximum` are). `SettingType` returns kind `"entity"` with the domain (null for `Entity`) for a handle type. The signature JSON as in Global Constraints. `NodeLibraryCompiler.SettingSecretEntityMessage` (a constant) and the reworded `SettingTypeMessage(type)`. `SameSettings` also compares `domain` and `required`.

- [ ] **Step 1: Failing tests** in `CustomNodeTests`, with a compilation that has generated entity classes for at least `light` and `sensor` (see how `EntityHandleTests` or the Custom step's handle tests get them):
  - `ClassSignatures`: a class with `[Setting] public LightEntity Light { get; set; }`, `[Setting] public Entity? Any { get; set; }`, `[Setting] public string Host { get; set; }`, `[Setting] public string Zone { get; set; } = "UTC"`, `[Setting] public double Rate { get; set; }` gives settings: Light `{type:"entity", domain:"light", required:true}`; Any `{type:"entity"}` with no `domain` and no `required` member; Host `{type:"string", required:true}`; Zone and Rate with no `required` member. Assert on the JSON text of each entry, so an extra member fails.
  - A setting declared in a library base class with no initializer is required in the node's signature (the initializer is read from the base's syntax).
  - `ClassProblems`: `[Setting(Secret = true)] public LightEntity Light { get; set; }` is refused with exactly one error, `An entity setting cannot be secret`, at the property's name; `[Setting] public DateTime When { get; set; }` is refused with the new `SettingTypeMessage("DateTime")` (update the existing assertion); `[Setting] public LightEntity[] Lights { get; set; }` is refused with `SettingTypeMessage("LightEntity[]")`.
  - `SameSettings`: two signatures that differ only in `required`, and two that differ only in `domain`, are not the same.
- [ ] **Step 2:** Run `--custom-nodes`; the new checks fail.
- [ ] **Step 3: Implement.** `SettingType` asks `EntityPin` for a handle type (after unwrapping a nullable annotation); required as in Global Constraints, read from the property's declaration syntax (`PropertyDeclarationSyntax.Initializer`), false when the symbol has no syntax. Range attributes on an entity setting are refused by the existing `SettingRangeTypeMessage`. `CheckLibrarySettings` goes through the same check, so a library class's entity setting is accepted too.
- [ ] **Step 4:** `--custom-nodes`, `--entity-handles`, `--generated-source-pins`, `--entity-graph-generator` pass.
- [ ] **Step 5: Commit**: `A setting of a C# node's class can be an entity handle, and says when it is required.`

### Task 2: A setting's pin: type, required, and the checks, in both validators

**Files:**
- Modify: `HS/Visual/VisualNodeCatalog.cs` (`SettingPinTypes` → `SettingPinType`, `WellFormedClassSignature`, `SettingInput`), `HS/Devices/DefinitionGenerator.cs` (`Fits`, `Phrase`), `HS/Visual/VisualGenerator.cs` (`SettingValues`, `DeriveSettings` only if they name the table), `FE/src/visual/contract.ts` (`ClassSetting`, `settingPinTypes` → `settingPinType`, `wellFormedClassSignature`, `settingInput`), every other reader of the two tables on either side
- Modify: `tests/fixtures/visual-graph-validation.json` (new cases only)
- Test: `tests/Runtime/VisualGraphContractTests.cs` (`--visual-graph`), `tests/Runtime/EntityGraphGeneratorTests.cs` (`--entity-graph-generator`), `FE/tests/visual-contract.test.ts`, `FE/tests/entity-node.test.ts`

**Interfaces:**
- Consumes: the signature shape of Task 1.
- Produces: `VisualNodeCatalog.SettingPinType(JsonObject setting) : string?` (null for a type word it does not know) and `settingPinType(setting: ClassSetting): string | undefined`; `ClassSetting` in TypeScript gains `type: … | 'entity'`, `domain?: string`, `required?: boolean`. `SettingInput` / `settingInput` give `required` from the signature and carry `domain` and `required` inside the input's `setting` member beside `type`. A well-formed signature: `domain` only on `entity`, a non-empty lower-case domain word (`^[a-z0-9_]+$`); `required` a boolean.

- [ ] **Step 1: Fixture cases first** (each with `messages` and `codes`, on a `csharp_entity` node whose `config.signature` is written inline, as the existing `setting.secretLiteral` cases are):
  1. valid: an `entity`/`light` setting with the literal `{kind:"literal", type:"entity_id:light", value:"light.kitchen"}`.
  2. the same pin with `switch.fan`: the existing wrong-domain sentence for an entity pin (read `WrongDomain` in the validator for its exact form), on that input.
  3. a plain `entity` setting (no domain) with `switch.fan`: valid.
  4. an `entity`/`light` setting wired from a Setting node: refused on that input by the existing type sentence (a Setting node gives text).
  5. a required `entity` setting left unset: `{Label} is required`.
  6. a required `string` setting left unset: `{Label} is required`; the same wired from a Setting node: valid.
  7. a required secret `string` setting with no wire: `{Label} is required` and no second message; with a typed value: the existing `setting.secretLiteral` sentence alone.
  8. a signature with `domain` on a `string` setting, one with `domain: "Light Bulb"`, one with `required: "yes"`: each `csharp.signature` with the existing unreadable-settings sentence.
- [ ] **Step 2:** `--visual-graph` and `bun run test tests/visual-contract.test.ts` fail on the new cases.
- [ ] **Step 3: Implement on both sides** until the fixture passes with byte-identical messages. No change to how any other node kind's inputs are derived.
- [ ] **Step 4: `setting.value`** (server only, `EntityGraphGeneratorTests.ASettingValueMustFit`): `Fits` for an `entity` setting takes a string that has the shape of an entity id and, with a domain, starts with `{domain}.`; `Phrase` as in Global Constraints. Tests: a number given to an entity setting through a hand-made graph is `Source takes an entity id that starts with sensor.`; a pin that already has the validator's wrong-domain error gets no second `setting.value` message (the existing skip).
- [ ] **Step 5: MCP's derivation** (`DeriveSettings`, Review Focus 5): a graph whose node's code changed `string Host` to `LightEntity Host` keeps the old text value and is refused with the validator's sentence on that pin; a setting that became required and has no value is refused with `{Label} is required`. Tests beside the existing derivation tests in `EntityGraphGeneratorTests` or `CustomNodeTests.ClassNodesInAGraph`.
- [ ] **Step 6:** `--visual-graph`, `--entity-graph-generator`, `--custom-nodes`, `--generated-source-pins`, `--contract-guards`; frontend `bun run test` and `bunx tsc --noEmit`.
- [ ] **Step 7: Commit**: `A setting's pin takes its type and whether it is required from the signature, in both validators.`

### Task 3: The host gives the class its handle, and refuses to start without a required value

**Files:**
- Modify: `HS/Devices/DefinitionSettings.cs` (`TryConvert`, `Apply`), `HS/Devices/DefinitionGraph.cs` (`Components`, `DefinitionComponent`), `HS/Devices/DefinitionHost.cs` / `DefinitionHost.Graph.cs` (the start failure), `HS/Devices/SharpComponent.cs` (`Track` overload), `HS/Entities/Entity.cs` only if Step 4 shows a stale read
- Test: `tests/Runtime/EntityHostTests.cs` (`--entity-host`), `tests/Runtime/EntityGraphGeneratorTests.cs` (`ComponentsResolveTheirSettings`), `tests/Runtime/EntityStartupTests.cs` (`--entity-startup`) or `EntityGraphTests.cs` for the end-to-end cases

**Interfaces:**
- Consumes: the signature (Task 1) and `SettingPinType` (Task 2).
- Produces: `DefinitionComponent` gains `IReadOnlyList<string> Unset` — the labels of its required settings that have no value (empty when none), worked out in `DefinitionGraph.Components` from the signature and the resolved values. `protected IDisposable Track(Entity entity, Func<EntityChange, Task> action)` on `SharpComponent` (throws `ArgumentNullException` for a null handle; otherwise `Track(entity.EntityId, action)`).

- [ ] **Step 1: Failing tests.**
  - `EntityHostTests.Settings`: a property `LightEntity Light` given `"light.kitchen"` is a `LightEntity` whose `EntityId` is `light.kitchen`; `Entity Any` given `"switch.fan"` is an `Entity`; a `LightEntity` property given `"switch.fan"`, a number, or `"not an id"` fails with the existing `{name}: the saved value of setting {key} does not fit its type LightEntity`; a nullable `LightEntity?` with no value stays null.
  - `ComponentsResolveTheirSettings`: a typed entity literal arrives as its id text; a required setting with no literal and no wire, and one wired to a Setting node whose definition setting has no value, are listed in `Unset` by label; an optional one is not.
  - End to end through `EntityApi` (a save, then the running definition), with a class `Follower : SharpSensor` that has `[Setting] public SensorEntity Source { get; set; }` and in `Start()` does `Track(Source, change => { Value = Source.NumericState; return Task.CompletedTask; })`: after the test home changes `sensor.a` twice, the follower's value is the second state (Review Focus 4, the track half); a timer in the same class reading `Source.NumericState` sees it too (the timer half).
  - Review Focus 3: a required string setting wired to a Setting node whose definition setting has no value: the save is accepted, the definition does not run, its status and the log give `{Definition}: {Node name}: the setting {Label} has no value`, and the class's `Setup()` was not called (a flag the test class sets).
  - Review Focus 1: with a manager whose entity schema has not been pushed, loading a stored definition with a `LightEntity` setting fails with a sentence naming the node; pushing the schema starts it (failed loads are retried). If the existing startup tests have no way to load before the schema, say so in the report and test the retry at the level `RetryFailedLoads` is tested today.
  - Review Focus 2, existence: an id of the right domain that the test home does not have: the definition starts, and `Source.RawState` in the class throws `Entity 'sensor.missing' is unavailable.`
- [ ] **Step 2:** Run the suites; the new checks fail.
- [ ] **Step 3: Implement.** `TryConvert`: a property type assignable to `Entity` takes a JSON string and makes the handle with the type's `(string)` constructor (as `EntityJsonConverter` does); a constructor that throws means "does not fit". The start failure is raised where settings are applied, before the class's `Setup()`, for the first label in `Unset`, through the host's existing failure path (so the definition's status, the log line and the cleanup are the existing ones). The constructor still runs before settings are applied; that order does not change.
- [ ] **Step 4: The stale read.** If the end-to-end test shows a handle reading a stale state inside a component (`Entity.Snapshot()` reads without `fresh`, code map E and L3), make a read from a component callback fresh without changing what a step's code reads in a run: the 117 pins and `EntityHandleTests.TriggerState` stay as they are. Report what you found either way.
- [ ] **Step 5:** `--entity-host`, `--entity-graph-generator`, `--entity-startup`, `--entity-graph`, `--entity-handles`, `--sharp-entities`; then the whole runtime suite once.
- [ ] **Step 6: Commit**: `A C# node's class is given its entity handles before Setup, and does not start without a required setting.`

### Task 4: Typed services inside a class

**Files:**
- Create: `HS/Visual/ServiceCaller.cs` (or beside `NodeContextServices.cs`): the interface and the component's type
- Modify: `HS/ServiceGenerator.cs`, `HS/Visual/NodeContext.cs` (implements the interface), `HS/Devices/SharpComponent.cs` (`Services`)
- Test: `tests/Runtime/ServiceCallTests.cs` (`--service-calls`), `tests/Runtime/SharpEntityTests.cs` (`--sharp-entities`), `tests/Runtime/EntityHostTests.cs` (`ServiceFromStop`), `CC/HassSharp.Editor.Tests/NodeLibraryLanguageServicesTests.cs`, `DiagnosticsProviderTests.cs`

**Interfaces:**
- Produces, in namespace `HassSharp`:
  - `public interface IServiceCaller { Task<JsonElement?> CallService(string service, JsonNode? target = null, JsonObject? data = null, bool returnResponse = false); }` — `NodeContext` implements it with its existing method.
  - `public sealed class ComponentServices : IServiceCaller` with an internal constructor taking the component; its `CallService` is the component's `CallService` (same preparation, same wait on the host's calls token, usable from `Stop()`).
  - `protected ComponentServices Services` on `SharpComponent`.
  - Generated (`ServiceGenerator`): the root and every domain class hold `global::HassSharp.IServiceCaller __context` (the field's name and the line `__context.CallService(…)` do not change); `NodeContextServices` gains a second extension block, `extension(global::HassSharp.ComponentServices services)`, with one property per domain (`public global::HassSharp.ServiceCalls.LightServices Light => new(services);`), each with the domain's doc summary. `context.Services` for a `NodeContext` is unchanged.
- So a class writes `await Services.Light.TurnOn(Light, brightnessPct: 30);` — `Services` is the real member, `.Light` the generated extension property.

- [ ] **Step 1: Failing tests.**
  - `ServiceCallTests`: the generated text has the second extension block with a property for each domain of the fixture and its doc comment; the constructors take `IServiceCaller`; the existing assertions on `__context.CallService(…)` lines pass unchanged; the generated source still compiles without diagnostics and is deterministic (`DiagnosticsProviderTests`).
  - A C# entity class, saved through `EntityApi` in the test home: `Start()` calls `await Services.Light.TurnOn(Light, brightnessPct: 30)` with `Light` a `LightEntity` setting; the home records the call `light.turn_on` with target `light.kitchen` and `brightness_pct: 30`. The same from `Stop()` completes (as `ServiceFromStop` proves for `CallService`). A service with a response returns it.
  - Step code is unchanged: an existing test of `context.Services` passes untouched, and the 117 pins pass.
  - Editor (`NodeLibraryLanguageServicesTests`): in a C# entity's code, completion after `Services.` lists the domains, after `Services.Light.` lists `TurnOn` with its documentation, and hover on `Services` gives the member's summary.
  - A class that declares its own member named `Services` still compiles (a warning at most): assert it does.
- [ ] **Step 2:** Run; the new checks fail.
- [ ] **Step 3: Implement.** Keep `ServiceGenerator`'s output deterministic and its spans (`spans.Add`) covering the new properties, so a service that goes away is reported where it is. XML docs for the new public types say what they are for a class's author.
- [ ] **Step 4:** `--service-calls`, `--service-schema`, `--sharp-entities`, `--entity-host`, `--generated-source-pins`, the editor tests; then the whole runtime suite once; Release publish with no new warning.
- [ ] **Step 5: Commit**: `A C# node's class calls services with typed methods through Services.`

### Task 5: The editor: the picker, the required mark, Apply

**Files:**
- Modify: `FE/src/visual/customCode.ts` (`valueFits`, `applyClassCode`), `FE/src/visual/CustomCodeEditor.tsx` (`settingTypeWords`), `FE/src/visual/widgets/model.ts` and `FE/src/visual/InputRow.tsx` only if a test shows the entity widget or the required mark missing for a setting pin, `FE/src/entities/model.ts` (types)
- Test: `FE/tests/custom-code.test.tsx`, `FE/tests/widgets.test.tsx`, `FE/tests/entity-node-config.test.tsx`, `FE/tests/canvas-cards.test.tsx`

**Interfaces:**
- Consumes: `settingPinType`, `ClassSetting` (Task 2).

- [ ] **Step 1: Failing tests.**
  - A `csharp_entity` node whose signature has an `entity`/`light` setting shows, on its card and in the right panel, the entity picker limited to lights (the widget is `{kind: 'entity', domains: ['light']}`), and a plain `entity` setting the picker with no limit; choosing an entity writes `{kind:'literal', type:'entity_id:light', value:'light.kitchen'}` into `config.arguments.<key>`.
  - A required setting's row has the required mark every other required input has, and the node shows the problem `{Label} is required` while it is empty.
  - `applyClassCode` (Review Focus 5): `string` → `entity`/`light` removes a text value and names the setting in the notice; `entity`/`light` → `entity`/`switch` removes `light.kitchen`; `entity`/`light` → plain `entity` keeps it; `entity`/`light` → `entity`/`light` with `required` added keeps it; a wire from a Setting node into a pin that became an entity is removed with the existing "Removed 1 connection" notice.
  - The code editor's Settings panel names an entity setting's type `Entity (light)` and a plain one `Entity`.
  - `rangeHint` gives nothing for an entity setting.
- [ ] **Step 2:** `bun run test` on those files; the new tests fail.
- [ ] **Step 3: Implement.**
- [ ] **Step 4:** `bun run test`, `bunx tsc --noEmit`, `bun run build`.
- [ ] **Step 5: Commit**: `The editor picks an entity for an entity setting and marks a required one.`

### Task 6: What an assistant reads, and the guide

**Files:**
- Modify: `CC/llm.py` (`SEMANTICS["definitions"]`), `tests/python/test_mcp_adapter.py` (the phrase lists), `docs/custom-entities.md` ("Settings as inputs", "Timers, tracks and Home Assistant", a new short part "Follow another entity" with one class), `docs/mcp-authoring.md` ("C# entity and C# device nodes": "Settings are pins"), `docs/backend/entity-definitions.md` (the signature's members, the start failure), `docs/node-libraries.md` only where it states what a setting can be
- Test: `tests/python/test_mcp_adapter.py`, `tests/test_docs_examples.py`, a runtime test that compiles the guide's class

**Interfaces:**
- Consumes: everything above, as built (read the code, not this plan, for what is true).

- [ ] **Step 1: Failing tests.** `test_mcp_adapter.py`: the definitions text names an entity setting (`Entity` or a domain's class such as `LightEntity`), how its value is given (`{kind: literal, type: entity_id:light, value: light.kitchen}`), that a string or entity setting with no initializer and no `?` is required, that an entity pin takes no Setting node, `Track(entity, …)`, and `Services.<Domain>.<Service>(…)`; the old sentence "a string, bool, int, long, double or enum" is in a gone-list. A runtime test compiles the class the guide shows (keep the two equal by having the test read the class from the guide's code block, as `tests/test_docs_examples.py` reads examples) and checks its signature: one required `entity`/`sensor` setting.
- [ ] **Step 2: Write.** The guide's class is a sensor that follows another sensor and calls a typed service, about fifteen lines:

```csharp
/// <summary>Twice what another sensor reads; turns a light on while that is above a limit.</summary>
public class Doubled : SharpSensor
{
    /// <summary>The sensor to follow.</summary>
    [Setting("Source")]
    public SensorEntity Source { get; set; }

    /// <summary>The light to turn on while the reading is above the limit; none when left empty.</summary>
    [Setting("Warning light")]
    public LightEntity? Light { get; set; }

    /// <summary>The reading above which the light is on.</summary>
    [Setting("Limit")]
    public double Limit { get; set; } = 100;

    /// <summary>Follows the source from the moment the definition runs.</summary>
    protected override Task Start()
    {
        Track(Source, async change =>
        {
            Value = Source.NumericState * 2;
            if (Light != null && Source.NumericState > Limit) await Services.Light.TurnOn(Light);
        });
        return Task.CompletedTask;
    }
}
```

  If this does not compile as written against the built code (a member's name, a warning for the uninitialized `Source`), change the sample to what does and say so in the report; a compiler warning the author would see for a required setting without an initializer is worth one sentence in the guide.
- [ ] **Step 3:** The Python suites on the host and in the container, `tests/test_docs_examples.py`, the runtime suite; every in-page link of the edited documents resolves.
- [ ] **Step 4: Commit**: `The guide and the assistant's texts describe entity settings, required settings and typed services.`

---

## Rulings

Decisions this plan made where the spec left room. Format: what, why, cost if wrong.

1. **Settings gain entity handles only**, not every type a step parameter accepts. A list, an object or an author's class has no typed value in a pin and can only arrive on a wire from a flow, which a setting does not have. Cost if wrong: one more kind per type (`SettingType`, the pin function, convert, fit, the editor's words).
2. **Required is derived, not declared**: a non-nullable string or entity property with no initializer. A value type is never required, since it always has a value. Cost if wrong: an attribute property `Required` to add.
3. **A class that sets a default in its constructor still counts as required** when the property has no initializer: only the declaration is read. The guide says to use an initializer. Cost if wrong: flow analysis nobody wants.
4. **An entity pin takes no Setting node.** Definition settings stay string, number, boolean. Cost if wrong: an entity type for definition settings, on both sides and in the right panel.
5. **The empty required pin uses the validators' existing sentence without a code.** One sentence for every required input of every node. Cost if wrong: a code to add in both validators.
6. **`Services` is a real member whose type is in HassSharp; the generated assembly extends that type.** HassSharp cannot reference the generated assembly, and an extension property on `SharpComponent` itself would have to be written `this.Services`. Cost if wrong: one generated block to move.
7. **`Services` works wherever `CallService` works**, the constructor included, where the spec first said "from `Setup()` on". It is the same call. Cost if wrong: one guard.
8. **A stored signature that lacks `required` for a setting that is now required is out of date**, and the node says so at the next preview, as for any code change. Nothing has shipped. Cost if wrong: none.
9. **No new authoring example** in `authoring_examples.json`; the guide's class is held by its own test. The examples file feeds several pinned lists. Cost if wrong: one example and the lists that count them.

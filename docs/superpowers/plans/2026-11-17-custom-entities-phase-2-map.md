# Phase 2 code map (graph-backed entity definitions)

Repo `/Users/dompa/dev/HassSharp`, branch `custom-entities`, HEAD `207c814`. Paths are under
`custom_components/hass_sharp/` unless they start with `tests/`. Every line number below was read at HEAD.
Short names: `VNC` = `HassSharp/Visual/VisualNodeCatalog.cs`, `VGV` = `HassSharp/Visual/VisualGraphValidator.cs`,
`VG` = `HassSharp/Visual/VisualGenerator.cs`, `VR` = `HassSharp/Visual/VisualRuntime.cs`, `ct` = `frontend/src/visual/contract.ts`.

## 1. Catalog

**File:** `visual_node_catalog.json` (1931 lines). It is embedded as a resource (`HassSharp/HassSharp.csproj:24`
`LogicalName="HassSharp.visual_node_catalog.json"`), imported by the frontend and read by the Python MCP tests.

Top-level keys, in order: `version` 1 (l.2), `graphVersion` 5 (l.3), `runtimeSupport` (l.4), `limits` (l.5:
nodes 256, edges 512, nesting 8, executionSteps 100000, loopIterations 1000), `types` (l.12), `parametricTypes` (l.160,
only `entity_id`: `{param, valuePattern:"^{param}\\.[a-z0-9_]+$", label}`), `operandKinds` (l.167: `literal`, `unset`),
`nodes` (l.171), `triggerOutputs` (l.1685), `edgeKinds` (l.1824), `structures` (l.1854), `scopeContract` (l.1906).

**Node spec shape.** The spec is keyed by the node kind. There is **no `kind` field in a spec**: a graph node's
`kind` is the key, and "trigger" is detected everywhere by `node.kind == "trigger"`. The fields in use are:
`label, category, description, minGraphVersion, config{}, inputs{}, outputs{}, execution[]` (execution port names),
`runtimeVersions[]`, `pure` (bool, value node), `keywords`, `dynamicInputs` ("service"|"custom"|"fields"|"items"|"loop"),
`dynamicOutputs` ("trigger"|"custom"|"loop"), `dynamicExecution` ("branches"), `scopePorts`, `requiredExecution`,
`argumentStorage` ("arguments"), `comparison{leftInput,rightInput,operatorConfig,orderingOperators,orderingType}`,
and `sourceId` on library nodes.
- A config field is `{type: "string"|"boolean"|"object"|"array"|"number"|"integer", enum?, default?, required?, label, description, pattern?, format?, enumSource?:"types", min?, max?}`.
- An input is `{types:[type ids, "$any", "$properties", "$type"], required, default?: operand, label, description, min?, max?, enum?, connectable?}`.
- An output is `{type, label, description, when?: <bool config key>, propertyOf?: <input>, pointer?, refineFrom?, visibility?, scope?}`.
- Operand: `{"kind":"literal","type":"boolean","value":true}` or `{"kind":"unset"}`.

Node kinds (l.172-1683): trigger, condition, service, delay, custom, if, and, or, xor, not, compare, read_entity,
constant, convert, stop, last_error, log, repeat, while, until, foreach, parallel, condition_result, loop_result,
wait_until, object, array, object_create, array_create, get_field, select_property, entity_field.
- **There is no "is on" node.** No pure node reads Home Assistant state today. `read_entity` (l.728-763) is an
  execution step. `entity_field` (l.1625-1683) is pure but takes an `EntityStateSnapshot` input.

Trigger node (l.172-258): `config.type` enum entity|daily|once|manual|startup|ha (default entity); `entity`, `time`,
`timeZone`, `trigger` (object), `mode` enum Single|Restart|Queued|Parallel (default Single), `manual` (default true),
`trackHistory` (default true), `startup`, `at` (format date-time-offset); `"inputs":{}, "outputs":{}, "dynamicOutputs":"trigger", "execution":["next"]`.
Its outputs come from `triggerOutputs` rows keyed `"*"`, `"entity"`, `"daily|once"`, `"ha:state|ha:numeric_state|ha:template"`,
and so on. For example `entity` → `entity_id` (type entity_id, `refineFrom:"entity"`), `to_state` and `from_state`
(type `global::HassSharp.EntityStateSnapshot`, `pointer:"/to_state"`) (l.1694-1714).

Pure node `or` (l.529-572; `and` l.485, `xor` l.573, `not` l.617 alike): `"pure":true`, inputs `left`/`right` `{"types":["boolean"],"required":true,"default":{"kind":"literal","type":"boolean","value":true|false}}`, output `value` boolean.
`compare` (l.649-727): pure, with `config.operator` enum eq/ne/lt/le/gt/ge, inputs `left`/`right` of types `["$any"]`,
output `value` boolean, and a `comparison` block (l.710-721).

Service action (l.292-345): `config.service` (pattern `^[a-z0-9_]+\.[a-z0-9_]+$`), `config.fields` (object),
`config.returnResponse`; input `target` of types `["target","entity_id"]`; output `response` (`"when":"returnResponse"`);
`execution:["next","error"]`; `dynamicInputs:"service"` (ports `field:<key>`).

Entity-state reader `read_entity` (l.728-763): input `entity` of types `["entity_id"]`, output `value` of type
`global::HassSharp.EntityStateSnapshot`, `execution:["next","error"]`.

**C# loader** (`VNC`):
- `internal static readonly JsonObject Root = Load();` (l.8). `Load()` reads the manifest resource and calls
  `RefreshStructuralTypes` (l.9-16, 17-25). That method rewrites `structures.loopDeclaration.type` and
  `structures.operand.literal.types`.
- There are no record types. The catalog is plain `JsonObject` throughout. The accessors are `GetCatalog(snapshot)`
  (l.26), `GetNode(kind)` (l.27), `Spec(node, snapshot)` (l.28: `Root["nodes"][node.kind]`),
  `IsPure(spec)`/`IsPure(node, spec)` (l.31-33), `ExecutionPorts` (l.181-192), `ScopePorts` (l.194),
  `DataOutputs(node, catalog)` (l.201-215), `DataInputs(node, loopOwner, snapshot)` (l.282-332),
  `TriggerOutputs` (l.250-266), `TriggerType(node, snapshot)` (l.179), and `EffectiveTriggerMethod(node)` (l.178:
  `"Run_" + hex(UTF8(id))`).
- Generation wrapper: `VisualCatalogSnapshot` (`HassSharp/Visual/VisualCatalogSnapshot.cs:76-166`). It holds `Root`, the
  library `Methods`/`Types`/`Custom`, `Services` (never in Root) and `EntityTypes` (never in Root, l.94).
  `BuiltIn` is at l.95 and `WithRoot(root)` at l.125.
- `WithGraphTypes(JsonObject catalog, JsonObject graph)` (l.70-96) lifts two kinds of per-graph data today:
  1. each well-formed Custom C# `config.signature.types` entry whose id the catalog lacks, merging
     `assignableTo`/`assignableFrom` (l.75-89);
  2. for `select_property` nodes (outputs with `propertyOf`), `ResolveOutputs` writes `catalog["resolvedOutputs"][nodeId][port] = type` (l.90-95, 136-175).
  It returns the same instance when nothing is added, and also for an over-limit graph (l.72).
  `GraphNodes(graph)` (l.112-118) is shared by both validators.

**TS loader** (`ct`): `import rawCatalog from '../../../visual_node_catalog.json'` (l.1). The interface is
`VisualCatalogSnapshot` (l.56: services?, version, graphVersion, limits, types, parametricTypes?, nodes, triggerOutputs?,
resolvedOutputs?, structures; there is no index signature and no `entityDomains`). `NodeSpec` is at l.34 (no
`root`/`documents`/`singleton`). `export const nodeCatalog = rawCatalog` is at l.58, and the comment there says to pass
the server catalog per document. The server catalog comes from the library `catalog` operation: `frontend/src/visual/api.ts:132`
calls `HassSharp/Visual/NodeLibraryApi.cs:129`, which returns `saved.Preview.Snapshot.Catalog`.
`withGraphTypes(catalog, graph)` (l.210-233) is the same as the C# one (signature types plus `resolveOutputs`, l.269).
`overLimit` is at l.238.

**Tests that pin the catalog or loader parity:**
- `tests/Runtime/VisualGraphContractTests.cs`: `--visual-graph` (l.6, shared fixture); `--catalog-wording` (l.103-133)
  checks every catalog `description` and node `label` against `BannedWording` (l.135-137: "boolean", "string", "snapshot",
  "typed", "operand", "literal", "payload", `xxx:<…>`).
- `tests/Runtime/ContractGuardTests.cs` (`--contract-guards`) covers type-label guards.
- `tests/Runtime/EntityTypeTests.cs` (`--entity-types`) checks that "the catalog automations embed never carries"
  entity types (header l.3-7).
- `tests/python/test_mcp_adapter.py:39` uses the raw catalog as a fake server catalog (l.48).
- Frontend: `frontend/tests/visual-contract.test.ts` runs the shared cases. No test compares the two loaders' outputs
  directly; parity rests entirely on the fixture.

## 2. Graph contract

- `HassSharp/Visual/VisualGraphContract.cs` (39 lines) has only helpers:
  - `CreateNode(kind, id, snapshot)` (l.9-24) builds config from spec defaults and stores inputs under
    `config.arguments` when `argumentStorage`. It sets `label` unless the node is custom.
  - `FillMissingLayout(graph)` (l.27-38) deep-clones and adds `position`, removing `size`.
  - There are no typed models. The graph is a `JsonObject` everywhere, so unknown keys survive by construction:
    `FillMissingLayout` clones the whole graph, `VisualStore.Save` stores `preview.Graph ?? snapshot`
    (`VisualStore.cs:87-91`), `DeriveSignatures` deep-clones (`VG:242`), and `EntityDefinitionStore.Save` deep-clones
    `Logic.Graph` (`Devices/EntityDefinition.cs:173`).
- Document shape (as validated): `{version: 5, name, nodes: [{id, kind, config{}, label?, position?}], edges: [execution {id, source, target, kind?: "execution", port?} | data {id, kind:"data", source, target, sourcePort, targetPort}]}`.
  - The validator rejects graph keys `imports` and `members` (`VGV:470-471`, `ct:845-846`).
  - Envelope versions: graph `version` must equal catalog `graphVersion` (`VGV:467-468`). `VisualDocument.Version`
    and `EntityDefinitionDocument.Version` are 1 (`VisualStore.cs:48`, `EntityDefinition.cs:88`).
- TS counterpart (`ct:8-12`):
  ```ts
  interface GraphNode { id: string; kind: string; config: Record<string, unknown>; label?: string; position?: { x: number; y: number } }
  interface Graph { version: 5; name: string; nodes: GraphNode[]; edges: GraphEdge[] }
  ```
  It has no extension field, so `graph.definition` needs a type change. At runtime, `fillGraphLayout`/`addGraphNode`
  spread `{...graph}` (`ct:1158-1171`) and keep the extra keys.

## 3. Validators

**C#** `VGV`:
- Entry: `public static List<VisualDiagnostic> Validate(JsonObject graph, VisualCatalogSnapshot? snapshot)` (l.10). An
  internal overload with `out inputTypes` is used by the generator (l.13-18).
- `sealed class Validation(VisualCatalogSnapshot snapshot)` (l.367), `Run(graph)` (l.465-552), `ValidateNode` (l.705-834).
- `VisualDiagnostic(string? NodeId, string Message, int Severity=3, int? StartLine, int? StartColumn, string? InputId, string? PortId, string? Code)` (`VG:14-15`).
- `Codes` (l.32-40), identical to `ct:714-721`: graph.version, graph.name, graph.shape, graph.limit, **graph.noTrigger**,
  node.invalid, node.duplicate, node.unknownKind, **node.unreachable**, limit.nesting, edge.invalid, edge.duplicate,
  edge.endpoint, edge.unknownKind, edge.valueWirePins, edge.orderWirePins, edge.orderPinName, port.unknownExecution,
  port.executionTaken, port.unknownOutput, port.unknownInput, port.inputTaken, **trigger.incoming**, cycle.execution,
  cycle.data, scope.execution, scope.data, scope.conditionResult, scope.loopResult, flow.pure, pure.unused,
  custom.signature, property.unset, property.unknown.
- Run order: version/name/shape/limit (l.467-473) → `WithGraphTypes` (l.476) → nodes (l.477-482) → edges (l.485-514)
  → cycles (l.515) → roots (l.516-518) → reachability (l.520-522) → data edges and types (l.523-549) → `ValidateNode` (l.550).

  The trigger-root code:
  ```csharp
  510: if (Kind(nodes[target]) == "trigger") Error(target, "Trigger cannot have incoming execution edges", code: "trigger.incoming");
  516: var roots = nodes.Values.Where(n => Kind(n) == "trigger").ToArray();
  517: if (roots.Length == 0) Error(null, "At least one trigger is required", code: "graph.noTrigger");
  518: foreach (var root in roots) Scope(Id(root), []);
  520-522: if (Pure(node)) { if (!data.Any(e => source == id)) warning "pure.unused" (severity 2) }
           else if (!scopes.ContainsKey(id)) Error(..., "node.unreachable");
  ```
- A pure node reading step outputs needs them visible to each step reading it (`PureSources` l.450-464, used l.544-547).
- Other places that check `kind == "trigger"`: the `at` skip (l.728) and the trigger config rules (l.780-791). Both
  are trigger-specific and fine as they are.

**TS** `ct`:
- `validateGraph(value, catalog)` (l.779), `analyzeGraph` (l.785-~1150, returns `GraphAnalysis` l.744-762 with
  `scopes`, `pureSources`, `resolved`), `analyzeScopes` (l.783, documented "every node reachable from a trigger").
- Trigger-root code (the same rules as C#):
  ```ts
  883: if (nodes.get(edge.target)!.kind === 'trigger') error(edge.target, 'Trigger cannot have incoming execution edges', ..., 'trigger.incoming')
  912: const roots = [...nodes.values()].filter(n => n.kind === 'trigger')
  913: if (!roots.length) error(null, 'At least one trigger is required', ..., 'graph.noTrigger')
  916-918: pure → 'pure.unused' warning; else !scopes.has(id) → 'node.unreachable'
  ```
  Also l.1059 and l.1110-1116 (trigger config rules).

**Shared fixture** `tests/fixtures/visual-graph-validation.json`: a JSON array of 376 cases.
- Keys: `name`, `graph`, `valid` (true when there are no severity-3 diagnostics), `nodeId?`, `inputId?`, `contains?`
  (one error must match all three), `messages?` (every diagnostic message, sorted by ordinal; **absent means none
  expected**), `codes?` (every coded diagnostic, sorted), `warning?`, `warningInputId?`, `noDiagnostics?`,
  `services?` (attach `tests/fixtures/visual-services.json`), `catalogPatch?` (deep-merged into the built-in catalog),
  `maxValidationMs?`.
- One existing case named `"code graph.noTrigger: no trigger"`.
- C# consumer: `tests/Runtime/VisualGraphContractTests.cs` `Run()` (l.6-61, suite `--visual-graph`). It also checks
  every code is in `VisualGraphValidator.Codes` (l.42) and that no message uses jargon (`Jargon` l.142).
  - Helper: `--describe-visual-graph=<cases.json>` (l.68-96) prints the C# messages and codes so new cases can be
    filled in. It honours `deriveSignatures`.
- Frontend consumers: `frontend/tests/visual-contract.test.ts` (`describe('shared visual graph validation contract')`,
  l.40, one `it` per case); `connection-index.test.ts` (l.84 all cases, l.99 cycle/scope cases);
  `custom-types.test.ts:13`, `graph-types-cache.test.ts:16` and `flow-outputs.test.tsx:18`, which pick graphs by case
  name with `validationCase(name)`.

## 4. Generator

`VG`, `public sealed class VisualGenerator(DiagnosticsProvider diagnostics, GeneratedServiceCalls? serviceCalls=null)` (l.30).
- Entry points: `Preview(graph)` (l.134), `Preview(graph, sources)` (l.135), and
  `public VisualPreview Preview(JsonObject graph, VisualCatalogSnapshot snapshot)` (l.273-433).
  `VisualPreview(bool Valid, string Source, List<VisualSourceMap> SourceMap, List<VisualDiagnostic> Diagnostics){ Graph?, EmbeddedCatalog? }` (l.17-26).
  `VisualSourceMap(string NodeId, int StartLine, int EndLine)` (l.16): the generated line range that belongs to each
  node, `"$library:<id>"` or a custom node. It is used to map compiler errors back to nodes (l.429).
- Pipeline: library checks (l.276-279), `Buildable` (l.283) → `WithCustom` (l.290) → signature staleness (l.293-303) →
  `Validate(..., out inputTypes)`, returning on error (l.304) → comparison types (l.314-319), conversions (l.320-326) →
  **roots** (l.327) → emit (l.332-410) → Roslyn diagnostics (l.411-430) → return with `EmbeddedCatalog` (l.431).
- **The generator is not table-driven per node.** Built-in nodes are not emitted at all. The whole graph JSON and a
  pruned catalog are embedded as string literals, and `VisualRuntime` interprets them. Only nodes with a compiled
  method (library kinds via `snapshot.Methods[kind]`, Custom C# via `snapshot.Custom[id]`) or a user-defined
  conversion get a `case` in the generated `switch` (l.349-403). Data edges are never emitted; the runtime resolves them.
- Trigger assumption and per-trigger code:
  ```csharp
  327: var roots=nodes.Where(n=>Text(n,"kind")=="trigger").OrderBy(n=>Text(n,"id"),StringComparer.Ordinal).ToArray();
  334-346: per root: kind=TriggerType(root); entity → Entities:[new EntityReference(cfg.entity)]; daily/once → Schedules; ha → Triggers;
           startup=cfg.startup ?? kind=="startup"; manual=cfg.manual ?? true;
           Section(id, $"new global::HassSharp.AutomationAction(nameof({EffectiveTriggerMethod(root)}),global::HassSharp.AutomationMode.{cfg.mode ?? "Single"},Startup:..,Manual:..,Entities:..,Schedules:..,Triggers:..)")
  356: foreach root: $"public global::System.Threading.Tasks.Task {EffectiveTriggerMethod(root)}() => {run}({Q(id)});"
  ```
  `EmbeddedCatalog` also tests `kinds.Contains("trigger")` to decide whether to keep `triggerOutputs` (l.109, l.125).
- Shape of the generated source (no golden example in the repo; from l.332-410): `#nullable enable`;
  `public class VisualAutomation : global::HassSharp.Automation { Configure() => new(<one AutomationAction per root>); `
  `public Task Run_<hex>() => __visual_run("<id>");` per root; `__visual_run(id) => new global::HassSharp.VisualRuntime(`
  `Pieces(graph JSON), Pieces(embedded catalog JSON), {type id → typeof}, TriggerData, CancellationToken, __visual_node,`
  `conversions, comparison types, __visual_json, EntityHandles.Create).RunAsync(id);` then `__visual_node` with a `switch`
  of library/custom cases and `default: throw ... "Unknown compiled node"` (l.404); then the library and custom sources.
  - `Pieces` (l.44-55) splits long strings into 64-character literals.
  - The prefix `__visual_` gets more `x`s if the authored text contains it (l.329).
- `EmbeddedCatalog(graph, snapshot)` (l.98-133) decides which catalog parts reach the source:
  - only the node specs of kinds the graph uses, plus every library kind (l.103);
  - only the types that are reachable (l.104-120);
  - **every other top-level section kept whole** (l.122-126):
    ```csharp
    foreach(var (key,value) in root) {
        if(key=="nodes")catalog[key]=nodes;
        else if(key=="types")catalog[key]=...used...;
        else if(key!="triggerOutputs"||kinds.Contains("trigger"))catalog[key]=value?.DeepClone(); }
    ```
- **Testing "source unchanged":**
  - No golden-source test exists.
  - `tests/Runtime/VisualTests.cs:42` checks determinism only (`preview.Source==generator.Preview(graph).Source`).
  - Revision identity: `UserScript.Revision` = SHA-256 of `CompiledScript.SourceCode` (`UserScriptManager/UserScript.cs:63`);
    `VisualDocument.ActiveSourceHash` is set at `Visual/VisualApi.cs:19`.
  - Startup regenerates and stores every automation's source (`Manager.cs:76-80`, `store.RefreshDerived`), so any
    change in embedded output changes the stored source of every automation.
  - Candidate golden inputs: the fixture graphs plus `VisualTests.Graph()` (`tests/Runtime/VisualTests.cs:25-31`). Hash
    their `Preview(...).Source` at HEAD and pin the hashes.

## 5. Runtime

`VR`:
- Constructor (l.172):
  `public VisualRuntime(string graphJson, string catalogJson, Dictionary<string,Type> types, JsonElement trigger, CancellationToken cancellation, Func<string,VisualScope,VisualRuntime,Task> custom, HashSet<string>? compiledConversions=null, Dictionary<string,string>? comparisonTypes=null, JsonSerializerOptions? json=null, Func<string,Entities.Entity>? entities=null)`.
  It builds `flow`/`data` maps (l.179-185) and, for each non-pure node, **`pureInputs[id]`: its pure upstream nodes,
  depth-first, each after those it reads** (l.186-191).
- Surface used by generated code:
  - `RunAsync(string root)` (l.232-238): `Walk(root, new VisualScope{...}, 0)`.
  - `Input<T>(scope,node,port)` (l.240) and `Input(scope,node,port)` (l.259).
  - `Output(scope,node,value,port="value")` (l.332).
  - `Context(scope,node)` returns a `NodeContext` (l.242).
  - `Flows(scope,node)` returns `VisualFlows` (l.244, class l.671).
  - `static JsonOptions()` (l.201).
  - There are no variables.
- Walk (l.420-468): `Prepare` runs the node's pure inputs first (l.432, l.473-489), then
  `switch(kind)` (l.436-451): trigger, if, condition, read_entity, delay, wait_until, service, log, stop, parallel,
  loops, condition_result, loop_result, and `default: await custom(id, scope, this)`. An unknown built-in kind
  therefore reaches the generated `default: throw`.
- Evaluate, for pure nodes (l.490-520): and/or/xor, not, compare, constant/convert, entity_field, object/array,
  object_create, array_create, last_error, get_field, select_property, and `default: custom`.
- State read: `internal static EntityStateSnapshot ReadEntity(string entity, bool fresh=false)` (l.539-543). It uses
  `UserScriptManager.CurrentTrigger.Value?.State` when the trigger's entity matches, else `PyInterop.Entity(entity)`,
  and throws "Entity '…' is unavailable."
- Service calls: `Service` (l.595-602), `CallService` (l.604) and `Call` (l.609-).
- `NodeContext` (`Visual/NodeContext.cs`) offers `State(id)` (l.44), `CallService` (l.57), `Log` (l.63), `Trigger`
  (l.31) and `RunId` (l.25). The runtime has **no handle to a `DefinitionHost` or its entities.**

**Actions** (`UserScripts/AutomationConfiguration.cs`): l.3 `enum InvocationCause { Startup, Entity, Manual, Schedule, HomeAssistantTrigger }`;
l.5 `record AutomationAction(string Method, AutomationMode Mode = Single, bool Startup = false, bool Manual = true,
EntityReference[]? Entities = null, AutomationSchedule[]? Schedules = null, HomeAssistantTrigger[]? Triggers = null)`; l.9 `EntityReference(string EntityId)`.
- `AutomationMode {Single, Restart, Queued, Parallel}` is at `UserScripts/Automation.cs:3-9`. `Automation` exposes
  `Cause`, `TriggerData` and `CancellationToken` (l.11-20).
- Registration (`UserScriptManager/UserScriptClass.cs`):
  - `Configure()` (l.21-40) requires `Task`/`ValueTask` methods with no parameters.
  - `SubscribeAsync()` (l.42-66): each `action.Entities` goes to `ScriptManager.TrackEntityCall(entityId, this, method)`
    (l.51-52); native triggers and schedules are registered too.
  - `Start()` (l.175-176) runs the `Startup` actions unless the script is disabled.
  - `RunMethod(method, cause=Manual, runId, payload, recorded)` (l.180-192) refuses Manual when `!action.Manual`.
- Entity changes reach runs through `UserScriptManager.RunEntries(entityId, trigger)` (`UserScriptManager.cs:230-246`)
  and then `RunEntry` (l.254-266), which sets `CurrentTrigger` and calls `RunMethod(..., InvocationCause.Entity, payload)`.
- Visual run metadata: `VisualTrigger(string NodeId, string? Label, bool TrackHistory = true)`
  (`UserScriptManager/CompiledUserScript.cs:49`), stored in `CompiledUserScript.VisualTriggers` (l.18).

**`ScriptSyncRunner`** (`UserScriptManager/ScriptSyncRunner.cs`):
- `public Task Run(UserScriptClass, MethodInfo, AutomationMode, InvocationCause, JsonElement payload, string? runId=null, DateTime? plannedUtc=null, TaskCompletionSource? recorded=null)` (l.70-75) calls `RunCore` (l.77-83).
- **`async Task RunRecorded(...)`** (l.85-184) is private and returns no value. It builds an `InvocationRecord`
  (l.89-97). History is on for Manual or when `trigger?.TrackHistory != false` (l.100). A trace snapshot is kept only
  when the compiled script has `VisualId`, `VisualRevision`, `VisualGraph` and `VisualCatalog` (l.101-104).
  - **The method's failure is swallowed inside `Task.Run`**:
    `catch (Exception ex) { record.Status = "Failed"; record.Error = ex.GetBaseException().Message; Logger.Error(...); }` (l.152-153).
    Only a failure before execution (writing the first record) is rethrown (l.157-163).
  - A Single-mode collision returns early with status "Skipped" (l.113-122, l.218-224).
- Callers:
  - `RunRecorded`: only `RunCore` (l.80).
  - `Run`: `UserScriptClass.RunNative` (l.128), `ClaimAndRun` (l.165) and `RunMethod` (l.191).
  - `RunMethod`: `UserScriptClass.Start` (l.176), `UserScriptManager.DispatchManual` (l.24) and `RunEntry` (l.260).
- Restart (l.240-250) cancels earlier active runs once the new run's first record is written.

## 6. Visual API

`Visual/VisualApi.cs`, `VisualRequest(json)` (l.91-171), under `_visualGate`:
- **preview** (l.110-114): `VisualGenerator.Preview(graph, authoring)` plus `EntityWarnings`.
- **save** (l.141-148): `store.Save` (`VisualStore.cs:79-97`: `FillMissingLayout`, `Preview`, throws
  `VisualValidationException` when invalid, writes `VisualDocument`), then
  `ActivateVisualDocument` (l.26-35) → `CompileVisualDocument` (l.57-75) → `LoadOrReplace` (l.41-52: `LoadUserScript(staged)`+`ConfigureAndStart`, or `UpdateUserScript`).
- Trigger assumptions:
  - l.60: `compiled.DesignatedRootType = "VisualAutomation"`.
  - **l.68-71**: `VisualTriggers = Graph.nodes.Where(kind == "trigger").ToDictionary(EffectiveTriggerMethod, n => new VisualTrigger(id, label, config.trackHistory ?? true))`.
  - l.81 (`DispatchVisual`, Run now, needs `kind=="trigger"` and `config.manual`).
  - l.229 (`UnknownEntities`, trigger entity checks).
  - l.296 (`Outline`, trigger id and config).
  - The notes' ":288" is now Outline at l.285-305.
- The "operand walker" is `UnknownEntities(graph, snapshot, lookup)` (l.195-254). Its loop at **l.226-251** visits
  every node: trigger entity and native `entity_id`s (l.229-238), then every `DataInputs` port whose operand is a
  **literal** of an `entity_id`/`target`/entity-list type (l.239-251), and warns `entity.unknown` (l.213).
  - It does **not** walk upstream, and it does not distinguish pure from non-pure nodes. It is advice only, for preview
    and save.
  - Upstream walkers that do exist: `PureSources` (`VGV:450-464`, `ct:830-841`) returns the *step outputs* a pure
    node reads; `VR` `pureInputs` (l.186-191) returns the pure node ids. Neither collects entity ids.

## 7. Phase 1 definition code

- **Document/store** (`Devices/EntityDefinition.cs`):
  - `DefinitionSetting(string Key, JsonNode? Value)` (l.28). **It has no label, type or secret.**
  - `DefinitionLogic(string Kind, string? Type, JsonObject? Graph)` (l.34).
  - `DefinitionBody(Name, DefinitionDevice? Device, List<DefinitionSetting>? Settings, JsonArray? Entities, DefinitionLogic Logic)` (l.42-43).
  - `EntityDefinitionDocument(int Version, string Id, long Revision, string Name, DefinitionDevice? Device, List<DefinitionSetting> Settings, JsonArray? Entities, DefinitionLogic Logic, string Source, List<VisualSourceMap> SourceMap){Enabled, SavedUtc, CatalogGeneration; [JsonIgnore] Body}` (l.49-60).
  - `EntityDefinitionStore(directory, DefinitionGenerator)` (l.73) has `Save(id, body, expectedRevision, snapshot, generation, enabled?)`
    (l.160-181: `generator.Preview(body, catalog)`, throws `DefinitionValidationException(preview)`), `Rebuilt` (l.188),
    `RefreshDerived` (l.200), `SetEnabled` (l.214) and `Delete` (l.230). Files go through `VisualStore.Json` (camelCase).
- **Refusal of `graph`**: `Devices/DefinitionGenerator.cs`, `Preview(DefinitionBody body, VisualCatalogSnapshot snapshot)` (l.28-52):
  ```csharp
  35: if (kind is not ("class" or "graph")) Error("definition.logic", "Logic is a graph or a class");
  36: if (kind == "graph") Error("definition.graphUnsupported", "Graph definitions are not available yet");
  38: if (kind == "class" && body.Entities is { Count: > 0 }) Error("definition.entitiesNotAllowed", ...);
  ```
  - It returns `DefinitionPreview(bool Valid, List<VisualDiagnostic> Diagnostics, string Source, EntityTypeDescriptor? Type){ SourceMap }`
    (l.12-16). Diagnostics have `NodeId=null`, `Code="definition.*"`, and `InputId=<setting key>` for setting problems
    (l.31). Other codes: definition.name, definition.library, definition.unknownType, definition.unknownSetting,
    definition.settingType, definition.duplicateSetting.
  - There is no `Graph` or `EmbeddedCatalog` field.
  - `Generate` (l.98-112) emits `public static class SharpDefinition { public static object Create() => new <Type>(); }` plus the library sections.
- **Host** (`Devices/DefinitionHost.cs`):
  - `CompiledDefinition(string Id, string Name, DefinitionDevice? Device, IReadOnlyList<DefinitionSetting> Settings){ internal Func<object>? Factory }` (l.15-19).
  - `internal sealed class DefinitionHost(UserScript script, CompiledDefinition definition) : IComponentHost` (l.64).
  - `PrepareAsync(lifetime)` (l.116-165):
    1. calls `Factory ?? CreateCompiled` (l.120; `CreateCompiled` l.523-529 reflects `SharpDefinition.Create()`);
    2. requires `SharpDevice` or `SharpEntity`;
    3. `ApplySettings` (l.123, 567-579: reflection on `[Setting]` properties, **throws "has no setting X"** for an
       unknown key);
    4. runs `Setup()` on the device, then on each entity, within `SetupTimeout` 60 s (l.70);
    5. seals the device and runs `CheckKeys`;
    6. builds the sync JSON `{definitionId, name, device: DeviceJson(), entities: [entity.Describe(uniqueId)]}` (l.152-158);
    7. calls `PyInterop.SyncEntitiesAsync` within `SyncTimeout` 30 s (l.72, 159-161) and fills `Entities` (`LiveEntity`, l.27).
  - `Start()` (l.190-202) registers in `Manager.Entities` and runs `StartAsync` on `Task.Run` (l.204-234):
    `Start()` on the device, then each entity; `status = Running`; then `Publish` each entity. `StartWarning` is 10 s (l.74).
  - `Publish(entity)` (l.402-415) **returns unless `status` is Running or Failed**, then calls `Manager.Pump.Post(uniqueId, entity.Snapshot(id, running))`.
  - `Command(entity, name, data)` (l.378-393) throws `InvalidOperationException("The entity is not running")`
    unless `status == Running`, then awaits `entity.Command(name, data)`.
  - `Every` is at l.418 and `Track` at l.443 (uses `DependencyTracking.TrackDelegate`). `StopAsync` (l.253-332) is
    bounded by `UserScript.RunDrainTimeout`.
  - `IComponentHost` (`Devices/SharpComponent.cs:11-28`): `Stopping`, `Calls`, `Publish`, `Every`, `Track`, `Log`.
- **Entity description and snapshot JSON**: `SharpEntity.Describe(uniqueId)` (`Devices/SharpEntity.cs:99-111`:
  uniqueId, key, domain, name, icon, deviceClass, entityCategory, enabledByDefault, `commands[]`, `options{}`) and
  `Snapshot(uniqueId, running)` (l.114-122: `{uniqueId, available, values: Values(), attributes}`). The constructor
  `SharpEntity(string key, string? name)` (l.37) checks `KeyPattern` `^[a-z][a-z0-9_]{0,39}\z` (l.181).
  `Name`, `Icon`, `DeviceClass`, `Category` and `EnabledByDefault` are `init` (l.48-56). `Available` publishes (l.59-63).
- **API** (`Devices/EntityApi.cs`, `EntityRequest(json)` l.327-419):
  - Operations: types, preview (`DefinitionGenerator.Preview`, l.342), list, read, set_enabled (l.354-378), save
    (l.382-386: `store.Save` then `ActivateDefinition`), and delete (l.387-403).
  - `CompileDefinition(document, services)` (l.48-56) uses `_compiler.CompileSingleFile(EntityRuntimePath(id), document.Source, services)`.
    The path is `<storage>/entities/entity_<id>.cs` (l.15). It sets **`DesignatedRootType = "SharpDefinition"`** and
    `Definition = new(Id, Name, Device, Settings)`, but not `VisualId`, `VisualTriggers` or `VisualGraph`.
  - `ActivateDefinition` (l.66-70) → `Activate` (l.73-88) → `LoadDefinition` (l.95-108) → `LoadOrReplace` (`VisualApi.cs:41`).
  - `Regenerated` (l.112-117) and `CheckDefinitions` (l.263-280, library save) both call `new DefinitionGenerator().Preview(...)`.
  - `EntityCommand(uniqueId, command, dataJson)` (l.430-436) calls `host.Command`. Python wraps any failure as
    `HomeAssistantError` (`entity_bridge.py:110-117`).
- **Loading** (`UserScriptManager/UserScriptManager.cs`, `LoadUserScript`, l.151-183): classes are `Automation` types
  filtered by `DesignatedRootType == null || t.FullName == DesignatedRootType` (l.164-168); l.171:
  `if (compiledScript.Definition is { } definition) userScript.Host = new DefinitionHost(userScript, definition);`
  A definition script is one whose `CompiledScript.Definition` is not null (`CompiledUserScript.cs:20`). Because
  `"SharpDefinition"` is not an `Automation`, a class definition has **no classes**.
  - `UserScript.PrepareCoreAsync` (`UserScript.cs:92-106`) runs `Configure` and `SubscribeAsync` on the classes
    **before** `Host.PrepareAsync`.
  - `UserScript.Start` (l.108-116) runs the classes' `Start()` (startup runs) **before** `Host?.Start()`.
  - Clear: `ClearCoreAsync` (l.139-161) calls `Host.StopAsync` after the runner clear.
- **Pump** (`Devices/EntityStatePump.cs`): `EntityStatePump(TimeSpan? debounce)` (l.11, default 25 ms, l.13) with
  `Post(uniqueId, snapshot)` (l.29), `Forget(ids)` (l.43), `FlushAsync()` (l.53) and `Reset()` (l.90). There is one
  per manager (`UserScriptManager.Pump`, l.57).
- **Seams for a graph-backed definition:**
  - `DefinitionGenerator.Preview` l.36 (refusal) and `Generate` l.98 (class only).
  - `EntityApi.CompileDefinition` l.51 (root type) and l.54 (no visual metadata).
  - `DefinitionHost.PrepareAsync` l.120-123: the factory, and `ApplySettings` assumes a class.
  - `CheckDefinitions`/`Regenerated` use `DefinitionGenerator` only.
  - `UserScriptManager.LoadUserScript` l.164-171: one root-type filter. A graph needs `VisualAutomation` classes **and**
    a `Host`.

## 8. Entity domains today

| domain | C# class (`Devices/SharpEntities.cs`) | state values (`Values()`) | commands | options (`DomainOptions()`) | Python platform |
|---|---|---|---|---|---|
| binary_sensor | `SharpBinarySensor` l.7-27, `bool? IsOn` | `{"is_on": bool?}` l.26 | none | none | `binary_sensor.py:8-10` `_attr_is_on = values["is_on"]` |
| sensor | `SharpSensor` l.30-66, `object? Value`, `Unit`, `StateClass`, `Options` | `{"value": ValueJson(Value)}` l.59 | none | `{unit, stateClass, options[]}` l.60-65 | `sensor.py:9-17` native_unit, state_class, options, native_value |
| switch | `SharpSwitch` l.72-110, `bool? IsOn`, `OnTurnOn`/`OnTurnOff` (init `Func<Task>`) | `{"is_on": bool?}` l.109 | `["turn_on","turn_off"]` l.74, 102-108 | none | `switch.py:9-17` `async_turn_on/off → self.command("turn_on"/"turn_off")` |

- `SharpDomains` (l.113-142) maps types to domains (`Typed` l.116-121, `Of(Type)`, `OfName(string)`).
- Base: `SharpEntity.Domain`, `Commands` and `Command(name, data)` (`SharpEntity.cs:80-87`).
- Python base `entity_base.py`: `apply` (l.25-42: name, icon, deviceClass, entityCategory, enabledByDefault),
  `apply_state` (l.44-48: available, attributes, values) and `command` (l.53-55).
- `const.py:4` has `PLATFORMS = ["binary_sensor", "sensor", "switch"]`.
- Common description fields, valid for every domain: `name`, `icon`, `deviceClass`, `entityCategory` (config/diagnostic), `enabledByDefault`.

## 9. Frontend surface that assumes triggers (sizing)

- `frontend/src/visual/contract.ts:883` (`trigger.incoming`), `:912-913` (roots/`graph.noTrigger`), `:782` (doc
  comment), `:1059` and `:1110-1116` (trigger config only), `:326` (`dynamicOutputs==='trigger'`, fine).
- `flow/flowModel.ts:212` (`unreachable` excludes triggers), `:245` (execution input pin hidden for trigger or pure);
  `flow/autoConnect.ts:66` (no auto-connect for a trigger), `:97` (`running()` seeded from triggers);
  `layout.ts:102` (`lead` prefers triggers), `:106` (`hasTrigger` sorts groups); `graph.ts:70` (`visualRunClasses`,
  trigger methods only), `:78` (`visualActionLabels`), `:91-101` (`createGraph` starts with trigger, condition, service),
  `:139` (`canConnect` refuses an execution wire into a trigger).
- `flow/FlowAdapter.tsx:580`: trace focus prefers a trigger. `visual/FlowAdapter.tsx` is a 3-line re-export.
- Also: `Inspector.tsx:218` (overview lists triggers), `RunNow.tsx:30`, `NodeConfig.tsx:179,199,241,264,328`,
  `flow/NodeCard.tsx:374` (CSS), `icons.ts:49`, `QuickAdd.tsx:48`, `NodeLibraryPanel.tsx:43`, `connectionIndex.ts:281`.
- Frontend tests that consume the fixture: listed in §3.

## 10. Tests to model new ones on (`tests/Runtime/`)

- `Program.cs`: discovers `[RuntimeSuite(flag, Order, RunByDefault)]` methods (`RuntimeSuiteAttribute.cs`).
  - With no args it runs every `RunByDefault` suite ordered by `Order`, then by flag (l.8-13).
  - Flags select suites, and `--flag=value` passes the value to a one-string-parameter method (l.30-36).
  - It refuses unknown flags (l.15-19) and installs default fakes: `PyInterop.Entity` returns State "fresh", and the
    sync, remove, prune and push delegates succeed (l.21-29).
- `VisualExecutionTests.cs` (`--visual-execution`) compiles and runs graphs in-process.
  - Helpers: `N(id,kind,cfg)`, `E(src,tgt,port)`, `D(src,tgt,targetPort,sourcePort="value")`, `Lit(type,value)`,
    `G(nodes…)` (adds a manual trigger `t` and chains the steps), `Derived(graph)`, `Code(...)`, `Diagnostics`, `Check`.
  - `Execute(graph, snapshot, cancel)` (l.41-54) previews, compiles with `CodeCompiler`, and invokes `Run_74`.
- `PureNodeTests.cs` (`--pure-nodes`): pure nodes worked out per reader; `Logged(graph)` returns Log output.
- `TriggerOutputTests.cs:280-332`: the best **end-to-end model**. It builds a graph, runs `Preview` and
  `CodeCompiler.CompileSingleFile`, loads it with `UserScriptManager.LoadUserScripts`, awaits `StartupCompletion`,
  then drives `manager.RunEntries("sensor.test", new(state))`, schedules, native triggers and `RunMethod`.
- `VisualTests.cs` (`--visual`): store revisions, `node.unreachable` (l.51-62), a manager `VisualRequest`
  save/Init/delete (l.64-70) and `Loaded(api,id)` (l.33).
- `VisualGraphContractTests.cs`: §3. `ContractGuardTests.cs`: label and preview guards.
- `AutomationHomeTests.cs`, `LifecycleTests.cs` and `HistoryDispatchTests.cs`: manager-level automation lifecycle and history.
- Entity definitions:
  - `EntityTestHome.cs`: temp configuration plus recording fakes (sync, push, remove, prune, log): `Syncs(id)`,
    `Pushes()`, `Snapshots()`, `Logs()`, `Until(cond, what)`, `Commands()`/`ClearCommands()` (`NodeLib.FakePlug.Calls`).
  - `EntityHostTests.cs` (`--entity-host`, header l.7-14): `Compiled(id,name,factory,device,settings)` (l.63) sets
    `Definition = new(...){ Factory = factory }`; covers setup order, sync JSON, publish, hot replace, commands, timers, tracks.
  - `EntityApiTests.cs` (`--entity-api`): `EntityRequest` over a manager; library `FakePlug`/`BrokenPlug`/`StubbornPlug`
    (l.16-44), `Definition(name, host, type)` (l.60). `EntityStoreTests.cs` (`--entity-store`): `Body` l.33, `Setting` l.37,
    `Codes` l.39. Also `--entity-startup`, `--entity-pump`, `--entity-types`, `--sharp-entities`.
  - `EntityGeneratorTests`, `EntityHandleTests`, `EntitySchemaTests` cover generated entity handles, not definitions.
- Python.NET smoke: `tests/packaging/entity_bridge_smoke.py`.

## 11. Where the design does not fit the code as it stands

1. **Adding `entityDomains` at catalog top level changes every automation's generated source.** `EmbeddedCatalog`
   copies every top-level section except nodes, types and (without a trigger) triggerOutputs (`VG:122-126`).
   Startup then rewrites every stored source (`Manager.cs:76-80`), and the revision hash moves (`UserScript.cs:63`).
   It needs an explicit exclusion, for example kept only when the graph uses an entity node.
2. **Adding `root: true` (or `documents`/`singleton`) to the existing `trigger` spec also changes the embedded
   source.** Used node specs are embedded whole (`VG:103`). "Is a root" should be derived for `trigger`, or stripped
   when embedding.
3. **Both validators require a trigger and reachability from one** (`VGV:516-522`, `ct:912-918`), so `entity_state` must
   be a non-pure root. Were it pure, nothing would schedule it (the runtime prepares pure nodes only as a step's inputs,
   `VR:186-191,432`) and it would get `pure.unused`. The fixture asserts `graph.noTrigger` ("code graph.noTrigger: no trigger").
4. **The validator has no document-kind input** (`VGV:10`, `ct:785`). `documents: ["entity"]` needs data in the graph
   (such as `graph.definition`) or a new parameter.
5. **Generator roots and actions are trigger-specific** (`VG:327`, `VG:334-346`: `TriggerType`, `cfg.mode`, `startup`,
   `manual`; entities only from `cfg.entity`). No dependency walker exists (§6). The pieces exist, though:
   `AutomationAction` has `Startup` and `Mode` (`AutomationConfiguration.cs:5`) and `RunAsync(root)` walks from any id (`VR:232-234`).
6. **Built-in behaviour lives in `VisualRuntime`'s switches.** New kinds need `Walk` (`VR:436-451`) and `Evaluate`
   (`VR:493-519`) cases, else they reach the generated `default: throw "Unknown compiled node"` (`VG:404`).
7. **The runtime cannot reach the definition's entities.** Neither `VisualRuntime` (`VR:172`) nor `NodeContext` has a host;
   setting entity state needs a new channel (constructor argument, `InvocationContext` field `InvocationContext.cs:9`, or the `Automation`).
8. **`RunRecorded` returns nothing and swallows the run's failure** (`ScriptSyncRunner.cs:85,152-153`); a Single collision
   "Skipped" also completes normally (l.113-122). `InvocationCause` has no Command/Setup member (`AutomationConfiguration.cs:3`),
   and `RunMethod` builds payloads only for Manual and Startup, throwing otherwise (`UserScriptClass.cs:185-190`).
9. **History is on by default for unlisted methods** (`ScriptSyncRunner.cs:100`: `trigger?.TrackHistory != false`). Reactive
   runs need a `VisualTrigger` with `TrackHistory=false`, but `VisualTriggers` come only from triggers (`VisualApi.cs:68`).
   Traces also need `VisualId`/`VisualGraph`/`VisualCatalog`, which `CompileDefinition` does not set (`EntityApi.cs:48-56`).
10. **No script has both a host and graph classes today.** `DesignatedRootType="SharpDefinition"` (`EntityApi.cs:51`) filters
    out every `Automation` (`UserScriptManager.cs:164-168`). Without `SharpDefinition.Create()` the host needs
    `CompiledDefinition.Factory`, internal and described as a test seam (`DefinitionHost.cs:17-18,120,523-529`).
11. **Settings have no schema.** `DefinitionSetting(Key, Value)` (`EntityDefinition.cs:28`) cannot hold `{key,label,type,value,secret}`
    (System.Text.Json drops unknown properties). `ApplySettings` reflects `[Setting]` properties and throws for unknown keys
    (`DefinitionHost.cs:567-579`), so graph definitions must skip it.
12. **Ordering.** Classes subscribe before `Host.PrepareAsync` (`UserScript.cs:92-106`) and startup actions start before
    `Host.Start()` (`UserScript.cs:113-114`). `Publish` is dropped until `status` is Running (`DefinitionHost.cs:216,408`); an
    early startup run's value survives only if stored on the entity, since `StartAsync` publishes all at the end (l.233).
    `Command` refuses unless Running (l.384).
13. **The definition preview carries no graph.** `DefinitionPreview` (`DefinitionGenerator.cs:12-16`) has no `Graph` or
    `EmbeddedCatalog`, diagnostics use `NodeId=null`, and its `Type` is an `EntityTypeDescriptor?`. `Regenerated`,
    `CheckDefinitions` (`EntityApi.cs:112-117,263-280`) and `EntityDefinitionStore.Save` (`EntityDefinition.cs:169`) all
    call `DefinitionGenerator`, so all three need the graph path.
14. **TS types are closed**: `Graph` (`ct:12`) has no `definition`, `VisualCatalogSnapshot` (`ct:56`) no `entityDomains`,
    `NodeSpec` (`ct:34`) no `root`/`documents`/`singleton`.
15. **No pure node reads Home Assistant state yet**; `entity_is` would be the first. `ReadEntity` (`VR:539-543`) prefers
    `CurrentTrigger`'s state for the triggering entity, set by `RunEntry` (`UserScriptManager.cs:257`), which suits a
    reactive recompute. Static extraction sees only literal entity operands, not wired ids or reads inside C#.
16. **New catalog text must pass `--catalog-wording`** (`VisualGraphContractTests.cs:135-137`), which bans "snapshot",
    "boolean", "string" and "typed". New diagnostic codes must be added to both `Codes` arrays (`VGV:32-40`,
    `ct:714-721`), or the fixture test fails at `VisualGraphContractTests.cs:42`.
17. **Run now requires a trigger.** `DispatchVisual` (`VisualApi.cs:81`), `graph.ts:70` and `RunNow.tsx:30` only
    support trigger nodes. Commands and setup roots have no manual run path.
18. Line hints checked: `VGV:510,516`, `ct:883,912`, `VG:327`, `VisualApi.cs:60`, `VNC:70` and `ct:210` are still
    accurate. The trigger dictionary is `VisualApi.cs:68`; Outline is `:285-305` (trigger case `:296`); the walker is
    `:226-251`. `VisualGraphContract.cs:27-38` (`FillMissingLayout`) keeps unknown keys only because it clones a `JsonObject`.

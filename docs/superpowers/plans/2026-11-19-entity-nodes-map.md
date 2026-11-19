# Entity nodes: code map

What the code looks like before the redesign and what each phase touches. Written from a read of the tree at
`c7e789e`; line numbers are that tree's and drift. Spec: `docs/superpowers/specs/2026-11-19-entity-nodes-design.md`.
Each phase's plan is written against this map and the files themselves.

`CC/` is `custom_components/hass_sharp/`. `FE/` is `CC/frontend/`.

## What the design had to bend to

1. **Hub command outputs are not scope ports.** `scope.execution` (`CC/HassSharp/Visual/VisualGraphValidator.cs`
   l.716-727) would stop two command flows sharing a later step, which two `entity_command` roots can do today, and
   would use one of the 8 nesting levels. Keep today's semantics: per-flow outputs absent on other flows
   (`VisualRuntime.Read` l.434-449, `AbsentCommandValue`), now keyed by (node, port). One static rule, `scope.flow`:
   a command output may only be read by steps reachable solely from that hub port. The `scope.` prefix makes the
   editor block the wire (`blockingDiagnostic`, `FE/src/visual/contract.ts` l.1006). For card layout each command
   output carries a layout-only `group: "<port>"`; `buildNodeView` (`FE/src/visual/flow/flowModel.ts` l.299-304)
   places outputs where `scope === port || group === port`.
2. **Device and C# nodes are not `pure`.** The runtime constructor registers every data-edge source as a pure input
   of its target (`VisualRuntime.cs` l.172-193); preparing the hub would evaluate the device node and throw "Unknown
   compiled node" (l.691). New spec flag `declaration: true`: not a step, not a value, no reachability or
   `pure.unused` checks, ignored by the generator's node switch (`VisualGenerator.cs` l.394-431). The `device` input
   is `structural: true`; the runtime constructor, `EntityValues` (l.845-855), `StateDependencies.Of` and
   `ValueUpstream` skip structural edges.
3. **`$any` would accept a device wire.** `$any` expands to every catalog type (`VisualNodeCatalog.DataInputs`
   l.411/426; `contract.ts` `dataInputs` l.596-600). The `device` type is `structural: true, listed: false` and
   structural types are left out of `$any` on both sides. "Must come from a device node" then follows from typing.
4. **Pin ids collide with config keys on the hub** (`value`, `options`, `is_on` against `key`, `name`, `options`).
   Use `argumentStorage: "arguments"` as `custom` does (`TryGetInputValue` l.467-481, `setInput` `contract.ts`
   l.643). The unknown-argument check (validator l.918) means a type change must drop stale arguments.
5. **One node, several flows.** `Run_<hex(id)>` (`EffectiveTriggerMethod` l.278, TS l.91) is one method per node.
   New `FlowMethod(node, port)` gives `Run_<hex(id)>_<hex(port)>` for hubs; triggers and `entity_setup` are
   unchanged. `ScriptSyncRunner.cs` l.98-210 and `DefinitionHost.Flow/IsState/StateEnded/FlowFailed` are keyed by
   method name and keep working once `DefinitionRoots` is rebuilt.
6. **C# node keys.** A class deriving a typed entity gets key `main` (`SharpEntity()` → `this("main")`,
   `SharpEntity.cs` l.31), so two C# entity nodes would clash; two C# device nodes of one class clash on their
   entity keys. C# entity: the node carries `config.key`, assigned after construction through a new internal
   `SharpEntity.Adopt(key)`. C# device: unique id `<defId>:<deviceKey>:<entityKey>` (`EntityDirectory.TryGet` splits
   on the first colon only; the state-store prefix `<defId>:` still matches). Hub and C# entity nodes stay
   `<defId>:<key>`.
7. **Setting pins on C# nodes resolve at prepare time**, not by a graph run: `Setup()` runs before any flow
   (`PrepareClass` l.151-206), and `Compose` keeps setting values out of graph and source (`DefinitionGraph.cs`
   l.33-42). A pin takes a literal or a direct wire from a `setting` node (`scope.settingSource`, blocking); a
   `[Setting(Secret = true)]` pin refuses a literal (`setting.secretLiteral`).
8. **The document version is raised, not the graph version.** `graphVersion` 5 is shared with automations.
   `EntityDefinitionStore.Read` requires `Version == 1` (`Complete`). Phase 2 → 2, phase 3 → 3, phase 5 → 4, with
   one message: "saved by an earlier development build; delete it and build it again".
9. **The derived `entities` list is incomplete for C# device nodes**: their entities exist only after the
   constructor runs. Store `entities: null` on that device; lists and cards use `liveEntities`/`registeredEntities`.
   No user class is instantiated at preview.
10. **The class kind outlives "definition = device".** In phase 3 a class definition's device goes through the new
    payload as one implicit device keyed `main`; phase 5 deletes the shim.
11. **Entity name versus node label.** For `entity`, `device` and the C# kinds the card title is `config.name`
    (`nodeTitle` in `flowModel.ts`) and the inspector's header input (`NodeConfig.tsx` l.240-241) edits
    `config.name`; otherwise a card has two names.

## Where things are today

- **Catalog:** `visual_node_catalog.json` (find it with `git ls-files | grep visual_node_catalog`), embedded by
  `CC/HassSharp/HassSharp.csproj:30`, loaded at `CC/HassSharp/Visual/VisualNodeCatalog.cs:11`, imported by
  `FE/src/visual/contract.ts:1`. Entity kinds have `"documents": ["entity"]`. `entityDomains` (ten domains: state
  values, commands with data, options) drives dynamic pins. `edgeKinds`: `execution`, `data`.
- **Entity document marker:** `graph.definition` is an object. C# `VisualNodeCatalog.GraphDocument` (l.47), TS
  `graphDocument` (`contract.ts` l.204).
- **Compose / roots:** `CC/HassSharp/Devices/DefinitionGraph.cs`: `Compose` (l.33-42) injects
  `graph.definition = {entities, settings: [{key, type}]}`; `Roots` (l.53-77) maps setup, commands
  (`"<entityKey>:<command>"`), states and frames to methods.
- **Stored document:** `CC/HassSharp/Devices/EntityDefinition.cs`: `DefinitionDevice` l.22, `DefinitionSetting`
  l.31, `DefinitionLogic` l.58, `DefinitionBody` l.66, store l.102-321, `Save` l.192-234.
- **Document checks:** `CC/HassSharp/Devices/DefinitionGenerator.cs` `Preview` l.43-68, `GenerateGraph` l.74-84,
  class `Generate` l.147-161; `CC/HassSharp/Devices/DefinitionDocument.cs` `Check` l.34-69, `Options` l.100-157.
- **Generator:** `CC/HassSharp/Visual/VisualGenerator.cs` `Preview` l.289-460; every root becomes one
  `AutomationAction` (l.345-373); `RootActions` l.41-46 (`entity_state` Restart + startup +
  `StateDependencies.Of`; `entity_command` Queued; `entity_setup`, `camera_frame` Single); root methods l.383-390.
- **Runtime:** `CC/HassSharp/Visual/VisualRuntime.cs` `Walk` l.615-691 (entity kinds l.629-672), `EntityKinds`
  l.838, `EntityValues` l.845-855; `IDefinitionRuntime`.
- **Host:** `CC/HassSharp/Devices/DefinitionHost.Graph.cs` `PrepareGraph` l.53-107 (entities via
  `SharpDomains.Create`, `SharpEntities.cs:170-200`), `RunFlow` l.276-300, `Flow()` l.629-643;
  `DefinitionHost.cs` `PrepareAsync` l.130-148, `PrepareClass` l.151-206, `Command` l.505-525, `RestoreClass`
  l.709, `CreateCompiled` l.880-886, `CheckKeys` l.888-897, `DeviceJson` l.900-920.
- **API:** `CC/HassSharp/Devices/EntityApi.cs` `CompileDefinition` l.64-95, `TrackHistory` l.103-107,
  `ForgetRetyped` l.307-314, run l.340-355, `types` l.374/383-384. `EntityLifecycle.cs`.
- **Validator:** `CC/HassSharp/Visual/VisualGraphValidator.cs`: `root.incoming` l.576, `graph.noTrigger` l.585,
  `Definition()` l.633-649, graph-wide rules l.658-693, per-node rules l.862-884, unknown argument l.918. TS mirror
  `FE/src/visual/contract.ts` l.1339-1572. Shared cases `tests/fixtures/visual-graph-validation.json` (61 entity
  cases), read by C# and vitest; also `entity-graph-order.json`, `entity-option-problems.json`.
- **Custom C# node:** catalog kind `custom`; `NodeLibraryCompiler.Preview(libs, customSources)` l.95-160, l.474-523,
  `WrapCustomSource` l.36-43; `VisualGenerator.WithCustom` l.190-206, `DeriveSignatures` l.254-283, embed
  l.433-437; `custom_preview` in `NodeLibraryApi.cs` l.154-184; FE `CustomCodeEditor.tsx`,
  `components/Editor/CustomCode.tsx`, `visual/customCode.ts` (`applyCustomCode`, `exampleSource`),
  `VisualEditor.tsx` `editCode`/`applyCode`.
- **Class kind:** `NodeLibraryCompiler.EntityTypes` l.701-802, `SettingType` l.808-819;
  `VisualCatalogSnapshot.cs` l.44-105; `SettingAttribute` (`SharpComponent.cs:51`); `SharpDevice.cs`;
  `DefinitionSettings.cs`.
- **Bridge:** `CC/entity_bridge.py`: `_async_sync` l.342, `_sync_device` l.384-410 (identifiers
  `{(DOMAIN, definition_id)}`), `_via` l.412-423, `_async_sync_entity` l.425-447 (moves an entity between devices
  at l.446-448), `_async_prune` l.473-488, `_purge` l.490-499.
- **MCP and examples:** `CC/llm.py` (prompt l.20, `SEMANTICS["definitions"]` l.30, `DEFINITION` l.99-102,
  `ENTITY_SPECS` l.158-168, `class_kinds` l.312-326, hints l.343-480, `document_catalog` l.520-532,
  `definition_summary` l.549-561, `node_schema` l.588-605); `CC/authoring.py`; `CC/authoring_examples.json`.
- **Frontend entity code:** `FE/src/entities/` (`model.ts`, `adapter.ts`, `definitionEdits.ts`,
  `DefinitionPanel.tsx`, `EntityRows.tsx`, `DeviceForm.tsx`, `SettingRows.tsx`, `NodePickers.tsx`,
  `ClassDefinitionEditor.tsx`, `classSession.ts`, `api.ts`, `DefinitionCard.tsx`); `FE/src/views/`
  (`EntitiesListView.tsx`, `EntityDefinitionView.tsx`). Contract helpers that read the definition:
  `withGraphTypes` (l.383-408), `checkDefinition` (l.1504), `addGraphNode` (l.1599), `definitionEntity`,
  `entityDomain`, `entityCommands`, `entityChoices` (l.261), `cameraKinds` (l.256), `dataInputs` (l.621-628),
  `commandOutputs` (l.569-579), `prefilled` (l.1588-1615).

## Couplings to keep in mind

- **Registry:** `_async_prune` and `_purge` assume a device identifier equals the definition id. `ForgetRetyped`
  and the delete path use the prefix `<defId>:`, which still holds.
- **History and trace:** all of a hub's flows share `TriggerNodeId`; `TriggerLabel` and a new `rootPort`
  (`VisualTrace.cs` l.469, header size l.135; `FE/src/types.ts` l.465) tell them apart. `Manager.cs` l.236 falls
  back to the method name, which is now longer.
- **State ownership:** `stateKeys`, `awaiting`, `Restorable` and `Heir` come from hubs with supplied state inputs.
  State pins are optional: the examples push state with `set_entity_state`.
- **Handover:** `HandOver/Inherit/Carry` match by entity key; C# device entities need the namespaced key or two
  plugs swap state.
- **Naming:** `entity_base.apply` falls back to the definition's name for a nameless standalone entity; the spec
  removes that fallback (`entity.name`).
- **Limits:** each entity and device now costs a node (256) and usually an edge (512).
- **`via {definition}`** means nothing once a definition has several devices: only `{deviceId}` and `{device}`.
- **Clipboard:** a pasted or duplicated hub needs a fresh key (`pasteSteps`), else `definition.duplicateEntity`.

## Reuse

`applyCustomCode`, `exampleSource`, `CustomCodeEditor`; `WrapCustomSource`, `DeriveSignatures`, `SamePins`;
`EntityTypes`/`SettingType` and their messages; `DefinitionSettings.Apply`, `DefinitionGenerator.Fits/Within/Phrase`;
`PrepareClass`'s life cycle; `SharpDomains.Create/OfName/DefaultOptions`, `DefinitionDocument.Options`;
`buildNodeView`, `stepDescription`, `nodeIdentity`, `TypeChip`; the entity move in `_async_sync_entity`; the
shared-fixture pattern.

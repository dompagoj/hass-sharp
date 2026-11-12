# Visual authoring API: envelope1, graph4

Only graph version4 is supported. Old graphs must be recreated; there is no
upgrade UI, migration tool or backward-execution path. The document and API
**envelope** remains version1. The canonical current guides are
[visual authoring](../writing-visual-automations.md),
[custom C# node libraries](../node-libraries.md) and
[administrator MCP](../mcp-authoring.md).

## Shared WebSocket and MCP services

Administrator WebSocket requests use:

```json
{"id":12,"type":"hass_sharp/visual","request":{"operation":"preview","graph":{"version":4,"name":"Manual log","nodes":[{"id":"start","kind":"trigger","config":{"type":"manual"}},{"id":"log","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"Hello"}}}],"edges":[{"id":"flow","source":"start","target":"log","port":"next"}]}}}
```

HA's outer WebSocket result envelope contains either
`{version:1,ok:true,result:...}` or
`{version:1,ok:false,error:{code,detail}}`. Unauthorized transport requests use HA's
`unauthorized`; unexpected bridge failures use `visual_failed` or `library_failed`
with a generic message and server-side logging.

| Visual operation | Request fields | Result |
|---|---|---|
| preview | graph | `{valid,source,sourceMap,diagnostics}` |
| list | none | Complete document array |
| read | id | Complete document |
| save | id, graph, expectedRevision | `{id,saved:true,document,active,activationError}` |
| delete | id, expectedRevision | `{deleted:true}` |
| run | id, triggerId | `{version:1,runId}` |

IDs match `[a-z0-9][a-z0-9_-]{0,79}`. Creation uses expectedRevision0; updates and
deletes require the exact stored revision. Changing graph.name renames a document
without changing its ID/history identity. Errors include `validation` (mapped
preview in detail), `conflict`, `not_found`, `invalid_request` and `storage`.
Warnings (severity2) allow saves; errors (severity3) do not. A corrupt document
can be explicitly deleted with expectedRevision0; that recovery value never
bypasses revision checking for a valid document.

A document contains `{version:1,id,revision,graph,source,sourceMap,automationId}`
and runtime metadata: `active`, `activationError`, `catalogGeneration`,
`activeRevision`, `activeSourceHash`, `currentRevisionActive`. Shared Python
list/read/save responses also include
`triggers:[{triggerId,method,manual,runnable}]`. `active` means some generation
exists; `currentRevisionActive` means its revision and source exactly match the
saved document. A failed replacement can leave an older generation active.
The outer save result's active flag describes the newly saved revision.

The existing editor run command remains
`{id,type:"hass_sharp/run_now",automationId,className,method}`; for visuals,
className is `VisualAutomation`, and method is the advertised stable trigger
method, `Run_` plus uppercase UTF-8 hexadecimal of its node ID. The run operation
used by MCP accepts only document ID and triggerId. Both resolve the saved
visual and check current activation/manual eligibility while holding the managed
authoring gate through dispatch. Ordinary C# run behavior is unchanged.

History remains
`{id,type:"hass_sharp/run_history",automationId,offset?,limit?}` and returns
`{version:1,offset,limit,runs}`. Defaults are offset0/limit50; maximum limit100.
MCP history resolves automationId from the supplied saved visual document ID.

## Graph4 contract

```text
Graph: {version:4,name,nodes,edges,imports?:string,members?:string}
Node: {id,kind,config:object,label?,position?:{x,y},size?:{width,height}}
Execution edge: {id,kind?:"execution",source,target,port?:"next"}
Data edge: {id,kind:"data",source,sourcePort,target,targetPort}
Operand: {kind:"literal",type:<catalog CLR/built-in type ID>,value:<JSON value>}
       | {kind:"variable",name}
       | {kind:"unset"}
```

The actual catalog comes from `NodeLibraryRequest({operation:"catalog"})`, merged
by Roslyn from the packaged built-ins and saved library sources. MCP
get_node_schema exposes this same snapshot. Do not maintain another node/type
list. It includes inputs/outputs, defaults, dynamic ports, scope rules, limits,
CLR assignability, nullability, literal choices, labels and XML documentation.
Library arguments live in `config.arguments`; built-in input values follow their
catalog definitions. Data edges read previously produced values and do not execute
a producer. Control edges schedule nodes. Multiple triggers, shared downstream
paths, structured branches/loops/error scopes and rich CLR values are supported
under the shared validator's rules. Complete compile-checked examples are in
[`authoring_examples.json`](../../custom_components/hass_sharp/authoring_examples.json).

Layout is retained; the existing managed store deterministically fills only
missing position/size. Generated source is server-authoritative. Imports and
members diagnostics use synthetic IDs `$imports` and `$members`. Source maps are
`{nodeId,startLine,endLine}` (inclusive, one-based); diagnostics include
`{nodeId,message,severity,startLine?,startColumn?,inputId?,portId?}`. Multiple map
ranges may belong to a node. HA native diagnostics identify the trigger input.

## Library and language-service API

Use `{id,type:"hass_sharp/node_library",request:{operation,...}}`. Its envelope
and administrator checks match visual authoring. Saved library documents are
`{version:1,id,name,revision,source}`; source is namespace contents wrapped in
`NodeLib`, not an outer file-scoped namespace.

| Library operation | Request fields | Result |
|---|---|---|
| catalog | none | `{catalog,generation,saved:true,valid,diagnostics}` |
| list | none | `{documents,generation}` |
| read | id | Document |
| save | id, name, source, expectedRevision | `{saved:true,document,deleted:false,generation,catalog,automations}` |
| delete | id, expectedRevision | `{saved:true,document:null,deleted:true,generation,catalog,automations}` |

`automations` entries are `{id,saved:true,document,active,activationError}`. Every
library mutation validates/regenerates all graphs and advances their revisions
because every graph artifact embeds the complete source workspace. Invalid edits
or referenced deletions are rejected before persistence with
`detail:{diagnostics,automations:[{id,diagnostics}]}`. A successful storage commit
can still report per-graph activation errors.

Draft/editor operations `preview`, `completion`, `hover`, `signature`, `format`,
`definition` accept `{id,source,workspaceVersion,generation?,sources?,position?}`.
Sources is an array of `{id,source}` overlays; selected source wins. Position is a
zero-based UTF-16 offset. Results are
`{id,workspaceVersion,generation,sourceGeneration,saved:false,data}`. Saved
generation is separate from draft sourceGeneration. An expected saved-generation
mismatch conflicts; clients must also discard obsolete workspace/model responses.
Preview data is `{valid,diagnostics,catalog}`. Other data is the normal Roslyn
completion/hover/signature/format/definition result. Navigation returns library
source IDs with UTF-16 ranges; diagnostic line/column positions are one-based.

## Persistence and runtime ownership

Visual documents live in `.storage/hass_sharp/visual`, library sources in
`.storage/hass_sharp/libraries`. The managed authoring gate and revision stores
are authoritative for editor and MCP writers. Visual saves use flushed temporary
files and atomic replacement. Library mutations coordinate source/graph updates
with the existing recoverable undo journal, then activate committed graphs. A
process restart recovers unfinished transactions before authoring and regeneration.
Multiple external writers/processes and power-loss directory durability are not
claimed. Direct file edits bypass conflict checking.

Preview compiles immutable snapshots and validates native HA declarations without
attaching listeners or executing user code. Save persists then activates through
the normal Automation runtime; custom code has administrator privileges and
startup/native/scheduled effects require authorization. Cancellation, concurrency,
schedules and history remain owned by the existing runtime. Compiled ordinary
C# cache keys include loaded runtime/compiler module identities so upgrades cannot
reuse stale constructor signatures; same-build reuse is retained.

## Verification

The runtime harness covers v4 typed execution and scopes, custom CLR libraries,
revision races, recovery, failed activation, atomic manual dispatch, cache build
identity, history and native trigger lifecycle. The editor suite tests real Roslyn
services. Python tests cover shared service parity, immutable snapshots, native
validation, lifecycle cleanup and current HA2026.11 LLM/MCP schemas, permissions,
read-only filtering and registry discovery. Live publication/transport/browser
acceptance is performed separately against the local HA2026.11.1 instance; unit
tests alone do not claim device effects or live-client transport acceptance.

# Visual authoring API: envelope 1, graph 5

Only graph version 5 is supported. Old graphs must be recreated; there is no
upgrade UI, migration tool or backward-execution path. The document and API
**envelope** remains version 1. The canonical current guides are
[visual authoring](../writing-visual-automations.md),
[custom C# node libraries](../node-libraries.md) and
[administrator MCP](../mcp-authoring.md).

## Shared WebSocket and MCP services

Administrator WebSocket requests use:

```json
{"id":12,"type":"hass_sharp/visual","request":{"operation":"preview","graph":{"version":5,"name":"Manual log","nodes":[{"id":"start","kind":"trigger","config":{"type":"manual"}},{"id":"log","kind":"log","config":{"value":{"kind":"literal","type":"string","value":"Hello"}}}],"edges":[{"id":"flow","source":"start","target":"log","port":"next"}]}}}
```

HA's outer WebSocket result envelope contains either
`{version:1,ok:true,result:...}` or
`{version:1,ok:false,error:{code,detail}}`. Unauthorized transport requests use HA's
`unauthorized`; unexpected bridge failures use `visual_failed` or `library_failed`
with a generic message and server-side logging.

| Visual operation | Request fields | Result |
|---|---|---|
| preview | graph | `{valid,source,sourceMap,diagnostics}` |
| list | none | Document array without `source` and `sourceMap` |
| read | id | Complete document |
| save | id, graph, expectedRevision | `{id,saved:true,document,active,activationError}` |
| delete | id, expectedRevision | `{deleted:true}` |
| run | id, triggerId | `{version:1,runId}` |

Preview and save validate against the registered service schemas (see Call service
below); list, read and run do not validate.

IDs match `[a-z0-9][a-z0-9_-]{0,79}`. Creation uses expectedRevision 0; updates and
deletes require the exact stored revision. Changing graph.name renames a document
without changing its ID/history identity. Errors include `validation` (mapped
preview in detail), `conflict`, `not_found`, `invalid_request` and `storage`.
Warnings (severity 2) allow saves; errors (severity 3) do not. A corrupt document
can be explicitly deleted with expectedRevision 0; that recovery value never
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
authoring gate through dispatch.

Run history is `{id,type:"hass_sharp/run_history",automationId,offset?,limit?,status?}`
and returns run history version 2; `hass_sharp/run_trace {runId}` returns one run's
trace. Both are described in [run history](run-history.md). MCP history resolves
automationId from the supplied saved visual document ID.

## Graph 5 contract

```text
Graph: {version:5,name,nodes,edges}
Node: {id,kind,config:object,label?,position?:{x,y}}
Execution edge: {id,kind?:"execution",source,target,port?:"next"}
Data edge: {id,kind:"data",source,sourcePort,target,targetPort}
Operand: {kind:"literal",type:<type ID>,value:<JSON value>}
       | {kind:"unset"}
Custom C# config: {source:string,signature:Signature,arguments:{<input>:Operand}}
Signature: {title,description,pure:boolean,
            inputs:{<id>:{types:[<type ID>],required,label,description,nullable,hasDefaultValue}},
            outputs:{<id>:{type,label,description}},
            types:{<type ID>:<type entry>}}
```

Removed in graph 5's current form, and refused by both validators with a sentence
saying what to do instead: graph `imports` and `members`, a node's `config.output`
(a value exposed to C# by name), the `variable` operand, a trigger's
`entitiesExpression` and `schedulesExpression`, and Custom C# `config.code`
(statements). An old saved graph that uses them is refused until it is edited.

A Custom C# node's pins come from `config.signature`, which the compiler writes from
the code (`VisualGenerator.DeriveSignatures`; the `deriveSignatures` flag of
preview and save). Pruning a value or a wire of a pin that is gone gives one warning
(severity 2) per value, data wire and order wire, on the node, naming the pin
("Removed the connection to Text: the code no longer has that input"); preview and
save return them in `diagnostics`. `signature.types` holds an entry for every type
the pins use that the static catalog (`visual_node_catalog.json`) does not have:
CLR types such as `global::System.Int32` or `global::System.Collections.Generic.List<global::System.String>`,
library types, and the types the code declares. Each entry is the compiler's, with
`assignableTo` (every type of the generation, the node's other types and each of
the type's own interfaces and base types it converts to implicitly) and
`assignableFrom` (every type of the generation and the node's other types that
convert to it implicitly). Both validators merge those into the catalog
(`WithGraphTypes`/`withGraphTypes`: catalog types first, then each well-formed
signature's types in node order, the first entry for an id winning) and accept a
wire `s → t` when `t` is in `s.assignableTo` or `s` is in `t.assignableFrom`, so
wires between two Custom C# nodes match through types both can name. Any numeric
type goes into any other (`number` and every CLR numeric type), except a nullable
one into a pin that cannot hold null; a value the pin cannot hold fails the step
when it runs, naming the step and pin, and a literal that cannot fit is refused
when validating. A type one Custom C# node declares is its own: another node's
code that names it is refused ("… belongs to the Custom C# node make; declare the
type in this code or in a node library to use it here"); a wire can still carry it
into an `object` pin or a pin of an interface it implements.

A signature that is not well formed is refused by both validators with code
`custom.signature`. The backend compiles the code and compares what the code
decides: the pin ids, each input's type ids, `required`, `nullable`,
`hasDefaultValue` and `default`, each output's type id, and `pure`. When those
differ the node is refused ("The pins are out of date with the code: open Edit code
and apply it"). When they match, the compiled signature is used: titles, labels,
descriptions and type entries are refreshed from it in what preview and save return
and in what the next save stores, and `Manager.Init` loads the automation as it is.
The code compiles in namespace `NodeLib.Custom_<escaped node id>`, beside the
libraries; the escaped id keeps ASCII letters and digits and writes every other
character as `_` and its UTF-16 code in four uppercase hex digits (`kitchen-light`
becomes `kitchen_002Dlight`). Its diagnostics map to the node's `source` input and
to lines of its code. `NodeLibraryRequest` offers `custom_preview`
(`{nodeId,source,sources?}` returns `{valid,diagnostics,signature}`; `nodeId` is
required, since the type ids depend on it, and a library among `sources` with errors
is one diagnostic saying to fix the node library first), and the language
operations take `custom: true` and `nodeId` for a node's unsaved code.

The actual catalog comes from `NodeLibraryRequest({operation:"catalog"})`, merged
by Roslyn from the packaged built-ins and saved library sources. MCP
get_node_schema exposes this same snapshot. Do not maintain another node/type
list. It includes inputs/outputs, defaults, dynamic ports, scope rules, limits,
CLR assignability, nullability, literal choices, labels and XML documentation.
Library arguments live in `config.arguments`; built-in input values follow their
catalog definitions. An input marked `connectable: false` takes only a literal:
both validators refuse a data edge into it as into an input the node does not
have (Constant's `value`, typed `$type` from `config.type`). Data edges read previously produced values and do not execute
a producing step. Control edges schedule steps. Value nodes (catalog `pure: true`:
constant, object, array, object_create, array_create, get_field, entity_field,
convert, and, or, xor, not, compare, last_error, and any `[Node(Pure = true)]`
method) take no execution edges; the runtime works out
the value nodes a step reads, in post-order and in the step's scope, just before
that step runs, counting each against the step limit and recording it in the trace
with `src` set to the value node. A failure is the reading step's failure, named
`<value node> (used by <step>): <message>`. Multiple triggers, shared downstream
paths, structured branches/loops/error scopes and rich CLR values are supported
under the shared validator's rules. Complete compile-checked examples are in
[`authoring_examples.json`](../../custom_components/hass_sharp/authoring_examples.json);
the runtime harness validates each of them with and without the service schemas.

### Types

A type ID is a key of the catalog's `types`, or a parametric ID `<base>:<param>`
built from `parametricTypes`. Today the only family is `entity_id:<domain>`: it
refines `entity_id` with the pattern `^<domain>\.[a-z0-9_]+$`, keeps its base's
CLR type and literal kind, and gets the label `<Domain> entity`. Parametric IDs
need the exact shape `^([a-z_]+):([a-z0-9_]+)$`, because library type IDs contain
colons too. Anything else is unknown.

The refinement chain of a type is the type, the type it `refines`, and so on up to
a root. A source type fits a target type when they are equal; when the target
refines another type, the target must be in the source's chain; otherwise the
source's root must equal the target or convert to it (Roslyn when a library
compilation exists, the root's `assignableTo` otherwise). Refinement is decided
before any CLR check, because built-in types are registered under their CLR
symbols and Roslyn alone would treat `entity_id` as `string`. A literal must
satisfy the pattern, bounds and enum values of every type in its chain. Runtime
CLR lookups use the chain's root. C# (`VisualTypes.cs`) and TypeScript
(`frontend/src/visual/contract.ts`) implement the same rule. Config settings that
choose a type (`enumSource: "types"`, such as Convert's and loop declarations')
accept every resolvable ID, so Convert can narrow a value to `entity_id:light`.

### Trigger outputs

The trigger node declares `dynamicOutputs: "trigger"`. Its data outputs come from
the catalog's top-level `triggerOutputs` table: the `*` row (`data`, the whole
normalised payload) and every row whose `|`-separated key lists the trigger's
`config.type`, or `ha:` plus `config.trigger.trigger` (else `platform`) for native
triggers. Each output has a JSON pointer into the normalised payload. An output
with `refineFrom` names a config path; when that path holds one literal entity ID,
or a list of entity IDs of one domain, the output's type becomes
`entity_id:<domain>`. A pointer that resolves to nothing or to JSON null leaves
that output unproduced, and a consumer fails with an error naming the trigger
output. The payload for each cause is described in
[visual authoring](../writing-visual-automations.md#triggers-and-their-outputs).

A manual dispatch (`run` or `run_now`) builds its payload where the run starts
(`TriggerPayload.Manual` called from the visual dispatch): an `entity` trigger, or
a native `state` or `numeric_state` trigger naming an entity (the first of a
list), gets the payload a real firing would carry, with the entity's current
state (`PyInterop.Entity`, as Read entity) as both `from_state` and `to_state`,
a native trigger's configured `id` and `attribute`, `numeric_state`'s configured
`below`/`above`, and `manual: true` (never `for`, `description` or `context`).
Other triggers, or an entity without a state, give
`{platform: "manual"}`. So does a state that cannot be read or normalised; that is
logged as a warning naming the entity, and the run still starts.
The cause stays `Manual`; the run's trigger summary is `{kind: "manual"}` plus
`entityId` when the payload names one, never `from`/`to`. Generated source is
unchanged: the payload is built outside the generated class.

### Call service

`service` nodes have `config.service` (`domain.service`), `config.fields` (field
name to operand), `config.returnResponse` (boolean) and a `target` input whose
operand lives in `config.target`. Ports never depend on the service schema: one
`field:<name>` input exists per key of `config.fields`, and the `response` output
exists only while `returnResponse` is true. Outputs with `when` follow that rule
generally. A field named `target` (notify services have one) is the port
`field:target` and does not collide with the node's target.

When the services section knows the service, it refines types and adds
diagnostics: field types and literal constraints come from the schema, the target
accepts `target` plus `entity_id:<domain>` for each domain its entity filter
allows (plain `entity_id` when it has no domain filter), and a literal target
object's `entity_id` values must be in those domains (`all` and `none` are
exempt). Unknown fields, missing required fields (present but unset and unwired
counts as missing), a target on a service without one, `returnResponse` on a
service that returns none, and no `returnResponse` on a service that only returns
one are errors. An unknown service is a single warning and its fields accept every
type. Without a services section there is no schema diagnostic, but the target-key
rule below still applies.

Whatever the services section holds, a target value may only contain `entity_id`,
`device_id`, `area_id`, `floor_id` and `label_id`, and a node whose target is set
(a value or a connection) may not also have a field with one of those
names; a node with no target may. At run time a target object with any other key
fails with the same sentence the validators use.

Breaking change: a stored graph that sets a target and also has a field named
`entity_id`, `device_id`, `area_id`, `floor_id` or `label_id`, or whose target
object holds another key, no longer validates; move those ids into the target.

Service schemas are attached only to authoring validation of one graph (visual
preview and save) and to the library `service_ports` operation; the `services`
operation serves them, and `catalog` reports only their `servicesGeneration`. Startup
loading and the rebuild after a library change validate without them, so whether
an automation loads never depends on which integrations had registered services.
The schemas never enter the catalog root, the generated source or the library
generation.

At run time the payload holds every field input that is wired or not unset, by
field name, plus the target: a string becomes `entity_id`, an object's properties
are copied in. A response is requested only when `returnResponse` is true.

## Library and language-service API

Use `{id,type:"hass_sharp/node_library",request:{operation,...}}`. Its envelope
and administrator checks match visual authoring. Saved library documents are
`{version:1,id,name,revision,source}`; source is namespace contents wrapped in
`NodeLib`, not an outer file-scoped namespace.

| Library operation | Request fields | Result |
|---|---|---|
| catalog | none | `{catalog,generation,servicesGeneration,saved:true,valid,diagnostics}` |
| services | none | `{servicesGeneration,items}` |
| service_ports | service | `{service,known,response,hasTarget,inputs,outputs}` |
| list | none | `{documents,generation}` |
| read | id | Document |
| save | id, name, source, expectedRevision | `{saved:true,document,deleted:false,generation,catalog,automations}` |
| delete | id, expectedRevision | `{saved:true,document:null,deleted:true,generation,catalog,automations}` |

### Service schemas

Python sends Home Assistant's service descriptions to the manager at startup and
again two seconds after services stop being registered or removed
(`SetServiceDescriptions`). The manager normalises them into an immutable
`ServiceSchemaCatalog` and swaps it whole, so the `services` operation answers
without waiting for the authoring gate. `items` is keyed by `domain.service`;
each entry has `domain`, `service`, `name`, `description`, `target` (its `entity`
filters by domain, device class and supported features, and its `device` filters,
or `null` when the service takes no target), `response` (`none`, `optional` or `only`) and `fields`. Sections are
flattened into `fields`; each field records its section, its `order` in Home
Assistant's display order, the catalog `types` derived from its selector, and the
literal constraints and widget hints that selector implies (`min`, `max`, `step`,
`unit`, `enum`, `pattern`, `minItems`, `maxItems`, `default`, `example`, the raw
`selector` and its `filter`). Malformed entries are skipped or repaired and
logged, never fatal. `servicesGeneration` is the SHA-256 of the canonical items,
independent of input key order; clients refetch when the catalog operation reports
a new one. Number selectors never set `integer`, because Home Assistant does not
enforce their step.

`service_ports {service}` returns the ports a Call service node gets for one
service, computed by the validator's own port code on a node that holds every
schema field, not set, in Home Assistant's order, with `returnResponse` on when the
service can return a response. `inputs` and `outputs` are those ports, `known`
says whether the service is registered, `response` is its response mode (`null`
when unknown) and `hasTarget` whether it takes a target. An unknown service keeps
only the permissive target. MCP `get_node_schema` passes this result through.

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
schedules and history remain owned by the existing runtime.

## Verification

The runtime harness covers v5 typed execution and scopes, type refinement, trigger
outputs, service schemas and the Call service node, run history and traces, custom CLR libraries,
revision races, recovery, failed activation, atomic manual dispatch, history and native trigger lifecycle. The editor suite tests real Roslyn
services. Python tests cover shared service parity, immutable snapshots, native
validation, lifecycle cleanup and current HA 2026.11 LLM/MCP schemas, permissions,
read-only filtering and registry discovery. Live publication/transport/browser
acceptance is performed separately against the local HA 2026.11.1 instance; unit
tests alone do not claim device effects or live-client transport acceptance.

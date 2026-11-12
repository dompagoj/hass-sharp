# Visual automation API v1

Backend Task 6; frontend integration is a separate change. No existing-script
migration or arbitrary C# → graph conversion is performed.

Send administrator-only Home Assistant WebSocket messages:

```json
{"id":12,"type":"hass_sharp/visual","request":{"operation":"preview","graph":{"version":1,"name":"Motion","nodes":[{"id":"start","kind":"trigger","config":{"entity":"binary_sensor.motion"}},{"id":"on","kind":"service","config":{"service":"light.turn_on","entity":"light.hall"}}],"edges":[{"id":"edge","source":"start","target":"on"}]}}}
```

All results use `{version:1,ok:true,result:...}` or
`{version:1,ok:false,error:{code,detail}}`. HA's outer WebSocket result envelope
still applies. Non-admin access uses HA `unauthorized`; unexpected infrastructure
failures use `visual_failed` and integration logs.

| operation | Request fields | result |
|---|---|---|
| preview | graph | `{valid,source,sourceMap,diagnostics}`; no persistence or execution |
| list | none | Array of complete documents |
| read | id | Document |
| save | id, graph, expectedRevision | `{document,active,activationError}` |
| delete | id, expectedRevision | `{deleted:true}` |

IDs match `[a-z0-9][a-z0-9_-]{0,79}`. Create with expectedRevision=0; updates and
deletes require the revision from read/save. Conflict code is `conflict`; missing
read/delete is `not_found`; invalid graph save is `validation`, with the preview
in `detail`. Invalid request shapes are `invalid_request`. Rename by changing
`graph.name` through save with the same ID/revision. ID and run-history identity
remain stable. IDs should be freshly generated for new documents, not reused after
delete. Code editor file APIs must not be used for visual documents.

Document: `{version:1,id,revision,graph,source,sourceMap,automationId}`. API responses
include the runtime/history `automationId`; use it with existing `run_history` and
`run_now` (className `VisualAutomation`, method `Run`). Visual entries are excluded
from the ordinary code-editor list; merge the visual list in the frontend. Original graph metadata
(label/position/size and additional UI fields) is preserved. Generated source is
server-authoritative; caller-supplied source is never accepted. Each document is
one JSON file under HA `.storage/hass_sharp/visual`; a flushed temporary file and
same-directory atomic replacement commit graph/source together. There is no second
`.cs` file to get out of sync. In-process writers serialize per storage directory.
Multiple HA processes writing the same storage directory are not supported.
Directory-entry durability across sudden power loss is filesystem-dependent.

Save first commits, then attempts runtime activation. `active:false` explicitly
means persistence succeeded but activation failed: display `activationError` and
offer editing/reload. It does not mean the previous runtime has been replaced.
On integration initialization the persisted graph is regenerated against current entity symbols, then compiled and loaded. The saved source remains the last-save artifact. Preview
never executes custom C#. Saving can execute startup code, exactly like code
saving. Custom code has the same trust/access as handwritten automations.

## Graph semantics

The contract extends frontend `src/visual/graph.ts` without changing its required
shape: `{version:1,name,nodes,edges}`; node `config` values remain strings.
Optional edge `port` defaults to `next`. Exactly one trigger, all nodes reachable,
no cycles, at most 256 nodes/512 edges. Only one outgoing edge per port is allowed;
unspecified multi-output fan-out is rejected. Rejoining branches is supported.
Stable node IDs sorted ordinally determine source order, independent of canvas
position and node-array order. A missing outgoing edge ends the invocation.

| kind | config |
|---|---|
| trigger | `type` defaults to `entity`; `entity`; `mode`: Single (default), Restart, Queued, Parallel |
| trigger daily | `type:daily`, `time:HH:mm`, `timeZone`: explicit system timezone ID |
| trigger once | `type:once`, `at`: ISO timestamp with explicit offset |
| trigger manual/startup | `type:manual` or `type:startup` |
| condition | `entity`, `state`; compares RawState; `true` (or `next`) and `false` ports |
| service | `service:domain.name`, optional `entity`, `data`: JSON object string, `response`: dictionary key |
| delay | `seconds`: invariant finite number from 0 to 4,294,967.294 |
| custom | `code`: C# statements in the async action; no preprocessor directives |

Every trigger also supports manual runs. A service with `response` awaits a JSON
response into `responses[config.response]`; other calls await completion. Entity
config overrides data.entity_id. All action nodes except Stop can use an `error` edge; an exception
sets `lastError` and follows it. Unhandled errors reach the ordinary runtime
history. Cancellation is rethrown and never routed to an error handler. Each step
checks cancellation; delay uses the invocation token. Custom sections can access
`responses`, `lastError`, `CancellationToken`, `Cause`, `Entity`, and `Services`.
Local variables declared in one custom node are scoped to that node; use responses
or combine statements for shared data. Custom statements can perform arbitrary
code-authoring operations, but graph-level multiple triggers, parallel branches,
loops and weekly/monthly/cron schedules are not provided. Concurrency modes govern
whole invocations, not branches. These limits must be exposed in the frontend.

`sourceMap` entries are `{nodeId,startLine,endLine}`, inclusive, 1-based. A trigger
can have multiple ranges (declaration and step). Diagnostics are
`{nodeId,message,severity,startLine,startColumn}`; severity matches Roslyn (3 error,
2 warning, 1 info, 0 hidden). Graph diagnostics may have null positions/nodeId.
Warnings do not block save; errors do. Source and graph are returned together for
frontend preview and navigation.

## Verification

`dotnet run --project tests/Runtime --no-restore --disable-build-servers` covers
compiled/executed entity/condition/service, false branch, response/custom use,
service-error recovery, delay and mode configuration, daily/once fixtures,
determinism, node diagnostics, atomic pair persistence/reopen, conflicts, rename
and delete, concurrent writers, generated cancellation, failed activation cleanup
and save/update/delete/reload through the backend bridge. Existing runtime tests exercise policy cancellation/history/scheduling.
`python3 -m unittest discover -s tests/python` checks the WebSocket admin boundary.
The integrated branch was tested in a local Home Assistant instance: an administrator
saved and reloaded a visual graph, dispatched its generated action with Run now,
and saw the failed manual run in history. The sample entity did not exist, so
this did not verify a successful device action. Python.NET SQLite loading was
also checked in the local Alpine container.

## Graph v2

The API and document envelope remain version 1. Set **graph.version** to 2 to use
multiple trigger nodes. Existing v1 graphs keep their single `Run` action and
existing node/edge behavior. New visual drafts use v2. Saved v1 graphs remain v1
until the user explicitly selects **Upgrade to graph v2** in the editor; this
changes their action methods on the next save.

Each v2 trigger becomes an independent action, ordered by ordinal node ID, with
its own mode, subscriptions, schedules, startup/manual flags and history action.
Its public method is `Run_` plus uppercase hexadecimal UTF-8 bytes of its node ID
(for example `a` → `Run_61`). Use that method for run-now. Keep trigger IDs stable:
renaming the display name or reordering nodes preserves action identity; changing
the ID changes it. Source maps include trigger configuration, action entry, and
node body ranges. Multiple ranges may have the same NodeId.

All nodes must be reachable from at least one trigger. Triggers cannot have
incoming edges. Different triggers may share downstream nodes: each invocation
executes that path independently, with separate responses/lastError locals.
Two triggers for the same entity intentionally invoke two actions. Repeated entity
IDs within one action are deduplicated by the existing runtime subscription layer.

Additional optional v2 fields:

| Location | Field | Meaning |
|---|---|---|
| graph | `imports` string | C# using directives before the generated class |
| graph | `members` string | C# fields, properties, helper methods and nested types inside VisualAutomation |
| trigger.config | `entitiesExpression` string | C# expression replacing the Entities argument, e.g. `["sensor.temperature", "input_boolean.enabled"]` |
| trigger.config | `schedulesExpression` string | C# expression replacing Schedules, e.g. `[AutomationSchedule.Daily("morning", new TimeOnly(7, 0), "Europe/Zagreb")]` |
| trigger.config | `startup` boolean | Overrides startup behavior derived from type |
| trigger.config | `manual` boolean | Defaults true; false disables manual invocation |

For fully custom subscriptions use `type:"manual"` plus the expression fields;
the normal entity/daily/once types still validate their required fields before
overrides. Expressions can call helpers defined in members. Preview compiles
expressions but does not evaluate them: runtime-invalid schedules or throwing
helpers are reported by save as activation errors. Import/member compiler errors
map to synthetic IDs `$imports` and `$members`; expression errors map to the
trigger. These synthetic IDs are diagnostic locations, not graph nodes.

### C# escape hatch and execution semantics

A custom node's `config.code` is an async C# statement body. Arbitrary conditions,
expressions, sequential branches, loops, typed service calls and response handling
can live there or in members. For example:

```csharp
if (Entity("sensor.bathroom").RawState == "cold"
    && EntityUntracked("input_boolean.enabled").RawState == "on")
{
    for (var i = 0; i < 2; i++)
    {
        CancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAll(Heat(), Notify()); // concurrent, joined before next node
    }
}
await Finish(); // explicitly sequential
```

Define Heat/Notify/Finish as private async Task helpers in members. Helpers inherit
Cause, TriggeredBy(), CancellationToken and the normal Automation APIs. Pass the
token to delays/I/O and await all child work. Cancellation from Restart/unload
propagates through awaited helpers and bypasses graph error edges. Class fields
are shared across concurrent invocations, so use synchronization for shared state.
Use method locals for invocation-local values. A `return;` in a custom node ends the current dispatcher body; at top level this ends the invocation. Inside Repeat it ends that iteration. Use Stop for whole-invocation termination.

Tracked `Entity(...)` and `EntityUntracked(...)` APIs are available with the same
semantics as handwritten code. The current declarative runtime does **not**
discover subscriptions from either read: declare triggering entities explicitly.
TriggeredBy() is the event snapshot and Cause preserves Entity/Startup/Manual/
Schedule; helper execution does not create separate history records.

Boundaries: graph edges remain acyclic and have one target per port. Version 3 adds structured Repeat loops; custom C# provides concurrent fork/join operations.
Generated class name/base class/action signatures and Configure are reserved;
do not redeclare them in members. This is parity for behavior expressible through
the current Automation runtime, not arbitrary C# source-file round-tripping or
arbitrary inheritance. C# snippets have the same privileges and reference set as
code automations, not a sandbox. Cancellation remains cooperative, just as in
handwritten code. Custom node preprocessor directives remain unsupported.
Do not depend on generated locals such as step or private ExecuteGraph.

Verification includes a compiled bathroom-heating-style fixture (tracked and
untracked reads, event cause, shared action paths, loops and awaited concurrent
helpers), duplicated triggers, deterministic output, member diagnostics, and
generated Restart/Parallel executions cancelled on unload. This is a representative
fixture, not a claim of testing a particular installation's devices or script.

Runtime status: list/read document results include `active` and, when inactive or a
replacement failed, `activationError`. These are runtime metadata, not graph data.
A corrupt stored JSON document may be removed with `delete`, its ID and
`expectedRevision: 0`. This recovery value never bypasses revision checks for a
valid document. Read/save still report `invalid_request` for corrupt JSON.


## Graph v3: typed nodes and structured bodies

The outer API/document envelope is unchanged. Graph v3 retains all v1/v2 node
configurations and v2 trigger identities/advanced sections. New nodes require v3.
Operands are JSON objects: `{kind:"literal",type:"boolean"|"number"|"string",value:...}`
or `{kind:"variable",name:"output_name"}`. JSON literal values must match their type.

| kind | configuration | outputs |
|---|---|---|
| if | `value`: boolean operand | true / false / error |
| and, or, xor | `left`, `right`: boolean operands; `output` | next / error |
| not | `value`: boolean operand; `output` | next / error |
| compare | `left`, `right` operands; `operator`: eq/ne/lt/le/gt/ge; `output` | next / error |
| read_entity | `entity`; `field`: state/attribute; `attribute` when needed; `type`; `output` | next / error |
| set_value | `value` operand; destination `type`; `output` | next / error |
| log | `value` operand; `level`: Debug/Info/Warn/Error | next / error |
| stop | empty configuration | none |
| repeat | `count`: invariant integer string 0–1000 | body / next / error |
| wait_until | entity/field/attribute/type as read_entity; `operator`, `right` operand; `timeout`, `interval`: seconds strings | next / error |

A name has the pattern `[A-Za-z_][A-Za-z0-9_]{0,63}`. Declarations with conflicting
types, missing producers, incompatible operands and invalid fields are rejected
before compilation. Boolean/number/string values are held in `VisualValues` inside
each invocation, along with responses/lastError. Reads are checked at runtime too:
a declaration on a path that did not run never yields a silent default. Equality
requires matching types; ordering requires numbers. Logic evaluates both inputs,
so a missing right-hand value is still diagnosed. Set value performs explicit
invariant conversion; entity extraction does the selected conversion. Boolean
accepts true/false, on/off and 1/0. Nonfinite numbers, missing attributes, null and
collections fail. Python.NET scalar attributes are normalized while holding the GIL.

Repeat uses a flat DAG, with a Body edge entering a distinct scope. Ordinary and
error edges may not escape or enter that scope. Body nodes cannot be shared with
an outer continuation, another repeat body, or an independent trigger. The same
invocation-local dispatcher executes the body and returns on an unconnected output;
Repeat Next runs once after all iterations. Nested scopes are limited to 8 and an
invocation to 100,000 dispatched steps. Stop propagates across nested dispatchers.
Cancellation checks occur at every node/iteration and bypass error routes.

Wait until reevaluates its entity predicate with live `PyInterop.Entity` reads,
so the original triggering snapshot cannot hide state changes. It uses a monotonic
clock, mandatory timeout 0.01–86400 seconds and interval 0.01–3600 seconds; the final
delay is capped by remaining time. Timeout throws TimeoutException into error
routing. Frozen local-variable conditions are deliberately unsupported.

New regression coverage executes generated C# for logic truth tables, branches,
state/attribute conversion, local-value isolation and missing-path errors, nested
Repeat completion/stop/cancellation/scope errors, Wait fresh polling/timeout/
cancellation, validation and v3 persistence. Frontend tests cover category filters,
typed editing, compatible references, attributes and graph serialization.

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
On integration initialization the saved source is compiled and loaded. Preview
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
config overrides data.entity_id. All nodes can use an `error` edge; an exception
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
Live HA has not been tested from this branch (Docker socket permission denied).

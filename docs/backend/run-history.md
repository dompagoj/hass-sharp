# Run history backend (run history version 2, run trace version 1)

History uses EF Core SQLite migrations in HA config at
`.storage/hass_sharp/runs.db`, independently of Recorder and integration files.
A single manager owns the store, serializing writes with one connection per
operation. Construct it once per process; construction marks unfinished runs
Interrupted. Integration reload reuses it and cancels active invocations.

Script list items include `automationId` and `revision`. Identity survives
save, rename and process restart; deleting and recreating a script creates a new
identity. Revision is the SHA-256 of the generated or written source. External
filesystem renames are not tracked; use the integration rename API.

## WebSocket commands

All are administrator only.

- `hass_sharp/run_now`: `automationId`, `className`, `method`.
  Returns `{version: 1, runId}` after initial persistence and registration.
  This is an accepted attempt, not a claim of successful execution.
- `hass_sharp/run_history`: `automationId`, optional `offset` (default 0),
  `limit` (default 50, maximum 100) and `status` (a list of statuses; only runs
  with one of them are returned, filtered before paging). Returns
  `{version: 2, offset, limit, runs}`. Read the returned run ID to distinguish
  success, skip, failure or cancellation.
- `hass_sharp/run_trace`: `runId`. Returns
  `{version: 1, run, live, snapshot, trace}`, or the error `not_found` for an
  unknown run. `live` is true while the run is Pending or Running.

## Runs

```text
{runId, automationId, revision, action, actionLabel, triggerNodeId, visualId, visualRevision,
 cause, trigger, plannedUtc, receivedUtc, startUtc, endUtc, durationMs, status, error, hasTrace}
```

- `action` is the class and method; `actionLabel` is the trigger node's label, or
  the method name when it has none. `triggerNodeId`, `visualId` and
  `visualRevision` identify the visual trigger and document revision that ran and
  are `null` for C# automations.
- `cause` is `Startup`, `Entity`, `Manual`, `Schedule` or `HomeAssistantTrigger`.
- `trigger` is a parsed object summarising what fired the run:
  `{kind, platform?, id?, description?, entityId?, from?, to?, eventType?, scheduleId?, planned?}`
  with `kind` one of `ha`, `entity`, `schedule`, `manual` or `startup`. It is built
  from the normalised trigger payload, is at most 2,048 characters as JSON, and
  shortens long strings rather than ever becoming invalid JSON.
- `status` is Pending, Running, Succeeded, Failed, Cancelled, Skipped,
  Interrupted or Stopped. Succeeded means the action completed; fire-and-forget
  service calls do not prove a device effect. Stopped means a visual Stop node
  ended the run. Skipped means the trigger's mode did not admit the run.
  Interrupted runs were unfinished when the process restarted.
- `error` is the failure message for Failed runs. Cancelled runs give the reason:
  `Restarted by a newer trigger`, `Automation reloaded`, `Home Assistant stopping`,
  or `Invocation cancelled` when no reason is known. Errors are capped at 2,048
  characters.
- `hasTrace` is true when a trace is stored for the run.

Planned time is set for scheduled runs. The newest 10,000 completed records are
retained globally; active runs are never pruned. Reads order by received
timestamp then run ID descending. Offset pages can shift as new runs arrive.

## Traces and graph snapshots

Every run of a visual automation records a trace of the nodes it executed. Each
visual revision's graph and the catalog it was compiled against are stored once,
keyed by the run's `revision` (the hash of the generated source), when that
revision first runs. `run_trace` returns that snapshot as
`{revision, visualId, visualRevision, graph, catalog}`; `visualId` and
`visualRevision` come from the run, because one snapshot can be shared by several
document revisions that generate the same source. C# automations have no snapshot
and no trace.

```json
{"format":1,"runId":"…","rootNodeId":"t1","startedUtc":"…","complete":false,"truncated":false,"dropped":0,
 "trigger":{},
 "events":[{"seq":3,"node":"svc","frame":"p1:branch:a/l1#2:body","t":12.5,"ms":340.1,
   "status":"ok","port":"next",
   "inputs":{"field:brightness_pct":{"type":"number","v":40,"src":{"node":"n0","port":"value"}}},
   "outputs":{"response":{"type":"object","v":{},"trunc":true}},
   "error":{"type":"InvalidOperationException","message":"…"}}],
 "summary":{"svc":{"count":3,"failed":0,"last":"ok"}}}
```

- `trigger` is the normalised trigger payload, at most 16 KiB.
- One event per node execution, in order. `status` is `ok`, `error` (the node
  failed and followed its `error` port), `failed` (the node failed and the failure
  was not handled there), `running`, `cancelled` or `stopped`.
- `frame` is `""` at top level; nested scopes add `/`-joined segments
  `<parallelNodeId>:branch:<branchId>`, `<loopNodeId>#<iteration>:body` and
  `<loopNodeId>#<iteration>:condition`, with 0-based iterations.
- `t` is milliseconds since the run started and `ms` the node's duration. `port`
  is the execution port the node took; it is absent for nodes without one.
- `inputs` and `outputs` map port names to `{type, v, src?, trunc?}`; `src` names
  the node and port a wired input came from.
- `summary` has one row per node of the graph: how often it ran, how often it
  failed, and its last status.

Caps: at most 2,000 events per run; after that only `summary` and `dropped`
change and `truncated` is true. Each value is cut at depth 6, 50 items or
properties, 1,000 characters per string and 4 KiB serialised, and is then marked
`trunc: true`. An event is at most 16 KiB and a trace at most 256 KiB: beyond that
the oldest events lose their inputs and outputs first (they keep node, frame,
status, port, timing and error, and are marked `"trunc": true`), and only then are
new events dropped.

The trace is written at most once per second, again when a node starts waiting
(delay, wait until, service calls and asynchronous custom or library nodes) and at
run end, so a running run's trace can be read while it waits. Recording never
fails a run: a recorder error stops recording and marks the trace truncated.

Retention: traces are kept for the newest 1,000 runs that have one; older runs
keep their record with `hasTrace` false. A pruned run's trace is deleted with it,
and a snapshot is deleted when no remaining run refers to it.

## Failure behaviour

If opening or migrating the database fails at startup, HassSharp setup fails
closed: automations, the sidebar, and its WebSocket routes do not start. This
avoids executing actions without an initial history record. Later failures
surface through the bridge/logs, and unfinished records become Interrupted on
process restart. A final write failure cannot undo an action already performed.

## Verification

Runtime console regressions cover lifecycle modes, identity, concurrent
persistence, migration/reopen interruption, retention and trace caps, pagination
and status filtering, UTC roundtrip, manual dispatch, skip, cancellation reasons,
success, failure, stop, trace recording and initial database failure. Python
handler tests cover dispatch/read arguments, admin access and sanitized errors with
diagnostic logging. `tests/packaging/sqlite_smoke.py` checks native SQLite loading
and the four migrations through Python.NET.

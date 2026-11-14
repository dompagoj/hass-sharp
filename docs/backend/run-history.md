# Run history backend (run history version 2, run trace version 1)

History uses EF Core SQLite migrations in HA config at
`.storage/hass_sharp/runs.db`, independently of Recorder and integration files.
A single manager owns the store, serializing writes with one connection per
operation. Construct it once per process; construction marks unfinished runs
Interrupted. Integration reload reuses it and cancels active invocations.

Each visual automation document carries `automationId`, the identity its runs are
recorded under. It survives saves, renames (a new `graph.name`) and process
restarts; deleting and recreating an automation creates a new identity. A run's
`revision` is the SHA-256 of the generated source that ran.

## WebSocket commands

All are administrator only, and answer `not_ready` while no HassSharp entry is
set up.

- `hass_sharp/run_now`: `automationId`, `className`, `method`.
  Returns `{version: 1, runId}` after initial persistence and registration.
  This is an accepted attempt, not a claim of successful execution. An automation
  that is not active, or a method it does not have, answers `invalid_request`
  with the reason.
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
  `visualRevision` identify the visual trigger and document revision that ran. They
  are `null` only in rows kept from hand-written C# automations, which are no
  longer loaded.
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
document revisions that generate the same source. Rows kept from hand-written C#
automations have no snapshot and no trace.

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
- `inputs` and `outputs` map port names to `{type, v, trunc?, redacted?, src?}`;
  `src` names the node and port a wired input came from.
- `summary` has one row per node of the graph: how often it ran, how often it
  failed, and its last status.

What a value records: tracing never runs automation code, so only values that
are already materialised are walked:

- JSON values, strings, numbers, booleans, dates and times, entity states, and
  exceptions (type and message).
- Arrays.
- Collections and dictionaries that meet all of these conditions:
  - They are top-level (not nested) types from the .NET base library.
  - They are in `System.Collections`, `System.Collections.Generic`,
    `.Concurrent`, `.Immutable`, `.Frozen` or `.Specialized`.
  - They implement `ICollection`, `ICollection<T>`, `IReadOnlyCollection<T>` or
    `IDictionary`.

  Examples are `List<T>`, `HashSet<T>`, `Dictionary<K,V>`, `Queue<T>`,
  `SortedDictionary<K,V>`, `ImmutableArray<T>` and `ConcurrentDictionary<K,V>`.
  None of the following is enumerated:
  - a user-defined collection type, including a subclass of a base-library
    collection
  - wrappers and views over another collection, which are nested types
    (`ArrayList.Adapter`, the `ReadOnly` and `Synchronized` wrappers, `SortedSet`
    views, a dictionary's `Keys` and `Values`)
  - anything in `System.Collections.ObjectModel` (such as `ReadOnlyCollection<T>`)
  - `BlockingCollection<T>`
- Tuples (`ValueTuple`, `Tuple`) by their items (`Item1`, …, `Rest`), and
  `KeyValuePair` as `Key` and `Value`.
- The public fields and compiler-generated auto-properties of other objects,
  such as records, plain classes and anonymous types. They are read from their
  fields, so no getter runs. A computed property is left out and the object is
  marked `trunc`. When a derived type hides a member with `new`, only the derived
  member is recorded, and the object is marked `trunc`.

A value with nothing readable is recorded as its C# type name (such as
`global::System.Collections.ObjectModel.ReadOnlyCollection<int>`) with
`trunc: true`. This applies to lazy sequences such as LINQ queries or iterators
(never enumerated), collections that are not walked, objects with only computed
properties, and Python objects. Dictionary keys are strings, or are formatted by
the base library (numbers, dates, enums). A key of any other type is written as
its type name and position, such as `global::System.ValueTuple<int, string>#0`,
with `trunc`. A key that repeats after formatting gets its position appended, or
the next free position. If a base-library enumerator or an exception's message
throws, the value is shortened and marked `trunc`: an enumerator that cannot
start gives `null`, the rest of the collection is left out, or the message is
empty.

Caps:

- A run records at most 2,000 events. After that only `summary` and `dropped`
  change, and `truncated` is true.
- Each value is cut at depth 6, 50 items or properties, 1,000 characters per
  string, and 4 KiB serialised, and is then marked `trunc: true`. A container
  deeper than the depth cap is written as `null`.
- An event is at most 16 KiB. A port whose value does not fit keeps its type and
  source but not `v`.
- A trace is at most 256 KiB. Beyond that, the oldest events lose their inputs and
  outputs first. They keep node, frame, status, port, timing and error, and are
  marked `"trunc": true`. Only then are new events dropped.
- The trigger payload is at most 16 KiB.

Redaction: a property, dictionary key or port is recorded as `"***"` when its
name meets either of these conditions:

- It is `password`, `passwd`, `passcode`, `secret`, `client_secret`, `token`,
  `access_token`, `api_key`, `apikey`, `api-key`, `authorization`, `code` or
  `pin`.
- It ends in `_token`, `_secret`, `_password`, `_passcode` or `_key`, as in
  `refresh_token`.

Names are matched without regard to case. The short names match only exactly, so
`zip_code` and `pincode` are kept. For a port, the name after `field:` is used,
so the `code` field of `lock.unlock` is redacted. A redacted port's entry also
has `redacted: true`. Inside any string, `token=<value>` becomes `token=***`,
which covers, for example, an `entity_picture` URL. A redaction alone does not
mark a value `trunc` or the trace `truncated`. These rules apply to the trigger
payload, entity state attributes and nested objects alike, and the real value is
never serialised. Redaction is by name only. It is a safeguard against the common
cases, not a security boundary: a secret stored under another name, or inside an
opaque string, is recorded as it is.

Storing: nodes only mark the trace as changed, and a single writer builds the
latest state and stores it, so stored documents never go backwards. While a run
is going, the trace is stored at most once per second. When a node starts waiting
(delay, wait until, service calls, and asynchronous custom or library nodes), it
is stored as soon as 500 ms have passed since the last store, or at once if
nothing has been stored yet. A running run's trace can therefore be read while it
waits, and many short waits cost at most two stores a second. The gaps, both the
recorder's and the store's one-second throttle, are measured on a monotonic clock,
so a change to the system time does not delay them. The run's end stores the
complete document, and nothing is stored after it.

Recording never fails a run. A recorder error is logged once and stops
recording. Events already listed still get their final status, and the stored
trace stays valid and is marked `truncated`. If the final document itself cannot
be built, a minimal document is stored instead. It is marked `complete` and
`truncated`, its `dropped` counts every node start, and its trigger, events and
summary are empty.

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

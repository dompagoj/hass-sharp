# Run history backend (contract version 1)

History uses EF Core SQLite migrations in HA config at
`.storage/hass_sharp/runs.db`, independently of Recorder and integration files.
A single manager owns the store, serializing writes with one connection per
operation. Construct it once per process; construction marks unfinished runs
Interrupted. Integration reload reuses it and cancels active invocations.

Script list items now include `automationId` and `revision`. Identity survives
save, rename and process restart; deleting and recreating a script creates a new
identity. Revision is the SHA-256 of source. External filesystem renames are not
tracked; use the integration rename API.

Administrator WebSocket commands:

- `hass_sharp/run_now`: `automationId`, `className`, `method`.
  Returns `{version: 1, runId}` after initial persistence and registration.
  This is an accepted attempt, not a claim of successful execution.
- `hass_sharp/run_history`: `automationId`, optional `offset` (default 0),
  `limit` (default 50, maximum 100). Returns `{version: 1, offset, limit, runs}`.
  Read the returned run ID to distinguish success, skip, failure or cancellation.

Records contain run/automation IDs, revision, class/action, cause, bounded
entity ID details, planned/received/start/end UTC timestamps, duration in
milliseconds, status, and bounded error. Planned time is reserved for the
scheduling task. Status is Pending, Running, Succeeded, Skipped, Cancelled,
Failed, or Interrupted. Succeeded means the C# action completed; fire-and-forget
service calls do not prove a device effect.

The newest 10,000 completed records are retained globally; active runs are never
pruned. Reads order by received timestamp then run ID descending. Offset pages
can shift as new runs arrive. Error and cause details are capped at 2,048
characters. If opening or migrating the database fails at startup, HassSharp
setup fails closed: automations, the sidebar, and its WebSocket routes do not
start. This avoids executing actions without an initial history record. Later failures
surface through the bridge/logs, and unfinished records become Interrupted on
process restart. A final write failure cannot undo an action already performed.

Verification: runtime console regressions cover lifecycle modes, identity,
concurrent persistence, migration/reopen interruption, retention, pagination,
UTC roundtrip, manual dispatch, skip, cancellation, success, failure, and initial
database failure. Python handler tests cover dispatch/read arguments, admin
access and sanitized errors with diagnostic logging. Live HA/Python.NET native
SQLite loading was unverified during the original task. The integrated branch
subsequently passed the packaging smoke check in the local Alpine Home Assistant
container. No frontend files were changed in the original backend task.

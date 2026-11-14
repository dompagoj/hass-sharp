# Scheduling

A Trigger step runs its automation on a schedule when its type is **Daily** or
**Once**. In the graph, that is the trigger's `config`:

```json
{"type": "daily", "time": "07:30", "timeZone": "Europe/Zagreb"}
{"type": "once", "at": "2026-12-01T10:00:00Z"}
```

Daily recurrence requires a local time (`HH:mm`) and an explicit IANA time zone.
A once trigger is an absolute instant: an ISO timestamp with an explicit offset or
`Z`, normalized to UTC. The schedule's ID is the trigger node's ID, which stays the
same through renames and layout changes. The trigger's Trigger data output carries
`{platform: "schedule", schedule_id, planned}`; see
[Writing visual automations](writing-visual-automations.md#triggers-and-their-outputs).

Policy: only future occurrences are registered at startup/reload. Missed starts
are skipped, including past one-shots. A nonexistent local time during a DST gap
is skipped. A repeated time fires at its first instant only. A callback delayed
while HA remains running dispatches its planned occurrence once; subsequent
missed recurrences are skipped. No hard real-time guarantee is made. While the
automation is turned off, an occurrence that comes due is skipped, without a
history row or a claim, and is not caught up when it is turned on again; see
[turning an automation off](writing-visual-automations.md#turning-an-automation-off).

HA's UTC point-in-time helper owns each timer; recurrence computes the next local
calendar date rather than adding 24 elapsed hours. Registration/removal is marshaled
to HA's loop. Replacement and unload cancel the automation's lifetime and all
timers; stale callbacks cannot dispatch into the old lifetime.

Schedule invocations use the ordinary mode/cancellation/history runner. History
contains cause `Schedule`, planned UTC, received/start/end UTC, schedule ID and a
stable occurrence ID (hash of automation/class/action/schedule ID/planned UTC).
SQLite claims each occurrence just after the schedule fires, outside the trigger
path, including mode-skipped attempts, and then dispatches it only if the automation
is still active, on and not stopping; otherwise the claim is given back. An
occurrence claimed by a revision that a save replaced before it dispatched is thus
run by the new revision when its own schedule fires for that instant. Claims survive
reload, process restart, revision edits and history retention; an occurrence whose
revision is replaced at the instant it fires can still be lost, when the new
revision's schedule has already passed that instant.
The claims table is intentionally retained indefinitely, independently of bounded
run history; it currently has no pruning UI. Without configured history, dedup
is limited to the loaded automation. At-most-once dispatch attempts do not
guarantee exactly-once device effects: a crash after claiming can lose that attempt.

Daily and once are the only schedule kinds; weekly/monthly/cron rules are not
implemented. Replacing a trigger with a new node deliberately creates a new
schedule identity. For other time-based starts, use a native Home Assistant
trigger such as `time` or `time_pattern`.

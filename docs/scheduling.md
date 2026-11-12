# Scheduling

Declare schedules on the same action as entity/manual/startup triggers:

```csharp
public override AutomationConfiguration Configure() => new(
    new AutomationAction(nameof(Run), AutomationMode.Restart,
        Entities: ["sensor.temperature"],
        Schedules: [
            AutomationSchedule.Daily("morning", new TimeOnly(7, 30), "Europe/Zagreb"),
            AutomationSchedule.Once("appointment", DateTimeOffset.Parse("2026-12-01T10:00:00Z"))
        ]));
```

Schedules persist in the saved script. Daily recurrence requires an explicit IANA
timezone and local time. One-shots represent absolute instants, normalized to UTC.
IDs must be nonempty and unique within an action. Keep IDs stable across edits.

Policy: only future occurrences are registered at startup/reload. Missed starts
are skipped, including past one-shots. A nonexistent local time during a DST gap
is skipped. A repeated time fires at its first instant only. A callback delayed
while HA remains running dispatches its planned occurrence once; subsequent
missed recurrences are skipped. No hard real-time guarantee is made.

HA's UTC point-in-time helper owns each timer; recurrence computes the next local
calendar date rather than adding 24 elapsed hours. Registration/removal is marshaled
to HA's loop. Replacement and unload cancel the script lifetime and all timers;
stale callbacks cannot dispatch into the old lifetime.

Schedule invocations use the ordinary mode/cancellation/history runner. History
contains cause `Schedule`, planned UTC, received/start/end UTC, schedule ID and a
stable occurrence ID (hash of automation/class/action/schedule ID/planned UTC).
SQLite claims each occurrence before dispatch, including mode-skipped attempts.
Claims survive reload, process restart, revision edits and history retention.
The claims table is intentionally retained indefinitely, independently of bounded
run history; it currently has no pruning UI. Without configured history, dedup
is limited to the loaded class. At-most-once dispatch attempts do not guarantee
exactly-once device effects: a crash after claiming can lose that attempt.

Daily recurrence is the initial recurring API; weekly/monthly/cron rules are not
implemented. Changing a schedule ID deliberately creates a new identity.

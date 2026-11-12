# Backend review corrections

Based on integrated commit e7ee0ac, 2026-11-12.

- Startup compiles code files and saved visual sources independently. Invalid
  files stay on disk for repair, and errors identify their paths. Visual listing
  skips malformed JSON, invalid names and invalid document envelopes.
- Startup activation stages each script until configuration and subscriptions
  succeed. A failed script is removed from tracking and its assembly unloaded;
  subsequent healthy scripts still load.
- Failed action logs include exception type and stack; history retains its bounded
  message rather than storing unbounded stack traces.
- Code delete and rename reject visual runtime paths. Visual deletion forgets the
  identity even when no runtime is active. Visual concurrency modes accept only
  exact enum names, excluding numbers and comma-separated combinations.
- Missing MotionSensor configuration reports the property name.
- Migration 202611120003 adds a partial global completed-run index. SQLite query
  plan coverage verifies the retention query avoids a temporary sort. The existing
  automation/date/run index remains for per-automation history reads.

## Deliberately retained behavior

The initial history write still fails closed, including after startup. The history
contract explicitly says this avoids executing actions without an initial record,
and runtime regression coverage verifies a database becoming unavailable prevents
execution. Swallowing failures would violate that guarantee and make Run now return
an accepted run ID with no durable attempt. Final writes cannot undo actions; their
failures still propagate. No persistence policy was changed.

Entity/schedule dispatch still holds ActivationGate through synchronous initial
persistence and run registration. This can block on SQLite, but dropping the gate
would race activation/replacement and stale callbacks. The retention index reduces
avoidable work without changing admission ordering. Moving I/O off the dispatch
path requires a separate design preserving those guarantees; no latency or load
benchmark is claimed here.

## Verification

Runtime console suite: tests/Runtime/Runtime.csproj. BackendReviewTests reproduces
startup compilation isolation, partial subscription cleanup, malformed listing,
exception diagnostics, strict modes, missing sensor errors, inactive identity
cleanup and query plan selection. Each new behavior was observed failing before
its fix; the code-delete guard was separately removed to confirm its regression.
Full runtime suite and all eight Python unittest checks pass. Python compileall
and git diff --check pass. No live HA restart or production database migration
was performed. The rename guard is inspected but not executed through Python.NET
by these console tests.

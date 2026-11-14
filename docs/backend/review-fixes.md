# Backend review corrections

Based on integrated commit e7ee0ac, 2026-11-12.

This is a historical record. Hand-written `.cs` automations, their HTTP routes,
the compile cache and the MotionLight helper have since been removed (automations
are visual only); statements about them describe the code at that commit.

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

The initial history write still fails closed, including after startup. This is the
existing implementation and regression-tested behavior, not a verified owner
requirement. The written contract explicitly mandates startup failure only and
says later failures surface through the bridge/logs. Best-effort runtime history
would change Run now's accepted-attempt semantics: the returned ID could have no
record. Schedule occurrence claims also depend on this database. A consistent
best-effort policy needs to define those cases together; this focused correction
leaves the behavior unchanged. Final writes cannot undo actions and their failures
still propagate. Runtime fail-closed remains an unresolved product policy choice.

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

## Follow-up review

Failed code files are included in the list with active=false, activationError and
empty classes; opening reads the preserved source, saving compiles/prepares before
writing and activating, and deleting works without a loaded runtime. The backend
supplies error metadata; rendering an error badge is a frontend follow-up. Failed
code entries have no dispatchable action or automationId until activation.

Visual list/read responses add active and activationError. Failed saves can retain
the old active runtime, so active=true with activationError describes that case.
Corrupt documents can be explicitly deleted with expectedRevision=0; valid documents
still require an exact revision. Reads/saves continue reporting invalid_request for
corrupt files. Compile cache image failures fall back to source compilation. Default
graph previews no longer report scaffolding nullable/unused-variable warnings.

Fable independently passed the first commit's runtime/Python suites, and confirmed
that the HTTP code paths of that time confined delete/rename to the hand-written
script folder; the additional
runtime guard is defense in depth. The synchronous rename HTTP bridge issue reported
by inspection predates this work and remains outside these fixes.

Fable's second review independently passed the full runtime suite and eight Python
tests at 576717d, including corrupt-cache variants and repaired configuration
failures. A final HTTP regression now checks that open/save reject invalid script
names with 400 before accessing the backend (nine Python tests total). The possible
delete-versus-save race remains unverified and unchanged; lifecycle serialization
across those operations would need a separate focused fix. Live HA/Python.NET and
frontend rendering remain unverified.

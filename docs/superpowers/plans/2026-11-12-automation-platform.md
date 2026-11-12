# HassSharp Automation Platform Implementation Plan

> **For agentic workers:** Implement task by task with failing regression tests before behavior changes. Keep each branch reviewable and report verification.

**Goal:** Deliver reliable code and graph-authored C# automations, scheduling, run history, Run now, preview, Monaco fixes, and a polished sidebar UI.

**Architecture:** A synchronous configuration API declares triggers; a common invocation runner executes every cause and records structured outcomes. Visual documents generate C# for that same runner. Frontend and persistence use versioned contracts.

**Tech Stack:** Home Assistant Python integration, Python.NET, .NET 10, Roslyn, EF Core with SQLite, SolidJS, Monaco, solid-flow.

**Spec:** `docs/superpowers/specs/2026-11-12-automation-platform-design.md`

## Global constraints

- No existing scripts need migration.
- SQLite belongs in persistent HA configuration, outside replaceable integration files.
- Graph source and generated C# must represent the same revision.
- Each task needs meaningful regression coverage, a successful build, and a commit.
- Do not claim external service success from fire-and-forget dispatch.

## Review focus

- Trigger arrives during replacement or unload: no old callback invokes a disposed script.
- Rapid repeated changes: mode policy and cause recorded for every attempted run.
- Restart during delay: old invocation cancels and cannot later act on the device.
- Entity list refresh during editing: stale generated symbols disappear from Monaco.
- Daylight-saving gap/fold: schedule fires according to one documented policy.

---

### Task 1: Runtime lifecycle and trigger contract (Programming MID?)

**Files:** `HassSharp/UserScripts/`, `HassSharp/UserScriptManager/`, `__init__.py`; add runtime tests near the relevant code.

**Produces:** A synchronous declaration phase, explicit startup/entity/manual invocation causes, a single async execution path, cancellation and mode behavior for all causes. Remove the async `Initialize()` route. Fix unsubscribe bookkeeping and duplicate dependency registration.

- [ ] Add failing tests for the BathroomHeating pattern with a shortened delay, reload/resubscribe, duplicate entity registration, trigger during startup, and each mode.
- [ ] Run tests and verify expected failures.
- [ ] Implement the new API and ownership rules, then run focused and full runtime checks.
- [ ] Commit with tests and a short API example.

### Task 2: Monaco correctness (Programming BABY)

**Files:** `HassSharp/DiagnosticsProvider.cs`, `frontend/src/components/Editor/`, related WebSocket DTOs and tests.

**Produces:** Correct generated-entity document replacement, exception-safe temporary document cleanup, Roslyn text edits, versioned/cancellable requests, editor disposal, model-scoped diagnostics, and a shared Save command.

- [ ] Add failing regression tests for entity refresh and empty hover; exercise stale response and save behavior in frontend tests.
- [ ] Run tests and verify expected failures.
- [ ] Fix the completion and lifecycle path; run .NET tests, frontend checks and build.
- [ ] Commit with test results.

### Task 3: Structured run history and Run now

**Files:** Runtime invocation records, EF Core persistence module, Python bridge/API, frontend automation detail and history views.

**Produces:** Stable automation ID, revision, run ID, cause and bounded details, planned/received/start/end UTC times, duration, status, cancellation/error; paginated history and retention; manual dispatch.

- [ ] Add failing tests for success, failure, skip, cancel, interrupted restart, concurrent writes, retention and manual cause.
- [ ] Implement EF Core SQLite migrations and one serialized write path under persistent HA config.
- [ ] Expose paginated reads and Run now through the integration; render list and detail.
- [ ] Verify live reload and database-failure behavior, then commit.

### Task 4: Wall-clock scheduling

**Files:** Trigger declaration, Python HA time registration, schedule model/persistence, runtime dispatch and tests.

**Produces:** One-shot timestamps and recurring local schedules with explicit timezone, missed-run policy, DST policy, deduplicated occurrence ID, and clean unsubscribe.

- [ ] Add failing fake-clock tests for normal, gap/fold, restart, missed, and colliding entity/schedule triggers.
- [ ] Implement HA callback registration and shared runner dispatch.
- [ ] Verify actual HA integration and history cause details, then commit.

### Task 5: Custom UI foundation

**Files:** `frontend/src/components/`, `MainLayout.tsx`, automation list/detail views and styles.

**Produces:** Solid buttons, fields, dialogs, menus, tabs, status, responsive layout and light/dark tokens replacing visible `ha-*` controls.

- [ ] Add accessibility-oriented interaction checks for focus, keyboard and dialogs.
- [ ] Implement components and redesign automation list/detail/editor screens.
- [ ] Verify 360px and desktop widths, light/dark themes, build and keyboard flow; commit.

### Task 6: Visual editor and preview

**Files:** Versioned graph schema, graph validator/generator, backend graph/source persistence, solid-flow adapter, new frontend views and tests.

**Produces:** C#/Visual creation choice, palette/canvas/inspector, undo/redo, persistent graph, deterministic C#, source mappings and preview/validation. Support custom C# sections for capability parity.

- [ ] Spike solid-flow adapter for same-count graph updates, drag persistence, resizing, keyboard/touch and CSS isolation; decide pinned patch/fork if needed.
- [ ] Add failing graph schema, generator, and atomic-save tests before production code.
- [ ] Implement entity/condition/service path end to end; then add time, delay, branch, response, error and concurrency nodes plus custom C# sections.
- [ ] Compare generated and hand-written fixtures across modes/cancellation, verify preview errors map to nodes, run build and commit.

### Task 7: Final integration

- [ ] Run full .NET/frontend/Python tests and Home Assistant smoke tests from `AGENTS.md`.
- [ ] Review the whole branch for runtime safety, persistence, editor accessibility and production packaging.
- [ ] Fix findings with regression tests; document accepted limits and prepare a pull request.

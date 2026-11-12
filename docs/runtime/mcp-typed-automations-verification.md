# MCP and typed automations verification

Verified locally on 2026-11-12 on `feat/mcp-typed-automations`. Backend acceptance build: `6c19321`; final spacing build: `36fa6fd`. Home Assistant 2026.11.1 in the repository Compose container, Linux .NET 10.0.101, Python.NET 3.1.0. The host SDK is an installed .NET 11 preview targeting net10; its preview-SDK informational message is expected. No remote Home Assistant deployment was performed.

## Automated checks

- Full .NET runtime harness passed, including every built-in node kind, typed entity/custom class/record/interface execution, exact Int64 values, response-driven service target/data, all loop forms, initialized zero-iteration exports, stale output rejection, scope/type errors, shared execution limits, parallel isolation/cancellation/Stop cleanup, native attachment/replacement, library dependency transactions/recovery, cache identity, and atomic visual dispatch. Includes 80 shared v4 and 21 rich catalog validation fixtures.
- Roslyn editor suite: 18 passed, 0 skipped. Completion, diagnostics, hover, signature help including named arguments, formatting and cross-file definitions use real compiler workspaces.
- Frontend: 312 tests in 22 files passed. Includes catalog/port labels, typed connection rejection, errors/focus, touch and keyboard controls, Option/Alt single-edge undo/redo, library drafts/revisions/stale requests and external deletion recovery.
- Python: 38 tests passed without skips in a separate process inside actual HA2026.11.1. The old host HA suite passed with 8 expected feature/version skips and is not counted as2026.11 verification.
- Release packager: 3 tests passed. Fresh production frontend build and RID-neutral .NET publish passed. The final archive includes catalog/examples and all native runtime directories; no user scripts, development sources or preview fixtures.
- Actual Python.NET packaging smokes passed: Linux-musl-arm64 SQLite loading, architecture assets, migrations/persistence/schedule deduplication; native trigger Task registration/dispatch/removal/failure; managed-worker scalar normalization and entity snapshot→custom class→custom record execution.

## Actual MCP and Home Assistant workflow

HTTP MCP initialize/tools/list exposes 15 prefixed tools with real schemas/annotations. Strict invalid arguments reject. Missing authentication returns 401 and a temporary ordinary local-only user returns 403 on configured/direct endpoints. Configuring only HassSharp in read-only mode exposes 10 query/preview tools; the original options were restored and the test user/credentials removed.

The actual MCP workflow passed: library preview/save/list/read and typed schema discovery; filtered/paginated entities/services/devices/areas/triggers; complete graph preview/save/read with supplied layout preserved and missing layout filled; typed synthetic entity→custom record→Boolean execution and successful history; persisted-but-failed activation reporting and stale active generation run refusal; concurrent editor/MCP saves yielding exactly one revision winner; incompatible library edit and referenced deletion rejection; valid library rename/rebuild and graph revision conflicts.

Native event preview attached nothing. Saving attached one trigger, delivered immutable payload/cause/history, rejected an invalid replacement without changing the saved revision, replaced the listener, and removed it on deletion. Real config-entry unload/reload restored the MCP API, sidebar panel, exact library graph revision/source hash/current activation, and exactly one native listener. Recent logs no longer contain the prior sensor-already-setup error. All temporary graphs, libraries and synthetic states were removed. Tests used harmless logs and synthetic events; no physical device service was invoked.

## Actual browser checks

Observed the real Home Assistant panel with label-free agent-authored graphs, typed canvas/inspector labels and matching categories. Real mouse Number→Boolean drag rejected before an edge was added, with an exact mismatch and target highlight. Option-click removed one wire, required input became unset, Save blocked, one Undo restored the complete saved state, and Redo repeated it. Error-list navigation focused the input; keyboard connection cleared errors.

Observed library file list, typed cards/XML docs, normal/full screen, actual Roslyn entity member completion, precise diagnostics and Save blocking, error navigation/Undo clearing, hover, named-argument signature help, formatting, F12 cross-file navigation, create/save/rename and dependent activation feedback. External deletion preserved a dirty draft until confirmed reload removed only the missing file. Physical touch hardware was not tested; the actual SolidFlow touch DOM regression tests passed.

## Remaining release notes

Only graph v4 is supported. Old visual graphs must be recreated; the document/API envelope remains 1. Library saves revalidate and rebuild all saved graphs and advance their revisions, so open editors may need to reload. Saving remains coupled to activation; always inspect currentRevisionActive and activationError.

The requested spacing correction passed TypeScript, 33 focused tests and production build. Independent review found no issues. Actual desktop, 393px device viewport, empty/populated state and normal/full-screen checks confirmed gutters, horizontal navigation, stacked narrow columns and usable controls. A temporary unsaved example was discarded; no library was persisted. This follow-up was visually checked in the current dark theme; no light-theme preference was changed. Physical touch hardware was not tested.

The final whole-branch review is recorded separately after all implementation tasks.

## Implementation decisions

The chronological decisions from the implementation ledger are retained here, including superseded ones:

1. Work in the current checkout on a feature branch, as explicitly requested. This keeps the local container bind mount aligned; the tradeoff is no separate worktree isolation.
2. An initial ruling preserved the legacy generator alongside v4. The user's later no-compatibility instruction superseded it; legacy graph execution and migration were removed. The final cost is intentional breakage for old graphs.
3. An initial migration ruling retained the old Run trigger identity. The same user correction superseded it; v4 uses stable Run_ plus UTF-8 hex node IDs with no migration hint. Old graph identities are not retained.
4. Implement the independent native HA bridge before the typed runtime while the custom-library design was awaiting approval. This established concrete bridge interfaces earlier; the risk was integration rework, covered by later runtime and live tests.
5. Revalidate/recompile every saved visual graph on a library mutation because generated artifacts embed all saved library sources, including indirect C# references. This avoids missing dependencies; the cost is extra compilation and revision conflicts even for apparently unrelated graphs.
6. Fold the deferred named-argument signature correction into the custom-library editor task. It is now covered by real Roslyn tests and actual Monaco signature help; the cost was a small backend extension to that UI task.


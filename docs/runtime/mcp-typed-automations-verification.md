# MCP and typed automations verification

This is a historical record of graph version 4 at `546663d`. Hand-written `.cs`
automations, typed entity objects, the 15-tool MCP surface and graph version 4
have since been removed or replaced: automations are visual only (graph version 5),
C# is written as `[Node]` methods, and the MCP API has 17 tools. Statements below
describe the code at that commit; [MCP authoring](../mcp-authoring.md) and
[writing visual automations](../writing-visual-automations.md) describe it now.

Verified locally on 2026-11-12 on `feat/mcp-typed-automations`. Final implementation and published build: `546663d`. The full MCP workflow initially passed on `6c19321`, with final trigger/schema regressions repeated on `546663d`; the spacing correction is included from `36fa6fd`. Home Assistant 2026.11.1 in the repository Compose container, Linux .NET 10.0.101, Python.NET 3.1.0. The host SDK is an installed .NET 11 preview targeting net10; its preview-SDK informational message is expected. No remote Home Assistant deployment was performed.

## Automated checks

- Full .NET runtime harness passed, including every built-in node kind, typed entity/custom class/record/interface execution, exact Int64 values, response-driven service target/data, all loop forms, initialized zero-iteration exports, stale output rejection, scope/type errors, shared execution limits, parallel isolation/cancellation/Stop cleanup, native attachment/replacement, library dependency transactions/recovery, cache identity, and atomic visual dispatch. The final validation run covers 98 shared v4 and 21 rich catalog fixtures.
- Roslyn editor suite: 18 passed, 0 skipped. Completion, diagnostics, hover, signature help including named arguments, formatting and cross-file definitions use real compiler workspaces.
- Frontend: 330 tests in 22 files passed on the final implementation. Includes catalog/port labels, typed connection rejection, errors/focus, touch and keyboard controls, Option/Alt single-edge undo/redo, library drafts/revisions/stale requests and external deletion recovery.
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

The whole-branch review found two trigger defects and three catalog/UI/documentation mismatches. All five were fixed in `546663d` and closed by one independent scoped re-review, with no new findings. The full runtime harness passed during this fix wave; the final minute-precision refinement additionally passed compiled `Configure()` checks for seven timestamps across UTC, Zagreb and New York, all 98 shared validator fixtures, and the 21 rich catalog cases. Final frontend tests passed 330/330.

Final actual MCP checks confirmed that an omitted trigger type subscribes and executes on a synthetic entity change; offset-free, malformed and impossible-calendar timestamps reject; Z and signed offsets work both with seconds and at minute precision. A read-only library preview exposed a custom CLR loop type and integer/enum literals while excluding object JSON for a non-null rich class. No preview source was saved. Temporary graph/state cleanup and entity-symbol restoration passed.

All three Python.NET smoke checks passed again on the final published assembly. The fresh production build and final archive were checked by hash against the published assets: 182 files, 21 native runtime assets, 33,578,556 bytes ZIP and 93,104,434 bytes unpacked. The local archive is `/tmp/hass-sharp-mcp-acceptance.zip`. Expected HA custom-integration warnings and intentional failure-test logging remain visible; the final review classified the earlier build/test notices as nonblocking verification noise.

## Implementation decisions

The chronological decisions from the implementation ledger are retained here, including superseded ones:

1. Work in the current checkout on a feature branch, as explicitly requested. This keeps the local container bind mount aligned; the tradeoff is no separate worktree isolation.
2. An initial ruling preserved the legacy generator alongside v4. The user's later no-compatibility instruction superseded it; legacy graph execution and migration were removed. The final cost is intentional breakage for old graphs.
3. An initial migration ruling retained the old Run trigger identity. The same user correction superseded it; v4 uses stable Run_ plus UTF-8 hex node IDs with no migration hint. Old graph identities are not retained.
4. Implement the independent native HA bridge before the typed runtime while the custom-library design was awaiting approval. This established concrete bridge interfaces earlier; the risk was integration rework, covered by later runtime and live tests.
5. Revalidate/recompile every saved visual graph on a library mutation because generated artifacts embed all saved library sources, including indirect C# references. This avoids missing dependencies; the cost is extra compilation and revision conflicts even for apparently unrelated graphs.
6. Fold the deferred named-argument signature correction into the custom-library editor task. It is now covered by real Roslyn tests and actual Monaco signature help; the cost was a small backend extension to that UI task.


7. Keep the final review within the approved scope: no old-graph migration, malicious administrator-code sandbox, atomic multi-file library save, or exhaustive physical-device/third-party-platform testing. This matches the approved design and harmless acceptance workflow; those capabilities are not claimed.

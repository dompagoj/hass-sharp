# Frontend checks

Run `bun run test` and `bun run build` from the frontend directory.

For visual and browser checks, run `bun run dev --host 0.0.0.0` and open
`http://localhost:5173/tests/preview.html`. The development-only preview renders
the real app and Monaco with an in-memory sample API. It never calls Home
Assistant or a device. Its outer controls set a real 360px iframe viewport and
switch inherited Home Assistant theme tokens while the editor stays mounted.
Do not treat these samples as compiled or working runtime automations.

Check list/search, the action menu with Arrow keys/Escape, the New automation form
with Enter, delete confirmation, and modal focus wrapping/return. In the node library
editor, check Source/Editor settings with arrows/Home/End, font size and Vim mode,
typing, Save and Ctrl/Cmd+S. Use Ctrl+M (Control+Shift+M on Mac) to let Tab move
focus out of Monaco. Check both themes at 360px and desktop
widths; long names should wrap without horizontal page scrolling.

For smoke-check evidence in browser devtools, the iframe exposes `previewCode()`
(real Monaco models). This fixture is outside the production build entry point. Live Home Assistant checks
still require the local instance described in the repository's AGENTS.md.

Visual editor page coverage lives in `editor-shell.test.tsx` (the top bar's in-place
rename and "⋯" menu, the bottom drawer's tabs, a problem selecting its step and field,
the step library's search and click-to-add, full screen with and without the Fullscreen
API, the side panel and the narrow-screen peek bar and sheet across resizes, Run now
in the top bar and the history-only Runs tab, the panel's Duplicate, and the keyboard
boundary) and `VisualAutomationView.test.tsx` (save state, Run now, Runs, Problems and
Generated C# against the in-memory API, and the unsaved-changes guard). `matchMedia` is
stubbed for the narrow layout; Happy DOM has no CSS, so the layout itself is a browser
check (light, dark, 360px and full screen in the preview below). Tests use the real pinned Solid Flow/D3
implementation. Happy DOM has no layout engine; the resize/edge tests supply only
measured DOM dimensions at that boundary. These tests do not replace desktop/mobile
browser acceptance. See `../docs/visual-editor-feasibility.md` for the adapter touch handling,
capability limits, and browser follow-up results.

Run `bun run build:preview` to build the mock-data responsive fixture; see the feasibility report for the temporary local HA static-route workflow. This is not a real HA integration test.

Run history checks are in `AutomationRuns.test.tsx` (the history list, with runs
started through the same `runDispatch.ts` dispatch as the editor's Run now) and
`VisualAutomationView.test.tsx`. The preview's sample runs follow run history v2 (action labels, trigger summaries,
`hasTrace`, and Stopped/Cancelled/Interrupted reasons); `?view=visual&typed=1` has
runs for its saved typed-demo automation under the editor's Runs tab.
Those runs accept mock manual runs. `window.runFixture` lets browser checks set
`historyError`, `dispatchError`, or the terminal `result`; no real HA service/device
is called. `window.previewErrors` collects page errors and rejected promises,
including the Monaco worker bootstrap regression. Open the node library, type, and
switch Source/Editor settings: the real Monaco model must survive, with no worker error.

The preview build uses `/hass-sharp-static/visual-v2-check-baby/`.
For a narrow iframe, open its `tests/visual-check.html` with `?view=list`,
`?view=library` or `?view=visual`, plus for example `&width=390&theme=dark`. See `../docs/run-history-ui.md` for evidence and
remaining live-HA verification. Copy preview output only to a unique temporary
static subdirectory, leaving the live panel bundles untouched, and remove it after
checking.

`visual-fixture.ts` also supplies the in-memory visual API, exposed as
`window.visualFixture` in the preview frame. Its controls simulate validation,
preview failure, revision conflicts, deletion conflicts and activation failure.
Use `VisualAutomationView.test.tsx` for editor/save/run behavior and
`visual-session.test.ts` for debounce, in-flight results and revision handling.
See `../docs/visual-api-integration.md` for browser evidence and the live sign-in
blocker. Fixture results do not verify backend compilation or persistence.


Graph v4 authoring is covered by `VisualAutomationView.test.tsx` (multiple
triggers, shared C# problems, saved action dispatch, unsupported-version rejection and source mappings) and
`visual-graph.test.ts` (UTF-8 action IDs and duplicate-label disambiguation).
The fixture's `visualFixture.controls.preview` accepts an explicit backend preview
response for inspecting real source and maps without authenticating to HA. This
remains a fixture: it does not execute a real automation.


Solid Flow is installed without Bun patches. Touch regression coverage checks the
full final coordinates (rather than only movement), one-step undo/redo, repeated
gestures, diagnostic/selection updates, second-finger interruption, cancellation,
outside-finger release, and external graph replacement/removal. The real library
remains mounted; only its node-body touch path is owned by the adapter. Native
mouse/resize regressions still exercise D3. Physical touch hardware remains a
separate acceptance check.

For compiler-marker browser checks, open `tests/preview.html?view=library&shadow=1`.
`shadow=1` embeds the app beneath two open shadow roots, matching Home Assistant
event retargeting and exercising the pinned Monaco hover correction. Add
`MissingName` to a draft for a diagnostic from the in-memory fixture, then check the
red squiggle, Show Hover (Ctrl+K, Ctrl+I), the clickable error below the editor, and
clear/recheck behavior after removing the name.

Typed v4 coverage also lives in `typed-editor.test.tsx`, `NodeConfig.test.tsx`,
`TriggerConfig.test.tsx` and `visual-session.test.ts`. These mount real Solid Flow
handles/edges for mouse, touch, keyboard, category colors and Option/Alt deletion,
and cover immediate local diagnostics, saved-catalog generations, HA-shaped trigger
metadata subscriptions and cleanup, revision conflicts, and stale preview/source maps.

Open `tests/preview.html?view=visual&typed=1` for the checked-in source-derived rich
CLR catalog and its valid Read entity → Describe → Consume sample. Without `typed=1`,
the new-draft preview uses that same saved catalog, including its custom palette.
The fixture implements library catalog/list/read envelopes and real-shaped HA
trigger metadata. Library authoring and language fixture operations are described below. Preview graphs remain in-memory samples; they do not compile or execute C#.

Graph sessions refetch saved catalogs on window focus and the payload-free
`hass-sharp-libraries-changed` event. A changed generation invalidates backend
preview/source maps without creating an undo entry. If a library mutation rebuilt
the opened saved graph, the session preserves its local draft and surfaces a revision
conflict; export/reload resolves it. Save-time backend revision checks remain authoritative.


Open `tests/preview.html?view=library` (optionally `&shadow=1`) for the node library
screen with the real Monaco host, source-derived rich catalog, two files, typed
cards and XML documentation. `visual-check.html?view=library&width=390&theme=dark`
provides the narrow wrapper. The fixture supports revisioned create/save/rename/delete,
coherent draft response identities, representative nonempty member completions,
hover ranges, named-argument signatures, formatting and cross-file definition ranges.
Add `MissingName` to either draft for an exact source diagnostic. `window.visualFixture`
exposes `libraries`, graph `documents`, request `calls` and conflict/activation controls.
These deterministic language responses demonstrate the UI contract; they do not run
Roslyn or compile arbitrary edited source. Actual Roslyn behavior is covered by
`NodeLibraryLanguageServicesTests` and root's live Home Assistant acceptance.

`library-session.test.ts` checks coherent unsaved workspaces, previews, identity
rejection, optimistic CRUD/revisions, activation reporting and edit/save/delete races.
`NodeLibraryView.test.tsx` mounts the actual editor/session with only Monaco's host
doubled; it checks source-derived cards/docs, file and definition draft preservation,
exact cross-file diagnostic focus, save blocking, full screen model retention and
completion recovery. `editor-actions.test.ts` additionally covers library transport,
all provider invalidation, actual returned named-argument parameter identity,
source fingerprints, hover mapping and readable XML signature documentation.

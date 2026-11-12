# Frontend checks

Run `bun run test` and `bun run build` from the frontend directory.

For visual and browser checks, run `bun run dev --host 0.0.0.0` and open
`http://localhost:5173/tests/preview.html`. The development-only preview renders
the real app and Monaco with an in-memory sample API. It never calls Home
Assistant or a device. Its outer controls set a real 360px iframe viewport and
switch inherited Home Assistant theme tokens while the editor stays mounted.
Do not treat these samples as compiled or working runtime automations.

Check list/search, the action menu with Arrow keys/Escape, create/rename forms
with Enter, delete confirmation, and modal focus wrapping/return. In the editor,
check Source/Editor settings with arrows/Home/End, font size and Vim mode, typing,
Save and Ctrl/Cmd+S, and switching back to the list. Use Ctrl+M (Control+Shift+M
on Mac) to let Tab move focus out of Monaco. Check both themes at 360px and desktop
widths; long names should wrap without horizontal page scrolling.

For smoke-check evidence in browser devtools, the iframe exposes `previewCode()`
(real Monaco models) and `previewSaved()` (last in-memory saved source). This
fixture is outside the production build entry point. Live Home Assistant checks
still require the local instance described in the repository's AGENTS.md.

Visual editor shell coverage lives in `visual-graph.test.ts`, `VisualEditor.test.tsx`
and `VisualAutomationView.test.tsx`. Tests use the real pinned Solid Flow/D3
implementation. Happy DOM has no layout engine; the resize/edge tests supply only
measured DOM dimensions at that boundary. These tests do not replace desktop/mobile
browser acceptance. See `../docs/visual-editor-feasibility.md` for the adapter touch handling,
capability limits, and browser follow-up results.

Run `bun run build:preview` to build the mock-data responsive fixture; see the feasibility report for the temporary local HA static-route workflow. This is not a real HA integration test.

Run history checks are in `AutomationRuns.test.tsx` and `AutomationView.test.tsx`.
The fixture's BathroomHeating automation has sample history and accepts mock manual
runs. Its `window.runFixture` lets browser checks set `historyError`,
`dispatchError`, or the terminal `result`; no real HA service/device is called.
`window.previewErrors` collects page errors and rejected promises, including the
Monaco worker bootstrap regression. Open a code automation, type, and switch
Source/Run history: the real Monaco model must survive, with no worker error.

The preview build uses `/hass-sharp-static/visual-v2-check-baby/`.
For a narrow code-editor iframe, open its `tests/visual-check.html` with
`?view=code&width=390&theme=dark`. See `../docs/run-history-ui.md` for evidence and
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


Graph v2 authoring is covered by `VisualAutomationView.test.tsx` (multiple
triggers, C# sections, saved action dispatch, v1 upgrade and source mappings) and
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

For compiler-marker browser checks, open `tests/preview.html?diagnostics=1`.
Append `&shadow=1` to embed the app beneath two open shadow roots, matching Home
Assistant event retargeting and exercising the pinned Monaco hover correction.
This opens a deliberately broken C# source with a `MissingName` diagnostic from
an in-memory fixture. Check the red squiggle, Show Hover (Ctrl+K, Ctrl+I), the
clickable error below the editor, and clear/recheck behavior after removing the
name. The fixture intentionally returns no symbol hover so marker hover is tested
independently from Roslyn QuickInfo.

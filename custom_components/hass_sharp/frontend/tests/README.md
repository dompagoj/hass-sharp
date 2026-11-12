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
browser acceptance. See `../docs/visual-editor-feasibility.md` for the local patch,
capability limits, and browser follow-up results.

Run `bun run build:preview` to build the mock-data responsive fixture; see the feasibility report for the temporary local HA static-route workflow. This is not a real HA integration test.

Run history checks are in `AutomationRuns.test.tsx` and `AutomationView.test.tsx`.
The fixture's BathroomHeating automation has sample history and accepts mock manual
runs. Its `window.runFixture` lets browser checks set `historyError`,
`dispatchError`, or the terminal `result`; no real HA service/device is called.
`window.previewErrors` collects page errors and rejected promises, including the
Monaco worker bootstrap regression. Open a code automation, type, and switch
Source/Run history: the real Monaco model must survive, with no worker error.

The preview build is currently hosted under `/hass-sharp-static/run-check-9b24/`.
For a narrow code-editor iframe, open its `tests/visual-check.html` with
`?view=code&width=390&theme=dark`. See `../docs/run-history-ui.md` for evidence and
remaining live-HA verification. Copy preview output only to a unique temporary
static subdirectory, leaving the live panel bundles untouched, and remove it after
checking.

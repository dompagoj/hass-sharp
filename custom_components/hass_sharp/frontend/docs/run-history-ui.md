# Run history frontend (Task 3 continuation)

Based on integrated commit `398905a` and `docs/backend/run-history.md` contract v1.
This change touches only the frontend; scheduler, graph backend, and C# runtime
are unchanged. No live automation or device was dispatched during verification.

The code editor has a Run history tab and a Run now shortcut. The panel discovers
the file's stable automation ID and declared classes/actions from the existing
script-list API. Saving refreshes these declarations without replacing the Monaco
model. History is keyed by automation identity, not filename or source revision;
changing identity resets pagination and request feedback. Missing identity and
non-administrator access are shown explicitly.

Manual dispatch uses `hass_sharp/run_now` with automationId/className/method. An
accepted response displays its run ID and explicitly asks the reader to check the
record's status. It never substitutes acceptance for Succeeded. Runs use the last
saved code; unsaved edits stay in Monaco. The backend remains authoritative about
whether an action allows manual calls. A C# action completing does not prove a
physical device effect.

History uses 20-record offset pages without inventing a total. It shows every
backend status, entity/manual/time/startup cause, cause details, received time and
duration; expandable details include revision, run ID, planned/start/end times.
Times are displayed locally with original UTC values in `datetime` and tooltips.
Errors and cause details render as plain text. Newest/newer/older and refresh,
loading/error/retry/empty states are included. Active records on the newest page
refresh every two seconds while visible; an accepted attempt absent from that
page is checked for up to one minute. Query errors stop automatic refresh until
retry. Offset pages can move as records arrive, as the panel explains.

## Monaco ErrorEvent reproduction and fix

Opening the real Monaco editor in the mounted Browser reproduced the reported
ErrorEvent. Captured worker error:

```
Uncaught TypeError: Failed to resolve module specifier
"../../../base/common/worker/webWorkerBootstrap.js".
Invalid relative url or base scheme isn't hierarchical.
```

Monaco 0.57's default blob bootstrap could not resolve that relative import. The
editor now supplies Vite's explicitly bundled editor worker. The production Vite
base is `/hass-sharp-static/`, matching HA's registered static route; the emitted
worker is `/hass-sharp-static/assets/editor.worker-*.js`. Production build output
and file presence were checked. The browser fixture fetched its nested worker and
captured zero errors/rejected promises afterward, including opening and typing in
a code editor and creating another C# automation.

The optional Monaco fallback source and the mock fixture source now use the
current declarative Configure/AutomationAction API with a Task-returning action.
The backend's empty new-file template is unchanged to keep this task frontend-only.

## Verification (2026-11-12)

- Baseline: 48 frontend tests passed.
- TDD: six run-panel behavior tests failed against the empty component, then
  passed; editor integration failed without the history tab, then passed.
  The saved-action refresh regression failed with the prior inactive-file-only
  invalidation, then passed with script metadata invalidation.
- Final frontend suite: 59 tests across 10 files; `bun run test`.
- TypeScript/production build: `bun run build`; preview: `bun run build:preview`.
- Separate read-only review found no Critical/Important/Minor issues. It
  independently ran the then-current 57-test suite; two additional regressions
  were subsequently added for saving and dispatching from an older page.
- Browser: selected OpenMausBot Chromium, existing local HA static route, unique
  temporary fixture directory. 390px light/dark and 1280px light/dark layouts had
  no page-level horizontal overflow. Screenshots inspected for mobile dark and
  desktop light. Verified pending→Succeeded mock dispatch, accepted ID, pagination
  to records 21–24, history error/retry, manual dispatch rejection, and one preserved
  Monaco model through typing and source/history switches. Final error capture
  stayed empty. The fixture performs no backend or physical-device calls.
- Screenshots: `/home/dompa/workspace/hass-sharp-run-checks/mobile-dark.png` and
  `/home/dompa/workspace/hass-sharp-run-checks/desktop-light.png`.

Live `http://localhost:5001/hass-sharp/automations` redirected to HA sign-in. There
is no saved login, so authenticated panel execution, actual dispatch/history
roundtrips, runtime reload, database failures, and device effects remain
unverified. This report does not treat a mock browser result as live HA proof.
Temporary hosted fixture files were removed after checking.

# Run history frontend

Based on integrated commit `398905a` and `docs/backend/run-history.md` contract v1.
This change touches only the frontend; scheduler, graph backend, and C# runtime
are unchanged. No live automation or device was dispatched during verification.

Automations are visual only (hand-written C# automations were removed in the node
editor overhaul, Task 24). Each saved automation's run history is the Runs tab of
the visual editor's bottom drawer (`components/AutomationRuns.tsx`), keyed by the
automation's stable identity, not its name or source revision; changing identity
resets pagination and request feedback. Non-administrator access is shown
explicitly.

Runs are started from the editor's top bar (`visual/RunNow.tsx`) and the automations
page, both through `components/runDispatch.ts`, which calls `hass_sharp/run_now` with
automationId/className/method. An accepted run is remembered so the Runs tab polls
until it has finished; acceptance is never shown as Succeeded. Runs use the last
saved version; unsaved edits are not included. The backend remains authoritative
about whether a trigger allows manual runs. A run completing does not prove a
physical device effect.

History uses 20-record offset pages without inventing a total. Newest/newer/older
and refresh, loading/error/retry/empty states are included. Active records on the
newest page refresh every two seconds while visible; an accepted attempt absent
from that page is checked for up to one minute. Query errors stop automatic
refresh until retry. Offset pages can move as records arrive, as the panel explains.

## Run list (run history v2, node editor overhaul Task 8)

Built against contract C9 (`hass_sharp/run_history` version 2). Each run is one
compact row, a real `<button>` with `aria-expanded`:

- status as text plus a coloured marker whose shape also differs (Interrupted
  diamond, Stopped square, Skipped/Pending hollow, Running pulsing unless reduced
  motion is requested);
- `actionLabel` (the trigger node label), then the cause in words ("Home
  Assistant trigger", "Entity change", "Schedule", "Manual run", "Startup") and a
  one-line trigger summary from `components/TriggerSummary.tsx`, for example
  `climate.outside_bathroom_vtrv: off → heat`;
- for runs that did not succeed, the first line of `error`, which carries the
  cancel reason for Cancelled runs ("Restarted by a newer trigger");
- received time relative ("5 minutes ago", refreshed every 30 s; absolute local
  time in the tooltip, UTC in `datetime`) and duration.

Expanding shows the full error or cancel reason, the structured trigger summary,
run metadata (run ID with a Copy button where the Clipboard API exists, graph
revision `visualRevision`, automation revision, planned/received/started/ended,
duration) and the raw record in a collapsible tree (`components/JsonTree.tsx`).
Expansion is keyed by run ID, so an open row stays open while polling replaces it.

A Status select sends the server-side `status: [value]` filter and returns to the
first page; "All statuses" omits the parameter. An accepted manual dispatch clears
the filter as well as the page so the new run is visible. Rows with `hasTrace` and
a `visualId` link "View trace" to `/visual/{visualId}/runs/{runId}` (the route
arrives with the trace viewer).

Until the backend ships v2, rows in the v1 shape still render: the label falls back
to `action`, rows without `trigger` show only the cause, rows without `hasTrace`
show no trace link. The v1 `details` string (the raw trigger payload) is no longer
displayed. The v1 command schema rejects the `status` key, so filtering only works
once the backend accepts it.

Checked in the dev preview with temporary v2 sample rows (not committed): desktop
light and 360 px dark, no horizontal overflow at 360 px.

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

The Monaco editor is now used only by the node library editor; the hand-written
automation editor these notes once described is gone.

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

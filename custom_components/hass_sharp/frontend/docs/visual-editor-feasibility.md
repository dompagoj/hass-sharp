# Visual editor shell feasibility — 2026-11-12

Task 6 **frontend subset**, based on `49bde15`. This is a draft authoring shell, not the completed visual automation platform. Backend files, the running Home Assistant container, and backend deployment were not changed.

## Delivered boundary

New Automation offers C# (existing create endpoint) or Visual (named local-only draft, no server write). Visual drafts have an entity-change/condition/service sample, palette additions including delay and custom C#, editable labels/configuration, mouse/touch canvas movement, resizing, keyboard movement, explicit connections and deletion, bounded undo/redo, JSON preview/export, and a static C# example. Connections reject dangling endpoints, duplicates, self-links, links into triggers, and cycles. These checks protect the authoring shell; they are **not** runtime/type/execution validation.

`src/visual/graph.ts` defines the version-1 JSON document. It stores node IDs/kinds, labels, position, size, string configuration, and edge endpoints. Canvas selection, measured dimensions, JSX and library stores are excluded. The adapter copies geometry into its own stores and commits completed edits back into immutable graph history. One drag is one history entry; inspector typing currently produces an entry per input event (up to 100 retained edits).

The C# preview is a fixed sample independent of graph data. There is **no graph-to-C# generator, source mapping, Roslyn validation, compilation, execution, graph import, server persistence, atomic graph/source save, or C# conversion**. Export keeps a JSON copy; this shell does not reopen it yet. Time/branch/response/error/concurrency nodes and runtime semantics are deferred. Custom C# text is retained only as graph data.

Drafts exist only while the editor is mounted. The Solid router asks before leaving; browser unload warns after edits. Outer Home Assistant sidebar SPA navigation can unmount this panel without those hooks, so export before leaving. Nothing is claimed saved to Home Assistant.

## Dependency spike and decision

The unscoped [`solid-flow` 1.0.4](https://github.com/miguelsalesvieira/solid-flow) was installed and characterized first. A real rendered-component test changed the label and position while keeping one node: the component still displayed the original label. Source investigation found the length-only synchronization gate, position mutation without an `onNodesChange` drag completion, mount-only handle measurements, mouse-only node gestures, and global `:root` CSS injection. It was removed after the spike.

Selected [`@dschz/solid-flow` 0.2.6](https://github.com/dsnchz/solid-flow), the SolidJS 1.x line, pinned exactly. All third-party canvas types/events remain behind `FlowAdapter`. MIT/ISC notices are retained under `licenses/`; no dependency patch is applied.

The original implementation used a Bun patch because the port's draggable effect
recreates D3's element handlers on each node update. That drops the active touch
gesture after the first move. **The patch file and `patchedDependencies` have now
been removed**; Solid Flow remains the unmodified, exactly pinned 0.2.6 release.
The [npm version listing](https://www.npmjs.com/package/%40dschz/solid-flow?activeTab=versions)
still identifies 0.2.6 as stable (checked 2026-11-12); the next tag is a separate
prerelease line, not a drop-in stable fix.

`FlowAdapter` owns single-finger node-body dragging through native capture events.
It uses public `useSolidFlow().screenToFlowPosition()` for pan/zoom coordinates
and updates the controlled `nodes` prop. It neither changes library code nor
imports private modules. Mouse movement, connection handles, resize controls,
inputs and canvas pan/zoom remain with the library. Selection/diagnostic updates
preserve active local touch geometry; a completed drag creates one history edit.
A second finger ends the drag at the last single-finger position; remaining
fingers are ignored until release. Terminal events are observed on the document
so an outside finger lifting last cannot strand a gesture. External graph
replacement cancels the gesture, including when its target is detached.

Drag-edge auto-pan stays disabled because the upstream interruption path can
leave its animation running. Users can pan the background or use zoom/fit.

| Concern | Evidence and implementation | Remaining verification |
| --- | --- | --- |
| Same-count external updates | Real-library DOM test updates label, position and size, then restores coordinates | Browser rendering passed |
| Drag retention | Mouse drives real D3 events; touch drives native adapter events with the real library mounted. Full endpoints, interruption, cancellation, repeated gestures and undo/redo are asserted | Mouse passed in browser; physical touch unverified |
| Resized nodes | Live resize-handle width and final domain dimensions tested with a DOM measurement fixture; duplicate inline size removed | Browser size/edge layout inspected |
| Keyboard | Native boundary blocks ancestor shortcuts; adapter handles node Enter/Escape/arrows, inspector handles connections/sizing, editor supports undo/redo/Delete for nodes and edges | Fixture native keys passed; actual HA unverified |
| CSS isolation | Upstream CSS imported as inline text and enclosed in `@scope (.hs-flow)`; application styles prefixed with `.hs-app`; no global CSS import | Outside sentinel and light/dark screenshots passed |
| Narrow layout | Responsive rules stack palette/canvas/inspector and bottom panels at 700px | 390px/1280px screenshots and overflow checks passed |

CSS `@scope` support is required (use a current browser). The browser acceptance gate was subsequently checked through an isolated mock-data fixture on the existing HA static route (see below). DOM tests alone do not prove visual isolation, colors, edge alignment or mobile usability.

## Verification and browser access

Run from this frontend directory: `bun install --frozen-lockfile`, `bun run test`, `bun run build`. Build outputs are ignored and were produced only inside this isolated worktree.

The selected OpenMausBot Browser was used first. Opening the local fixture at `http://localhost:5176/tests/preview-app.html` failed with `ERR_SOCKS_CONNECTION_FAILED`: the browser's service allowlist treated the native-shell process as a service no bot started. A LAN-address attempt was initially rejected by automatic approval review as an unverified destination/exposure concern. Host network and listener checks subsequently proved `192.168.100.53` is this VM and port 5176 belongs to this worktree's Vite process; a further browser attempt still hit the same service allowlist. No alternate browser or external tunnel was used. The temporary preview server was stopped after testing. This initial blocker was subsequently resolved using the existing static route, without making port 5176 public.

### Completed browser follow-up

`bun run build:preview` builds the existing mock-data App fixture and a responsive iframe wrapper. The output was copied into a unique temporary `visual-check-6c76542` subdirectory of the existing HA-served `www` directory; the live panel bundle and backend files were untouched. The selected Browser opened it at `http://localhost:5001/hass-sharp-static/visual-check-6c76542/tests/visual-check.html`. The fixture makes no real HA service calls. Temporary hosted files were removed after checking.

Verified in the mounted Chromium browser:

- 1280×720 desktop and 390×720 iframe viewports in light and dark themes; also checked 1440px layout metrics. No page-level horizontal overflow. On narrow screens palette, canvas, inspector, connections and code preview stack correctly; code scrolls within its own region.
- New Automation → Visual → named draft opens the real frontend shell. Leaving shows the discard dialog, and explicit discard returns to the mock automation list.
- Browser-dispatched mouse drag changed domain position `(60,100)` to `(140,140)`; native ArrowRight moved to `(150,140)`; Ctrl+Z and Undo restored the original geometry.
- Changing inspector Width to 300 produced graph width 300 and rendered width 225 at zoom 0.75. Edges remained connected in the inspected layout. Fit View made the complete graph visible on mobile.
- An outside `solid-flow__node` sentinel retained `position: static`; CSS scope was supported by this browser.
- Found and fixed an unreadable light hover background on dark zoom controls. All canvas buttons now use theme-aware hover/border/text colors and 44×44px targets; verified computed styles and screenshots afterward.

Screenshots are preserved at `/home/dompa/workspace/hass-sharp-visual-checks/`: `desktop-light.png`, `desktop-dark.png`, `mobile-light.png`, `mobile-dark.png`, `mobile-dark-canvas.png`, `mobile-code-preview.png`.

Remaining: physical phone/touch hardware and the actual signed-in HA panel are not verified. Synthetic single-touch/multitouch behavior is covered by the real-library DOM tests. The responsive fixture verifies this frontend in a browser, not the running HA integration or generated-code/backend behavior.

Final local result: the original patched installation from the frozen lockfile installed 158 packages; all **48 frontend tests across 9 files passed**, and `tsc && vite build` passed. `git diff --check` passed. The temporary port 5176 listener was stopped and its absence verified. A separate read-only code review found no Critical issues; all reported Important code findings have regression coverage and were fixed. The subsequent browser follow-up above completed local responsive/theme acceptance; physical touch and signed-in HA integration remain unverified.


## Dependency patch removal follow-up, 2026-11-12

Base: `e7ee0ac`, isolated `fix/solid-flow-without-patch` branch. The main integration
checkout was not modified. Initially, removing the patch appeared to pass the
existing movement tests: they asserted only that x increased. Strengthening the
single-touch endpoint reproduced the real bug: expected `(220,180)`, got
`(140,140)` after two move events at zoom 0.75. The adapter fix passes that test.
The native mouse test includes its threshold-crossing move before checking the
full endpoint, preserving the library's existing drag-threshold behavior.

Fresh `bun install --frozen-lockfile` installs 158 unmodified packages. The full
frontend suite passes 88 tests across 11 files; TypeScript/production and preview
builds pass. The preview build retains its existing large-chunk advisory. There
is no patch file, patch declaration, or replacement vendored library code.

A fresh read-only review found two gesture lifecycle defects (last finger ending
outside canvas, and graph replacement detaching the original node). Both have
red-to-green regression tests and pass independent reviewer probes. The review
reported no remaining Critical/Important findings.

Mounted Chromium checked the final `preview-app-DBvCgcTE.js` fixture bundle at
`http://localhost:5001/local/drag-no-patch-check-baby/tests/visual-check.html`.
It was copied into the container's separate `/config/www` directory, never the
integration bind mount. The iframe revision query was changed to bypass HA's
cached earlier preview HTML; its loaded bundle URL was verified explicitly.

- At zoom 0.75, two synthetic single-touch moves totaling 60x30 client pixels
  moved graph `(60,100)` to `(140,140)`; one Undo restored `(60,100)` and Redo
  restored `(140,140)`.
- A second-finger interruption retained `(220,180)` and one Undo returned to
  `(140,140)`. Ending the last finger outside the canvas then starting a new
  gesture correctly reached `(220,140)`.
- Native mouse events (including the initial drag-threshold crossing) moved
  `(140,140)` to `(220,180)` and one Undo restored the original point.
- Native resize-handle events changed 220x112 to 260x132; Undo restored 220x112.
  Both graph edges remained rendered. Fit View worked after the gestures.
- The 390px dark and 1200px light views had matching client/scroll widths and no
  captured page errors. Screenshots and structured results are at
  `/home/dompa/workspace/hass-sharp-no-patch-checks/`.

The temporary container fixture was removed after checking. No container restart,
HA setting change, real service call, main-checkout edit, push or PR was performed.
Physical touch devices and the signed-in HA panel remain unverified: browser
inputs here are synthetic events through the real frontend and library, backed
by the native-event DOM tests, not a physical phone acceptance run.


### Touch ownership correction

Document `touchend`/`touchcancel` events are ignored while a primary gesture is
active unless `changedTouches` includes its tracked identifier. An unrelated
finger ending outside the canvas does not commit or interrupt the primary drag.
After explicit multi-touch cancellation, the final release still clears ownership.
Two real-library regressions (touchend and touchcancel) failed on premature commit
before this correction; both now require continued movement and exactly one
commit on primary release. Final frontend suite: 90 tests across 11 files pass;
TypeScript and production build pass. This follow-up uses DOM tests; the browser
visual evidence above predates this ownership correction.

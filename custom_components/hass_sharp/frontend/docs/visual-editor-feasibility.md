# Visual editor shell feasibility — 2026-11-12

Task 6 **frontend subset**, based on `49bde15`. This is a draft authoring shell, not the completed visual automation platform. Backend files, the running Home Assistant container, and backend deployment were not changed.

## Delivered boundary

New Automation offers C# (existing create endpoint) or Visual (named local-only draft, no server write). Visual drafts have an entity-change/condition/service sample, palette additions including delay and custom C#, editable labels/configuration, mouse/touch canvas movement, resizing, keyboard movement, explicit connections and deletion, bounded undo/redo, JSON preview/export, and a static C# example. Connections reject dangling endpoints, duplicates, self-links, links into triggers, and cycles. These checks protect the authoring shell; they are **not** runtime/type/execution validation.

`src/visual/graph.ts` defines the version-1 JSON document. It stores node IDs/kinds, labels, position, size, string configuration, and edge endpoints. Canvas selection, measured dimensions, JSX and library stores are excluded. The adapter copies geometry into its own stores and commits completed edits back into immutable graph history. One drag is one history entry; inspector typing currently produces an entry per input event (up to 100 retained edits).

The C# preview is a fixed sample independent of graph data. There is **no graph-to-C# generator, source mapping, Roslyn validation, compilation, execution, graph import, server persistence, atomic graph/source save, or C# conversion**. Export keeps a JSON copy; this shell does not reopen it yet. Time/branch/response/error/concurrency nodes and runtime semantics are deferred. Custom C# text is retained only as graph data.

Drafts exist only while the editor is mounted. The Solid router asks before leaving; browser unload warns after edits. Outer Home Assistant sidebar SPA navigation can unmount this panel without those hooks, so export before leaving. Nothing is claimed saved to Home Assistant.

## Dependency spike and decision

The unscoped [`solid-flow` 1.0.4](https://github.com/miguelsalesvieira/solid-flow) was installed and characterized first. A real rendered-component test changed the label and position while keeping one node: the component still displayed the original label. Source investigation found the length-only synchronization gate, position mutation without an `onNodesChange` drag completion, mount-only handle measurements, mouse-only node gestures, and global `:root` CSS injection. It was removed after the spike.

Selected [`@dschz/solid-flow` 0.2.6](https://github.com/dsnchz/solid-flow), the SolidJS 1.x line, pinned exactly. All third-party canvas types/events remain behind `FlowAdapter`. MIT/ISC notices are retained under `licenses/`; the patch does not remove upstream notices.

The port needed a narrow local Bun patch in both its JSX and compiled JS exports (`patches/@dschz%2Fsolid-flow@0.2.6.patch`). Its draggable effect recreated D3's element gesture handlers on each geometry update. Mouse drags survived via window handlers, but touch-end used a new empty gesture map and never committed. Memoizing only primitive drag configuration prevents rebinding during movement. A single-touch regression failed before the patch and passed afterward. Bun applies this patch from `patchedDependencies` on install; keep the version pinned until upstream behavior is rechecked.

The adapter additionally commits final geometry after touch-end/cancel in a capture-phase microtask: upstream intentionally omits `onDragStop` when a second finger aborts a drag. This prevents canvas/JSON divergence on interrupted gestures. It avoids duplicate history via graph no-op detection. Drag-edge auto-pan is disabled because the upstream abort path can leave its animation running; users can pan the background and use zoom/fit controls.

| Concern | Evidence and implementation | Remaining verification |
| --- | --- | --- |
| Same-count external updates | Real-library DOM test updates label, position and size, then restores coordinates | Browser rendering passed |
| Drag retention | Mouse, single-touch and multitouch interruption drive real D3 events; JSON position changes, prior snapshot stays intact | Mouse passed in browser; physical touch unverified |
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

Final local result: a fresh installation from the frozen lockfile installed 158 packages with the patch; all **48 frontend tests across 9 files passed**, and `tsc && vite build` passed. `git diff --check` passed. The temporary port 5176 listener was stopped and its absence verified. A separate read-only code review found no Critical issues; all reported Important code findings have regression coverage and were fixed. The subsequent browser follow-up above completed local responsive/theme acceptance; physical touch and signed-in HA integration remain unverified.

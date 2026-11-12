# Visual API frontend integration

Implements the v1 contract in `docs/backend/visual-automations.md` from base
63db7d1. Backend files and live production panel bundles are unchanged.

- `visual/api.ts` types and unwraps versioned WebSocket results. Visual documents
  never use the C# file APIs; save submits graph and expectedRevision only.
- `visual/session.ts` owns debounced preview, revision/baseline state, read/save
  failures, conflict recovery and activation feedback. Old previews and reads
  cannot replace newer results. Save snapshots preserve edits made in flight.
- Optional node label/position/size fields receive display defaults; read/save preserves the original graph metadata.
- The editor previews server source with clickable source lines and diagnostic
  navigation, marks invalid nodes, and blocks save until the current preview is
  valid. Warnings are shown without blocking save. Name changes save under the
  same document ID. Graph JSON remains exportable, including during conflicts.
- The list merges visual documents with C# entries. Delete uses the displayed
  revision; conflicts preserve the item and offer a list reload. Rename is in the
  visual editor. New IDs are UUID-based and are not reused on deletion.
- Save reports persistence separately from activation. Saved documents expose
  VisualAutomation.Run and history using the returned automationId. Opening a
  different identity immediately removes the prior document's runtime controls.
- Route and browser-close guards compare graph content with its baseline; undoing
  to the baseline, successful save and unchanged drafts do not warn.

## Verification on 2026-11-12

From `custom_components/hass_sharp/frontend`:

- `bun run test`: 77 tests across 11 files, passing (baseline: 59 tests).
- `bun run build`: TypeScript and production bundling passed.
- `bun run build:preview`: fixture build passed, with Vite's large-chunk advisory.
- `git diff --check`: passed.

A fresh generic reviewer found a pending A→B→A read race and prior-document Run
now controls during a different read. Clearing the document at read start fixes
both; the regression failed before the fix and passed afterwards.

Mounted Browser checks used the in-memory API fixture on HA's existing static
route, `/hass-sharp-static/visual-api-check-baby/tests/visual-check.html`. The
fixture subdirectory was removed after checking; no container restart or
recreation was performed. The real frontend, router and pinned Flow adapter were
used. Browser evidence covers:

- 390px dark: preview, save/revision, stable saved URL, mock Run now followed by
  Succeeded history, conflict retention and explicit reload/discard, invalid
  save blocking, node error badge and diagnostic-to-source-line selection.
- Activation failure shows "Saved, but not activated" and preserves revision.
- Responsive save controls wrap with main/editor/action scroll widths matching
  client widths at 390px, including saved+dirty status. Earlier overflow was
  found in a screenshot and corrected. Desktop light was also checked.
- `previewErrors` remained empty. The fixture does not compile C#, persist across
  a browser reload, contact the runtime, or call devices.

Live HA at localhost:5001 redirected to its login page. The mounted browser has
no saved localhost login. Authenticated real backend preview, save/reopen/delete,
activation and run/history acceptance remain unverified. Docker inspect was used
read-only to confirm the static mount; HA was not restarted or reconfigured.

## Boundaries and deferred polish

MID's separate graph parity work owns additional trigger/config/port authoring.
This change keeps the existing v1 palette and explains graph-level limits.
No schema conversion, source regeneration or graph-field filtering is performed.
A selected node ID can become absent after switching graphs (the inspector then
asks the user to select a step). If a saved graph loads but preview is offline,
its read source remains in session.document but the UI reports the preview error
instead of showing a labelled saved-source fallback. These are deferred polish,
not silent overwrites or loss of the saved graph/source pair.

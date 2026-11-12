# Visual graph v2 frontend

New visual drafts use graph version 2. Saved v1 documents remain v1, retain their
single `VisualAutomation.Run` action, and can be edited normally. The explicit
**Upgrade to graph v2** button enables additional triggers and C# sections. The
upgrade changes action identities when saved; it is undoable before saving.
The API/document envelope remains version 1.

The trigger inspector supports entity, daily (HH:mm plus explicit time zone), once
(ISO timestamp with explicit offset), manual and startup types, plus Single,
Restart, Queued and Parallel invocation modes. V2 also supports boolean manual and
startup overrides and optional C# entity/schedule expressions. Inactive config
fields and unknown stored fields are preserved. Clearing an optional expression or
startup override removes the field and restores backend defaults.

Trigger IDs are read-only and stable across label/config/position changes. Run
now derives its methods from the **saved** graph, using uppercase UTF-8 hexadecimal
IDs for v2; manual-disabled triggers are omitted. Duplicate labels include their
IDs in the action chooser. Unsaved additions/deletions/upgrades do not change saved
actions. History continues to use the backend-returned automationId.

Graph imports/members are native C# text sections, matching the existing custom
node editing behavior. Backend source and diagnostics remain authoritative.
Synthetic `$imports`/`$members` mappings navigate to the corresponding editor;
node selection highlights every mapped range for that node. No backend files or
existing scripts were migrated or changed.

## Edge ports

Edges carry the backend's optional `port`. Conditions offer **If true**, **If false**
and **On error** handles; other non-trigger steps offer **Next** and **On error**;
triggers offer only **Next**. New edges write `true`, `false` or `error` and omit the
port for `next`. A stored port-less edge on a condition is its true branch and is
kept byte-for-byte. Each output holds one edge, so a used output (on the canvas or
in the inspector's **Output** picker) accepts no second edge, and `next` and `true`
on one condition count as the same output. Cycles stay rejected through every port.
Different outputs may lead to the same step. The inspector defaults to the first
free output.

## Verification, 2026-11-12

- Baseline: 77 frontend tests. Final: `bun run test`, 83 tests across 11 files pass.
  Regression tests were run failing before implementation. Coverage includes two
  triggers plus imports/members save/reload, all trigger kinds and concurrency
  modes, boolean/expression encoding, v1 preservation/explicit upgrade, stable
  Unicode UTF-8 methods, saved-vs-draft action lists, custom C#, duplicate labels,
  and synthetic/multiple source-map navigation.
- `bun run build`: TypeScript and production build pass.
- `bun run build:preview`: passes, with the existing large-chunk advisory.
- `git diff --check`: passes. Fresh read-only review found no Critical/Important
  issues; its duplicate-label finding was fixed with a red-to-green regression.

Mounted Browser checked a unique temporary fixture at the existing HA static
route `/hass-sharp-static/visual-v2-check-baby/tests/visual-check.html`:

- Authored a daily Restart trigger, a second manual trigger sharing the service
  path, and imports/members. Saved and discarded a later member edit by reloading.
- Both saved actions dispatched independently and appeared as Succeeded in the
  mock history. No actual HA service/device was invoked.
- Passed the exact browser-authored JSON to the real C# VisualGenerator in a
  temporary CLI: valid compilation, both expected action methods, imports/members
  mappings, multiple ranges per trigger, and real VisualStore save/reopen with
  identical graph/source. The CLI uses existing backend code without modifying it.
  NuGet reported NU1900 because its vulnerability feed was unreachable; the
  cached dependencies built and the check exited successfully.
- Injected that real preview response into the browser fixture: displayed source
  exactly matched the generator output; source line 4 focused C# members and
  highlighted the matching source line. Widths were 390/390 in dark mode and
  1440/1440 in light mode; `previewErrors` stayed empty.

Browser evidence is saved outside git at
`/home/dompa/workspace/hass-sharp-visual-v2-checks/` (graph.json, preview.json and
screenshots). Temporary hosted fixture files were removed after checks. Live
panel assets were not replaced; HA was not restarted or reconfigured.

Live HA opens a sign-in page in the mounted browser. Authenticated HA WebSocket
preview/save/reopen, real runtime dispatch/history, and physical device effects
remain unverified. The offline generator/store checks and mock browser history
are separate evidence, not a claim of a signed-in end-to-end run.

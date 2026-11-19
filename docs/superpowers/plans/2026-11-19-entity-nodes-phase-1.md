# Entity nodes, phase 1: the step library describes, dragging adds

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the entity editor the Entity group leads the step library; in both editors clicking a library item shows what it is in the right panel, and only a drag (or quick-add) puts a step on the canvas.

**Architecture:** The order is a per-document rank applied in `catalogPalette`, which the library and quick-add both read. A clicked item is a `previewKind` signal in `VisualEditor`; the `Inspector` gets a fifth mode that renders a read-only `KindPreview` built from a prototype node. The click-to-add path is removed, not hidden.

**Tech Stack:** SolidJS, TypeScript 7, vitest 5 with happy-dom, `@dschz/solid-flow`. All paths below are under `custom_components/hass_sharp/frontend/` unless they start with `docs/`.

**Spec:** `docs/superpowers/specs/2026-11-19-entity-nodes-design.md` (Decisions 7; "Decisions (mine)" on the palette).

## Global Constraints

- Frontend only. `visual_node_catalog.json` is not edited: it is part of the generated C# sources.
- The automation editor changes in exactly one way: clicking a library item no longer adds it. Its category order stays the catalog's.
- No click, Enter or Space on a library item creates a node. Creating a node is a drag onto the canvas or quick-add (`A`, double-click, right-click, the rail's "Add step").
- User-facing strings are hardcoded English in the components, as everywhere in this frontend. Exact copy:
  - library hint: `Click for details · drag onto the canvas`
  - preview hint: `Drag onto the canvas, or press A.`
- Run from `custom_components/hass_sharp/frontend`: `bun run test` (all 70 files pass before this phase: 1835 tests) and `bunx tsc --noEmit`.
- Commit messages are one sentence saying what the user now gets, ending with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. ~~A kind this Home Assistant cannot create.~~ Dropped while executing: without a pending connection `canCreateNode` refuses only kinds the catalog lacks, and those already render nothing (item 3). `KindPreview` keeps a one-line guard for a `createNodeAt` that returns the same graph; nothing can reach it today, so it has no test.
2. **A step is selected, then a library item is clicked:** the panel shows the preview, no card stays selected, and pressing Delete on the canvas removes nothing. Test in Task 3.
3. **The previewed kind leaves the catalog** (a node library saved elsewhere drops it): the panel falls back to the overview. Test in Task 2.
4. **The search hides the previewed item:** the preview stays until something is selected or it is closed. Test in Task 3.
5. **Keyboard:** Enter or Space on a focused item previews it and focus stays on the item, so the next Tab reaches the next item, not the panel. Test in Task 3.

---

### Task 1: Entity group first in an entity definition's library

**Files:**
- Modify: `src/visual/graph.ts:34-49`
- Test: `tests/palette.test.tsx`

**Interfaces:**
- Produces: `catalogPalette(catalog?, document?)` keeps its signature and item shape; its result is now ordered. Exported `paletteOrder: Partial<Record<GraphDocument, string[]>>` with the single entry `{ entity: ['Entity'] }`.

- [ ] **Step 1: Write the failing tests** in `tests/palette.test.tsx`

```tsx
it('an entity definition lists the Entity group first, then the catalog\'s order with Trigger leading it', () => {
  const categories = [...new Set(catalogPalette(nodeCatalog, 'entity').map(item => item.category))]
  expect(categories[0]).toBe('Entity')
  expect(categories.slice(1)).toEqual([...new Set(catalogPalette().map(item => item.category))])
  const kinds = catalogPalette(nodeCatalog, 'entity').map(item => item.kind)
  const firstOther = kinds.findIndex(kind => nodeCatalog.nodes[kind].category !== 'Entity')
  expect(kinds[firstOther]).toBe('trigger')
  // Within the group, the catalog's own order.
  const entityKinds = Object.keys(nodeCatalog.nodes).filter(kind => nodeCatalog.nodes[kind].category === 'Entity')
  expect(kinds.slice(0, firstOther)).toEqual(entityKinds)
})

it('an automation keeps the catalog\'s order', () => {
  const expected = Object.keys(nodeCatalog.nodes).filter(kind => !nodeCatalog.nodes[kind].documents)
  expect(catalogPalette().map(item => item.kind)).toEqual(expected)
})

it('the step library and quick-add of an entity definition both start with the Entity group', () => {
  const root = host()
  dispose = render(() => <NodeLibraryPanel document="entity" collapsed={false} onCollapsedChange={() => {}} onPreview={() => {}} onQuickAdd={() => {}} />, root)
  expect(root.querySelector('.hs-lib__category')!.textContent).toBe('Entity')
  dispose()
  dispose = render(() => <QuickAdd document="entity" x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={() => {}} />, root)
  expect(root.querySelector('[role="option"]')!.getAttribute('aria-label')).toBe(`Create ${label('entity_state')}`)
})
```

The third test names `onPreview`, which Task 3 adds; until then pass `onAdd={() => {}}` and let Task 3 rename it.

- [ ] **Step 2: Run** `bun run test tests/palette.test.tsx`. Expected: the first and third fail (`'Hass'` is first); the second passes.

- [ ] **Step 3: Implement.** In `src/visual/graph.ts` add `paletteOrder` beside `documentDescriptions` and make `catalogPalette` stable-sort its items so the document's listed categories come first, in the listed order; every other item keeps its catalog position. If quick-add groups or ranks its options itself and so does not show the Entity kinds first, fix that in `src/visual/QuickAdd.tsx` without changing its ranking of a typed search.

- [ ] **Step 4: Run** `bun run test tests/palette.test.tsx` and `bunx tsc --noEmit`. Expected: pass, no type errors.

- [ ] **Step 5: Commit** `src/visual/graph.ts`, `tests/palette.test.tsx` (and `QuickAdd.tsx` if touched).

---

### Task 2: A read-only description of a kind in the right panel

**Files:**
- Create: `src/visual/KindPreview.tsx`
- Modify: `src/visual/Inspector.tsx` (props at 22-73, `hasContent` at 88, the fallback chain at 157-160, `PeekBar` at 204-231)
- Modify: `src/styles/` (the stylesheet that holds `.hs-insp-head`; add `.hs-insp-preview` rules there)
- Test: create `tests/kind-preview.test.tsx`

**Interfaces:**
- Consumes: `createNodeAt(graph, kind, position, pending?, catalog)` from `src/visual/graph.ts` (returns the same graph when the kind cannot be created); `buildNodeView(node, ctx)`, `execLabel` from `src/visual/flow/flowModel.ts`; `stepDescription`, `nodeIdentity`, `graphDocument`, `TypeChip`, `typeLabel`, `typeColor`, `typeIsList`, `graphTypes`.
- Produces:
  - `KindPreview(props: { kind: string; graph: VisualGraph; catalog: VisualCatalogSnapshot; onClose(): void }): JSX.Element`
  - `InspectorProps.preview?: string` (a kind) and `InspectorProps.onPreviewClose?(): void`

**What it renders** (`<section class="hs-insp-preview" aria-label="Step details">`):
- The header NodeConfig uses, without an input or menu: the category icon, an `<h2 class="hs-insp-title">` with the kind's label, and `<small class="hs-insp-kind">` with `Label · Category` (just the label when they are equal, as `NodeConfig.tsx:235` does). A close button `aria-label="Close details"` calling `onClose`.
- The whole description (`stepDescription(kind, spec.description, graphDocument(graph))`), not truncated.
- `Inputs` and `Outputs` lists (`<ul aria-label="Inputs">`, `<ul aria-label="Outputs">`) from the view of a prototype node: `createNodeAt(graph, kind, { x: 0, y: 0 }, undefined, catalog).nodes.at(-1)` when that graph differs from `graph`. Each data pin is its label and a `TypeChip`; each execution output is its `execLabel`. A list with no entries is not rendered. No prototype: no lists.
- `<p class="hs-muted hs-insp-hint">Drag onto the canvas, or press A.</p>`
- A kind the catalog does not have renders nothing.

**Inspector precedence:** several selected > one selected > a wire > `preview` (when the catalog has that kind) > the overview. `hasContent()` is true with a preview. `PeekBar` titles it with the kind's label, uses the kind's icon and colour, and the verb `Show`. The sheet's close (`close()`) also calls `onPreviewClose`.

- [ ] **Step 1: Write the failing tests** in `tests/kind-preview.test.tsx`, rendering `Inspector` with `graph={createGraph('Hall')}`, `catalog={nodeCatalog}`, `diagnostics={[]}`, `commit={() => {}}`, `docked`.

```tsx
it('describes a kind: label, category, the whole description, its pins and how to add it', () => {
  const root = mount({ preview: 'delay' })
  const panel = root.querySelector('[aria-label="Step details"]')!
  expect(panel.querySelector('h2')!.textContent).toBe(nodeCatalog.nodes.delay.label)
  expect(panel.querySelector('.hs-insp-kind')!.textContent).toContain(nodeIdentity('delay', nodeCatalog).label)
  expect(panel.textContent).toContain(nodeCatalog.nodes.delay.description)
  expect([...panel.querySelectorAll('[aria-label="Inputs"] li')].length).toBeGreaterThan(0)
  expect(panel.querySelector('.hs-insp-hint')!.textContent).toBe('Drag onto the canvas, or press A.')
  expect(panel.querySelector('input, select, textarea')).toBeNull()
  expect(root.querySelector('[aria-label="Automation overview"], .hs-insp-overview')).toBeNull()
})

it('says an entity definition\'s Trigger in its own words', () => {
  const root = mount({ preview: 'trigger', graph: { ...createGraph('Relay'), definition: { entities: [{ key: 'relay', domain: 'switch' }], settings: [] } } })
  expect(root.querySelector('[aria-label="Step details"]')!.textContent).toContain('Run the steps after it when something happens, at a set time or on an interval.')
})

it('a selected step, several steps or a wire come before the preview', () => {
  const graph = createGraph('Hall')
  const root = mount({ preview: 'delay', graph, selected: graph.nodes[0], selectedIds: [graph.nodes[0].id] })
  expect(root.querySelector('[aria-label="Step details"]')).toBeNull()
  expect(root.querySelector('.hs-insp-node')).not.toBeNull()
})

it('falls back to the overview when the catalog has no such kind', () => {
  const root = mount({ preview: 'library:gone:NodeLib.Missing.Run' })
  expect(root.querySelector('[aria-label="Step details"]')).toBeNull()
  expect(root.querySelector('.hs-insp-overview')).not.toBeNull()
})

it('Close details asks the page to drop the preview', () => {
  const closed = vi.fn()
  const root = mount({ preview: 'delay', onPreviewClose: closed })
  root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!.click()
  expect(closed).toHaveBeenCalledOnce()
})
```

A new `preview` also reopens a sheet the user closed, as a new selection does (added while executing, with a test).

- [ ] **Step 2: Run** `bun run test tests/kind-preview.test.tsx`. Expected: FAIL, `preview` is not a prop and nothing renders `Step details`.

- [ ] **Step 3: Implement `KindPreview`** and the Inspector mode as described above.

- [ ] **Step 4: Run** `bun run test tests/kind-preview.test.tsx tests/editor-inspector.test.tsx` and `bunx tsc --noEmit`. Expected: pass.

- [ ] **Step 5: Commit.**

---

### Task 3: Clicking a library item describes it; only a drag adds

**Files:**
- Modify: `src/visual/NodeLibraryPanel.tsx` (props 11-23, doc comment 25-30, hint 90, item 106-124)
- Modify: `src/visual/VisualEditor.tsx` (`addStep` at 241-249, `<NodeLibraryPanel>` at 534-541, `<Inspector>` at 559-582)
- Modify: `src/styles/library.css` (a pressed item's look)
- Modify: `src/components/HelpDialog.tsx:7`, `docs/custom-entities.md:41` and any other "click it to add" wording `grep -rn -i "click" docs/*.md src/components/HelpDialog.tsx` turns up about the step library
- Create: `tests/library.ts`
- Test: `tests/editor-shell.test.tsx`, `tests/palette.test.tsx`, `tests/editor-inspector.test.tsx`, `tests/EntityDefinitionView.test.tsx`

**Interfaces:**
- Consumes: `InspectorProps.preview`, `onPreviewClose` (Task 2).
- Produces:
  - `NodeLibraryPanelProps`: `onAdd` is removed; `onPreview(kind: NodeKind | undefined): void` and `previewed?: NodeKind` are added.
  - `tests/library.ts`: `export function dropStep(root: Element, label: string, at?: { x: number; y: number }): void` and `export function libraryItem(root: Element, label: string): HTMLButtonElement`.

**Behaviour:**
- A library item's accessible name is the step's label (`aria-label={item.label}`), with `aria-pressed` true while it is the previewed kind. Clicking it calls `onPreview(kind)`; clicking the pressed item calls `onPreview(undefined)`. Enter and Space do the same as a click (it stays a `<button>`), and focus stays on the item. `draggable` and `onDragStart` are unchanged.
- `VisualEditor` holds `previewKind`. Setting a kind clears the canvas selection (the selected step, a multi-selection and a selected wire) and opens the panel without moving focus (`openPanel(false)`). It is cleared when anything is selected on the canvas or in the panel, when `Close details` or the sheet's close is used, and when the pressed item is clicked again. Typing in the library's search does not clear it.
- `addStep` and its "Cannot add …" notice are deleted. Nothing else calls them.
- `dropStep` finds the `.hs-lib__item` whose `<strong>` text is `label`, dispatches `dragstart` on it with a `DataTransfer` (so the real handler names the kind), then `dragover` and `drop` on `.hs-flow` at `at` (default `{ x: 400, y: 300 }`) with the same `DataTransfer`. Move `dragEvent` out of `tests/editor-canvas.test.tsx:435-439` into `tests/library.ts` and import it back.

**Copy:**
- Hint under the search: `Click for details · drag onto the canvas`.
- `HelpDialog.tsx:7`: "An automation starts with a trigger and runs its steps from left to right. Add a step from the step library on the left by dragging it onto the canvas; click it there to read what it does. On the canvas, double-click or right-click empty space, press A, or use Add step to search for a step."
- `docs/custom-entities.md:41`: "search for `Has state` and drag it onto the canvas".

- [ ] **Step 1: Write the failing tests.**

In `tests/editor-shell.test.tsx`, replace the click-adds assertions at 357-364 with:

```tsx
expect(labels()).toContain('Delay')
expect(host.querySelector('.hs-lib__item')!.getAttribute('draggable')).toBe('true')
// A click describes the step and adds nothing.
libraryItem(host, 'Delay').click()
expect(json(host).nodes).toHaveLength(3)
expect(libraryItem(host, 'Delay').getAttribute('aria-pressed')).toBe('true')
expect(host.querySelector('[aria-label="Step details"] h2')!.textContent).toBe('Delay')
// A drag adds it, selects it and ends the preview.
dropStep(host, 'Delay')
const graph = json(host)
expect(graph.nodes).toHaveLength(4)
expect(graph.nodes[3].kind).toBe('delay')
expect(host.querySelector<HTMLInputElement>('input[aria-label="Node label"]')!.value).toBe('Delay')
expect(host.querySelector('[aria-label="Step details"]')).toBeNull()
expect(libraryItem(host, 'Delay').getAttribute('aria-pressed')).toBe('false')
```

and add, in the same file:

```tsx
it('a library click with a step selected shows the details and leaves nothing selected to delete', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  await vi.waitFor(() => expect(host.querySelector('.solid-flow__node')).not.toBeNull())
  host.querySelector<HTMLElement>('.solid-flow__node')!.click()
  await vi.waitFor(() => expect(host.querySelector('.hs-insp-node')).not.toBeNull())
  const before = json(host).nodes.length
  libraryItem(host, 'Delay').click()
  expect(host.querySelector('.hs-insp-node')).toBeNull()
  expect(host.querySelector('[aria-label="Step details"]')).not.toBeNull()
  host.querySelector('.hs-flow')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true }))
  expect(json(host).nodes).toHaveLength(before)
})

it('the details stay while the search hides their item, and go when the item is clicked again or a step is selected', async () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  libraryItem(host, 'Delay').click()
  const search = host.querySelector<HTMLInputElement>('input[aria-label="Search the step library"]')!
  search.value = 'constant'; search.dispatchEvent(new Event('input', { bubbles: true }))
  expect(host.querySelector('[aria-label="Step details"] h2')!.textContent).toBe('Delay')
  search.value = ''; search.dispatchEvent(new Event('input', { bubbles: true }))
  libraryItem(host, 'Delay').click()
  expect(host.querySelector('[aria-label="Step details"]')).toBeNull()
  libraryItem(host, 'Delay').click()
  host.querySelector<HTMLElement>('.solid-flow__node')!.click()
  await vi.waitFor(() => expect(host.querySelector('.hs-insp-node')).not.toBeNull())
  expect(host.querySelector('[aria-label="Step details"]')).toBeNull()
})

it('Enter on a focused library item shows its details and keeps focus on the item', () => {
  const host = root(); dispose = render(() => <VisualEditor name="Heating" />, host)
  const item = libraryItem(host, 'Delay')
  item.focus()
  item.click() // what Enter and Space do on a button
  expect(host.querySelector('[aria-label="Step details"]')).not.toBeNull()
  expect(host.ownerDocument.activeElement).toBe(item)
})
```

Use the helpers each file already has (`root`, `json`, `button`); select a card the way that file's other tests do if `.click()` on the node is not it.

- [ ] **Step 2: Run** `bun run test tests/editor-shell.test.tsx`. Expected: FAIL, `libraryItem` is not defined, then (once `tests/library.ts` exists) the click still adds a fourth node.

- [ ] **Step 3: Implement** `tests/library.ts`, the `NodeLibraryPanel` change, the `VisualEditor` signal and wiring, the pressed style and the copy changes.

- [ ] **Step 4: Move every test that added by clicking onto `dropStep`.** `grep -rn "'Add [A-Z]" tests | grep click` lists them: `tests/editor-shell.test.tsx` (584, 731, 733) and about 25 in `tests/EntityDefinitionView.test.tsx`. Each `button(root, 'Add X').click()` becomes `dropStep(root, 'X')`. Where a test then depends on where the step landed, pass `at`. Assertions on `aria-label` lists of `.hs-lib__item` (`tests/palette.test.tsx:38-41,60,79`, `tests/editor-inspector.test.tsx:104`, `tests/editor-shell.test.tsx:345-357`) drop the `Add ` prefix. The `onAdd={() => {}}` props in `tests/palette.test.tsx` become `onPreview={() => {}}`.

- [ ] **Step 5: Run** `bun run test` and `bunx tsc --noEmit`. Expected: every file passes; no type errors; `grep -rn "onAdd\|addStep" src/visual/NodeLibraryPanel.tsx src/visual/VisualEditor.tsx` prints nothing.

- [ ] **Step 6: Commit.**

---

## After the tasks

- [ ] `bun run test` and `bunx tsc --noEmit` from `custom_components/hass_sharp/frontend`; the other four suites are untouched by this phase but run them once (`AGENTS.md`).
- [ ] Live check in a `hass_wt` container on port 5002 mounting this worktree: `bun run build`, restart Home Assistant (a rebuilt bundle needs it), then in both editors: a click shows the details and adds nothing; a drag and `A` add; the entity editor's library starts with Entity and an automation's does not.

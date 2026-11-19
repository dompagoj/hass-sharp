import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { NodeLibraryPanel } from '../src/visual/NodeLibraryPanel'
import { QuickAdd } from '../src/visual/QuickAdd'
import { catalogPalette, createGraph, type VisualGraph } from '../src/visual/graph'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { nodeCatalog } from '../src/visual/contract'

// The steps only an entity definition's graph takes: an automation's step library and quick-add never offer them.
const entityOnly = ['entity_state', 'entity_command', 'entity_setup', 'set_entity_state', 'set_entity_available', 'setting', 'download_file', 'camera_snapshot', 'image_draw_rect', 'image_draw_text', 'camera_frame', 'show_frame']

let dispose: (() => void) | undefined
afterEach(() => {
  dispose?.()
  dispose = undefined
  document.body.innerHTML = ''
})
const host = () => document.body.appendChild(document.createElement('div'))
const label = (kind: string) => {
  expect(Object.hasOwn(nodeCatalog.nodes, kind), `the catalog has ${kind}`).toBe(true)
  return nodeCatalog.nodes[kind].label
}

it('the automation palette leaves out the entity-only steps and keeps the entity readers', () => {
  for (const kind of entityOnly) expect(nodeCatalog.nodes[kind]?.documents, kind).toEqual(['entity'])
  const kinds = catalogPalette().map(item => item.kind)
  for (const kind of entityOnly) expect(kinds, kind).not.toContain(kind)
  for (const kind of ['entity_is', 'entity_current', 'trigger', 'log', 'http_request']) expect(kinds, kind).toContain(kind)
  // An entity definition's graph offers them all.
  const entityKinds = catalogPalette(nodeCatalog, 'entity').map(item => item.kind)
  for (const kind of [...entityOnly, 'entity_is', 'entity_current', 'trigger']) expect(entityKinds, kind).toContain(kind)
})

it('the step library of an automation lists no entity-only step', () => {
  const root = host()
  dispose = render(() => <NodeLibraryPanel collapsed={false} onCollapsedChange={() => {}} onPreview={() => {}} onQuickAdd={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('.hs-lib__item')].map(item => item.getAttribute('aria-label')))
  for (const kind of entityOnly) expect(offered, kind).not.toContain(label(kind))
  expect(offered).toContain(label('entity_is'))
  expect(offered).toContain(label('entity_current'))
  expect(offered).toContain(label('http_request'))
})

it('quick-add in an automation lists no entity-only step', () => {
  const root = host()
  dispose = render(() => <QuickAdd x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('[role="option"]')].map(option => option.getAttribute('aria-label')))
  for (const kind of entityOnly) expect(offered, kind).not.toContain(`Create ${label(kind)}`)
  expect(offered).toContain(`Create ${label('entity_is')}`)
  expect(offered).toContain(`Create ${label('entity_current')}`)
})

// An entity definition's graph: its own steps beside everything an automation offers (a trigger is how it polls).
const entityKinds = [...entityOnly, 'entity_is', 'entity_current', 'trigger', 'log', 'http_request']

it('the step library of an entity definition lists its steps and every automation step', () => {
  const root = host()
  dispose = render(() => <NodeLibraryPanel document="entity" collapsed={false} onCollapsedChange={() => {}} onPreview={() => {}} onQuickAdd={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('.hs-lib__item')].map(item => item.getAttribute('aria-label')))
  for (const kind of entityKinds) expect(offered, kind).toContain(label(kind))
})

it('quick-add in an entity definition lists its steps and every automation step', () => {
  const root = host()
  dispose = render(() => <QuickAdd document="entity" x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('[role="option"]')].map(option => option.getAttribute('aria-label')))
  for (const kind of entityKinds) expect(offered, kind).toContain(`Create ${label(kind)}`)
})

it.each([
  ['automation', 'Start the automation when something happens, at a set time, or when you run it.'],
  ['entity', 'Run the steps after it when something happens, at a set time or on an interval.'],
] as const)("describes the Trigger step in the words of an %s's graph, in the step library and quick-add", (document, description) => {
  // The catalog's own text is an automation's: an entity definition's palette says it in its words.
  expect(nodeCatalog.nodes.trigger.description).toBe('Start the automation when something happens, at a set time, or when you run it.')
  expect(catalogPalette(nodeCatalog, document).find(item => item.kind === 'trigger')!.description).toBe(description)
  const root = host()
  dispose = render(() => <NodeLibraryPanel document={document} collapsed={false} onCollapsedChange={() => {}} onPreview={() => {}} onQuickAdd={() => {}} />, root)
  expect(root.querySelector('.hs-lib__item[aria-label="Trigger"] small')!.textContent).toBe(description)
  dispose()
  dispose = render(() => <QuickAdd document={document} x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={() => {}} />, root)
  const option = root.querySelector('[role="option"][aria-label="Create Trigger"]')!
  expect(root.querySelector(`[id="${option.getAttribute('aria-describedby')}"]`)!.textContent).toBe(description)
})

it('the canvas quick-add offers the steps of the document its graph is', async () => {
  const offered = async (graph: VisualGraph) => {
    const root = host()
    root.className = 'hs-app'
    dispose?.()
    dispose = render(() => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />, root)
    root.querySelector('.solid-flow__pane')!.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, clientX: 160, clientY: 470, button: 0 }))
    await vi.waitFor(() => expect(root.querySelector('[role="dialog"][aria-label="Add a step"]')).not.toBeNull())
    const kinds = new Set([...root.querySelectorAll('[role="option"]')].map(option => option.getAttribute('aria-label')))
    root.remove()
    return kinds
  }
  const automation = await offered(createGraph('Hall'))
  for (const kind of entityOnly) expect(automation, kind).not.toContain(`Create ${label(kind)}`)
  expect(automation).toContain(`Create ${label('trigger')}`)
  const entity = await offered({ version: 5, name: 'Relay', nodes: [], edges: [], definition: { entities: [{ key: 'relay', domain: 'switch' }], settings: [] } })
  for (const kind of entityKinds) expect(entity, kind).toContain(`Create ${label(kind)}`)
})

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

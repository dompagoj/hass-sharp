import { afterEach, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import { NodeLibraryPanel } from '../src/visual/NodeLibraryPanel'
import { QuickAdd } from '../src/visual/QuickAdd'
import { catalogPalette } from '../src/visual/graph'
import { nodeCatalog } from '../src/visual/contract'

// The steps only an entity definition's graph takes: an automation's step library and quick-add never offer them.
const entityOnly = ['entity_state', 'entity_command', 'entity_setup', 'set_entity_state', 'set_entity_available', 'setting']

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
  for (const kind of ['entity_is', 'entity_current', 'trigger', 'log']) expect(kinds, kind).toContain(kind)
  // An entity definition's graph offers them all.
  const entityKinds = catalogPalette(nodeCatalog, 'entity').map(item => item.kind)
  for (const kind of [...entityOnly, 'entity_is', 'entity_current', 'trigger']) expect(entityKinds, kind).toContain(kind)
})

it('the step library of an automation lists no entity-only step', () => {
  const root = host()
  dispose = render(() => <NodeLibraryPanel collapsed={false} onCollapsedChange={() => {}} onAdd={() => {}} onQuickAdd={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('.hs-lib__item')].map(item => item.getAttribute('aria-label')))
  for (const kind of entityOnly) expect(offered, kind).not.toContain(`Add ${label(kind)}`)
  expect(offered).toContain(`Add ${label('entity_is')}`)
  expect(offered).toContain(`Add ${label('entity_current')}`)
})

it('quick-add in an automation lists no entity-only step', () => {
  const root = host()
  dispose = render(() => <QuickAdd x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={() => {}} />, root)
  const offered = new Set([...root.querySelectorAll('[role="option"]')].map(option => option.getAttribute('aria-label')))
  for (const kind of entityOnly) expect(offered, kind).not.toContain(`Create ${label(kind)}`)
  expect(offered).toContain(`Create ${label('entity_is')}`)
  expect(offered).toContain(`Create ${label('entity_current')}`)
})

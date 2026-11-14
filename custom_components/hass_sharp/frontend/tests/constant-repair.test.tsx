import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualEditor } from '../src/visual/VisualEditor'
import { NodeConfig } from '../src/visual/NodeConfig'
import { nodeCatalog, validateGraph, type Graph } from '../src/visual/contract'
import { addNode, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { visualFixture } from './visual-fixture'
import { constantTypes, ConstantType } from '../src/visual/constant'
import { typeLabel } from '../src/visual/typeLabels'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.()
  dispose = undefined
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const root = () => { const el = document.createElement('div'); el.className = 'hs-app'; document.body.append(el); return el }
const byLabel = (scope: ParentNode, label: string) =>
  [...scope.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)

/** A Constant whose Type is Number, holding a value stored before its Type set the value's type. */
function stale(value: unknown, storedAs = 'string'): VisualGraph {
  let g = addNode(createGraph('Repair'), 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type: 'number', value: lit(storedAs, value) }, position: { x: 400, y: 300 } })
  return g
}

function mount(graph: VisualGraph) {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(260)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(120)
  const host = root(), fixture = visualFixture()
  fixture.documents.set('saved', { version: 1, id: 'saved', revision: 1, graph, source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <VisualEditor name="Repair" documentId="saved" />
    </HassCtx.Provider></QueryClientProvider>, host)
  return host
}
const graphOf = (host: Element) => {
  const pre = () => host.querySelector('pre[aria-label="Graph JSON"]')
  if (!pre()) byLabel(host, 'Graph JSON')!.click()
  return JSON.parse(pre()!.textContent!) as Graph
}
const providers = (children: () => any) => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
  <HassCtx.Provider value={{ ...visualFixture(), user: { is_admin: true } } as any}>{children()}</HassCtx.Provider></QueryClientProvider>
const configOf = (host: Element, id = 'c') => graphOf(host).nodes.find(n => n.id === id)!.config
const blur = (field: HTMLInputElement) => { field.dispatchEvent(new FocusEvent('blur')); field.dispatchEvent(new FocusEvent('focusout', { bubbles: true })) }
const cardField = (host: Element) => host.querySelector<HTMLInputElement>('[data-id="c"] .hs-card-editor input[aria-label="Value"]')!
const panelField = (host: Element) => host.querySelector<HTMLInputElement>('.hs-inspector [data-input-id="value"] input[aria-label="Value"]')!

it("shows a Constant's stored text in its Type's own editor, and stores it as the Type when committed unchanged: one undo step", async () => {
  const host = mount(stale('21'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  // The number field, holding the stored text (not an empty field, not the JSON editor).
  expect(cardField(host).value).toBe('21')
  expect(host.querySelector('[data-id="c"] .hs-json-editor, [data-id="c"] textarea')).toBeNull()
  host.querySelector<HTMLElement>('[data-id="c"] .hs-card__title')!.click()
  await vi.waitFor(() => expect(panelField(host)).not.toBeNull())
  expect(panelField(host).value).toBe('21')
  // The validator refuses the value as stored.
  expect(validateGraph(graphOf(host), nodeCatalog).some(d => d.severity === 3 && d.nodeId === 'c')).toBe(true)

  // Committing it unchanged stores it as a number; the graph then validates.
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
  expect(validateGraph(graphOf(host), nodeCatalog).filter(d => d.severity === 3)).toEqual([])
  await vi.waitFor(() => expect(cardField(host).value).toBe('21'))
  // Committing again changes nothing more.
  blur(cardField(host))
  blur(panelField(host))
  // One undo step brings back the value as it was stored.
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', '21') })
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
})

it('stores the stale value from the panel too, and a number stored under another type is stored again as a number', async () => {
  const host = mount(stale(21, 'integer'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  host.querySelector<HTMLElement>('[data-id="c"] .hs-card__title')!.click()
  await vi.waitFor(() => expect(panelField(host)).not.toBeNull())
  expect(panelField(host).value).toBe('21')
  panelField(host).dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 21) })
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('integer', 21) })
})

it("shows a stored value that does not read as the Type with the field's own error, and keeps it", async () => {
  const host = mount(stale('abc'))
  await vi.waitFor(() => expect(cardField(host)).not.toBeNull())
  const field = cardField(host)
  expect(field.value).toBe('abc')
  expect(field.getAttribute('aria-invalid')).toBe('true')
  const error = () => host.querySelector('[data-id="c"] .hs-card-editor .hs-error')
  expect(error()?.textContent).toBe('Enter a number.')
  // Committing it, or pressing Escape, neither drops it nor hides why it is wrong.
  blur(field)
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', 'abc') })
  expect(cardField(host).value).toBe('abc')
  expect(error()?.textContent).toBe('Enter a number.')
  expect(byLabel(host, 'Undo')!.disabled).toBe(true)
  // Typing a number repairs it, in one undo step.
  cardField(host).value = '7'
  cardField(host).dispatchEvent(new Event('input', { bubbles: true }))
  blur(cardField(host))
  expect(configOf(host)).toEqual({ type: 'number', value: lit('number', 7) })
  await vi.waitFor(() => expect(error()).toBeNull())
  byLabel(host, 'Undo')!.click()
  expect(configOf(host)).toEqual({ type: 'number', value: lit('string', 'abc') })
})

it("changes a Constant's Type in the panel: the value stays when it reads as the new Type, is cleared otherwise, one undo step each", () => {
  const el = root()
  let start = addNode(createGraph('Panel'), 'constant', 'c', nodeCatalog)
  start = updateNode(start, 'c', { config: { type: 'string', value: lit('string', 'light.hall') } })
  const history = createGraphHistory(start)
  dispose = render(() => providers(() => {
    const node = () => history.graph().nodes.find(n => n.id === 'c')!
    return <NodeConfig graph={history.graph()} node={node()} catalog={nodeCatalog}
      onChange={config => history.commit(updateNode(history.graph(), 'c', { config }))} onGraphChange={graph => history.commit(graph)} />
  }), el)
  const type = () => el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!
  const config = () => history.graph().nodes.find(n => n.id === 'c')!.config
  const choose = (value: string) => { type().value = value; type().dispatchEvent(new Event('change', { bubbles: true })) }
  choose('entity_id')
  expect(config()).toEqual({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  choose('number')
  expect(config()).toEqual({ type: 'number', value: { kind: 'unset' } })
  expect(type().value).toBe('number')
  history.undo()
  expect(config()).toEqual({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  history.undo()
  expect(config()).toEqual({ type: 'string', value: lit('string', 'light.hall') })
  expect(history.canUndo()).toBe(false)
})

it("keeps a Constant's outgoing wire when its new Type no longer fits the input it feeds: an error on that input, as with Convert", () => {
  let g = addNode(createGraph('Fit'), 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type: 'boolean', value: lit('boolean', true) } })
  g = addNode(g, 'if', 'branch', nodeCatalog)
  g = { ...g, edges: [...g.edges, { id: 'w', kind: 'data', source: 'c', sourcePort: 'value', target: 'branch', targetPort: 'value' }] } as VisualGraph
  const wireErrors = (graph: VisualGraph) => validateGraph(graph, nodeCatalog).filter(d => d.severity === 3 && d.code !== 'node.unreachable')
  expect(wireErrors(g)).toEqual([])
  const el = root()
  const history = createGraphHistory(g)
  dispose = render(() => providers(() => {
    const node = () => history.graph().nodes.find(n => n.id === 'c')!
    return <NodeConfig graph={history.graph()} node={node()} catalog={nodeCatalog}
      onChange={config => history.commit(updateNode(history.graph(), 'c', { config }))} onGraphChange={graph => history.commit(graph)} />
  }), el)
  const type = el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!
  type.value = 'string'
  type.dispatchEvent(new Event('change', { bubbles: true }))
  const after = history.graph()
  expect(after.nodes.find(n => n.id === 'c')!.config.type).toBe('string')
  expect(after.edges.find(e => e.id === 'w')).toEqual(g.edges.find(e => e.id === 'w'))
  // The wire stays; the If's Condition carries the mismatch (the Constant's own problem is only its cleared value).
  expect(wireErrors(after).map(d => [d.nodeId, d.inputId, d.message])).toEqual([
    ['branch', 'value', 'Condition accepts Yes/No, not Text'],
    ['c', 'value', 'Value is required'],
  ])
})

it('offers a Constant only the types a value can be typed in for, and keeps showing a stored Type that is not offered', () => {
  const catalog = (rich as any).catalog
  const offered = constantTypes(catalog)
  // The built-in value types, entity ids and targets, JSON objects and lists, the CLR text, number and boolean types
  // and enums; never an interface, an abstract or record type (no literal form) and never a nullable one.
  expect(offered).toEqual(expect.arrayContaining(['boolean', 'number', 'string', 'object', 'array', 'entity_id', 'global::System.Int32', 'global::System.String', 'global::NodeLib.Mode']))
  expect(offered.every(id => !!catalog.types[id].literalKind && catalog.types[id].nullable !== true)).toBe(true)
  for (const left of ['global::System.IComparable', 'global::System.ISpanParsable<global::System.Boolean>']) expect(offered).not.toContain(left)
  expect(Object.keys(catalog.types).filter(id => catalog.types[id].nullable === true).some(id => offered.includes(id))).toBe(false)
  expect(offered.length).toBeLessThan(Object.keys(catalog.types).length / 4)

  // A Constant stored with a Type that is no longer offered: the card and the panel show it, selected, by its name.
  const stored = 'global::System.IComparable'
  let g = addNode(createGraph('Stored'), 'constant', 'c', catalog)
  g = updateNode(g, 'c', { config: { type: stored, value: { kind: 'unset' } } })
  const el = root()
  const changes: unknown[] = []
  dispose = render(() => providers(() => <>
    <ConstantType type={stored} catalog={catalog} onChange={type => changes.push(type)} />
    <NodeConfig graph={g} node={g.nodes.find(n => n.id === 'c')!} catalog={catalog} onChange={config => changes.push(config)} onGraphChange={() => {}} />
  </>), el)
  const [card, panel] = [el.querySelector<HTMLSelectElement>('select[aria-label="Type"]')!, el.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!]
  for (const select of [card, panel]) {
    expect(select.selectedOptions[0].textContent).toBe(typeLabel(stored, catalog))
    expect([...select.options].map(o => o.value).filter(v => v !== '' && v !== stored)).toEqual(offered)
  }
  expect(changes).toEqual([])
})

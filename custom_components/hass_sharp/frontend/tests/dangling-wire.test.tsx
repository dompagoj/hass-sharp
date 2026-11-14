import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { VisualEditor } from '../src/visual/VisualEditor'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { nodeCatalog, validateGraph, type Graph } from '../src/visual/contract'
import { addNode, connectNodes, updateNode, type VisualGraph } from '../src/visual/graph'
import { visualFixture } from './visual-fixture'

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
const layout = () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(260)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(120)
}

/**
 * Motion (an entity trigger) → Log, which logs a Constant. A wire from the trigger's Entity goes into the Constant's
 * value, as graphs saved before a Constant took a typed-in value only could have: the Constant has no such pin.
 */
function wired(): VisualGraph {
  let g: VisualGraph = { version: 5, name: 'Wires', nodes: [], edges: [] }
  g = addNode(g, 'trigger', 't', nodeCatalog)
  g = updateNode(g, 't', { label: 'Motion', config: { type: 'entity', entity: 'binary_sensor.motion' }, position: { x: 0, y: 0 } })
  g = addNode(g, 'log', 'l', nodeCatalog)
  g = updateNode(g, 'l', { position: { x: 400, y: 0 } })
  g = addNode(g, 'constant', 'c', nodeCatalog)
  g = updateNode(g, 'c', { config: { type: 'string', value: lit('string', 'hello') }, position: { x: 400, y: 300 } })
  g = connectNodes(g, 't', 'l', undefined, nodeCatalog)
  return {
    ...g,
    edges: [
      ...g.edges,
      { id: 'said', kind: 'data', source: 'c', sourcePort: 'value', target: 'l', targetPort: 'value' },
      { id: 'into-constant', kind: 'data', source: 't', sourcePort: 'entity_id', target: 'c', targetPort: 'value' },
    ],
  } as VisualGraph
}

function mount(graph: VisualGraph) {
  layout()
  const host = root(), fixture = visualFixture()
  fixture.documents.set('saved', { version: 1, id: 'saved', revision: 1, graph, source: '// fixture', sourceMap: [], automationId: 'visual:saved', currentRevisionActive: true, catalogGeneration: 'saved-fixture' })
  dispose = render(() => <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
      <VisualEditor name="Wires" documentId="saved" />
    </HassCtx.Provider></QueryClientProvider>, host)
  return host
}
const graphOf = (host: Element) => { byLabel(host, 'Graph JSON')!.click(); return JSON.parse(host.querySelector('pre[aria-label="Graph JSON"]')!.textContent!) as Graph }
const LABEL = 'Remove connection from Motion · Entity'

it('does not draw a wire whose pin the step does not have (so the canvas has no Delete wire for it)', async () => {
  layout()
  const el = root()
  dispose = render(() => <FlowAdapter graph={wired()} onSelect={() => {}} onChange={() => {}} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge[data-id="said"]')).not.toBeNull())
  expect(el.querySelector('.solid-flow__edge[data-id="into-constant"]')).toBeNull()
  // The Constant draws no input pin for its value.
  expect(el.querySelector('[data-id="c"] .target')).toBeNull()
})

it('removes a wire into a missing pin from its problem, in the Problems list and in the panel: only that wire, one undo step', async () => {
  const host = mount(wired())
  await vi.waitFor(() => expect(host.querySelector('[data-id="c"]')).not.toBeNull())
  // The problem is the validator's, about the Constant's value; it offers to remove exactly that wire, by name.
  const tab = [...host.querySelectorAll<HTMLButtonElement>('.hs-drawer [role="tab"]')].find(b => b.textContent?.startsWith('Problems'))!
  tab.click()
  const drawer = host.querySelector<HTMLElement>('.hs-problems')!
  await vi.waitFor(() => expect(byLabel(drawer, LABEL)).toBeDefined())
  expect(byLabel(drawer, LABEL)!.textContent).toBe('Remove connection')
  // The same action on the step's own problems in the panel.
  host.querySelector<HTMLElement>('[data-id="c"] .hs-card__title')!.click()
  await vi.waitFor(() => expect(byLabel(host.querySelector('.hs-insp-problems')!, LABEL)).toBeDefined())
  // Other problems offer nothing of the kind.
  expect([...host.querySelectorAll('button')].filter(b => b.textContent?.trim() === 'Remove connection')).toHaveLength(2)

  byLabel(drawer, LABEL)!.click()
  let after = graphOf(host)
  expect(after.edges.some(e => e.id === 'into-constant')).toBe(false)
  expect(after.edges.some(e => e.id === 'said')).toBe(true)
  expect(after.edges).toHaveLength(wired().edges.length - 1)
  // The Constant keeps its value; the graph now validates.
  expect(after.nodes.find(n => n.id === 'c')!.config).toEqual({ type: 'string', value: lit('string', 'hello') })
  expect(validateGraph(after, nodeCatalog).filter(d => d.severity === 3)).toEqual([])
  await vi.waitFor(() => expect(byLabel(host, LABEL)).toBeUndefined())
  // Focus does not fall to the page when the problem and its button go.
  await vi.waitFor(() => expect(document.activeElement).toBe(drawer))

  // One undo step brings the wire back, with its problem and its action.
  byLabel(host, 'Undo')!.click()
  after = graphOf(host)
  expect(after.edges.find(e => e.id === 'into-constant')).toEqual(wired().edges.find(e => e.id === 'into-constant'))
  await vi.waitFor(() => expect(byLabel(drawer, LABEL)).toBeDefined())
})

it('shows a Constant with a wire into it as a problem on its card and in its panel, never as connected', async () => {
  const host = mount(wired())
  const card = () => host.querySelector('[data-id="c"]')!
  await vi.waitFor(() => expect(card()).not.toBeNull())
  // No source chip: the wire does not work. The value's editor stays, holding the value, with the validator's message.
  expect(card().querySelector('.hs-source-chip')).toBeNull()
  // Its summary is its value, not the wire's source ("entity").
  await vi.waitFor(() => expect(card().querySelector('.hs-card__summary')?.textContent).toBe('\u201chello\u201d'))
  const field = () => card().querySelector<HTMLInputElement>('.hs-card-editor input[aria-label="Value"]')
  await vi.waitFor(() => expect(field()?.value).toBe('hello'))
  const badge = card().querySelector('.hs-card__badge')!
  expect(badge.classList.contains('hs-card__badge--warning')).toBe(false)
  expect(badge.getAttribute('title')).toContain('Nothing can be connected to a Constant')
  // The row itself is marked: its editor carries the message, as for any value with an error.
  expect(card().querySelector('[data-input-id="value"]')!.textContent).toContain('Nothing can be connected to a Constant')
  // The panel does not offer to select the wire's source either.
  card().querySelector<HTMLElement>('.hs-card__title')!.click()
  await vi.waitFor(() => expect(host.querySelector('.hs-inspector [data-input-id="value"] input[aria-label="Value"]')).not.toBeNull())
  expect(host.querySelector('.hs-inspector [data-input-id="value"] .hs-insp-pill')).toBeNull()
})

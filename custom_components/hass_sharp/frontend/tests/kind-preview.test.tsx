import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { Inspector, type InspectorProps } from '../src/visual/Inspector'
import { addNode, connectNodes, createGraph } from '../src/visual/graph'
import { nodeCatalog } from '../src/visual/contract'
import { nodeIdentity } from '../src/visual/icons'

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(() => {
  dispose?.()
  dispose = undefined
  client?.clear()
  document.body.innerHTML = ''
})
/**
 * The docked Inspector of a fresh automation, with `props` over those defaults (a selected step's pickers ask Home
 * Assistant), in a workspace with a canvas stand-in and, with `pressed`, the step library's pressed item.
 */
function mount(props: Partial<InspectorProps>, options: { pressed?: boolean; hidden?: boolean } = {}) {
  const root = document.createElement('div')
  root.className = 'hs-app'
  document.body.append(root)
  client = new QueryClient()
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS: async () => ({}) } as any}>
        <div class="hs-visual-workspace">
          <div class="hs-flow" tabindex="-1" />
          {options.pressed && <button type="button" class="hs-lib__item" aria-pressed="true">Delay</button>}
          <Inspector graph={createGraph('Hall')} catalog={nodeCatalog} diagnostics={[]} commit={() => {}} docked {...props} />
        </div>
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  // A hidden library (below 700px it is display: none) draws its items nowhere.
  const item = root.querySelector<HTMLElement>('.hs-lib__item')
  if (item && options.hidden) item.getClientRects = () => [] as unknown as DOMRectList
  return root
}

/** The rows of a pin list in the details: each pin's name, the type its chip names (none for a flow pin) and its description. */
const rows = (panel: Element, list: string) =>
  [...panel.querySelectorAll(`[aria-label="${list}"] li`)].map(row => [
    row.querySelector('.hs-insp-output__name')!.textContent,
    row.querySelector('.hs-type-chip__name')?.textContent,
    row.querySelector('.hs-insp-output__desc')?.textContent,
  ])

it('describes a kind: label, category, the whole description, how to add it and then its pins', () => {
  const root = mount({ preview: 'delay' })
  const panel = root.querySelector('[aria-label="Step details"]')!
  expect(panel.querySelector('h2')!.textContent).toBe(nodeCatalog.nodes.delay.label)
  expect(panel.querySelector('.hs-insp-kind')!.textContent).toContain(nodeIdentity('delay', nodeCatalog).label)
  expect(panel.textContent).toContain(nodeCatalog.nodes.delay.description)
  // How to add it comes right after the description, before the pins.
  const hint = panel.querySelector('.hs-insp-hint')!
  expect(hint.textContent).toBe('Drag onto the canvas, or use + on the canvas (A).')
  expect(panel.querySelector('.hs-insp-desc')!.nextElementSibling).toBe(hint)
  // A value pin names its type; a flow pin has no type chip.
  expect(rows(panel, 'Inputs')).toEqual([['Seconds', 'Number', 'How long to wait, up to about 49 days.']])
  expect(rows(panel, 'Outputs')).toEqual([['Next', undefined, undefined], ['On error', undefined, undefined]])
  expect(panel.querySelector('input, select, textarea')).toBeNull()
  expect(root.querySelector('[aria-label="Automation overview"], .hs-insp-overview')).toBeNull()
})

it('says an entity definition\'s Trigger in its own words', () => {
  // An entity definition's graph: its definition (no entity listed) and its relay an Entity node.
  const graph = createGraph('Relay')
  const relay = { id: 'relay', kind: 'entity', config: { key: 'relay', domain: 'switch', name: 'Relay' }, position: { x: 60, y: 300 } }
  const root = mount({ preview: 'trigger', graph: { ...graph, nodes: [...graph.nodes, relay], definition: { settings: [] } } })
  expect(root.querySelector('[aria-label="Step details"]')!.textContent).toContain('Run the steps after it when something happens, at a set time or on an interval.')
})

it('a selected step, several steps or a wire come before the preview', () => {
  let graph = createGraph('Hall')
  graph = addNode(graph, 'delay', 'delay-1')
  graph = connectNodes(graph, graph.nodes[0].id, 'delay-1')
  const step = mount({ preview: 'delay', graph, selected: graph.nodes[0], selectedIds: [graph.nodes[0].id] })
  expect(step.querySelector('[aria-label="Step details"]')).toBeNull()
  expect(step.querySelector('.hs-insp-node')).not.toBeNull()
  dispose?.()
  const many = mount({ preview: 'delay', graph, selectedIds: [graph.nodes[0].id, 'delay-1'] })
  expect(many.querySelector('[aria-label="Step details"]')).toBeNull()
  expect(many.querySelector('[aria-label="Selection"]')).not.toBeNull()
  dispose?.()
  const wire = mount({ preview: 'delay', graph, selectedEdgeId: graph.edges[0].id })
  expect(wire.querySelector('[aria-label="Step details"]')).toBeNull()
  expect(wire.querySelector('[aria-label="Flow wire"]')).not.toBeNull()
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

it('as a sheet, a peek bar shows the kind and closing the sheet drops the preview', () => {
  const peek = mount({ preview: 'delay', docked: false, open: false })
  expect(peek.querySelector('.hs-insp-peek')!.getAttribute('aria-label')).toBe(`Show ${nodeCatalog.nodes.delay.label}`)
  dispose?.()
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const sheet = mount({ get preview() { return kind() }, docked: false, onPreviewClose: () => setKind(undefined) })
  const panel = sheet.querySelector('.hs-inspector')!
  expect(panel.getAttribute('data-open')).toBe('true')
  sheet.querySelector<HTMLButtonElement>('button[aria-label="Close panel"]')!.click()
  expect(kind()).toBeUndefined()
  expect(panel.getAttribute('data-open')).toBe('false')
  // The next kind described opens the sheet again.
  setKind('delay')
  expect(panel.getAttribute('data-open')).toBe('true')
})

it('Close details puts focus on the canvas once the preview is gone, so it never falls to the page', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, onPreviewClose: () => setKind(undefined) })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!
  close.focus()
  close.click()
  expect(root.querySelector('[aria-label="Step details"]')).toBeNull()
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-flow')))
})

it('Close details puts focus back on the library item that showed the kind', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, onPreviewClose: () => { setKind(undefined); root.querySelector('.hs-lib__item')!.setAttribute('aria-pressed', 'false') } }, { pressed: true })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!
  close.focus()
  close.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-lib__item')))
})

it('Close details puts focus on the canvas when the library item that showed the kind is gone', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, onPreviewClose: () => { setKind(undefined); root.querySelector('.hs-lib__item')!.remove() } }, { pressed: true })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!
  close.focus()
  close.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-flow')))
})

it('Close details puts focus on the canvas when the library that showed the kind is hidden', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, onPreviewClose: () => { setKind(undefined); root.querySelector('.hs-lib__item')!.setAttribute('aria-pressed', 'false') } }, { pressed: true, hidden: true })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!
  close.focus()
  close.click()
  expect(root.querySelector('.hs-lib__item')!.isConnected).toBe(true)
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-flow')))
})

it('Escape inside the details ends them as Close details does, and gives focus back to the library item', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, onPreviewClose: () => { setKind(undefined); root.querySelector('.hs-lib__item')!.setAttribute('aria-pressed', 'false') } }, { pressed: true })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close details"]')!
  close.focus()
  const escape = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true })
  close.dispatchEvent(escape)
  expect(escape.defaultPrevented).toBe(true)
  expect(kind()).toBeUndefined()
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-lib__item')))
})

it('as a sheet, Close panel gives focus back to the library item that showed the kind; with a step selected, to the canvas', async () => {
  const [kind, setKind] = createSignal<string | undefined>('delay')
  const root = mount({ get preview() { return kind() }, docked: false, onPreviewClose: () => { setKind(undefined); root.querySelector('.hs-lib__item')!.setAttribute('aria-pressed', 'false') } }, { pressed: true })
  const close = root.querySelector<HTMLButtonElement>('button[aria-label="Close panel"]')!
  close.focus()
  close.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-lib__item')))
  dispose?.()
  document.body.innerHTML = ''
  const graph = createGraph('Hall')
  const step = mount({ preview: 'delay', graph, selected: graph.nodes[0], selectedIds: [graph.nodes[0].id], docked: false }, { pressed: true })
  const closeStep = step.querySelector<HTMLButtonElement>('button[aria-label="Close panel"]')!
  closeStep.focus()
  closeStep.click()
  await vi.waitFor(() => expect(document.activeElement).toBe(step.querySelector('.hs-flow')))
})

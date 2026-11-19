import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { Inspector, type InspectorProps } from '../src/visual/Inspector'
import { createGraph } from '../src/visual/graph'
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
function mount(props: Partial<InspectorProps>, options: { pressed?: boolean } = {}) {
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
  return root
}

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

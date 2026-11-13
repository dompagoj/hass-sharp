import { afterEach, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { createMemoryHistory, MemoryRouter, Route } from '@solidjs/router'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { HassCtx } from '../src/context'
import { RunTraceView } from '../src/views/RunTraceView'
import { nodeCatalog, type Graph } from '../src/visual/contract'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import type { RunTrace, RunTraceResponse, TraceEvent } from '../src/types'
import { run } from './run-fixture'
import services from '../../../../tests/fixtures/visual-services.json'

let dispose: (() => void) | undefined
let client: QueryClient | undefined
afterEach(async () => {
  dispose?.(); dispose = undefined; client?.clear(); vi.useRealTimers(); vi.restoreAllMocks(); document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})

const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
/** trigger-1 → condition-1 → (true) lights loop → body: service-1 (error → note); condition false → off (never ran). */
function graph(): Graph {
  let g = createGraph('Motion lights')
  g = { ...g, edges: g.edges.filter(e => e.source !== 'condition-1') }
  g = addNode(g, 'foreach', 'lights', nodeCatalog)
  g = addNode(g, 'log', 'note', nodeCatalog)
  g = addNode(g, 'stop', 'off', nodeCatalog)
  g = updateNode(g, 'lights', { label: 'Each light', position: { x: 400, y: 100 }, config: { ...g.nodes.find(n => n.id === 'lights')!.config, itemType: 'entity_id' } })
  g = updateNode(g, 'service-1', { label: 'Turn on', position: { x: 700, y: 100 }, config: { ...g.nodes.find(n => n.id === 'service-1')!.config, fields: { brightness_pct: lit('number', 80), api_key: lit('string', 'x') } } })
  g = updateNode(g, 'note', { position: { x: 1000, y: 100 } })
  g = updateNode(g, 'off', { label: 'Give up', position: { x: 400, y: 400 } })
  g = connectNodes(g, 'condition-1', 'lights', 'true')
  g = connectNodes(g, 'condition-1', 'off', 'false')
  g = connectNodes(g, 'lights', 'service-1', 'body')
  g = connectNodes(g, 'service-1', 'note', 'error')
  return g
}
const lights = ['light.hall', 'light.kitchen', 'light.landing']
function events(): TraceEvent[] {
  const list: Omit<TraceEvent, 'seq'>[] = [
    { node: 'trigger-1', frame: '', t: 0, ms: 2, status: 'ok', port: 'next', outputs: { entity_id: { type: 'entity_id', v: 'binary_sensor.motion' } } },
    { node: 'condition-1', frame: '', t: 2, ms: 1, status: 'ok', port: 'true', inputs: { entity: { type: 'entity_id', v: 'binary_sensor.motion' } } },
    { node: 'lights', frame: '', t: 3, ms: 900, status: 'ok', port: 'next', inputs: { items: { type: 'array', v: lights } } },
    ...lights.flatMap((light, i): Omit<TraceEvent, 'seq'>[] => [
      { node: 'service-1', frame: `lights#${i}:body`, t: 4 + i * 300, ms: 250 + i, status: i === 1 ? 'error' : 'ok', port: i === 1 ? 'error' : 'next',
        inputs: { target: { type: 'entity_id', v: light, src: { node: 'lights', port: 'item' } }, 'field:brightness_pct': { type: 'number', v: 80 + i }, 'field:api_key': { type: 'string', v: '***', redacted: true } },
        ...(i === 1 ? { error: { type: 'InvalidOperationException', message: `${light} is unavailable` } } : {}) },
      ...(i === 1 ? [{ node: 'note', frame: `lights#${i}:body`, t: 260 + i * 300, ms: 1, status: 'ok' as const, port: 'next', trunc: true }] : []),
    ]),
  ]
  return list.map((event, i) => ({ ...event, seq: i + 1 }))
}
function response(overrides: Partial<RunTraceResponse> = {}, traceOverrides: Partial<RunTrace> = {}): RunTraceResponse {
  const list = traceOverrides.events ?? events()
  const summary: RunTrace['summary'] = {}
  for (const event of list) {
    const row = (summary[event.node] ??= { count: 0, failed: 0, last: 'ok' })
    row.count++; row.last = event.status
  }
  return {
    version: 1, live: false,
    run: run({ runId: 'run-1', visualId: 'heating', visualRevision: 4, actionLabel: 'Motion in the hall', trigger: { kind: 'entity', entityId: 'binary_sensor.motion', from: 'off', to: 'on' } }),
    // With the services section: its field specs are copied while the graph is checked, which a store proxy refuses.
    snapshot: { revision: 'rev', visualId: 'heating', visualRevision: 4, graph: graph(), catalog: { ...nodeCatalog, services: services as any } },
    trace: { format: 1, runId: 'run-1', rootNodeId: 'trigger-1', startedUtc: '2026-11-12T01:00:01Z', complete: true, truncated: false, dropped: 0, trigger: { platform: 'entity', entity_id: 'binary_sensor.motion' }, events: list, summary, ...traceOverrides },
    ...overrides,
  }
}

/** Layout for the canvas: wires are drawn once cards have a size. */
const layout = () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
}
function mount(trace: (message: any) => Promise<unknown>, options: { admin?: boolean; revision?: number } = {}) {
  layout()
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: '/visual/heating/runs/run-1' })
  const callWS = vi.fn(async (message: any) => {
    if (message.type === 'hass_sharp/run_trace') return trace(message)
    if (message.type === 'hass_sharp/visual' && message.request.operation === 'read')
      return { version: 1, ok: true, result: { id: 'heating', revision: options.revision ?? 4, graph: graph() } }
    return []
  })
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS, user: { is_admin: options.admin ?? true } } as any}>
        <MemoryRouter history={history}>
          <Route path="/visual/:id/runs/:runId" component={RunTraceView} />
          <Route path="/visual/:id" component={() => <p>Editor page</p>} />
        </MemoryRouter>
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  return { root, callWS, history }
}
const card = (root: Element, id: string) => root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
const steps = (root: Element) => [...root.querySelectorAll<HTMLButtonElement>('.hs-trace-step')]
const panel = (root: Element) => root.querySelector<HTMLElement>('[aria-label="Step values"]')!
const port = (root: Element, id: string) => panel(root).querySelector<HTMLElement>(`.hs-trace-port[data-port="${id}"]`)!

it('shows the run\'s own graph read-only, with the path it took and the steps it never reached', async () => {
  const { root, callWS } = mount(async () => response())
  await vi.waitFor(() => expect(root.querySelectorAll('.solid-flow__edge').length).toBe(graph().edges.length))
  expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/run_trace', runId: 'run-1' })
  expect(root.querySelector('.hs-flow')!.classList).toContain('hs-flow--readonly')
  expect(root.querySelector('.hs-card-editor')).toBeNull()
  expect(card(root, 'off').querySelector('.hs-card')!.classList).toContain('hs-card--unreached')
  expect(card(root, 'lights').querySelector('.hs-card')!.classList).not.toContain('hs-card--unreached')
  const travelled = (id: string) => root.querySelector(`.solid-flow__edge[data-id="${id}"]`)!.outerHTML.includes('hs-wire--travelled')
  expect(travelled('trigger-1->condition-1')).toBe(true)
  expect(travelled('condition-1:true->lights')).toBe(true)
  expect(travelled('lights:body->service-1')).toBe(true)
  expect(travelled('service-1:error->note')).toBe(true)
  expect(travelled('condition-1:false->off')).toBe(false)
  // Header: the automation's name, the run's status, what started it, and the way back to the editor.
  expect(root.querySelector('h1')!.textContent).toBe('Motion lights')
  expect(root.querySelector('.hs-save-chip')!.textContent).toContain('Succeeded')
  expect(root.querySelector('.hs-trace-strip')!.textContent).toContain('Motion in the hall · binary_sensor.motion: off → on')
  expect(root.querySelector<HTMLAnchorElement>('a.hs-trace-open')!.getAttribute('href')).toBe('/visual/heating')
  expect(root.textContent).not.toContain('earlier version')
  // The timeline lists every recorded run of a step, iterations 1-based.
  expect(steps(root)).toHaveLength(7)
  expect(steps(root)[3].textContent).toContain('Iteration 1 of 3')
  expect(steps(root)[5].textContent).toContain('Some values were too large to record')
  // Keys stay inside the page.
  const outside = vi.fn(); root.addEventListener('keydown', outside)
  card(root, 'lights').dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete', bubbles: true, cancelable: true }))
  expect(outside).not.toHaveBeenCalled()
})

it('shows a selected step\'s recorded values with their sources, and switches iterations with the chooser', async () => {
  const { root } = mount(async () => response())
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  // The last iteration is on the card until another is chosen.
  await vi.waitFor(() => expect(card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-value-chip')?.textContent).toBe('82'))
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  expect(port(root, 'field:brightness_pct').textContent).toContain('82')
  expect(port(root, 'field:brightness_pct').textContent).toContain('Number')
  expect(port(root, 'field:brightness_pct').textContent).toContain('Value')
  // A secret is never shown as data; an object is a tree, its kind in words.
  expect(port(root, 'field:api_key').querySelector('.hs-trace-value')!.textContent).toBe('Hidden')
  expect(port(root, 'field:api_key').textContent).not.toContain('***')
  expect(port(root, 'target').querySelector('.hs-trace-value')!.textContent).toBe('light.landing')
  expect(port(root, 'target').querySelector('.hs-trace-port__from')!.textContent).toBe('From Each light · Item')
  expect(port(root, 'target').textContent).toContain('Entity')
  // A list is a tree, its kind in words on the card and in the panel.
  expect(card(root, 'lights').querySelector('[data-input-id="items"] .hs-value-chip')?.textContent).toBe('List · 3 items')
  // Its runs are marked in the timeline.
  expect(steps(root).filter(step => step.classList.contains('hs-trace-step--node'))).toHaveLength(3)
  const chooser = panel(root).querySelector<HTMLSelectElement>('select')!
  expect([...chooser.options].map(o => o.textContent)).toEqual(['Iteration 1 of 3', 'Iteration 2 of 3 · Recovered', 'Iteration 3 of 3'])
  chooser.value = chooser.options[1].value
  chooser.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(port(root, 'field:brightness_pct').textContent).toContain('81'))
  expect(panel(root).querySelector('.hs-trace-error')!.textContent).toContain('light.kitchen is unavailable')
  expect(panel(root).textContent).not.toContain('InvalidOperationException')
  expect(card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-value-chip')?.textContent).toBe('81')
  expect(steps(root).find(step => step.getAttribute('aria-current') === 'true')!.textContent).toContain('Iteration 2 of 3')
})

it('selects and centres a step chosen in the timeline, and keeps focus on the timeline', async () => {
  const { root } = mount(async () => response())
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  const note = steps(root)[5]
  note.focus(); note.click()
  await vi.waitFor(() => expect(card(root, 'note').querySelector('.hs-card')!.classList).toContain('hs-card--focus'))
  expect(panel(root).querySelector('.hs-trace-node__title')!.textContent).toBe('Log')
  expect(panel(root).textContent).toContain('Some values were too large to record.')
  expect(document.activeElement).toBe(note)
  // Arrow keys move along the timeline.
  note.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true, cancelable: true }))
  expect(document.activeElement).toBe(steps(root)[6])
  // A source pill selects the step it came from and moves focus to its title in the panel, never to the page.
  steps(root)[3].click()
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')!.textContent).toBe('Turn on'))
  const pill = port(root, 'target').querySelector<HTMLButtonElement>('.hs-trace-port__from')!
  pill.focus(); pill.click()
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')!.textContent).toBe('Each light'))
  await vi.waitFor(() => expect(document.activeElement).toBe(panel(root).querySelector('.hs-trace-node__title')))
  expect(port(root, 'items').querySelector('.hs-trace-value')!.textContent).toBe('List · 3 items')
  expect(port(root, 'items').querySelector('.hs-json-tree')!.textContent).toContain('light.kitchen')
  expect(card(root, 'lights').classList).toContain('selected')
})

it('polls a live run every 2 seconds and stops once it has finished', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let calls = 0
  const all = events()
  const { root } = mount(async () => {
    calls++
    const live = calls < 3
    const shown = live ? all.slice(0, calls + 2) : all
    return response({ live, run: run({ runId: 'run-1', status: live ? 'Running' : 'Succeeded', durationMs: live ? null : 900 }) }, { events: shown, complete: !live })
  })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(3))
  expect(root.querySelector('.hs-save-chip')!.textContent).toContain('Running')
  expect(root.textContent).toContain('Still running')
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(steps(root)).toHaveLength(4))
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  expect(root.querySelector('.hs-save-chip')!.textContent).toContain('Succeeded')
  await vi.advanceTimersByTimeAsync(6000)
  expect(calls).toBe(3)
})

it('opens a failed run on the step that failed, and says when the trace was cut or the automation changed since', async () => {
  const list = events().slice(0, 4).map(e => (e.node === 'service-1' ? { ...e, status: 'failed' as const, port: undefined, error: { type: 'X', message: 'No light' } } : e))
  const { root } = mount(async () => response({ run: run({ runId: 'run-1', status: 'Failed', error: 'No light\nstack', visualRevision: 4 }) }, { events: list, truncated: true, dropped: 12 }), { revision: 6 })
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  expect(panel(root).querySelector('.hs-trace-error')!.textContent).toContain('Failed here')
  expect(root.querySelector('.hs-trace-strip')!.textContent).toContain('No light')
  expect(root.querySelector('.hs-trace-strip')!.textContent).not.toContain('stack')
  expect(root.querySelector('.hs-trace-strip')!.textContent).toContain('12 steps are missing')
  expect(root.querySelector('.hs-trace-timeline__end')!.textContent).toContain('12 more steps were not recorded')
  await vi.waitFor(() => expect(root.textContent).toContain('This run used an earlier version'))
})

it('explains a run without a trace, and one whose graph is gone too', async () => {
  const { root } = mount(async () => response({ trace: null }))
  await vi.waitFor(() => expect(root.querySelector('.hs-flow')).not.toBeNull())
  expect(root.textContent).toContain('No step-by-step record was kept for this run')
  expect(root.querySelector('.hs-card--unreached')).toBeNull()
  expect(root.querySelector('.hs-drawer')).toBeNull()
  dispose?.(); document.body.innerHTML = ''
  const second = mount(async () => response({ trace: null, snapshot: null }))
  await vi.waitFor(() => expect(second.root.textContent).toContain('This run can no longer be shown on the canvas'))
  expect(second.root.querySelector('.hs-flow')).toBeNull()
  expect(second.root.textContent).toContain('Motion in the hall')
  expect(second.root.textContent).toContain('binary_sensor.motion: off → on')
})

it('says when the run cannot be loaded or the user is not an administrator', async () => {
  const failing = mount(async () => { throw { code: 'not_found', message: 'Run run-1 was not found.' } })
  await vi.waitFor(() => expect(failing.root.textContent).toContain('Run run-1 was not found.'))
  dispose?.(); document.body.innerHTML = ''
  const user = mount(async () => response(), { admin: false })
  expect(user.root.textContent).toContain('Administrator access is required')
  expect(user.callWS).not.toHaveBeenCalled()
})

it('below 900px: the value panel is a sheet that a canvas selection folds to its peek bar, and changes made inside it keep it open', async () => {
  vi.spyOn(window, 'matchMedia').mockImplementation(query => ({ matches: query.includes('max-width: 899px'), media: query, addEventListener() {}, removeEventListener() {} }) as any)
  const { root } = mount(async () => response())
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  // Closed by default on a phone: the timeline is one line, nothing selected shows the run's peek bar.
  expect(root.querySelector('.hs-drawer')!.classList).not.toContain('hs-drawer--open')
  expect(panel(root).dataset.peek).toBe('true')
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-insp-peek')?.textContent).toContain('Turn on'))
  expect(panel(root).dataset.open).toBe('false')
  panel(root).querySelector<HTMLButtonElement>('.hs-insp-peek')!.click()
  await vi.waitFor(() => expect(panel(root).dataset.open).toBe('true'))
  await vi.waitFor(() => expect(document.activeElement).toBe(panel(root).querySelector('.hs-trace-node__title')))
  const chooser = panel(root).querySelector<HTMLSelectElement>('select')!
  chooser.focus()
  chooser.value = chooser.options[0].value
  chooser.dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(port(root, 'field:brightness_pct').textContent).toContain('80'))
  expect(panel(root).dataset.open).toBe('true')
  expect(document.activeElement).toBe(chooser)
  // Closing puts focus back on the step's card, never on the page.
  panel(root).querySelector<HTMLButtonElement>('.hs-insp-close')!.click()
  await vi.waitFor(() => expect(panel(root).dataset.open).toBe('false'))
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, 'service-1')))
})

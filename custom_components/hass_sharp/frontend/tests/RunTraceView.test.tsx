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
  // A value wire into a step whose values were too large to record: it cannot be shown as travelled.
  g = { ...g, edges: [...g.edges, { id: 'lights.item->note.value', kind: 'data', source: 'lights', sourcePort: 'item', target: 'note', targetPort: 'value' }] }
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
        inputs: { target: { type: 'entity_id', v: light, src: { node: 'lights', port: 'item' } }, 'field:brightness_pct': { type: 'number', v: 80 + i }, 'field:api_key': { type: 'string', v: '***', redacted: true },
          'field:data': { type: 'object', v: { color: { rgb: { r: 255, g: 180 + i } }, level: i } } },
        ...(i === 1 ? { error: { type: 'InvalidOperationException', message: `${light} is unavailable` } } : {}) },
      ...(i === 1 ? [{ node: 'note', frame: `lights#${i}:body`, t: 260 + i * 300, ms: 1, status: 'ok' as const, port: 'next', trunc: true,
        // What the recorder writes in place of values it did not keep, inside a value and as a whole value.
        inputs: {
          value: { type: 'object', v: { api_key: '***', note: '***', reader: 'global::Hall.Reader', state: { token: '***' } }, trunc: true },
          extra: { type: 'object', v: null, trunc: true },
        } }] : []),
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
function mount(trace: (message: any) => Promise<unknown>, options: { admin?: boolean; revision?: number; path?: string; services?: unknown } = {}) {
  layout()
  const root = document.createElement('div'); root.className = 'hs-app'; document.body.append(root)
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const history = createMemoryHistory()
  history.set({ value: options.path ?? '/visual/heating/runs/run-1' })
  const callWS = vi.fn(async (message: any) => {
    if (message.type === 'hass_sharp/run_trace') return trace(message)
    if (message.type === 'hass_sharp/visual' && message.request.operation === 'read')
      return { version: 1, ok: true, result: { id: message.request.id, revision: options.revision ?? 4, graph: graph() } }
    if (message.type === 'hass_sharp/node_library' && message.request.operation === 'services' && options.services)
      return { version: 1, ok: true, result: options.services }
    return []
  })
  dispose = render(() => (
    <QueryClientProvider client={client!}>
      <HassCtx.Provider value={{ callWS, user: { is_admin: options.admin ?? true } } as any}>
        <MemoryRouter history={history}>
          <Route path="/visual/:id/runs/:runId" component={RunTraceView} />
          <Route path="/visual/:id" component={() => <p class="editor-page">Editor page</p>} />
        </MemoryRouter>
      </HassCtx.Provider>
    </QueryClientProvider>
  ), root)
  return { root, callWS, history }
}
const card = (root: Element, id: string) => root.querySelector<HTMLElement>(`.solid-flow__node[data-id="${id}"]`)!
const steps = (root: Element) => [...root.querySelectorAll<HTMLButtonElement>('.hs-trace-step')]
const panel = (root: Element) => root.querySelector<HTMLElement>('[aria-label="Step values"]')!
/** The option a closed select shows: the selected one, else (none selected) the first that is not disabled. */
const shown = (select: HTMLSelectElement) => {
  const options = [...select.options]
  const selected = options.findIndex(option => option.selected)
  return selected >= 0 ? selected : options.findIndex(option => !option.disabled)
}
/**
 * Picks an option the way a person does: a disabled option cannot be picked, and picking the option already shown
 * fires nothing (a browser fires "change" only when the selection changes).
 */
function choose(select: HTMLSelectElement, text: string) {
  const index = [...select.options].findIndex(option => option.textContent === text)
  expect(index, text).toBeGreaterThanOrEqual(0)
  expect(select.options[index].disabled, `${text} is disabled`).toBe(false)
  if (shown(select) === index) return
  select.options[index].selected = true
  select.dispatchEvent(new Event('input', { bubbles: true }))
  select.dispatchEvent(new Event('change', { bubbles: true }))
}
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
  // The same revision as the automation now: no note, once the automation has been read.
  await vi.waitFor(() => expect(client!.getQueryState(['visual-trace-document', 'heating'])?.status).toBe('success'))
  // Its result reaches the page through Solid Query's notification (a later task): give it time to arrive.
  await new Promise(resolve => setTimeout(resolve, 50))
  expect(root.textContent).not.toContain('earlier version')
  // Read once, for the run's automation.
  expect(callWS.mock.calls.filter(([message]) => message.type === 'hass_sharp/visual')).toHaveLength(1)
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
  expect([...chooser.options].map(o => o.textContent)).toEqual(['Whole run', 'Iteration 1 of 3', 'Iteration 2 of 3 · Recovered', 'Iteration 3 of 3'])
  // The whole run is on view until an iteration is chosen; the panel says which run it shows.
  expect(chooser.selectedIndex).toBe(0)
  expect(panel(root).textContent).toContain('Showing its last run: Iteration 3 of 3.')
  const unreached = (id: string) => card(root, id).querySelector('.hs-card')!.classList.contains('hs-card--unreached')
  expect(unreached('note')).toBe(false)
  choose(chooser, 'Iteration 2 of 3 · Recovered')
  await vi.waitFor(() => expect(port(root, 'field:brightness_pct').textContent).toContain('81'))
  expect(panel(root).querySelector('.hs-trace-error')!.textContent).toContain('light.kitchen is unavailable')
  expect(panel(root).textContent).not.toContain('InvalidOperationException')
  expect(card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-value-chip')?.textContent).toBe('81')
  expect(steps(root).find(step => step.getAttribute('aria-current') === 'true')!.textContent).toContain('Iteration 2 of 3')
  expect(chooser.selectedIndex).toBe(2)
  // The canvas shows that iteration: the recovered status, the error wire it took.
  const travelled = (id: string) => root.querySelector(`.solid-flow__edge[data-id="${id}"]`)!.outerHTML.includes('hs-wire--travelled')
  await vi.waitFor(() => expect(card(root, 'service-1').querySelector('.hs-card')!.className).toContain('error'))
  expect(travelled('service-1:error->note')).toBe(true)
  // An iteration that did not fail: the step after the error did not run in it, nor was its wire travelled.
  choose(chooser, 'Iteration 3 of 3')
  await vi.waitFor(() => expect(unreached('note')).toBe(true))
  expect(travelled('service-1:error->note')).toBe(false)
  expect(unreached('lights')).toBe(false)
  expect(travelled('condition-1:true->lights')).toBe(true)
  // A dimmed step of the loop, chosen on the canvas, says it did not run in that iteration.
  card(root, 'note').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).textContent).toContain('This step did not run in Iteration 3 of 3.'))
  // The control says the same as the canvas: that iteration, which this step did not run in.
  const again = panel(root).querySelector<HTMLSelectElement>('select')!
  expect(again.options[shown(again)].textContent).toBe('Iteration 3 of 3 (did not run)')
  expect(again.options[shown(again)].disabled).toBe(true)
  // "Whole run" can be chosen from there, and puts back the whole picture.
  choose(again, 'Whole run')
  await vi.waitFor(() => expect(unreached('note')).toBe(false))
  expect(again.options[shown(again)].textContent).toBe('Whole run')
  expect(travelled('service-1:error->note')).toBe(true)
})

it('selects and centres a step chosen in the timeline, and keeps focus on the timeline', async () => {
  const { root } = mount(async () => response())
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  const note = steps(root)[5]
  note.focus(); note.click()
  await vi.waitFor(() => expect(card(root, 'note').querySelector('.hs-card')!.classList).toContain('hs-card--focus'))
  expect(panel(root).querySelector('.hs-trace-node__title')!.textContent).toBe('Log')
  expect(panel(root).textContent).toContain('Some values were too large to record, so their wires are not highlighted.')
  expect(root.querySelector('.solid-flow__edge[data-id="lights.item->note.value"]')!.outerHTML).not.toContain('hs-wire--travelled')
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

it('polls a live run every 2 seconds, keeps what was opened and the cards across polls, and stops once it has finished', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let calls = 0
  const all = events()
  // 1: up to the second iteration's note; 2: the same again; 3: one more event; 4: the end.
  const counts = [6, 6, 7]
  const { root } = mount(async () => {
    calls++
    const live = calls <= counts.length
    const shown = live ? all.slice(0, counts[calls - 1]) : all
    return response({ live, run: run({ runId: 'run-1', status: live ? 'Running' : 'Succeeded', durationMs: live ? null : 900 }) }, { events: shown, complete: !live })
  })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(6))
  expect(root.querySelector('.hs-save-chip')!.textContent).toContain('Running')
  expect(root.textContent).toContain('Still running')
  // On "Turn on": choose the first of its runs, and open a recorded value two levels deep.
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  const chooser = () => panel(root).querySelector<HTMLSelectElement>('select')!
  chooser().value = chooser().options[1].value
  chooser().dispatchEvent(new Event('change', { bubbles: true }))
  await vi.waitFor(() => expect(port(root, 'field:brightness_pct').textContent).toContain('80'))
  const level = (depth: number) => [...port(root, 'field:data').querySelectorAll<HTMLDetailsElement>('details')][depth]
  const toggle = (details: HTMLDetailsElement) => { details.open = true; details.dispatchEvent(new Event('toggle')) }
  toggle(level(0)); toggle(level(1))
  const opened = [level(0), level(1)]
  expect(port(root, 'field:data').textContent).toContain('g 180')
  const cards = [...root.querySelectorAll('.solid-flow__node .hs-card')]
  const viewport = root.querySelector<HTMLElement>('.solid-flow__viewport')!.style.transform
  const values = panel(root).querySelector('.hs-trace-node')
  // Two polls: one with nothing new, one that adds an event.
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(calls).toBe(2))
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  expect(chooser().selectedIndex).toBe(1)
  expect(chooser().options[chooser().selectedIndex].textContent).toBe('Iteration 1 of 3')
  expect(port(root, 'field:brightness_pct').textContent).toContain('80')
  expect(panel(root).querySelector('.hs-trace-node')).toBe(values)
  expect(level(0)).toBe(opened[0])
  expect(level(1)).toBe(opened[1])
  expect(opened.every(details => details.open)).toBe(true)
  expect(port(root, 'field:data').textContent).toContain('g 180')
  expect([...root.querySelectorAll('.solid-flow__node .hs-card')].every((el, i) => el === cards[i])).toBe(true)
  expect(root.querySelector<HTMLElement>('.solid-flow__viewport')!.style.transform).toBe(viewport)
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(root.querySelector('.hs-save-chip')!.textContent).toContain('Succeeded'))
  await vi.advanceTimersByTimeAsync(6000)
  expect(calls).toBe(4)
})

it('keeps the last reply when a read of a live run gets no answer, says updates are paused, and goes on once a read succeeds', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let calls = 0
  const all = events()
  // The websocket library rejects a request that got no answer with a numeric code.
  const lost = { code: 3, message: 'Connection lost' }
  const { root } = mount(async () => {
    calls++
    if (calls === 2) throw lost
    const live = calls < 4
    return response({ live, run: run({ runId: 'run-1', status: live ? 'Running' : 'Succeeded', durationMs: live ? null : 900 }) }, { events: all.slice(0, calls + 2), complete: !live })
  })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(3))
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(root.textContent).toContain('Updates paused: could not reach Home Assistant.'))
  expect(steps(root)).toHaveLength(3)
  expect(root.textContent).toContain('updates paused')
  // Still tried again (less often), and the note goes once a read succeeds.
  await vi.advanceTimersByTimeAsync(5000)
  await vi.waitFor(() => expect(steps(root)).toHaveLength(5))
  expect(root.textContent).not.toContain('Updates paused')
  expect(root.textContent).toContain('updates every 2 seconds')
})

it('keeps "Try again" in place and focused while it reads, then moves focus to the title', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let calls = 0
  let release: (() => void) | undefined
  const all = events()
  const { root } = mount(async () => {
    calls++
    if (calls === 2) throw { code: 3, message: 'Connection lost' }
    // The read "Try again" starts: it waits until released.
    if (calls === 3) await new Promise<void>(resolve => (release = resolve))
    return response({ live: true, run: run({ runId: 'run-1', status: 'Running', durationMs: null }) }, { events: all.slice(0, calls + 2), complete: false })
  })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(3))
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(root.querySelector('.hs-trace-paused')).not.toBeNull())
  const note = root.querySelector('.hs-trace-paused')!
  const status = note.querySelector('[role="status"]')!
  const retry = note.querySelector<HTMLButtonElement>('.hs-trace-paused__retry')!
  retry.focus(); retry.click()
  await vi.waitFor(() => expect(release).toBeDefined())
  // Reading: the same note, status and button, still focused, busy.
  await vi.waitFor(() => expect(retry.textContent).toBe('Trying…'))
  expect(retry.getAttribute('aria-busy')).toBe('true')
  expect(root.querySelector('.hs-trace-paused')).toBe(note)
  expect(note.querySelector('[role="status"]')).toBe(status)
  expect(retry.isConnected).toBe(true)
  expect(document.activeElement).toBe(retry)
  // Pressing it again while it reads does nothing.
  retry.click()
  expect(calls).toBe(3)
  release!()
  await vi.waitFor(() => expect(root.querySelector('.hs-trace-paused')).toBeNull())
  await vi.waitFor(() => expect(document.activeElement).toBe(root.querySelector('.hs-topbar__heading')))
})

it('shows an error reply\'s own words, and waits for "Try again" before reading again', async () => {
  vi.useFakeTimers({ shouldAdvanceTime: true })
  let calls = 0
  const all = events()
  const { root } = mount(async () => {
    calls++
    if (calls === 2) throw { code: 'not_found', message: 'Run run-1 was not found.' }
    return response({ live: true, run: run({ runId: 'run-1', status: 'Running', durationMs: null }) }, { events: all.slice(0, calls + 2), complete: false })
  })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(3))
  await vi.advanceTimersByTimeAsync(2000)
  await vi.waitFor(() => expect(root.textContent).toContain('Updates paused: Run run-1 was not found.'))
  expect(root.textContent).not.toContain('could not reach')
  await vi.advanceTimersByTimeAsync(12000)
  expect(calls).toBe(2)
  root.querySelector<HTMLButtonElement>('.hs-trace-paused__retry')!.click()
  await vi.waitFor(() => expect(steps(root)).toHaveLength(5))
  expect(root.textContent).not.toContain('Updates paused')
})

it('shows another run when the run id changes in place', async () => {
  const { root, history } = mount(async message => (message.runId === 'run-1'
    ? response()
    : response({ run: run({ runId: 'run-2', visualId: 'heating', visualRevision: 4, actionLabel: 'Second run' }) }, { runId: 'run-2', events: events().slice(0, 3) })))
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  const errors = vi.spyOn(console, 'error')
  history.set({ value: '/visual/heating/runs/run-2' })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(3))
  expect(root.querySelector('.hs-trace-strip')!.textContent).toContain('Second run')
  expect(errors).not.toHaveBeenCalled()
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
  choose(chooser, 'Iteration 1 of 3')
  await vi.waitFor(() => expect(port(root, 'field:brightness_pct').textContent).toContain('80'))
  expect(panel(root).dataset.open).toBe('true')
  expect(document.activeElement).toBe(chooser)
  // Closing puts focus back on the step's card, never on the page.
  panel(root).querySelector<HTMLButtonElement>('.hs-insp-close')!.click()
  await vi.waitFor(() => expect(panel(root).dataset.open).toBe('false'))
  await vi.waitFor(() => expect(document.activeElement).toBe(card(root, 'service-1')))
})

it('shows the recorder\'s placeholders inside a recorded value in words, and real text as it is', async () => {
  const { root } = mount(async () => response())
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  steps(root)[5].click()
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Log'))
  const leaves = () => [...port(root, 'value').querySelectorAll('.hs-json-leaf')].map(leaf => leaf.textContent)
  // A secret name's "***" is hidden; the same text under another name is real; an unreadable value is not a type name.
  expect(leaves()).toEqual(['api_key Hidden', 'note "***"', 'reader Could not be recorded'])
  expect(port(root, 'value').textContent).not.toContain('global::')
  const state = port(root, 'value').querySelector<HTMLDetailsElement>('details')!
  state.open = true; state.dispatchEvent(new Event('toggle'))
  expect(leaves()).toContain('token Hidden')
  // A whole value cut to nothing could not be read.
  expect(port(root, 'extra').querySelector('.hs-trace-value')!.textContent).toBe('Could not be recorded')
})

it('reads the recorder\'s placeholders in the trigger data the same way', async () => {
  const { root } = mount(async () => response({}, { trigger: { platform: 'webhook', token: '***', note: '***', json: { password: '***' } } }))
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  const tree = panel(root).querySelector<HTMLElement>('[aria-label="Trigger data"]')!
  const leaves = () => [...tree.querySelectorAll('.hs-json-leaf')].map(leaf => leaf.textContent)
  expect(leaves()).toEqual(['platform "webhook"', 'token Hidden', 'note "***"'])
  const json = tree.querySelector<HTMLDetailsElement>('details')!
  json.open = true; json.dispatchEvent(new Event('toggle'))
  expect(leaves()).toContain('password Hidden')
})

it('goes back to the automation\'s Runs tab, and reads the automation the run belongs to', async () => {
  const { root, callWS, history } = mount(async () => response({ run: run({ runId: 'run-1', visualId: 'heating', visualRevision: 4 }) }), { path: '/visual/old-name/runs/run-1', revision: 5 })
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  expect(root.querySelector<HTMLAnchorElement>('a.hs-trace-open')!.getAttribute('href')).toBe('/visual/heating')
  await vi.waitFor(() => expect(root.textContent).toContain('This run used an earlier version'))
  expect(callWS).toHaveBeenCalledWith(expect.objectContaining({ request: expect.objectContaining({ operation: 'read', id: 'heating' }) }))
  root.querySelector<HTMLButtonElement>('[aria-label="Back to the automation’s runs"]')!.click()
  await vi.waitFor(() => expect(root.querySelector('.editor-page')).not.toBeNull())
  expect(history.get()).toBe('/visual/heating?drawer=runs')
})

it('names service fields and their options from the live services when the run\'s snapshot has no schema', async () => {
  const flash = (i: number) => ({ type: 'string', v: i === 1 ? 'short' : 'long' })
  const reply = response({ snapshot: { revision: 'rev', visualId: 'heating', visualRevision: 4, graph: (() => {
    const g = graph()
    const service = g.nodes.find(n => n.id === 'service-1')!
    service.config = { ...service.config, fields: { ...(service.config.fields as object), flash: lit('string', 'long') } }
    return g
  })(), catalog: nodeCatalog } }, { events: events().map(e => e.node === 'service-1' ? { ...e, inputs: { ...e.inputs, 'field:flash': flash(Number(e.frame.at(6))) } } : e) })
  const { root, callWS } = mount(async () => reply, { services })
  await vi.waitFor(() => expect(callWS).toHaveBeenCalledWith({ type: 'hass_sharp/node_library', request: { operation: 'services' } }))
  // The schema's label ("Brightness", not the key in words) and the option in words ("Long", not "long"), on the card and in the panel.
  await vi.waitFor(() => expect(card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-pin-label')?.textContent).toBe('Brightness'))
  expect(card(root, 'service-1').querySelector('[data-input-id="field:flash"] .hs-value-chip')?.textContent).toBe('Long')
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  expect(port(root, 'field:brightness_pct').querySelector('.hs-trace-port__name')!.textContent).toBe('Brightness')
  expect(port(root, 'field:flash').querySelector('.hs-trace-value')!.textContent).toBe('Long')
})

it('names service fields by their key in words when the service is not known', async () => {
  const reply = response({ snapshot: { revision: 'rev', visualId: 'heating', visualRevision: 4, graph: graph(), catalog: nodeCatalog } })
  const { root } = mount(async () => reply)
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  expect(port(root, 'field:brightness_pct').querySelector('.hs-trace-port__name')!.textContent).toBe('Brightness pct')
  expect(card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-pin-label')?.textContent).toBe('Brightness pct')
})

it('reads an empty value (null) as "Not set" in the muted style in the trigger data, a step\'s values and its card', async () => {
  const list = events().map(e => e.node === 'service-1' && e.frame === 'lights#2:body' ? { ...e, inputs: { ...e.inputs, 'field:brightness_pct': { type: 'number', v: null } } } : e)
  const { root } = mount(async () => response({}, { trigger: { platform: 'state', from_state: null, entity_id: 'light.hall' }, events: list }))
  await vi.waitFor(() => expect(steps(root)).toHaveLength(7))
  const tree = panel(root).querySelector<HTMLElement>('[aria-label="Trigger data"]')!
  const leaf = [...tree.querySelectorAll('.hs-json-leaf')].find(l => l.textContent?.startsWith('from_state'))!
  expect(leaf.textContent).toBe('from_state Not set')
  expect(leaf.querySelector('.hs-trace-value--muted')).not.toBeNull()
  const chip = await vi.waitFor(() => { const found = card(root, 'service-1').querySelector('[data-input-id="field:brightness_pct"] .hs-value-chip'); expect(found).not.toBeNull(); return found! })
  expect(chip.textContent).toBe('Not set')
  expect(chip.classList).toContain('hs-value-chip--muted')
  card(root, 'service-1').dispatchEvent(new MouseEvent('click', { bubbles: true }))
  await vi.waitFor(() => expect(panel(root).querySelector('.hs-trace-node__title')?.textContent).toBe('Turn on'))
  const value = port(root, 'field:brightness_pct').querySelector('.hs-trace-value')!
  expect(value.textContent).toBe('Not set')
  expect(value.classList).toContain('hs-trace-value--muted')
  expect(root.querySelector('.hs-trace-page, .hs-visual')?.textContent ?? root.textContent).not.toMatch(/\bnull\b/)
})

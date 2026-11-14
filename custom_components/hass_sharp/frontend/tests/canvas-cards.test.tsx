import { afterEach, expect, it, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { FlowAdapter, type FlowTrace } from '../src/visual/FlowAdapter'
import { VisualEditor } from '../src/visual/VisualEditor'
import { HassCtx } from '../src/context'
import * as contract from '../src/visual/contract'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { visualFixture } from './visual-fixture'
import { Timeline } from '../src/visual/trace/Timeline'
import { flowTrace, traceModel } from '../src/visual/trace/traceModel'
import type { RunTrace, TraceEvent } from '../src/types'
import services from '../../../../tests/fixtures/visual-services.json'

// Counts full validations of a dropped wire (D12: hover is quick, a drop is validated once).
vi.mock('../src/visual/contract', async original => {
  const actual = await original<typeof import('../src/visual/contract')>()
  return { ...actual, connectData: vi.fn(actual.connectData) }
})

// Records where the canvas centres the view (a run's focus node).
const centred = vi.hoisted(() => ({ calls: [] as unknown[][] }))
vi.mock('@dschz/solid-flow', async original => {
  const actual = await original<typeof import('@dschz/solid-flow')>()
  return {
    ...actual,
    useSolidFlow: () => {
      const api = actual.useSolidFlow()
      return new Proxy(api, {
        get: (target, key) =>
          key === 'setCenter'
            ? (...args: Parameters<typeof api.setCenter>) => {
                centred.calls.push(args)
                return target.setCenter(...args)
              }
            : Reflect.get(target, key),
      })
    },
  }
})

let dispose: (() => void) | undefined
afterEach(async () => {
  dispose?.()
  vi.restoreAllMocks()
  vi.mocked(contract.connectData).mockClear()
  document.body.innerHTML = ''
  await new Promise(resolve => setTimeout(resolve, 0))
})
const host = () => {
  const el = document.createElement('div')
  el.className = 'hs-app'
  document.body.append(el)
  return el
}
const layout = () => {
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
}
const button = (root: Element, label: string) =>
  [...root.querySelectorAll<HTMLButtonElement>('button')].find(b => b.textContent?.trim() === label || b.getAttribute('aria-label') === label)!
/** trigger-1 → condition-1 → service-1 (the default graph), plus a number value and a list that feed a log step. */
/** The built-in catalog with the C4 services section, so service fields have their schema. */
const withServices = (edit?: (section: contract.ServicesSection) => void) => {
  const section = structuredClone(services) as unknown as contract.ServicesSection
  edit?.(section)
  return { ...contract.nodeCatalog, services: section }
}
const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
/** Transition as a typed number field instead of a slider, so a draft can be refused. */
const typedTransition = (section: contract.ServicesSection) => {
  ;(section as any).items['light.turn_on'].fields.transition.selector.number.mode = 'box'
}
/** The default graph's Call service step turning on a light, with the given service fields. */
const lightOn = (fields: Record<string, unknown>) =>
  updateNode(typed(), 'service-1', { config: { ...typed().nodes.find(n => n.id === 'service-1')!.config, service: 'light.turn_on', fields } })
const providers = (children: () => any, fixture = visualFixture()) => (
  <QueryClientProvider client={new QueryClient()}>
    <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>{children()}</HassCtx.Provider>
  </QueryClientProvider>
)
const typed = () => {
  let graph = addNode(createGraph('Cards'), 'constant', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 20 } } })
  graph = addNode(graph, 'constant', 'rooms')
  graph = updateNode(graph, 'rooms', { config: { type: 'array', value: { kind: 'literal', type: 'array', value: [] } } })
  // Constants are values: they take no place in the run order, so there is nothing to connect by execution.
  return graph
}

it('draws execution and typed data pins, with inputs on the left and outputs on the right of shared rows', () => {
  const el = host()
  dispose = render(() => <FlowAdapter graph={typed()} onSelect={() => {}} onChange={() => {}} />, el)
  const card = el.querySelector('[data-id="temperature"] .hs-card')!
  expect(card.querySelector('.hs-card__title')?.textContent).toBe('Constant')
  expect(card.getAttribute('data-category')).toBe('data')
  // A value node has no execution pins and no "On error" row, and a lighter, compact card.
  expect(card.classList).toContain('hs-card--pure')
  expect(card.querySelector('[data-handleid="in"], [data-handleid="next"], [data-handleid="error"], .hs-card__row--footer')).toBeNull()
  expect(card.textContent).not.toContain('On error')
  expect(el.querySelector('[data-id="service-1"] .hs-card')!.classList).not.toContain('hs-card--pure')
  // A step's execution pins sit in the header; the default output is the header's right pin.
  const step = el.querySelector('[data-id="service-1"] .hs-card')!
  const execIn = step.querySelector('.hs-card__head [data-handleid="in"]')!
  expect(execIn.classList).toContain('hs-pin--exec')
  expect(execIn.classList).toContain('hs-pin--connected')
  expect(step.querySelector('.hs-card__head [data-handleid="next"]')?.classList).toContain('hs-pin--exec')
  // One row carries the Value input (left) and the Value output (right).
  const row = card.querySelector('[data-input-id="value"]')!.closest('.hs-card__row')!
  expect(row.querySelector('[data-output-id="value"]')).not.toBeNull()
  const input = row.querySelector<HTMLElement>('[data-handleid="data:value"].target')!
  const output = row.querySelector<HTMLElement>('[data-handleid="data:value"].source')!
  for (const pin of [input, output]) {
    expect(pin.classList).toContain('hs-pin--data')
    expect(pin.style.getPropertyValue('--hs-pin-color')).toBe('var(--hs-port-number)')
    expect(pin.getAttribute('data-type')).toBe('number')
  }
  expect(input.classList).toContain('hs-pin--in')
  expect(output.classList).toContain('hs-pin--out')
  expect(input.getAttribute('aria-label')).toBe('Input Value, Number, required, not connected, has a value')
  // Lists are drawn as squares; entity ids and targets have their own colours.
  const list = el.querySelector<HTMLElement>('[data-id="rooms"] .source[data-handleid="data:value"]')!
  expect(list.classList).toContain('hs-pin--list')
  expect(list.style.getPropertyValue('--hs-pin-color')).toBe('var(--hs-port-array)')
  expect(el.querySelector<HTMLElement>('[data-id="trigger-1"] .source[data-handleid="data:entity_id"]')!.style.getPropertyValue('--hs-pin-color')).toBe(
    'var(--hs-port-entity)',
  )
  expect(el.querySelector<HTMLElement>('[data-id="service-1"] .target[data-handleid="data:target"]')!.style.getPropertyValue('--hs-pin-color')).not.toBe(
    'var(--hs-port-object)',
  )
  // Triggers have no execution input and look different from steps.
  expect(el.querySelector('[data-id="trigger-1"] [data-handleid="in"]')).toBeNull()
  expect(el.querySelector('[data-id="trigger-1"] .hs-card')?.classList).toContain('hs-card--trigger')
  // The error output is a secondary footer row.
  expect(el.querySelector('[data-id="service-1"] .hs-card__row--footer [data-handleid="error"]')?.classList).toContain('hs-pin--error')
})

it('shows the wired source on a connected input instead of an editor', () => {
  const el = host()
  const graph = contract.connectData(typed(), 'temperature', 'value', 'rooms', 'value')
  dispose = render(() => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />, el)
  const cell = el.querySelector('[data-id="rooms"] [data-input-id="value"]')!
  expect(cell.querySelector('.hs-source-chip')?.textContent).toBe('Constant · Value')
  expect(cell.querySelector('.hs-card-editor')).toBeNull()
  expect(cell.querySelector('[data-handleid="data:value"]')?.getAttribute('aria-label')).toBe('Input Value, Number, required, connected to Constant · Value')
})

it('edits an unconnected input on the card: one graph change, the same value in the inspector, no canvas keys', async () => {
  const el = host()
  const fixture = visualFixture()
  dispose = render(
    () => (
      <QueryClientProvider client={new QueryClient()}>
        <HassCtx.Provider value={{ ...fixture, user: { is_admin: true } } as any}>
          <VisualEditor name="Inline" />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    el,
  )
  button(el, 'Add step').click()
  button(el, 'Create Delay').click()
  button(el, 'Graph JSON').click()
  const graphJson = () => JSON.parse(el.querySelector('pre[aria-label="Graph JSON"]')!.textContent!)
  const delay = graphJson().nodes.at(-1)
  const card = el.querySelector<HTMLElement>(`[data-id="${delay.id}"]`)!
  const field = card.querySelector<HTMLInputElement>('.hs-card-editor input[aria-label="Seconds"]')!
  expect(field.value).toBe('60')
  const before = graphJson()
  field.focus()
  // Keys typed into a card editor never move the node, delete it or reach the editor's shortcuts.
  for (const key of ['ArrowRight', 'Delete', 'Backspace'])
    field.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  expect(graphJson()).toEqual(before)
  field.value = '45'
  field.dispatchEvent(new Event('input', { bubbles: true }))
  // Enter commits through the field's delegated handler, inside the editor's keyboard boundary.
  field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(graphJson().nodes.at(-1).config.seconds).toEqual({ kind: 'literal', type: 'number', value: 45 })
  expect(graphJson().nodes.at(-1).position).toEqual(delay.position)
  // Both views read the graph: the inspector shows the value typed on the card.
  await vi.waitFor(() => expect(el.querySelector<HTMLInputElement>('.hs-inspector input[aria-label="Seconds"]')?.value).toBe('45'))
  // One edit, one undo step.
  button(el, 'Undo').click()
  expect(graphJson().nodes.at(-1).config.seconds).toEqual({ kind: 'literal', type: 'number', value: 60 })
  expect(card.querySelector<HTMLInputElement>('.hs-card-editor input[aria-label="Seconds"]')!.value).toBe('60')
  button(el, 'Redo').click()
  expect(graphJson().nodes.at(-1).config.seconds).toEqual({ kind: 'literal', type: 'number', value: 45 })
})

it('passes the coalesce key of an inline edit to onChange: one key for every step of a slider drag, none for a typed value', () => {
  const el = host()
  const changes: [VisualGraph, { coalesce?: string } | undefined][] = []
  const graph = lightOn({ brightness_pct: lit('number', 50) })
  dispose = render(() => providers(() => <FlowAdapter graph={graph} catalog={withServices()} onSelect={() => {}} onChange={(next, options) => changes.push([next, options])} />), el)
  const slider = el.querySelector<HTMLInputElement>('[data-id="service-1"] [data-input-id="field:brightness_pct"] .hs-card-editor input[type="range"]')!
  expect(slider).not.toBeNull()
  for (const value of ['60', '70']) {
    slider.value = value
    slider.dispatchEvent(new Event('input', { bubbles: true }))
  }
  slider.dispatchEvent(new Event('change', { bubbles: true }))
  expect(changes.map(([next]) => (next.nodes.find(n => n.id === 'service-1')!.config.fields as any).brightness_pct.value)).toEqual([60, 70])
  const key = changes[0][1]?.coalesce
  expect(key).toEqual(expect.any(String))
  expect(changes.map(([, options]) => options?.coalesce)).toEqual([key, key])
  // A typed value is its own undo step.
  const field = el.querySelector<HTMLInputElement>('[data-id="temperature"] .hs-card-editor input')!
  field.value = '21'
  field.dispatchEvent(new Event('input', { bubbles: true }))
  field.dispatchEvent(new FocusEvent('blur'))
  field.dispatchEvent(new FocusEvent('focusout', { bubbles: true }))
  expect(changes).toHaveLength(3)
  expect(changes[2][0].nodes.find(n => n.id === 'temperature')!.config.value).toEqual({ kind: 'literal', type: 'number', value: 21 })
  expect(changes[2][1]).toBeUndefined()
})

it('selects a card when it is clicked', () => {
  const el = host()
  const select = vi.fn()
  dispose = render(() => <FlowAdapter graph={typed()} onSelect={select} onChange={() => {}} />, el)
  el.querySelector<HTMLElement>('[data-id="temperature"] .hs-card__title')!.click()
  expect(select).toHaveBeenLastCalledWith('temperature')
})

it('opens the problem in the panel from the card badge', async () => {
  const el = host()
  const select = vi.fn()
  dispose = render(
    () => (
      <div class="hs-visual">
        <FlowAdapter
          graph={typed()}
          onSelect={select}
          onChange={() => {}}
          diagnostics={[{ severity: 3, message: 'Value is required', nodeId: 'temperature', inputId: 'value', startLine: null, startColumn: null }]}
        />
        <aside class="hs-inspector">
          <div data-input-id="value">
            <input aria-label="Inspector value" />
          </div>
        </aside>
      </div>
    ),
    el,
  )
  const card = el.querySelector('[data-id="temperature"] .hs-card')!
  expect(card.classList).toContain('hs-flow-card--error')
  expect(card.querySelector('[data-handleid="data:value"].target')?.getAttribute('aria-invalid')).toBe('true')
  const badge = card.querySelector<HTMLButtonElement>('.hs-card__badge')!
  expect(badge.textContent).toBe('1')
  expect(badge.getAttribute('aria-label')).toBe('1 problem: show in the panel')
  badge.click()
  expect(select).toHaveBeenLastCalledWith('temperature')
  await vi.waitFor(() => expect(document.activeElement?.getAttribute('aria-label')).toBe('Inspector value'))
})

it('in read-only mode ignores drags, connections, deletes and keyboard edits and shows traced values', async () => {
  layout()
  const el = host()
  const onChange = vi.fn()
  const graph = typed()
  const trace: FlowTrace = {
    nodes: { 'trigger-1': { status: 'ok', runs: 1, ms: 4 }, 'condition-1': { status: 'ok', runs: 1 }, 'service-1': { status: 'ok', runs: 1, ms: 120 }, temperature: { status: 'ok', runs: 3, ms: 2 } },
    edges: new Set(['trigger-1->condition-1']),
    value: (nodeId, port, side) => (nodeId === 'temperature' && port === 'value' ? { type: 'number', v: side === 'in' ? 21.5 : 22 } : undefined),
  }
  const outside = vi.fn()
  el.addEventListener('keydown', outside)
  // The node is selected, so arrow keys would move it if read-only mode let them.
  dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} selectedId="temperature" onSelect={() => {}} onChange={onChange} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  // Values are chips, not editors.
  expect(el.querySelector('.hs-card-editor')).toBeNull()
  expect(el.querySelector('[data-id="temperature"] [data-input-id="value"] .hs-value-chip')?.textContent).toBe('21.5')
  expect(el.querySelector('[data-id="temperature"] [data-output-id="value"] .hs-value-chip')?.textContent).toBe('22')
  expect(el.querySelector('[data-id="temperature"] .hs-card__runs')?.textContent).toBe('×3')
  // Nothing is draggable or connectable.
  const node = el.querySelector<HTMLElement>('[data-id="temperature"]')!
  expect(node.classList).not.toContain('draggable')
  expect(el.querySelector('.solid-flow__handle.connectionindicator')).toBeNull()
  node.dispatchEvent(new MouseEvent('mousedown', { clientX: 80, clientY: 110, button: 0, bubbles: true, view: window }))
  for (const [x, y] of [[83, 113], [143, 143], [203, 173]]) window.dispatchEvent(new MouseEvent('mousemove', { clientX: x, clientY: y, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mouseup', { clientX: 203, clientY: 173, button: 0, bubbles: true, view: window }))
  // Keyboard connection, movement and deletion.
  const source = el.querySelector<HTMLElement>('[data-id="temperature"] .source[data-handleid="data:value"]')!
  source.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(el.textContent).not.toContain('Select a destination port')
  // Pins stay reachable from the keyboard (their names say type and state) but do nothing.
  expect(source.tabIndex).toBe(0)
  for (const key of ['ArrowRight', 'ArrowDown']) node.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  for (const key of ['Delete', 'Backspace']) node.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }))
  expect(outside.mock.calls.map(([event]) => event.key)).not.toContain('Delete')
  expect(outside.mock.calls.map(([event]) => event.key)).not.toContain('Backspace')
  // Alt-click on a wire and a pane click (the step chooser) do nothing.
  el.querySelector('.solid-flow__edge')!.dispatchEvent(new MouseEvent('click', { altKey: true, button: 0, bubbles: true }))
  el.querySelector<HTMLElement>('.solid-flow__pane')!.dispatchEvent(new MouseEvent('click', { button: 0, bubbles: true, clientX: 300, clientY: 300 }))
  expect(el.querySelector('[aria-label="Search steps"]')).toBeNull()
  expect(onChange).not.toHaveBeenCalled()
})

it('marks trace statuses on cards and travelled wires', async () => {
  layout()
  const el = host()
  let graph = typed()
  graph = addNode(graph, 'stop', 'never')
  graph = addNode(graph, 'stop', 'halt')
  graph = connectNodes(graph, 'rooms', 'halt')
  const trace: FlowTrace = {
    nodes: {
      'trigger-1': { status: 'ok', runs: 1 },
      'condition-1': { status: 'error', runs: 1 },
      'service-1': { status: 'failed', runs: 2, ms: 1500 },
      temperature: { status: 'running', runs: 1 },
      rooms: { status: 'cancelled', runs: 1 },
      halt: { status: 'stopped', runs: 1, ms: 3 },
    },
    edges: new Set(['trigger-1->condition-1']),
    focusNodeId: 'service-1',
  }
  dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} />, el)
  await vi.waitFor(() => expect(el.querySelectorAll('.solid-flow__edge').length).toBe(graph.edges.length))
  const card = (id: string) => el.querySelector(`[data-id="${id}"] .hs-card`)!.classList
  expect(card('trigger-1')).toContain('hs-card--status-ok')
  expect(card('condition-1')).toContain('hs-card--status-error')
  expect(card('service-1')).toContain('hs-card--status-failed')
  expect(card('service-1')).toContain('hs-card--focus')
  expect(card('temperature')).toContain('hs-card--status-running')
  expect(card('rooms')).toContain('hs-card--status-cancelled')
  expect(card('never')).toContain('hs-card--unreached')
  expect(card('trigger-1')).not.toContain('hs-card--unreached')
  // Every state has its own icon and word in the header chip, never colour alone, and the card's name says it.
  const states: [string, string, string][] = [
    ['trigger-1', 'Done', 'Done'], ['condition-1', 'Recovered', 'Recovered'], ['service-1', 'Failed', 'Failed, 2 times, 1.5 s'],
    ['temperature', 'Running', 'Running'], ['rooms', 'Cancelled', 'Cancelled'], ['halt', 'Stopped', 'Stopped, 3 ms'], ['never', 'Not reached', 'Not reached'],
  ]
  const icons = new Set<string>()
  for (const [id, word, text] of states) {
    const chip = el.querySelector(`[data-id="${id}"] .hs-card__trace`)!
    expect(chip.querySelector('.hs-card__trace-word')?.textContent, id).toBe(word)
    icons.add([...chip.querySelectorAll('.hs-card__trace-icon path')].map(p => p.getAttribute('d')).join(' '))
    expect(chip.getAttribute('title'), id).toBe(text)
    expect(el.querySelector(`[data-id="${id}"]`)!.getAttribute('aria-label'), id).toMatch(new RegExp(`\\. Run: ${text}$`))
    // The status is described even without problems.
    const card = el.querySelector(`[data-id="${id}"] .hs-card`)!
    expect(el.querySelector(`#${card.getAttribute('aria-describedby')}`)?.textContent, id).toBe(text)
  }
  expect(icons.size).toBe(states.length)
  expect(el.querySelector('[data-id="service-1"] .hs-card__trace')?.textContent).toBe('Failed×21.5 s')
  expect(card('halt')).toContain('hs-card--status-stopped')
  const edge = (id: string) => el.querySelector(`.solid-flow__edge[data-id="${id}"]`)!
  expect(edge('trigger-1->condition-1').classList).toContain('hs-wire--travelled')
  expect(edge('trigger-1->condition-1').getAttribute('aria-label')).toMatch(/, ran$/)
  expect(edge('condition-1:true->service-1').classList).toContain('hs-wire--idle')
  expect(edge('condition-1:true->service-1').getAttribute('aria-label')).toMatch(/, not taken$/)
  expect(edge('condition-1:true->service-1').querySelector('.solid-flow__edge-path')?.classList).toContain('hs-wire--exec')
})

it('skips malformed nodes and edges instead of throwing', () => {
  const el = host()
  const graph = createGraph('Broken')
  const broken = {
    ...graph,
    nodes: [null, { id: 'no-config', kind: 'delay' }, ...graph.nodes, graph.nodes[0]],
    edges: [null, { id: 'dangling', kind: 'execution', source: 'trigger-1', target: 'missing' }, ...graph.edges],
  } as unknown as VisualGraph
  dispose = render(() => <FlowAdapter graph={broken} onSelect={() => {}} onChange={() => {}} />, el)
  expect([...el.querySelectorAll('.solid-flow__node')].map(n => n.getAttribute('data-id'))).toEqual(graph.nodes.map(n => n.id))
  // Hovering or picking an execution pin with malformed edges around still works.
  el.querySelector<HTMLElement>('[data-id="trigger-1"] [data-handleid="next"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(el.textContent).toContain('Choose a pin to connect to')
})

it('validates a dropped data wire once, and only the connection index while hovering', async () => {
  layout()
  const el = host()
  let graph = typed()
  graph = updateNode(graph, 'temperature', { config: { type: 'boolean', value: { kind: 'literal', type: 'boolean', value: true } } })
  graph = addNode(graph, 'if', 'branch')
  graph = connectNodes(graph, 'rooms', 'branch')
  const history = createGraphHistory(graph)
  dispose = render(() => <FlowAdapter graph={history.graph()} onSelect={() => {}} onChange={history.commit} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  vi.mocked(contract.connectData).mockClear()
  const source = el.querySelector<HTMLElement>('[data-id="temperature"] .source[data-handleid="data:value"]')!
  const target = el.querySelector<HTMLElement>('[data-id="branch"] .target[data-handleid="data:value"]')!
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(target)
  source.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 150, clientY: 150 }))
  // While dragging, compatible pins are marked and nothing ran the full validation.
  expect(source.classList).toContain('hs-pin--origin')
  expect(target.classList).toContain('hs-pin--compatible')
  expect(el.querySelector('[data-id="service-1"] .target[data-handleid="data:target"]')?.classList).toContain('hs-pin--incompatible')
  expect(contract.connectData).not.toHaveBeenCalled()
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 200, clientY: 200 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 200, clientY: 200 }))
  expect(history.graph().edges.at(-1)).toMatchObject({ kind: 'data', source: 'temperature', target: 'branch', targetPort: 'value' })
  expect(contract.connectData).toHaveBeenCalledTimes(1)
  expect(source.classList).not.toContain('hs-pin--origin')
})

it('keeps card rows in place when only a position changes', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(typed())
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const row = el.querySelector('[data-id="temperature"] .hs-card__row')!
  const summary = el.querySelector('[data-id="temperature"] .hs-card__summary')!
  setGraph(updateNode(graph(), 'temperature', { position: { x: 500, y: 400 } }))
  await vi.waitFor(() => expect(el.querySelector<HTMLElement>('[data-id="temperature"]')!.style.transform).toContain('500px, 400px'))
  expect(el.querySelector('[data-id="temperature"] .hs-card__row')).toBe(row)
  expect(el.querySelector('[data-id="temperature"] .hs-card__summary')).toBe(summary)
})

it('marks pin states by side: a twin pin with the same id on the other side is neither origin nor target', () => {
  const el = host()
  dispose = render(() => <FlowAdapter graph={typed()} onSelect={() => {}} onChange={() => {}} />, el)
  const pin = (id: string, side: 'source' | 'target') => el.querySelector<HTMLElement>(`[data-id="${id}"] .${side}[data-handleid="data:value"]`)!
  // Constant has data:value on both sides; pick the output of one from the keyboard.
  pin('temperature', 'source').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(pin('temperature', 'source').classList).toContain('hs-pin--origin')
  expect(pin('temperature', 'target').classList).not.toContain('hs-pin--origin')
  // The other Constant's input is a candidate target; its output, with the same handle id, is not marked at all.
  expect(pin('rooms', 'target').className).toMatch(/hs-pin--(compatible|incompatible)/)
  expect(pin('rooms', 'source').className).not.toMatch(/hs-pin--(origin|compatible|incompatible)/)
})

it('shows an unset optional input as its label only, a "Set" button that opens the editor until it is left unset', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  dispose = render(() => providers(() => <FlowAdapter graph={graph()} catalog={withServices()} onSelect={() => {}} onChange={setGraph} />), el)
  const cell = () => el.querySelector<HTMLElement>('[data-id="service-1"] [data-input-id="field:transition"]')!
  const set = () => cell().querySelector<HTMLButtonElement>('button.hs-pin-set')
  // Compact: the label, muted, and no editor or placeholder chip.
  expect(cell().classList).toContain('hs-pin-cell--muted')
  expect(cell().querySelector('.hs-card-editor')).toBeNull()
  expect(cell().querySelector('.hs-value-chip')).toBeNull()
  expect(set()?.getAttribute('aria-label')).toBe('Set Transition')
  expect(set()?.textContent).toBe('Transition')
  // A required input and an input with a value keep their editors.
  expect(el.querySelector('[data-id="service-1"] [data-input-id="field:brightness_pct"] .hs-card-editor')).not.toBeNull()
  expect(el.querySelector('[data-id="temperature"] [data-input-id="value"] .hs-card-editor')).not.toBeNull()
  // Click opens the editor and focuses it; leaving it unset folds the row back to the label.
  set()!.click()
  await Promise.resolve()
  expect(cell().querySelector('.hs-card-editor')).not.toBeNull()
  expect(cell().contains(document.activeElement)).toBe(true)
  const outside = el.appendChild(document.createElement('button'))
  outside.focus()
  await vi.waitFor(() => expect(cell().querySelector('.hs-card-editor')).toBeNull())
  expect(set()).not.toBeNull()
  // Enter (and Space) open it from the keyboard; a value set there keeps the editor after leaving.
  set()!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  await Promise.resolve()
  const field = cell().querySelector<HTMLInputElement>('.hs-card-editor input')!
  expect(document.activeElement).toBe(field)
  field.value = '3'
  field.dispatchEvent(new Event(field.type === 'range' ? 'change' : 'input', { bubbles: true }))
  if (field.type !== 'range') field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect((graph().nodes.find(n => n.id === 'service-1')!.config.fields as any).transition).toEqual(lit('number', 3))
  outside.focus()
  await Promise.resolve()
  await Promise.resolve()
  expect(cell().classList).toContain('hs-pin-cell--value')
  expect(cell().querySelector('.hs-card-editor')).not.toBeNull()
  // Space works too, on another unset row.
  setGraph(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  await vi.waitFor(() => expect(set()).not.toBeNull())
  set()!.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true, cancelable: true }))
  await Promise.resolve()
  expect(cell().querySelector('.hs-card-editor')).not.toBeNull()
})

it('labels service field rows by the service field label, with the key in words as the fallback', () => {
  const el = host()
  const catalog = withServices(section => {
    section.items['light.turn_on'].fields.brightness_pct.label = 'Brightness'
  })
  dispose = render(() => <FlowAdapter graph={lightOn({ brightness_pct: lit('number', 50), transition: lit('number', 2) })} catalog={catalog} onSelect={() => {}} onChange={() => {}} />, el)
  const row = (field: string) => el.querySelector(`[data-id="service-1"] [data-input-id="field:${field}"]`)!
  expect(row('brightness_pct').querySelector('.hs-pin-label')?.textContent).toBe('Brightness')
  expect(row('brightness_pct').querySelector('[data-handleid="data:field:brightness_pct"]')?.getAttribute('aria-label')).toMatch(/^Input Brightness, Number, /)
  // The fixture repeats the key as the label (Home Assistant without translations): the key in words.
  expect(row('transition').querySelector('.hs-pin-label')?.textContent).toBe('Transition')
})

it('rebuilds a card when what it shows changes elsewhere: a loop carry adds a loop result row, a renamed source updates the chip', async () => {
  const el = host()
  let start = addNode(addNode(createGraph('Loop'), 'repeat', 'loop'), 'loop_result', 'result')
  start = updateNode(start, 'loop', { config: { count: lit('number', 3), carry: [], exports: [] } })
  start = addNode(start, 'constant', 'source')
  start = updateNode(start, 'source', { config: { type: 'number', value: lit('number', 1) } })
  start = connectNodes(connectNodes(start, 'service-1', 'loop'), 'loop', 'result', 'body')
  const [graph, setGraph] = createSignal(start)
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const carryPin = () => el.querySelector('[data-id="result"] .target[data-handleid="data:carry:total"]')
  expect(carryPin()).toBeNull()
  // The loop result's inputs come from the loop's declarations, not from its own config.
  setGraph(updateNode(graph(), 'loop', { config: { ...graph().nodes.find(n => n.id === 'loop')!.config, carry: [{ name: 'total', type: 'number', initial: lit('number', 0) }] } }))
  await vi.waitFor(() => expect(carryPin()).not.toBeNull())
  expect(el.querySelector('[data-id="result"] [data-input-id="carry:total"] .hs-pin-label')?.textContent).toBe('total')
  // A wired input names its source; renaming the source updates the chip on the other card.
  setGraph(contract.connectData(graph(), 'source', 'value', 'result', 'carry:total'))
  const chip = () => el.querySelector('[data-id="result"] [data-input-id="carry:total"] .hs-source-chip')?.textContent
  await vi.waitFor(() => expect(chip()).toBe('Constant · Value'))
  setGraph(updateNode(graph(), 'source', { label: 'Running total' }))
  await vi.waitFor(() => expect(chip()).toBe('Running total · Value'))
})

const brokenGraph = () => {
  const graph = createGraph('Broken')
  const broken = {
    ...graph,
    nodes: [null, { id: 'no-config', kind: 'delay' }, ...graph.nodes, graph.nodes[0]],
    edges: [null, { id: 'dangling', kind: 'execution', source: 'trigger-1', target: 'missing' }, ...graph.edges],
  } as unknown as VisualGraph
  return { graph, broken }
}
/** The canvas edited `id` and nothing else: every malformed entry is still there, in place. */
const keptMalformed = (broken: VisualGraph, next: VisualGraph, id: string) => {
  expect(next.nodes).toHaveLength(broken.nodes.length)
  next.nodes.forEach((node, i) => (node?.id === id ? expect(node).not.toBe(broken.nodes[i]) : expect(node).toBe(broken.nodes[i])))
  expect(next.edges).toEqual(broken.edges)
}

it('applies a keyboard canvas edit of a graph with malformed entries to the original graph, keeping those entries', () => {
  const el = host()
  const { graph, broken } = brokenGraph()
  const changes: VisualGraph[] = []
  dispose = render(() => <FlowAdapter graph={broken} selectedId="service-1" onSelect={() => {}} onChange={next => changes.push(next)} />, el)
  // An edit (here a keyboard move) is made on what the canvas shows and applied to the graph as saved.
  el.querySelector('[data-id="service-1"]')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }))
  expect(changes).toHaveLength(1)
  keptMalformed(broken, changes[0], 'service-1')
  expect(changes[0].nodes.find(n => n?.id === 'service-1')!.position!.x).toBe(graph.nodes.find(n => n.id === 'service-1')!.position!.x + 10)
  // A wire made on the canvas keeps them too.
  const pin = (id: string, handle: string) => el.querySelector<HTMLElement>(`[data-id="${id}"] [data-handleid="${handle}"]`)!
  pin('service-1', 'error').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  pin('condition-1', 'in').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(changes).toHaveLength(1)
  pin('condition-1', 'false').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  pin('service-1', 'in').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(changes).toHaveLength(2)
  expect(changes[1].nodes).toEqual(broken.nodes)
  expect(changes[1].edges.slice(0, broken.edges.length)).toEqual(broken.edges)
  expect(changes[1].edges.at(-1)).toMatchObject({ source: 'condition-1', target: 'service-1', port: 'false' })
})

it('applies a mouse drag of a graph with malformed entries to the original graph, keeping those entries', async () => {
  const el = host()
  const { broken } = brokenGraph()
  const changes: VisualGraph[] = []
  dispose = render(() => <FlowAdapter graph={broken} onSelect={() => {}} onChange={next => changes.push(next)} />, el)
  const node = el.querySelector<HTMLElement>('[data-id="trigger-1"]')!
  node.dispatchEvent(new MouseEvent('mousedown', { clientX: 80, clientY: 110, button: 0, bubbles: true, view: window }))
  for (const [x, y] of [[83, 113], [143, 143], [203, 173]]) window.dispatchEvent(new MouseEvent('mousemove', { clientX: x, clientY: y, buttons: 1, bubbles: true, view: window }))
  window.dispatchEvent(new MouseEvent('mouseup', { clientX: 203, clientY: 173, button: 0, bubbles: true, view: window }))
  await vi.waitFor(() => expect(changes).toHaveLength(1))
  // The duplicate trigger entry is malformed (a repeated id) and is kept as it was.
  expect(changes[0].nodes).toHaveLength(broken.nodes.length)
  expect(changes[0].nodes.at(-1)).toBe(broken.nodes.at(-1))
  expect(changes[0].nodes.slice(0, 2)).toEqual(broken.nodes.slice(0, 2))
  expect(changes[0].nodes[2]!.position).toEqual({ x: 220, y: 180 })
  expect(changes[0].edges).toEqual(broken.edges)
})

it('shows trace value chips only where the run recorded a value', async () => {
  layout()
  const el = host()
  const graph = contract.connectData(typed(), 'temperature', 'value', 'rooms', 'value')
  const trace: FlowTrace = {
    nodes: { 'trigger-1': { status: 'ok', runs: 1 }, 'condition-1': { status: 'ok', runs: 1 }, 'service-1': { status: 'ok', runs: 1 }, temperature: { status: 'ok', runs: 1 }, rooms: { status: 'ok', runs: 1 } },
    edges: new Set(),
    value: (nodeId, port, side) => (nodeId === 'temperature' && port === 'value' && side === 'out' ? { type: 'number', v: 20 } : undefined),
  }
  dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  // Connected and not traced: the source chip only, no "No value" noise.
  const connected = el.querySelector('[data-id="rooms"] [data-input-id="value"]')!
  expect(connected.querySelector('.hs-source-chip')?.textContent).toBe('Constant · Value')
  expect(connected.querySelector('.hs-value-chip')).toBeNull()
  // Not connected and not traced: the configured value.
  expect(el.querySelector('[data-id="temperature"] [data-input-id="value"] .hs-value-chip')?.textContent).toBe('20')
  expect(el.querySelector('[data-id="temperature"] [data-output-id="value"] .hs-value-chip')?.textContent).toBe('20')
  expect(el.textContent).not.toContain('No value')
})

it('shows a run of value nodes: their cards with status and values, "Not used" for one no step read, lesser timeline rows before the step that used them, and their wires lit', async () => {
  layout()
  const el = host()
  let graph = addNode(addNode(createGraph('Values'), 'constant', 'level'), 'constant', 'spare')
  graph = updateNode(graph, 'level', { config: { type: 'number', value: lit('number', 20) } })
  graph = updateNode(graph, 'spare', { config: { type: 'number', value: lit('number', 3) } })
  graph = connectNodes(addNode(graph, 'log', 'say'), 'service-1', 'say')
  graph = contract.connectData(graph, 'level', 'value', 'say', 'value')
  const list: Omit<TraceEvent, 'seq'>[] = [
    { node: 'trigger-1', frame: '', t: 0, ms: 1, status: 'ok', port: 'next' },
    { node: 'condition-1', frame: '', t: 1, ms: 1, status: 'ok', port: 'true' },
    { node: 'service-1', frame: '', t: 2, ms: 3, status: 'ok', port: 'next' },
    { node: 'level', frame: '', t: 5, ms: 0, status: 'ok', outputs: { value: { type: 'number', v: 20 } } },
    { node: 'say', frame: '', t: 5, ms: 1, status: 'ok', port: 'next', inputs: { value: { type: 'number', v: 20, src: { node: 'level', port: 'value' } } } },
  ]
  const events = list.map((event, i) => ({ ...event, seq: i + 1 }) as TraceEvent)
  const summary: RunTrace['summary'] = Object.fromEntries(events.map(e => [e.node, { count: 1, failed: 0, last: 'ok' as const }]))
  const run: RunTrace = { format: 1, runId: 'run-1', rootNodeId: 'trigger-1', startedUtc: '2026-11-12T18:00:00Z', complete: true, truncated: false, dropped: 0, trigger: {}, events, summary }
  const model = traceModel(run, graph, contract.nodeCatalog)
  dispose = render(() => (
    <>
      <FlowAdapter graph={graph} readOnly trace={flowTrace(model, graph, '')} onSelect={() => {}} />
      <Timeline model={model} graph={graph} catalog={contract.nodeCatalog} live={false} onSelect={() => {}} />
    </>
  ), el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  const card = (id: string) => el.querySelector(`[data-id="${id}"] .hs-card`)!
  expect(card('level').querySelector('.hs-card__trace-word')!.textContent).toBe('Done')
  expect(card('level').querySelector('[data-output-id="value"] .hs-value-chip')!.textContent).toBe('20')
  // A value node no step read was not used, which is what the card says (not "Not reached").
  expect(card('spare').querySelector('.hs-card__trace-word')!.textContent).toBe('Not used')
  expect(card('spare').classList).toContain('hs-card--unreached')
  expect(card('say').querySelector('.hs-card__trace-word')!.textContent).toBe('Done')
  // The wire out of the value node is lit: the Log read it.
  const wire = graph.edges.find(e => e.kind === 'data')!
  expect([...el.querySelectorAll<SVGElement>('.solid-flow__edge')].find(edge => edge.dataset.id === wire.id)!.outerHTML).toContain('hs-wire--travelled')
  // The timeline: the value node is a lesser row labelled Value, directly before the step that used it.
  const rows = [...el.querySelectorAll<HTMLButtonElement>('.hs-trace-step')]
  expect(rows.map(row => row.classList.contains('hs-trace-step--value'))).toEqual([false, false, false, true, false])
  expect(rows[3].querySelector('.hs-trace-step__detail')!.textContent).toBe('Value')
  expect(rows[3].querySelector('.hs-trace-step__title')!.textContent).toBe('Constant')
  expect(rows[4].querySelector('.hs-trace-step__title')!.textContent).toBe('Log')
  expect(rows[4].querySelector('.hs-trace-step__detail')).toBeNull()
})

it('names registry items on read-only value chips', async () => {
  const el = host()
  const fixture = visualFixture()
  const callWS = fixture.callWS
  const named = {
    ...fixture,
    callWS: async (message: any) =>
      message.type === 'hass_sharp/get_entities'
        ? { entities: [], areas: [{ area_id: 'hall', name: 'Hall', floor_id: null }], devices: [], floors: [], labels: [] }
        : callWS(message),
  }
  const graph = updateNode(typed(), 'service-1', { config: { ...typed().nodes.find(n => n.id === 'service-1')!.config, target: lit('target', { area_id: 'hall' }) } })
  dispose = render(() => providers(() => <FlowAdapter graph={graph} readOnly onSelect={() => {}} />, named as any), el)
  await vi.waitFor(() => expect(el.querySelector('[data-id="service-1"] [data-input-id="target"] .hs-value-chip')?.textContent).toBe('Hall'))
})

it('names branch rows by the branch name, else by position; keeps the execution input of an unknown kind; tells warnings by icon', async () => {
  layout()
  const el = host()
  let graph = addNode(typed(), 'parallel', 'parallel')
  graph = updateNode(graph, 'parallel', { config: { branches: ['lights', '6f1c2b9e-0d4a-4b7e-9a51-2f8e3c4d5a6b'] } })
  graph = connectNodes(graph, 'service-1', 'parallel')
  graph = { ...graph, nodes: [...graph.nodes, { id: 'gone', kind: 'library:Gone.Node', position: { x: 900, y: 100 }, config: {} }] }
  graph = { ...graph, edges: [...graph.edges, { id: 'parallel->gone', kind: 'execution', source: 'parallel', target: 'gone' } as any] }
  dispose = render(
    () => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} diagnostics={[{ severity: 2, message: 'Slow', nodeId: 'rooms', startLine: null, startColumn: null }, { severity: 3, message: 'Broken', nodeId: 'temperature', startLine: null, startColumn: null }]} />,
    el,
  )
  const labels = [...el.querySelectorAll('[data-id="parallel"] .hs-pin-cell--exec .hs-pin-label')].map(l => l.textContent)
  expect(labels).toContain('Lights')
  expect(labels).toContain('Branch 2')
  expect(el.querySelector('[data-id="parallel"]')!.textContent).not.toContain('6f1c2b9e')
  expect(el.querySelector('[data-id="gone"] .target[data-handleid="in"]')).not.toBeNull()
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge[data-id="parallel->gone"]')).not.toBeNull())
  const icon = (id: string) => [...el.querySelectorAll(`[data-id="${id}"] .hs-card__badge path`)].map(p => p.getAttribute('d')).join(' ')
  expect(icon('rooms')).not.toBe(icon('temperature'))
  expect(el.querySelector('[data-id="rooms"] .hs-card__badge')?.classList).toContain('hs-card__badge--warning')
})

it('keeps an opened optional row open when focus moves to its own menu, even when the focus event comes late', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  dispose = render(() => providers(() => <FlowAdapter graph={graph()} catalog={withServices(typedTransition)} onSelect={() => {}} onChange={setGraph} />), el)
  const cell = () => el.querySelector<HTMLElement>('[data-id="service-1"] [data-input-id="field:transition"]')!
  cell().querySelector<HTMLButtonElement>('button.hs-pin-set')!.click()
  await Promise.resolve()
  const field = cell().querySelector<HTMLElement>('.hs-card-editor input')!
  const menuButton = cell().querySelector<HTMLElement>('.hs-card-editor button[aria-label="Transition mode"]')!
  expect(menuButton).not.toBeNull()
  // Focus first lands on the body (the field blurred), then the browser reports where it went: the row's menu.
  field.blur()
  expect(document.activeElement).toBe(document.body)
  field.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: menuButton }))
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(cell().querySelector('.hs-card-editor')).not.toBeNull()
  // A draft the editor refused ("abc" is not a number) keeps the row open even when focus leaves the card.
  const input = cell().querySelector<HTMLInputElement>('.hs-card-editor input')!
  input.value = 'abc'
  input.dispatchEvent(new InputEvent('input', { bubbles: true }))
  input.dispatchEvent(new FocusEvent('blur'))
  expect(input.getAttribute('aria-invalid')).toBe('true')
  expect(cell().textContent).toContain('Enter a number.')
  const outside = el.appendChild(document.createElement('button'))
  input.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: outside }))
  await new Promise(resolve => setTimeout(resolve, 0))
  expect(cell().querySelector('.hs-card-editor')).not.toBeNull()
  // Escape drops the draft; then leaving folds the unset row.
  input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
  expect(input.getAttribute('aria-invalid')).toBeNull()
  input.dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: outside }))
  await vi.waitFor(() => expect(cell().querySelector('.hs-card-editor')).toBeNull())
})

it('keeps an opened optional row open while a click inside it lands, where a click does not focus the button (Safari, Firefox on macOS)', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  dispose = render(() => providers(() => <FlowAdapter graph={graph()} catalog={withServices()} onSelect={() => {}} onChange={setGraph} />), el)
  const cell = () => el.querySelector<HTMLElement>('[data-id="service-1"] [data-input-id="field:transition"]')
  cell()!.querySelector<HTMLButtonElement>('button.hs-pin-set')!.click()
  await Promise.resolve()
  const wrapper = el.querySelector<HTMLElement>('.solid-flow__node[data-id="service-1"]')!
  const field = () => cell()!.querySelector<HTMLElement>('.hs-card-editor input')!
  /** The pointer goes down on `target`, focus leaves the field for the card (not the button), then the click lands. */
  const press = async (target: HTMLElement) => {
    target.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, pointerId: 1 }))
    field().dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: wrapper }))
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
    target.dispatchEvent(new PointerEvent('pointerup', { bubbles: true, button: 0, pointerId: 1 }))
    target.click()
    await new Promise(resolve => setTimeout(resolve, 0))
  }
  await press(cell()!.querySelector<HTMLElement>('button[aria-label="Transition mode"]')!)
  expect(cell()!.querySelector('[role="menu"]')).not.toBeNull()
  expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
  // "Clear" from that menu leaves the input unset, and the row stays open under the pointer.
  setGraph(lightOn({ brightness_pct: lit('number', 50), transition: lit('number', 2) }))
  await Promise.resolve()
  await press([...cell()!.querySelectorAll<HTMLElement>('[role="menuitem"]')].find(item => item.textContent === 'Clear')!)
  expect((graph().nodes.find(n => n.id === 'service-1')!.config.fields as any).transition).toEqual({ kind: 'unset' })
  expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
})

it('keeps an opened optional row open for a tap on iOS, where the click comes in a later task, and never leaves it stuck open', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  dispose = render(() => providers(() => <FlowAdapter graph={graph()} catalog={withServices()} onSelect={() => {}} onChange={setGraph} />), el)
  const cell = () => el.querySelector<HTMLElement>('[data-id="service-1"] [data-input-id="field:transition"]')
  const open = async () => {
    cell()!.querySelector<HTMLButtonElement>('button.hs-pin-set')!.click()
    await Promise.resolve()
    expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
  }
  await open()
  const wrapper = el.querySelector<HTMLElement>('.solid-flow__node[data-id="service-1"]')!
  const field = () => cell()!.querySelector<HTMLElement>('.hs-card-editor input')!
  const menu = () => cell()!.querySelector<HTMLElement>('button[aria-label="Transition mode"]')!
  const pointer = (target: Element, type: string, pointerId = 1) => target.dispatchEvent(new PointerEvent(type, { bubbles: true, button: 0, pointerId, pointerType: 'touch' }))
  const task = (ms = 0) => new Promise(resolve => setTimeout(resolve, ms))
  // pointerdown and pointerup with touchend; a task later focus moves to the card, then the click arrives.
  pointer(menu(), 'pointerdown')
  pointer(menu(), 'pointerup')
  await task()
  field().dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: wrapper }))
  await task()
  expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
  menu().click()
  await task()
  expect(cell()!.querySelector('[role="menu"]')).not.toBeNull()
  // A press released outside the row makes no click: once the wait is over, the row folds (focus is elsewhere).
  menu().click()
  await task()
  const outside = el.appendChild(document.createElement('button'))
  pointer(menu(), 'pointerdown')
  outside.focus()
  pointer(outside, 'pointerup')
  await task(100)
  expect(cell()!.querySelector('.hs-card-editor')).not.toBeNull()
  await vi.waitFor(() => expect(cell()!.querySelector('.hs-card-editor')).toBeNull(), { timeout: 1500 })
  // Two quick presses (the second before the first one's wait is over) do not leave the row open for good.
  await open()
  pointer(menu(), 'pointerdown')
  pointer(menu(), 'pointerup')
  pointer(menu(), 'pointerdown', 2)
  pointer(menu(), 'pointerup', 2)
  outside.focus()
  await vi.waitFor(() => expect(cell()!.querySelector('.hs-card-editor')).toBeNull(), { timeout: 1500 })
  // A cancelled press (the page scrolled instead) ends at once.
  await open()
  pointer(menu(), 'pointerdown')
  pointer(menu(), 'pointercancel')
  outside.focus()
  await vi.waitFor(() => expect(cell()!.querySelector('.hs-card-editor')).toBeNull())
})

it('ends each press by its own click on iOS, and folds the row when a press ends with focus outside it', async () => {
  const el = host()
  const [graph, setGraph] = createSignal(lightOn({ brightness_pct: lit('number', 50), transition: { kind: 'unset' } }))
  dispose = render(() => providers(() => <FlowAdapter graph={graph()} catalog={withServices()} onSelect={() => {}} onChange={setGraph} />), el)
  const cell = () => el.querySelector<HTMLElement>('[data-id="service-1"] [data-input-id="field:transition"]')
  const editor = () => cell()?.querySelector('.hs-card-editor') ?? null
  const open = async () => {
    cell()!.querySelector<HTMLButtonElement>('button.hs-pin-set')!.click()
    await Promise.resolve()
    expect(editor()).not.toBeNull()
  }
  const wrapper = el.querySelector<HTMLElement>('.solid-flow__node[data-id="service-1"]')!
  const menu = () => cell()!.querySelector<HTMLElement>('button[aria-label="Transition mode"]')!
  const pointer = (target: Element, type: string, pointerId: number) => target.dispatchEvent(new PointerEvent(type, { bubbles: true, button: 0, pointerId, pointerType: 'touch' }))
  const task = () => new Promise(resolve => setTimeout(resolve, 0))
  await open()
  // Two taps: the second starts before the first one's click arrives. That click ends only the first tap.
  pointer(menu(), 'pointerdown', 1)
  pointer(menu(), 'pointerup', 1)
  pointer(menu(), 'pointerdown', 2)
  menu().click()
  await task()
  wrapper.focus()
  await task()
  expect(editor()).not.toBeNull()
  pointer(menu(), 'pointerup', 2)
  menu().click()
  await task()
  expect(editor()).not.toBeNull()
  // A tap on the row's padding: focus goes to the card, the click opens nothing, and the row folds once it lands.
  pointer(cell()!, 'pointerdown', 3)
  wrapper.focus()
  pointer(cell()!, 'pointerup', 3)
  expect(editor()).not.toBeNull()
  cell()!.click()
  await task()
  expect(editor()).toBeNull()
  // A cancelled press with focus gone folds it at once.
  await open()
  pointer(menu(), 'pointerdown', 4)
  wrapper.focus()
  expect(editor()).not.toBeNull()
  pointer(menu(), 'pointercancel', 4)
  expect(editor()).toBeNull()
})

it.each([
  ['accepts', 'Added State condition and connected Entity. Connect State condition into the flow to run it.'],
  ['refuses', 'Added State condition. Connect it into the flow, then connect Entity.'],
] as const)('adds a step from a data pin outside the flow and says what happened when the editor %s its value wire', async (verdict, message) => {
  layout()
  const el = host()
  const [graph, setGraph] = createSignal(createGraph('Pins'))
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
  const before = graph()
  // Only the drop's own value wire is decided here; every other check is the real one.
  const real = vi.mocked(contract.connectData).getMockImplementation()!
  vi.mocked(contract.connectData).mockImplementation((g, source, output, target, input, catalog) => {
    if (source !== 'trigger-1' || target === 'condition-1') return real(g, source, output, target, input, catalog)
    if (verdict === 'refuses') throw new Error('Left cannot use this value here: it is across a loop or parallel branch boundary')
    return { ...g, edges: [...g.edges, { id: 'value', kind: 'data', source, sourcePort: output, target, targetPort: input }] }
  })
  vi.spyOn(document, 'elementFromPoint').mockReturnValue(el.querySelector('.solid-flow__pane'))
  el.querySelector('[data-id="trigger-1"] .source[data-handleid="data:entity_id"]')!.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
  document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 160, clientY: 470 }))
  document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 160, clientY: 470 }))
  button(el, 'Create State condition').click()
  const created = graph().nodes.at(-1)!.id
  // State condition has no plain next: it is not put into the flow, and no wire there changes.
  expect(graph().edges.slice(0, before.edges.length)).toEqual(before.edges)
  expect(graph().edges.slice(before.edges.length)).toEqual(verdict === 'accepts' ? [expect.objectContaining({ kind: 'data', source: 'trigger-1', target: created })] : [])
  await vi.waitFor(() => expect(el.textContent).toContain(message))
  expect(el.textContent).not.toContain('boundary')
})

it('centres a run on its focus node once: a new trace object for the same node leaves the view alone', async () => {
  layout()
  const el = host()
  const trace = (focusNodeId: string): FlowTrace => ({ nodes: { [focusNodeId]: { status: 'ok', runs: 1 } }, edges: new Set(), focusNodeId })
  const [current, setCurrent] = createSignal(trace('service-1'))
  centred.calls = []
  dispose = render(() => <FlowAdapter graph={createGraph('Run')} readOnly trace={current()} onSelect={() => {}} />, el)
  await vi.waitFor(() => expect(centred.calls).toHaveLength(1))
  setCurrent(trace('service-1'))
  await new Promise(resolve => setTimeout(resolve, 100))
  expect(centred.calls).toHaveLength(1)
  setCurrent(trace('trigger-1'))
  await vi.waitFor(() => expect(centred.calls).toHaveLength(2))
})

it('rebuilds a card whose wired input shows its source when only the source output changes: its type or its label', async () => {
  const el = host()
  let start = addNode(addNode(createGraph('Outputs'), 'constant', 'source'), 'get_field', 'pick')
  start = updateNode(start, 'source', { config: { type: 'object', value: lit('object', {}) } })
  start = contract.connectData(connectNodes(connectNodes(start, 'service-1', 'source'), 'source', 'pick'), 'source', 'value', 'pick', 'value')
  const [graph, setGraph] = createSignal(start)
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const pin = () => el.querySelector<HTMLElement>('[data-id="pick"] .target[data-handleid="data:value"]')!
  expect(pin().getAttribute('data-type')).toBe('object')
  // An object or a list both fit: the wire stays, and the input now shows a list arriving.
  setGraph(updateNode(graph(), 'source', { config: { type: 'array', value: lit('array', []) } }))
  await vi.waitFor(() => expect(pin().getAttribute('data-type')).toBe('array'))
  // A loop carry renamed (with its wire): the consumer's chip names the new output.
  let loop = addNode(addNode(createGraph('Loop'), 'repeat', 'loop'), 'constant', 'shown')
  loop = updateNode(loop, 'loop', { config: { count: lit('number', 3), carry: [{ name: 'total', type: 'number', initial: lit('number', 0) }], exports: [] } })
  loop = updateNode(loop, 'shown', { config: { type: 'number', value: lit('number', 0) } })
  loop = contract.connectData(connectNodes(connectNodes(loop, 'service-1', 'loop'), 'loop', 'shown', 'body'), 'loop', 'carry:total', 'shown', 'value')
  setGraph(loop)
  const chip = () => el.querySelector('[data-id="shown"] [data-input-id="value"] .hs-source-chip')?.textContent
  await vi.waitFor(() => expect(chip()).toMatch(/· total$/))
  const renamed = updateNode(loop, 'loop', { config: { ...loop.nodes.find(n => n.id === 'loop')!.config, carry: [{ name: 'sum', type: 'number', initial: lit('number', 0) }] } })
  setGraph({ ...renamed, edges: renamed.edges.map(e => (e.kind === 'data' ? { ...e, sourcePort: 'carry:sum' } : e)) })
  await vi.waitFor(() => expect(chip()).toMatch(/· sum$/))
})

it('names a data wire into a service field by the field label, as the card row does', async () => {
  layout()
  const el = host()
  const catalog = withServices(section => {
    section.items['light.turn_on'].fields.brightness_pct.label = 'Brightness'
  })
  let graph = addNode(lightOn({ brightness_pct: lit('number', 50) }), 'constant', 'level')
  graph = updateNode(graph, 'level', { config: { type: 'number', value: lit('number', 80) } })
  graph = contract.connectData(graph, 'level', 'value', 'service-1', 'field:brightness_pct', catalog)
  dispose = render(() => <FlowAdapter graph={graph} catalog={catalog} onSelect={() => {}} onChange={() => {}} />, el)
  await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge[aria-label^="Data wire"]')).not.toBeNull())
  expect(el.querySelector('.solid-flow__edge[aria-label^="Data wire"]')!.getAttribute('aria-label')).toBe('Data wire from Constant · Value to Call service · Brightness')
})

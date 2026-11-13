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

// Counts full validations of a dropped wire (D12: hover is quick, a drop is validated once).
vi.mock('../src/visual/contract', async original => {
  const actual = await original<typeof import('../src/visual/contract')>()
  return { ...actual, connectData: vi.fn(actual.connectData) }
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
const typed = () => {
  let graph = addNode(createGraph('Cards'), 'set_value', 'temperature')
  graph = updateNode(graph, 'temperature', { config: { type: 'number', value: { kind: 'literal', type: 'number', value: 20 } } })
  graph = addNode(graph, 'set_value', 'rooms')
  graph = updateNode(graph, 'rooms', { config: { type: 'array', value: { kind: 'literal', type: 'array', value: [] } } })
  graph = connectNodes(graph, 'service-1', 'temperature')
  graph = connectNodes(graph, 'temperature', 'rooms')
  return graph
}

it('draws execution and typed data pins, with inputs on the left and outputs on the right of shared rows', () => {
  const el = host()
  dispose = render(() => <FlowAdapter graph={typed()} onSelect={() => {}} onChange={() => {}} />, el)
  const card = el.querySelector('[data-id="temperature"] .hs-card')!
  expect(card.querySelector('.hs-card__title')?.textContent).toBe('Set value')
  expect(card.getAttribute('data-category')).toBe('data')
  // Execution pins sit in the header; the default output is the header's right pin.
  const execIn = card.querySelector('.hs-card__head [data-handleid="in"]')!
  expect(execIn.classList).toContain('hs-pin--exec')
  expect(execIn.classList).toContain('hs-pin--connected')
  expect(card.querySelector('.hs-card__head [data-handleid="next"]')?.classList).toContain('hs-pin--exec')
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
  expect(input.getAttribute('aria-label')).toBe('Input Value, Number, not connected, has a value')
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
  expect(el.querySelector('[data-id="temperature"] .hs-card__row--footer [data-handleid="error"]')?.classList).toContain('hs-pin--error')
})

it('shows the wired source on a connected input instead of an editor', () => {
  const el = host()
  const graph = contract.connectData(typed(), 'temperature', 'value', 'rooms', 'value')
  dispose = render(() => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />, el)
  const cell = el.querySelector('[data-id="rooms"] [data-input-id="value"]')!
  expect(cell.querySelector('.hs-source-chip')?.textContent).toBe('Set value · Value')
  expect(cell.querySelector('.hs-card-editor')).toBeNull()
  expect(cell.querySelector('[data-handleid="data:value"]')?.getAttribute('aria-label')).toBe('Input Value, Number, connected to Set value · Value')
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

it('passes the coalesce key of an inline edit to onChange', () => {
  const el = host()
  const fixture = visualFixture()
  const changes: [VisualGraph, { coalesce?: string } | undefined][] = []
  dispose = render(
    () => (
      <QueryClientProvider client={new QueryClient()}>
        <HassCtx.Provider value={fixture as any}>
          <FlowAdapter graph={typed()} onSelect={() => {}} onChange={(graph, options) => changes.push([graph, options])} />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    el,
  )
  const field = el.querySelector<HTMLInputElement>('[data-id="temperature"] .hs-card-editor input')!
  expect(field).not.toBeNull()
  field.value = '21'
  field.dispatchEvent(new Event('input', { bubbles: true }))
  field.dispatchEvent(new FocusEvent('blur'))
  field.dispatchEvent(new FocusEvent('focusout', { bubbles: true }))
  expect(changes).toHaveLength(1)
  expect(changes[0][0].nodes.find(n => n.id === 'temperature')!.config.value).toEqual({ kind: 'literal', type: 'number', value: 21 })
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
  dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} onChange={onChange} />, el)
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
  expect(source.tabIndex).toBe(-1)
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
  const trace: FlowTrace = {
    nodes: {
      'trigger-1': { status: 'ok', runs: 1 },
      'condition-1': { status: 'error', runs: 1 },
      'service-1': { status: 'failed', runs: 2, ms: 1500 },
      temperature: { status: 'running', runs: 1 },
      rooms: { status: 'cancelled', runs: 1 },
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
  expect(el.querySelector('[data-id="service-1"] .hs-card__trace')?.textContent).toBe('×21.5 s')
  expect(el.querySelector('[data-id="never"] .hs-node-errors')?.textContent).toBe('Not reached')
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
  expect(el.textContent).toContain('Select a destination port')
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

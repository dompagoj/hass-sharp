import { afterEach, describe, expect, it, onTestFinished, vi } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
import { FlowAdapter, type FlowTrace } from '../src/visual/FlowAdapter'
import { tipBounds, tipPlace } from '../src/visual/flow/NodeCard'
import { VisualEditor } from '../src/visual/VisualEditor'
import { HassCtx } from '../src/context'
import * as contract from '../src/visual/contract'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import { add, empty, visualFixture } from './visual-fixture'
import { Timeline } from '../src/visual/trace/Timeline'
import { nodeSummary } from '../src/visual/nodeSummary'
import { chipValue, flowTrace, traceModel } from '../src/visual/trace/traceModel'
import type { RunTrace, TraceEvent } from '../src/types'
import services from '../../../../tests/fixtures/visual-services.json'

// Counts full validations of a dropped wire (D12: hover is quick, a drop is validated once).
vi.mock('../src/visual/contract', async original => {
  const actual = await original<typeof import('../src/visual/contract')>()
  return { ...actual, connectData: vi.fn(actual.connectData) }
})

// Records where the canvas centres the view (a run's focus node), and what it fits (a run's path).
const centred = vi.hoisted(() => ({ calls: [] as unknown[][], fits: [] as unknown[][] }))
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
            : key === 'fitView'
              ? (...args: Parameters<typeof api.fitView>) => {
                  centred.fits.push(args)
                  return target.fitView(...args)
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
/** What a step's card says about it. */
const nodeSummaryOf = (graph: VisualGraph, id: string, catalog: contract.VisualCatalogSnapshot = contract.nodeCatalog) =>
  nodeSummary(graph.nodes.find(n => n.id === id)! as contract.GraphNode, catalog, undefined, graph as contract.Graph)
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
  // A Convert, so a wire can go into its value (nothing can be wired into a Constant).
  graph = addNode(graph, 'convert', 'rooms')
  graph = updateNode(graph, 'rooms', { config: { type: 'array', value: { kind: 'literal', type: 'array', value: [] } } })
  // Constants and Converts are values: they take no place in the run order, so there is nothing to connect by execution.
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
  // Its accessible name says it is a value; a step's does not.
  const name = (id: string) => el.querySelector(`.solid-flow__node[data-id="${id}"]`)!.getAttribute('aria-label')!
  expect(name('temperature')).toMatch(/^Constant, [^.]+, value\b/)
  expect(name('service-1')).not.toContain(', value')
  // A step's execution pins sit in the header; the default output is the header's right pin.
  const step = el.querySelector('[data-id="service-1"] .hs-card')!
  const execIn = step.querySelector('.hs-card__head [data-handleid="in"]')!
  expect(execIn.classList).toContain('hs-pin--exec')
  expect(execIn.classList).toContain('hs-pin--connected')
  expect(step.querySelector('.hs-card__head [data-handleid="next"]')?.classList).toContain('hs-pin--exec')
  // One row carries the Constant's value (left: its Type and the value, nothing to wire into) and its one output (right).
  const row = card.querySelector('[data-input-id="value"]')!.closest('.hs-card__row')!
  expect(row.querySelector('[data-output-id="value"]')).not.toBeNull()
  expect(card.querySelectorAll('.target')).toHaveLength(0)
  expect(card.querySelectorAll('.source')).toHaveLength(1)
  expect(row.querySelector('[data-input-id="value"] .hs-pin-value')?.textContent).toBe('20')
  const output = row.querySelector<HTMLElement>('[data-handleid="data:value"].source')!
  expect(output.classList).toContain('hs-pin--data')
  expect(output.style.getPropertyValue('--hs-pin-color')).toBe('var(--hs-port-number)')
  expect(output.getAttribute('data-type')).toBe('number')
  expect(output.classList).toContain('hs-pin--out')
  // A step's value input has its pin on the left.
  const input = el.querySelector<HTMLElement>('[data-id="rooms"] [data-handleid="data:value"].target')!
  expect(input.classList).toContain('hs-pin--data')
  expect(input.classList).toContain('hs-pin--in')
  expect(input.getAttribute('aria-label')).toBe('Input Value, List, required, not connected, has a value')
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

it("shows a Constant's Type and value on its card, with nothing to wire into: a new Type keeps a value that fits and clears one that does not, one undo step each", async () => {
  const el = host()
  const fixture = visualFixture()
  dispose = render(() => providers(() => <VisualEditor name="Constant" />, fixture), el)
  button(el, 'Add step').click()
  button(el, 'Create Constant').click()
  button(el, 'Graph JSON').click()
  const graphJson = () => JSON.parse(el.querySelector('pre[aria-label="Graph JSON"]')!.textContent!)
  const id = graphJson().nodes.at(-1).id
  const card = () => el.querySelector<HTMLElement>(`[data-id="${id}"] .hs-card`)!
  const type = () => card().querySelector<HTMLSelectElement>('select[aria-label="Type"]')!
  const value = () => card().querySelector<HTMLInputElement>('.hs-card-editor input[aria-label="Value"]')!
  const config = () => graphJson().nodes.at(-1).config
  // The Type select and one editor for that type; no input pin and no second type select; one output pin.
  expect(type().value).toBe('string')
  expect(card().querySelectorAll('[data-input-id="value"] select')).toHaveLength(1)
  expect(card().querySelectorAll('.target')).toHaveLength(0)
  expect(card().querySelectorAll('.source')).toHaveLength(1)
  value().value = 'light.hall'
  value().dispatchEvent(new Event('input', { bubbles: true }))
  value().dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true, cancelable: true }))
  expect(config().value).toEqual(lit('string', 'light.hall'))
  // Text that is an entity id is still a value of Entity: kept, as an entity id.
  type().value = 'entity_id'
  type().dispatchEvent(new Event('change', { bubbles: true }))
  expect(config()).toMatchObject({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  // An entity id is not a number: cleared.
  type().value = 'number'
  type().dispatchEvent(new Event('change', { bubbles: true }))
  expect(config()).toMatchObject({ type: 'number', value: { kind: 'unset' } })
  await vi.waitFor(() => expect(type().value).toBe('number'))
  // The panel shows the same two controls and no type select of the value's own.
  const panel = el.querySelector<HTMLElement>('.hs-inspector')!
  expect(panel.querySelector<HTMLSelectElement>('[data-input-id="type"] select')!.value).toBe('number')
  expect(panel.querySelectorAll('[data-input-id="value"] select')).toHaveLength(0)
  // Each Type change is one undo step: Type and value come back together.
  button(el, 'Undo').click()
  expect(config()).toMatchObject({ type: 'entity_id', value: lit('entity_id', 'light.hall') })
  button(el, 'Undo').click()
  expect(config()).toMatchObject({ type: 'string', value: lit('string', 'light.hall') })
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
  // The Convert's input is a candidate target; its output, with the same handle id, is not marked at all.
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
  // A row says what the step's card says about it: the Constant's value, what the Log writes.
  expect(rows[3].querySelector('.hs-trace-step__detail')!.textContent).toBe('Value · 20')
  expect(rows[3].querySelector('.hs-trace-step__title')!.textContent).toBe('Constant')
  expect(rows[4].querySelector('.hs-trace-step__title')!.textContent).toBe('Log')
  expect(rows[4].querySelector('.hs-trace-step__detail')!.textContent).toBe('Log value')
  expect(nodeSummaryOf(graph, 'say')).toBe('Log value')
})

it('words a True/False value on the card and the peek bar as the panel and the trace do (live defect D3)', () => {
  const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
  const compare = (right: boolean) => ({ id: 'cmp', kind: 'compare', config: { operator: 'gt', left: lit('number', 3), right: lit('boolean', right) } })
  for (const [right, shown] of [[true, 'True'], [false, 'False']] as const) {
    const graph = { version: 5, name: 'D3', nodes: [compare(right)], edges: [] } as unknown as VisualGraph
    expect(nodeSummaryOf(graph, 'cmp')).toBe(`If 3 > ${shown}`)
    expect(contract.words.show(right)).toBe(shown)
  }
})

it('tells runs of steps of one kind apart in the timeline: each row says what its card says (the service it calls)', () => {
  const el = host()
  const catalog = withServices()
  let graph = typed()
  const base = graph.nodes.find(n => n.id === 'service-1')!.config
  graph = updateNode(graph, 'service-1', { config: { ...base, service: 'light.turn_on' } })
  graph = connectNodes(addNode(graph, 'service', 'service-2', catalog), 'service-1', 'service-2', undefined, catalog)
  graph = updateNode(graph, 'service-2', { config: { ...base, service: 'light.turn_off' } })
  graph = connectNodes(addNode(graph, 'service', 'service-3', catalog), 'service-2', 'service-3', undefined, catalog)
  graph = updateNode(graph, 'service-3', { config: { ...base, service: 'climate.set_hvac_mode' } })
  const events = (['trigger-1', 'condition-1', 'service-1', 'service-2', 'service-3'] as const).map((node, i) => ({ node, frame: '', t: i, ms: 1, status: 'ok', port: node === 'condition-1' ? 'true' : 'next', seq: i + 1 }) as TraceEvent)
  const summary: RunTrace['summary'] = Object.fromEntries(events.map(e => [e.node, { count: 1, failed: 0, last: 'ok' as const }]))
  const run: RunTrace = { format: 1, runId: 'run-1', rootNodeId: 'trigger-1', startedUtc: '2026-11-12T18:00:00Z', complete: true, truncated: false, dropped: 0, trigger: {}, events, summary }
  dispose = render(() => <Timeline model={traceModel(run, graph, catalog)} graph={graph} catalog={catalog} live={false} onSelect={() => {}} />, el)
  const rows = [...el.querySelectorAll<HTMLButtonElement>('.hs-trace-step')].slice(2)
  expect(rows.map(row => row.querySelector('.hs-trace-step__title')!.textContent)).toEqual(['Call service', 'Call service', 'Call service'])
  const details = rows.map(row => row.querySelector('.hs-trace-step__detail')?.textContent)
  expect(details).toEqual(['light.turn_on · Hall', 'light.turn_off · Hall', 'climate.set_hvac_mode · Hall'])
  expect(details).toEqual(['service-1', 'service-2', 'service-3'].map(id => nodeSummaryOf(graph, id, catalog)))
  expect(new Set(details).size).toBe(3)
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

it('reads state values on cards as the state picker labels them: from the entity\'s state options when known, else in words', async () => {
  const el = host()
  const fixture = visualFixture()
  const callWS = fixture.callWS
  const states = { entity_id: 'climate.hall', name: 'Hall', domain: 'climate', area_id: null, device_id: null, device_class: null,
    supported_features: null, unit: null, icon: null, disabled: false, state_options: ['off', 'heat_cool'] }
  const named = {
    ...fixture,
    callWS: async (message: any) =>
      message.type === 'hass_sharp/get_entities' ? { entities: [states], areas: [], devices: [], floors: [], labels: [] } : callWS(message),
  }
  const base = typed()
  let graph = updateNode(base, 'service-1', { config: { ...base.nodes.find(n => n.id === 'service-1')!.config, service: 'climate.set_hvac_mode',
    target: lit('target', { entity_id: ['climate.hall'] }), fields: { hvac_mode: lit('string', 'heat_cool') } } })
  graph = updateNode(graph, 'condition-1', { config: { ...graph.nodes.find(n => n.id === 'condition-1')!.config, entity: lit('entity_id', 'climate.hall'), state: 'dry_mode' } })
  graph = updateNode(graph, 'trigger-1', { config: { type: 'ha', trigger: { trigger: 'state', entity_id: 'climate.hall', to: ['heat_cool', 'off'] } } })
  dispose = render(() => providers(() => <FlowAdapter graph={graph} catalog={withServices()} readOnly onSelect={() => {}} />, named as any), el)
  const chip = () => el.querySelector('[data-id="service-1"] [data-input-id="field:hvac_mode"] .hs-value-chip')?.textContent
  const summary = (id: string) => el.querySelector(`[data-id="${id}"] .hs-card__summary`)?.textContent
  // Once the registry has loaded, a state the entity does not have reads as stored (the picker would not offer it)...
  await vi.waitFor(() => expect(summary('condition-1')).toBe('If Hall is dry_mode'))
  // ...and one it has reads as the picker labels it, whether the entity is the step's literal target or its own.
  expect(summary('trigger-1')).toBe('When Hall becomes Heat cool or Off')
  expect(chip()).toBe('Heat cool')
  // The stored values are unchanged.
  expect((graph.nodes.find(n => n.id === 'service-1')!.config.fields as any).hvac_mode.value).toBe('heat_cool')
  dispose()
  // Without the registry the states are not known: the value in words when it looks like an identifier.
  dispose = render(() => <FlowAdapter graph={graph} catalog={withServices()} readOnly onSelect={() => {}} />, el)
  expect(chip()).toBe('Heat cool')
  expect(summary('condition-1')).toBe('If Hall is Dry mode')
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
  // The 800 × 570 cards overlap: the view opens on the node, and once more on it after the cards are moved apart.
  await vi.waitFor(() => expect(centred.calls).toHaveLength(2))
  expect(centred.calls[1][0]).toBe(centred.calls[0][0])
  setCurrent(trace('service-1'))
  await new Promise(resolve => setTimeout(resolve, 100))
  expect(centred.calls).toHaveLength(2)
  setCurrent(trace('trigger-1'))
  await vi.waitFor(() => expect(centred.calls).toHaveLength(3))
})

// A window below 900 px (happy-dom's own is 1024 px wide).
const narrowScreen = () => vi.stubGlobal('matchMedia', (query: string) => ({ matches: query.includes('max-width: 899px'), media: query, addEventListener() {}, removeEventListener() {} }))
const wideScreen = () => vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(min-width: 900px)', media: query, addEventListener() {}, removeEventListener() {} }))

it('opens a run fitted to its path, at whatever zoom a narrow screen needs, once; a run with a focus step opens on it', async () => {
  layout()
  narrowScreen()
  onTestFinished(() => vi.unstubAllGlobals())
  const el = host()
  const trace = (ids: string[], focusNodeId?: string): FlowTrace =>
    ({ nodes: Object.fromEntries(ids.map(id => [id, { status: 'ok' as const, runs: 1 }])), edges: new Set(), focusNodeId })
  const [current, setCurrent] = createSignal(trace(['trigger-1', 'condition-1']))
  centred.calls = []
  centred.fits = []
  const paths = () => centred.fits.filter(([options]) => (options as { nodes?: unknown })?.nodes)
  dispose = render(() => <FlowAdapter graph={createGraph('Run')} readOnly trace={current()} onSelect={() => {}} />, el)
  // The 800 × 570 cards overlap: the path is fitted, and fitted again (the same path) once they are moved apart.
  await vi.waitFor(() => expect(paths()).toHaveLength(2))
  expect(paths()[1]).toEqual(paths()[0])
  const [options] = paths()[0] as [{ nodes: { id: string }[]; minZoom: number }]
  expect(options.nodes).toEqual([{ id: 'trigger-1' }, { id: 'condition-1' }])
  // A trace zooms out further than the editor (0.5 to fit, 0.25 by hand): a 360 px screen fits a long path.
  expect(options.minZoom).toBe(0.1)
  // A live run reaching more steps leaves the view where the person put it.
  setCurrent(trace(['trigger-1', 'condition-1', 'service-1']))
  await new Promise(resolve => setTimeout(resolve, 100))
  expect(paths()).toHaveLength(2)
  expect(centred.calls).toHaveLength(0)
  dispose()

  centred.fits = []
  centred.calls = []
  dispose = render(() => <FlowAdapter graph={createGraph('Run')} readOnly trace={trace(['trigger-1', 'service-1'], 'service-1')} onSelect={() => {}} />, host())
  // Centred on the focus step, and again on it once the cards are moved apart.
  await vi.waitFor(() => expect(centred.calls).toHaveLength(2))
  expect(centred.calls[1][0]).toBe(centred.calls[0][0])
  await new Promise(resolve => setTimeout(resolve, 100))
  expect(paths()).toHaveLength(0)
})

it('on a screen 900 px or wider, a run whose path needs more than halfway out opens at 0.5 on its failed or first step', async () => {
  // A 1200 x 800 canvas with 200 x 100 cards.
  vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('solid-flow__node') ? 200 : 1200 })
  vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockImplementation(function (this: HTMLElement) { return this.classList.contains('solid-flow__node') ? 100 : 800 })
  vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1200)
  vi.spyOn(HTMLElement.prototype, 'clientHeight', 'get').mockReturnValue(800)
  wideScreen()
  try {
    const trace = (ids: string[], focusNodeId?: string): FlowTrace =>
      ({ nodes: Object.fromEntries(ids.map(id => [id, { status: 'ok' as const, runs: 1 }])), edges: new Set(), focusNodeId })
    const paths = () => centred.fits.filter(([options]) => (options as { nodes?: unknown })?.nodes)
    // The service far to the right: the whole path fits only well below zoom 0.5.
    const far = updateNode(createGraph('Run'), 'service-1', { position: { x: 20000, y: 100 } })
    const open = async (graph: VisualGraph, value: FlowTrace) => {
      centred.calls = []
      centred.fits = []
      dispose = render(() => <FlowAdapter graph={graph} readOnly trace={value} onSelect={() => {}} />, host())
      // It either fits the path or centres on one step. The first render of the flow can take over a second on a
      // loaded CI runner.
      await vi.waitFor(() => expect(paths().length + centred.calls.length).toBeGreaterThan(0), { timeout: 5000 })
      await new Promise(resolve => setTimeout(resolve, 100))
    }
    const centre = () => centred.calls as [number, number, { zoom: number }][]

    // A short path fits at a readable zoom: fitted, not moved again.
    await open(createGraph('Run'), trace(['trigger-1']))
    expect(paths()).toHaveLength(1)
    expect(centre()).toHaveLength(0)
    dispose!()

    // Nothing failed: half size, on the path's first step (its trigger, at x 60), not the far service.
    await open(far, trace(['trigger-1', 'condition-1', 'service-1']))
    expect(paths()).toHaveLength(0)
    expect(centre()).toHaveLength(1)
    expect(centre()[0][2].zoom).toBe(0.5)
    expect(centre()[0][0]).toBeLessThan(5000)
    dispose!()

    // A failed step: half size, on that step, once (the focus does not move the view again).
    await open(far, trace(['trigger-1', 'condition-1', 'service-1'], 'service-1'))
    expect(paths()).toHaveLength(0)
    expect(centre()).toHaveLength(1)
    expect(centre()[0][2].zoom).toBe(0.5)
    expect(centre()[0][0]).toBeGreaterThan(20000)
  } finally {
    vi.unstubAllGlobals()
  }
})

it('rebuilds a card whose wired input shows its source when only the source output changes: its type or its label', async () => {
  const el = host()
  let start = addNode(addNode(createGraph('Outputs'), 'constant', 'source'), 'get_field', 'pick')
  start = updateNode(start, 'source', { config: { type: 'object', value: lit('object', {}) } })
  start = contract.connectData(start, 'source', 'value', 'pick', 'value')
  const [graph, setGraph] = createSignal(start)
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const pin = () => el.querySelector<HTMLElement>('[data-id="pick"] .target[data-handleid="data:value"]')!
  expect(pin().getAttribute('data-type')).toBe('object')
  // An object or a list both fit: the wire stays, and the input now shows a list arriving.
  setGraph(updateNode(graph(), 'source', { config: { type: 'array', value: lit('array', []) } }))
  await vi.waitFor(() => expect(pin().getAttribute('data-type')).toBe('array'))
  // A loop carry renamed (with its wire): the consumer's chip names the new output.
  let loop = addNode(addNode(createGraph('Loop'), 'repeat', 'loop'), 'convert', 'shown')
  loop = updateNode(loop, 'loop', { config: { count: lit('number', 3), carry: [{ name: 'total', type: 'number', initial: lit('number', 0) }], exports: [] } })
  loop = updateNode(loop, 'shown', { config: { type: 'number', value: lit('number', 0) } })
  loop = contract.connectData(connectNodes(loop, 'service-1', 'loop'), 'loop', 'carry:total', 'shown', 'value')
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

it('shows an empty value stored as null as "Not set" in the muted style, never as the text null', async () => {
  layout()
  const el = host()
  // profile is text, hs_color a mixed value: both edited inline, empty, with nothing reading "null".
  const graph = lightOn({ profile: lit('string', null), hs_color: lit('object', null), transition: lit('number', null) })
  dispose = render(() => providers(() => <FlowAdapter graph={graph} catalog={withServices()} onSelect={() => {}} onChange={() => {}} />), el)
  const cell = (field: string) => el.querySelector<HTMLElement>(`[data-id="service-1"] [data-input-id="field:${field}"]`)!
  await vi.waitFor(() => expect(cell('hs_color')).not.toBeNull())
  expect(el.querySelector('[data-id="service-1"]')!.textContent).not.toContain('null')
  const mixed = cell('hs_color').querySelector<HTMLTextAreaElement>('textarea')!
  expect(mixed.value).toBe('')
  expect(mixed.placeholder).toBe('Not set')
  expect(cell('profile').querySelector<HTMLInputElement>('input')!.value).toBe('')
  dispose(); dispose = undefined; el.innerHTML = ''
  // Read-only, and in a run whose recorder wrote null: the muted "Not set" chip.
  const trace: FlowTrace = {
    nodes: { 'trigger-1': { status: 'ok', runs: 1 }, 'condition-1': { status: 'ok', runs: 1 }, 'service-1': { status: 'ok', runs: 1 } },
    edges: new Set(),
    value: (nodeId, port, side) => (nodeId === 'service-1' && port === 'field:transition' && side === 'in' ? chipValue({ type: 'number', v: null }) : undefined),
  }
  dispose = render(() => providers(() => <FlowAdapter graph={graph} catalog={withServices()} readOnly trace={trace} onSelect={() => {}} />), el)
  await vi.waitFor(() => expect(cell('transition')?.querySelector('.hs-value-chip')).not.toBeNull())
  const chip = cell('transition').querySelector('.hs-value-chip')!
  expect(chip.textContent).toBe('Not set')
  expect(chip.classList).toContain('hs-value-chip--muted')
  expect(el.querySelector('[data-id="service-1"]')!.textContent).not.toContain('null')
})

it('keeps a card\'s problem tag inside the canvas and clear of a floating panel: right, else left, else below or above, moved in', async () => {
  const box = (left: number, right: number, top = 100, bottom = 200) => ({ left, right, top, bottom })
  const size = { width: 200, height: 60 }
  expect(tipPlace(box(100, 300), size, 12, box(0, 1000, 0, 800))).toEqual({ side: 'right', shift: 0 })
  // Near the side panel's edge: on the left.
  expect(tipPlace(box(700, 900), size, 12, box(0, 1000, 0, 800))).toEqual({ side: 'left', shift: 0 })
  // A 360px canvas has room on neither side: below, moved left to stay inside the right edge.
  expect(tipPlace(box(40, 300), size, 12, box(0, 360, 0, 800))).toEqual({ side: 'below', shift: 0 })
  expect(tipPlace(box(200, 340), size, 12, box(0, 360, 0, 800))).toEqual({ side: 'below', shift: -40 })
  // Never past the left edge either: a tag wider than the canvas starts at its left edge.
  expect(tipPlace(box(100, 300), { width: 400, height: 60 }, 12, box(0, 360, 0, 800))).toEqual({ side: 'below', shift: -100 })
  // No room below the card: above it.
  expect(tipPlace(box(40, 300, 700, 760), size, 12, box(0, 360, 0, 800))).toEqual({ side: 'above', shift: 0 })
  // A panel floating over the canvas (full screen, 900px and wider) is an edge; one docked beside it changes nothing;
  // a sheet over the bottom of the canvas is the bottom edge.
  const canvas = box(0, 1280, 0, 800)
  expect(tipBounds(canvas, [box(908, 1268, 72, 788)])).toEqual(box(0, 908, 0, 800))
  expect(tipBounds(box(0, 920, 0, 800), [box(920, 1280, 0, 800)])).toEqual(box(0, 920, 0, 800))
  expect(tipBounds(box(0, 360, 0, 700), [box(0, 360, 640, 700)])).toEqual(box(0, 360, 0, 640))
  expect(tipBounds(canvas, [box(0, 0, 0, 0)])).toEqual(canvas)
  // As measured in the page: DOMRects, whose sides are getters.
  expect(tipBounds(new DOMRect(0, 0, 1280, 800), [new DOMRect(908, 72, 360, 716)])).toEqual(box(0, 908, 0, 800))
  expect(tipPlace(box(700, 880), size, 12, tipBounds(canvas, [box(908, 1268, 72, 788)]))).toEqual({ side: 'left', shift: 0 })
  layout()
  const el = host()
  dispose = render(() => <FlowAdapter graph={typed()} onSelect={() => {}} onChange={() => {}}
    diagnostics={[{ severity: 3, message: 'Count is required', nodeId: 'service-1', startLine: null, startColumn: null }]} />, el)
  const card = await vi.waitFor(() => { const found = el.querySelector<HTMLElement>('[data-id="service-1"] .hs-card'); expect(found).not.toBeNull(); return found! })
  const tip = card.querySelector<HTMLElement>('.hs-node-errors')!
  expect(tip.classList).toContain('hs-node-errors--right')
  const rect = (left: number, right: number, top = 0, bottom = 1000) => ({ left, right, width: right - left, top, bottom, height: bottom - top, x: left, y: top, toJSON() {} }) as DOMRect
  vi.spyOn(card, 'getBoundingClientRect').mockReturnValue(rect(700, 900, 100, 200))
  vi.spyOn(el.querySelector<HTMLElement>('.solid-flow')!, 'getBoundingClientRect').mockReturnValue(rect(0, 1000))
  vi.spyOn(tip, 'scrollWidth', 'get').mockReturnValue(220)
  // Drawn at zoom 1.
  vi.spyOn(card, 'offsetWidth', 'get').mockReturnValue(200)
  card.dispatchEvent(new PointerEvent('pointerenter'))
  await vi.waitFor(() => expect(tip.classList).toContain('hs-node-errors--left'))
  // Focus from the keyboard places it too; below, it is moved left to stay inside the canvas (20px here, at zoom 1).
  vi.spyOn(el.querySelector<HTMLElement>('.solid-flow')!, 'getBoundingClientRect').mockReturnValue(rect(600, 960))
  vi.spyOn(card, 'getBoundingClientRect').mockReturnValue(rect(780, 980, 100, 200))
  card.dispatchEvent(new FocusEvent('focusin', { bubbles: true }))
  await vi.waitFor(() => expect(tip.classList).toContain('hs-node-errors--below'))
  expect(tip.style.getPropertyValue('--hs-tip-shift')).toBe('-20px')
})

it('makes one undo step of each run of arrow-key moves', () => {
  const el = host()
  const history = createGraphHistory(createGraph('Moves'))
  dispose = render(() => <FlowAdapter graph={history.graph()} selectedId="service-1" onSelect={() => {}} onChange={history.commit} />, el)
  const x = () => history.graph().nodes.find(n => n.id === 'service-1')!.position!.x
  const start = x()
  const key = (type: 'keydown' | 'keyup', key: string) =>
    el.querySelector('[data-id="service-1"]')!.dispatchEvent(new KeyboardEvent(type, { key, bubbles: true, cancelable: true }))
  // An arrow held down repeats its keydown, then is let go once.
  for (let i = 0; i < 5; i++) key('keydown', 'ArrowRight')
  key('keyup', 'ArrowRight')
  expect(x()).toBe(start + 50)
  key('keydown', 'ArrowRight'); key('keyup', 'ArrowRight')
  key('keydown', 'ArrowRight'); key('keyup', 'ArrowRight')
  expect(x()).toBe(start + 70)
  history.undo()
  expect(x()).toBe(start + 60)
  history.undo()
  expect(x()).toBe(start + 50)
  history.undo()
  expect(x()).toBe(start)
  expect(history.canUndo()).toBe(false)
})

it('finds the timeline row in the Tab order once per selection, not once per row', () => {
  const el = host()
  const graph = createGraph('Long run')
  const events = Array.from({ length: 2000 }, (_, i) => ({ node: 'service-1', frame: '', t: i, ms: 1, status: 'ok', port: 'next', seq: i + 1 }) as TraceEvent)
  const run: RunTrace = { format: 1, runId: 'run-1', rootNodeId: 'trigger-1', startedUtc: '2026-11-12T18:00:00Z', complete: true, truncated: false, dropped: 0, trigger: {}, events,
    summary: { 'service-1': { count: 2000, failed: 0, last: 'ok' } } }
  const model = traceModel(run, graph, contract.nodeCatalog)
  // Counts the scans of the whole run for the selected row.
  let scans = 0
  const find = model.events.findIndex.bind(model.events)
  Object.defineProperty(model.events, 'findIndex', { value: (...args: Parameters<typeof find>) => { scans++; return find(...args) } })
  const [seq, setSeq] = createSignal<number | undefined>(1)
  dispose = render(() => <Timeline model={model} graph={graph} catalog={contract.nodeCatalog} selectedSeq={seq()} live={false} onSelect={() => {}} />, el)
  const rows = el.querySelectorAll<HTMLButtonElement>('.hs-trace-step')
  expect(rows).toHaveLength(2000)
  expect(rows[0].tabIndex).toBe(0)
  expect(scans).toBeLessThanOrEqual(2)
  setSeq(1500)
  expect(rows[1499].tabIndex).toBe(0)
  expect(rows[0].tabIndex).toBe(-1)
  expect(scans).toBeLessThanOrEqual(3)
})

it('a step that names an entity follows a rename and a retype of its Entity node on the card', async () => {
  const el = host()
  const start = connectNodes(add(add(empty(), 'entity', 'l', { key: 'lamp', domain: 'switch', name: 'Lamp' }), 'set_entity_state', 's', { entity: 'lamp' }), 'l', 's', 'command:turn_on')
  const [graph, setGraph] = createSignal(start)
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const summary = () => el.querySelector('[data-id="s"] .hs-card__summary')?.textContent
  const inputs = () => [...el.querySelectorAll('[data-id="s"] .hs-pin--in')].map(pin => pin.getAttribute('aria-label') ?? '')
  expect(summary()).toBe('Set state ▸ Lamp')
  const hub = () => graph().nodes.find(n => n.id === 'l')!
  setGraph(updateNode(graph(), 'l', { config: { ...hub().config, name: 'Desk lamp' } }))
  await vi.waitFor(() => expect(summary()).toBe('Set state ▸ Desk lamp'))
  expect(inputs().some(name => name.startsWith('Input Brightness'))).toBe(false)
  setGraph(updateNode(graph(), 'l', { config: { ...hub().config, domain: 'light' } }))
  await vi.waitFor(() => expect(inputs().some(name => name.startsWith('Input Brightness'))).toBe(true))
  expect(summary()).toBe('Set state ▸ Desk lamp')
})

it('a Setting step follows a relabelled setting on the card', async () => {
  const el = host()
  const settings = (label: string) => ({ settings: [{ key: 'host', label, type: 'string', value: '10.0.0.7' }] })
  const start = add({ ...empty(), definition: settings('Address') as never }, 'setting', 's', { key: 'host' })
  const [graph, setGraph] = createSignal(start)
  dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
  const summary = () => el.querySelector('[data-id="s"] .hs-card__summary')?.textContent
  expect(summary()).toBe('Setting ▸ Address')
  // The label is edited in the definition's Settings form: the step itself does not change.
  setGraph({ ...graph(), definition: settings('Host address') as never })
  await vi.waitFor(() => expect(summary()).toBe('Setting ▸ Host address'))
})

describe('the Device pin and the Device card', () => {
  /** A switch hub, and a Device node Hall plug (key plug) not wired to it yet. */
  const hubAndPlug = () => add(add(empty(), 'entity', 'hub', { key: 'hub', domain: 'switch', name: 'Hub' }), 'device', 'plug', { key: 'plug', name: 'Hall plug' })
  const wired = () => contract.connectData(hubAndPlug(), 'plug', 'entities', 'hub', 'device')

  it('an Entity card has its Device pin in the header', () => {
    const el = host()
    dispose = render(() => providers(() => <FlowAdapter graph={hubAndPlug()} onSelect={() => {}} onChange={() => {}} />), el)
    const card = el.querySelector('[data-id="hub"] .hs-card')!
    const pin = card.querySelector<HTMLElement>('.hs-card__head [data-handleid="data:device"].target')!
    expect(pin).not.toBeNull()
    expect(pin.classList).toContain('hs-pin--data')
    expect(pin.getAttribute('aria-label')).toBe('Device, not connected')
    expect(pin.style.getPropertyValue('--hs-pin-color')).toBe('var(--hs-port-device)')
    // None among the rows, and nothing on the card offers to type a device in.
    expect(card.querySelector('.hs-card__body [data-handleid="data:device"]')).toBeNull()
    expect(card.querySelector('[data-input-id="device"]')).toBeNull()
    expect(button(card, 'Set Device')).toBeUndefined()
    // The state row is still level with the first command pin, and the second command has a row of its own.
    const on = card.querySelector('[data-input-id="is_on"]')!.closest('.hs-card__row')!
    expect(on.querySelector('[data-output-id="command:turn_on"]')).not.toBeNull()
    expect(card.querySelector('[data-output-id="command:turn_off"]')!.closest('.hs-card__row')!.querySelector('[data-input-id]')).toBeNull()
    // Not wired: no device named in the header.
    expect(card.querySelector('.hs-card__device')).toBeNull()
  })

  it('an Entity card on a device names it', async () => {
    const el = host()
    const [graph, setGraph] = createSignal(wired())
    // A bare adapter, so no registry read rebuilds every card while the rename is checked.
    dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
    const head = () => el.querySelector('[data-id="hub"] .hs-card__head')!
    const device = () => head().querySelector<HTMLElement>('.hs-card__device')
    expect(device()?.textContent).toBe('Hall plug')
    // A long name is cut short on the card (flow.css) and said in full in its title.
    expect(device()?.title).toBe('Hall plug')
    expect(head().querySelector('[data-handleid="data:device"]')!.getAttribute('aria-label')).toBe('Device, connected to Hall plug')
    expect(head().querySelector('[data-handleid="data:device"]')!.classList).toContain('hs-pin--connected')
    // Renamed in its own node, the device is named so on the entity's card at once.
    const plug = () => graph().nodes.find(n => n.id === 'plug')!
    setGraph(updateNode(graph(), 'plug', { config: { ...plug().config, name: 'Kitchen plug with a very long name indeed' } }))
    await vi.waitFor(() => expect(device()?.textContent).toBe('Kitchen plug with a very long name indeed'))
    expect(device()?.title).toBe('Kitchen plug with a very long name indeed')
    // Unwired, the name goes.
    setGraph(contract.disconnectData(graph(), 'hub', 'device'))
    await vi.waitFor(() => expect(device()).toBeNull())
  })

  it('a Device card in a run trace is drawn as outside a trace: it is not a step that did not run', async () => {
    const el = host()
    // The hub's flow ran; a Log step did not: it is drawn as not reached, the Device node is not.
    const graph = add(wired(), 'log', 'stray', { value: lit('string', 'Hi') })
    const trace: FlowTrace = { nodes: { hub: { status: 'ok', runs: 1 } }, edges: new Set() }
    dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} />, el)
    await vi.waitFor(() => expect(el.querySelector('[data-id="plug"] .hs-card')).not.toBeNull())
    const card = (id: string) => el.querySelector(`[data-id="${id}"] .hs-card`)!
    expect(card('stray').classList).toContain('hs-card--unreached')
    expect(card('stray').querySelector('.hs-card__trace-word')!.textContent).toBe('Not reached')
    expect(card('hub').classList).toContain('hs-card--status-ok')
    // No dashed, faded card, no status chip or tip, and its name says nothing of the run.
    const plug = card('plug')
    for (const name of ['hs-card--unreached', 'hs-card--traced', 'hs-card--focus']) expect(plug.classList, name).not.toContain(name)
    expect([...plug.classList].filter(name => name.startsWith('hs-card--status-'))).toEqual([])
    expect(plug.querySelector('.hs-card__trace')).toBeNull()
    expect(plug.querySelector('[role="tooltip"]')).toBeNull()
    expect(plug.getAttribute('aria-describedby')).toBeNull()
    expect(el.querySelector('[data-id="plug"]')!.getAttribute('aria-label')).not.toContain('Run:')
    expect(el.querySelector('[data-id="stray"]')!.getAttribute('aria-label')).toMatch(/\. Run: Not reached$/)
  })

  it('a Device card has an Entities pin and no step pins', () => {
    const el = host()
    // A Log step outside the flow, which is drawn muted: the Device node, which never runs, is not.
    const graph = add(wired(), 'log', 'stray', { value: lit('string', 'Hi') })
    dispose = render(() => providers(() => <FlowAdapter graph={graph} onSelect={() => {}} onChange={() => {}} />), el)
    const card = el.querySelector('[data-id="plug"] .hs-card')!
    expect(card.querySelector('[data-handleid="in"]')).toBeNull()
    expect(card.querySelector('[data-handleid="next"]')).toBeNull()
    expect(card.querySelector('[data-handleid="error"]')).toBeNull()
    expect(card.querySelectorAll('.target')).toHaveLength(0)
    expect([...card.querySelectorAll('.source')].map(pin => pin.getAttribute('data-handleid'))).toEqual(['data:entities'])
    expect(card.querySelector('[data-output-id="entities"] .hs-pin-label')!.textContent).toBe('Entities')
    expect(card.querySelector('.hs-card__title')!.textContent).toBe('Hall plug')
    expect(card.querySelector('.hs-card__summary')!.textContent).toBe('Device · plug')
    expect(card.classList).not.toContain('hs-card--unreachable')
    expect(el.querySelector('[data-id="stray"] .hs-card')!.classList).toContain('hs-card--unreachable')
  })

  it('a Device card follows its name and key', async () => {
    const el = host()
    const [graph, setGraph] = createSignal(wired())
    dispose = render(() => providers(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />), el)
    const card = () => el.querySelector('[data-id="plug"] .hs-card')!
    setGraph(updateNode(graph(), 'plug', { config: { key: 'socket' } }))
    // No name: named by its key.
    await vi.waitFor(() => expect(card().querySelector('.hs-card__title')!.textContent).toBe('socket'))
    expect(card().querySelector('.hs-card__summary')!.textContent).toBe('Device · socket')
    setGraph(updateNode(graph(), 'plug', { config: {} }))
    await vi.waitFor(() => expect(card().querySelector('.hs-card__title')!.textContent).toBe('Device'))
  })
})

describe('the C# entity and C# device cards', () => {
  const setting = (key: string, type: contract.ClassSetting['type'] = 'string', more: Partial<contract.ClassSetting> = {}): contract.ClassSetting =>
    ({ key, label: key, description: '', type, secret: false, ...more })
  /** A Thermometer's settings: an address, a port of 1 to 65535, a mode, a switch and a secret token. */
  const settings = () => [setting('Address'), setting('Port', 'integer', { minimum: 1, maximum: 65535 }), setting('Mode', 'enum', { options: ['eco', 'comfort'] }),
    setting('Fast', 'boolean'), setting('Token', 'string', { secret: true })]
  const signature = (title: string, domain: string | null, list = settings()): contract.ClassSignature => ({ title, description: '', domain, settings: list })
  /** A Device node Hall (key hall), a C# entity Thermometer (key thermometer) on it, and a C# device Plug (key plug). */
  const nodes = (args: Record<string, unknown> = {}) => {
    let graph: VisualGraph = { ...empty('Plug'), definition: { settings: [{ key: 'token', label: 'Token', type: 'string', secret: true, value: null, hasValue: true }] } as never }
    graph = add(graph, 'device', 'hall', { key: 'hall', name: 'Hall' })
    graph = add(graph, 'csharp_entity', 'thermo', { key: 'thermometer', signature: signature('Thermometer', 'sensor'), arguments: structuredClone(args) })
    graph = add(graph, 'csharp_device', 'plug', { key: 'plug', name: 'Hall plug', signature: signature('Plug', null), arguments: structuredClone(args) })
    return contract.connectData(graph, 'hall', 'entities', 'thermo', 'device')
  }
  const card = (el: Element, id: string) => el.querySelector(`[data-id="${id}"] .hs-card`)!
  const rowLabels = (el: Element, id: string) => [...card(el, id).querySelectorAll('.hs-card__body [data-input-id]')].map(row => [row.getAttribute('data-input-id'), row.querySelector('.hs-pin-label')!.textContent])

  it('a C# entity card is titled by its class, says its type and key, has the Device pin in its header and a row per setting', () => {
    const el = host()
    dispose = render(() => providers(() => <FlowAdapter graph={nodes()} onSelect={() => {}} onChange={() => {}} onEditCode={() => {}} />), el)
    const thermo = card(el, 'thermo')
    expect(thermo.querySelector('.hs-card__title')!.textContent).toBe('Thermometer')
    expect(thermo.querySelector('.hs-card__summary')!.textContent).toBe('Sensor · thermometer')
    // The Device pin is the header's, with the device it is on beside it; none among the rows.
    const pin = thermo.querySelector<HTMLElement>('.hs-card__head [data-handleid="data:device"].target')!
    expect(pin.getAttribute('aria-label')).toBe('Device, connected to Hall')
    expect(thermo.querySelector('.hs-card__device')!.textContent).toBe('Hall')
    expect(thermo.querySelector('.hs-card__body [data-handleid="data:device"]')).toBeNull()
    expect(rowLabels(el, 'thermo')).toEqual([['Address', 'Address'], ['Port', 'Port'], ['Mode', 'Mode'], ['Fast', 'Fast'], ['Token', 'Token']])
    // Its code is edited from the card.
    expect(thermo.querySelector('.hs-card__code')!.getAttribute('aria-label')).toBe('Edit code of Thermometer')
    // A declaration: no step pins.
    for (const handle of ['in', 'next', 'error']) expect(thermo.querySelector(`[data-handleid="${handle}"]`), handle).toBeNull()
  })

  it('a C# device card is titled by its name, says C# device and its key, and has a row per setting and no Device pin', () => {
    const el = host()
    dispose = render(() => providers(() => <FlowAdapter graph={nodes()} onSelect={() => {}} onChange={() => {}} onEditCode={() => {}} />), el)
    const plug = card(el, 'plug')
    expect(plug.querySelector('.hs-card__title')!.textContent).toBe('Hall plug')
    expect(plug.querySelector('.hs-card__summary')!.textContent).toBe('C# device · plug')
    expect(plug.querySelector('[data-handleid="data:device"]')).toBeNull()
    expect(rowLabels(el, 'plug').map(([id]) => id)).toEqual(['Address', 'Port', 'Mode', 'Fast', 'Token'])
    expect(plug.querySelector('.hs-card__code')!.getAttribute('aria-label')).toBe('Edit code of Hall plug')
  })

  it('shows a typed value as other cards do, a wired setting as wired, and a secret never: not even as dots', async () => {
    const el = host()
    const typed = { Address: lit('string', 'http://10.0.0.7'), Port: lit('number', 8080), Mode: lit('string', 'comfort'), Token: lit('string', 's3cr3t-token') }
    let graph = add(nodes(typed), 'setting', 'tok', { key: 'token' })
    const plug = graph.nodes.find(n => n.id === 'plug')!
    graph = updateNode(graph, 'plug', { config: { ...plug.config, arguments: { Address: lit('string', 'http://10.0.0.8') } } })
    graph = contract.connectData(graph, 'tok', 'value', 'plug', 'Token')
    // A bare adapter: values are chips, as on a read-only canvas.
    dispose = render(() => <FlowAdapter graph={graph} onSelect={() => {}} />, el)
    const chip = (id: string, port: string) => card(el, id).querySelector(`[data-input-id="${port}"] .hs-value-chip`)?.textContent
    expect(chip('thermo', 'Address')).toBe('http://10.0.0.7')
    expect(chip('thermo', 'Port')).toBe('8080')
    // An enum's member as the code names it.
    expect(chip('thermo', 'Mode')).toBe('comfort')
    // The typed secret of the hand-made C# entity: no value, no editor, nothing that shows how long it is.
    const token = card(el, 'thermo').querySelector('[data-input-id="Token"]')!
    expect(token.querySelector('.hs-value-chip')).toBeNull()
    expect(token.querySelector('.hs-card-editor')).toBeNull()
    expect(token.textContent).toBe('Token')
    // The C# device's secret is wired from the Setting: it shows its source, as a wired input does.
    const wired = card(el, 'plug').querySelector('[data-input-id="Token"]')!
    expect(wired.classList).toContain('hs-pin-cell--connected')
    expect(wired.querySelector('.hs-source-chip')!.textContent).toBe('Setting · Value')
    // Not anywhere on the canvas, in an attribute either.
    expect(el.innerHTML).not.toContain('s3cr3t')
    expect(el.innerHTML).not.toContain('••')
  })

  it('offers no editor for a secret on an editable card, and the editors of the others by their type', async () => {
    const el = host()
    dispose = render(() => providers(() => <FlowAdapter graph={nodes({ Token: lit('string', 's3cr3t-token') })} onSelect={() => {}} onChange={() => {}} />), el)
    const row = (port: string) => card(el, 'thermo').querySelector(`[data-input-id="${port}"]`)!
    // An unset optional setting offers Set; a secret does not.
    for (const port of ['Address', 'Port', 'Mode', 'Fast']) expect(row(port).querySelector('button.hs-pin-set')?.getAttribute('aria-label'), port).toBe(`Set ${port}`)
    expect(row('Token').querySelector('button.hs-pin-set')).toBeNull()
    expect(row('Token').querySelector('.hs-card-editor')).toBeNull()
    expect(el.innerHTML).not.toContain('s3cr3t')
    // Opened, the enum's editor is a choice of its members.
    row('Mode').querySelector<HTMLButtonElement>('button.hs-pin-set')!.click()
    await vi.waitFor(() => expect(row('Mode').querySelector('select')).not.toBeNull())
    expect([...row('Mode').querySelector('select')!.options].filter(o => !o.disabled).map(o => o.textContent)).toEqual(['eco', 'comfort'])
  })

  it('marks a required entity setting as other required inputs are, shows its problem while empty, and picks a light for a LightEntity', async () => {
    const el = host()
    const entitySettings = [setting('Lamp', 'entity', { domain: 'light', required: true }), setting('Source', 'entity')]
    let graph = add(nodes(), 'csharp_entity', 'dimmer', { key: 'dimmer', signature: signature('Dimmer', 'switch', entitySettings) })
    graph = contract.connectData(graph, 'hall', 'entities', 'dimmer', 'device')
    const diagnostics = contract.validateGraph(graph, contract.nodeCatalog).filter(d => d.nodeId === 'dimmer')
    expect(diagnostics.map(d => d.message)).toEqual(['Lamp is required'])
    const known = ['light.kitchen', 'light.porch', 'switch.fan'].map(entity_id => ({ entity_id, name: null, domain: entity_id.split('.')[0], area_id: null, device_id: null, device_class: null,
      supported_features: null, unit: null, icon: null, disabled: false, state_options: null }))
    const fixture = visualFixture()
    const callWS = (message: any) => (message.type === 'hass_sharp/get_entities' ? Promise.resolve({ entities: known, areas: [], floors: [], labels: [], devices: [] }) : fixture.callWS(message))
    const [current, setCurrent] = createSignal(graph)
    dispose = render(() => providers(() => <FlowAdapter graph={current()} onSelect={() => {}} onChange={setCurrent} diagnostics={diagnostics as never} />, { ...fixture, callWS } as never), el)
    const row = (port: string) => card(el, 'dimmer').querySelector(`[data-input-id="${port}"]`)!
    // The required mark and the pin's name say it is required; the optional Source has neither.
    expect(row('Lamp').querySelector('.hs-required')!.getAttribute('title')).toBe('Required')
    expect(row('Lamp').querySelector('[data-handleid="data:Lamp"]')!.getAttribute('aria-label')).toContain(', required,')
    expect(row('Source').querySelector('.hs-required')).toBeNull()
    expect(row('Source').querySelector('[data-handleid="data:Source"]')!.getAttribute('aria-label')).not.toContain('required')
    // Its problem is the card's.
    expect(card(el, 'dimmer').classList).toContain('hs-flow-card--error')
    expect(card(el, 'dimmer').querySelector('.hs-node-errors')!.textContent).toContain('Lamp is required')
    // The Lamp's picker offers the lights only, and stores the one chosen as a light's id.
    row('Lamp').querySelector<HTMLButtonElement>('.hs-picker-field')!.click()
    const options = () => [...document.querySelectorAll<HTMLButtonElement>('dialog[open] [role="option"]')]
    await vi.waitFor(() => expect(options().map(o => o.querySelector('small')!.textContent)).toEqual(['light.kitchen', 'light.porch']))
    options()[0].click()
    await vi.waitFor(() => expect((current().nodes.find(n => n.id === 'dimmer')!.config.arguments as Record<string, unknown>).Lamp).toEqual(lit('entity_id:light', 'light.kitchen')))
  })

  it('shows a setting relabelled by applied code on the card at once', async () => {
    const el = host()
    const [graph, setGraph] = createSignal(nodes())
    dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
    const relabelled = settings().map(s => (s.key === 'Address' ? { ...s, label: 'Host address' } : s))
    const thermo = graph().nodes.find(n => n.id === 'thermo')!
    setGraph(updateNode(graph(), 'thermo', { config: { ...thermo.config, signature: signature('Thermometer', 'sensor', relabelled) } }))
    await vi.waitFor(() => expect(card(el, 'thermo').querySelector('[data-input-id="Address"] .hs-pin-label')!.textContent).toBe('Host address'))
  })

  it('lists the entities Home Assistant has for a C# device, by name and entity id, and says when it has none yet', async () => {
    const el = host()
    const [entities, setEntities] = createSignal<Record<string, { name: string; entityId: string | null }[]>>({
      plug: [{ name: 'Relay', entityId: 'switch.plug_relay' }, { name: 'Power', entityId: 'sensor.plug_power' }],
    })
    dispose = render(() => <FlowAdapter graph={nodes()} onSelect={() => {}} deviceEntities={entities()} />, el)
    const list = () => card(el, 'plug').querySelector('.hs-card__entities')
    const listed = () => [...list()!.querySelectorAll('li')].map(li => [li.querySelector('.hs-pin-label')!.textContent, li.querySelector('code')?.textContent ?? null])
    expect(list()!.getAttribute('aria-label')).toBe('Entities of Hall plug')
    expect(listed()).toEqual([['Relay', 'switch.plug_relay'], ['Power', 'sensor.plug_power']])
    // Under its settings.
    const body = card(el, 'plug').querySelector('.hs-card__body')!
    expect(body.lastElementChild!.contains(list())).toBe(true)
    // The C# entity is no device: it lists nothing.
    expect(card(el, 'thermo').querySelector('.hs-card__entities, .hs-card__none')).toBeNull()
    // None yet: it says when they appear.
    setEntities({})
    await vi.waitFor(() => expect(list()).toBeNull())
    expect(card(el, 'plug').querySelector('.hs-card__none')!.textContent).toBe('Its entities appear here after it has started once')
    // Many: the first six, as the Entities page's card lists them, and how many more.
    setEntities({ plug: Array.from({ length: 8 }, (_, i) => ({ name: `Outlet ${i + 1}`, entityId: `switch.plug_outlet_${i + 1}` })) })
    await vi.waitFor(() => expect(list()).not.toBeNull())
    expect(listed().map(([name]) => name)).toEqual(['Outlet 1', 'Outlet 2', 'Outlet 3', 'Outlet 4', 'Outlet 5', 'Outlet 6'])
    expect(card(el, 'plug').querySelector('.hs-card__more')!.textContent).toBe('and 2 more')
  })

  it('lists no entities on a canvas that is not told them (a run\'s)', () => {
    const el = host()
    dispose = render(() => <FlowAdapter graph={nodes()} readOnly onSelect={() => {}} />, el)
    expect(card(el, 'plug').querySelector('.hs-card__entities, .hs-card__none')).toBeNull()
  })

  it('draws C# nodes in a run trace as outside it, as a Device node: never as not reached', async () => {
    const el = host()
    const graph = add(nodes(), 'log', 'stray', { value: lit('string', 'Hi') })
    const trace: FlowTrace = { nodes: {}, edges: new Set() }
    dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} />, el)
    await vi.waitFor(() => expect(el.querySelector('[data-id="plug"] .hs-card')).not.toBeNull())
    expect(card(el, 'stray').classList).toContain('hs-card--unreached')
    for (const id of ['thermo', 'plug']) {
      const drawn = card(el, id)
      for (const name of ['hs-card--unreached', 'hs-card--traced', 'hs-card--unreachable']) expect(drawn.classList, `${id} ${name}`).not.toContain(name)
      expect(drawn.querySelector('.hs-card__trace'), id).toBeNull()
      expect(el.querySelector(`[data-id="${id}"]`)!.getAttribute('aria-label'), id).not.toContain('Run:')
    }
  })
})

it('names a value node no step read "Not used" to a screen reader, as its card says', async () => {
  const el = host()
  let graph = addNode(addNode(createGraph('Values'), 'constant', 'spare'), 'log', 'say')
  graph = updateNode(graph, 'spare', { config: { type: 'number', value: lit('number', 3) } })
  graph = connectNodes(graph, 'service-1', 'say')
  const trace: FlowTrace = { nodes: { 'trigger-1': { status: 'ok', runs: 1 } }, edges: new Set() }
  dispose = render(() => <FlowAdapter graph={graph} readOnly trace={trace} onSelect={() => {}} />, el)
  await vi.waitFor(() => expect(el.querySelector('[data-id="spare"] .hs-card')).not.toBeNull())
  expect(el.querySelector('[data-id="spare"] .hs-card__trace-word')!.textContent).toBe('Not used')
  expect(el.querySelector('[data-id="spare"]')!.getAttribute('aria-label')).toMatch(/\. Run: Not used$/)
  // A step that did not run is still not reached.
  expect(el.querySelector('[data-id="say"]')!.getAttribute('aria-label')).toMatch(/\. Run: Not reached$/)
})

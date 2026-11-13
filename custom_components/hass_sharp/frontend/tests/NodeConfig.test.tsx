import { afterEach, expect, it } from 'vitest'
import { createSignal } from 'solid-js'
import { render } from 'solid-js/web'
import { NodeConfig } from '../src/visual/NodeConfig'
import { QuickAdd } from '../src/visual/QuickAdd'
import { TriggerConfig } from '../src/visual/TriggerConfig'
import { ValueOperand } from '../src/visual/ValueOperand'
import { addNode, createGraph, catalogPalette, catalogCategories, type VisualGraph } from '../src/visual/graph'
import { nodeCatalog } from '../src/visual/contract'
import { HassCtx } from '../src/context'
import { QueryClient, QueryClientProvider } from '@tanstack/solid-query'
let dispose: () => void
let client: QueryClient
afterEach(() => {
  dispose?.()
  client?.clear()
  document.body.innerHTML = ''
})
function host() {
  const host = document.createElement('div')
  document.body.append(host)
  return host
}
function change(root: Element, label: string, value: string) {
  const field = root.querySelector<HTMLInputElement>(`[aria-label="${label}"]`)!
  expect(field, label).not.toBeNull()
  field.value = value
  field.dispatchEvent(new Event(field.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true }))
}
function mount(graph: VisualGraph, id: string) {
  const root = host(),
    [current, setGraph] = createSignal(graph)
  client = new QueryClient()
  const node = () => current().nodes.find(n => n.id === id)!
  dispose = render(
    () => (
      <QueryClientProvider client={client}>
        <HassCtx.Provider value={{ callWS: async () => ({ entities: [{ entity_id: 'light.kitchen', name: 'Kitchen' }] }) } as any}>
          <NodeConfig
            node={node()}
            graph={current()}
            catalog={nodeCatalog}
            onChange={config => setGraph({ ...current(), nodes: current().nodes.map(n => (n.id === id ? { ...n, config } : n)) })}
            onGraphChange={setGraph}
          />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    root,
  )
  return { root, node, graph: current }
}
it.each(['condition', 'read_entity', 'wait_until'])('keeps %s entity literal picker required', kind => {
  const { root } = mount(addNode(createGraph('Entity'), kind, 'required'), 'required')
  change(root, 'Entity source', 'literal')
  root.querySelector<HTMLButtonElement>('[aria-label="Entity ID"]')!.click()
  expect(root.textContent).not.toContain('No target')
  change(root, 'Search entities', '')
  expect(root.textContent).not.toContain('Use ID:')
})
it.each([
  ['entity_id:light', true],
  ['entity_id', true],
  ['string', false],
])('edits a %s literal with the entity picker only when it refines entity_id', (type, picker) => {
  const root = host()
  client = new QueryClient()
  dispose = render(
    () => (
      <QueryClientProvider client={client}>
        <HassCtx.Provider value={{ callWS: async () => ({ entities: [] }) } as any}>
          <ValueOperand
            label="Entity"
            value={{ kind: 'literal', type, value: 'light.kitchen' }}
            spec={{ types: [type], required: true, description: '' }}
            catalog={nodeCatalog}
            onChange={() => {}}
          />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    root,
  )
  expect(root.querySelector('[aria-label="Entity ID"]') !== null).toBe(picker)
  expect(root.querySelector('input[aria-label="Entity"]') !== null).toBe(!picker)
})
it('keeps trigger subscriptions required', () => {
  const root = host()
  client = new QueryClient()
  dispose = render(
    () => (
      <QueryClientProvider client={client}>
        <HassCtx.Provider value={{ callWS: async () => ({ entities: [] }) } as any}>
          <TriggerConfig node={createGraph('Trigger').nodes[0]} onChange={() => {}} />
        </HassCtx.Provider>
      </QueryClientProvider>
    ),
    root,
  )
  root.querySelector<HTMLButtonElement>('[aria-label="Entity ID"]')!.click()
  expect(root.textContent).not.toContain('No target')
})
it('categorizes every catalog node and groups and filters the quick-add palette', () => {
  const root = host()
  let chosen = ''
  dispose = render(() => <QuickAdd x={0} y={0} title="Add a step" allowed={() => true} onClose={() => {}} onChoose={kind => (chosen = kind)} />, root)
  expect(new Set(catalogPalette().map(item => item.category))).toEqual(new Set(catalogCategories()))
  const groups = [...root.querySelectorAll('[role="group"]')]
  expect(groups.map(group => group.querySelector('.hs-quick-add__category')!.textContent)).toEqual(catalogCategories().filter(c => catalogPalette().some(n => n.category === c)))
  const logic = groups.find(group => group.querySelector('.hs-quick-add__category')!.textContent === 'Logic')!
  expect(logic.querySelectorAll('[role="option"]')).toHaveLength(catalogPalette().filter(n => n.category === 'Logic').length)
  change(root, 'Search steps', 'Xor')
  expect(root.querySelectorAll('[role="option"]')).toHaveLength(1)
  root.querySelector('input')!.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  expect(chosen).toBe('xor')
  change(root, 'Search steps', 'no such step anywhere')
  expect(root.textContent).toContain('No step matches that search.')
})
it('offers typed named variables and keeps producer configuration unchanged', () => {
  let graph = addNode(addNode(addNode(createGraph('Values'), 'set_value', 'text'), 'and', 'boolean'), 'if', 'if')
  graph = {
    ...graph,
    nodes: graph.nodes.map(n =>
      n.id === 'boolean'
        ? { ...n, config: { ...n.config, output: 'is_ready' } }
        : n.id === 'text'
          ? { ...n, config: { ...n.config, output: 'name' } }
          : n,
    ),
  }
  const { root, node } = mount(graph, 'if')
  change(root, 'Condition source', 'variable')
  expect([...root.querySelectorAll('datalist option')].map(o => o.getAttribute('value'))).toEqual(['is_ready'])
  change(root, 'Condition variable', 'is_ready')
  expect(node().config.value).toEqual({ kind: 'variable', name: 'is_ready' })
  change(root, 'Condition source', 'literal')
  change(root, 'Condition', 'false')
  expect(node().config.value).toEqual({ kind: 'literal', type: 'boolean', value: false })
  expect(graph.nodes.find(n => n.id === 'boolean')!.config.left).toEqual({ kind: 'literal', type: 'boolean', value: true })
})
it('preserves intermediate numeric input, element identity/focus and invalid cleared input', () => {
  const { root, node } = mount(addNode(createGraph('Numbers'), 'compare', 'compare'), 'compare')
  const field = root.querySelector<HTMLInputElement>('[aria-label="Left"]')!
  field.focus()
  for (const value of ['-', '-2', '-2.', '-2.5']) {
    change(root, 'Left', value)
    expect(root.querySelector('[aria-label="Left"]')).toBe(field)
    expect(field.value).toBe(value)
    expect(document.activeElement).toBe(field)
  }
  expect(node().config.left).toEqual({ kind: 'literal', type: 'number', value: -2.5 })
  change(root, 'Left', '')
  expect(node().config.left).toEqual({ kind: 'literal', type: 'number', value: '' })
  expect(field.getAttribute('aria-invalid')).toBe('true')
  expect(root.textContent).toContain('Enter a finite number')
})
it('edits object and array literal JSON and explicit unset without discarding unknown config', () => {
  const graph = addNode(createGraph('Objects'), 'object', 'call')
  graph.nodes.at(-1)!.config.extra = { keep: true }
  const { root, node } = mount(graph, 'call')
  change(root, 'Object', '{"brightness":120}')
  expect(node().config.value).toEqual({ kind: 'literal', type: 'object', value: { brightness: 120 } })
  change(root, 'Object source', 'unset')
  expect(node().config.value).toEqual({ kind: 'unset' })
  expect(node().config.extra).toEqual({ keep: true })
})

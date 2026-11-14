import { afterEach, describe, expect, it, vi } from 'vitest'
import { render } from 'solid-js/web'
import { analyzeGraph, connectData, dataOutputs, nodeCatalog, resolveOutputs, withGraphTypes, type Graph, type GraphNode, type TypeSpec, type VisualCatalogSnapshot } from '../src/visual/contract'
import { connectionIndex, graphTypes } from '../src/visual/connectionIndex'
import { dataConnectionError } from '../src/visual/graph'
import { FlowAdapter } from '../src/visual/FlowAdapter'
import { createSignal } from 'solid-js'
import { readFileSync } from 'node:fs'
import { words } from '../src/visual/contract'
import { canAddFor } from '../src/visual/flow/autoConnect'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { NodeConfig } from '../src/visual/NodeConfig'
import { nodeSummary } from '../src/visual/nodeSummary'
import { chooseProperty, propertyChoices, propertyText } from '../src/visual/selectProperty'

// Task 30: a Select property node reads one property of a typed value; the types come from stored Custom C# signatures.
const U = { kind: 'unset' }
const entry = (label: string, properties?: TypeSpec['properties']): TypeSpec => ({ label, description: '', category: 'Object', nullable: false, literalKind: null, ...(properties ? { properties } : {}) })
const USER = 'global::NodeLib.User', ROOM = 'global::NodeLib.Room'
const types: Record<string, TypeSpec> = {
  [USER]: entry('User', [{ name: 'UserName', type: 'string', nullable: false, description: 'What the person is called.' }, { name: 'Age', type: 'number', nullable: false }]),
  [ROOM]: entry('Room', [{ name: 'Age', type: 'string', nullable: false }]),
}
const make = (id: string, type: string, own: Record<string, TypeSpec> = types): GraphNode => ({
  id, kind: 'custom', config: {
    source: 'public static class CustomNode { }', arguments: {},
    signature: { title: id, description: '', pure: false, inputs: {}, outputs: { result: { type, label: 'Result', description: '' } }, types: own },
  },
})
const select = (id: string, property: unknown = ''): GraphNode => ({ id, kind: 'select_property', config: { property } })
const step = (source: string, target: string, port = 'next') => ({ id: `${source}:${port}:${target}`, source, target, port })
const wire = (source: string, sourcePort: string, target: string, targetPort: string) => ({ id: `${source}.${sourcePort}:${target}.${targetPort}`, kind: 'data' as const, source, target, sourcePort, targetPort })
/** t -> user -> room -> d (a Delay), and Select property `s`, its value from `from`, into the Delay's seconds when `into`. */
function graph(property: unknown, from: string | null = 'user', into = true): Graph {
  return {
    version: 5, name: 'Select',
    nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } }, make('user', USER), make('room', ROOM), select('s', property), { id: 'd', kind: 'delay', config: { seconds: U } }, { id: 'l', kind: 'log', config: { value: { kind: 'literal', type: 'string', value: 'hi' } } }],
    edges: [step('t', 'user'), step('user', 'room'), step('room', 'd'), step('d', 'l'), ...(from ? [wire(from, 'result', 's', 'value')] : []), ...(into ? [wire('s', 'value', 'd', 'seconds')] : [])],
  }
}
const node = (g: Graph, id: string) => g.nodes.find(n => n.id === id)!
let dispose: (() => void) | undefined
afterEach(() => { dispose?.(); vi.restoreAllMocks(); document.body.innerHTML = '' })

describe('the type of a Select property node', () => {
  it('is the chosen property type of the wired value, resolved per graph', () => {
    const g = graph('Age')
    expect(dataOutputs(node(g, 's'), withGraphTypes(nodeCatalog, g)).value.type).toBe('number')
    // The catalog alone does not know it: the type comes from the wire.
    expect(dataOutputs(node(g, 's'), nodeCatalog).value.type).toBe('')
    expect(dataOutputs(node(graph('Age', 'room'), 's'), graphTypes(graph('Age', 'room'))).value.type).toBe('string')
    expect(dataOutputs(node(graph(''), 's'), graphTypes(graph(''))).value.type).toBe('')
    expect(analyzeGraph(g).diagnostics.filter(d => d.severity === 3)).toEqual([])
  })
  it('lists the properties to choose once a value is connected', () => {
    expect(propertyChoices(graph('', null), node(graph('', null), 's'), nodeCatalog)).toEqual({ connected: false, properties: [] })
    const g = graph('')
    const choices = propertyChoices(g, node(g, 's'), nodeCatalog)
    expect(choices.type).toBe(USER)
    expect(choices.properties.map(p => propertyText(p, graphTypes(g)))).toEqual(['UserName · Text (string)', 'Age · Number (double)'])
    expect(nodeSummary(node(graph('Age'), 's'), nodeCatalog, undefined, graph('Age'))).toBe('Age')
  })
  it('names the output pin by the property type, or says no property is chosen', () => {
    const view = (g: Graph) => buildNodeView(node(g, 's'), { graph: g, catalog: nodeCatalog, diagnostics: [] }).rows.find(r => r.output)!.output!
    expect(view(graph('Age')).typeName).toBe('Number')
    expect(view(graph('')).typeName).toBe('No property chosen')
    expect(view(graph('Level')).typeName).toBe('Unknown type')
  })
})

describe('resolving the types of a chain of Select property nodes', () => {
  // Fix 1: each output's type follows the node before it, resolved in dependency order, never one call deeper per node.
  const LINK = 'global::NodeLib.Link'
  const links = { [LINK]: entry('Link', [{ name: 'Next', type: LINK, nullable: false }]) }
  const withLinks: VisualCatalogSnapshot = { ...nodeCatalog, types: { ...nodeCatalog.types, ...links } }
  /** v (a Link) -> s0 (Next) -> s1 (Next) -> …, listed last first, so a resolver that goes to each source first goes the whole way down. */
  function chain(count: number): Graph {
    const nodes: GraphNode[] = [{ id: 't', kind: 'trigger', config: { type: 'manual' } }, make('v', LINK, links)]
    const edges: Graph['edges'] = []
    for (let i = count - 1; i >= 0; i--) { nodes.push(select(`s${i}`, 'Next')); edges.push(wire(i ? `s${i - 1}` : 'v', i ? 'value' : 'result', `s${i}`, 'value')) }
    return { version: 5, name: 'Chain', nodes, edges }
  }
  const COUNT = 50_000
  it('resolves a chain of any length to its last node', () => {
    const catalog = { ...withLinks }
    resolveOutputs(catalog, chain(COUNT))
    expect(catalog.resolvedOutputs?.[`s${COUNT - 1}`]?.value).toBe(LINK)
  })
  it('leaves a cycle, what reads it and a node reading itself unresolved', () => {
    const catalog = { ...withLinks }
    resolveOutputs(catalog, { version: 5, name: 'Cycle', nodes: [make('v', LINK, links), select('a', 'Next'), select('b', 'Next'), select('c', 'Next'), select('me', 'Next')],
      edges: [wire('b', 'value', 'a', 'value'), wire('a', 'value', 'b', 'value'), wire('a', 'value', 'c', 'value'), wire('me', 'value', 'me', 'value')] })
    expect(catalog.resolvedOutputs).toEqual({})
  })
  it('resolves nothing past the graph limit, and the validator answers the limit only', () => {
    const long = chain(COUNT)
    expect(withGraphTypes(nodeCatalog, long)).toBe(nodeCatalog)
    expect(analyzeGraph(long).diagnostics.map(d => d.code)).toEqual(['graph.limit'])
  })
})

describe('dragging a wire from a Select property node (live defect D1)', () => {
  // As on the live instance: a value node gives a User, Select property (UserName) reads it, and a Log takes the text.
  const pure = (node: GraphNode) => { (node.config.signature as { pure: boolean }).pure = true; return node }
  const HOLDER = 'global::NodeLib.Holder', ADDRESS = 'global::NodeLib.Address'
  const holder = { [HOLDER]: entry('Holder', [{ name: 'Home', type: ADDRESS, nullable: false }]), [ADDRESS]: entry('Address', [{ name: 'City', type: 'string', nullable: false }, { name: 'Zip', type: 'number', nullable: false }]) }
  const log = (id = 'l'): GraphNode => ({ id, kind: 'log', config: { value: U } })
  function live(property: unknown = 'UserName'): Graph {
    return {
      version: 5, name: 'Smoke',
      nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } }, pure(make('user', USER)), select('s', property), log(), pure(make('h', HOLDER, holder)), select('home', 'Home'), select('city', 'City'), log('l2')],
      edges: [step('t', 'l'), step('l', 'l2'), wire('user', 'result', 's', 'value'), wire('h', 'result', 'home', 'value'), wire('home', 'value', 'city', 'value')],
    }
  }
  it('connects the chosen property into a step that takes any value, as connectData does', () => {
    const g = live()
    expect(dataConnectionError(g, 's', 'value', 'l', 'value', nodeCatalog, { quick: true })).toBeUndefined()
    expect(dataConnectionError(g, 's', 'value', 'l', 'value', nodeCatalog)).toBeUndefined()
    expect(dataConnectionError(g, 'city', 'value', 'l2', 'value', nodeCatalog)).toBeUndefined()
    expect(connectData(g, 's', 'value', 'l', 'value')).toBeTruthy()
  })
  it('connects into typed inputs by the property type: a Delay, a Compare, a step already wired when the property changes', () => {
    const g = { ...live('Age'), nodes: [...live('Age').nodes, { id: 'd', kind: 'delay', config: { seconds: U } }, { id: 'c', kind: 'compare', config: { operator: 'gt', left: U, right: { kind: 'literal', type: 'number', value: 3 } } }] }
    expect(dataConnectionError(g, 's', 'value', 'd', 'seconds', nodeCatalog)).toBeUndefined()
    expect(dataConnectionError(g, 's', 'value', 'c', 'left', nodeCatalog)).toBeUndefined()
    // The text property into the number pin: the validator's words, never an empty type.
    const text = updateSelect(g, 'UserName')
    expect(dataConnectionError(text, 's', 'value', 'd', 'seconds', nodeCatalog)).toBe('Seconds accepts Number, not Text')
    // Wired while Age, then the property becomes UserName: the wire is removed (chooseProperty), and dragging it back is refused in words.
    const wired = connectData(g, 's', 'value', 'd', 'seconds')
    const chosen = chooseProperty(wired, 's', 'UserName', nodeCatalog).graph
    expect(dataConnectionError(chosen, 's', 'value', 'd', 'seconds', nodeCatalog)).toBe('Seconds accepts Number, not Text')
    expect(dataConnectionError(chooseProperty(chosen, 's', 'Age', nodeCatalog).graph, 's', 'value', 'd', 'seconds', nodeCatalog)).toBeUndefined()
  })
  it('connects an entity state\'s property', () => {
    const catalog: VisualCatalogSnapshot = { ...nodeCatalog, types: { ...nodeCatalog.types, 'global::HassSharp.EntityStateSnapshot': { ...nodeCatalog.types['global::HassSharp.EntityStateSnapshot'], properties: [{ name: 'State', type: 'string', nullable: false }] } } }
    const g: Graph = { version: 5, name: 'State', nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } }, { id: 'r', kind: 'read_entity', config: { entity: { kind: 'literal', type: 'entity_id', value: 'light.hall' } } }, select('s', 'State'), log()],
      edges: [step('t', 'r'), step('r', 'l'), wire('r', 'value', 's', 'value')] }
    expect(dataConnectionError(g, 's', 'value', 'l', 'value', catalog)).toBeUndefined()
  })
  it('never words an output without a type as an empty one', () => {
    // No property chosen: the validator's own sentence.
    expect(dataConnectionError(live(''), 's', 'value', 'l', 'value', nodeCatalog)).toBe('Choose a property in Select property to use its value')
    // An output of a type the catalog does not know.
    const g = { ...live(), nodes: [...live().nodes, make('odd', 'global::Nope.Gone', {}), { id: 'd', kind: 'delay', config: { seconds: U } }] }
    const message = dataConnectionError(g, 'odd', 'result', 'd', 'seconds', nodeCatalog)!
    expect(message).toBe('Seconds is connected to a value of unknown type')
  })
  it('lists a handful of accepted types, then how many more', () => {
    const many = ['boolean', 'number', 'string', 'object', 'array', 'entity_id']
    expect(words.incompatible('Many', USER, many, withGraphTypes(nodeCatalog, live()))).toBe('Many accepts True/False, Number, Text, Object and 2 more, not User')
    const take: GraphNode = { id: 'take', kind: 'custom', config: { source: 'public static class CustomNode { }', arguments: { many: U },
      signature: { title: 'Take', description: '', pure: false, inputs: { many: { types: many, required: true, label: 'Many', description: '', nullable: false, hasDefaultValue: false } }, outputs: {}, types: {} } } }
    const g = { ...live(), nodes: [...live().nodes, take], edges: [...live().edges, step('l2', 'take')] }
    const message = dataConnectionError(g, 'user', 'result', 'take', 'many', nodeCatalog)!
    expect(message).toContain('accepts True/False, Number, Text, Object and 2 more, not User')
    expect(message.length).toBeLessThan(120)
  })
  /** A real pointer drag on the canvas, from `source`'s output to `target`'s input, as the canvas tests make one. */
  async function drag(g: Graph, source: string, target: string, port: string) {
    vi.spyOn(HTMLElement.prototype, 'offsetWidth', 'get').mockReturnValue(800)
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(570)
    const el = document.createElement('div')
    document.body.append(el)
    // Laid out apart, as a saved graph is.
    const [graph, setGraph] = createSignal<Graph>({ ...g, nodes: g.nodes.map((n, i) => ({ ...n, position: { x: 260 * i, y: 0 } })) })
    dispose = render(() => <FlowAdapter graph={graph()} onSelect={() => {}} onChange={setGraph} />, el)
    await vi.waitFor(() => expect(el.querySelector('.solid-flow__edge')).not.toBeNull())
    const from = el.querySelector<HTMLElement>(`[data-id="${source}"] .source[data-handleid="data:value"]`)!,
      to = el.querySelector<HTMLElement>(`[data-id="${target}"] .target[data-handleid="data:${port}"]`)!
    vi.spyOn(document, 'elementFromPoint').mockReturnValue(to)
    from.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, clientX: 10, clientY: 10 }))
    document.dispatchEvent(new MouseEvent('mousemove', { bubbles: true, clientX: 200, clientY: 200 }))
    document.dispatchEvent(new MouseEvent('mouseup', { bubbles: true, clientX: 200, clientY: 200 }))
    return { el, graph, to }
  }
  it('adds the wire on a real drag from the chosen property into Log', async () => {
    const { graph, to } = await drag(live(), 's', 'l', 'value')
    expect(graph().edges.some(e => e.kind === 'data' && e.source === 's' && e.target === 'l' && e.targetPort === 'value')).toBe(true)
    expect(to.getAttribute('aria-invalid')).not.toBe('true')
  })
  it('refuses a drag that does not fit in the validator\'s words, clamped on the card with the full text as its tip', async () => {
    const g = { ...live(), nodes: [...live().nodes, { id: 'd', kind: 'delay', config: { seconds: U } }], edges: [...live().edges, step('l2', 'd')] }
    const { el, graph, to } = await drag(g, 's', 'd', 'seconds')
    expect(graph().edges.some(e => e.kind === 'data' && e.target === 'd')).toBe(false)
    expect(to.getAttribute('title')).toBe('Seconds accepts Number, not Text')
    // The callout under the pin and the pin's own tip say it; the callout is clamped, the tip and the problems tip are whole.
    expect(el.querySelector('[data-id="d"] .hs-pin-refusal')!.textContent).toBe('Seconds accepts Number, not Text')
    const css = readFileSync('src/styles/flow.css', 'utf8')
    expect(css).toMatch(/\.hs-pin-refusal \{[^}]*-webkit-line-clamp: 3;[^}]*overflow: hidden;/)
    expect(el.querySelector('[data-id="d"] .hs-node-errors')!.textContent).toContain('Seconds accepts Number, not Text')
  })
})
const updateSelect = (g: Graph, property: string): Graph => ({ ...g, nodes: g.nodes.map(n => n.id === 's' ? { ...n, config: { ...n.config, property } } : n) })

describe('connecting a Select property node', () => {
  it('connects a value before a property is chosen, and refuses a wire from its output until then', () => {
    const open = graph('', null, false)
    const connected = connectData(open, 'user', 'result', 's', 'value')
    expect(analyzeGraph(connected).diagnostics.map(d => [d.nodeId, d.inputId, d.code, d.message])).toContainEqual(['s', 'property', 'property.unset', 'Choose a property of User'])
    expect(() => connectData(connected, 's', 'value', 'l', 'value')).toThrow('Choose a property in Select property to use its value')
    expect(connectionIndex(connected).verdict('s', 'value', 'l', 'value').message).toBe('Choose a property in Select property to use its value')
    expect(connectionIndex(open).verdict('user', 'result', 's', 'value')).toEqual({ fallback: false })
  })
  it('refuses a new value whose property no longer fits where the output goes, and the index says the same', () => {
    // Room's Age is text; the Delay's seconds take a number.
    const g = graph('Age')
    expect(() => connectData(g, 'room', 'result', 's', 'value')).toThrow('Seconds accepts Number, not Text')
    expect(connectionIndex(g).verdict('room', 'result', 's', 'value')).toEqual({ message: 'Seconds accepts Number, not Text', fallback: false })
    // A value whose type lacks the chosen property makes it missing on the node; the wire into it is accepted.
    const lacking = graph('UserName', 'user', false)
    expect(connectData(lacking, 'room', 'result', 's', 'value')).toBeTruthy()
    expect(connectionIndex(lacking).verdict('room', 'result', 's', 'value')).toEqual({ fallback: false })
    expect(analyzeGraph(connectData(lacking, 'room', 'result', 's', 'value')).diagnostics.map(d => d.message)).toContain('Room has no property UserName; choose another')
  })
  it('refuses a value whose type has no properties, saying what to use instead', () => {
    const g: Graph = { ...graph('', null, false) }
    g.nodes = [...g.nodes, { id: 'c', kind: 'constant', config: { type: 'object', value: { kind: 'literal', type: 'object', value: { a: 1 } } } }]
    expect(() => connectData(g, 'c', 'value', 's', 'value')).toThrow('Object has no properties to select; use Get field to take a value out of an object')
    expect(connectionIndex(g).verdict('c', 'value', 's', 'value').message).toBe('Object has no properties to select; use Get field to take a value out of an object')
  })
  it('is offered by quick-add for an output whose type has properties, never for an input', () => {
    const g = graph('', null, false)
    expect(canAddFor(g, 'select_property', { nodeId: 'user', handle: 'data:result', type: 'source' }, nodeCatalog)).toBe(true)
    expect(canAddFor(g, 'select_property', { nodeId: 'd', handle: 'data:seconds', type: 'target' }, nodeCatalog)).toBe(false)
    const constant: Graph = { ...g, nodes: [...g.nodes, { id: 'c', kind: 'constant', config: { type: 'string', value: { kind: 'literal', type: 'string', value: 'x' } } }] }
    expect(canAddFor(constant, 'select_property', { nodeId: 'c', handle: 'data:value', type: 'source' }, nodeCatalog)).toBe(false)
  })
})

describe('choosing a property', () => {
  it('is one change that removes the wires it no longer fits, with a notice', () => {
    const g = graph('Age')
    const chosen = chooseProperty(g, 's', 'UserName', nodeCatalog)
    expect(node(chosen.graph, 's').config.property).toBe('UserName')
    expect(chosen.graph.edges.some(e => e.kind === 'data' && e.target === 'd')).toBe(false)
    expect(chosen.notice).toBe('Removed 1 connection that no longer fits: Delay · Seconds.')
    // A wire that still fits stays, with no notice.
    const fits = chooseProperty(graph('UserName', 'user', false), 's', 'Age', nodeCatalog)
    expect(fits.notice).toBeUndefined()
    expect(chooseProperty(g, 's', 'Age', nodeCatalog).graph).toBe(g)
  })
  it('judges a wire into a Loop result by the loop it is in', () => {
    const loop: Graph = {
      version: 5, name: 'Carry',
      nodes: [{ id: 't', kind: 'trigger', config: { type: 'manual' } },
        { id: 'loop', kind: 'repeat', config: { count: { kind: 'literal', type: 'number', value: 3 }, carry: [{ name: 'total', type: 'number', initial: { kind: 'literal', type: 'number', value: 0 } }], exports: [] } },
        make('user', USER), { id: 'r', kind: 'loop_result', config: { values: {} } }, select('s', 'Age')],
      edges: [step('t', 'loop'), step('loop', 'user', 'body'), step('user', 'r'), wire('user', 'result', 's', 'value'), wire('s', 'value', 'r', 'carry:total')],
    }
    expect(analyzeGraph(loop).diagnostics.filter(d => d.severity === 3)).toEqual([])
    const chosen = chooseProperty(loop, 's', 'UserName', nodeCatalog)
    expect(chosen.graph.edges.some(e => e.kind === 'data' && e.target === 'r')).toBe(false)
    expect(chosen.notice).toBe('Removed 1 connection that no longer fits: Loop result · Total.')
    expect(analyzeGraph(chosen.graph).diagnostics.filter(d => d.severity === 3)).toEqual([])
  })
})

describe('the property choice in the panel', () => {
  const panel = (g: Graph, onGraphChange: (next: Graph) => void = () => {}) => {
    const el = document.createElement('div')
    document.body.append(el)
    dispose = render(() => <NodeConfig graph={g} node={node(g, 's')} catalog={nodeCatalog} diagnostics={analyzeGraph(g).diagnostics} onChange={() => {}} onGraphChange={onGraphChange} />, el)
    return el.querySelector<HTMLElement>('[data-input-id="property"]')!
  }
  it('is disabled until a value is connected', () => {
    const field = panel(graph('', null, false))
    const choice = field.querySelector('select')!
    expect(choice.disabled).toBe(true)
    expect(choice.selectedOptions[0].textContent).toBe('Connect a value to choose a property')
    expect(field.querySelector('label')!.textContent).toBe('Property')
  })
  it('lists the properties with their types and chooses one as one graph change', () => {
    const changes: Graph[] = []
    const field = panel(graph(''), next => changes.push(next))
    const choice = field.querySelector('select')!
    expect(choice.disabled).toBe(false)
    expect([...choice.options].map(o => o.textContent)).toEqual(['Choose a property', 'UserName · Text (string)', 'Age · Number (double)'])
    choice.value = 'Age'
    choice.dispatchEvent(new Event('change', { bubbles: true }))
    expect(changes).toHaveLength(1)
    expect(node(changes[0], 's').config.property).toBe('Age')
  })
  it('shows the chosen property type and its description, and a stored one the type lacks as missing', () => {
    const chosen = panel(graph('UserName', 'user', false))
    expect(chosen.querySelector('.hs-select-property__type')!.textContent).toBe('Text, C# type stringWhat the person is called.')
    dispose?.(); document.body.innerHTML = ''
    const missing = panel(graph('Level'))
    const choice = missing.querySelector('select')!
    expect(choice.selectedOptions[0].textContent).toBe('Level · missing')
    expect(choice.getAttribute('aria-invalid')).toBe('true')
    expect(missing.textContent).toContain('User has no property Level; choose another')
  })
  it('offers no value to type into Value, and refuses one stored there', () => {
    const open = graph('', null, false)
    const el = document.createElement('div')
    document.body.append(el)
    dispose = render(() => <NodeConfig graph={open} node={node(open, 's')} catalog={nodeCatalog} diagnostics={analyzeGraph(open).diagnostics} onChange={() => {}} />, el)
    const value = el.querySelector<HTMLElement>('[data-input-id="value"]')!
    expect(value.textContent).toContain('Connect an output whose value has properties, such as a record from a node library.')
    expect(value.querySelector('input, textarea, select')).toBeNull()
    for (const typed of [{ kind: 'literal', type: `${USER}?`, value: null }, { kind: 'literal', type: 'string', value: 'x' }]) {
      const stored = { ...open, nodes: open.nodes.map(n => n.id === 's' ? { ...n, config: { property: 'UserName', value: typed } } : n) }
      expect(analyzeGraph(stored).diagnostics.filter(d => d.nodeId === 's' && d.inputId).map(d => [d.inputId, d.message])).toEqual([['value', 'Connect a value to Select property; nothing can be typed in here']])
    }
  })
  it('is searched when there are more than 12 properties', () => {
    const many = Array.from({ length: 13 }, (_, i) => ({ name: `P${i}`, type: 'string', nullable: false }))
    const g = graph('')
    const custom = node(g, 'user').config.signature as { types: Record<string, TypeSpec> }
    custom.types = { ...custom.types, [USER]: entry('User', many) }
    const field = panel(g)
    expect(field.querySelector('select')).toBeNull()
    expect(field.querySelector('[role="combobox"]')!.getAttribute('aria-label')).toBe('Property')
  })
})

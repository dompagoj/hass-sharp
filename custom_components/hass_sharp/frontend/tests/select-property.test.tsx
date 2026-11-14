import { afterEach, describe, expect, it } from 'vitest'
import { render } from 'solid-js/web'
import { analyzeGraph, connectData, dataOutputs, nodeCatalog, withGraphTypes, type Graph, type GraphNode, type TypeSpec } from '../src/visual/contract'
import { connectionIndex, graphTypes } from '../src/visual/connectionIndex'
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
const make = (id: string, type: string): GraphNode => ({
  id, kind: 'custom', config: {
    source: 'public static class CustomNode { }', arguments: {},
    signature: { title: id, description: '', pure: false, inputs: {}, outputs: { result: { type, label: 'Result', description: '' } }, types },
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
afterEach(() => { dispose?.(); document.body.innerHTML = '' })

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

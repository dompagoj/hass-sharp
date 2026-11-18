import { expect, it } from 'vitest'
import { addGraphNode, connectData, dataInputs, dataOutputs, nodeCatalog, validateGraph, withGraphTypes, type Graph } from '../src/visual/contract'
import { addNode, canConnect, connectNodes, createGraph, updateNode, type VisualGraph } from '../src/visual/graph'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { addConnected, canAddFor } from '../src/visual/flow/autoConnect'
import { tidyLayout } from '../src/visual/layout'

// The roots of an entity definition's graph start a flow as a trigger does: When commanded and When started run the
// steps after them, State is worked out from its value inputs and has no execution pins at all.
const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const entityGraph = (): VisualGraph => ({
  version: 5, name: 'Relay', nodes: [], edges: [],
  definition: { entities: [{ key: 'relay', domain: 'switch' }], settings: [] },
})
const add = (graph: Graph, kind: string, id: string, config: Record<string, unknown> = {}, position?: { x: number; y: number }) => {
  const added = addGraphNode(graph, kind, id, nodeCatalog)
  const node = added.nodes.at(-1)!
  return updateNode(added, id, { config: { ...node.config, ...config }, ...(position ? { position } : {}) })
}
/** When commanded (turn_on) → Read entity, When started, State of the relay fed by Has state, and a loose Log. */
const roots = () => {
  let graph = add(entityGraph(), 'entity_command', 'cmd', { entity: 'relay', command: 'turn_on' })
  graph = add(graph, 'read_entity', 'read', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'entity_setup', 'setup')
  graph = add(graph, 'entity_state', 'state', { entity: 'relay' })
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'log', 'log')
  graph = connectNodes(graph, 'cmd', 'read')
  return connectData(graph, 'is', 'value', 'state', 'is_on')
}

it('no step order wire can be drawn into a root of an entity graph', () => {
  // A second When commanded that runs nothing: a wire from Read entity into it would make no cycle, so only the root
  // guard refuses it (one into the first, which runs Read entity, would also be a cycle).
  const graph = add(roots(), 'entity_command', 'idle', { entity: 'relay', command: 'turn_off' })
  expect(graph.edges.some(e => e.kind !== 'data' && e.source === 'cmd' && e.target === 'read')).toBe(true)
  expect(graph.edges.some(e => e.source === 'idle' || e.target === 'idle')).toBe(false)
  for (const target of ['idle', 'setup', 'state']) {
    expect(canConnect(graph, 'read', target), target).toBe(false)
    expect(connectNodes(graph, 'read', target), target).toBe(graph)
  }
  // State has no execution output either; the steps that start flows still run what comes after them.
  expect(canConnect(graph, 'state', 'log')).toBe(false)
  expect(canConnect(graph, 'setup', 'log')).toBe(true)
  expect(canConnect(graph, 'read', 'log')).toBe(true)
})

it('the roots of an entity graph have no execution input pin, and State no execution pin at all', () => {
  const graph = roots()
  const view = (id: string) => buildNodeView(graph.nodes.find(n => n.id === id)!, { graph, catalog: nodeCatalog, diagnostics: [] })
  for (const id of ['cmd', 'setup', 'state']) {
    expect(view(id).execIn, id).toBeUndefined()
    expect(view(id).unreachable, id).toBe(false)
  }
  expect(view('cmd').next?.id).toBe('next')
  expect(view('setup').next?.id).toBe('next')
  expect(view('state').next).toBeUndefined()
  expect(view('state').footer).toBeUndefined()
  expect(view('state').rows.flatMap(r => (r.input ? [r.input.port] : []))).toEqual(['is_on'])
  // A step after a root runs; a step no root reaches does not.
  expect(view('read').execIn?.id).toBe('in')
  expect(view('read').unreachable).toBe(false)
  expect(view('log').unreachable).toBe(true)
})

it('auto-connect never wires into a root of an entity graph', () => {
  const graph = roots()
  const fromNext = { nodeId: 'read', handle: 'next', type: 'source' as const }
  for (const kind of ['entity_command', 'entity_setup', 'entity_state', 'trigger']) expect(canAddFor(graph, kind, fromNext, nodeCatalog), kind).toBe(false)
  expect(canAddFor(graph, 'log', fromNext, nodeCatalog)).toBe(true)
  // A new step's value never feeds a new root.
  const fromValue = { nodeId: 'read', handle: 'data:value', type: 'source' as const }
  for (const kind of ['entity_command', 'entity_setup', 'entity_state']) expect(canAddFor(graph, kind, fromValue, nodeCatalog), kind).toBe(false)
})

it('auto-connect treats the steps after a When commanded as running', () => {
  const result = addConnected(roots(), 'log', { x: 600, y: 0 }, { nodeId: 'read', handle: 'data:value', type: 'source' }, nodeCatalog, 'added')!
  expect(result).toBeDefined()
  // Put into the flow after Read entity, not left beside it.
  expect(result.graph.edges.some(e => e.kind !== 'data' && e.source === 'read' && e.target === 'added')).toBe(true)
  expect(result.graph.edges.some(e => e.kind === 'data' && e.source === 'read' && e.target === 'added')).toBe(true)
  expect(result.notice ?? '').not.toContain('into the flow')
  expect(result.error).toBeUndefined()
})

it('layout puts the bands of an entity graph\'s roots first, each led by its root', () => {
  // Steps no root runs (Delay → Stop) sit highest; When commanded is below the Log it runs, When started in between.
  let graph = add(entityGraph(), 'delay', 'wait', {}, { x: 0, y: 0 })
  graph = add(graph, 'stop', 'halt', {}, { x: 300, y: 0 })
  graph = add(graph, 'entity_command', 'cmd', { entity: 'relay', command: 'turn_on' }, { x: 0, y: 900 })
  graph = add(graph, 'log', 'log', {}, { x: 300, y: 100 })
  graph = add(graph, 'entity_setup', 'setup', {}, { x: 0, y: 500 })
  graph = add(graph, 'delay', 'later', {}, { x: 300, y: 500 })
  graph = connectNodes(connectNodes(connectNodes(graph, 'wait', 'halt'), 'cmd', 'log'), 'setup', 'later')
  const positions = tidyLayout(graph, nodeCatalog)
  const y = (id: string) => positions.get(id)!.y
  // Bands with a root come before the one without; among them, the root higher on the canvas first.
  expect(y('setup')).toBeLessThan(y('cmd'))
  expect(y('cmd')).toBeLessThan(y('wait'))
  // A root heads its flow: the steps it runs are right of it.
  expect(positions.get('cmd')!.x).toBeLessThan(positions.get('log')!.x)
  expect(positions.get('setup')!.x).toBeLessThan(positions.get('later')!.x)
})

it('a State band counts as a rooted flow in the layout', () => {
  let graph = add(entityGraph(), 'delay', 'wait', {}, { x: 0, y: 0 })
  graph = add(graph, 'stop', 'halt', {}, { x: 300, y: 0 })
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') }, { x: 0, y: 600 })
  graph = add(graph, 'entity_state', 'state', { entity: 'relay' }, { x: 300, y: 600 })
  graph = connectData(connectNodes(graph, 'wait', 'halt'), 'is', 'value', 'state', 'is_on')
  const positions = tidyLayout(graph, nodeCatalog)
  expect(positions.get('state')!.y).toBeLessThan(positions.get('wait')!.y)
  // State reads its value: it sits right of what feeds it.
  expect(positions.get('is')!.x).toBeLessThan(positions.get('state')!.x)
})

it('an automation keeps its trigger-only roots on the canvas', () => {
  const graph = addNode(createGraph('Hall'), 'log', 'log')
  expect(canConnect(graph, 'service-1', 'trigger-1')).toBe(false)
  expect(canConnect(graph, 'service-1', 'log')).toBe(true)
  const trigger = buildNodeView(graph.nodes[0], { graph, catalog: nodeCatalog, diagnostics: [] })
  expect(trigger.execIn).toBeUndefined()
  expect(buildNodeView(graph.nodes.find(n => n.id === 'log')!, { graph, catalog: nodeCatalog, diagnostics: [] }).unreachable).toBe(true)
})

it('a new When commanded names the first command no other When commanded of the entity handles', () => {
  // The switch's Turn on is handled, so the next When commanded is for Turn off, and nothing is handled twice.
  const second = addGraphNode(add(entityGraph(), 'entity_command', 'on'), 'entity_command', 'off', nodeCatalog)
  expect(second.nodes.map(n => n.config)).toMatchObject([{ entity: 'relay', command: 'turn_on' }, { entity: 'relay', command: 'turn_off' }])
  expect(validateGraph(second, nodeCatalog).filter(d => d.code === 'command.duplicate')).toEqual([])
  // Once every command is handled, a new one names the first again, as before.
  const third = addGraphNode(second, 'entity_command', 'again', nodeCatalog)
  expect(third.nodes.at(-1)!.config).toMatchObject({ entity: 'relay', command: 'turn_on' })
})

/** A light that can be dimmed, and a cover, of one definition. */
const lampGraph = (): VisualGraph => ({
  version: 5, name: 'Lamp', nodes: [], edges: [],
  definition: { entities: [{ key: 'lamp', domain: 'light', options: { dimmable: true } }, { key: 'blind', domain: 'cover' }], settings: [] },
})

it('When commanded has an output for each value of its command\'s data, typed and named as its row has it', () => {
  let graph = add(lampGraph(), 'entity_command', 'on', { entity: 'lamp', command: 'turn_on' })
  graph = add(graph, 'entity_command', 'off', { entity: 'lamp', command: 'turn_off' })
  graph = add(graph, 'entity_command', 'move', { entity: 'blind', command: 'set_position' })
  const catalog = withGraphTypes(nodeCatalog, graph)
  const node = (id: string) => graph.nodes.find(n => n.id === id)!
  expect(dataOutputs(node('on'), catalog)).toEqual({ brightness: { type: 'number', label: 'Brightness', pointer: '/data/brightness' } })
  expect(dataOutputs(node('off'), catalog)).toEqual({})
  expect(dataOutputs(node('move'), catalog)).toEqual({ position: { type: 'number', label: 'Position', pointer: '/data/position' } })
  // Without the definition (or for a command its entity does not have) there is none.
  expect(dataOutputs(node('on'), nodeCatalog)).toEqual({})
  expect(dataOutputs({ ...node('on'), config: { entity: 'lamp', command: 'toggle' } }, catalog)).toEqual({})
  // The canvas shows it as a value pin, and a wire from it into a number input is taken.
  const view = buildNodeView(node('on'), { graph, catalog: nodeCatalog, diagnostics: [] })
  expect(view.rows.flatMap(r => (r.output ? [r.output.label] : []))).toEqual(['Brightness'])
  graph = add(graph, 'set_entity_state', 'dim', { entity: 'lamp' })
  graph = connectData(connectNodes(graph, 'on', 'dim'), 'on', 'brightness', 'dim', 'brightness')
  expect(graph.edges.some(e => e.kind === 'data' && e.source === 'on' && e.sourcePort === 'brightness' && e.target === 'dim')).toBe(true)
  expect(() => connectData(graph, 'on', 'brightness', 'dim', 'is_on')).toThrow('On accepts True/False, not Number')
})

it('a State needs only the values its row requires, and a Set state none, but each at least one', () => {
  let graph = add(lampGraph(), 'entity_state', 'lit', { entity: 'lamp' })
  graph = add(graph, 'entity_state', 'shut', { entity: 'blind' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp' })
  const catalog = withGraphTypes(nodeCatalog, graph)
  const required = (id: string) => Object.fromEntries(Object.entries(dataInputs(graph.nodes.find(n => n.id === id)!, undefined, catalog)).map(([port, input]) => [port, input.required]))
  expect(required('lit')).toEqual({ is_on: true, brightness: false })
  expect(required('shut')).toEqual({ is_closed: false, position: false })
  expect(required('set')).toEqual({ is_on: false, brightness: false })
  // Errors on the three steps, a Set state's not being in a flow aside.
  const problems = (g: Graph) => validateGraph(g, nodeCatalog).filter(d => d.severity === 3 && d.code !== 'node.unreachable' && ['lit', 'shut', 'set'].includes(d.nodeId ?? '')).map(d => `${d.nodeId}: ${d.message}`).sort()
  expect(problems(graph)).toEqual(['lit: On is required', 'set: Set state needs at least one value', 'shut: State needs at least one value'])
  graph = updateNode(graph, 'lit', { config: { entity: 'lamp', is_on: lit('boolean', true) } })
  graph = updateNode(graph, 'shut', { config: { entity: 'blind', position: lit('number', 40) } })
  graph = updateNode(graph, 'set', { config: { entity: 'lamp', brightness: lit('number', 128) } })
  expect(problems(graph)).toEqual([])
})

it('a State or Set state of a button, which has no state, is refused', () => {
  let graph: Graph = { ...entityGraph(), definition: { entities: [{ key: 'bell', domain: 'button' }], settings: [] } }
  graph = add(graph, 'entity_state', 'state', { entity: 'bell' })
  graph = add(graph, 'entity_command', 'press', { entity: 'bell', command: 'press' })
  graph = connectNodes(add(graph, 'set_entity_state', 'set', { entity: 'bell' }), 'press', 'set')
  const stateless = validateGraph(graph, nodeCatalog).filter(d => d.code === 'entity.stateless')
  expect(stateless).toEqual([
    { nodeId: 'state', message: 'A button has no state to set', severity: 3, inputId: 'entity', code: 'entity.stateless' },
    { nodeId: 'set', message: 'A button has no state to set', severity: 3, inputId: 'entity', code: 'entity.stateless' },
  ])
  expect(Object.keys(dataOutputs(graph.nodes.find(n => n.id === 'press')!, withGraphTypes(nodeCatalog, graph)))).toEqual([])
})

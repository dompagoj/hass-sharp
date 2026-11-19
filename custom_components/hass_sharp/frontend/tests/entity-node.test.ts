import { expect, it } from 'vitest'
import { addGraphNode, connectData, dataInputs, dataOutputs, declaredEntities, definitionCatalog, entityNodeName, executionLabel, executionPorts, flowMethod, nodeCatalog, setInput, validateGraph, withGraphTypes, words, type Graph } from '../src/visual/contract'
import { definitionNames, runName } from '../src/components/TriggerSummary'
import { connectNodes, dataConnectionError, updateNode, type VisualGraph } from '../src/visual/graph'
import { buildNodeView } from '../src/visual/flow/flowModel'
import { editorGraph } from '../src/entities/model'

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const empty = (entities: Record<string, unknown>[] = []): VisualGraph => ({ version: 5, name: 'Nodes', nodes: [], edges: [], definition: { entities, settings: [] } as never })
const add = (graph: Graph, kind: string, id: string, config: Record<string, unknown> = {}) => {
  const added = addGraphNode(graph, kind, id, nodeCatalog)
  return updateNode(added, id, { config: { ...added.nodes.at(-1)!.config, ...config } })
}

it('an Entity node declares an entity', () => {
  let graph = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'entity', 'b', { key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  const declared = declaredEntities(graph, nodeCatalog)
  expect(declared.map(d => [d.nodeId, d.entity.key])).toEqual([['a', 'door'], ['b', 'power']])
  expect(declared[1].entity).toMatchObject({ key: 'power', domain: 'sensor', name: 'Power', options: { unit: 'W' } })
  expect(declared[1].entity).not.toHaveProperty('history')
  expect(declared[1].entity).not.toHaveProperty('arguments')
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => e.key)).toEqual(['door', 'power'])
  expect(validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
})

it('a node replaces a listed entity of its key, and a definition without a list is one', () => {
  const graph = add(empty([{ key: 'old', domain: 'switch' }, { key: 'door', domain: 'switch' }]), 'entity', 'a', { key: 'door', domain: 'binary_sensor' })
  expect(withGraphTypes(nodeCatalog, graph).definition!.entities.map(e => `${e.key}:${e.domain}`)).toEqual(['old:switch', 'door:binary_sensor'])
  const bare = { ...graph, definition: { settings: [] } }
  expect(definitionCatalog(nodeCatalog, bare).definition!.entities.map(e => e.key)).toEqual(['door'])
  expect(validateGraph(bare).some(d => d.code === 'definition.invalid')).toBe(false)
})

it('a new Set state step chooses a node\'s entity', () => {
  const graph = add(add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }), 'set_entity_state', 'set')
  expect(graph.nodes.at(-1)!.config.entity).toBe('door')
})

it('the editor does not list an entity that a node declares', () => {
  const node = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor' }).nodes[0]
  const body = { name: 'Nodes', device: null, settings: [], entities: [{ key: 'door', domain: 'binary_sensor' }, { key: 'old', domain: 'switch' }], logic: { kind: 'graph', graph: { version: 5, name: 'Nodes', nodes: [node], edges: [] } } }
  expect((editorGraph(body as never).definition as never as { entities: { key: string }[] }).entities.map(e => e.key)).toEqual(['old'])
})

it('a new Entity node does not take the key of the definition\'s one setting', () => {
  const graph: VisualGraph = { version: 5, name: 'Nodes', nodes: [], edges: [], definition: { entities: [], settings: [{ key: 'delay', type: 'number' }] } as never }
  const node = addGraphNode(graph, 'entity', 'a', nodeCatalog).nodes[0]
  expect(node.config.key).toBeUndefined()
  expect(Object.values(node.config)).not.toContain('delay')
})

/** The labels of a node's right column, top to bottom: each execution pin with the values under it. */
const rightColumn = (graph: VisualGraph, id: string) =>
  buildNodeView(graph.nodes.find(n => n.id === id)!, { graph, catalog: nodeCatalog, diagnostics: [] }).rows.flatMap(r => (r.output ? [r.output.label] : []))

it('the node has the pins of its type', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' }).nodes[0]
  expect(Object.keys(dataInputs(light))).toEqual(['is_on', 'brightness'])
  expect(Object.values(dataInputs(light)).every(input => input.required === false)).toBe(true)
  expect(executionPorts(light)).toEqual(['command:turn_on', 'command:turn_off'])
  expect(executionLabel(light, 'command:turn_on')).toBe('Turn on')
  expect(dataOutputs(light)['command:turn_on:brightness']).toMatchObject({ pointer: '/data/brightness', group: 'command:turn_on' })
  const camera = add(empty(), 'entity', 'c', { key: 'cam', domain: 'camera' }).nodes[0]
  expect(executionPorts(camera)).toEqual(['frame'])
  expect(executionLabel(camera, 'frame')).toBe('When a frame is needed')
  expect(Object.keys(dataInputs({ ...light, config: { ...light.config, key: '' } }))).toEqual(['is_on', 'brightness'])
})

it('a value set on the node is kept under arguments, and one can be connected', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'entity_is', 'is', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = connectData(graph, 'is', 'value', 'l', 'is_on')
  expect(graph.edges.some(e => e.kind === 'data' && e.target === 'l' && e.targetPort === 'is_on')).toBe(true)
  // setInput changes the node it is given, as InputRow does on a copy.
  const set = structuredClone(graph.nodes[0])
  setInput(set, 'brightness', lit('number', 128), nodeCatalog)
  // The connected input is cleared under arguments too, as connectData clears any input it wires.
  expect(set.config.arguments).toEqual({ is_on: { kind: 'unset' }, brightness: lit('number', 128) })
  expect(set.config).not.toHaveProperty('brightness')
})

it('a step can be connected to a command, and the card lists each command with its values under it', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light' })
  graph = add(graph, 'log', 'log')
  graph = connectNodes(graph, 'l', 'log', 'command:turn_on')
  // A step order wire names its execution pin as port (sourcePort is a value wire's).
  expect(graph.edges.some(e => e.kind !== 'data' && e.source === 'l' && e.target === 'log' && e.port === 'command:turn_on')).toBe(true)
  expect(rightColumn(graph, 'l')).toEqual(['Turn on', 'Brightness', 'Turn off'])
})

it('a flow is named by node and port, as the server names it', () => {
  const light = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' }).nodes[0]
  expect(flowMethod(light, 'state')).toBe('Run_6C_7374617465')
  // VisualNodeCatalog.FlowMethod: the node's method, then the hex of the port's UTF-8.
  expect(flowMethod(light, 'command:turn_on')).toBe('Run_6C_636F6D6D616E643A7475726E5F6F6E')
})

it('a run of a node\'s flow reads as the entity and the flow', () => {
  const graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const names = definitionNames(graph, nodeCatalog)!
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_on'), names)).toBe('Lamp: Turn on')
  expect(runName(flowMethod(graph.nodes[0], 'command:turn_off'), names)).toBe('Lamp: Turn off')
  expect(runName(flowMethod(graph.nodes[0], 'state'), names)).toBe('Lamp: State')
  expect(names.entities!.map(e => e.key)).toEqual(['lamp'])
  // A camera's frame flow; a nameless entity by its key, and one with neither as Entity.
  let cameras = add(empty(), 'entity', 'c', { key: 'door_cam', domain: 'camera', name: 'Front door' })
  cameras = add(cameras, 'entity', 'k', { key: 'yard', domain: 'camera', name: '' })
  cameras = add(cameras, 'entity', 'n', { key: '', domain: 'switch', name: '' })
  const named = definitionNames(cameras, nodeCatalog)!
  expect(runName(flowMethod(cameras.nodes[0], 'frame'), named)).toBe('Front door: When a frame is needed')
  expect(runName(flowMethod(cameras.nodes[1], 'state'), named)).toBe('yard: State')
  expect(runName(flowMethod(cameras.nodes[2], 'command:turn_on'), named)).toBe('Entity: Turn on')
  // A port the node does not have is no run of it, and keeps its recorded name.
  expect(runName(flowMethod(graph.nodes[0], 'frame'), names)).toBe(flowMethod(graph.nodes[0], 'frame'))
})

it('a light\'s Brightness can be connected before On, and the node then asks for On', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'constant', 'c', { type: 'number', value: lit('number', 128) })
  const wired = connectData(graph, 'c', 'value', 'l', 'brightness')
  expect(wired).not.toBe(graph)
  expect(validateGraph(wired).filter(d => d.severity === 3)).toMatchObject([{ nodeId: 'l', inputId: 'is_on', code: 'entity.partialState', message: 'Lamp has a value for Brightness but none for On: connect or set On too' }])
  // A hover says what the drop says.
  expect(dataConnectionError(graph, 'c', 'value', 'l', 'brightness', nodeCatalog, { quick: true })).toBeUndefined()
  // With On connected too, the node is whole again.
  const whole = connectData(add(wired, 'constant', 'on', { type: 'boolean', value: lit('boolean', true) }), 'on', 'value', 'l', 'is_on')
  expect(validateGraph(whole).filter(d => d.severity === 3)).toEqual([])
})

it('the editor refuses a wire that reads a command\'s value from a step another command also runs', () => {
  let graph = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  graph = add(graph, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) })
  graph = connectNodes(connectNodes(graph, 'l', 'set', 'command:turn_on'), 'l', 'set', 'command:turn_off')
  expect(graph.edges.flatMap(e => e.kind === 'data' ? [] : [e.port])).toEqual(['command:turn_on', 'command:turn_off'])
  const refusal = 'Set state reads Brightness from Turn on of Lamp but also runs from Turn off of Lamp, where there is no Brightness'
  // connectData refuses by throwing what is wrong, as every refused connection does; the hover says the same.
  expect(() => connectData(graph, 'l', 'command:turn_on:brightness', 'set', 'brightness')).toThrow(refusal)
  expect(dataConnectionError(graph, 'l', 'command:turn_on:brightness', 'set', 'brightness', nodeCatalog, { quick: true })).toBe(refusal)
  const one = add(empty(), 'entity', 'l', { key: 'lamp', domain: 'light', name: 'Lamp' })
  const alone = connectNodes(add(one, 'set_entity_state', 'set', { entity: 'lamp', is_on: lit('boolean', true) }), 'l', 'set', 'command:turn_on')
  const read = connectData(alone, 'l', 'command:turn_on:brightness', 'set', 'brightness')
  expect(read).not.toBe(alone)
  expect(validateGraph(read).filter(d => d.severity === 3)).toEqual([])
  expect(dataConnectionError(alone, 'l', 'command:turn_on:brightness', 'set', 'brightness', nodeCatalog, { quick: true })).toBeUndefined()
})

it('a state value of the node cannot be connected to a step, and a hover says so', () => {
  let graph = add(empty(), 'entity', 'd', { key: 'door', domain: 'binary_sensor', name: 'Door' })
  graph = add(graph, 'trigger', 't', { type: 'manual' })
  graph = add(graph, 'read_entity', 'read', { entity: lit('entity_id', 'binary_sensor.motion') })
  graph = add(graph, 'convert', 'c', { type: 'boolean' })
  graph = connectData(connectNodes(graph, 't', 'read'), 'read', 'value', 'c', 'value')
  const refusal = 'On of Door cannot read Read entity, which runs in a flow: use a Set state step after Read entity instead'
  expect(() => connectData(graph, 'c', 'value', 'd', 'is_on')).toThrow(refusal)
  expect(dataConnectionError(graph, 'c', 'value', 'd', 'is_on', nodeCatalog, { quick: true })).toBe(refusal)
})

it('a declaring node is named by its entity in messages and run names', () => {
  const graph = add(empty(), 'entity', 'a', { key: 'door', domain: 'binary_sensor', name: '  Front door ' })
  const named = graph.nodes[0]
  expect(words.stepName(named)).toBe('Front door')
  expect(entityNodeName(named)).toBe('Front door')
  expect(words.stepName({ ...named, config: { ...named.config, name: ' ' } })).toBe('door')
  // Its own label is not its name: a node is named by its entity, else its kind.
  expect(words.stepName({ ...named, label: 'Something else', config: { ...named.config, name: '', key: '' } })).toBe('Entity')
  // Any other step keeps its label, and declares no entity.
  expect(entityNodeName({ id: 'l', kind: 'log', label: 'Log', config: {} })).toBeUndefined()
  expect(runName(flowMethod(named, 'state'), definitionNames(graph, nodeCatalog))).toBe('Front door: State')
})

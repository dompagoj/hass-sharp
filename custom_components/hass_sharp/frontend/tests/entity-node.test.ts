import { expect, it } from 'vitest'
import { addGraphNode, connectData, dataInputs, dataOutputs, declaredEntities, definitionCatalog, executionLabel, executionPorts, nodeCatalog, setInput, validateGraph, withGraphTypes, type Graph } from '../src/visual/contract'
import { connectNodes, updateNode, type VisualGraph } from '../src/visual/graph'
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

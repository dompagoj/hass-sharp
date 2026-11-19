import { expect, it } from 'vitest'
import { addGraphNode, declaredEntities, definitionCatalog, nodeCatalog, validateGraph, withGraphTypes, type Graph } from '../src/visual/contract'
import { updateNode, type VisualGraph } from '../src/visual/graph'
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

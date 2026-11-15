import { expect, it } from 'vitest'
import richFixture from '../../../../tests/fixtures/visual-rich-catalog.json'
import { entityStateContext } from '../src/visual/entityStateContext'
import { dataOutputs, type Graph, type GraphNode, type VisualCatalogSnapshot } from '../src/visual/contract'
import { graphTypes } from '../src/visual/connectionIndex'
import { viewKey } from '../src/visual/flow/flowModel'
import { stateField, withStates } from '../src/visual/widgets'

const catalog = richFixture.catalog as unknown as VisualCatalogSnapshot
const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
const condition: GraphNode = { id: 'check', kind: 'condition', config: { entity: lit('entity_id', 'climate.stale'), state: 'off' } }
const graphFor = (source: GraphNode, port = 'result'): Graph => ({ version: 5, name: 'Entity states', nodes: [source, condition], edges: [
  { id: 'wire', kind: 'data', source: source.id, sourcePort: port, target: condition.id, targetPort: 'entity' },
] })
const context = (graph: Graph, types = catalog) => entityStateContext(graph, condition, 'entity', types)
const custom = (type: string, types = {}): GraphNode => ({ id: 'code', kind: 'custom', config: {
  source: '', arguments: {}, signature: { title: 'Read entity', description: '', pure: true, inputs: {}, outputs: { result: { type, label: 'Result' } }, types },
} })
const values = (graph: Graph) => context(graph).fallbackStateOptions.map(o => o.value)

it('recovers exact trigger IDs through refineFrom metadata, including multi-domain unions', () => {
  const trigger: GraphNode = { id: 'trigger', kind: 'trigger', config: { type: 'ha', trigger: { trigger: 'state', entity_id: ['climate.den', 'select.scene', 'climate.den'] } } }
  const graph = graphFor(trigger, 'entity_id')
  const result = context(graph)
  expect(result.entityIds).toEqual(['climate.den', 'select.scene'])
  expect(withStates({ ...stateField, fallbackStateOptions: result.fallbackStateOptions }, result.entityIds, [
    { entity_id: 'select.scene', state_options: ['movie_night', 'reading'] }, { entity_id: 'climate.den', state_options: ['off', 'heat', 'reading'] },
  ]).options).toEqual([
    { value: 'movie_night', label: 'Movie night' }, { value: 'reading', label: 'Reading' }, { value: 'off', label: 'Off' }, { value: 'heat', label: 'Heat' },
  ])
})

it('recovers an entity Constant using its value input without following obsolete wires into that value', () => {
  const graph = graphFor({ id: 'constant', kind: 'constant', config: { type: 'entity_id:select', value: lit('entity_id:select', 'select.scene') } }, 'value')
  graph.edges.push({ id: 'obsolete', kind: 'data', source: 'missing', sourcePort: 'value', target: 'constant', targetPort: 'value' })
  expect(context(graph).entityIds).toEqual(['select.scene'])
})

it('reads custom C# entity outputs from the catalog State enum, preserving its raw strings and labels', () => {
  const graph = graphFor(custom('entity_id:climate'))
  expect(context(graph).entityIds).toEqual([])
  expect(context(graph).fallbackStateOptions).toContainEqual({ value: 'heat_cool', label: 'Heat/Cool' })
  expect(values(graph)).toEqual(['auto', 'cool', 'dry', 'fan_only', 'heat', 'heat_cool', 'off', 'unknown', 'unavailable'])
})

it('resolves entity-valued Select property outputs through graph custom types', () => {
  const graph = graphFor(custom('global::NodeLib.Room', {
    'global::NodeLib.Room': { description: 'Room', properties: [{ name: 'Motion', type: 'entity_id:binary_sensor', nullable: false }] },
  }))
  graph.nodes.push({ id: 'property', kind: 'select_property', config: { property: 'Motion' } })
  graph.edges = [
    { id: 'room', kind: 'data', source: 'code', sourcePort: 'result', target: 'property', targetPort: 'value' },
    { id: 'motion', kind: 'data', source: 'property', sourcePort: 'value', target: 'check', targetPort: 'entity' },
  ]
  expect(dataOutputs(graph.nodes[2], graphTypes(graph, catalog)).value.type).toBe('entity_id:binary_sensor')
  expect(values(graph)).toEqual(['off', 'on', 'unknown', 'unavailable'])
})

it.each(['missing node', 'missing port', 'unknown type', 'string State', 'nonentity State'])('leaves text entry for %s and never uses a wired input\'s stale literal', kind => {
  let graph = graphFor(custom('entity_id:sensor'))
  if (kind === 'missing node') graph.nodes = [condition]
  if (kind === 'missing port') graph = graphFor(custom('entity_id:binary_sensor'), 'gone')
  if (kind === 'unknown type') graph = graphFor(custom('global::Missing.Entity'))
  if (kind === 'nonentity State') graph = graphFor(custom('global::NodeLib.Impostor', {
    'global::NodeLib.Impostor': { description: '', properties: [{ name: 'State', type: 'global::HassSharp.Entities.BinarySensorState', nullable: false }] },
  }))
  expect(context(graph)).toEqual({ entityIds: [], fallbackStateOptions: [] })
})

it('restores a remaining literal only after the wire is removed', () => {
  const graph = graphFor(custom('entity_id:binary_sensor'))
  expect(context(graph).entityIds).toEqual([])
  expect(context({ ...graph, edges: [] }).entityIds).toEqual(['climate.stale'])
})

it.each([
  lit('entity_id:climate', 'select.scene'),
  lit('entity_id', ['climate.den']),
  lit('entity_id', 'not-an-entity'),
])('does not infer exact entities from an invalid Constant operand %j', operand => {
  const graph = graphFor({ id: 'constant', kind: 'constant', config: { type: 'entity_id', value: operand } }, 'value')
  expect(context(graph).entityIds).toEqual([])
})

it('invalidates a condition view when its upstream entity changes within the same domain', () => {
  const source: GraphNode = { id: 'trigger', kind: 'trigger', config: { type: 'entity', entity: 'climate.hall' } }
  const graph = graphFor(source, 'entity_id')
  const next = { ...graph, nodes: [{ ...source, config: { type: 'entity', entity: 'climate.den' } }, condition] }
  expect(viewKey(condition, { graph, catalog, diagnostics: [] })).not.toBe(viewKey(condition, { graph: next, catalog, diagnostics: [] }))
})

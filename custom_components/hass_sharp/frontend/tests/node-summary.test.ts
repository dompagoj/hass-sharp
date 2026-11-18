import { describe, expect, it } from 'vitest'
import { nodeCatalog, withGraphTypes, type Graph, type GraphNode } from '../src/visual/contract'
import { nodeSummary, stepSummary } from '../src/visual/nodeSummary'

const lit = (type: string, value: unknown) => ({ kind: 'literal' as const, type, value })
const graph: Graph = {
  version: 5, name: 'Plug', nodes: [], edges: [],
  definition: {
    entities: [{ key: 'relay', domain: 'switch', name: 'Relay' }, { key: 'power', domain: 'sensor', name: 'Power' }, { key: 'online', domain: 'binary_sensor' }],
    settings: [{ key: 'host', label: 'Address', type: 'string', value: '10.0.0.7' }, { key: 'port', type: 'number' }, { key: 'password', label: 'Password', type: 'string', secret: true, value: null, hasValue: true }],
  } as unknown as Graph['definition'],
}
const node = (kind: string, config: Record<string, unknown>): GraphNode => ({ id: 'n', kind, config })
/** The summary with the definition lifted into the catalog (the canvas), and from the graph alone (the inspector). */
const both = (n: GraphNode) => {
  const lifted = nodeSummary(n, withGraphTypes(nodeCatalog, graph), undefined, graph)
  expect(nodeSummary(n, nodeCatalog, undefined, graph)).toBe(lifted)
  return lifted
}

describe('the summaries of an entity definition\'s steps', () => {
  it('name the entity by its name, else its key', () => {
    expect(both(node('entity_state', { entity: 'power' }))).toBe('State ▸ Power')
    expect(both(node('set_entity_state', { entity: 'online' }))).toBe('Set state ▸ online')
    expect(both(node('set_entity_available', { entity: 'relay', available: lit('boolean', true) }))).toBe('Set available ▸ Relay')
  })
  it('name the command by its label', () => {
    expect(both(node('entity_command', { entity: 'relay', command: 'turn_off' }))).toBe('When commanded ▸ Relay: Turn off')
    expect(both(node('entity_command', { entity: 'relay', command: 'toggle' }))).toBe('When commanded ▸ Relay: toggle')
  })
  it('name a setting by its label, else its key, and never show its value', () => {
    expect(both(node('setting', { key: 'host' }))).toBe('Setting ▸ Address')
    expect(both(node('setting', { key: 'port' }))).toBe('Setting ▸ port')
    expect(both(node('setting', { key: 'password' }))).toBe('Setting ▸ Password')
  })
  it('show a key the definition no longer has as it is', () => {
    expect(both(node('entity_state', { entity: 'heater' }))).toBe('State ▸ heater')
    expect(both(node('entity_command', { entity: 'heater', command: 'turn_on' }))).toBe('When commanded ▸ heater: turn_on')
    expect(both(node('setting', { key: 'token' }))).toBe('Setting ▸ token')
  })
  it('fall back to the description while nothing is chosen', () => {
    expect(stepSummary(node('entity_state', {}), nodeCatalog, undefined, graph)).toBeUndefined()
    expect(stepSummary(node('setting', { key: '' }), nodeCatalog, undefined, graph)).toBeUndefined()
    expect(nodeSummary(node('entity_state', {}), nodeCatalog, undefined, graph)).toBe(nodeCatalog.nodes.entity_state.description)
  })
})

describe('Has state and Current state', () => {
  it('name the Home Assistant entity and the state', () => {
    expect(both(node('entity_is', { entity: lit('entity_id', 'binary_sensor.motion_a'), state: lit('string', 'on') }))).toBe('Has state ▸ binary_sensor.motion_a is on')
    expect(both(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
    // In an automation as well.
    expect(nodeSummary(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
  })
  it('name the entity as Home Assistant names it, and by its id when Home Assistant does not know it', () => {
    const registry = { entities: [{ entity_id: 'binary_sensor.motion_a', name: 'Hall motion' }, { entity_id: 'sensor.power', name: null }] }
    const summary = (n: GraphNode) => nodeSummary(n, nodeCatalog, registry, graph)
    expect(summary(node('entity_is', { entity: lit('entity_id', 'binary_sensor.motion_a'), state: lit('string', 'on') }))).toBe('Has state ▸ Hall motion is on')
    expect(summary(node('entity_current', { entity: lit('entity_id', 'binary_sensor.motion_a') }))).toBe('Current state ▸ Hall motion')
    expect(summary(node('entity_current', { entity: lit('entity_id', 'sensor.power') }))).toBe('Current state ▸ sensor.power')
    expect(summary(node('entity_is', { entity: lit('entity_id', 'binary_sensor.gone'), state: lit('string', 'on') }))).toBe('Has state ▸ binary_sensor.gone is on')
    // In an automation as well.
    expect(nodeSummary(node('entity_current', { entity: lit('entity_id', 'binary_sensor.motion_a') }), nodeCatalog, registry)).toBe('Current state ▸ Hall motion')
  })
  it('name a wired entity by the output it comes from', () => {
    const wired: Graph = { version: 5, name: 'A', nodes: [
      { id: 't', kind: 'trigger', config: { type: 'entity', entity: 'binary_sensor.motion' } },
      node('entity_is', { entity: { kind: 'unset' }, state: lit('string', 'off') }),
    ], edges: [{ id: 'w', kind: 'data', source: 't', sourcePort: 'entity_id', target: 'n', targetPort: 'entity' }] }
    expect(nodeSummary(wired.nodes[1], nodeCatalog, undefined, wired)).toMatch(/^Has state ▸ .+ is off$/)
  })
})

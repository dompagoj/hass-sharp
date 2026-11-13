import { existsSync, readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import type { Graph } from '../src/visual/contract'

interface ValidationCase {
  name: string; graph: unknown; valid: boolean; nodeId?: string; inputId?: string; contains?: string
  warning?: string; services?: boolean; maxValidationMs?: number; catalogPatch?: Record<string, unknown>
}
const cases = fixtureCases as ValidationCase[]
const servicesFixture = new URL('../../../../tests/fixtures/visual-services.json', import.meta.url)
// Loaded for cases that opt in; the validator takes it once service-aware validation lands.
function loadServices(name: string): unknown {
  if (!existsSync(servicesFixture)) throw new Error(`Visual graph ${name}: case sets "services": true but tests/fixtures/visual-services.json is missing`)
  return JSON.parse(readFileSync(servicesFixture, 'utf8'))
}

// A case may extend the built-in catalog, e.g. with input constraints no built-in node declares yet.
function patched(patch: Record<string, unknown>): contract.VisualCatalogSnapshot {
  const merge = (target: Record<string, unknown>, source: Record<string, unknown>) => {
    for (const [key, value] of Object.entries(source)) {
      const existing = target[key]
      if (value && typeof value === 'object' && !Array.isArray(value) && existing && typeof existing === 'object' && !Array.isArray(existing))
        merge(existing as Record<string, unknown>, value as Record<string, unknown>)
      else target[key] = structuredClone(value)
    }
  }
  const catalog = structuredClone(contract.nodeCatalog)
  merge(catalog as unknown as Record<string, unknown>, patch)
  return catalog
}

describe('shared visual graph validation contract', () => {
  for (const fixture of cases) it(fixture.name, () => {
    if (fixture.services === true) loadServices(fixture.name)
    const started = performance.now()
    const diagnostics = contract.validateGraph(fixture.graph as Graph, fixture.catalogPatch ? patched(fixture.catalogPatch) : undefined)
    if (fixture.maxValidationMs) expect(performance.now() - started).toBeLessThan(fixture.maxValidationMs)
    const errors = diagnostics.filter(d => d.severity === 3)
    expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
    if (!fixture.valid) expect(errors.some(e =>
      (!fixture.nodeId || e.nodeId === fixture.nodeId) && (!fixture.inputId || e.inputId === fixture.inputId) &&
      (!fixture.contains || e.message.toLowerCase().includes(fixture.contains.toLowerCase())))).toBe(true)
    const warning = fixture.warning
    if (warning !== undefined) expect(diagnostics.some(d => d.severity === 2 && d.message.toLowerCase().includes(warning.toLowerCase())), `expected warning "${warning}", got ${JSON.stringify(diagnostics)}`).toBe(true)
  })
})
it('missing layout is deterministic and supplied positions remain unchanged', () => {
  const graph = { version: 5 as const, name: 'Layout', nodes: [
    { id: 'trigger/é', kind: 'trigger', config: { type: 'manual' }, position: { x: 13, y: 42 }, size: { width: 300, height: 100 } },
    { id: 'code', kind: 'custom', config: { code: 'x++;' } },
  ], edges: [] }
  const laidOut = contract.fillGraphLayout(graph)
  expect(laidOut.nodes[0].position).toEqual({ x: 13, y: 42 })
  expect(laidOut.nodes[1].position).toEqual({ x: 360, y: 100 })
  expect(graph.nodes[1].position).toBeUndefined()
  expect(contract.fillGraphLayout(graph)).toEqual(laidOut)
  expect(contract.effectiveTriggerMethod(graph.nodes[0])).toBe('Run_747269676765722FC3A9')
})
it('connect replaces literals, disconnect remains unset, immutable snapshots enable undo', () => {
  const graph = structuredClone(cases[0].graph) as Graph
  graph.edges = graph.edges.filter(e => e.kind !== 'data')
  graph.nodes[2].config.value = { kind: 'literal', type: 'boolean', value: false }
  const connected = contract.connectData(graph, 'a', 'value', 'b', 'value')
  expect(connected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(graph.nodes[2].config.value).toEqual({ kind: 'literal', type: 'boolean', value: false })
  const disconnected = contract.disconnectData(connected, 'b', 'value')
  expect(disconnected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(contract.validateGraph(disconnected)).toEqual(expect.arrayContaining([expect.objectContaining({ nodeId: 'b', inputId: 'value' })]))
})
it('graph creation rejects other versions and consumes catalog defaults', () => {
  for (const version of [3, 4]) expect(() => contract.addGraphNode({ version, nodes: [], edges: [] } as unknown as Graph, 'parallel', 'p')).toThrow(/version 5/i)
  const graph = contract.addGraphNode({ version: 5, name: 'New', nodes: [], edges: [] }, 'parallel', 'p')
  expect(graph.nodes[0].config.branches).toEqual(['a', 'b'])
  expect(contract.executionPorts(graph.nodes[0])).toEqual(['branch:a', 'branch:b', 'next', 'error'])
})
it('replacing an invalid connection still rejects the same type error', () => {
  const graph = structuredClone(cases.find(c => c.name === 'incompatible data type')!.graph) as Graph
  expect(() => contract.connectData(graph, 'a', 'value', 'b', 'value')).toThrow(/type/)
})
it('resolves parametric types and decides refinement before root assignability', () => {
  expect(contract.resolveTypeSpec('entity_id:light')).toMatchObject({ refines: 'entity_id', pattern: '^light\\.[a-z0-9_]+$', label: 'Light entity', clrType: 'System.String', literalKind: 'string', category: 'String' })
  expect(contract.typeChain('entity_id:light')).toEqual(['entity_id:light', 'entity_id', 'string'])
  expect([contract.rootType('entity_id:binary_sensor'), contract.rootType('target')]).toEqual(['string', 'object'])
  expect(contract.refinesType('entity_id:light', 'entity_id')).toBe(true)
  expect(contract.refinesType('string', 'entity_id')).toBe(false)
  for (const id of ['entity_id:Light', 'foo:bar', 'entity_id:', 'string:x', 'global::HassSharp.EntityStateSnapshot:x']) {
    expect(contract.resolveTypeSpec(id)).toBeUndefined()
    expect(contract.typeChain(id)).toEqual([])
  }
  expect(contract.resolveTypeSpec('global::HassSharp.EntityStateSnapshot')).toBeDefined()
  const cases: [string, string, boolean][] = [
    ['entity_id:light', 'entity_id', true], ['entity_id:light', 'string', true], ['target', 'object', true], ['entity_id', 'entity_id', true],
    ['entity_id', 'entity_id:light', false], ['string', 'entity_id', false], ['entity_id:light', 'entity_id:switch', false], ['object', 'target', false], ['foo:bar', 'foo:bar', false],
  ]
  for (const [source, target, expected] of cases) expect(contract.isAssignable(source, target), `${source} -> ${target}`).toBe(expected)
  expect(contract.validLiteral('entity_id:light', 'light.kitchen')).toBe(true)
  expect(contract.validLiteral('entity_id:light', 'switch.x')).toBe(false)
  expect(contract.validLiteral('entity_id:light', 'bad id')).toBe(false)
})
it('read entity exposes a concrete snapshot without scalar configuration', () => {
  const node = { id: 'r', kind: 'read_entity', config: {} }
  expect(contract.dataOutputs(node).value.type).toBe('global::HassSharp.EntityStateSnapshot')
  expect(contract.nodeCatalog.nodes.read_entity.config.type).toBeUndefined()
})
it('trigger outputs follow the trigger type and refine entity ids from config', () => {
  const snapshot = 'global::HassSharp.EntityStateSnapshot'
  const describe = (config: Record<string, unknown>) => Object.entries(contract.dataOutputs({ id: 't', kind: 'trigger', config })).map(([port, output]) => `${port}=${output.type}`).join(',')
  const states = (entity: string) => `data=object,entity_id=${entity},to_state=${snapshot},from_state=${snapshot}`
  const ha = (trigger: Record<string, unknown>) => ({ type: 'ha', trigger })
  const cases: [Record<string, unknown>, string][] = [
    [{ type: 'manual' }, 'data=object'],
    [{ type: 'startup' }, 'data=object'],
    [{ type: 'daily', time: '07:30', timeZone: 'UTC' }, 'data=object,planned=string'],
    [{ type: 'once', at: '2030-01-01T10:00:00Z' }, 'data=object,planned=string'],
    [{ type: 'entity', entity: 'light.kitchen' }, states('entity_id:light')],
    [{ entity: 'light.kitchen' }, states('entity_id:light')],
    [{ type: 'entity' }, states('entity_id')],
    [{ type: 'entity', entity: 'Light.Kitchen' }, states('entity_id')],
    [ha({ trigger: 'state', entity_id: 'light.kitchen' }), states('entity_id:light')],
    [ha({ platform: 'state', entity_id: 'light.kitchen' }), states('entity_id:light')],
    [ha({ trigger: 'state', entity_id: ['light.a', 'light.b'] }), states('entity_id:light')],
    [ha({ trigger: 'state', entity_id: ['light.a', 'switch.b'] }), states('entity_id')],
    [ha({ trigger: 'state', entity_id: [] }), states('entity_id')],
    [ha({ trigger: 'numeric_state', entity_id: 'sensor.temperature', above: 20 }), states('entity_id:sensor')],
    [ha({ trigger: 'template', value_template: '{{ true }}' }), states('entity_id')],
    [ha({ trigger: 'zone', entity_id: 'person.ana', zone: 'zone.home', event: 'enter' }), 'data=object,entity_id=entity_id:person,zone=string,event=string'],
    [ha({ trigger: 'event', event_type: 'test' }), 'data=object,event_type=string,event_data=object'],
    [ha({ trigger: 'time', at: '07:00:00' }), 'data=object,now=string'],
    [ha({ trigger: 'time_pattern', minutes: '/5' }), 'data=object,now=string'],
    [ha({ trigger: 'sun', event: 'sunset' }), 'data=object,event=string'],
    [ha({ trigger: 'homeassistant', event: 'start' }), 'data=object,event=string'],
    [ha({ trigger: 'webhook', webhook_id: 'hook' }), 'data=object,json=object,query=object'],
    [ha({ trigger: 'mqtt', topic: 'a/b' }), 'data=object,topic=string,payload=string'],
    [ha({ trigger: 'device', device_id: 'x' }), 'data=object'],
  ]
  for (const [config, expected] of cases) expect(describe(config), JSON.stringify(config)).toBe(expected)
  const ports = contract.dataOutputs({ id: 't', kind: 'trigger', config: { type: 'entity', entity: 'light.kitchen' } })
  expect([ports.to_state.pointer, ports.data.pointer]).toEqual(['/to_state', ''])
  expect(contract.nodeCatalog.nodes.trigger_data).toBeUndefined()
})
it('accepts explicit per-document catalogs without contaminating built-ins', () => {
  const catalog = structuredClone(contract.nodeCatalog)
  catalog.types['global::Example.Reading'] = { description: 'Reading', assignableTo: ['global::Example.IReading'], nullable: false }
  catalog.types['global::Example.IReading'] = { description: 'IReading', assignableTo: [], nullable: false }
  expect(contract.isAssignable('global::Example.Reading', 'global::Example.IReading', catalog)).toBe(true)
  expect(contract.isAssignable('global::Example.Reading', 'global::Example.IReading')).toBe(false)
  expect(contract.isAssignable('invented', 'invented', catalog)).toBe(false)
})

import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
const richCatalog = rich.catalog as unknown as contract.VisualCatalogSnapshot
for (const fixture of rich.cases) it(`shared CLR catalog: ${fixture.name}`, () => {
  const errors = contract.validateGraph(fixture.graph, richCatalog)
  expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
  if (fixture.inputId) expect(errors.some(e => e.inputId === fixture.inputId)).toBe(true)
})
it('custom node creation and connections preserve reserved argument names', () => {
  const original = rich.cases[0].graph as Graph
  const consumer = original.nodes[3]
  const connected = contract.connectData(original, 'describe', 'result', consumer.id, 'reading', richCatalog)
  expect(connected.nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' }, type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(connected.nodes[3].config.type).toBeUndefined()
  expect(contract.disconnectData(connected, consumer.id, 'reading', richCatalog).nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' } })
  const created = contract.addGraphNode({ version: 5, name: 'Create', nodes: [], edges: [] }, consumer.kind, 'c', richCatalog).nodes[0]
  expect(created.config.arguments).toMatchObject({ type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(contract.validateGraph(original).some(d => d.message === 'Unknown node kind')).toBe(true)
  expect(contract.validateGraph(original, richCatalog)).toEqual([])
  expect(contract.validateGraph(original).some(d => d.message === 'Unknown node kind')).toBe(true)
})

it('nullable non-finite compiler defaults remain omitted when creating custom nodes', () => {
  const kind = Object.entries(richCatalog.nodes).find(([, spec]) => spec.label === 'Nonfinite defaults')![0]
  const node = contract.addGraphNode({ version: 5, name: 'Defaults', nodes: [], edges: [] }, kind, 'd', richCatalog).nodes[0]
  expect(node.config.arguments).toEqual({})
  for (const input of Object.values(richCatalog.nodes[kind].inputs)) expect(Object.hasOwn(input, 'default')).toBe(false)
})

it('rejects stale literal arguments after an optional custom parameter rename', () => {
  const graph = structuredClone(rich.cases[0].graph) as Graph
  const consumer = graph.nodes[3]
  ;(consumer.config.arguments as Record<string, unknown>).removedParameter = { kind: 'literal', type: 'string', value: 'old value' }
  expect(contract.validateGraph(graph, richCatalog)).toEqual(expect.arrayContaining([
    expect.objectContaining({ nodeId: consumer.id, inputId: 'removedParameter', message: 'Unknown custom argument' }),
  ]))
})

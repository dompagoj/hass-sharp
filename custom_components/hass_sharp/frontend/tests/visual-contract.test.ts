import { existsSync, readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import type { GraphV4 } from '../src/visual/contract'

interface ValidationCase {
  name: string; graph: unknown; valid: boolean; nodeId?: string; inputId?: string; contains?: string
  warning?: string; services?: boolean; maxValidationMs?: number
}
const cases = fixtureCases as ValidationCase[]
const servicesFixture = new URL('../../../../tests/fixtures/visual-services.json', import.meta.url)
// Loaded for cases that opt in; the validator takes it once service-aware validation lands.
function loadServices(name: string): unknown {
  if (!existsSync(servicesFixture)) throw new Error(`Visual graph ${name}: case sets "services": true but tests/fixtures/visual-services.json is missing`)
  return JSON.parse(readFileSync(servicesFixture, 'utf8'))
}

describe('shared visual graph validation contract', () => {
  for (const fixture of cases) it(fixture.name, () => {
    if (fixture.services === true) loadServices(fixture.name)
    const started = performance.now()
    const diagnostics = contract.validateGraphV4(fixture.graph as GraphV4)
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
  const graph = { version: 4 as const, name: 'Layout', nodes: [
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
  const graph = structuredClone(cases[0].graph) as GraphV4
  graph.edges = graph.edges.filter(e => e.kind !== 'data')
  graph.nodes[2].config.value = { kind: 'literal', type: 'boolean', value: false }
  const connected = contract.connectData(graph, 'a', 'value', 'b', 'value')
  expect(connected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(graph.nodes[2].config.value).toEqual({ kind: 'literal', type: 'boolean', value: false })
  const disconnected = contract.disconnectData(connected, 'b', 'value')
  expect(disconnected.nodes[2].config.value).toEqual({ kind: 'unset' })
  expect(contract.validateGraphV4(disconnected)).toEqual(expect.arrayContaining([expect.objectContaining({ nodeId: 'b', inputId: 'value' })]))
})
it('v4 creation rejects other formats and consumes catalog defaults', () => {
  expect(() => contract.addV4Node({ version: 3, nodes: [], edges: [] } as unknown as GraphV4, 'parallel', 'p')).toThrow(/version 4/i)
  const graph = contract.addV4Node({ version: 4, name: 'New', nodes: [], edges: [] }, 'parallel', 'p')
  expect(graph.nodes[0].config.branches).toEqual(['a', 'b'])
  expect(contract.executionPorts(graph.nodes[0])).toEqual(['branch:a', 'branch:b', 'next', 'error'])
})
it('replacing an invalid connection still rejects the same type error', () => {
  const graph = structuredClone(cases.find(c => c.name === 'incompatible data type')!.graph) as GraphV4
  expect(() => contract.connectData(graph, 'a', 'value', 'b', 'value')).toThrow(/type/)
})
it('read entity exposes a concrete snapshot without scalar configuration', () => {
  const node = { id: 'r', kind: 'read_entity', config: {} }
  expect(contract.dataOutputs(node).value.type).toBe('global::HassSharp.EntityStateSnapshot')
  expect(contract.nodeCatalog.nodes.read_entity.config.type).toBeUndefined()
})
it('accepts explicit per-document catalogs without contaminating built-ins', () => {
  const catalog = structuredClone(contract.nodeCatalog)
  catalog.types['global::Example.Reading'] = { description: 'Reading', assignableTo: ['global::Example.IReading'], nullable: false }
  catalog.types['global::Example.IReading'] = { description: 'IReading', assignableTo: [], nullable: false }
  expect(contract.compatibleTypes('global::Example.Reading', 'global::Example.IReading', catalog)).toBe(true)
  expect(contract.compatibleTypes('global::Example.Reading', 'global::Example.IReading')).toBe(false)
  expect(contract.compatibleTypes('invented', 'invented', catalog)).toBe(false)
})

import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
const richCatalog = rich.catalog as unknown as contract.VisualCatalogSnapshot
for (const fixture of rich.cases) it(`shared CLR catalog: ${fixture.name}`, () => {
  const errors = contract.validateGraphV4(fixture.graph, richCatalog)
  expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
  if (fixture.inputId) expect(errors.some(e => e.inputId === fixture.inputId)).toBe(true)
})
it('custom node creation and connections preserve reserved argument names', () => {
  const original = rich.cases[0].graph as GraphV4
  const consumer = original.nodes[3]
  const connected = contract.connectData(original, 'describe', 'result', consumer.id, 'reading', richCatalog)
  expect(connected.nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' }, type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(connected.nodes[3].config.type).toBeUndefined()
  expect(contract.disconnectData(connected, consumer.id, 'reading', richCatalog).nodes[3].config.arguments).toMatchObject({ reading: { kind: 'unset' } })
  const created = contract.addV4Node({ version: 4, name: 'Create', nodes: [], edges: [] }, consumer.kind, 'c', richCatalog).nodes[0]
  expect(created.config.arguments).toMatchObject({ type: { value: 3 }, output: { value: null }, service: { value: 'light.turn_on' } })
  expect(contract.validateGraphV4(original).some(d => d.message === 'Unknown node kind')).toBe(true)
  expect(contract.validateGraphV4(original, richCatalog)).toEqual([])
  expect(contract.validateGraphV4(original).some(d => d.message === 'Unknown node kind')).toBe(true)
})

it('nullable non-finite compiler defaults remain omitted when creating custom nodes', () => {
  const kind = Object.entries(richCatalog.nodes).find(([, spec]) => spec.label === 'Nonfinite defaults')![0]
  const node = contract.addV4Node({ version: 4, name: 'Defaults', nodes: [], edges: [] }, kind, 'd', richCatalog).nodes[0]
  expect(node.config.arguments).toEqual({})
  for (const input of Object.values(richCatalog.nodes[kind].inputs)) expect(Object.hasOwn(input, 'default')).toBe(false)
})

it('rejects stale literal arguments after an optional custom parameter rename', () => {
  const graph = structuredClone(rich.cases[0].graph) as GraphV4
  const consumer = graph.nodes[3]
  ;(consumer.config.arguments as Record<string, unknown>).removedParameter = { kind: 'literal', type: 'string', value: 'old value' }
  expect(contract.validateGraphV4(graph, richCatalog)).toEqual(expect.arrayContaining([
    expect.objectContaining({ nodeId: consumer.id, inputId: 'removedParameter', message: 'Unknown custom argument' }),
  ]))
})

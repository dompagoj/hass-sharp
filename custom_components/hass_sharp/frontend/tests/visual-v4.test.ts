import { describe, expect, it } from 'vitest'
import cases from '../../../../tests/fixtures/visual-v4-validation.json'
import * as contract from '../src/visual/graphV4'
import type { GraphV4 } from '../src/visual/graphV4'

describe('shared v4 validation contract', () => {
  for (const fixture of cases) it(fixture.name, () => {
    const errors = contract.validateGraphV4(fixture.graph as GraphV4)
    expect(errors.length === 0, JSON.stringify(errors)).toBe(fixture.valid)
    if (!fixture.valid) expect(errors.some(e =>
      (!fixture.nodeId || e.nodeId === fixture.nodeId) && (!fixture.inputId || e.inputId === fixture.inputId) &&
      (!fixture.contains || e.message.toLowerCase().includes(fixture.contains.toLowerCase())))).toBe(true)
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

import { describe, expect, it } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import servicesFixture from '../../../../tests/fixtures/visual-services.json'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import * as contract from '../src/visual/contract'
import type { Graph, VisualCatalogSnapshot } from '../src/visual/contract'
import { connectionIndex } from '../src/visual/connectionIndex'
import { connectNodes, createGraph, dataConnectionError, inputType, literal, nodeInputs, updateNode } from '../src/visual/graph'

interface ValidationCase { name: string; graph: unknown; services?: boolean; catalogPatch?: Record<string, unknown> }
const services = servicesFixture as unknown as contract.ServicesSection
// The same catalog patching as visual-contract.test.ts.
function patched(patch: Record<string, unknown>): VisualCatalogSnapshot {
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

/** The previous dataConnectionError: port and type pre-check, then the full connectData (clone plus two validations). */
function fullCheck(graph: Graph, source: string, sourcePort: string, target: string, targetPort: string, catalog: VisualCatalogSnapshot) {
  const from = graph.nodes.find(n => n.id === source)!, to = graph.nodes.find(n => n.id === target)!
  const output = contract.dataOutputs(from, catalog)[sourcePort], input = nodeInputs(graph, to, catalog)[targetPort]
  if (!output || !input) return 'Unknown data port'
  if (!input.types.some(t => contract.isAssignable(output.type, t, catalog))) return 'type'
  try { contract.connectData(graph, source, sourcePort, target, targetPort, catalog) } catch (error) { return (error as Error).message }
}
// Pinned totals of the agreement runs below.
const GRAPHS = 203, PAIRS = 657, ACCEPTED = 238, BY_VALIDATION = 135, SERVICES_ACCEPTED = 189, SERVICES_BY_VALIDATION = 142
const RICH: Totals = { graphs: 21, pairs: 190, accepted: 38, rejected: 152, byValidation: 0, fallbacks: [], skipped: [] }
const parts = (message?: string) => [...new Set(message?.split('; ') ?? [])].sort()

interface Totals { graphs: number; pairs: number; accepted: number; rejected: number; byValidation: number; fallbacks: string[]; skipped: string[] }
const totals = (): Totals => ({ graphs: 0, pairs: 0, accepted: 0, rejected: 0, byValidation: 0, fallbacks: [], skipped: [] })
function agree(name: string, value: unknown, catalog: VisualCatalogSnapshot, totals: Totals) {
  const graph = value as Graph
  const nodes = Array.isArray(graph?.nodes) ? graph.nodes : undefined
  // Only a graph whose node list cannot be read has no pairs to evaluate.
  if (!nodes || !Array.isArray(graph.edges)) { totals.skipped.push(`${name}: nodes or edges are not arrays`); return }
  const wellFormed = nodes.filter(n => n && typeof n === 'object' && typeof n.id === 'string' && n.config && typeof n.config === 'object')
  if (!wellFormed.length) { totals.skipped.push(`${name}: no node has an id and config`); return }
  totals.graphs++
  for (const source of wellFormed) for (const sourcePort of Object.keys(contract.dataOutputs(source, catalog)))
    for (const target of wellFormed) for (const targetPort of Object.keys(nodeInputs(graph, target, catalog))) {
      const expected = fullCheck(graph, source.id, sourcePort, target.id, targetPort, catalog)
      const actual = dataConnectionError(graph, source.id, sourcePort, target.id, targetPort, catalog, { quick: true })
      const label = `${name}: ${source.id}.${sourcePort} -> ${target.id}.${targetPort}`
      expect(actual === undefined, `${label}\nfull: ${expected}\nquick: ${actual}`).toBe(expected === undefined)
      // Every pair, accepted or rejected, gets an index verdict; one that ran connectData itself is a fallback.
      const verdict = connectionIndex(graph, catalog).verdict(source.id, sourcePort, target.id, targetPort)
      if (verdict.fallback) totals.fallbacks.push(label)
      if (expected === undefined) expect(verdict.message, label).toBeUndefined()
      if (expected !== undefined && expected !== 'type' && expected !== 'Unknown data port') {
        totals.byValidation++
        // The index predicts the same diagnostics connectData adds, so the hover message matches the drop message.
        expect(parts(verdict.message), label).toEqual(parts(expected))
      }
      totals.pairs++
      if (expected === undefined) totals.accepted++
      else totals.rejected++
    }
}

describe('connection index agrees with connectData', () => {
  const cases = fixtureCases as ValidationCase[]
  // Every case runs with and without the services section: ports never depend on it (D3), only types and checks.
  for (const withServices of [false, true]) it(`for every fixture graph and output -> input pair (${withServices ? 'with' : 'without'} services)`, () => {
    const sum = totals()
    for (const fixture of cases) {
      const base = fixture.catalogPatch ? patched(fixture.catalogPatch) : contract.nodeCatalog
      agree(fixture.name, fixture.graph, withServices ? { ...base, services } : base, sum)
    }
    // Pinned: a fixture change that alters these is visible here. No fixture pair needs the full check.
    expect(sum).toEqual(withServices
      ? { graphs: GRAPHS, pairs: PAIRS, accepted: SERVICES_ACCEPTED, rejected: PAIRS - SERVICES_ACCEPTED, byValidation: SERVICES_BY_VALIDATION, fallbacks: [], skipped: [] }
      : { graphs: GRAPHS, pairs: PAIRS, accepted: ACCEPTED, rejected: PAIRS - ACCEPTED, byValidation: BY_VALIDATION, fallbacks: [], skipped: [] })
  })
  it('for the shared CLR catalog graphs', () => {
    const sum = totals()
    for (const fixture of rich.cases) agree(fixture.name, fixture.graph, rich.catalog as unknown as VisualCatalogSnapshot, sum)
    expect(sum).toEqual(RICH)
  })
})

describe('connection index', () => {
  const light = () => updateNode(createGraph('Index'), 'trigger-1', { config: { type: 'entity', entity: 'light.hall', manual: true, mode: 'Single' } })
  it('is cached per graph object and per catalog object', () => {
    const graph = light()
    expect(connectionIndex(graph)).toBe(connectionIndex(graph))
    expect(connectionIndex(structuredClone(graph))).not.toBe(connectionIndex(graph))
    const withServices = { ...contract.nodeCatalog, services }
    expect(connectionIndex(graph, withServices)).not.toBe(connectionIndex(graph))
    expect(connectionIndex(graph, { ...contract.nodeCatalog, services })).not.toBe(connectionIndex(graph, withServices))
  })
  it('rejects a plain entity id on a domain-refined input and accepts the refined one (C1)', () => {
    const withServices = { ...contract.nodeCatalog, services }
    const refined = light()
    expect(dataConnectionError(refined, 'trigger-1', 'entity_id', 'service-1', 'target', withServices, { quick: true })).toBeUndefined()
    const plain = updateNode(refined, 'trigger-1', { config: { type: 'entity', manual: true, mode: 'Single' } })
    expect(dataConnectionError(plain, 'trigger-1', 'entity_id', 'service-1', 'target', withServices, { quick: true })).toMatch(/requires/)
    // Without the services section the target stays permissive.
    expect(dataConnectionError(plain, 'trigger-1', 'entity_id', 'service-1', 'target', contract.nodeCatalog, { quick: true })).toBeUndefined()
  })
  it('answers hover checks without cloning the graph', () => {
    const graph = light()
    connectionIndex(graph)
    const clone = globalThis.structuredClone
    let clones = 0
    globalThis.structuredClone = ((value: unknown) => { clones++; return clone(value) }) as typeof structuredClone
    try {
      for (let i = 0; i < 50; i++) dataConnectionError(graph, 'trigger-1', 'entity_id', 'condition-1', 'entity', contract.nodeCatalog, { quick: true })
    } finally { globalThis.structuredClone = clone }
    expect(clones).toBe(0)
  })
  it('resolves variable inputs to the first visible producer only (D7)', () => {
    let graph = createGraph('Variables')
    graph = contract.addGraphNode(graph, 'and', 'hidden')
    graph = contract.addGraphNode(graph, 'and', 'consumer')
    graph = updateNode(graph, 'hidden', { config: { output: 'flag', left: literal('boolean', true), right: literal('boolean', true) } })
    graph = updateNode(graph, 'consumer', { config: { left: { kind: 'variable', name: 'flag' }, right: literal('boolean', true) } })
    // 'hidden' is unreachable, so the validator sees no visible producer.
    const consumer = graph.nodes.find(n => n.id === 'consumer')!
    const input = { types: ['boolean', 'string'], required: true, description: '' }
    expect(inputType(graph, consumer, 'left', input)).toBeUndefined()
    expect(connectionIndex(graph).resolvedType('consumer', 'right')).toBe('boolean')
  })
})

describe('connection index fallbacks', () => {
  // trigger -> array -> foreach (body: delay), so the array value is visible to the loop's Items input.
  const loop = () => {
    let graph = contract.addGraphNode(contract.addGraphNode({ version: 5, name: 'Loop', nodes: [], edges: [] }, 'trigger', 't'), 'array', 'list')
    graph = contract.addGraphNode(contract.addGraphNode(graph, 'foreach', 'each'), 'delay', 'wait')
    graph = updateNode(graph, 't', { config: { type: 'manual', manual: true, mode: 'Single' } })
    return connectNodes(connectNodes(connectNodes(graph, 't', 'list'), 'list', 'each'), 'each', 'wait', 'body')
  }
  it('decides a ForEach Items input from the index, and memoises verdicts', () => {
    const graph = loop()
    expect(contract.validateGraph(graph).filter(d => d.severity === 3)).toEqual([])
    const index = connectionIndex(graph)
    const verdict = index.verdict('list', 'value', 'each', 'items')
    expect(verdict).toEqual({ fallback: false })
    expect(index.verdict('list', 'value', 'each', 'items')).toBe(verdict)
    expect(fullCheck(graph, 'list', 'value', 'each', 'items', contract.nodeCatalog)).toBeUndefined()
  })
  it('falls back only for an input stored where configuration is read, and memoises that verdict too', () => {
    // A patched catalog whose 'and' node also declares 'left' as configuration: wiring it would change that config.
    const catalog = patched({ nodes: { and: { config: { left: { type: 'object' } } } } })
    let graph = contract.addGraphNode(loop(), 'and', 'both', catalog)
    graph = connectNodes(graph, 'each', 'both', 'next', catalog)
    const index = connectionIndex(graph, catalog)
    const verdict = index.verdict('each', 'index', 'both', 'left')
    expect(verdict.fallback).toBe(true)
    expect(index.verdict('each', 'index', 'both', 'left')).toBe(verdict)
    expect(index.verdict('each', 'index', 'both', 'right').fallback).toBe(false)
    expect(dataConnectionError(graph, 'each', 'index', 'both', 'left', catalog, { quick: true }) === undefined)
      .toBe(fullCheck(graph, 'each', 'index', 'both', 'left', catalog) === undefined)
  })
  it('rejects instead of throwing when an edge entry is not an object', () => {
    for (const junk of [null, undefined, 'junk', 7]) {
      const graph = { ...loop(), edges: [...loop().edges, junk] } as unknown as Graph
      const quick = () => dataConnectionError(graph, 'list', 'value', 'each', 'items', contract.nodeCatalog, { quick: true })
      expect(quick, String(junk)).not.toThrow()
      expect(quick() === undefined, String(junk)).toBe(fullCheck(graph, 'list', 'value', 'each', 'items', contract.nodeCatalog) === undefined)
      expect(connectionIndex(graph).verdict('list', 'value', 'each', 'items').fallback).toBe(false)
    }
    const graph = { ...loop(), edges: [...loop().edges, null] } as unknown as Graph
    expect(dataConnectionError(graph, 'list', 'value', 'each', 'items', contract.nodeCatalog, { quick: true })).toBe('Every edge must be an object')
  })
})

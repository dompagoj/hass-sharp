import { expect, it } from 'vitest'
import rich from '../../../../tests/fixtures/visual-rich-catalog.json'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'
import * as contract from '../src/visual/contract'
import { addConnected } from '../src/visual/flow/autoConnect'
import { buildEdgeView, buildNodeView } from '../src/visual/flow/flowModel'
import type { VisualGraph } from '../src/visual/graph'

// The types a Custom C# node's code uses beyond the catalog live in its signature: cards, wires and auto-connect
// look them up there too (withGraphTypes), with the graphs and signatures the compiler wrote for the shared fixtures.
const richCatalog = rich.catalog as unknown as contract.VisualCatalogSnapshot
const richCase = (name: string) => structuredClone(rich.cases.find(c => c.name === name)!.graph) as unknown as VisualGraph
const validationCase = (name: string) => structuredClone((fixtureCases as { name: string; graph: unknown }[]).find(c => c.name === name)!.graph) as VisualGraph

it('auto-connect offers a library step for a custom node\'s output of a type the code declares', () => {
  const graph = richCase('custom: a record the code declares into the library interface it implements')
  const consume = graph.nodes.find(n => n.id === 'consume')!
  const without: VisualGraph = { ...graph, nodes: graph.nodes.filter(n => n.id !== 'consume'), edges: graph.edges.filter(e => e.source !== 'consume' && e.target !== 'consume') }
  const result = addConnected(without, consume.kind, { x: 400, y: 0 }, { nodeId: 'make', handle: 'data:result', type: 'source' }, richCatalog, 'added')
  expect(result, 'the library step is placed and wired').toBeDefined()
  expect(result!.graph.edges.some(e => e.kind === 'data' && e.source === 'make' && e.sourcePort === 'result' && e.target === 'added' && e.targetPort === 'reading')).toBe(true)
})

it('cards and wires name and colour a type the code declares, and a CLR number as a number', () => {
  const graph = richCase('custom: a record the code declares into the library interface it implements')
  const make = graph.nodes.find(n => n.id === 'make')!
  const view = buildNodeView(make, { graph, catalog: richCatalog, diagnostics: [] })
  const output = view.rows.map(r => r.output).find(p => p?.id === 'data:result')!
  expect(output.typeName).toBe('Own')
  expect(output.color, 'a declared type gets its own colour, not the unknown-type one').not.toBe('var(--hs-port-object)')
  const wire = graph.edges.find(e => e.kind === 'data' && e.source === 'make')!
  expect(buildEdgeView(wire, graph, richCatalog)!.color).toBe(output.color)

  const numbers = validationCase('custom node: a CLR int goes into another custom node\'s long, float and decimal pins')
  const take = numbers.nodes.find(n => n.id === 'take')!
  const inputs = buildNodeView(take, { graph: numbers, catalog: contract.nodeCatalog, diagnostics: [] }).rows.map(r => r.input?.pin).filter(p => p !== undefined)
  expect(inputs.map(p => [p.label, p.typeName, p.color])).toEqual([['A', 'Number', 'var(--hs-port-number)'], ['B', 'Number', 'var(--hs-port-number)'], ['C', 'Number', 'var(--hs-port-number)']])
})

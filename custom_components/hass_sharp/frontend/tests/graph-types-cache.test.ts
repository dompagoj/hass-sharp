import { expect, it, vi } from 'vitest'
import fixtureCases from '../../../../tests/fixtures/visual-graph-validation.json'

// Count the merges and analyses the cards, wires and connection checks ask contract for.
vi.mock('../src/visual/contract', async importOriginal => {
  const actual = await importOriginal<typeof import('../src/visual/contract')>()
  return { ...actual, withGraphTypes: vi.fn(actual.withGraphTypes), analyzeGraph: vi.fn(actual.analyzeGraph) }
})

const contract = await import('../src/visual/contract')
const { connectionIndex, graphTypes } = await import('../src/visual/connectionIndex')
const { buildEdgeView, buildNodeView, viewKey } = await import('../src/visual/flow/flowModel')
const { dataConnectionError } = await import('../src/visual/graph')
type VisualGraph = import('../src/visual/graph').VisualGraph

const validationCase = (name: string) => structuredClone((fixtureCases as { name: string; graph: unknown }[]).find(c => c.name === name)!.graph) as VisualGraph

it('merges the graph\'s Custom C# types once and analyses the graph once per graph change', () => {
  // Custom C# nodes with types only their code uses, and a Log whose value input takes any type (a union input).
  const graph = validationCase('custom node: a CLR list goes into another custom node\'s IEnumerable<object> pin when a third node names both (listed last)')
  graph.nodes.push({ id: 'log', kind: 'log', config: { value: { kind: 'unset' } } } as VisualGraph['nodes'][number])
  graph.edges.push(
    { id: 'take:next:log', source: 'take', target: 'log', port: 'next' } as VisualGraph['edges'][number],
    { id: 'make.result:log.value', kind: 'data', source: 'make', sourcePort: 'result', target: 'log', targetPort: 'value' } as VisualGraph['edges'][number],
  )
  const merges = vi.mocked(contract.withGraphTypes), analyses = vi.mocked(contract.analyzeGraph)
  // What the canvas does for one graph: the index, every card's key and view, every wire, a connection check.
  const render = (value: VisualGraph) => {
    const ctx = { graph: value, catalog: contract.nodeCatalog, diagnostics: [] }
    const index = connectionIndex(value, ctx.catalog)
    for (const node of value.nodes) { viewKey(node, ctx, index); buildNodeView(node, ctx) }
    for (const edge of value.edges) buildEdgeView(edge, value, ctx.catalog)
    dataConnectionError(value, 'make', 'result', 'log', 'value', ctx.catalog, { quick: true })
    return index
  }
  merges.mockClear()
  analyses.mockClear()
  const index = render(graph)
  expect(index.catalog, 'the graph has types of its own').not.toBe(contract.nodeCatalog)
  expect(graphTypes(graph, contract.nodeCatalog)).toBe(index.catalog)
  expect(connectionIndex(graph, index.catalog), 'the merged catalog shares the index').toBe(index)
  expect(merges).toHaveBeenCalledTimes(1)
  expect(analyses).toHaveBeenCalledTimes(1)

  render(graph)
  expect(merges, 'the same graph again: nothing new').toHaveBeenCalledTimes(1)
  expect(analyses).toHaveBeenCalledTimes(1)

  const next: VisualGraph = { ...graph, nodes: graph.nodes.map(n => (n.id === 'log' ? { ...n, config: { ...n.config, level: 'warning' } } : n)) }
  render(next)
  expect(merges, 'a changed graph: one more merge').toHaveBeenCalledTimes(2)
  expect(analyses, 'and one more analysis').toHaveBeenCalledTimes(2)
})

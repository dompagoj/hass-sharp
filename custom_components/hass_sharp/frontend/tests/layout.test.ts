import { expect, it } from 'vitest'
import { applyLayout, tidyLayout } from '../src/visual/layout'
import { addNode, connectNodes, createGraph, updateNode, type VisualGraph } from '../src/visual/graph'

const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
/** trigger → condition → (true) service, (false) delay → stop, and a Set value nothing runs. */
const branching = () => {
  let graph = addNode(addNode(createGraph('Layout'), 'delay', 'wait'), 'stop', 'halt')
  graph = connectNodes(connectNodes(graph, 'condition-1', 'wait', 'false'), 'wait', 'halt')
  graph = addNode(graph, 'set_value', 'feed')
  graph = updateNode(graph, 'feed', { config: { type: 'entity_id', value: lit('entity_id', 'binary_sensor.hall') } })
  return graph
}
const sizes: Record<string, { width: number; height: number }> = {
  'trigger-1': { width: 240, height: 160 }, 'condition-1': { width: 300, height: 140 }, 'service-1': { width: 280, height: 220 },
  wait: { width: 220, height: 90 }, halt: { width: 220, height: 60 }, feed: { width: 260, height: 120 },
}
const overlap = (a: { x: number; y: number }, sa: { width: number; height: number }, b: { x: number; y: number }, sb: { width: number; height: number }) =>
  a.x < b.x + sb.width && b.x < a.x + sa.width && a.y < b.y + sb.height && b.y < a.y + sa.height

it('lays out left to right along execution wires, on the grid, without overlaps', () => {
  const graph = branching()
  const positions = tidyLayout(graph, undefined, { sizes: id => sizes[id] })
  expect([...positions.keys()].sort()).toEqual(graph.nodes.map(n => n.id).sort())
  for (const edge of graph.edges) if (edge.kind !== 'data') expect(positions.get(edge.source)!.x, edge.id).toBeLessThan(positions.get(edge.target)!.x)
  // Branches keep their ports' order: If true above If false.
  expect(positions.get('service-1')!.y).toBeLessThan(positions.get('wait')!.y)
  const ids = [...positions.keys()]
  for (const a of ids) for (const b of ids) if (a < b) expect(overlap(positions.get(a)!, sizes[a], positions.get(b)!, sizes[b]), `${a} and ${b}`).toBe(false)
  for (const { x, y } of positions.values()) {
    expect(x % 20).toBe(0)
    expect(y % 20).toBe(0)
  }
})

it('is deterministic: the same graph and sizes give the same positions, whatever the stored positions were', () => {
  const graph = branching()
  const first = tidyLayout(graph, undefined, { sizes: id => sizes[id] })
  expect(tidyLayout(structuredClone(graph), undefined, { sizes: id => sizes[id] })).toEqual(first)
  // Scrambled positions with the same top-left corner lay out the same way.
  const scrambled: VisualGraph = { ...graph, nodes: graph.nodes.map((node, i) => ({ ...node, position: { x: 60 + ((i * 397) % 900), y: 100 + ((i * 211) % 500) } })) }
  expect(tidyLayout(scrambled, undefined, { sizes: id => sizes[id] })).toEqual(first)
  // Laying out again changes nothing.
  const tidied = applyLayout(graph, first)
  expect(applyLayout(tidied, tidyLayout(tidied, undefined, { sizes: id => sizes[id] }))).toBe(tidied)
})

it('places a node with only data wires left of its first consumer, and unmeasured cards at a fallback size', () => {
  let graph = createGraph('Data')
  graph = addNode(graph, 'set_value', 'value')
  graph = updateNode(graph, 'value', { config: { type: 'number', value: lit('number', 1) } })
  graph = addNode(graph, 'log', 'log')
  graph = connectNodes(graph, 'service-1', 'log')
  // A data wire on its own (the validator would warn the producer is not reachable; the layout still places it).
  graph = { ...graph, edges: [...graph.edges, { id: 'value-to-log', kind: 'data', source: 'value', target: 'log', sourcePort: 'value', targetPort: 'value' }] }
  const positions = tidyLayout(graph)
  expect(positions.get('value')!.x).toBeLessThan(positions.get('log')!.x)
  expect(positions.get('value')!.x).toBeGreaterThan(positions.get('condition-1')!.x)
  const ids = [...positions.keys()]
  const fallback = { width: 260, height: 120 }
  for (const a of ids) for (const b of ids) if (a < b) expect(overlap(positions.get(a)!, fallback, positions.get(b)!, fallback), `${a} and ${b}`).toBe(false)
})

it('lays out only the chosen nodes, from their own top-left corner, and leaves the rest alone', () => {
  let graph = branching()
  graph = updateNode(graph, 'wait', { position: { x: 1000, y: 600 } })
  graph = updateNode(graph, 'halt', { position: { x: 1020, y: 900 } })
  const positions = tidyLayout(graph, undefined, { only: new Set(['wait', 'halt']), sizes: id => sizes[id] })
  expect([...positions.keys()]).toEqual(['wait', 'halt'])
  expect(positions.get('wait')).toEqual({ x: 1000, y: 600 })
  expect(positions.get('halt')!.x).toBeGreaterThan(1000 + sizes.wait.width)
  const next = applyLayout(graph, positions)
  expect(next.nodes.find(n => n.id === 'trigger-1')).toBe(graph.nodes.find(n => n.id === 'trigger-1'))
})

it('survives malformed entries and an execution cycle', () => {
  const graph = createGraph('Broken')
  const broken = {
    ...graph,
    nodes: [null, ...graph.nodes],
    edges: [null, ...graph.edges, { id: 'back', source: 'service-1', target: 'condition-1', port: 'error' }],
  } as unknown as VisualGraph
  const positions = tidyLayout(broken)
  expect([...positions.keys()].sort()).toEqual(['condition-1', 'service-1', 'trigger-1'])
  expect(applyLayout(broken, positions).nodes[0]).toBeNull()
})

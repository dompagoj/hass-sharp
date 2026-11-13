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

const each = (positions: Map<string, { x: number; y: number }>, ids: string[]) => ids.map(id => positions.get(id)!)
const noOverlaps = (positions: Map<string, { x: number; y: number }>, size = { width: 260, height: 120 }) => {
  const ids = [...positions.keys()]
  for (const a of ids) for (const b of ids) if (a < b) expect(overlap(positions.get(a)!, size, positions.get(b)!, size), `${a} and ${b}`).toBe(false)
}

it('places what runs after a loop right of the loop body, not in its columns', () => {
  let graph = createGraph('Loop')
  graph = { ...graph, nodes: graph.nodes.filter(n => n.id === 'trigger-1'), edges: [] }
  for (const [kind, id] of [['repeat', 'loop'], ['delay', 'first'], ['delay', 'second'], ['log', 'after']] as const) graph = addNode(graph, kind, id)
  graph = connectNodes(graph, 'trigger-1', 'loop')
  graph = connectNodes(connectNodes(graph, 'loop', 'first', 'body'), 'first', 'second')
  graph = connectNodes(graph, 'loop', 'after', 'next')
  const positions = tidyLayout(graph)
  const [loop, first, second, after] = each(positions, ['loop', 'first', 'second', 'after'])
  expect(first.x).toBeGreaterThan(loop.x)
  expect(second.x).toBeGreaterThan(first.x)
  expect(after.x).toBeGreaterThan(second.x)
  noOverlaps(positions)
})

it('keeps a parallel step\'s two branches in their order and runs what follows after both', () => {
  let graph = createGraph('Parallel')
  graph = { ...graph, nodes: graph.nodes.filter(n => n.id === 'trigger-1'), edges: [] }
  graph = addNode(graph, 'parallel', 'both')
  graph = updateNode(graph, 'both', { config: { ...graph.nodes.find(n => n.id === 'both')!.config, branches: ['left', 'right'] } })
  for (const [kind, id] of [['delay', 'a1'], ['delay', 'a2'], ['log', 'b1'], ['log', 'done']] as const) graph = addNode(graph, kind, id)
  graph = connectNodes(graph, 'trigger-1', 'both')
  graph = connectNodes(connectNodes(graph, 'both', 'a1', 'branch:left'), 'a1', 'a2')
  graph = connectNodes(graph, 'both', 'b1', 'branch:right')
  graph = connectNodes(graph, 'both', 'done', 'next')
  expect(graph.edges).toHaveLength(5)
  const positions = tidyLayout(graph)
  const [both, a1, a2, b1, done] = each(positions, ['both', 'a1', 'a2', 'b1', 'done'])
  expect(a1.x).toBe(b1.x)
  expect(a1.y).toBeLessThan(b1.y)
  expect(a2.x).toBeGreaterThan(a1.x)
  expect(done.x).toBeGreaterThan(Math.max(a2.x, b1.x))
  expect(both.x).toBeLessThan(a1.x)
  noOverlaps(positions)
})

it('gives each trigger\'s flow its own band, in the order of the triggers, and loose steps a last row', () => {
  let graph = createGraph('Two')
  // The second flow's trigger sits above the first's: its band comes first.
  graph = updateNode(graph, 'trigger-1', { position: { x: 60, y: 400 } })
  graph = addNode(graph, 'trigger', 'other')
  graph = updateNode(graph, 'other', { position: { x: 300, y: 100 } })
  for (const id of ['o1', 'o2']) graph = addNode(graph, 'delay', id)
  graph = connectNodes(connectNodes(graph, 'other', 'o1'), 'o1', 'o2')
  graph = addNode(graph, 'stop', 'alone')
  const positions = tidyLayout(graph)
  const first = ['trigger-1', 'condition-1', 'service-1'], second = ['other', 'o1', 'o2']
  const top = (ids: string[]) => Math.min(...each(positions, ids).map(p => p.y))
  const bottom = (ids: string[]) => Math.max(...each(positions, ids).map(p => p.y + 120))
  expect(bottom(second)).toBeLessThanOrEqual(top(first))
  expect(positions.get('alone')!.y).toBeGreaterThanOrEqual(bottom(first))
  // Both bands start at the left edge of the layout.
  expect(positions.get('other')!.x).toBe(positions.get('trigger-1')!.x)
  noOverlaps(positions)
  // Tidying again keeps the bands in the same order.
  const tidied = applyLayout(graph, positions)
  expect(applyLayout(tidied, tidyLayout(tidied))).toBe(tidied)
})

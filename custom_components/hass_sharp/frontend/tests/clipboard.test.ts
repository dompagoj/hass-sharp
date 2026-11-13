import { afterEach, expect, it, vi } from 'vitest'
import { clearSteps, clipboardMarker, copySteps, parseSteps, pasteSteps, readSteps, writeSteps } from '../src/visual/clipboard'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { connectData, nodeCatalog, validateGraph, type VisualCatalogSnapshot } from '../src/visual/contract'

afterEach(() => {
  clearSteps()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})
const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
/** trigger → condition → service → value → log, with the value wired into the log by data. */
const sample = () => {
  let graph = addNode(addNode(createGraph('Copy'), 'set_value', 'value'), 'log', 'log')
  graph = updateNode(graph, 'value', { config: { type: 'number', value: lit('number', 4) }, position: { x: 500, y: 300 } })
  graph = updateNode(graph, 'log', { position: { x: 800, y: 340 } })
  graph = connectNodes(connectNodes(graph, 'service-1', 'value'), 'value', 'log')
  return connectData(graph, 'value', 'value', 'log', 'value')
}

it('copies the chosen steps with the wires between them only', () => {
  const graph = sample()
  const copy = copySteps(graph, ['value', 'log', 'missing'])!
  expect(copy.marker).toBe(clipboardMarker)
  expect(copy.nodes.map(n => n.id)).toEqual(['value', 'log'])
  // The execution wire into Set value comes from outside the copy and is left behind.
  expect(copy.edges.map(e => [e.source, e.target, e.kind ?? 'execution'])).toEqual([['value', 'log', 'execution'], ['value', 'log', 'data']])
  // A detached copy: editing the graph later does not change it.
  graph.nodes.find(n => n.id === 'value')!.config.type = 'string'
  expect(copy.nodes[0].config.type).toBe('number')
  expect(copySteps(graph, ['missing'])).toBeUndefined()
})

it('pastes with new ids, keeps internal wires remapped, drops outside wires and validates once like the editor', () => {
  const graph = sample()
  const copy = copySteps(graph, ['value', 'log'])!
  const result = pasteSteps(graph, copy, nodeCatalog, { offset: { x: 40, y: 40 } })
  if ('error' in result) throw new Error(result.error)
  const added = result.graph.nodes.slice(graph.nodes.length)
  expect(added.map(n => n.kind)).toEqual(['set_value', 'log'])
  expect(result.ids).toEqual(added.map(n => n.id))
  for (const node of added) expect(graph.nodes.some(n => n.id === node.id)).toBe(false)
  expect(added.map(n => n.position)).toEqual([{ x: 540, y: 340 }, { x: 840, y: 380 }])
  const [value, log] = result.ids
  const wires = result.graph.edges.slice(graph.edges.length)
  expect(wires).toEqual([
    { id: `${value}->${log}`, source: value, target: log },
    { id: JSON.stringify([value, 'value', log, 'value']), kind: 'data', source: value, target: log, sourcePort: 'value', targetPort: 'value' },
  ])
  // The originals are untouched and every edge id is unique.
  expect(result.graph.edges.slice(0, graph.edges.length)).toEqual(graph.edges)
  expect(new Set(result.graph.edges.map(e => e.id)).size).toBe(result.graph.edges.length)
  // The pasted copy is not yet reachable, which the validator says, but it refuses nothing the editor needs.
  expect(validateGraph(result.graph).some(d => d.message === 'Duplicate node id' || d.message === 'Duplicate edge id')).toBe(false)
})

it('places a paste at a point, by its top-left corner, on the grid when asked', () => {
  const graph = sample()
  const result = pasteSteps(graph, copySteps(graph, ['value', 'log'])!, nodeCatalog, { at: { x: 103, y: 47 } }, 20)
  if ('error' in result) throw new Error(result.error)
  expect(result.graph.nodes.slice(-2).map(n => n.position)).toEqual([{ x: 100, y: 40 }, { x: 400, y: 80 }])
})

it('pastes a second trigger the validator allows, and refuses what the editor would refuse', () => {
  const graph = createGraph('Triggers')
  const trigger = copySteps(graph, ['trigger-1'])!
  const twice = pasteSteps(graph, trigger, nodeCatalog, { offset: { x: 40, y: 40 } })
  expect('graph' in twice && twice.graph.nodes.filter(n => n.kind === 'trigger')).toHaveLength(2)
  // A step whose kind this catalog does not know.
  const unknown = { ...trigger, nodes: [{ ...trigger.nodes[0], kind: 'library:Gone' }] }
  expect(pasteSteps(graph, unknown, nodeCatalog, { offset: { x: 0, y: 0 } })).toEqual({ error: 'Unknown node kind' })
  // More steps than the graph limit allows.
  const small: VisualCatalogSnapshot = { ...nodeCatalog, limits: { ...nodeCatalog.limits, nodes: 3 } }
  const full = pasteSteps(graph, trigger, small, { offset: { x: 40, y: 40 } })
  expect('error' in full && full.error).toMatch(/^Graph limit/)
})

it('reads and writes the system clipboard when allowed, and never throws when it is missing or denied', async () => {
  const graph = sample()
  const copy = copySteps(graph, ['log'])!
  let stored = ''
  vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn(async (text: string) => { stored = text }), readText: vi.fn(async () => stored) } })
  await writeSteps(copy)
  expect(parseSteps(stored)).toEqual(copy)
  // Memory first.
  expect(await readSteps()).toEqual(copy)
  // Another tab's copy, through the system clipboard.
  clearSteps()
  expect(await readSteps()).toEqual(copy)
  // Unrelated clipboard text is not steps.
  stored = '{"nodes":[]}'
  expect(await readSteps()).toBeUndefined()
  stored = 'hello'
  expect(await readSteps()).toBeUndefined()
  // Denied.
  vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new Error('denied')), readText: () => Promise.reject(new Error('denied')) } })
  await expect(writeSteps(copy)).resolves.toBeUndefined()
  clearSteps()
  await expect(readSteps()).resolves.toBeUndefined()
  // Missing.
  vi.stubGlobal('navigator', {})
  await expect(writeSteps(copy)).resolves.toBeUndefined()
  expect(await readSteps()).toEqual(copy)
})

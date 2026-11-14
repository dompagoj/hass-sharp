import { afterEach, expect, it, vi } from 'vitest'
import { clipboardMarker, copySteps, parseSteps, pasteRefusals, pasteSteps, readSteps, writeSteps } from '../src/visual/clipboard'
import { addNode, connectNodes, createGraph, updateNode } from '../src/visual/graph'
import { analyzeGraph, connectData, diagnosticCodes, nodeCatalog, validateGraph, words, type DiagnosticCode, type VisualCatalogSnapshot } from '../src/visual/contract'

afterEach(() => {
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
})
const lit = (type: string, value: unknown) => ({ kind: 'literal', type, value })
/** trigger → condition → service → log, with a Constant (a value, outside the run order) wired into the log. */
const sample = () => {
  let graph = addNode(addNode(createGraph('Copy'), 'constant', 'value'), 'log', 'log')
  graph = updateNode(graph, 'value', { config: { type: 'number', value: lit('number', 4) }, position: { x: 500, y: 300 } })
  graph = updateNode(graph, 'log', { position: { x: 800, y: 340 } })
  graph = connectNodes(graph, 'service-1', 'log')
  return connectData(graph, 'value', 'value', 'log', 'value')
}

it('copies the chosen steps with the wires between them only', () => {
  const graph = sample()
  const copy = copySteps(graph, ['value', 'log', 'missing'])!
  expect(copy.marker).toBe(clipboardMarker)
  expect(copy.nodes.map(n => n.id)).toEqual(['value', 'log'])
  // The execution wire into the Log comes from outside the copy and is left behind.
  expect(copy.edges.map(e => [e.source, e.target, e.kind ?? 'execution'])).toEqual([['value', 'log', 'data']])
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
  expect(added.map(n => n.kind)).toEqual(['constant', 'log'])
  expect(result.ids).toEqual(added.map(n => n.id))
  for (const node of added) expect(graph.nodes.some(n => n.id === node.id)).toBe(false)
  expect(added.map(n => n.position)).toEqual([{ x: 540, y: 340 }, { x: 840, y: 380 }])
  const [value, log] = result.ids
  const wires = result.graph.edges.slice(graph.edges.length)
  expect(wires).toEqual([
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
  expect(pasteSteps(graph, unknown, nodeCatalog, { offset: { x: 0, y: 0 } })).toEqual({ error: words.missingLibraryNode })
  // A copy of a wire into an input that is already wired: refused by its code (port.inputTaken), not its wording.
  const fed = sample()
  const copy = copySteps(fed, ['value', 'log'])!
  const doubled = { ...copy, edges: [...copy.edges, { ...copy.edges.find(e => e.kind === 'data')!, id: 'again' }] }
  const refusedWire = pasteSteps(fed, doubled, nodeCatalog, { offset: { x: 0, y: 0 } })
  expect('error' in refusedWire && refusedWire.error).toBeTruthy()
  // More steps than the graph limit allows.
  const small: VisualCatalogSnapshot = { ...nodeCatalog, limits: { ...nodeCatalog.limits, nodes: 3 } }
  const full = pasteSteps(graph, trigger, small, { offset: { x: 40, y: 40 } })
  expect('error' in full && full.error).toMatch(/^Graph limit/)
})

it('pastes this tab\'s copy without reading the system clipboard while the page kept focus, else the newer of the two', async () => {
  const graph = sample()
  const now = vi.spyOn(Date, 'now')
  now.mockReturnValue(1000)
  const copy = copySteps(graph, ['log'])!
  let stored = ''
  const readText = vi.fn(async () => stored)
  vi.stubGlobal('navigator', { clipboard: { writeText: vi.fn(async (text: string) => { stored = text }), readText } })
  await writeSteps(copy)
  expect(parseSteps(stored)).toEqual(copy)
  // Same tab, focus never left: no permission prompt, this copy.
  stored = JSON.stringify(copySteps(graph, ['value']))
  expect(await readSteps()).toEqual(copy)
  expect(readText).not.toHaveBeenCalled()
  // The user went elsewhere and another tab copied later: its steps win.
  window.dispatchEvent(new Event('blur'))
  now.mockReturnValue(2000)
  const later = copySteps(graph, ['value'])!
  stored = JSON.stringify(later)
  expect(await readSteps()).toEqual(later)
  expect(readText).toHaveBeenCalledTimes(1)
  // An older copy on the system clipboard does not replace this tab's newer one.
  now.mockReturnValue(3000)
  const newest = copySteps(graph, ['value', 'log'])!
  await writeSteps(newest)
  window.dispatchEvent(new Event('blur'))
  stored = JSON.stringify(later)
  expect(await readSteps()).toEqual(newest)
  // Unrelated or oversized clipboard text is not steps; this tab's copy is pasted.
  stored = '{"nodes":[]}'
  expect(await readSteps()).toEqual(newest)
  expect(parseSteps('hello')).toBeUndefined()
  expect(parseSteps(JSON.stringify({ ...later, padding: 'x'.repeat(1_000_001) }))).toBeUndefined()
  // Denied.
  vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new Error('denied')), readText: () => Promise.reject(new Error('denied')) } })
  await expect(writeSteps(copy)).resolves.toBeUndefined()
  window.dispatchEvent(new Event('blur'))
  expect(await readSteps()).toEqual(copy)
  // Missing.
  vi.stubGlobal('navigator', {})
  await expect(writeSteps(newest)).resolves.toBeUndefined()
  window.dispatchEvent(new Event('blur'))
  expect(await readSteps()).toEqual(newest)
})

it('reads the system clipboard when this tab has no copy (steps copied in another tab)', async () => {
  vi.resetModules()
  const fresh = await import('../src/visual/clipboard')
  const steps = fresh.copySteps(sample(), ['log'])!
  const readText = vi.fn(async () => JSON.stringify(steps))
  vi.stubGlobal('navigator', { clipboard: { readText } })
  expect(await fresh.readSteps()).toEqual(steps)
  expect(readText).toHaveBeenCalledTimes(1)
})

it('refuses a paste only by structural codes the validator really produces', () => {
  // Every refused code is a declared code, and none is one pasted steps have until they are wired in.
  for (const code of pasteRefusals) expect(diagnosticCodes).toContain(code)
  for (const code of ['node.unreachable', 'scope.execution', 'scope.data', 'graph.name', 'graph.noTrigger'] as const) expect(pasteRefusals.has(code)).toBe(false)
  const base = createGraph('Codes')
  const [trigger, condition, service] = base.nodes
  const small = (limits: Partial<VisualCatalogSnapshot['limits']>): VisualCatalogSnapshot => ({ ...nodeCatalog, limits: { ...nodeCatalog.limits, ...limits } })
  let nested = addNode(addNode(base, 'repeat', 'outer'), 'repeat', 'inner')
  nested = updateNode(updateNode(nested, 'outer', { config: { count: lit('number', 2), carry: [], exports: [] } }), 'inner', { config: { count: lit('number', 2), carry: [], exports: [] } })
  nested = addNode(nested, 'delay', 'deep')
  nested = connectNodes(connectNodes(connectNodes(nested, 'service-1', 'outer'), 'outer', 'inner', 'body'), 'inner', 'deep', 'body')
  const valueWire = sample().edges.find(e => e.kind === 'data')!
  const crafted: Record<DiagnosticCode, [unknown, VisualCatalogSnapshot?]> = {
    'graph.version': [{ ...base, version: 4 }],
    'graph.shape': [{ ...base, edges: null }],
    'graph.limit': [base, small({ nodes: 2 })],
    'node.invalid': [{ ...base, nodes: [...base.nodes, { id: 'x' }] }],
    'node.duplicate': [{ ...base, nodes: [...base.nodes, service] }],
    'node.unknownKind': [{ ...base, nodes: [...base.nodes, { ...service, id: 'gone', kind: 'library:Gone' }] }],
    'limit.nesting': [nested, small({ nesting: 1 })],
    'edge.invalid': [{ ...base, edges: [...base.edges, { source: trigger.id, target: condition.id }] }],
    'edge.duplicate': [{ ...base, edges: [...base.edges, base.edges[0]] }],
    'edge.endpoint': [{ ...base, edges: [...base.edges, { id: 'dangling', source: trigger.id, target: 'missing' }] }],
    'edge.unknownKind': [{ ...base, edges: [...base.edges, { id: 'odd', kind: 'magic', source: trigger.id, target: service.id }] }],
    'edge.valueWirePins': [{ ...sample(), edges: [...sample().edges, { ...valueWire, id: 'pinless', port: 'next' }] }],
    'edge.orderWirePins': [{ ...base, edges: [...base.edges, { id: 'mixed', source: service.id, target: condition.id, sourcePort: 'x' }] }],
    'edge.orderPinName': [{ ...base, edges: [...base.edges, { id: 'nameless', source: service.id, target: condition.id, port: 7 }] }],
    'port.unknownExecution': [{ ...base, edges: [...base.edges, { id: 'nowhere', source: service.id, target: condition.id, port: 'sideways' }] }],
    'port.executionTaken': [{ ...base, edges: [...base.edges, { id: 'twice', source: trigger.id, target: service.id }] }],
    'port.unknownOutput': [{ ...sample(), edges: [...sample().edges.filter(e => e.kind !== 'data'), { ...valueWire, sourcePort: 'nothing' }] }],
    'port.unknownInput': [{ ...sample(), edges: [...sample().edges.filter(e => e.kind !== 'data'), { ...valueWire, targetPort: 'nothing' }] }],
    'port.inputTaken': [{ ...sample(), edges: [...sample().edges, { ...valueWire, id: 'again' }] }],
    'trigger.incoming': [{ ...base, edges: [...base.edges, { id: 'back', source: service.id, target: trigger.id }] }],
    'cycle.execution': [{ ...base, edges: [...base.edges, { id: 'loop', source: service.id, target: condition.id }] }],
    'cycle.data': [(() => {
      const g = addNode(addNode(base, 'and', 'a'), 'and', 'b')
      const wire = (source: string, target: string) => ({ id: `${source}-${target}`, kind: 'data', source, target, sourcePort: 'value', targetPort: 'left' })
      return { ...g, edges: [...g.edges, wire('a', 'b'), wire('b', 'a')] }
    })()],
  } as Record<DiagnosticCode, [unknown, VisualCatalogSnapshot?]>
  for (const code of pasteRefusals) {
    expect(crafted[code], code).toBeDefined()
    const [graph, catalog] = crafted[code]
    const codes = analyzeGraph(graph, catalog ?? nodeCatalog).diagnostics.map(d => d.code)
    expect(codes, code).toContain(code)
  }
})

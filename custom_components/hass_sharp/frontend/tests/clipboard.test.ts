import { afterEach, expect, it, vi } from 'vitest'
import { clipboardMarker, copySteps, customNamespace, parseSteps, pasteRefusals, pasteSteps, readSteps, writeSteps, type StepClipboard } from '../src/visual/clipboard'
import { add, empty } from './visual-fixture'
import { addNode, connectNodes, createGraph, createGraphHistory, updateNode, type VisualGraph } from '../src/visual/graph'
import richFixture from '../../../../tests/fixtures/visual-rich-catalog.json'
import { analyzeGraph, connectData, deviceNodeOf, diagnosticCodes, nodeCatalog, validateGraph, words, type DiagnosticCode, type VisualCatalogSnapshot } from '../src/visual/contract'

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

it('preserves a trigger\'s disabled history tracking through undo, redo and clipboard text', () => {
  const graph = createGraph('Tracking')
  const history = createGraphHistory(graph)
  history.commit(updateNode(graph, 'trigger-1', { config: { ...graph.nodes[0].config, trackHistory: false } }))
  history.undo()
  expect(history.graph().nodes[0].config.trackHistory).not.toBe(false)
  history.redo()
  const copy = parseSteps(JSON.stringify(copySteps(history.graph(), ['trigger-1'])))!
  const pasted = pasteSteps(graph, copy, nodeCatalog, { offset: { x: 40, y: 40 } })
  if ('error' in pasted) throw new Error(pasted.error)
  expect(pasted.graph.nodes.find(node => node.id === pasted.ids[0])!.config.trackHistory).toBe(false)
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

it('moves the types a pasted Custom C# node\'s code declares to the copy\'s own namespace, as its code compiles them', () => {
  const rich = richFixture.catalog as unknown as VisualCatalogSnapshot
  const graph = structuredClone(richFixture.cases.find(c => c.name === 'custom: a record the code declares into the library interface it implements')!.graph) as unknown as VisualGraph
  const copy = copySteps(graph, ['make', 'consume'])!
  const result = pasteSteps(graph, copy, rich, { offset: { x: 40, y: 40 } })
  if ('error' in result) throw new Error(result.error)
  const [made] = result.ids
  const pasted = result.graph.nodes.find(n => n.id === made)!
  const own = `global::NodeLib.${customNamespace(made)}.Own`
  const signature = pasted.config.signature as any
  expect(Object.keys(signature.types)).toEqual([own])
  expect(signature.outputs.result.type).toBe(own)
  expect(signature.types[own].label).toBe(`NodeLib.${customNamespace(made)}.Own`)
  // What it converts to: its own type moves, a library's type stays.
  expect(signature.types[own].assignableTo).toEqual(['global::NodeLib.IReading', 'global::System.Object', own, `global::System.IEquatable<${own}>`])
  // The code is as written; the original keeps its types.
  expect(pasted.config.source).toBe(graph.nodes.find(n => n.id === 'make')!.config.source)
  expect(Object.keys((graph.nodes.find(n => n.id === 'make')!.config.signature as any).types)).toEqual(['global::NodeLib.Custom_make.Own'])
  // The pasted wire between the two still fits (pasted steps are not connected into the flow yet, as any paste).
  const errors = (g: VisualGraph) => analyzeGraph(g, rich).diagnostics.filter(d => d.severity === 3 && d.code !== 'node.unreachable')
  expect(errors(result.graph)).toEqual(errors(graph))
})

it('writes a Custom C# node\'s namespace as the compiler does', () => {
  expect(customNamespace('make')).toBe('Custom_make')
  expect(customNamespace('a-1_b')).toBe('Custom_a_002D1_005Fb')
})

it('a pasted Entity node keeps its key where it is free, else takes the next free one, past the keys given earlier in the paste', () => {
  const lamp = (id: string, key: string) => ({ id, kind: 'entity', position: { x: 0, y: 0 }, config: { key, domain: 'switch', name: 'Lamp' } })
  const definition = (nodes: VisualGraph['nodes'] = []): VisualGraph => ({ version: 5, name: 'Lights', nodes, edges: [], definition: { settings: [] } as never })
  const copy = { marker: clipboardMarker, version: 1 as const, copiedAt: 0, edges: [], nodes: [lamp('a', 'lamp'), lamp('b', 'lamp'), { id: 's', kind: 'set_entity_available', position: { x: 0, y: 0 }, config: { entity: 'lamp' } }] }
  const into = (graph: VisualGraph) => {
    const result = pasteSteps(graph, structuredClone(copy), nodeCatalog, { offset: { x: 0, y: 0 } })
    if ('error' in result) throw new Error(result.error)
    return result.graph.nodes.map(n => n.kind === 'entity' ? n.config.key : n.config.entity)
  }
  // Into another definition without the key: the first keeps it, the second is given the next, and the step follows the first.
  expect(into(definition())).toEqual(['lamp', 'lamp_2', 'lamp'])
  // Into one whose Entity node has it: both move on, and the step with the first.
  expect(into(definition([lamp('x', 'lamp')]))).toEqual(['lamp', 'lamp_2', 'lamp_3', 'lamp_2'])
})

/** Hub (hub), Hall plug (plug, part of hub) with Relay on it, and Lamp, a switch on no device. */
const devices = () => {
  let graph = add(empty(), 'device', 'hub', { key: 'hub', name: 'Hub' }, { x: 0, y: 0 })
  graph = add(graph, 'device', 'plug', { key: 'plug', name: 'Hall plug', via: { device: 'hub' } }, { x: 0, y: 200 })
  graph = add(graph, 'entity', 'relay', { key: 'relay', domain: 'switch', name: 'Relay' }, { x: 400, y: 200 })
  graph = add(graph, 'entity', 'lamp', { key: 'lamp', domain: 'switch', name: 'Lamp' }, { x: 400, y: 400 })
  return connectData(graph, 'plug', 'entities', 'relay', 'device')
}
const pasteOf = (graph: VisualGraph, data: StepClipboard) => {
  const result = pasteSteps(graph, structuredClone(data), nodeCatalog, { offset: { x: 40, y: 40 } })
  if ('error' in result) throw new Error(result.error)
  return { graph: result.graph, copies: result.ids.map(id => result.graph.nodes.find(n => n.id === id)!) }
}
const deviceWires = (graph: VisualGraph) => graph.edges.flatMap(e => (e.kind === 'data' && e.targetPort === 'device' ? [`${e.source}->${e.target}`] : []))

it('a pasted device gets its own key, and a device pasted with it stays part of it', () => {
  const graph = devices()
  const before = structuredClone(graph)
  const data = copySteps(graph, ['hub', 'plug', 'relay'])!
  const { graph: pasted, copies } = pasteOf(graph, data)
  const [hub, plug, relay] = copies
  expect([hub.config.key, plug.config.key, relay.config.key]).toEqual(['hub_2', 'plug_2', 'relay_2'])
  // The copy of the plug is part of the copy of the hub; the relay's copy is on the plug's copy.
  expect(plug.config.via).toEqual({ device: 'hub_2' })
  expect(deviceNodeOf(pasted, relay.id)).toBe(plug.id)
  expect(deviceWires(pasted)).toEqual(['plug->relay', `${plug.id}->${relay.id}`])
  // The originals are unchanged.
  expect(pasted.nodes.slice(0, graph.nodes.length)).toEqual(before.nodes)
  expect(pasted.edges.slice(0, graph.edges.length)).toEqual(before.edges)
  expect(validateGraph(pasted).filter(d => d.severity === 3)).toEqual([])
  // Pasted again, each copy takes the next free key, and the plug's copy follows its hub's.
  const again = pasteOf(pasted, data).copies
  expect(again.map(n => n.config.key)).toEqual(['hub_3', 'plug_3', 'relay_3'])
  expect(again[1].config.via).toEqual({ device: 'hub_3' })
  // A device pasted without the one it is part of keeps that key; one part of a Home Assistant device keeps it too.
  expect(pasteOf(graph, copySteps(graph, ['plug'])!).copies[0].config).toMatchObject({ key: 'plug_2', via: { device: 'hub' } })
  const ha = updateNode(graph, 'plug', { config: { ...graph.nodes[1].config, via: { deviceId: 'zigbee' } } })
  expect(pasteOf(ha, copySteps(ha, ['hub', 'plug'])!).copies[1].config).toMatchObject({ key: 'plug_2', via: { deviceId: 'zigbee' } })
  // Two devices of one key in a copy are given two keys; the device part of that key follows the first.
  const twins = { marker: clipboardMarker, version: 1 as const, copiedAt: 0, edges: [], nodes: [
    { id: 'a', kind: 'device', position: { x: 0, y: 0 }, config: { key: 'hub', name: 'Hub' } },
    { id: 'b', kind: 'device', position: { x: 0, y: 0 }, config: { key: 'hub', name: 'Hub' } },
    { id: 'c', kind: 'device', position: { x: 0, y: 0 }, config: { key: 'plug', name: 'Plug', via: { device: 'hub' } } },
  ] }
  expect(pasteOf(graph, twins).copies.map(n => [n.config.key, n.config.via])).toEqual([['hub_2', undefined], ['hub_3', undefined], ['plug_2', { device: 'hub_2' }]])
  // Into another definition, against its keys: free there, a key is kept; device keys and entity keys are apart.
  const other = add(add(empty('Other'), 'entity', 'e', { key: 'hub', domain: 'switch', name: 'Hub' }), 'device', 'd', { key: 'plug' })
  const into = pasteOf(other, data).copies
  expect(into.map(n => n.config.key)).toEqual(['hub', 'plug_2', 'relay'])
  expect(into[1].config.via).toEqual({ device: 'hub' })
})

it('an entity pasted without its device stands alone', () => {
  const graph = devices()
  const { graph: pasted, copies } = pasteOf(graph, copySteps(graph, ['relay'])!)
  const [relay] = copies
  expect(relay.config.key).toBe('relay_2')
  // No data edge into its Device pin: the wire from the device not copied is left behind.
  expect(pasted.edges.filter(e => e.kind === 'data' && e.target === relay.id)).toEqual([])
  expect(deviceNodeOf(pasted, relay.id)).toBeUndefined()
  expect(deviceWires(pasted)).toEqual(['plug->relay'])
  expect(validateGraph(pasted).filter(d => d.nodeId === relay.id && d.severity === 3)).toEqual([])
  // A device pasted without its entities arrives with none.
  const alone = pasteOf(graph, copySteps(graph, ['plug'])!)
  expect(validateGraph(alone.graph).filter(d => d.nodeId === alone.copies[0].id).map(d => d.code)).toEqual(['device.empty'])
})

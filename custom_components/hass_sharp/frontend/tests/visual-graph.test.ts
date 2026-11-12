import { describe, expect, it } from 'vitest'
import { createRoot } from 'solid-js'
import { addNode, canConnect, connectNodes, createGraph, createGraphHistory, edgePort, outputPort, outputPorts, removeEdge, removeNode, updateNode, serializeGraph, visualRunClasses, visualActionLabels } from '../src/visual/graph'

describe('visual draft graph', () => {
  it('updates position/config without mutating an earlier history snapshot', () => {
    const before = createGraph('Heating')
    const after = updateNode(before, 'trigger-1', { position: { x: 210, y: 90 }, label: 'Door opens', config: { entity: 'binary_sensor.door' } })
    expect(before.nodes[0].position).toEqual({ x: 60, y: 100 })
    expect(before.nodes[0].label).toBe('Entity changes')
    expect(after.nodes[0]).toMatchObject({ position: { x: 210, y: 90 }, label: 'Door opens', config: { entity: 'binary_sensor.door' } })
  })
  it('keeps JSON versioned and excludes canvas/library state', () => {
    const graph = updateNode(createGraph('Heating'), 'trigger-1', { size: { width: 300, height: 180 } })
    const value = JSON.parse(serializeGraph(graph))
    expect(value).toMatchObject({ version: 2, name: 'Heating', nodes: [{ id: 'trigger-1', size: { width: 300, height: 180 } }, {}, {}] })
    expect(value.nodes[0]).not.toHaveProperty('selected')
    expect(value).not.toHaveProperty('generatedSource')
  })
  it('rejects self, duplicate, dangling and cyclic connections', () => {
    const graph = createGraph('Heating')
    expect(connectNodes(graph, 'service-1', 'condition-1')).toBe(graph)
    expect(connectNodes(graph, 'service-1', 'trigger-1')).toBe(graph)
    expect(connectNodes(graph, 'trigger-1', 'trigger-1')).toBe(graph)
    expect(connectNodes(graph, 'missing', 'service-1')).toBe(graph)
    expect(connectNodes(graph, 'trigger-1', 'condition-1')).toBe(graph)
  })
  it('removes attached edges with a deleted node', () => {
    const next = removeNode(createGraph('Heating'), 'condition-1')
    expect(next.nodes.map(n => n.id)).toEqual(['trigger-1', 'service-1'])
    expect(next.edges).toEqual([])
  })
  it('adds a configurable node and a valid connection', () => {
    const graph = addNode(createGraph('Heating'), 'delay', 'delay-new')
    const connected = connectNodes(graph, 'service-1', 'delay-new')
    expect(connected.nodes.at(-1)).toMatchObject({ id: 'delay-new', kind: 'delay', config: { seconds: '60' } })
    expect(connected.edges.at(-1)).toMatchObject({ source: 'service-1', target: 'delay-new' })
  })
})

it('undoes and redoes entire edits, clears redo on a new edit, and ignores no-ops', () => createRoot(dispose => {
  const history = createGraphHistory(createGraph('Heating'))
  history.commit(history.graph())
  expect(history.canUndo()).toBe(false)
  history.commit(updateNode(history.graph(), 'trigger-1', { position: { x: 90, y: 110 } }))
  history.undo()
  expect(history.graph().nodes[0].position).toEqual({ x: 60, y: 100 })
  expect(history.canRedo()).toBe(true)
  history.redo()
  expect(history.graph().nodes[0].position).toEqual({ x: 90, y: 110 })
  history.undo()
  history.commit(removeNode(history.graph(), 'service-1'))
  expect(history.canRedo()).toBe(false)
  dispose()
}))


it('derives stable v2 actions from UTF-8 trigger IDs and omits manual-disabled actions', () => {
  let graph = addNode(createGraph('Two triggers'), 'trigger', 'č🔥')
  graph = updateNode(graph, 'trigger-1', { label: 'Renamed', config: { type: 'startup', manual: false } })
  expect(visualRunClasses(graph)).toEqual([{ name: 'VisualAutomation', methods: ['Run_C48DF09F94A5'] }])
  graph = updateNode(graph, 'trigger-1', { config: { type: 'manual' } })
  expect(visualRunClasses({ ...graph, nodes: [...graph.nodes].reverse() })).toEqual([
    { name: 'VisualAutomation', methods: ['Run_747269676765722D31', 'Run_C48DF09F94A5'] },
  ])
  expect(visualRunClasses({ ...graph, version: 1 })).toEqual([{ name: 'VisualAutomation', methods: ['Run'] }])
})


it('distinguishes same-label triggers in the manual action chooser', () => {
  const graph = addNode(createGraph('Multiple'), 'trigger', 'evening')
  expect(visualActionLabels(graph)).toEqual({
    'VisualAutomation::Run_747269676765722D31': 'Entity changes · trigger-1',
    'VisualAutomation::Run_6576656E696E67': 'Entity changes · evening',
  })
  const renamed = updateNode(graph, 'evening', { label: 'Evening' })
  expect(visualActionLabels(renamed)['VisualAutomation::Run_6576656E696E67']).toBe('Evening')
})

describe('port-aware edges', () => {
  const branching = () => addNode(addNode(createGraph('Branches'), 'delay', 'otherwise'), 'service', 'recover')
  it('authors condition true, false and error branches with the backend port names', () => {
    let graph = removeEdge(branching(), 'condition-1:true->service-1')
    expect(branching().edges[1]).toEqual({ id: 'condition-1:true->service-1', source: 'condition-1', target: 'service-1', port: 'true' })
    graph = connectNodes(graph, 'condition-1', 'service-1', 'true')
    graph = connectNodes(graph, 'condition-1', 'otherwise', 'false')
    graph = connectNodes(graph, 'condition-1', 'recover', 'error')
    expect(graph.edges.filter(e => e.source === 'condition-1').map(e => [e.port, e.target])).toEqual([['true', 'service-1'], ['false', 'otherwise'], ['error', 'recover']])
    expect(new Set(graph.edges.map(e => e.id)).size).toBe(graph.edges.length)
  })
  it('allows one outgoing edge per port, but next and error from the same step', () => {
    let graph = connectNodes(branching(), 'service-1', 'otherwise')
    expect(canConnect(graph, 'service-1', 'recover')).toBe(false)
    expect(connectNodes(graph, 'service-1', 'recover', 'next')).toBe(graph)
    graph = connectNodes(graph, 'service-1', 'recover', 'error')
    expect(graph.edges.filter(e => e.source === 'service-1').map(e => edgePort(e))).toEqual(['next', 'error'])
    expect(canConnect(removeEdge(graph, graph.edges.at(-1)!.id), 'service-1', 'recover', 'error')).toBe(true)
  })
  it('treats a legacy port-less condition edge as its true branch and keeps it unchanged', () => {
    const legacy = { id: 'condition-1->service-1', source: 'condition-1', target: 'service-1' }
    const graph = { ...branching(), edges: branching().edges.map(e => e.source === 'condition-1' ? legacy : e) }
    const edited = connectNodes(graph, 'condition-1', 'otherwise', 'false')
    const saved = JSON.parse(serializeGraph(edited)).edges
    expect(Object.keys(saved[1])).toEqual(['id', 'source', 'target'])
    expect(saved[1]).toEqual(legacy)
    expect(saved.at(-1)).toEqual({ id: 'condition-1:false->otherwise', source: 'condition-1', target: 'otherwise', port: 'false' })
    expect(canConnect(graph, 'condition-1', 'otherwise', 'true')).toBe(false)
    expect(canConnect(graph, 'condition-1', 'otherwise', 'next')).toBe(false)
    expect(canConnect(graph, 'condition-1', 'otherwise', 'false')).toBe(true)
    expect(outputPort('condition', graph.edges.find(e => e.source === 'condition-1')!)).toBe('true')
  })
  it('lets different outputs lead to the same step', () => {
    const graph = connectNodes(connectNodes(branching(), 'condition-1', 'service-1', 'false'), 'condition-1', 'service-1', 'error')
    expect(graph.edges.filter(e => e.source === 'condition-1').map(e => [e.port, e.target])).toEqual([['true', 'service-1'], ['false', 'service-1'], ['error', 'service-1']])
  })
  it('suffixes a generated edge id that another edge already uses', () => {
    const graph = branching()
    const taken = { ...graph, edges: [...graph.edges, { id: 'service-1->otherwise', source: 'condition-1', target: 'recover', port: 'false' as const }] }
    expect(connectNodes(taken, 'service-1', 'otherwise').edges.at(-1)).toEqual({ id: 'service-1->otherwise#2', source: 'service-1', target: 'otherwise' })
  })
  it('rejects ports the backend does not accept for a node kind', () => {
    const graph = branching()
    expect(canConnect(graph, 'service-1', 'otherwise', 'false')).toBe(false)
    expect(canConnect(graph, 'trigger-1', 'otherwise', 'error')).toBe(false)
    expect(canConnect(graph, 'service-1', 'otherwise', 'bogus' as never)).toBe(false)
    expect(outputPorts('condition')).toEqual(['true', 'false', 'error'])
    expect(outputPorts('trigger')).toEqual(['next'])
  })
  it('still rejects cycles through any port', () => {
    const graph = connectNodes(branching(), 'service-1', 'otherwise')
    expect(canConnect(graph, 'otherwise', 'condition-1', 'error')).toBe(false)
  })
})

it('creates positioned nodes and exact output connections atomically, revalidating stale requests', async () => {
  const { createNodeAt, canCreateNode } = await import('../src/visual/graph')
  for (const [source, port] of [['service-1', 'next'], ['condition-1', 'false'], ['service-1', 'error'], ['condition-1', 'true']] as const) {
    const base = { ...createGraph('Flow'), edges: [] }
    const history = createGraphHistory(base)
    const pending = { source, port }
    expect(canCreateNode(base, 'trigger', pending)).toBe(false)
    history.commit(createNodeAt(base, 'delay', { x: 120, y: -30 }, pending))
    const created = history.graph()
    expect(created.nodes.at(-1)?.position).toEqual({ x: 120, y: -30 })
    expect(created.edges[0]).toMatchObject({ source, target: created.nodes.at(-1)!.id, ...(port === 'next' ? {} : { port }) })
    expect(createNodeAt(created, 'service', { x: 0, y: 0 }, pending)).toBe(created)
    history.undo(); expect(history.graph()).toEqual(base)
    history.redo(); expect(history.graph()).toEqual(created)
  }
  const legacy = { ...createGraph('Legacy'), version: 1 as const }
  expect(canCreateNode(legacy, 'trigger')).toBe(false)
  expect(createNodeAt(legacy, 'trigger', { x: 0, y: 0 })).toBe(legacy)
  expect(createNodeAt(legacy, 'delay', { x: 0, y: 0 }, { source: 'deleted', port: 'next' })).toBe(legacy)
})

it('upgrades typed nodes to v3 and preserves typed operands through history and serialization', () => {
  const legacy = createGraph('Typed')
  const graph = addNode(legacy, 'and', 'logic')
  expect(graph.version).toBe(3)
  expect(legacy.version).toBe(2)
  expect(graph.nodes.at(-1)?.config.left).toEqual({ kind: 'literal', type: 'boolean', value: true })
  expect(JSON.parse(serializeGraph(graph))).toEqual(graph)
  expect(outputPorts('repeat')).toEqual(['body', 'next', 'error'])
  expect(outputPorts('if')).toEqual(['true', 'false', 'error'])
  expect(outputPorts('stop')).toEqual([])
})
it('gives repeated value producers distinct default output names', () => {
 const graph=addNode(addNode(addNode(createGraph('Defaults'),'xor','first'),'xor','second'),'xor','third')
 expect(graph.nodes.slice(-3).map(node=>node.config.output)).toEqual(['xor_result','xor_result_2','xor_result_3'])
})
